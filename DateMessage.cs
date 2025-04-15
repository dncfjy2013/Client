using Client.Common;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.Json;

[Serializable]
public class CommunicationData
{
    public string Message { get; set; }
    public InfoType InfoType { get; set; }
    // 新增可靠性字段
    public int SeqNum { get; set; }     // 序列号
    public int AckNum { get; set; }     // 确认号
    public DataPriority Priority { get; set; } = DataPriority.Medium;

    // 文件传输专用字段
    public string FileId { get; set; }          // 文件唯一标识
    public string FileName { get; set; }        // 文件名
    public long FileSize { get; set; }          // 文件总大小
    public int ChunkIndex { get; set; }         // 当前块索引
    public int TotalChunks { get; set; }        // 总块数
    public byte[] ChunkData { get; set; }       // 块数据
    public string MD5Hash { get; set; }         // 文件整体MD5
    public string ChunkMD5 { get; set; }        // 当前块MD5
}
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
public enum DataPriority
{
    High = 0,    // 高优先级，需要严格SEQ
    Medium = 1,  // 中等优先级
    Low = 2      // 低优先级，宽松SEQ
}
// 协议配置类（新增）
public class ProtocolConfiguration
{
    public IDataSerializer DataSerializer { get; set; } = new JsonSerializerAdapter();
    public IChecksumCalculator ChecksumCalculator { get; set; } = new Crc16Calculator();
    public byte[] SupportedVersions { get; set; } = new[] { ProtocolHeader.CurrentVersion };
    public int MaxPacketSize { get; set; } = 1024 * 1024; // 1MB
    public Encoding TextEncoding { get; set; } = Encoding.UTF8;
}

// 数据序列化接口（新增）
public interface IDataSerializer
{
    byte[] Serialize<T>(T data);
    T Deserialize<T>(byte[] data);
}

// JSON序列化适配器（新增）
public class JsonSerializerAdapter : IDataSerializer
{
    public byte[] Serialize<T>(T data)
    {
        return JsonSerializer.SerializeToUtf8Bytes(data);
    }

    public T Deserialize<T>(byte[] data)
    {
        return JsonSerializer.Deserialize<T>(data);
    }
}

// 校验和计算接口（新增）
public interface IChecksumCalculator
{
    ushort Calculate(byte[] data);
}

// CRC16优化实现（改进）
public class Crc16Calculator : IChecksumCalculator
{
    private static readonly ushort[] CrcTable = GenerateCrcTable();

    public ushort Calculate(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF]);
        }
        return crc;
    }

    private static ushort[] GenerateCrcTable()
    {
        ushort[] table = new ushort[256];
        for (ushort i = 0; i < table.Length; ++i)
        {
            ushort value = 0;
            ushort temp = i;
            for (byte j = 0; j < 8; ++j)
            {
                if (((value ^ temp) & 0x0001) != 0)
                {
                    value = (ushort)((value >> 1) ^ 0xA001);
                }
                else
                {
                    value >>= 1;
                }
                temp >>= 1;
            }
            table[i] = value;
        }
        return table;
    }
}

// 协议头定义（增强）
public class ProtocolHeader
{
    public const byte CurrentVersion = 0x01;

    public byte Version { get; set; }
    public byte[] Reserved { get; set; } = new byte[3];
    public int MessageLength { get; set; }

    public byte[] ToBytes()
    {
        var header = new byte[1 + Reserved.Length + sizeof(int)];
        header[0] = Version;
        Buffer.BlockCopy(Reserved, 0, header, 1, Reserved.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(MessageLength), 0, header, 4, sizeof(int));
        return header;
    }

    public static bool TryFromBytes(byte[] data, out ProtocolHeader header)
    {
        header = null;
        if (data?.Length != 8) return false;

        try
        {
            header = new ProtocolHeader
            {
                Version = data[0],
                Reserved = new byte[3],
                MessageLength = BitConverter.ToInt32(data, 4)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// 协议数据包封装（增强）
public class ProtocolPacket
{
    private static readonly ConcurrentQueue<byte[]> BufferPool = new ConcurrentQueue<byte[]>();
    private readonly ProtocolConfiguration _config;

    public ProtocolPacket(ProtocolConfiguration config = null)
    {
        _config = config ?? new ProtocolConfiguration();
    }

    public ProtocolHeader Header { get; set; }
    public CommunicationData Data { get; set; }
    public ushort Checksum { get; set; }

    public byte[] ToBytes()
    {
        // 使用对象池减少内存分配
        if (!BufferPool.TryDequeue(out var buffer))
        {
            buffer = ArrayPool<byte>.Shared.Rent(_config.MaxPacketSize);
        }

        try
        {
            // 序列化业务数据
            byte[] jsonData = _config.DataSerializer.Serialize(Data);

            // 计算校验和
            Checksum = _config.ChecksumCalculator.Calculate(jsonData);

            // 构建完整数据包
            int payloadLength = jsonData.Length + sizeof(ushort);
            byte[] payloadWithChecksum = new byte[payloadLength];
            Buffer.BlockCopy(jsonData, 0, payloadWithChecksum, 0, jsonData.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(Checksum), 0, payloadWithChecksum, jsonData.Length, sizeof(ushort));

            // 更新协议头信息
            Header.MessageLength = payloadLength;
            byte[] headerBytes = Header.ToBytes();

            // 合并协议头和负载数据
            int totalLength = headerBytes.Length + payloadLength;
            Buffer.BlockCopy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Buffer.BlockCopy(payloadWithChecksum, 0, buffer, headerBytes.Length, payloadLength);

            return buffer.Take(totalLength).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<(bool Success, ProtocolPacket Packet)> TryFromBytesAsync(byte[] data, ProtocolConfiguration config)
    {
        if (data?.Length < 8) return (false, null);

        try
        {
            // 解析协议头
            if (!ProtocolHeader.TryFromBytes(data.Take(8).ToArray(), out ProtocolHeader header))
                return (false, null);

            // 版本兼容性检查
            if (!config.SupportedVersions.Contains(header.Version))
                return (false, null);

            // 提取负载数据
            byte[] payload = data.Skip(8).ToArray();

            // 验证数据长度
            if (payload.Length != header.MessageLength)
                return (false, null);

            // 异步校验和验证
            ushort receivedChecksum = BitConverter.ToUInt16(payload, payload.Length - sizeof(ushort));
            byte[] jsonData = payload.Take(payload.Length - sizeof(ushort)).ToArray();

            ushort calculatedChecksum = await Task.Run(() =>
                config.ChecksumCalculator.Calculate(jsonData));

            if (receivedChecksum != calculatedChecksum)
                return (false, null);

            // 异步反序列化
            CommunicationData communicationData = await Task.Run(() =>
                config.DataSerializer.Deserialize<CommunicationData>(jsonData));

            var packet = new ProtocolPacket(config)
            {
                Header = header,
                Data = communicationData,
                Checksum = receivedChecksum
            };

            return (true, packet);
        }
        catch
        {
            return (false, null);
        }
    }
}

// 扩展方法（新增）
public static class ProtocolExtensions
{
    public static async Task<ProtocolPacket> CreatePacketAsync(this CommunicationData data,
                                                             ProtocolConfiguration config = null)
    {
        var packet = new ProtocolPacket(config)
        {
            Header = new ProtocolHeader { Version = ProtocolHeader.CurrentVersion },
            Data = data
        };
        return new ProtocolPacket(config) { Data = data, Header = packet.Header };
    }

    public static async Task<bool> ValidatePacketAsync(this ProtocolPacket packet,
                                                     ProtocolConfiguration config = null)
    {
        config ??= new ProtocolConfiguration();
        byte[] serializedData = config.DataSerializer.Serialize(packet.Data);
        ushort calculatedChecksum = config.ChecksumCalculator.Calculate(serializedData);
        return calculatedChecksum == packet.Checksum;
    }
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
