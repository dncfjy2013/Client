using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass
{
    /// <summary>
    /// UdpClient的适配器实现，封装基础UDP操作
    /// </summary>
    internal class UdpClientAdapter : IUdpTransport
    {
        private readonly UdpClient _client;

        public UdpClientAdapter() => _client = new UdpClient();

        public Task<int> SendAsync(byte[] buffer, int bytes, IPEndPoint endpoint) =>
            _client.SendAsync(buffer, bytes, endpoint);

        public Task<UdpReceiveResult> ReceiveAsync() => _client.ReceiveAsync();

        public void Connect(IPEndPoint endpoint) => _client.Connect(endpoint);
        public void Close() => _client.Close();
    }
}
