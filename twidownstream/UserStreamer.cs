﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using CoreTweet;
using CoreTweet.Streaming;
using twitenlib;
using System.Threading;

namespace twidownstream
{
    class UserStreamer : IDisposable
    {
        // 各TokenのUserstreamを受信したり仕分けたりする
        
        public Tokens Token { get; }
        public bool NeedRestMyTweet { get; set; }   //次のconnect時にRESTでツイートを取得する
        public bool ConnectWaiting { get; set; }    //UserStreamerManager.ConnectBlockに入っているかどうか
        IDisposable StreamSubscriber;
        long LastReceivedTweetId;
        readonly TweetTimeList TweetTime = new TweetTimeList();
        DateTimeOffset LastStreamingMessageTime;

        //Singleton
        static readonly Config config = Config.Instance;
        static readonly DBHandler db = DBHandler.Instance;

        public UserStreamer(Tokens t)
        {
            Token = t;
            Token.ConnectionOptions.DisableKeepAlive = false;
            Token.ConnectionOptions.UseCompression = true;
            Token.ConnectionOptions.UseCompressionOnStreaming = true;
        }

        public void Dispose()
        {
            DisconnectStream();
            GC.SuppressFinalize(this);
        }

        ~UserStreamer()
        {
            Dispose();
        }

        //最近受信したツイートの時刻を一定数保持する
        //Userstreamの場合は実際に受信した時刻を使う
        class TweetTimeList
        {
            readonly SortedSet<DateTimeOffset> TweetTime = new SortedSet<DateTimeOffset>();
            static readonly Config config = Config.Instance;
            int AddCount;

            public void Add(DateTimeOffset Time)
            {
                lock (this)
                {
                    TweetTime.Add(Time);
                    AddCount++;
                    RemoveOldAndRefresh();
                }
            }
            public void AddRange(DateTimeOffset[] Time)
            {
                lock (this)
                {
                    if (Time.Length >= config.crawl.StreamSpeedTweets)
                    {
                        TweetTime.Clear();
                        AddCount = 0;
                    }
                    foreach (DateTimeOffset t in Time)
                    {
                        TweetTime.Add(t);
                        AddCount++;
                    }
                    RemoveOldAndRefresh();
                }
            }

            void RemoveOldAndRefresh()
            {
                //あまり使ってなかったら古い要素を消す
                if (AddCount < config.crawl.StreamSpeedTweets << 4)
                {
                    while (TweetTime.Count > config.crawl.StreamSpeedTweets)
                    {
                        TweetTime.Remove(TweetTime.Min);
                    }
                }
                //たくさん使ってたら作り直す
                else
                {
                    DateTimeOffset[] Temp = TweetTime.TakeLast(config.crawl.StreamSpeedTweets).ToArray();
                    TweetTime.Clear();
                    foreach (DateTimeOffset t in Temp) { TweetTime.Add(t); }
                    AddCount = 0;
                }
            }

            //config.crawl.UserStreamTimeoutTweets個前のツイートの時刻を返すってわけ
            public DateTimeOffset Min
            {
                get
                {
                    lock (this)
                    {
                        if (TweetTime.Count > 0) { return TweetTime.Min; }
                        else { return DateTimeOffset.Now; }
                    }
                }
            }
            public DateTimeOffset Max
            {
                get
                {
                    lock (this)
                    {
                        if (TweetTime.Count > 0) { return TweetTime.Max; }
                        else { return DateTimeOffset.Now; }
                    }
                }
            }
            public TimeSpan Span
            {
                get
                {
                    lock (this)
                    {
                        if (TweetTime.Count < 2) { return new TimeSpan(0); }
                        else { return TweetTime.Max - TweetTime.Min; }
                    }
                }
            }
            public int Count { get { lock(this) {return TweetTime.Count; } } }
        }

        DateTimeOffset? PostponedTime;    //ロックされたアカウントが再試行する時刻
        public void PostponeConnect() { PostponedTime = DateTimeOffset.Now.AddSeconds(config.crawl.LockedTokenPostpone); }
        bool IsPostponed()
        {
            if (PostponedTime == null) { return false; }
            else if (DateTimeOffset.Now > PostponedTime.Value) { return true; }
            else { PostponedTime = null; return false; }
        }

        public enum NeedConnectResult
        {
            StreamConnected,         //Stream接続済み→不要(Postponedもこれ)
            JustNeeded,       //必要だけど↓の各処理は不要
            First,       //初回(VerifyCredentialsしような
            RestOnly,    //TLが遅いからRESTだけにして
            Postponed   //ロックされてるから何もしない
        }

        //これを外部から叩いて再接続の必要性を確認
        public NeedConnectResult NeedConnect()
        {
            if (StreamSubscriber != null) { return NeedConnectResult.StreamConnected; }
            if (IsPostponed()) { return NeedConnectResult.Postponed; }
            else
            {
                //一度もTLを取得してないときはVerifyCredentials()してプロフィールを取得させる
                if (TweetTime.Count == 0) { return NeedConnectResult.First; }
                //TLが遅いアカウントはRESTだけでいいや
                else if (NeedStreamSpeed() != NeedStreamResult.Stream) { return NeedConnectResult.RestOnly; }
                else { return NeedConnectResult.JustNeeded; }
            }
            /*
            else if ((DateTimeOffset.Now - LastStreamingMessageTime).TotalSeconds
                > Math.Max(config.crawl.UserStreamTimeout, (LastStreamingMessageTime - TweetTime.Min).TotalSeconds))
            {
                //Console.WriteLine("{0} {1}: No streaming message for {2} sec.", DateTime.Now, Token.UserId, (DateTimeOffset.Now - LastStreamingMessageTime).TotalSeconds.ToString("#"));
                return NeedRetryResult.JustNeeded;
            }
            */
        }
        public enum NeedStreamResult
        {
            Stream,
            Hysteresis,
            RestOnly
        }
        public NeedStreamResult NeedStreamSpeed()
        {
            //User stream接続を失った可能性があるときもRestOnly→切断させる
            if (StreamSubscriber != null 
                && (DateTimeOffset.Now - LastStreamingMessageTime) > TweetTime.Span)
            { return NeedStreamResult.RestOnly; }
            //タイムラインを取得してない場合は必ずこれ
            if (TweetTime.Count < 2) { return NeedStreamResult.RestOnly; }
            int TotalSeconds = (int)((TweetTime.Max - TweetTime.Min).TotalSeconds);
            if (TotalSeconds < config.crawl.StreamSpeedSeconds) { return NeedStreamResult.Stream; }
            //ヒステリシスを用意する
            else if (TotalSeconds < config.crawl.StreamSpeedSeconds * config.crawl.StreamSpeedHysteresis) { return NeedStreamResult.Hysteresis; }
            else { return NeedStreamResult.RestOnly; }
        }


        //tokenの有効性を確認して自身のプロフィールも取得
        //Revokeの可能性があるときだけ呼ぶ
        public enum TokenStatus { Success, Failure, Revoked, Locked }
        public async ValueTask<TokenStatus> VerifyCredentials()
        {
            try
            {
                //Console.WriteLine("{0} {1}: Verifying token", DateTime.Now, Token.UserId);
                await db.StoreUserProfile(await Token.Account.VerifyCredentialsAsync().ConfigureAwait(false));
                //Console.WriteLine("{0} {1}: Token verification success", DateTime.Now, Token.UserId);
                return TokenStatus.Success;
            }
            catch (Exception e)
            {
                if (e is TwitterException t)
                {
                    if (t.Status == HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("{0}: VerifyCredentials Unauthorized", Token.UserId);
                        return TokenStatus.Revoked;
                    }
                    else { return TokenStatus.Failure; }
                }
                else if (e is ParsingException) { return TokenStatus.Failure; }
                else if (e is WebException ex
                    && ex.Status == WebExceptionStatus.ProtocolError
                    && ex.Response is HttpWebResponse wr)
                {
                    if (wr.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("{0}: VerifyCredentials Unauthorized", Token.UserId);
                        return TokenStatus.Revoked;
                    }
                    else
                    {
                        Console.WriteLine("{0}: VerifyCredentials {1} {2}", Token.UserId, ex.Status, ex.Message);
                        return TokenStatus.Failure;
                    }
                }
                else
                {
                    Console.WriteLine("{0}: VerifyCredentials {1}",Token.UserId, e.Message);
                    return TokenStatus.Failure;
                }
            }
        }
        
        public void RecieveStream()
        {
            DisconnectStream();
            LastStreamingMessageTime = DateTimeOffset.Now;
            StreamSubscriber = Token.Streaming.UserAsObservable()
                //.SubscribeOn(TaskPoolScheduler.Default)
                //.ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    async (StreamingMessage m) =>
                    {
                        LastStreamingMessageTime = DateTimeOffset.Now;
                        if (m.Type == MessageType.Create)
                        {
                            StatusMessage mm = m as StatusMessage;
                            LastReceivedTweetId = mm.Status.Id;
                            TweetTime.Add(mm.Timestamp);                        
                        }
                        await HandleStreamingMessage(m).ConfigureAwait(false);
                    },
                    (Exception e) =>
                    {
                        DisconnectStream();
                        //Console.WriteLine("{0} {1}: RecieveStream {2}", DateTime.Now, Token.UserId, e.Message);
                    },
                    () => { DisconnectStream(); } //接続中のRevokeはこれ
                );
        }

        public void DisconnectStream() { StreamSubscriber?.Dispose(); StreamSubscriber = null; }

        public async ValueTask<TokenStatus> RecieveRestTimelineAuto()
        {
            //TLが遅い分は省略
            if(TweetTime.Count >= 2 && TweetTime.Max - TweetTime.Min > DateTimeOffset.Now - TweetTime.Max) { return TokenStatus.Success; }
            //RESTで取得してツイートをDBに突っ込む
            //各ツイートの時刻をTweetTimeに格納
            try
            {
                CoreTweet.Core.ListedResponse<Status> Timeline;
                if (LastReceivedTweetId != 0) {
                     Timeline = await Token.Statuses.HomeTimelineAsync
                        (count => 200, tweet_mode => TweetMode.Extended, since_id => LastReceivedTweetId).ConfigureAwait(false);
                }
                else
                {
                    Timeline = await Token.Statuses.HomeTimelineAsync
                        (count => 200, tweet_mode => TweetMode.Extended).ConfigureAwait(false);
                }

                //Console.WriteLine("{0} {1}: Handling {2} RESTed timeline", DateTime.Now, Token.UserId, Timeline.Count);
                foreach(Status s in Timeline)
                {
                    UserStreamerStatic.HandleTweetRest(s, Token);
                    if(s.Id > LastReceivedTweetId) { LastReceivedTweetId = s.Id; }
                    TweetTime.Add(s.CreatedAt);
                }
                if (Timeline.Count == 0) { TweetTime.Add(DateTimeOffset.Now); }
                //Console.WriteLine("{0} {1}: REST timeline success", DateTime.Now, Token.UserId);
                return TokenStatus.Success;
            }
            catch (Exception e)
            {
                if (e is TwitterException t)
                {
                    if (t.Status == HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("{0}: Unauthorized", Token.UserId);
                        return TokenStatus.Revoked;
                    }
                    else { return TokenStatus.Failure; }
                }
                else if (e is ParsingException) { return TokenStatus.Failure; }
                else if (e is WebException ex
                    && ex.Status == WebExceptionStatus.ProtocolError
                    && ex.Response is HttpWebResponse wr)
                {
                    if (wr.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine("{0}: Unauthorized", Token.UserId);
                        return TokenStatus.Revoked;
                    }
                    else if (wr.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine("{0}: Locked", Token.UserId);
                        return TokenStatus.Locked;
                    }
                    else
                    {
                        Console.WriteLine("{0}: {1} {2}", Token.UserId, ex.Status, ex.Message);
                        return TokenStatus.Failure;
                    }
                }
                else
                {
                    Console.WriteLine("{0}: RecieveRestTimelineAuto {1}", Token.UserId, e.Message);
                    return TokenStatus.Failure;
                }
            }
        }

        public async Task RestMyTweet()
        {
            //RESTで取得してツイートをDBに突っ込む
            try
            {
                CoreTweet.Core.ListedResponse<Status> Tweets = await Token.Statuses.UserTimelineAsync(user_id => Token.UserId, count => 200, tweet_mode => TweetMode.Extended).ConfigureAwait(false);

                //Console.WriteLine("{0} {1}: Handling {2} RESTed tweets", DateTime.Now, Token.UserId, Tweets.Count);
                foreach (Status s in Tweets)
                {   //ここでRESTをDBに突っ込む
                    UserStreamerStatic.HandleTweetRest(s, Token);
                }
                //Console.WriteLine("{0} {1}: REST tweets success", DateTime.Now, Token.UserId);
            }
            catch (Exception e)
            {
                //Console.WriteLine("{0} {1}: REST tweets failed: {2}", DateTime.Now, Token.UserId, e.Message);
            }
        }

        public async ValueTask<int> RestBlock()
        {
            try
            {
                long[] blocks = (await Token.Blocks.IdsAsync(user_id => Token.UserId).ConfigureAwait(false)).ToArray();
                if (blocks != null) { await db.StoreBlocks(blocks, Token.UserId).ConfigureAwait(false); }
                return blocks.Length;
            }
            catch { return -1; }
        }

        public async ValueTask<int> RestFriend()
        {
            try
            {
                long[] friends = (await Token.Friends.IdsAsync(user_id => Token.UserId).ConfigureAwait(false)).ToArray();
                if (friends != null) { await db.StoreFriends(friends, Token.UserId).ConfigureAwait(false); }
                return friends.Length;
            }
            catch { return -1; }
        }

        async Task HandleStreamingMessage(StreamingMessage x)
        {
            switch (x.Type)
            {
                case MessageType.Create:
                    UserStreamerStatic.HandleStatusMessage((x as StatusMessage).Status, Token);
                    break;
                case MessageType.DeleteStatus:
                    UserStreamerStatic.HandleDeleteMessage(x as DeleteMessage);
                    break;
                case MessageType.Friends:
                    //UserStream接続時に届く(10000フォロー超だと届かない)
                    await db.StoreFriends(x as FriendsMessage, Token.UserId).ConfigureAwait(false);
                    //Console.WriteLine("{0} {1}: Stream connected", DateTime.Now, Token.UserId);
                    break;
                case MessageType.Disconnect:
                    //届かないことの方が多い
                    Console.WriteLine("{0}: DisconnectMessage({1})", Token.UserId, (x as DisconnectMessage).Code);
                    break;
                case MessageType.Event:
                    await HandleEventMessage(x as EventMessage).ConfigureAwait(false);
                    break;
                case MessageType.Warning:
                    if ((x as WarningMessage).Code == "FOLLOWS_OVER_LIMIT")
                    {
                        if (await RestFriend().ConfigureAwait(false) > 0) { Console.WriteLine("{0}: REST friends success", Token.UserId); }
                        //Console.WriteLine("{0} {1}: Stream connected", DateTime.Now, Token.UserId);
                    }
                    break;
            }
        }

        async Task HandleEventMessage(EventMessage x)
        {
            switch (x.Event)
            {
                case EventCode.Follow:
                case EventCode.Unfollow:
                case EventCode.Unblock:
                    if (x.Source.Id == Token.UserId) { await db.StoreEvents(x).ConfigureAwait(false); }
                    break;
                case EventCode.Block:
                    if (x.Source.Id == Token.UserId || x.Target.Id == Token.UserId) { await db.StoreEvents(x).ConfigureAwait(false); }
                    break;
                case EventCode.UserUpdate:
                    if (x.Source.Id == Token.UserId) { try { await db.StoreUserProfile(await Token.Account.VerifyCredentialsAsync().ConfigureAwait(false)).ConfigureAwait(false); } catch { } }
                    break;
            }
        }
    }
}
