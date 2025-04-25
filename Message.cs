using Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client
{
    public class FileTransferProgress
    {
        public string FileId { get; set; }
        public string FileName { get; set; }
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }
        public double Progress => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes : 0;
        public TransferStatus Status { get; set; }
    }
    public class FileTransferSession
    {
        public string FileId { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public int TotalChunks { get; set; }
        public int ChunkSize { get; set; }
        public DataPriority Priority { get; set; }
        public long TransferredBytes;
        public string FileHash { get; set; }
    }
    public enum TransferStatus
    {
        Preparing,
        Transferring,
        Verifying,
        Completed,
        Failed,
        Canceled
    }

    public class DependingMessage
    {
        public CommunicationData communicationData { get; set; }
        public int RetryCount { get; set; } = 0;      // 新增重试计数器
        public DateTime FirstSentTime { get; set; }   // 新增首次发送时间
        public bool IsAck { get; set; }
    }
    public class RetryConfig
    {
        public int MaxRetries { get; set; }
        public int BaseDelayMs { get; set; }
        public double BackoffFactor { get; set; }
        public float PriorityWeight { get; set; }
    }

    public class PendingMessage
    {
        public CommunicationData Data { get; init; }
        public DateTime FirstSent { get; init; }
        public DateTime LastSent { get; set; }
        public int RetryCount { get; set; }
        public float PriorityWeight { get; init; }
    }
}
