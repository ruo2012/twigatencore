﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime;
using System.Net;
using System.Diagnostics;

namespace twidownstream
{
    class Program
    {
        static async Task Main(string[] args)
        {
            ServicePointManager.ReusePort = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.EnableDnsRoundRobin = true;

            twitenlib.Config config = twitenlib.Config.Instance;
            //結局Minを超えると死ぬのでMinを大きくしておくしかない
            {
                ThreadPool.GetMinThreads(out int MinThreads, out int _);
                ThreadPool.GetMaxThreads(out int MaxThreads, out int CompletionThreads);
                ThreadPool.SetMinThreads(MinThreads, CompletionThreads);
                ThreadPool.SetMaxThreads(MaxThreads, CompletionThreads);
                //Console.WriteLine("{0} App: ThreadPool: {1}, {2}", DateTime.Now, MaxThreads, CompletionThreads);
            }
            await Task.Delay(10000).ConfigureAwait(false);

            if (args.Length >= 1 && args[0] == "/REST")
            {
                Console.WriteLine("App: Running in REST mode.");
                int RestCount = await new RestManager().Proceed().ConfigureAwait(false);
                Console.WriteLine("App: {0} Accounts REST Tweets Completed.", RestCount);
                return;
            }

            UserStreamerManager manager = await UserStreamerManager.Create().ConfigureAwait(false);
            Stopwatch sw = new Stopwatch();
            while (true)
            {
                sw.Restart();
                int Connected = await manager.ConnectStreamers().ConfigureAwait(false);
                Console.WriteLine("App: {0} / {1} Accounts Streaming.", Connected, manager.Count);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce; //これは毎回必要らしい
                GC.Collect();
                sw.Stop();
                await Task.Delay(60000 - (int)sw.ElapsedMilliseconds % 60000).ConfigureAwait(false);
                //↓再読み込みしても一部しか反映されないけどね
                config.Reload();
                await manager.AddAll().ConfigureAwait(false);
            }
        }
    }
}