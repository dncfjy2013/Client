using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Interface
{
    /// <summary>
    /// UDP传输接口，封装底层UDP操作
    /// </summary>
    public interface IUdpTransport
    {
        Task<int> SendAsync(byte[] buffer, int bytes, IPEndPoint endpoint);
        Task<UdpReceiveResult> ReceiveAsync();
        void Connect(IPEndPoint endpoint);
        void Close();
    }
}
