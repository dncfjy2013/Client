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
        private readonly Dictionary<int, DependingMessage> _pendingMessages = new();
        private readonly Logger logger = Logger.GetInstance();
        private int _heartbeatCountout;

        // 初始化信号量（在构造函数中）
        private bool _skipNextHeartbeat = false;

        private const int WindowSize = 1000; // 窗口大小
        private readonly Queue<int> _sendWindow = new Queue<int>(); // 发送窗口队列
        private readonly Dictionary<int, CommunicationData> _receiveBuffer = new(); // 接收缓冲区
        private int _isHeartAck = 0;
        private CancellationTokenSource _receiveCts;
        private CancellationTokenSource _heartbeatCts;
        private const int HeartbeatIntervalMs = 3000;
        private const int AckTimeoutMs = 1000; // 原逻辑10*10ms=100ms，建议延长
        private const int MaxMissedHeartbeats = 3; // 原100次约5分钟，建议缩短
                                                   // 新增优先级序列号管理
        private readonly Dictionary<DataPriority, int> _prioritySequences = new()
        {
            { DataPriority.High, 0 },
            { DataPriority.Medium, 0 },
            { DataPriority.Low, 0 }
        };
        private readonly Dictionary<DataPriority, int> _priorityNextExpect = new()
        {
            { DataPriority.High, 1 },
            { DataPriority.Medium, 1 },
            { DataPriority.Low, 1 }
        };
        private readonly object _sequenceLock = new();
        // ACK接收事件
        private event Action<int> AckReceived;
        // 初始化配置（示例）
        ProtocolConfiguration config = new ProtocolConfiguration
        {
            DataSerializer = new JsonSerializerAdapter(),
            ChecksumCalculator = new Crc16Calculator(),
            SupportedVersions = new byte[] { 0x01, 0x02 },
            MaxPacketSize = 2 * 1024 * 1024 // 2MB
        };

        // 新增文件传输相关字段
        private readonly ConcurrentDictionary<string, FileTransferSession> _fileTransfers = new();
        private readonly SemaphoreSlim _fileTransferSemaphore = new(Environment.ProcessorCount * 2);
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
            StartCheckResends();
            _isConnected = true;
            _heartbeatCountout = 0;
        }
        // 新增重传检查方法
        private void StartCheckResends()
        {
            Task.Run(async () =>
            {
                while (_isConnected)
                {
                    await Task.Delay(1000); // 每秒检查一次

                    var now = DateTime.Now;
                    var toResend = _pendingMessages
                        .Where(p =>
                            p.Value.communicationData.Priority <= DataPriority.Medium && // 只重传高和中优先级
                            p.Value.RetryCount < GetMaxRetries(p.Value.communicationData.Priority) &&
                            (now - p.Value.FirstSentTime).TotalMilliseconds > GetRetryDelay(p.Value.RetryCount, p.Value.communicationData.Priority))
                        .ToList();

                    foreach (var item in toResend)
                    {
                        try
                        {
                            Console.WriteLine($"Resending {item.Value.communicationData.Priority} priority Seq={item.Key}");
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

        private int GetMaxRetries(DataPriority priority)
        {
            return priority switch
            {
                DataPriority.High => 5,    // 高优先级最多重试5次
                DataPriority.Medium => 3,  // 中优先级最多重试3次
                _ => 0                    // 低优先级不重试
            };
        }

        private double GetRetryDelay(int retryCount, DataPriority priority)
        {
            var baseDelay = priority switch
            {
                DataPriority.High => 500,  // 高优先级基础延迟500ms
                DataPriority.Medium => 1000, // 中优先级1s
                _ => 2000                // 低优先级2s（实际上不会用到）
            };

            return baseDelay * Math.Pow(2, retryCount); // 指数退避
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
                                ProcessReceivedData(data);
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

        // 接收处理增强
        private void ProcessReceivedData(CommunicationData data)
        {
            // 触发ACK事件
            if (data.AckNum > 0)
            {
                AckReceived?.Invoke(data.AckNum);
            }

            // 根据优先级处理数据
            switch (data.Priority)
            {
                case DataPriority.High:
                    ProcessHighPriorityData(data);
                    break;

                case DataPriority.Medium:
                    ProcessMediumPriorityData(data);
                    break;

                case DataPriority.Low:
                    ProcessLowPriorityData(data);
                    break;
            }
        }

        private void ProcessHighPriorityData(CommunicationData data)
        {
            // 严格顺序处理
            if (data.SeqNum == _priorityNextExpect[data.Priority]++)
            {
                Console.WriteLine($"Processing HIGH priority Seq={data.SeqNum}");
                DeliverData(data);

                // 处理缓冲的后续包
                while (_receiveBuffer.TryGetValue(_priorityNextExpect[data.Priority], out var bufferedData))
                {
                    Console.WriteLine($"Processing buffered HIGH priority Seq={_priorityNextExpect[data.Priority]}");
                    _receiveBuffer.Remove(_priorityNextExpect[data.Priority]);
                    _priorityNextExpect[data.Priority]++;
                    DeliverData(bufferedData);
                }
            }
            else if (data.SeqNum > _priorityNextExpect[data.Priority])
            {
                // 缓存乱序包
                _receiveBuffer[data.SeqNum] = data;
                Console.WriteLine($"Buffered HIGH priority Seq={data.SeqNum}");
            }
            // SeqNum < _nextExpectedSeq 的包视为重复包，忽略
        }

        private void ProcessMediumPriorityData(CommunicationData data)
        {
            // 中等优先级：允许有限乱序
            if (data.SeqNum >= _priorityNextExpect[data.Priority] - 10 &&
                data.SeqNum <= _priorityNextExpect[data.Priority] + 10)
            {
                Console.WriteLine($"Processing MEDIUM priority Seq={data.SeqNum}");
                DeliverData(data);
                _priorityNextExpect[data.Priority] = Math.Max(_priorityNextExpect[data.Priority], data.SeqNum + 1);
            }
        }

        private void ProcessLowPriorityData(CommunicationData data)
        {
            // 低优先级：直接处理，不保证顺序
            Console.WriteLine($"Processing LOW priority Seq={data.SeqNum}");
            DeliverData(data);
        }

        private void DeliverData(CommunicationData data)
        {
            // 实际数据处理逻辑
            Console.WriteLine($"Delivering: {data.Message}");

            // 发送ACK（对高和中优先级数据）
            if (data.Priority <= DataPriority.Medium)
            {
                _ = Task.Run(async () =>
                {
                    var ack = new CommunicationData
                    {
                        AckNum = data.SeqNum,
                        Priority = DataPriority.High // ACK使用高优先级
                    };
                    await SendRawData(ack);
                });
            }
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
                            Priority = DataPriority.High
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
            // 根据优先级分配序列号
            lock (_sequenceLock)
            {
                data.SeqNum = ++_prioritySequences[data.Priority];
            }

            // 窗口控制逻辑（仅对高优先级数据严格限制）
            if (data.Priority == DataPriority.High)
            {
                await WaitForWindowAvailability(data.SeqNum);
            }

            // 记录待确认消息（仅高和中优先级）
            if (data.Priority <= DataPriority.Medium)
            {
                _pendingMessages[data.SeqNum] = new DependingMessage
                {
                    communicationData = data,
                    FirstSentTime = DateTime.Now,
                    RetryCount = 0
                };
            }

            // 异步发送
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendRawData(data);
                    Console.WriteLine($"Sent: {data.InfoType}, Seq={data.SeqNum}, Pri={data.Priority}");

                    // 高优先级数据需要等待ACK或重试
                    if (data.Priority == DataPriority.High)
                    {
                        await WaitForAck(data.SeqNum);
                    }
                }
                catch (Exception ex)
                {
                    HandleSendFailure(ex, data);
                }
            });
        }

        private async Task WaitForWindowAvailability(int seqNum)
        {
            lock (_sendLock)
            {
                while (_sendWindow.Count >= WindowSize)
                {
                    if (!Monitor.Wait(_sendLock, TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Send window blocked for too long");
                    }
                }
                _sendWindow.Enqueue(seqNum);
            }
        }

        private async Task WaitForAck(int seqNum)
        {
            var timeout = Task.Delay(5000); // 5秒ACK超时
            var completionSource = new TaskCompletionSource<bool>();

            // 设置ACK到达回调
            Action<int> ackHandler = null;
            ackHandler = (ackedSeq) =>
            {
                if (ackedSeq == seqNum)
                {
                    _pendingMessages.Remove(seqNum);
                    completionSource.TrySetResult(true);
                    AckReceived -= ackHandler; // 移除事件处理
                }
            };

            AckReceived += ackHandler;

            // 等待ACK或超时
            var completedTask = await Task.WhenAny(completionSource.Task, timeout);
            if (completedTask == timeout)
            {
                AckReceived -= ackHandler;
                throw new TimeoutException($"ACK for seq {seqNum} not received");
            }
        }

        // 修改后的发送方法
        private async Task SendRawData(CommunicationData data)
        {
            var packet = new ProtocolPacket(config)
            {
                Header = new ProtocolHeader { Version = ProtocolHeader.CurrentVersion },
                Data = data
            };

            // 使用协议配置中的序列化器和校验和计算
            byte[] fullPacket = packet.ToBytes();
            await _clientSocket.SendAsync(fullPacket, SocketFlags.None);
        }

        // 修改 ReceiveData 方法
        public async Task<CommunicationData> ReceiveData(CancellationToken token)
        {
            var ack = await Task.Run(() => ReceiveRawData(token));
            return ack;
        }
        // 优化后的接收方法
        // 修改后的接收方法
        private async Task<CommunicationData> ReceiveRawData(CancellationToken token)
        {
            try
            {
                // 第一阶段：读取协议头（8字节）
                byte[] headerBuffer = new byte[8];
                int headerOffset = 0;
                while (headerOffset < headerBuffer.Length)
                {
                    int n = await _clientSocket.ReceiveAsync(
                        new ArraySegment<byte>(headerBuffer, headerOffset, headerBuffer.Length - headerOffset),
                        SocketFlags.None,
                        token);

                    if (n == 0) return null; // 连接正常关闭
                    headerOffset += n;
                }

                // 解析协议头并进行版本检查
                if (!ProtocolHeader.TryFromBytes(headerBuffer, out ProtocolHeader header) ||
                    !config.SupportedVersions.Contains(header.Version))
                {
                    Console.WriteLine("Invalid protocol header or unsupported version");
                    return null;
                }

                // 第二阶段：读取消息体（含校验和）
                byte[] payloadBuffer = new byte[header.MessageLength];
                int payloadOffset = 0;
                while (payloadOffset < header.MessageLength)
                {
                    int n = await _clientSocket.ReceiveAsync(
                        new ArraySegment<byte>(payloadBuffer, payloadOffset, header.MessageLength - payloadOffset),
                        SocketFlags.None,
                        token);

                    if (n == 0) return null; // 连接正常关闭
                    payloadOffset += n;
                }

                // 合并完整数据包并进行完整校验
                byte[] fullPacket = headerBuffer.Concat(payloadBuffer).ToArray();
                var parseResult = await ProtocolPacket.TryFromBytesAsync(fullPacket, config);

                return parseResult.Success ? parseResult.Packet.Data : null;
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

        // 进度回调事件
        public event Action<FileTransferProgress> OnFileTransferProgress;

        public async Task UploadFileAsync(string filePath, DataPriority priority = DataPriority.Medium)
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("File not found", filePath);

            var fileId = Guid.NewGuid().ToString();
            var chunkSize = CalculateChunkSize(fileInfo.Length);
            var totalChunks = (int)((fileInfo.Length + chunkSize - 1) / chunkSize);

            // 初始化传输会话
            var session = new FileTransferSession
            {
                FileId = fileId,
                FileName = fileInfo.Name,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                TotalChunks = totalChunks,
                ChunkSize = chunkSize,
                Priority = priority
            };

            _fileTransfers[fileId] = session;

            // 计算文件整体MD5（可以后台进行）
            _ = Task.Run(() => CalculateFileHashAsync(session));

            // 启动传输
            await StartFileTransfer(session);
        }

        private int CalculateChunkSize(long fileSize)
        {
            // 动态计算块大小，大文件使用更大的块
            if (fileSize > 10L * 1024 * 1024 * 1024) return 4 * 1024 * 1024;   // 10GB+文件用4MB块
            if (fileSize > 1L * 1024 * 1024 * 1024) return 1 * 1024 * 1024;    // 1GB+文件用1MB块
            return 256 * 1024;  // 小文件用256KB块
        }

        private async Task StartFileTransfer(FileTransferSession session)
        {
            UpdateProgress(session, TransferStatus.Preparing);

            try
            {
                using var fileStream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // 多线程分块读取和发送
                var tasks = new List<Task>();
                var chunkIndexes = Enumerable.Range(0, session.TotalChunks).ToList();

                foreach (var chunkIndex in chunkIndexes)
                {
                    // 控制并发数
                    await _fileTransferSemaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await SendFileChunk(fileStream, session, chunkIndex);
                        }
                        finally
                        {
                            _fileTransferSemaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                // 发送完成标记
                await SendTransferComplete(session);

                UpdateProgress(session, TransferStatus.Completed);
            }
            catch (Exception ex)
            {
                UpdateProgress(session, TransferStatus.Failed, ex.Message);
                throw;
            }
            finally
            {
                _fileTransfers.TryRemove(session.FileId, out _);
            }
        }

        private async Task SendFileChunk(FileStream fileStream, FileTransferSession session, int chunkIndex)
        {
            var chunkSize = chunkIndex == session.TotalChunks - 1
                ? (int)(session.FileSize - chunkIndex * session.ChunkSize)
                : session.ChunkSize;

            var buffer = new byte[chunkSize];
            fileStream.Seek(chunkIndex * session.ChunkSize, SeekOrigin.Begin);
            await fileStream.ReadAsync(buffer, 0, chunkSize);

            var chunkMd5 = CalculateChunkHash(buffer);

            var data = new CommunicationData
            {
                InfoType = InfoType.File,
                FileId = session.FileId,
                FileName = session.FileName,
                FileSize = session.FileSize,
                ChunkIndex = chunkIndex,
                TotalChunks = session.TotalChunks,
                ChunkData = buffer,
                ChunkMD5 = chunkMd5,
                Priority = session.Priority
            };

            await SendData(data);

            Interlocked.Add(ref session.TransferredBytes, chunkSize);
            UpdateProgress(session, TransferStatus.Transferring);
        }

        private async Task SendTransferComplete(FileTransferSession session)
        {
            var completeData = new CommunicationData
            {
                InfoType = InfoType.File,
                FileId = session.FileId,
                Message = "FILE_COMPLETE",
                MD5Hash = session.FileHash,
                Priority = DataPriority.High // 完成通知用高优先级
            };

            await SendData(completeData);
        }

        private void UpdateProgress(FileTransferSession session, TransferStatus status, string error = null)
        {
            OnFileTransferProgress?.Invoke(new FileTransferProgress
            {
                FileId = session.FileId,
                FileName = session.FileName,
                TotalBytes = session.FileSize,
                TransferredBytes = session.TransferredBytes,
                Status = status
            });
        }

        private async Task CalculateFileHashAsync(FileTransferSession session)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(session.FilePath);

            session.FileHash = BitConverter.ToString(await md5.ComputeHashAsync(stream))
                .Replace("-", "").ToLowerInvariant();
        }

        private string CalculateChunkHash(byte[] data)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(data))
                .Replace("-", "").ToLowerInvariant();
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
