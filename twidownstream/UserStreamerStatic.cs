﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using CoreTweet;
using CoreTweet.Streaming;
using twitenlib;
using twidown;
using System.Net.Http;

namespace twidownstream
{
    class UserStreamerStatic
    {
        //Singleton
        static readonly Config config = Config.Instance;
        static readonly DBHandler db = DBHandler.Instance;

        static UserStreamerStatic()
        {
            DeleteTweetBatch.LinkTo(DeleteTweetBlock, new DataflowLinkOptions { PropagateCompletion = true });
            //Udp.Client.SendTimeout = 1000;
            //Udp.Client.ReceiveTimeout = 1000;
        }

        public static void ShowCount()
        {
            int a, b;
            if ((a = TweetDistinctBlock.InputCount) > 0 | (b = HandleTweetBlock.InputCount) > 0)
            { Console.WriteLine("App: {0} -> {1} Tweets in buffer", a, b); }
            if ((a = DownloadStoreMediaBlock.InputCount) > 0) { Console.WriteLine("App: {0} Media in buffer", a); }
        }

        public static bool NeedConnectPostpone()
        {
            return TweetDistinctBlock.InputCount > config.crawl.ConnectPostponeSize
                || HandleTweetBlock.InputCount > config.crawl.ConnectPostponeSize
                || DownloadStoreMediaBlock.InputCount > config.crawl.ConnectPostponeSize;
        }

        //ツイートをDBに保存したりRTを先に保存したりする
        //アイコンを適宜保存する
        public static void HandleTweetRest(Status x, Tokens t)   //REST用
        {
            if ((x.ExtendedEntities ?? x.Entities)?.Media == null) { return; }   //画像なしツイートを捨てる
            TweetDistinctBlock.Post((x, t, false));
        }

        public static void HandleStatusMessage(Status x, Tokens t)
        {
            if ((x.ExtendedEntities ?? x.Entities)?.Media != null)  //画像なしツイートを捨てる
            { TweetDistinctBlock.Post((x, t, true)); }
        }

        public static void HandleDeleteMessage(DeleteMessage x)
        {
            //DeleteTweetBufferSizeが小さいとツイートよりツイ消しが先に処理されるかも
            DeleteTweetBatch.Post(x.Id);
        }

        
        static readonly RemoveOldSet<long> TweetLock = new RemoveOldSet<long>(config.crawl.TweetLockSize);
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler()
        {
            UseCookies = false,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        });
        
        /*
        static readonly UdpClient Udp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, (config.crawl.LockerUdpPort ^ (Process.GetCurrentProcess().Id & 0x3FFF))));
        static readonly IPEndPoint LockerEndPoint = new IPEndPoint(IPAddress.IPv6Loopback, config.crawl.LockerUdpPort);
        static readonly Stopwatch LastTweetSw = new Stopwatch();
        static async Task<bool> LockTweet(long tweet_id)
        {            
            //雑にプロセス内でもLockしておく
            if (!TweetLock.Add(tweet_id)) { return false; }
            
            //twidownparentでもLockを確認する リトライあり
            LastTweetSw.Restart();
            do
            {
                try
                {
                    await Udp.SendAsync(BitConverter.GetBytes(tweet_id), sizeof(long), LockerEndPoint).ConfigureAwait(false);
                    IPEndPoint GomiEndPoint = null;
                    return BitConverter.ToBoolean(Udp.Receive(ref GomiEndPoint), 0);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            } while (LastTweetSw.ElapsedMilliseconds < 10000);
            return false;   //返事が来なかったらこれ            
        }
        */
        static readonly RemoveOldSet<long> UserLock = new RemoveOldSet<long>(config.crawl.TweetLockSize);
        static readonly ActionBlock<(Status, Tokens, bool)> TweetDistinctBlock
            = new ActionBlock<(Status x, Tokens t, bool stream)>((m) =>
            {   //ここでLockする(1スレッドなのでHashSetでおｋ
                if (TweetLock.Add(m.x.Id)/*await LockTweet(m.x.Id).ConfigureAwait(false)*/)
                {
                    HandleTweetBlock.Post((m.x, m.t, m.stream,
                        m.x.RetweetedStatus?.Id is long rtid && TweetLock.Add(rtid),
                        UserLock.Add(m.x.Id),
                        m.x.RetweetedStatus?.User?.Id is long rtuser && UserLock.Add(rtuser)));
                }
            }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = 1 });
        static readonly ActionBlock<(Status, Tokens, bool, bool, bool, bool)> HandleTweetBlock = new ActionBlock<(Status x, Tokens t, bool stream, bool rtlocked, bool storeuser, bool storertuser)>(async m =>
        {
            //画像なしツイートは先に捨ててるのでここでは確認しない
             await HandleTweet(m.x, m.t, m.stream, m.rtlocked, m.storeuser, m.storertuser).ConfigureAwait(false); 
        }, new ExecutionDataflowBlockOptions()
        {
            MaxDegreeOfParallelism = Math.Max(Math.Max(Environment.ProcessorCount, config.crawl.MaxDBConnections), config.crawl.MediaDownloadThreads), //一応これで
            SingleProducerConstrained = true
        });

        static async Task HandleTweet(Status x, Tokens t, bool stream, bool rtlocked, bool storeuser, bool storertuser)    //stream(=true)からのツイートならふぁぼRT数を上書きする
        {
            //画像なしツイートは先に捨ててるのでここでは確認しない(n回目
            //RTを先にやる(キー制約)
            if (x.RetweetedStatus != null)
            {
                await HandleTweet(x.RetweetedStatus, t, stream && rtlocked, false, storertuser, false).ConfigureAwait(false); 
            }
            if (storeuser) { await db.StoreUser(x, await DownloadStoreProfileImage(x).ConfigureAwait(false), stream).ConfigureAwait(false); }
            if (stream) { Counter.TweetToStoreStream.Increment(); } else { Counter.TweetToStoreRest.Increment(); }
            for (int RetryCount = 0; RetryCount < 2; RetryCount++)
            {
                int r;
                if ((r = await db.StoreTweet(x, stream).ConfigureAwait(false)) >= 0)
                {
                    if (x.RetweetedStatus == null) { DownloadStoreMediaBlock.Post((x, t)); }
                    if (r > 0) { if (stream) { Counter.TweetStoredStream.Increment(); } else { Counter.TweetStoredRest.Increment(); } }
                    break;
                }
                //ツイートが入らなかったらuserの外部キー制約を仮定してとりあえず書き込む
                else if (!storeuser) { await db.StoreUser(x, false, false).ConfigureAwait(false); }
            }
        }

        static async ValueTask<bool> DownloadStoreProfileImage(Status x)
        {
            //アイコンが更新または未保存ならダウンロードする
            //RTは自動でやらない
            //ダウンロード成功したかどうかを返すのでStoreUserに投げる
            //(古い奴のURLがDBにあれば古いままになる)
            if (x.User.Id == null) { return false; }
            string ProfileImageUrl = x.User.ProfileImageUrlHttps ?? x.User.ProfileImageUrl;
            DBHandler.ProfileImageInfo d = await db.NeedtoDownloadProfileImage(x.User.Id.Value, ProfileImageUrl).ConfigureAwait(false);
            if (!d.NeedDownload) { return false; }

            //新しいアイコンの保存先 卵アイコンは'_'をつけただけの名前で保存するお
            string LocalPath = x.User.IsDefaultProfileImage ?
                config.crawl.PictPathProfileImage + '_' + Path.GetFileName(ProfileImageUrl) :
                config.crawl.PictPathProfileImage + x.User.Id.ToString() + Path.GetExtension(ProfileImageUrl);

            bool DownloadOK = false; 
            if (!x.User.IsDefaultProfileImage || !File.Exists(LocalPath))
            {
                for (int RetryCount = 0; RetryCount < 2; RetryCount++)
                {
                    try
                    {
                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, ProfileImageUrl))
                        {
                            req.Headers.Referrer = new Uri(StatusUrl(x));
                            using (HttpResponseMessage res = await Http.SendAsync(req).ConfigureAwait(false))
                            {
                                if (res.IsSuccessStatusCode)
                                {
                                    using (FileStream file = File.Create(LocalPath))
                                    {
                                        await res.Content.CopyToAsync(file).ConfigureAwait(false);
                                        await file.FlushAsync().ConfigureAwait(false);
                                    }
                                    DownloadOK = true; break;
                                }
                                else if (res.StatusCode == HttpStatusCode.Forbidden
                                    || res.StatusCode == HttpStatusCode.NotFound)
                                { DownloadOK = false; break; }
                            }
                        }
                    }
                    catch { continue; }
                }
            }
            else { DownloadOK = true; } //卵アイコンのダウンロード不要でもtrue
            if (DownloadOK)
            {
                string oldext = Path.GetExtension(d.OldProfileImageUrl);
                string newext = Path.GetExtension(ProfileImageUrl);
                if (!d.isDefaultProfileImage && oldext != null && oldext != newext)  //卵アイコンはこのパスじゃないしそもそも消さない
                { File.Delete(config.crawl.PictPathProfileImage + x.User.Id.ToString() + oldext); }
            }
            return DownloadOK;
        }

        ///<summary>RTはこれに入れないでね</summary>
        static readonly ActionBlock<(Status, Tokens)> DownloadStoreMediaBlock = new ActionBlock<(Status x, Tokens t)>(async a =>
        {
            try
            {
                Lazy<HashSet<long>> RestId = new Lazy<HashSet<long>>();   //同じツイートを何度も処理したくない
                foreach (MediaEntity m in a.x.ExtendedEntities.Media ?? a.x.Entities.Media)
                {
                    Counter.MediaTotal.Increment();

                    //URLぶち抜き転載の場合はここでツイートをダウンロード(すでにあればキャンセルされる
                    //x.Idのツイートのダウンロード失敗については何もしない(成功したツイートのみPostするべき
                    bool OtherSourceTweet = m.SourceStatusId.HasValue && m.SourceStatusId.Value != a.x.Id;    //URLぶち抜きならtrue
                    switch (await db.ExistMedia_source_tweet_id(m.Id).ConfigureAwait(false))
                    {
                        case true:
                            if (OtherSourceTweet) { await db.Storetweet_media(a.x.Id, m.Id).ConfigureAwait(false); }
                            continue;
                        case null:
                            if (OtherSourceTweet && RestId.Value.Add(a.x.Id)) { await DownloadOneTweet(m.SourceStatusId.Value, a.t).ConfigureAwait(false); }
                            await db.Storetweet_media(a.x.Id, m.Id).ConfigureAwait(false);
                            await db.UpdateMedia_source_tweet_id(m, a.x).ConfigureAwait(false);
                            continue;
                        case false:
                            if (OtherSourceTweet && RestId.Value.Add(a.x.Id)) { await DownloadOneTweet(m.SourceStatusId.Value, a.t).ConfigureAwait(false); }    //コピペつらい
                            break;   //画像の情報がないときだけダウンロードする
                    }
                    string MediaUrl = m.MediaUrlHttps ?? m.MediaUrl;
                    string LocalPaththumb = config.crawl.PictPaththumb + m.Id.ToString() + Path.GetExtension(MediaUrl);  //m.Urlとm.MediaUrlは違う
                    string uri = MediaUrl + (MediaUrl.IndexOf("twimg.com") >= 0 ? ":thumb" : "");
                    Counter.MediaToStore.Increment();
                    for (int RetryCount = 0; RetryCount < 3; RetryCount++)
                    {
                        try
                        {
                            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, uri))
                            {
                                req.Headers.Referrer = new Uri(StatusUrl(a.x));
                                using (HttpResponseMessage res = await Http.SendAsync(req).ConfigureAwait(false))
                                {
                                    if (res.IsSuccessStatusCode)
                                    {
                                        byte[] mem = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                        long? dcthash = await PictHash.DCTHash(mem, config.crawl.HashServerUrl, Path.GetFileName(MediaUrl)).ConfigureAwait(false);
                                        if (dcthash != null && (await db.StoreMedia(m, a.x, (long)dcthash).ConfigureAwait(false)) > 0)
                                        {
                                            using (FileStream file = File.Create(LocalPaththumb))
                                            {
                                                await file.WriteAsync(mem, 0, mem.Length).ConfigureAwait(false);
                                                await file.FlushAsync().ConfigureAwait(false);
                                            }
                                            Counter.MediaSuccess.Increment();
                                        }
                                        break;
                                    }
                                    else if (res.StatusCode == HttpStatusCode.NotFound
                                        || res.StatusCode == HttpStatusCode.Forbidden)
                                    { break; }
                                }
                            }
                        }
                        catch (Exception e) { /*Console.WriteLine("{0} Dowload/Store Media failed: {1}", DateTime.Now, e.Message);*/ continue; }
                    }
                    //URL転載元もペアを記録する
                    if (OtherSourceTweet) { await db.Storetweet_media(m.SourceStatusId.Value, m.Id).ConfigureAwait(false); }
                }
            }
            catch { }   //MediaEntityがnullの時があるっぽい
        }, new ExecutionDataflowBlockOptions()
        {
            MaxDegreeOfParallelism = config.crawl.MediaDownloadThreads,
        });

        //API制限対策用
        static readonly ConcurrentDictionary<Tokens, DateTimeOffset> OneTweetReset = new ConcurrentDictionary<Tokens, DateTimeOffset>();
        //source_tweet_idが一致しないやつは元ツイートを取得したい
        static async Task DownloadOneTweet(long StatusId, Tokens Token)
        {
            if (OneTweetReset.ContainsKey(Token) && OneTweetReset[Token] > DateTimeOffset.Now) { return; }
            OneTweetReset.TryRemove(Token, out DateTimeOffset gomi);
            try
            {
                if (await db.ExistTweet(StatusId).ConfigureAwait(false)) { return; }
                var res = await Token.Statuses.LookupAsync(id => StatusId, include_entities => true, tweet_mode => TweetMode.Extended).ConfigureAwait(false);
                if (res.RateLimit.Remaining < 1) { OneTweetReset[Token] = res.RateLimit.Reset.AddMinutes(1); }  //とりあえず1分延長奴
                await HandleTweet(res.First(), Token, false, false, false, false).ConfigureAwait(false);
            }
            catch { Console.WriteLine("{0} REST Tweet failed: {1}", Token.UserId, StatusId); return; }
        }

        static readonly BatchBlock<long> DeleteTweetBatch = new BatchBlock<long>(config.crawl.DeleteTweetBufferSize);
        //ツイ消しはここでDBに投げることにした
        static readonly ActionBlock<long[]> DeleteTweetBlock = new ActionBlock<long[]>
            (async (long[] ToDelete) => {
                foreach (long d in (await db.StoreDelete(ToDelete.Distinct().ToArray()).ConfigureAwait(false))) { await DeleteTweetBatch.SendAsync(d).ConfigureAwait(false); }
            }, new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = 1
            });

        //ツイートのURLを作る
        public static string StatusUrl(Status x)
        {
            return "https://twitter.com/" + x.User.ScreenName + "/status/" + x.Id;
        }
    }

    public static class Counter
    {
        //パフォーマンスカウンター的な何か
        public struct CounterValue
        {
            int Value;
            public void Increment() { Interlocked.Increment(ref Value); }
            public void Add(int v) { Interlocked.Add(ref Value, v); }
            public int Get() { return Value; }
            public int GetReset() { return Interlocked.Exchange(ref Value, 0); }
        }

        //structだからreadonlyにすると更新されなくなるよ
        public static CounterValue MediaSuccess = new CounterValue();
        public static CounterValue MediaToStore = new CounterValue();
        public static CounterValue MediaTotal = new CounterValue();
        public static CounterValue TweetStoredStream = new CounterValue();
        public static CounterValue TweetStoredRest = new CounterValue();
        public static CounterValue TweetToStoreStream = new CounterValue();
        public static CounterValue TweetToStoreRest = new CounterValue();
        public static CounterValue TweetToDelete = new CounterValue();
        public static CounterValue TweetDeleted = new CounterValue();
        //ひとまずアイコンは除外しようか
        public static void PrintReset()
        {
            if (TweetToStoreStream.Get() > 0 || TweetToStoreRest.Get() > 0)
            {
                Console.WriteLine("App: {0} + {1} / {2} + {3} Tweet Stored",
                TweetStoredStream.GetReset(), TweetStoredRest.GetReset(),
                TweetToStoreStream.GetReset(), TweetToStoreRest.GetReset());
            }
            if (TweetToDelete.Get() > 0) { Console.WriteLine("App: {0} / {1} Tweet Deleted", TweetDeleted.GetReset(), TweetToDelete.GetReset()); }
            if (MediaTotal.Get() > 0) { Console.WriteLine("App: {0} / {1} / {2} Media Stored", MediaSuccess.GetReset(), MediaToStore.GetReset(), MediaTotal.GetReset()); }
        }
    }

}


