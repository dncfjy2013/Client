using Client.Core.UdpClientClass.Config;
using Client.Core.UdpClientClass.Interface;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.DateRetry
{
    /// <summary>
    /// 自动重试传输层（增强版）
    /// </summary>
    public class RetryTransport : IUdpTransport
    {
        private readonly IUdpTransport _innerTransport;
        private readonly UdpClientOptions _options;
        private readonly SemaphoreSlim _retryLock = new(1, 1); // 初始化信号量

        public RetryTransport(IUdpTransport innerTransport, UdpClientOptions options)
        {
            _innerTransport = innerTransport ?? throw new ArgumentNullException(nameof(innerTransport));
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (_options.MaxReconnectAttempts < 0)
                throw new ArgumentOutOfRangeException(nameof(_options.MaxReconnectAttempts), "最大重试次数不能为负数");
        }

        public async Task<int> SendAsync(byte[] buffer, int bytes, IPEndPoint endpoint)
        {
            // 使用正确的 WaitAsync 方法
            await _retryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ExecuteWithRetryAsync(
                    () => _innerTransport.SendAsync(buffer, bytes, endpoint),
                    _options.SendTimeout,
                    _options.MaxReconnectAttempts,
                    "发送数据");
            }
            finally
            {
                _retryLock.Release();
            }
        }

        public async Task<UdpReceiveResult> ReceiveAsync()
        {
            // 使用正确的 WaitAsync 方法
            await _retryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ExecuteWithRetryAsync(
                    () => _innerTransport.ReceiveAsync(),
                    _options.ReceiveTimeout,
                    _options.MaxReconnectAttempts,
                    "接收数据");
            }
            finally
            {
                _retryLock.Release();
            }
        }

        public void Connect(IPEndPoint endpoint) => _innerTransport.Connect(endpoint);
        public void Close() => _innerTransport.Close();

        private async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            TimeSpan retryInterval,
            int maxRetries,
            string operationName)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await action().ConfigureAwait(false);
                }
                catch (SocketException ex) when (attempt < maxRetries)
                {
                    // 实际使用时建议添加日志记录
                    // _logger?.LogWarning(ex, "{Operation} 失败，剩余重试次数：{Remaining}", operationName, maxRetries - attempt);

                    await Task.Delay(retryInterval).ConfigureAwait(false);
                    attempt++;
                }
                catch (ObjectDisposedException ex) when (attempt == 0)
                {
                    throw new InvalidOperationException("传输层已关闭", ex);
                }
            }
        }
    }
}