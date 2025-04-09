using Client.Common.Log;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.utils
{
    public class Logger
    {
        // Define log levels
        private readonly BlockingCollection<(LogLevel Level, string Message)> _logQueue = new BlockingCollection<(LogLevel, string)>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly Task _logWriterTask;
        private readonly static object _lock = new object();
        private static Logger logger;

        public static Logger GetInstance()
        {
            if (logger == null)
            {
                lock (_lock)
                {
                    if (logger == null)
                        logger = new Logger();
                }
            }

            return logger;
        }

        public Logger()
        {
            // Start the log writer task
            _logWriterTask = Task.Factory.StartNew(() => LogWriterLoop(), _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Log(LogLevel level, string message)
        {
            Console.WriteLine($"[{DateTime.Now}] {level} " + message);
            _logQueue.Add((level, message));
        }

        public void LogTemp(LogLevel level, string message)
        {
            Console.WriteLine($"[{DateTime.Now}] {level} " + message);
        }

        private void LogWriterLoop()
        {
            try
            {
                foreach (var (level, rawMessage) in _logQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
                {
                    var formattedMessage = $"[{DateTime.Now}] [{level}] {rawMessage}";
                    File.AppendAllText("server.log", formattedMessage + Environment.NewLine);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        public void Stop()
        {
            // Signal the log writer to stop and wait for it to finish
            _cancellationTokenSource.Cancel();
            _logQueue.CompleteAdding();
            try
            {
                _logWriterTask.Wait();
            }
            catch (AggregateException)
            {
                // Handle any exceptions that occurred during the task execution if needed
            }
        }
    }
}
