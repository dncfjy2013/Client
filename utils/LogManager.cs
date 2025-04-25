using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Client.utils
{
    #region Level
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5,
        None = 6
    }
    #endregion

    #region Configuration
    public class LoggerConfig
    {
        public LogLevel ConsoleLogLevel { get; set; } = LogLevel.Trace;
        public LogLevel FileLogLevel { get; set; } = LogLevel.Information;
        public string LogFilePath { get; set; } = "application.log";
        public bool EnableAsyncWriting { get; set; } = true;
    }
    #endregion

    #region Log Message Structure
    public  struct LogMessage
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }
        public int ThreadId { get; }
        public string ThreadName { get; }

        public LogMessage(DateTime timestamp, LogLevel level, string message,
            int threadId, string threadName)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
            ThreadId = threadId;
            ThreadName = threadName;
        }
    }
    #endregion
    public class Logger : IDisposable
    {
        private static readonly Lazy<Logger> _instance = new Lazy<Logger>(() => new Logger());
        public static Logger Instance => _instance.Value;

        private readonly BlockingCollection<LogMessage> _logQueue = new BlockingCollection<LogMessage>(1000);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly LoggerConfig _config;
        private readonly Task _logWriterTask;

        public Logger() : this(new LoggerConfig()) { }

        public Logger(LoggerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

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
        public void LogTrace(string message) => Log(LogLevel.Trace, message);
        public void LogDebug(string message) => Log(LogLevel.Debug, message);
        public void LogInformation(string message) => Log(LogLevel.Information, message);
        public void LogWarning(string message) => Log(LogLevel.Warning, message);
        public void LogError(string message) => Log(LogLevel.Error, message);
        public void LogCritical(string message) => Log(LogLevel.Critical, message);

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
            try
            {
                File.AppendAllText(_config.LogFilePath,
                    FormatMessage(message, "FILE") + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // 文件写入失败处理
                Console.WriteLine($"Failed to write log to file: {ex.Message}");
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