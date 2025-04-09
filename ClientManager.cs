using Client.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client
{
    // 修改后的客户端管理类
    public class ClientManager
    {
        private readonly int _clientCount;
        private readonly int _maxConcurrent;
        private readonly SemaphoreSlim _concurrentLimit;
        private readonly List<Task> _clientTasks = new();
        private readonly Random _rnd = new();

        public ClientManager(int clientCount, int maxConcurrent = 10)
        {
            _clientCount = clientCount;
            _maxConcurrent = maxConcurrent;
            _concurrentLimit = new SemaphoreSlim(_clientCount);
        }

        public async Task StartClientsAsync(string serverIp, int port)
        {
            var stats = new ConcurrentDictionary<string, int>();

            for (int i = 0; i < _clientCount; i++)
            {
                await _concurrentLimit.WaitAsync();

                _clientTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        ClientInstance client = new ClientInstance(serverIp, port);
                        await client.Connect();

                        // 模拟业务操作
                        var successCount = 0;
                        for (int j = 0; j < _maxConcurrent; j++) // 每个客户端执行10次操作
                        {
                            var data = GenerateTestData();
                            await client.SendData(data);
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            var response = await Task.Run(() => client.ReceiveData(cts.Token));
                            if (response != null && response.Message == "ACK")
                            {
                                Interlocked.Increment(ref successCount);
                            }

                            await Task.Delay(_rnd.Next(10, 100)); // 随机间隔
                        }

                        stats.AddOrUpdate($"Client_{i}_Success", successCount, (k, v) => v + successCount);
                    }
                    catch (Exception ex)
                    {
                        stats.AddOrUpdate("Errors", 1, (k, v) => v + 1);
                    }
                    finally
                    {
                        _concurrentLimit.Release();
                    }
                }));
            }

            await Task.WhenAll(_clientTasks);
            PrintStatistics(stats);
        }

        private CommunicationData GenerateTestData()
        {
            return new CommunicationData
            {
                Message = $"TestData",
                InfoType = InfoType.Normal
            };
        }

        private void PrintStatistics(ConcurrentDictionary<string, int> stats)
        {
            Console.WriteLine("\n=== 测试结果 ===");
            Console.WriteLine($"总客户端数: {_clientCount}");
            Console.WriteLine($"总请求数: {_clientCount * 10}");
            Console.WriteLine($"成功请求数: {stats.GetValueOrDefault("TotalSuccess", 0)}");
            Console.WriteLine($"失败请求数: {stats.GetValueOrDefault("Errors", 0)}");
            Console.WriteLine($"平均成功率: {CalculateSuccessRate(stats):P2}");
        }

        private double CalculateSuccessRate(ConcurrentDictionary<string, int> stats)
        {
            int totalSuccess = stats.Where(k => k.Key.StartsWith("Client_")).Sum(v => v.Value);
            return totalSuccess / (double)(_clientCount * 10);
        }
    }

}
