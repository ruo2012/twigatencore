﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Data;
using MySql.Data.MySqlClient;
using System.Threading.Tasks;
using twitenlib;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace twihash
{
    class DBHandler : twitenlib.DBHandler
    {
        public DBHandler() : base("hash", "", Config.Instance.database.Address, 300) { }
        
        const string StoreMediaPairsHead = @"INSERT IGNORE INTO dcthashpair VALUES";
        public const int StoreMediaPairsUnit = 1000;

        ThreadLocal<MySqlCommand> StoreMediaPairsCmdFull = new ThreadLocal<MySqlCommand>(() => {
            MySqlCommand Cmd = new MySqlCommand(BulkCmdStr(StoreMediaPairsUnit, 3, StoreMediaPairsHead));
            for (int i = 0; i < StoreMediaPairsUnit; i++)
            {
                string numstr = i.ToString();
                Cmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64);
                Cmd.Parameters.Add("@b" + numstr, MySqlDbType.Int64);
                Cmd.Parameters.Add("@c" + numstr, MySqlDbType.Byte);
            }
            return Cmd;
        });

        public async ValueTask<int> StoreMediaPairs(MediaPair[] StorePairs)
        //類似画像のペアをDBに保存
        {
            if (StorePairs.Length > StoreMediaPairsUnit) { throw new ArgumentOutOfRangeException(); }
            else if (StorePairs.Length == 0) { return 0; }
            else if (StorePairs.Length == StoreMediaPairsUnit)
            {
                return await StoreMediaPairsInner(StoreMediaPairsCmdFull.Value, StorePairs);
            }
            else
            {
                using (MySqlCommand Cmd = new MySqlCommand(BulkCmdStr(StorePairs.Length, 3, StoreMediaPairsHead)))
                { 
                    for (int i = 0; i < StorePairs.Length; i++)
                    {
                        string numstr = i.ToString();
                        Cmd.Parameters.Add("@a" + numstr, MySqlDbType.Int64);
                        Cmd.Parameters.Add("@b" + numstr, MySqlDbType.Int64);
                        Cmd.Parameters.Add("@c" + numstr, MySqlDbType.Byte);
                    }
                    return await StoreMediaPairsInner(Cmd, StorePairs);
                }
            }
        }

        MediaPair.OrderPri OrderPri = new MediaPair.OrderPri();
        MediaPair.OrderSub OrderSub = new MediaPair.OrderSub();
        async ValueTask<int> StoreMediaPairsInner(MySqlCommand Cmd, MediaPair[] StorePairs)
        {
            Array.Sort(StorePairs, OrderPri);   //deadlock防止
            for (int i = 0; i < StorePairs.Length; i++)
            {
                string numstr = i.ToString();
                Cmd.Parameters["@a" + numstr].Value = StorePairs[i].media0;
                Cmd.Parameters["@b" + numstr].Value = StorePairs[i].media1;
                Cmd.Parameters["@c" + numstr].Value = StorePairs[i].hammingdistance;
            }
            int ret = await ExecuteNonQuery(Cmd);

            Array.Sort(StorePairs, OrderSub);   //deadlock防止
            for (int i = 0; i < StorePairs.Length; i++)
            {
                string numstr = i.ToString(); 
                Cmd.Parameters["@a" + numstr].Value = StorePairs[i].media1;   //↑とは逆
                Cmd.Parameters["@b" + numstr].Value = StorePairs[i].media0;
                Cmd.Parameters["@c" + numstr].Value = StorePairs[i].hammingdistance;
            }
            return ret + await ExecuteNonQuery(Cmd);
        }

        ThreadLocal<MySqlCommand> GetMediaHashCmd = new ThreadLocal<MySqlCommand>(() => {
            MySqlCommand Cmd = new MySqlCommand(@"SELECT dcthash
FROM media
WHERE dcthash BETWEEN @begin AND @end
GROUP BY dcthash;");
            Cmd.Parameters.Add("@begin", MySqlDbType.Int64);
            Cmd.Parameters.Add("@end", MySqlDbType.Int64);
            return Cmd;
        });

        ///<summary>DBから読み込んだハッシュをそのままファイルに書き出す</summary>
        public async ValueTask<long> AllMediaHash()
        {
            try
            {
                using (AllHashFileWriter writer = new AllHashFileWriter())
                {
                    ActionBlock<DataTable> WriterBlock = new ActionBlock<DataTable>(
                        async (table) => { await writer.Write(table.AsEnumerable().Select((row) => row.Field<long>(0))); },
                        new ExecutionDataflowBlockOptions()
                        {
                            MaxDegreeOfParallelism = 1,
                            SingleProducerConstrained = true
                        });

                    long TotalHashCOunt = 0;
                    int HashUnitBits = Math.Min(63, 64 + 11 - (int)Math.Log(config.hash.LastHashCount, 2)); //TableがLarge Heapに載らない程度に調整
                    TransformBlock<long, DataTable> LoadHashBlock = new TransformBlock<long, DataTable>(async (i) =>
                    {
                        DataTable Table;
                        do
                        {
                            GetMediaHashCmd.Value.Parameters["@begin"].Value = i << HashUnitBits;
                            GetMediaHashCmd.Value.Parameters["@end"].Value = unchecked(((i + 1) << HashUnitBits) - 1);
                            Table = await SelectTable(GetMediaHashCmd.Value, IsolationLevel.ReadUncommitted);
                        } while (Table == null);    //大変安易な対応
                        Interlocked.Add(ref TotalHashCOunt, Table.Rows.Count);
                        return Table;
                    }, new ExecutionDataflowBlockOptions()
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        BoundedCapacity = Environment.ProcessorCount << 1,
                        SingleProducerConstrained = true
                    });

                    LoadHashBlock.LinkTo(WriterBlock, new DataflowLinkOptions() { PropagateCompletion = true });

                    for(int i = 0; i < 1 << (64 - HashUnitBits); i++)
                    {
                        await LoadHashBlock.SendAsync(i);
                    }
                    LoadHashBlock.Complete();
                    await WriterBlock.Completion;
                    return TotalHashCOunt;
                }
            }
            catch (Exception e) { Console.WriteLine(e); return -1; }
        }

        //dcthashpairに追加する必要があるハッシュを取得するやつ
        //これが始まった後に追加されたハッシュは無視されるが
        //次回の実行で拾われるから問題ない

        ThreadLocal<MySqlCommand> NewerMediaHashCmd = new ThreadLocal<MySqlCommand>(() => {
            MySqlCommand Cmd = new MySqlCommand(@"SELECT dcthash
FROM media_downloaded_at
NATURAL JOIN media
WHERE downloaded_at BETWEEN @begin AND @end;");
            Cmd.Parameters.Add("@begin", MySqlDbType.Int64);
            Cmd.Parameters.Add("@end", MySqlDbType.Int64);
            return Cmd;
        });

        public async ValueTask<HashSet<long>> NewerMediaHash()
        {
            try
            {
                HashSet<long> ret = new HashSet<long>();
                const int QueryRangeSeconds = 600;
                ActionBlock<long> LoadHashBlock = new ActionBlock<long>(async (i) => 
                {
                    DataTable Table;
                    do
                    {
                        NewerMediaHashCmd.Value.Parameters["@begin"].Value = config.hash.LastUpdate + QueryRangeSeconds * i;
                        NewerMediaHashCmd.Value.Parameters["@end"].Value = config.hash.LastUpdate + QueryRangeSeconds * (i + 1) - 1;
                        Table = await SelectTable(NewerMediaHashCmd.Value, IsolationLevel.ReadUncommitted);
                    } while (Table == null);    //大変安易な対応
                    lock (ret)
                    {
                        foreach (long h in Table.AsEnumerable().Select((row) => row.Field<long>(0)))
                        {
                            ret.Add(h);
                        }
                    }
                }, new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount });
                for(long i = 0; i < Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - config.hash.LastUpdate) / QueryRangeSeconds + 1; i++)
                {
                    LoadHashBlock.Post(i);
                }
                LoadHashBlock.Complete();
                await LoadHashBlock.Completion;
                return ret;
            }catch(Exception e) { Console.WriteLine(e); return null; }
        }
    }
}
