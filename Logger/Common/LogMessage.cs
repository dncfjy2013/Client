namespace Client.Logger.Common
{
    #region Log Message Structure
    public struct LogMessage
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
}
