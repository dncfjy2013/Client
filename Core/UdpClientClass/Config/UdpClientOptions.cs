using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Config
{
    /// <summary>
    /// UDP客户端配置选项
    /// </summary>
    public class UdpClientOptions
    {
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxReconnectAttempts { get; set; } = 3;
    }
}
