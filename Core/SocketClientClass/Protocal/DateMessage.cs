using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Protocol;

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
// ProtocolHeaderExtensions 类修正（固定4字节编码，非Varint）
public static class ProtocolHeaderExtensions
{
    // 编码：固定4字节，每个字节低7位存储数据，前3字节最高位设为1（标识后续有字节），第4字节最高位设为0
    private static byte[] EncodeFixed4Bytes(uint value)
    {
        byte[] bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            bytes[i] = (byte)(value & 0x7F);
            value >>= 7;
            if (i < 3) bytes[i] |= 0x80; // 前3字节标记为后续有字节（即使无后续，强制格式）
        }
        return bytes;
    }

    // 解码：固定读取4字节，组合为uint32
    private static uint DecodeFixed4Bytes(byte[] data, int startIndex)
    {
        if (startIndex + 4 > data.Length)
            throw new ArgumentException("Insufficient data for fixed 4-byte decode");

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            value |= (uint)(data[startIndex + i] & 0x7F) << (7 * i);
        }
        return value;
    }

    // 修正后的头部序列化（明确固定4字节长度字段）
    public static byte[] ToBytes(this Protocol.ProtocolHeader header)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)header.Version); // 1字节版本
        ms.Write(header.Reserved.ToByteArray().Take(3).ToArray()); // 3字节保留字段（截断/补零由Protobuf自动处理）
        ms.Write(EncodeFixed4Bytes(header.MessageLength)); // 4字节固定长度字段
        return ms.ToArray();
    }

    // 修正后的头部反序列化（明确固定4字节解析）
    public static bool TryFromBytes(byte[] data, out Protocol.ProtocolHeader result)
    {
        result = new Protocol.ProtocolHeader { Reserved = ByteString.CopyFrom(new byte[3]) };
        if (data.Length < 8) return false; // 固定头部长度：1+3+4=8字节

        result.Version = data[0];
        result.Reserved = ByteString.CopyFrom(data, 1, 3);
        result.MessageLength = DecodeFixed4Bytes(data, 4); // 从第4字节开始解析4字节长度
        return true;
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

    // 组装数据包（明确各部分字节长度）
    private byte[] AssemblePacket(byte[] buffer, byte[] headerBytes, byte[] protoData, uint checksum)
    {
        int headerLength = headerBytes.Length; // 固定8字节
        int checksumLength = sizeof(uint); // 4字节
        int totalLength = headerLength + protoData.Length + checksumLength;

        if (totalLength > _config.MaxPacketSize)
            throw new ProtocolSerializationException($"Packet too large: {totalLength} > {_config.MaxPacketSize}");

        Array.Copy(headerBytes, buffer, headerLength);
        Array.Copy(protoData, 0, buffer, headerLength, protoData.Length);
        Array.Copy(BitConverter.GetBytes(checksum), 0, buffer, headerLength + protoData.Length, checksumLength);

        return buffer.AsSpan(0, totalLength).ToArray();
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
            int expectedPayloadLength = (int)header.MessageLength; // 包含data和checksum的总长度
            if (data.Length < 8 + expectedPayloadLength) // 头部8字节 + 有效载荷
            {
                return (false, null, "Incomplete packet");
            }

            // 4. 跳过头部, 提取有效载荷
            byte[] payload = data.Skip(8).Take(expectedPayloadLength).ToArray();

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
