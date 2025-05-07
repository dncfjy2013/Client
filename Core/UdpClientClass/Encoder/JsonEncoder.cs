using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Encoder
{
    /// <summary>
    /// JSON格式编解码器，使用System.Text.Json实现
    /// </summary>
    public class JsonEncoder : IMessageEncoder
    {
        public byte[] Encode<T>(T message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        }

        public T Decode<T>(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
                throw new ArgumentException("接收数据为空");

            var json = Encoding.UTF8.GetString(buffer);
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("无效的JSON格式", ex);
            }
        }
    }
}
