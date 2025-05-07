using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Interface
{
    /// <summary>
    /// 消息编解码接口，支持泛型类型处理
    /// </summary>
    public interface IMessageEncoder
    {
        byte[] Encode<T>(T message);
        T Decode<T>(byte[] buffer);
    }

}
