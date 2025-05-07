using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Client.Core.UdpClientClass.Config;
using Client.Core.UdpClientClass.Interface;

namespace Client.Core.UdpClientClass
{
    #region 核心客户端
    /// <summary>
    /// UDP客户端实例，包含完整的消息处理管道
    /// </summary>
    public class UdpClientInstance : IDisposable
    {
        private readonly IUdpTransport _transport;
        private readonly IMessageEncoder _encoder;
        private readonly IPEndPoint _serverEndpoint;
        private readonly CancellationTokenSource _cts;
        private readonly List<IUdpPlugin> _plugins = new();
        private bool _disposed;

        public UdpClientOptions Options { get; }

        public UdpClientInstance(
            IUdpTransport transport,
            IMessageEncoder encoder,
            IPEndPoint endpoint,
            UdpClientOptions options = null,
            CancellationTokenSource cts = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _serverEndpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            Options = options ?? new UdpClientOptions();
            _cts = cts ?? new CancellationTokenSource();
        }

        #region 消息处理管道
        /// <summary>
        /// 发送消息处理管道，按顺序执行所有注册的发送插件
        /// </summary>
        private async Task<byte[]> ProcessSendingPipeline(byte[] rawData, CancellationToken ct)
        {
            var data = rawData;
            foreach (var plugin in _plugins)
            {
                data = await plugin.OnSendingAsync(data, ct).ConfigureAwait(false);
            }
            return data;
        }

        /// <summary>
        /// 接收消息处理管道，按逆序执行所有注册的接收插件
        /// </summary>
        private async Task<byte[]> ProcessReceivingPipeline(byte[] rawData, CancellationToken ct)
        {
            var data = rawData;
            foreach (var plugin in _plugins.AsEnumerable().Reverse())
            {
                data = await plugin.OnReceivedAsync(data, ct).ConfigureAwait(false);
            }
            return data;
        }
        #endregion

        #region 公共方法
        /// <summary>
        /// 注册消息处理插件（插件按注册顺序执行）
        /// </summary>
        public void RegisterPlugin(IUdpPlugin plugin) => _plugins.Add(plugin);

        /// <summary>
        /// 异步发送消息（支持取消）
        /// </summary>
        public async Task<bool> SendMessageAsync<T>(
            T message,
            CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cts.Token, cancellationToken);

            try
            {
                var data = _encoder.Encode(message);
                var processedData = await ProcessSendingPipeline(data, linkedCts.Token);
                int bytesSent = await _transport.SendAsync(
                    processedData, processedData.Length, _serverEndpoint)
                    .ConfigureAwait(false);

                return bytesSent > 0;
            }
            catch (OperationCanceledException) { /* 处理取消 */ }
            catch (SocketException ex) { /* 处理网络错误 */ }
            return false;
        }

        /// <summary>
        /// 异步接收消息（带超时控制）
        /// </summary>
        public async Task<(T Message, IPEndPoint Sender)> ReceiveMessageAsync<T>(
            CancellationToken cancellationToken = default)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cts.Token, cancellationToken);

            try
            {
                var receiveTask = _transport.ReceiveAsync();
                var delayTask = Task.Delay(Options.ReceiveTimeout, linkedCts.Token);

                Task completedTask = await Task.WhenAny(receiveTask, delayTask)
                    .ConfigureAwait(false);

                if (completedTask == receiveTask)
                {
                    var result = await receiveTask.ConfigureAwait(false);
                    var processedData = await ProcessReceivingPipeline(result.Buffer, linkedCts.Token);
                    var message = _encoder.Decode<T>(processedData);
                    return (message, result.RemoteEndPoint);
                }

                linkedCts.Token.ThrowIfCancellationRequested();
                return default;
            }
            catch (OperationCanceledException) { /* 处理取消 */ }
            catch (SocketException ex) { /* 处理网络错误 */ }
            return default;
        }

        public void Cancel() => _cts.Cancel();
        #endregion

        #region 资源管理
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    _transport?.Close();
                }
                _disposed = true;
            }
        }

        ~UdpClientInstance() => Dispose(false);
        #endregion
    }
    #endregion
}