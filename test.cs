using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Client;

public class MassiveFileStressTester
{
    private const int ParallelClients = 10;          // 并发客户端数量
    private const int FileSizeGB = 5;               // 单个文件大小（GB）
    private const int MaxRetries = 3;                // 最大重试次数
    private const int ProgressIntervalSec = 5;       // 进度报告间隔
    private const int MemorySamplingIntervalMs = 500;// 内存采样间隔

    private readonly string _serverIp;
    private readonly int _port;
    private readonly ConcurrentBag<TestResult> _results = new();
    private readonly MemoryMonitor _memoryMonitor = new();

    public MassiveFileStressTester(string serverIp, int port)
    {
        _serverIp = serverIp;
        _port = port;
    }

    public async Task RunTestAsync()
    {
        // 启动内存监控
        _memoryMonitor.Start(MemorySamplingIntervalMs);

        // 创建虚拟文件目录
        using var tempDir = new TempDirectory();
        var testFile = Path.Combine("C:\\Users\\95272\\AppData\\Local\\Temp\\dafe722a-6ec1-4405-b8a4-96b41efd89f6", $"test_{FileSizeGB}GB.dat");
        //GenerateVirtualFile(testFile, FileSizeGB);

        // 启动并行测试
        var tasks = Enumerable.Range(0, ParallelClients)
            .Select(i => Task.Run(() => TestClient(i, testFile)))
            .ToArray();

        // 启动进度监控
        var monitorTask = ProgressReporter();

        await Task.WhenAll(tasks);
        await monitorTask;

        GenerateReport();
    }

    private async Task TestClient(int clientId, string filePath)
    {
        var result = new TestResult { ClientId = clientId };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = new ClientInstance(_serverIp, _port);
            client.OnFileTransferProgress += progress =>
                UpdateProgress(clientId, progress, result);

            // 连接并上传
            await client.Connect();
            await AttemptTransferWithRetry(client, filePath, MaxRetries, result);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            _results.Add(result);
        }
    }

    #region 核心逻辑
    private void GenerateVirtualFile(string path, int sizeGB)
    {
        Console.WriteLine($"Generating {sizeGB}GB test file...");
        using var fs = File.Create(path);
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        var random = new Random();

        for (long i = 0; i < sizeGB * 1024L; i++) // 1GB = 1024MB
        {
            random.NextBytes(buffer);
            fs.Write(buffer, 0, buffer.Length);
        }
    }

    private async Task AttemptTransferWithRetry(ClientInstance client,
        string filePath, int retriesLeft, TestResult result)
    {
        try
        {
            await client.UploadFileAsync(filePath, DataPriority.High);
        }
        catch (Exception ex) when (retriesLeft > 0)
        {
            result.RetryCount++;
            await Task.Delay(CalculateRetryDelay(retriesLeft));
            await AttemptTransferWithRetry(client, filePath, retriesLeft - 1, result);
        }
    }
    #endregion

    #region 辅助方法
    private void UpdateProgress(int clientId, FileTransferProgress progress, TestResult result)
    {
        result.TotalBytes = progress.TotalBytes;
        result.TransferredBytes = progress.TransferredBytes;
    }

    private async Task ProgressReporter()
    {
        while (!_results.All(r => r.IsCompleted))
        {
            await Task.Delay(ProgressIntervalSec * 1000);

            var active = _results.Count(r => !r.IsCompleted);
            var completed = _results.Count(r => r.Success);
            var failed = _results.Count(r => r.IsCompleted && !r.Success);

            Console.WriteLine($"[Progress] Active: {active} | " +
                            $"Completed: {completed} | Failed: {failed} | " +
                            $"Memory: {_memoryMonitor.PeakMemoryMB}MB (Current: {_memoryMonitor.CurrentMemoryMB}MB)");
        }
    }

    private TimeSpan CalculateRetryDelay(int remainingRetries)
    {
        return TimeSpan.FromSeconds(Math.Pow(2, MaxRetries - remainingRetries));
    }

    private void GenerateReport()
    {
        var successful = _results.Where(r => r.Success).ToList();
        var avgThroughput = successful.Average(r =>
            r.TotalBytes / 1024.0 / 1024 / r.Duration.TotalSeconds);

        Console.WriteLine($@"
=== 压力测试报告 ===
测试规模:
    客户端数量:    {ParallelClients}
    单文件大小:    {FileSizeGB}GB
    总数据量:      {ParallelClients * FileSizeGB}GB

传输结果:
    成功率:        {successful.Count}/{ParallelClients} ({(successful.Count * 100.0 / ParallelClients):F1}%)
    平均吞吐量:    {avgThroughput:F2} MB/s
    最高吞吐量:    {successful.Max(r => r.TotalBytes / 1024.0 / 1024 / r.Duration.TotalSeconds):F2} MB/s
    平均重试次数:  {successful.Average(r => r.RetryCount):F1}

资源使用:
    峰值内存:      {_memoryMonitor.PeakMemoryMB} MB
    平均CPU:       {_memoryMonitor.AverageCpuUsage:F1}%
");
    }
    #endregion
}

#region 辅助类
public class TestResult
{
    public int ClientId { get; set; }
    public bool Success { get; set; }
    public TimeSpan Duration { get; set; }
    public int RetryCount { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public string Error { get; set; }
    public bool IsCompleted => Success || !string.IsNullOrEmpty(Error);
}

public class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            Guid.NewGuid().ToString()
        );
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        Directory.Delete(Path, true);
    }
}

public class MemoryMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private long _peakMemory = 0;
    private double _totalCpu = 0;
    private int _sampleCount = 0;

    public long CurrentMemoryMB => _process.PrivateMemorySize64 / 1024 / 1024;
    public long PeakMemoryMB => _peakMemory / 1024 / 1024;
    public double AverageCpuUsage => _totalCpu / _sampleCount;

    public void Start(int intervalMs)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                var memory = _process.PrivateMemorySize64;
                if (memory > _peakMemory) _peakMemory = memory;

                var cpuTime = _process.TotalProcessorTime.TotalMilliseconds;
                await Task.Delay(intervalMs);
                var cpuDelta = _process.TotalProcessorTime.TotalMilliseconds - cpuTime;

                _totalCpu += cpuDelta / intervalMs * 100;
                _sampleCount++;
            }
        });
    }
}
#endregion

// 使用示例
