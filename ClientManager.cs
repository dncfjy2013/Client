using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Client;
using Client.Common;

public class StressTester
{
    private const string ServerIp = "127.0.0.1";
    private const int Port = 12345;
    private const int ConcurrentClients = 100;
    private const int MessagesPerClient = 1000;
    private const int FileTransferCount = 50;
    private const string TestFilePath = "test_file.bin";

    public static async Task Main()
    {
        // 生成测试文件
        File.WriteAllBytes(TestFilePath, new byte[1024 * 1024]); // 1MB文件

        //await RunConnectionStressTest();
        await RunMessageStormTest();
        //await RunFileTransferTest();
    }

    private static async Task RunConnectionStressTest()
    {
        Console.WriteLine("\n=== 连接压力测试 ===");
        var clients = new ClientInstance[ConcurrentClients];
        var sw = Stopwatch.StartNew();

        // 创建并发客户端连接
        var connectTasks = Enumerable.Range(0, ConcurrentClients)
            .Select(i => Task.Run(async () =>
            {
                var client = new ClientInstance(ServerIp, Port);
                await client.Connect();
                clients[i] = client;
            })).ToArray();

        await Task.WhenAll(connectTasks);
        sw.Stop();

        Console.WriteLine($"成功建立 {ConcurrentClients} 个连接，耗时 {sw.ElapsedMilliseconds}ms");

        //// 断开所有连接
        //foreach (var client in clients)
        //{
        //    client.Disconnect();
        //}
    }

    private static async Task RunMessageStormTest()
    {
        Console.WriteLine("\n=== 消息风暴测试 ===");
        var clients = new ClientInstance[ConcurrentClients];
        var totalMessages = ConcurrentClients * MessagesPerClient;
        var successCount = 0;
        var sw = Stopwatch.StartNew();

        // 初始化客户端
        for (int i = 0; i < ConcurrentClients; i++)
        {
            clients[i] = new ClientInstance(ServerIp, Port);
            await clients[i].Connect();
        }

        // 并发发送消息
        var sendTasks = clients.Select(async client =>
        {
            for (int i = 0; i < MessagesPerClient; i++)
            {
                try
                {
                    var data = new CommunicationData
                    {
                        Message = $"Test message {i}",
                        InfoType = InfoType.Normal
                    };
                    await client.SendData(data);
                    Interlocked.Increment(ref successCount);
                }
                catch { /* 忽略发送失败 */ }
            }
        }).ToArray();

        await Task.WhenAll(sendTasks);
        sw.Stop();

        Console.WriteLine($"发送 {totalMessages} 条消息，成功 {successCount} 条");
        Console.WriteLine($"吞吐量：{totalMessages / sw.Elapsed.TotalSeconds:N0} msg/s");

        // 断开连接
        foreach (var client in clients)
        {
            client.Disconnect();
        }
    }

    private static async Task RunFileTransferTest()
    {
        Console.WriteLine("\n=== 文件传输测试 ===");
        var clients = new ClientInstance[ConcurrentClients];
        var transferTasks = new Task[ConcurrentClients];
        var successCount = 0;
        var sw = Stopwatch.StartNew();

        // 初始化客户端
        for (int i = 0; i < ConcurrentClients; i++)
        {
            clients[i] = new ClientInstance(ServerIp, Port);
            await clients[i].Connect();
        }

        // 并发文件传输
        var bag = new ConcurrentBag<string>();
        for (int i = 0; i < FileTransferCount; i++)
        {
            var fileId = $"file_{i}";
            bag.Add(fileId);
        }

        transferTasks = clients.Select(async client =>
        {
            foreach (var fileId in bag.OrderBy(_ => Guid.NewGuid())) // 随机分配文件ID
            {
                try
                {
                    await client.SendFile(TestFilePath, fileId,
                        progress => Console.WriteLine($"进度：{progress}%"));
                    Interlocked.Increment(ref successCount);
                }
                catch { /* 忽略传输失败 */ }
            }
        }).ToArray();

        await Task.WhenAll(transferTasks);
        sw.Stop();

        Console.WriteLine($"完成 {FileTransferCount} 个文件传输，成功 {successCount} 个");
        Console.WriteLine($"平均耗时：{sw.ElapsedMilliseconds / FileTransferCount}ms");

        // 清理测试文件
        File.Delete(TestFilePath);

        // 断开连接
        foreach (var client in clients)
        {
            client.Disconnect();
        }
    }
}