#define DISABLE_TRACE_LOGGING 
#define DISABLE_DEBUG_LOGGING 
//#define DISABLE_INFORMATION_LOGGING 

using Client.Logger.Common;
using System.Collections.Concurrent;

namespace Client.Logger
{
    public class LoggerInstance : AbstractLogger, ILogger
    {
        private static readonly Lazy<LoggerInstance> _instance = new Lazy<LoggerInstance>(() => new LoggerInstance());
        public static LoggerInstance Instance => _instance.Value;

        private readonly BlockingCollection<LogMessage> _logQueue = new BlockingCollection<LogMessage>(1000);
        private readonly Task _logWriterTask;

        public LoggerInstance() : this(new LoggerConfig()) { }

        public LoggerInstance(LoggerConfig config) : base(config)
        {
            if (_config.EnableAsyncWriting)
            {
                _logWriterTask = Task.Factory.StartNew(
                    ProcessLogQueue,
                    _cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        #region Public Logging Methods

#if !DISABLE_TRACE_LOGGING
        public override void LogTrace(string message) => Log(LogLevel.Trace, message);
#else
        public override void LogTrace(string message) { }
#endif

#if !DISABLE_DEBUG_LOGGING
        public override void LogDebug(string message) => Log(LogLevel.Debug, message);
#else
        public override void LogDebug(string message) { }
#endif

#if !DISABLE_INFORMATION_LOGGING
        public override void LogInformation(string message) => Log(LogLevel.Information, message);
#else
        public override void LogInformation(string message) { }
#endif

        public override void LogWarning(string message) => Log(LogLevel.Warning, message);
        public override void LogError(string message) => Log(LogLevel.Error, message);
        public override void LogCritical(string message) => Log(LogLevel.Critical, message);

        private void Log(LogLevel level, string message)
        {
            if (level < _config.ConsoleLogLevel && level < _config.FileLogLevel)
                return;

            var logMessage = new LogMessage(
                DateTime.UtcNow,
                level,
                message,
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.Name);

            // 同步处理控制台输出
            if (level >= _config.ConsoleLogLevel)
            {
                WriteToConsole(logMessage);
            }

            // 异步处理文件输出
            if (_config.EnableAsyncWriting && level >= _config.FileLogLevel)
            {
                if (!_logQueue.TryAdd(logMessage, 50)) // 添加超时保护
                {
                    // 队列已满时的降级处理
                    WriteToConsole(new LogMessage(
                        DateTime.UtcNow,
                        LogLevel.Error,
                        "Log queue is full, message dropped: " + message,
                        Environment.CurrentManagedThreadId,
                        Thread.CurrentThread.Name));
                }
            }
        }
        #endregion

        #region Log Processing
        private void ProcessLogQueue()
        {
            try
            {
                foreach (var message in _logQueue.GetConsumingEnumerable(_cts.Token))
                {
                    WriteToFile(message);
                }

                // 处理队列中剩余的消息
                while (_logQueue.TryTake(out var message))
                {
                    WriteToFile(message);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常终止
            }
        }

        private void WriteToConsole(LogMessage message)
        {
            var color = GetConsoleColor(message.Level);
            var originalColor = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(FormatMessage(message, "CONSOLE"));
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        private void WriteToFile(LogMessage message)
        {
            while (true)
            {
                try
                {
                    using (FileStream fileStream = new FileStream(_config.LogFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        using (StreamWriter writer = new StreamWriter(fileStream))
                        {
                            writer.WriteLine(FormatMessage(message, "FILE"));
                        }
                    }
                    break;
                }
                catch (IOException ex) when (ex.HResult == -2147024864) // 表示文件被占用
                {
                    // 等待一段时间后重试
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    // 文件写入失败处理
                    Console.WriteLine($"Failed to write log to file: {ex.Message}");
                    break;
                }
            }
        }
        #endregion

        #region Helpers
        private string FormatMessage(LogMessage message, string target)
        {
            return $"[{message.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] " +
                   $"[{message.Level.ToString().ToUpper()}] " +
                   $"[Thread: {message.ThreadId:0000}/{message.ThreadName ?? "Unknown"}] " +
                   //$"[Target: {target}] " +
                   $"{message.Message}";
        }

        private ConsoleColor GetConsoleColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Critical => ConsoleColor.DarkRed,
                LogLevel.Error => ConsoleColor.DarkMagenta,
                LogLevel.Warning => ConsoleColor.DarkYellow,
                LogLevel.Information => ConsoleColor.DarkGreen,
                LogLevel.Debug => ConsoleColor.DarkCyan,
                LogLevel.Trace => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray
            };
        }
        #endregion

        #region Cleanup
        public void Dispose()
        {
            _cts.Cancel();
            _logQueue.CompleteAdding();

            try
            {
                _logWriterTask?.Wait(3000);
            }
            catch (AggregateException)
            {
                // 忽略任务取消异常
            }

            _cts.Dispose();
            _logQueue.Dispose();
        }
        #endregion
    }
}
