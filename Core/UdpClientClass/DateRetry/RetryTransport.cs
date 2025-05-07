using Client.Core.UdpClientClass.Config;
using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.DateRetry
{
    /// <summary>
    /// 自动重试传输层
    /// </summary>
    public class RetryTransport : IUdpTransport
    {
        private readonly IUdpTransport _innerTransport;
        private readonly UdpClientOptions _options;

        public RetryTransport(IUdpTransport innerTransport, UdpClientOptions options)
        {
            _innerTransport = innerTransport;
            _options = options;
        }

        public async Task<int> SendAsync(byte[] buffer, int bytes, IPEndPoint endpoint)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await _innerTransport.SendAsync(buffer, bytes, endpoint)
                        .ConfigureAwait(false);
                }
                catch (SocketException ex) when (attempt++ < _options.MaxReconnectAttempts)
                {
                    await Task.Delay(_options.SendTimeout).ConfigureAwait(false);
                }
            }
        }

        public Task<UdpReceiveResult> ReceiveAsync()
        {
            int attempt = 0;
            return Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        return await _innerTransport.ReceiveAsync().ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (attempt++ < _options.MaxReconnectAttempts)
                    {
                        await Task.Delay(_options.ReceiveTimeout).ConfigureAwait(false);
                    }
                }
            });
        }

        public void Connect(IPEndPoint endpoint) => _innerTransport.Connect(endpoint);
        public void Close() => _innerTransport.Close();
    }
}
