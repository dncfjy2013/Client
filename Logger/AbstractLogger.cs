using Client.Logger.Common;

namespace Client.Logger
{
    public abstract class AbstractLogger : ILogger
    {
        protected readonly LoggerConfig _config;
        protected readonly CancellationTokenSource _cts = new CancellationTokenSource();
        protected bool _isDisposed;

        public LogLevel ConsoleLogLevel
        {
            get => _config.ConsoleLogLevel;
            set => _config.ConsoleLogLevel = value;
        }

        public LogLevel FileLogLevel
        {
            get => _config.FileLogLevel;
            set => _config.FileLogLevel = value;
        }

        public string LogFilePath
        {
            get => _config.LogFilePath;
            set => _config.LogFilePath = value;
        }

        public bool EnableAsyncWriting
        {
            get => _config.EnableAsyncWriting;
            set => _config.EnableAsyncWriting = value;
        }

        protected AbstractLogger(LoggerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public abstract void LogTrace(string message);
        public abstract void LogDebug(string message);
        public abstract void LogInformation(string message);
        public abstract void LogWarning(string message);
        public abstract void LogError(string message);
        public abstract void LogCritical(string message);

        public virtual void Dispose()
        {
            if (_isDisposed) return;
            _cts.Cancel();
            _isDisposed = true;
        }
    }
}