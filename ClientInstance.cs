using Client.Common;
using Client.Common.Log;
using Client.utils;
using Microsoft.VisualBasic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Client
{
    // ClientInstance.cs - 客户端类
    public class ClientInstance
    {
        private readonly string _serverIp;
        private readonly int _port;
        private Socket _clientSocket;
        private bool _isConnected = false;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private int _currentSeq = 0; // 当前发送序列号
        private readonly Dictionary<int, Date> _pendingMessages = new();
        private readonly Logger logger = Logger.GetInstance();
        private int _heartbeatCountout;
        // 新增文件传输相关字段
        private readonly ConcurrentDictionary<string, FileTransferState> _activeTransfers = new();
        private readonly object _transferLock = new object();
        // 初始化信号量（在构造函数中）
        private readonly SemaphoreSlim _transferSemaphore = new SemaphoreSlim(1, 1);
        private bool _skipNextHeartbeat = false;

        private const int WindowSize = 1000; // 窗口大小
        private readonly Queue<int> _sendWindow = new Queue<int>(); // 发送窗口队列
        private int _nextExpectedSeq = 1; // 下一个期望接收的序列号
        private readonly Dictionary<int, CommunicationData> _receiveBuffer = new(); // 接收缓冲区
        private int _isHeartAck = 0;
        private CancellationTokenSource _receiveCts;
        private CancellationTokenSource _heartbeatCts;
        private const int HeartbeatIntervalMs = 3000;
        private const int AckTimeoutMs = 1000; // 原逻辑10*10ms=100ms，建议延长
        private const int MaxMissedHeartbeats = 3; // 原100次约5分钟，建议缩短

        public ClientInstance(string serverIp, int port)
        {
            _serverIp = serverIp;
            _port = port;
        }

        public async Task Connect()
        {
            while (true)
            {
                try
                {
                    _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await _clientSocket.ConnectAsync(new IPEndPoint(IPAddress.Parse(_serverIp), _port));
                    Console.WriteLine($"Connected to server at {_serverIp}:{_port}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"server not at {_serverIp}:{_port}");
                }
            }
            // 连接成功后启动心跳
            StartHeartbeat();
            StartReceiveProcessing();
            //StartCheckResends();
            _isConnected = true;
            _heartbeatCountout = 0;
        }
        // 新增重传检查方法
        private void StartCheckResends()
        {
            Task.Run(async () =>
            {
                while (!_isConnected)
                {
                    await Task.Delay(1000);

                    const int MaxRetries = 5;
                    var now = DateTime.Now;
                    var toResend = _pendingMessages
                        .Where(p => p.Value.RetryCount < MaxRetries &&
                                   (now - p.Value.FirstSentTime).TotalMilliseconds >
                                   Math.Pow(2, p.Value.RetryCount) * 1000 && p.Value.communicationData.SeqNum > _nextExpectedSeq)
                        .ToList();

                    foreach (var item in toResend)
                    {
                        try
                        {
                            Console.WriteLine($"Resending SeqNum={item.Key}");
                            await SendRawData(item.Value.communicationData);
                            item.Value.RetryCount++;
                        }
                        catch (Exception ex)
                        {
                            HandleSendFailure(ex, item.Value.communicationData);
                        }
                    }
                }
            });
        }

        private void StartReceiveProcessing()
        {
            _receiveCts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try
                {
                    while (!_receiveCts.IsCancellationRequested)
                    {
                        var data = await ReceiveData(_receiveCts.Token);
                        if (data != null)
                        {
                            Interlocked.Exchange(ref _isHeartAck, 1); // 原子操作
                            try
                            {
                                await ProcessReceivedData(data);
                            }
                            catch (Exception ex)
                            {
                                logger.LogTemp(LogLevel.Error, "Data processing failed");
                            }
                        }
                        await Task.Delay(100, _receiveCts.Token);
                    }
                }
                catch (OperationCanceledException) { /* 正常取消 */ }
                catch (Exception ex)
                {
                    logger.LogTemp(LogLevel.Error, "Receive loop terminated unexpectedly");
                    Disconnect();
                }
            }, _receiveCts.Token);
        }

        private void StartHeartbeat()
        {
            _heartbeatCts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try
                {
                    while (!_heartbeatCts.IsCancellationRequested)
                    {
                        await Task.Delay(HeartbeatIntervalMs, _heartbeatCts.Token);

                        if (_skipNextHeartbeat)
                        {
                            _skipNextHeartbeat = false;
                            continue;
                        }

                        var heartbeatData = new CommunicationData
                        {
                            Message = "Heartbeat",
                            InfoType = InfoType.HeartBeat,
                        };

                        await SendData(heartbeatData);
                        Interlocked.Exchange(ref _isHeartAck, 0);

                        var ackTimeout = Task.Delay(AckTimeoutMs, _heartbeatCts.Token);
                        Task ackReceived = await Task.WhenAny(
                            Task.Run(() => _isHeartAck == 1), // 等待确认标志
                            ackTimeout
                        );

                        if (!ackReceived.IsCompleted)
                        {
                            Interlocked.Increment(ref _heartbeatCountout);
                            if (_heartbeatCountout >= MaxMissedHeartbeats)
                            {
                                Disconnect();
                                _heartbeatCts.Cancel();
                            }
                        }
                        else
                        {
                            Interlocked.Exchange(ref _heartbeatCountout, 0);
                        }
                    }
                }
                catch (OperationCanceledException) { /* 正常取消 */ }
                catch (Exception ex)
                {
                    Disconnect();
                    _heartbeatCts.Cancel();
                }
            }, _heartbeatCts.Token);
        }

        public async Task SendData(CommunicationData data)
        {
            // 窗口控制逻辑
            lock (_sendLock)
            {
                while (_sendWindow.Count >= WindowSize)
                {
                    // 添加超时机制
                    if (!Monitor.Wait(_sendLock, TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Send window blocked");
                    }
                }

                data.SeqNum = Interlocked.Increment(ref _currentSeq);
                _pendingMessages[data.SeqNum] = new Date
                {
                    communicationData = data,
                    FirstSentTime = DateTime.Now,
                    RetryCount = 0
                };

                _sendWindow.Enqueue(data.SeqNum);
            }

            // 异步发送（无需等待ACK即可继续发送窗口内数据）
            _ = Task.Run(async () =>
            {
                await SendRawData(data);
                Console.WriteLine($"Sent: {data.InfoType}, Seq={data.SeqNum}");
            });
        }
        private async Task SendRawData(CommunicationData data)
        {
            // 统一序列化发送逻辑
            var json = JsonSerializer.Serialize(data);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);

            byte[] fullPacket = new byte[lengthPrefix.Length + payload.Length];
            Buffer.BlockCopy(lengthPrefix, 0, fullPacket, 0, lengthPrefix.Length);
            Buffer.BlockCopy(payload, 0, fullPacket, lengthPrefix.Length, payload.Length);

            await _clientSocket.SendAsync(fullPacket, SocketFlags.None);
        }
        private async Task ProcessReceivedData(CommunicationData data)
        {
            // 处理乱序包
            if (data.SeqNum == _nextExpectedSeq)
            {
                // 按序到达：提交数据并移动接收窗口
                Console.WriteLine($"Received Seq={data.SeqNum}");
                _receiveBuffer.Remove(data.SeqNum);
                _nextExpectedSeq++;
                _pendingMessages.Remove(data.SeqNum);
                // 触发窗口滑动
                lock (_sendLock)
                {
                    while (_sendWindow.Count > 0 &&
                           _sendWindow.Peek() < _nextExpectedSeq)
                    {
                        _sendWindow.Dequeue();
                        Monitor.Pulse(_sendLock); // 通知发送线程窗口可用
                    }
                }
            }
            if (data.SeqNum > _nextExpectedSeq)
            {
                // 乱序包暂存
                _receiveBuffer[data.SeqNum] = data;
                Console.WriteLine($"Buffered Seq={data.SeqNum}");
            }
        }
        // 修改 ReceiveData 方法
        public async Task<CommunicationData> ReceiveData(CancellationToken token)
        {
            var ack = await Task.Run(() => ReceiveRawData(token));
            return ack;
        }
        private async Task<CommunicationData> ReceiveRawData(CancellationToken token)
        {
            try
            {
                byte[] header = new byte[4];
                await _clientSocket.ReceiveAsync(new ArraySegment<byte>(header), SocketFlags.None, token);
                int msgLength = BitConverter.ToInt32(header, 0);

                byte[] dataBuffer = new byte[msgLength];
                await _clientSocket.ReceiveAsync(new ArraySegment<byte>(dataBuffer), SocketFlags.None, token);

                var data = JsonSerializer.Deserialize<CommunicationData>(Encoding.UTF8.GetString(dataBuffer));

                return data;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Receive operation timed out");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                Console.WriteLine($"Connection reset by server: {ex.Message}");
                Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receive error: {ex.Message}");
            }
            return null;
        }
        // 新增异常处理统一方法
        private void HandleSendFailure(Exception ex, CommunicationData data)
        {
            Console.WriteLine($"Error sending SeqNum={data.SeqNum}: {ex.Message}");
            _pendingMessages.Remove(data.SeqNum);

            // 检查连接状态
            if (!_isConnected) return;

            // 尝试重新连接
            try
            {
                Disconnect();
                Connect().Wait();
                Console.WriteLine("Reconnected to server");
            }
            catch (Exception reconnectEx)
            {
                Console.WriteLine($"Reconnect failed: {reconnectEx.Message}");
            }
        }

        public async Task SendFile(string filePath, string fileId, Action<int> progressCallback = null)
        {
            var fileInfo = new FileInfo(filePath);
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] checksum = md5.ComputeHash(stream);
                stream.Position = 0;

                var transferInfo = new FileTransferState
                {
                    TotalChunks = (int)Math.Ceiling(fileInfo.Length / (double)Constants.ChunkSize),
                    FileName = Path.GetFileName(filePath),
                    MD5Hash = BitConverter.ToString(checksum).Replace("-", ""),
                    FileId = fileId,
                    SentChunks = new HashSet<int>(),
                    ProgressCallback = progressCallback
                };

                lock (_transferLock)
                {
                    _activeTransfers[fileId] = transferInfo;
                }

                try
                {
                    for (int i = 0; i < transferInfo.TotalChunks; i++)
                    {
                        if (_activeTransfers.TryGetValue(fileId, out var state) && state.Cancelled)
                        {
                            break;
                        }

                        byte[] chunkData = new byte[Constants.ChunkSize];
                        int bytesRead = await stream.ReadAsync(chunkData, 0, Constants.ChunkSize);
                        Array.Resize(ref chunkData, bytesRead);

                        var chunk = new FileChunk { Index = i, Data = chunkData };
                        var message = new CommunicationData
                        {
                            InfoType = InfoType.File,
                            FileId = fileId,
                            FileName = transferInfo.FileName,
                            TotalChunks = transferInfo.TotalChunks,
                            MD5Hash = transferInfo.MD5Hash,
                            FileChunks = new List<FileChunk> { chunk }
                        };

                        await SendRawData(message);
                        transferInfo.SentChunks.Add(i);
                        progressCallback?.Invoke((int)((i + 1) * 100 / transferInfo.TotalChunks));

                        // 等待ACK
                        var ack = await ReceiveFileAck(TimeSpan.FromSeconds(30));
                        HandleFileAck(ack, transferInfo);
                    }

                    // 发送完成标记
                    var completionMessage = new CommunicationData
                    {
                        InfoType = InfoType.File,
                        FileId = fileId,
                        Message = "FILE_COMPLETE"
                    };
                    await SendRawData(completionMessage);
                    await ReceiveFileAck(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    lock (_transferLock)
                    {
                        _activeTransfers.TryRemove(fileId, out _);
                    }
                }
            }
        }

        private async Task<CommunicationData> ReceiveFileAck(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                try
                {
                    var ack = await ReceiveRawData(cts.Token);
                    if (ack == null)
                    {
                        await ResendMissingChunks();
                    }
                    if (ack.Message == "FILE_ACK" || ack.Message == "FILE_COMPLETE_ACK") return ack;
                }
                catch (TimeoutException)
                {
                    // 实现重传逻辑
                    await ResendMissingChunks();
                }
            }
        }

        private void HandleFileAck(CommunicationData ack, FileTransferState transferInfo)
        {
            if (ack.ReceivedChunks != null)
            {
                lock (_transferLock)
                {
                    foreach (var chunk in ack.ReceivedChunks)
                    {
                        transferInfo.SentChunks.Add(chunk);
                    }
                }
            }
        }

        private async Task ResendMissingChunks()
        {
            await _transferSemaphore.WaitAsync();
            try
            {
                foreach (var transfer in _activeTransfers.Values.ToList())
                {
                    var missingChunks = Enumerable.Range(0, transfer.TotalChunks)
                        .Where(i => !transfer.SentChunks.Contains(i))
                        .ToList();

                    using (var fs = File.OpenRead(transfer.FilePath))
                    {
                        foreach (var chunkIndex in missingChunks)
                        {
                            fs.Position = chunkIndex * Constants.ChunkSize;
                            byte[] buffer = new byte[Constants.ChunkSize];
                            int bytesRead = await fs.ReadAsync(buffer, 0, Constants.ChunkSize);
                            var chunk = new FileChunk
                            {
                                Index = chunkIndex,
                                Data = buffer.Take(bytesRead).ToArray()
                            };

                            var message = new CommunicationData
                            {
                                InfoType = InfoType.File,
                                FileId = transfer.FileId,
                                FileChunks = new List<FileChunk> { chunk }
                            };
                            await SendRawData(message);
                        }
                    }
                }
            }
            finally
            {
                _transferSemaphore.Release();
            }
        }

        public void Disconnect()
        {
            _isConnected = false;

            // 清除所有待处理消息
            _pendingMessages.Clear();
            _receiveCts?.Cancel();
            _heartbeatCts?.Cancel();
            // 安全关闭socket
            try
            {
                _clientSocket?.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException) { /* 忽略关闭异常 */ }
            finally
            {
                _clientSocket?.Dispose();
                _clientSocket?.Close();
            }

            Console.WriteLine("Connection closed gracefully");
        }
    }
}
