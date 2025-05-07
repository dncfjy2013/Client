using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass
{
    #region 接口定义
    public interface IUdpTransport
    {
        Task<int> SendAsync(byte[] buffer, int bytes, IPEndPoint endpoint);
        Task<UdpReceiveResult> ReceiveAsync();
        void Connect(IPEndPoint endpoint);
        void Close();
    }

    public interface IMessageEncoder
    {
        byte[] Encode<T>(T message);
        T Decode<T>(byte[] buffer);
    }

    public interface IUdpPlugin
    {
        ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct);
        ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct);
    }
    #endregion

    #region 默认实现
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
    #endregion

    #region 核心客户端
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
        private async Task<byte[]> ProcessSendingPipeline(byte[] rawData, CancellationToken ct)
        {
            var data = rawData;
            foreach (var plugin in _plugins)
            {
                data = await plugin.OnSendingAsync(data, ct).ConfigureAwait(false);
            }
            return data;
        }

        private async Task<byte[]> ProcessReceivingPipeline(byte[] rawData, CancellationToken ct)
        {
            var data = rawData;
            foreach (var plugin in _plugins)
            {
                data = await plugin.OnReceivedAsync(data, ct).ConfigureAwait(false);
            }
            return data;
        }
        #endregion

        #region 公共方法
        public void RegisterPlugin(IUdpPlugin plugin) => _plugins.Add(plugin);

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

    #region 扩展配置
    public class UdpClientOptions
    {
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxReconnectAttempts { get; set; } = 3;
        public Encoding DefaultEncoding { get; set; } = Encoding.UTF8;
    }
    #endregion

    #region 扩展插件示例
    public class CompressionPlugin : IUdpPlugin
    {
        public ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct)
        {
            // 发送端：JSON编码 → 压缩 → Base64编码
            var compressed = Compress(data);
            return new ValueTask<byte[]>(compressed);
        }

        public ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct)
        {
            // 接收端：Base64解码 → 解压 → 返回原始数据
            var decompressed = Decompress(data);
            return new ValueTask<byte[]>(decompressed);
        }

        private byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gs = new GZipStream(ms, CompressionMode.Compress))
            {
                gs.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        private byte[] Decompress(byte[] compressed)
        {
            using var ms = new MemoryStream(compressed);
            using var gs = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gs.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

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
    #endregion

    #region 使用示例
    public class Date
    {
        public string message { get; set; }
        public int id { get; set; }
    }
    #endregion
}