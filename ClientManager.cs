using Client.Core;
using Protocol;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public class ThroughputTest
{
    private readonly string _serverIp;
    private readonly int _serverPort;
    private readonly int _clientCount; // 并发客户端数量
    private readonly int _messageCountPerClient; // 每个客户端发送的消息数
    private readonly DataPriority _testPriority; // 测试数据优先级

    public ThroughputTest(string serverIp, int serverPort, int clientCount = 10,
        int messageCountPerClient = 1000, DataPriority testPriority = DataPriority.Medium)
    {
        _serverIp = serverIp;
        _serverPort = serverPort;
        _clientCount = clientCount;
        _messageCountPerClient = messageCountPerClient;
        _testPriority = testPriority;
    }

    public async Task RunTestAsync()
    {
        // 初始化并发统计变量
        var stopwatch = Stopwatch.StartNew();
        var sentCount = new ConcurrentDictionary<DataPriority, long>();
        var receivedAckCount = new ConcurrentDictionary<DataPriority, long>();
        var latencyList = new ConcurrentBag<long>(); // 记录每条消息的往返延迟

        // 注册ACK接收事件以记录延迟
        Action<int> ackHandler = seqNum =>
        {
            if (_sentTimestamps.TryRemove(seqNum, out var sendTime))
            {
                var latency = Stopwatch.GetTimestamp() - sendTime;
                latencyList.Add(latency);
                receivedAckCount.AddOrUpdate(_testPriority, 1, (k, v) => v + 1);
            }
        };

        // 客户端工厂方法
        Func<SocketClientInstance> createClient = () =>
        {
            var client = new SocketClientInstance(_serverIp, _serverPort);
            client.AckReceived += ackHandler; // 绑定ACK处理
            return client;
        };

        // 生成测试数据
        var testMessages = Enumerable.Range(0, _messageCountPerClient)
            .Select(i => new CommunicationData
            {
                InfoType = InfoType.CtsNormal,
                Message = "TestData-" + i, // 固定消息内容以减少开销
                Priority = _testPriority,
                // SeqNum 由客户端自动分配，无需手动设置
            }).ToList();

        // 并发客户端任务
        var clientTasks = new List<Task>();
        for (int c = 0; c < _clientCount; c++)
        {
            var client = createClient();
            await client.Connect(); // 提前连接以避免连接时间影响测试
            clientTasks.Add(Task.Run(async () => await SendMessages(client, testMessages, sentCount)));
        }

        // 执行测试
        await Task.WhenAll(clientTasks);
        stopwatch.Stop();

        // 输出统计结果
        Console.WriteLine($"=== 压力测试结果 ===");
        Console.WriteLine($"测试时间: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"并发客户端数: {_clientCount}");
        Console.WriteLine($"总发送消息数: {_clientCount * _messageCountPerClient}");
        Console.WriteLine($"成功接收ACK数: {receivedAckCount[_testPriority]}");
        Console.WriteLine($"吞吐量: {receivedAckCount[_testPriority] / (stopwatch.ElapsedMilliseconds / 1000.0):F2} 条/秒");

        if (latencyList.Count > 0)
        {
            var avgLatency = latencyList.Average() / Stopwatch.Frequency * 1000; // 转换为毫秒
            Console.WriteLine($"平均延迟: {avgLatency:F2} ms");
        }

        // 清理资源
        foreach (var client in clientTasks.Select(t => t.AsyncState as SocketClientInstance))
        {
            client?.Disconnect();
        }
    }

    private ConcurrentDictionary<int, long> _sentTimestamps = new ConcurrentDictionary<int, long>();

    private async Task SendMessages(SocketClientInstance client, List<CommunicationData> messages,
        ConcurrentDictionary<DataPriority, long> sentCount)
    {
        var tasks = new List<Task>();
        foreach (var msg in messages)
        {
            tasks.Add(Task.Run(async () =>
            {
                // 记录发送时间戳（基于Stopwatch的高精度时钟）
                var sendTime = Stopwatch.GetTimestamp();
                await client.SendData(msg);
                _sentTimestamps.TryAdd(msg.SeqNum, sendTime); // 关联序列号和发送时间
                sentCount.AddOrUpdate(msg.Priority, 1, (k, v) => v + 1);
            }));
        }
        await Task.WhenAll(tasks);
    }
}