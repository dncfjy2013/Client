using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Interface
{
    /// <summary>
    /// UDP消息处理插件接口
    /// </summary>
    public interface IUdpPlugin
    {
        ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct);
        ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct);
    }
}
