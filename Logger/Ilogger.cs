using Client.Logger.Common;

namespace Client.Logger
{
    public interface ILogger : IDisposable
    {
        LogLevel ConsoleLogLevel { get; set; }
        LogLevel FileLogLevel { get; set; }
        string LogFilePath { get; set; }
        bool EnableAsyncWriting { get; set; }

        void LogTrace(string message);
        void LogDebug(string message);
        void LogInformation(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogCritical(string message);
    }
}