using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Protocol;

// 文件传输信息类
public class FileTransferInfo
{
    public string FileId { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public int TotalChunks { get; set; }
    public int ChunkSize { get; set; }
    public string FilePath { get; set; }
    public ConcurrentDictionary<int, byte[]> ReceivedChunks { get; set; }
}
// 协议配置类
// 协议配置类
public class ProtocolConfiguration
{
    public IDataSerializer DataSerializer { get; set; } = new ProtobufSerializerAdapter();
    public IChecksumCalculator ChecksumCalculator { get; set; } = new Crc16Calculator();
    public byte[] SupportedVersions { get; set; } = new byte[] { 0x01 };
    public int MaxPacketSize { get; set; } = 1024 * 1024; // 1MB
    public Encoding TextEncoding { get; set; } = Encoding.UTF8;
}

// 数据序列化接口
public interface IDataSerializer
{
    byte[] Serialize<T>(T data) where T : IMessage<T>;
    T Deserialize<T>(byte[] data) where T : IMessage<T>, new();
}

// Protobuf序列化适配器
public class ProtobufSerializerAdapter : IDataSerializer
{
    public byte[] Serialize<T>(T data) where T : IMessage<T>
    {
        return data.ToByteArray();
    }

    public T Deserialize<T>(byte[] data) where T : IMessage<T>, new()
    {
        var message = new T();
        message.MergeFrom(data);
        return message;
    }
}

// 校验和计算接口
public interface IChecksumCalculator
{
    ushort Calculate(byte[] data);
}

// CRC16 优化实现
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

// 协议头定义
// 定义扩展方法的静态类
// 优化后的 ProtocolHeaderExtensions 类，使用 Varint 编码 message_length
public static class ProtocolHeaderExtensions
{
    public static byte[] ToBytes(this Protocol.ProtocolHeader header)
    {
        if (header == null)
        {
            throw new ArgumentNullException(nameof(header));
        }

        var ms = new MemoryStream();
        // 版本号 (1字节)
        ms.WriteByte((byte)header.Version);

        // 保留字段 (3字节) - 处理null或不足3字节的情况
        byte[] reservedBytes = header.Reserved?.ToByteArray() ?? new byte[3];
        if (reservedBytes.Length < 3)
        {
            Array.Resize(ref reservedBytes, 3);
        }
        ms.Write(reservedBytes, 0, 3);

        // 消息长度 (Varint 编码)
        var lengthBytes = EncodeVarint((uint)header.MessageLength);
        ms.Write(lengthBytes, 0, lengthBytes.Length);

        return ms.ToArray();
    }

    public static bool TryFromBytes(byte[] data, out Protocol.ProtocolHeader result)
    {
        result = new Protocol.ProtocolHeader
        {
            // 设置默认的3字节保留字段
            Reserved = ByteString.CopyFrom(new byte[3])
        };

        if (data == null || data.Length < 4)
        {
            return false;
        }

        try
        {
            result.Version = data[0];
            result.Reserved = ByteString.CopyFrom(data, 1, 3);

            int index = 4;
            result.MessageLength = DecodeVarint(data, ref index);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Header parse error: {ex.Message}");
            return false;
        }
    }

    private static byte[] EncodeVarint(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte temp = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                temp |= 0x80;
            }
            bytes.Add(temp);
        } while (value != 0);
        return bytes.ToArray();
    }

    private static uint DecodeVarint(byte[] data, ref int index)
    {
        uint result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (index >= data.Length)
            {
                throw new IndexOutOfRangeException("Varint decoding error: unexpected end of data");
            }
            b = data[index++];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }
}

// 协议数据包封装
public class ProtocolPacketWrapper
{
    private readonly Protocol.ProtocolPacket _packet;
    private readonly ProtocolConfiguration _config;
    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    public ProtocolPacketWrapper(Protocol.ProtocolPacket packet, ProtocolConfiguration config = null)
    {
        _packet = packet ?? throw new ArgumentNullException(nameof(packet));
        _config = config ?? new ProtocolConfiguration();
    }

    public byte[] ToBytes()
    {
        byte[] buffer = BufferPool.Rent(_config.MaxPacketSize);
        try
        {
            // 1. 验证数据包完整性
            ValidatePacket();

            // 2. 序列化数据部分
            byte[] protoData = SerializeData();

            // 3. 计算校验和
            uint checksum = CalculateChecksum(protoData);

            // 4. 更新头部长度信息
            UpdateHeaderLength(protoData);

            // 5. 序列化头部
            byte[] headerBytes = SerializeHeader();

            // 6. 组装完整数据包
            return AssemblePacket(buffer, headerBytes, protoData, checksum);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Packet serialization error: {ex}");
            throw new ProtocolSerializationException("Failed to serialize protocol packet", ex);
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }

    private void ValidatePacket()
    {
        if (_packet?.Data == null)
        {
            throw new ProtocolSerializationException("Packet data is null");
        }

        if (_packet.Header == null)
        {
            throw new ProtocolSerializationException("Packet header is null");
        }
    }

    private byte[] SerializeData()
    {
        try
        {
            byte[] protoData = _config.DataSerializer.Serialize(_packet.Data);
            if (protoData == null || protoData.Length == 0)
            {
                throw new ProtocolSerializationException("Serialized data is empty");
            }
            return protoData;
        }
        catch (Exception ex)
        {
            throw new ProtocolSerializationException("Failed to serialize packet data", ex);
        }
    }

    private uint CalculateChecksum(byte[] protoData)
    {
        try
        {
            return (uint)_config.ChecksumCalculator.Calculate(protoData);
        }
        catch (Exception ex)
        {
            throw new ProtocolSerializationException("Failed to calculate checksum", ex);
        }
    }

    private void UpdateHeaderLength(byte[] protoData)
    {
        _packet.Header.MessageLength = (uint)(protoData.Length + sizeof(uint));
    }

    private byte[] SerializeHeader()
    {
        try
        {
            // 确保 Reserved 字段不为 null
            if (_packet.Header.Reserved == null)
            {
                _packet.Header.Reserved = ByteString.CopyFrom(new byte[3]);
            }

            byte[] headerBytes = _packet.Header.ToBytes();
            return headerBytes;
        }
        catch (Exception ex)
        {
            throw new ProtocolSerializationException("Failed to serialize header", ex);
        }
    }

    private byte[] AssemblePacket(byte[] buffer, byte[] headerBytes, byte[] protoData, uint checksum)
    {
        int totalLength = headerBytes.Length + protoData.Length + sizeof(uint);
        if (totalLength > _config.MaxPacketSize)
        {
            throw new ProtocolSerializationException(
                $"Packet size {totalLength} exceeds maximum allowed size {_config.MaxPacketSize}");
        }

        try
        {
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(protoData, 0, buffer, headerBytes.Length, protoData.Length);
            byte[] checksumBytes = BitConverter.GetBytes(checksum);
            Array.Copy(checksumBytes, 0, buffer, headerBytes.Length + protoData.Length, checksumBytes.Length);

            return buffer.AsSpan(0, totalLength).ToArray();
        }
        catch (Exception ex)
        {
            throw new ProtocolSerializationException("Failed to assemble packet", ex);
        }
    }

    public static (bool Success, Protocol.ProtocolPacket Packet, string Error) TryFromBytes(byte[] data, ProtocolConfiguration config = null)
    {
        config ??= new ProtocolConfiguration();
        if (data == null || data.Length < 4)
        {
            return (false, null, "Data length too short");
        }
        try
        {
            // 1. 解析头部
            if (!ProtocolHeaderExtensions.TryFromBytes(data, out var header))
            {
                return (false, null, "Invalid header format");
            }

            // 2. 版本检查
            if (!config.SupportedVersions.Contains((byte)header.Version))
            {
                return (false, null, $"Unsupported version: {header.Version}");
            }

            // 3. 检查数据长度
            int expectedLength = 4 + (int)header.MessageLength;
            if (data.Length < expectedLength)
            {
                return (false, null, $"Data length {data.Length} less than expected {expectedLength}");
            }

            // 4. 提取有效载荷
            byte[] payload = data.Skip(4).Take((int)header.MessageLength).ToArray();

            // 5. 提取校验和和数据
            uint receivedChecksum = BitConverter.ToUInt32(payload, payload.Length - sizeof(uint));
            byte[] protoData = payload.Take(payload.Length - sizeof(uint)).ToArray();

            // 6. 验证校验和
            uint calculatedChecksum = (uint)config.ChecksumCalculator.Calculate(protoData);

            if (receivedChecksum != calculatedChecksum)
            {
                return (false, null, $"Checksum mismatch: {receivedChecksum} != {calculatedChecksum}");
            }

            // 7. 反序列化数据
            var communicationData = config.DataSerializer.Deserialize<Protocol.CommunicationData>(protoData);

            return (true, new Protocol.ProtocolPacket
            {
                Header = header,
                Data = communicationData,
                Checksum = receivedChecksum
            }, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, $"Processing error: {ex.Message}");
        }
    }
}

// 自定义异常类
public class ProtocolSerializationException : Exception
{
    public ProtocolSerializationException(string message) : base(message) { }
    public ProtocolSerializationException(string message, Exception inner) : base(message, inner) { }
}

// 扩展方法
public static class ProtocolExtensions
{
    public static async System.Threading.Tasks.Task<ProtocolPacketWrapper> CreatePacketAsync(this Protocol.CommunicationData data,
                                                                                              ProtocolConfiguration config = null)
    {
        var packet = new ProtocolPacketWrapper(new Protocol.ProtocolPacket()
        {
            Header = new Protocol.ProtocolHeader { Version = 0x01 },
            Data = data
        }, config);

        return packet;
    }

    public static async System.Threading.Tasks.Task<bool> ValidatePacketAsync(this Protocol.ProtocolPacket packet,
                                                                              ProtocolConfiguration config = null)
    {
        config ??= new ProtocolConfiguration();
        byte[] serializedData = config.DataSerializer.Serialize(packet.Data);
        ushort calculatedChecksum = config.ChecksumCalculator.Calculate(serializedData);
        return calculatedChecksum == (ushort)packet.Checksum;
    }
}