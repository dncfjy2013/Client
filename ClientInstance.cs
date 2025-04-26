using Client.Common;
using Client.Common.Log;
using Client.utils;
using Google.Protobuf;
using Microsoft.VisualBasic;
using Protocol;
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

        private readonly Logger logger = new Logger();
        private int _heartbeatCountout;

        // 初始化信号量（在构造函数中）
        private bool _skipNextHeartbeat = false;
        private int highUsed = 0;
        private int mediumUsed = 0;
        private int lowUsed = 0;
        private const float HighBaseRatio = 0.8f;    // 高优先级基准比例
        private const float LowMinRatio = 0.05f;    // 低优先级最低保留比例
        private const float LowMaxRatio = 0.10f;    // 低优先级最高比例
        private const int BufferReserve = 10;       // 动态调整缓冲区
        private readonly object _windowLock = new object();
        private const int WindowSize = 1000000; // 窗口大小
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
        public readonly ConcurrentDictionary<DataPriority, RetryConfig> _retryConfigs = new()
        {
            [DataPriority.High] = new RetryConfig
            {
                MaxRetries = 5,
                BaseDelayMs = 300,
                BackoffFactor = 1.5,
                PriorityWeight = 1.0f
            },
            [DataPriority.Medium] = new RetryConfig
            {
                MaxRetries = 3,
                BaseDelayMs = 500,
                BackoffFactor = 2.0,
                PriorityWeight = 0.7f
            },
            [DataPriority.Low] = new RetryConfig
            {
                MaxRetries = 1,
                BaseDelayMs = 1000,
                BackoffFactor = 3.0,
                PriorityWeight = 0.3f
            }
        };
        private readonly ConcurrentDictionary<DataPriority, ConcurrentDictionary<int, PendingMessage>> _priorityPendingMessages = new()
        {
            [DataPriority.High] = new(),
            [DataPriority.Medium] = new(),
            [DataPriority.Low] = new()
        };
        private readonly object _sequenceLock = new();
        // 新增字段记录已处理的中等优先级序列号
        private readonly SortedSet<int> _processedMediumSeq = new();
        private readonly SortedDictionary<int, CommunicationData> _mediumBuffer = new();
        private readonly HashSet<int> _processedMediumSeqs = new();
        private int _mediumWindowSize = 20; // 初始窗口大小
        private DateTime _lastWindowAdjustTime = DateTime.Now;

        private readonly object _processedHighLock = new();
        private readonly object _processedMediumLock = new();
        private readonly object _processedLowLock = new();
        // ACK接收事件
        private event Action<int> AckReceived;
        // 初始化配置（示例）
        ProtocolConfiguration config = new ProtocolConfiguration
        {
            DataSerializer = new ProtobufSerializerAdapter(),
            ChecksumCalculator = new Crc16Calculator(),
            SupportedVersions = new byte[] { 0x01, 0x02 },
            MaxPacketSize = 128 * 1024 * 1024 // 128MB
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
                    logger.LogInformation($"Connected to server at {_serverIp}:{_port}");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"server not at {_serverIp}:{_port}");
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
                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                while (await timer.WaitForNextTickAsync() && _isConnected)
                {
                    logger.LogInformation("Start CheckResend ......");
                    await ProcessRetriesByPriority(DataPriority.High);
                    await ProcessRetriesByPriority(DataPriority.Medium);
                    await ProcessRetriesByPriority(DataPriority.Low);
                }
            });
        }
        public bool TryRemovePendingMessage(DataPriority priority, int seqNumber)
        {
            // 1. 检查是否存在该优先级的队列
            if (!_priorityPendingMessages.TryGetValue(priority, out var priorityQueue))
            {
                logger.LogDebug($"Try Get {priority} priorityQueue failure");
                return false;
            }

            // 2. 尝试从队列中移除指定键
            bool r = priorityQueue.TryRemove(seqNumber, out _);
            if (!r)
            {
                logger.LogDebug($"Try Get seqNumber from {priority} Queue failure");
            }

            return r;
        }
        private async Task ProcessRetriesByPriority(DataPriority priority)
        {
            if (!_priorityPendingMessages.TryGetValue(priority, out var messages) || messages.IsEmpty)
            {
                if (!messages.IsEmpty)
                {
                    logger.LogDebug($"Try Get seqNumber from {priority} PendingMessages Queue failure");
                }
                return; 
            }

            var config = _retryConfigs[priority];
            var now = DateTime.UtcNow;

            var expiredMessages = messages
                .Where(kvp =>
                {
                    var msg = kvp.Value;
                    var delay = config.BaseDelayMs * Math.Pow(config.BackoffFactor, msg.RetryCount);
                    return msg.RetryCount < config.MaxRetries &&
                           (now - msg.LastSent).TotalMilliseconds > delay;
                })
                .OrderByDescending(kvp => kvp.Value.PriorityWeight)
                .Take(GetMaxParallelRetries(priority))
                .ToList();

            var retryTasks = expiredMessages.Select(async kvp =>
            {
                var (seq, msg) = (kvp.Key, kvp.Value);
                try
                {
                    msg.RetryCount++;
                    msg.LastSent = now;

                    await SendRawData(msg.Data);
                    logger.LogInformation($"Resent {priority} seq={seq}, retry={msg.RetryCount}");
                }
                catch (Exception ex)
                {
                    logger.LogError($"Resent {priority} seq={seq}, retry={msg.RetryCount} failure");
                    HandleRetryFailure(priority, seq, ex);
                }
            });

            await Task.WhenAll(retryTasks);
        }
        // 增强的错误处理
        private void HandleRetryFailure(DataPriority priority, int seq, Exception ex)
        {
            if (_priorityPendingMessages[priority].TryGetValue(seq, out var msg))
            {
                if (msg.RetryCount >= _retryConfigs[priority].MaxRetries)
                {
                    _priorityPendingMessages[priority].TryRemove(seq, out _);
                    logger.LogError($"Final retry failed for {priority} seq={seq}: {ex.Message}");

                    if (priority == DataPriority.High)
                        HandleCriticalFailure(ex);
                }
                else
                {
                    logger.LogWarning($"Retry failed for {priority} seq={seq}: {ex.Message}");
                }
            }
        }

        private void HandleCriticalFailure(Exception ex)
        {
            // 高优先级消息连续失败处理
            Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (!_isConnected) return;

                logger.LogCritical("Critical message delivery failed, initiating reconnect...");
                await ReconnectAsync();
            });
        }

        private async Task ReconnectAsync()
        {
            logger.LogCritical("Reconnect......");
            Disconnect();
            await Connect();
        }
        private int GetMaxParallelRetries(DataPriority priority)
        {
            return priority switch
            {
                DataPriority.High => Environment.ProcessorCount * 2,
                DataPriority.Medium => Environment.ProcessorCount,
                _ => Math.Max(1, Environment.ProcessorCount / 2)
            };
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
                                logger.LogDebug($"Receive data {data.Priority} {data.SeqNum}, waiting process");
                                ProcessReceivedData(data);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError("Data processing failed");
                            }
                        }
                        await Task.Delay(100, _receiveCts.Token);
                    }
                }
                catch (OperationCanceledException) { /* 正常取消 */ }
                catch (Exception ex)
                {
                    logger.LogError("Receive loop terminated unexpectedly");
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
            lock (_processedHighLock)
            {
                if(!TryRemovePendingMessage(DataPriority.High, data.SeqNum))
                {
                    logger.LogTrace($"PendingMessage HIGH priority Seq={data.SeqNum}");
                }
                // 严格顺序处理
                if (data.SeqNum == _priorityNextExpect[data.Priority]++)
                {
                    logger.LogInformation($"Processing HIGH priority Seq={data.SeqNum}");
                    DeliverData(data);
                    // 处理缓冲的后续包
                    while (_receiveBuffer.TryGetValue(_priorityNextExpect[data.Priority], out var bufferedData))
                    {
                        if (bufferedData.SeqNum == _priorityNextExpect[data.Priority] + 1)
                        {
                            logger.LogInformation($"Processing buffered HIGH priority Seq={_priorityNextExpect[data.Priority]}");
                            _receiveBuffer.Remove(_priorityNextExpect[data.Priority]);
                            _priorityNextExpect[data.Priority]++;
                            DeliverData(bufferedData);
                        }
                    }
                }
                else if (data.SeqNum > _priorityNextExpect[data.Priority])
                {
                    // 缓存乱序包
                    _receiveBuffer[data.SeqNum] = data;
                    logger.LogInformation($"Buffered HIGH priority Seq={data.SeqNum}");
                }
                // SeqNum < _nextExpectedSeq 的包视为重复包，忽略
            }
        }

        private void ProcessMediumPriorityData(CommunicationData data)
        {
            lock (_processedMediumLock)
            {
                TryRemovePendingMessage(DataPriority.Medium, data.SeqNum);
                // 1. 重复数据包检查
                if (_processedMediumSeqs.Contains(data.SeqNum))
                {
                    logger.LogInformation($"Duplicate MEDIUM seq={data.SeqNum}");
                    return;
                }

                // 2. 立即发送ACK（无论是否处理）
                SendMediumAck(data.SeqNum);

                // 3. 动态窗口范围计算
                int expected = _priorityNextExpect[DataPriority.Medium];
                int windowStart = expected;
                int windowEnd = expected + _mediumWindowSize;

                // 4. 判断是否在动态窗口内
                if (data.SeqNum >= windowStart && data.SeqNum <= windowEnd)
                {
                    ProcessInWindow(data, ref expected);
                }
                else
                {
                    // 5. 超出窗口则缓冲管理
                    if (!_mediumBuffer.ContainsKey(data.SeqNum))
                    {
                        _mediumBuffer.Add(data.SeqNum, data);
                        logger.LogInformation($"Buffered MEDIUM seq={data.SeqNum} (window:{windowStart}-{windowEnd})");
                    }
                }

                // 6. 动态调整窗口大小
                AdjustWindowSize();
            }
        
        }
        private void ProcessInWindow(CommunicationData data, ref int expected)
        {
            // 标记为已处理
            _processedMediumSeqs.Add(data.SeqNum);

            // 处理当前数据包
            DeliverData(data);
            logger.LogInformation($"Processed MEDIUM seq={data.SeqNum}");

            // 更新期望值：找到最大的连续序列号
            while (_processedMediumSeqs.Contains(expected))
            {
                expected++;
            }
            _priorityNextExpect[DataPriority.Medium] = expected;

            // 处理缓冲区内可处理的数据包
            ProcessBufferedData(ref expected);
        }

        private void ProcessBufferedData(ref int expected)
        {
            // 从缓冲区提取连续序列号
            while (_mediumBuffer.TryGetValue(expected, out var bufferedData))
            {
                DeliverData(bufferedData);
                _mediumBuffer.Remove(expected);
                _processedMediumSeqs.Add(expected);
                expected++;
                logger.LogInformation($"Process buffered MEDIUM seq={expected - 1}");
            }
            _priorityNextExpect[DataPriority.Medium] = expected;
        }

        private void AdjustWindowSize()
        {
            // 每5秒调整窗口大小
            if ((DateTime.Now - _lastWindowAdjustTime).TotalSeconds < 5) return;

            // 根据缓冲区积压情况动态调整
            int bufferSize = _mediumBuffer.Count;
            if (bufferSize > 50)
            {
                _mediumWindowSize = Math.Min(100, _mediumWindowSize + 10); // 扩大窗口
            }
            else if (bufferSize < 10 && _mediumWindowSize > 20)
            {
                _mediumWindowSize = Math.Max(20, _mediumWindowSize - 5); // 收缩窗口
            }

            // 根据处理延迟调整
            var processingRate = CalculateProcessingRate();
            _mediumWindowSize = processingRate switch
            {
                > 100 => _mediumWindowSize + 5,  // 高吞吐量时扩大窗口
                < 50 => Math.Max(10, _mediumWindowSize - 3), // 低吞吐量时收缩
                _ => _mediumWindowSize
            };

            logger.LogInformation($"Adjusted window size to {_mediumWindowSize}");
            _lastWindowAdjustTime = DateTime.Now;
        }

        private double CalculateProcessingRate()
        {
            // 计算最近10秒的处理速率（包/秒）
            var processedCount = _processedMediumSeqs.Count(s => s > _priorityNextExpect[DataPriority.Medium] - 100);
            return processedCount / 10.0;
        }

        private async void SendMediumAck(int seqNum)
        {
            try
            {
                var ack = new CommunicationData
                {
                    AckNum = seqNum,
                    Priority = DataPriority.High,
                };
                await SendRawData(ack);
            }
            catch (Exception ex)
            {
                logger.LogError($"MEDIUM ACK发送失败 seq={seqNum}: {ex.Message}");
                // 失败重试机制
                _ = Task.Delay(100).ContinueWith(_ => SendMediumAck(seqNum));
            }
        }

        private void ProcessLowPriorityData(CommunicationData data)
        {
            lock (_processedLowLock)
            {
                // 低优先级：直接处理，不保证顺序
                logger.LogInformation($"Processing LOW priority Seq={data.SeqNum}");
                DeliverData(data);
            }
        }

        private void DeliverData(CommunicationData data)
        {
            // 实际数据处理逻辑
            logger.LogInformation($"Delivering: {data.Message}");

            // 发送ACK（对高和中优先级数据）
            if (data.Priority <= DataPriority.Medium)
            {
                _ = Task.Run(async () =>
                {
                    var ack = new CommunicationData
                    {
                        InfoType = InfoType.Ack,
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
                            logger.LogDebug("Skip Heartbeat");
                            _skipNextHeartbeat = false;
                            continue;
                        }

                        logger.LogDebug("Start Heartbeat");

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
                            logger.LogWarning($"Heartbeat is not complete, Current missed: {_heartbeatCountout}");

                            if (_heartbeatCountout >= MaxMissedHeartbeats)
                            {
                                logger.LogCritical("Heartbeat larger than MaxMissedHeartbeats");
                                await ReconnectAsync();
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
                    logger.LogCritical($"Heartbeat error, {ex.Message}");
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
            await WaitForWindowAvailability(data.Priority);

            // 记录待确认消息（仅高和中优先级）
            if (data.Priority <= DataPriority.Medium)
            {
                var config = _retryConfigs[data.Priority];
                var pending = new PendingMessage
                {
                    Data = data,
                    FirstSent = DateTime.UtcNow,
                    LastSent = DateTime.UtcNow,
                    PriorityWeight = config.PriorityWeight
                };

                _priorityPendingMessages[data.Priority].TryAdd(data.SeqNum, pending);
            }

            // 异步发送
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendRawData(data);
                    logger.LogInformation($"Sent: {data.InfoType}, Seq={data.SeqNum}, Pri={data.Priority}");

                    // 高优先级数据需要等待ACK或重试
                    if (data.Priority == DataPriority.High)
                    {
                        await WaitForAck(data.SeqNum, data.Priority);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Sent: {data.InfoType}, Seq={data.SeqNum}, Pri={data.Priority}");
                    HandleSendFailure(ex, data);
                }
            });
        }

        private async Task WaitForWindowAvailability(DataPriority priority)
        {
            lock (_windowLock)
            {
                while (true)
                {
                    // 计算当前窗口状态
                    int totalUsed = highUsed + mediumUsed + lowUsed;
                    int windowRemain = WindowSize - totalUsed;

                    // 动态计算低优先级配额
                    int lowMax = Math.Min(
                        (int)(WindowSize * LowMaxRatio),
                        Math.Max(
                            (int)(WindowSize * LowMinRatio),
                            lowUsed + windowRemain
                        )
                    );

                    // 高优先级动态调整因子
                    float highUsageFactor = highUsed / (float)(WindowSize * HighBaseRatio);

                    // 中优先级动态配额
                    int mediumMax = (int)(WindowSize * (0.15f + 0.65f * (1 - highUsageFactor)));
                    mediumMax = Math.Clamp(mediumMax, 0, WindowSize - (int)(WindowSize * LowMinRatio));

                    switch (priority)
                    {
                        case DataPriority.High:
                            // 高优先级动态上限
                            int highDynamicMax = (int)(WindowSize * HighBaseRatio +
                                (WindowSize * 0.2f * (1 - highUsageFactor)));
                            logger.LogTrace($"Currrent highDynamicMax: {highDynamicMax}, used {highUsed}");
                            if (highUsed < highDynamicMax && windowRemain > 0)
                            {
                                highUsed++;
                                return;
                            }
                            break;

                        case DataPriority.Medium:
                            // 中优先级可用空间 = 总剩余 - 低预留 - 缓冲区
                            int mediumAvailable = WindowSize - highUsed - lowMax - BufferReserve;
                            logger.LogTrace($"Currrent mediumAvailable: {mediumAvailable}, used {mediumUsed}");
                            if (mediumUsed < mediumAvailable && windowRemain > 0)
                            {
                                mediumUsed++;
                                return;
                            }
                            break;

                        case DataPriority.Low:
                            // 低优先级强制保留区间
                            int lowAvailable = Math.Min(lowMax - lowUsed, windowRemain);
                            logger.LogTrace($"Currrent lowAvailable: {lowAvailable}, used {lowUsed}");
                            if (lowAvailable > 0)
                            {
                                lowUsed++;
                                return;
                            }
                            break;
                    }

                    // 等待窗口空间释放
                    Monitor.Wait(_windowLock, 100); // 添加超时防止死锁
                }
            }
        }
        private void ReleaseWindowSlot(DataPriority priority)
        {
            lock (_windowLock)
            {
                switch (priority)
                {
                    case DataPriority.High:
                        if (highUsed > 0) highUsed--;
                        break;
                    case DataPriority.Medium:
                        if (mediumUsed > 0) mediumUsed--;
                        break;
                    case DataPriority.Low:
                        if (lowUsed > 0) lowUsed--;
                        break;
                }
                Monitor.PulseAll(_windowLock);
            }
        }
        private async Task WaitForAck(int seqNum, DataPriority priority)
        {
            var timeout = Task.Delay(5000); // 5秒ACK超时
            var completionSource = new TaskCompletionSource<bool>();

            // 设置ACK到达回调
            Action<int> ackHandler = null;
            ackHandler = (ackedSeq) =>
            {
                if (TryRemovePendingMessage(priority, seqNum))
                {
                    ReleaseWindowSlot(priority);
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
                logger.LogWarning($"ACK for seq {priority} {seqNum} not received");
            }
        }

        // 修改后的发送方法
        private async Task<bool> SendRawData(CommunicationData data)
        {
            // 检查 _clientSocket 是否为 null 或未连接
            if (_clientSocket == null || !_clientSocket.Connected)
            {
                logger.LogError("Client socket is null or not connected.");
                return false;
            }

            // 检查 config 是否为 null
            if (config == null)
            {
                logger.LogError("Protocol configuration is null.");
                return false;
            }

            try
            {
                // 创建协议数据包
                var packet = CreateProtocolPacket(data);

                // 序列化为字节数组
                byte[] protocolBytes = SerializePacket(packet);
                if (protocolBytes == null)
                {
                    return false;
                }

                // 发送数据(确保发送完整)
                bool sendSuccess = await SendDataBytes(protocolBytes);
                return sendSuccess;
            }
            catch (SocketException sex)
            {
                logger.LogError($"Socket error in SendData: {sex.SocketErrorCode} - {sex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Unexpected error in SendData: {ex.Message}");
                return false;
            }
        }

        private ProtocolPacketWrapper CreateProtocolPacket(CommunicationData data)
        {
            return new ProtocolPacketWrapper(
                new Protocol.ProtocolPacket()
                {
                    Header = new Protocol.ProtocolHeader { Version = 0x01, Reserved = ByteString.CopyFrom(new byte[3]) },
                    Data = data
                },
                config);
        }

        private byte[] SerializePacket(ProtocolPacketWrapper packet)
        {
            try
            {
                return packet.ToBytes();
            }
            catch (Exception ex)
            {
                logger.LogError($"Packet serialization failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> SendDataBytes(byte[] protocolBytes)
        {
            int totalSent = 0;
            while (totalSent < protocolBytes.Length)
            {
                int sent = await _clientSocket.SendAsync(
                    new ArraySegment<byte>(protocolBytes, totalSent, protocolBytes.Length - totalSent),
                    SocketFlags.None);

                if (sent == 0)
                {
                    logger.LogWarning("Connection closed during send");
                    return false;
                }

                totalSent += sent;
                logger.LogDebug($"Sent {sent} bytes, total sent: {totalSent}");
            }

            logger.LogInformation($"Data sent successfully, total length: {protocolBytes.Length}");
            return true;
        }
        // 修改 ReceiveData 方法
        public async Task<CommunicationData> ReceiveData(CancellationToken token)
        {
            var ack = await Task.Run(() => ReceiveRawData(token));
            if (ack.Error != null)
            {
                logger.LogWarning($"Receive failed: {ack.Error}");
            }
            return ack.Data;
        }
        // 优化后的接收方法
        private async Task<(CommunicationData Data, string Error)> ReceiveRawData(CancellationToken token)
        {
            // 检查 _clientSocket 是否为 null 或未连接
            if (_clientSocket == null || !_clientSocket.Connected)
            {
                Disconnect();
                return (null, "Client socket is null or not connected.");
            }

            // 检查 config 是否为 null
            if (config == null)
            {
                return (null, "Protocol configuration is null.");
            }

            try
            {
                // 1. 读取协议头（8字节）
                byte[] headerBuffer = new byte[8];
                int headerOffset = 0;
                while (headerOffset < headerBuffer.Length && !token.IsCancellationRequested)
                {
                    int received = await _clientSocket.ReceiveAsync(
                        new ArraySegment<byte>(headerBuffer, headerOffset, headerBuffer.Length - headerOffset),
                        SocketFlags.None,
                        token);

                    if (received == 0)
                    {
                        return (null, "Connection closed by remote host");
                    }
                    headerOffset += received;
                }

                // 2. 解析协议头
                if (!ProtocolHeaderExtensions.TryFromBytes(headerBuffer, out ProtocolHeader header))
                {
                    return (null, "Invalid protocol header format");
                }

                // 3. 版本检查
                if (!config.SupportedVersions.Contains((byte)header.Version))
                {
                    return (null, $"Unsupported protocol version: {header.Version}");
                }

                // 4. 验证消息长度
                if (header.MessageLength > config.MaxPacketSize - 8) // 减去头部长度
                {
                    return (null, $"Message length {header.MessageLength} exceeds maximum allowed size");
                }

                // 5. 读取消息体
                byte[] fullPacket = new byte[8 + (int)header.MessageLength];
                Buffer.BlockCopy(headerBuffer, 0, fullPacket, 0, 8);

                int payloadOffset = 8;
                int remaining = (int)header.MessageLength;
                while (remaining > 0 && !token.IsCancellationRequested)
                {
                    int received = await _clientSocket.ReceiveAsync(
                        new ArraySegment<byte>(fullPacket, payloadOffset, remaining),
                        SocketFlags.None,
                        token);

                    if (received == 0)
                    {
                        return (null, "Connection closed during payload receive");
                    }

                    payloadOffset += received;
                    remaining -= received;
                }

                // 6. 解析完整数据包
                var parseResult = ProtocolPacketWrapper.TryFromBytes(fullPacket, config);
                if (!parseResult.Success)
                {
                    return (null, parseResult.Error ?? "Failed to parse protocol packet");
                }

                // 7. 返回成功结果
                return (parseResult.Packet.Data, null);
            }
            catch (OperationCanceledException)
            {
                return (null, "Receive operation was canceled");
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.ConnectionReset:
                        Disconnect();
                        return (null, $"Connection reset: {ex.SocketErrorCode}");
                    default:
                        return (null, $"Socket error: {ex.SocketErrorCode} - {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return (null, $"Receive error: {ex.Message}");
            }
        }
        // 新增异常处理统一方法
        private void HandleSendFailure(Exception ex, CommunicationData data)
        {
            logger.LogWarning($"Error sending SeqNum={data.SeqNum}: {ex.Message}");

            TryRemovePendingMessage(data.Priority, data.SeqNum);

            // 检查连接状态
            if (!_isConnected) return;

            // 尝试重新连接
            try
            {
                Disconnect();
                Connect().Wait();
                logger.LogCritical("Reconnected to server");
            }
            catch (Exception reconnectEx)
            {
                logger.LogCritical($"Reconnect failed: {reconnectEx.Message}");
            }
        }

        // 进度回调事件
        public event Action<FileTransferProgress> OnFileTransferProgress;

        public async Task UploadFileAsync(string filePath, DataPriority priority = DataPriority.Medium)
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                logger.LogWarning($"File not found {filePath}");

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

            // 并行计算哈希和传输
            var hashTask = CalculateFileHashAsync(session);
            var transferTask = StartFileTransfer(session);

            await Task.WhenAll(hashTask, transferTask);
        }

        private int CalculateChunkSize(long fileSize)
        {
            // 动态分块策略（单位：MB）
            return fileSize switch
            {
                > 20L * 1024 * 1024 * 1024 => 16 * 1024 * 1024,  // 20GB+文件用16MB块
                > 10L * 1024 * 1024 * 1024 => 8 * 1024 * 1024,   // 10GB+文件用8MB块
                > 1L * 1024 * 1024 * 1024 => 4 * 1024 * 1024,    // 1GB+文件用4MB块
                _ => 1 * 1024 * 1024                             // 小文件用1MB块
            };
        }

        private async Task StartFileTransfer(FileTransferSession session)
        {
            UpdateProgress(session, TransferStatus.Preparing);
            try
            {
                // 动态并发度：20G以上文件使用更高并发（如32线程）
                int parallelism = Environment.ProcessorCount * 2;
                if (session.FileSize > 20L * 1024 * 1024 * 1024)
                    parallelism = Math.Min(parallelism * 2, 32); // 避免过度占用CPU

                // 异步文件流 + 16MB缓冲区（提升读取速度）
                using var fileStream = new FileStream(
                    session.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024 * 1024,  // 大缓冲区减少I/O次数
                    useAsync: true
                );

                var chunkIndexes = Enumerable.Range(0, session.TotalChunks).ToList();
                var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };

                // 带重试的并行块发送（每个块最多重试3次，指数退避）
                await Parallel.ForEachAsync(chunkIndexes, options, async (chunkIndex, ct) =>
                {
                    int retry = 0;
                    while (retry < 3) // 增加重试次数到3次，提高可靠性
                    {
                        try
                        {
                            await SendFileChunk(fileStream, session, chunkIndex);
                            break;
                        }
                        catch (Exception ex)
                        {
                            retry++;
                            logger.LogWarning($"Chunk {chunkIndex} 发送失败，重试 {retry}/3: {ex.Message}");
                            await Task.Delay(100 * retry); // 退避时间：100ms → 200ms → 400ms
                        }
                    }
                });

                await SendTransferComplete(session);
                UpdateProgress(session, TransferStatus.Completed);
            }
            catch (Exception ex)
            {
                UpdateProgress(session, TransferStatus.Failed, ex.Message);
                logger.LogError($"文件 {session.FileName} 传输失败: {ex.Message}");
                throw;
            }
            finally
            {
                _fileTransfers.TryRemove(session.FileId, out _);
            }
        }

        private async Task SendFileChunk(FileStream fileStream, FileTransferSession session, int chunkIndex)
        {
            // 防止块索引溢出（使用long计算位置）
            long chunkPosition = (long)chunkIndex * session.ChunkSize;
            if (chunkPosition >= session.FileSize)
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), "块索引超出文件范围");

            var buffer = new byte[session.ChunkSize];

            fileStream.Seek(chunkPosition, SeekOrigin.Begin);
            int bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);

            if (bytesRead == 0)
                throw new InvalidOperationException($"块 {chunkIndex} 读取失败（文件结束）");

            var chunkData = buffer.AsSpan(0, bytesRead).ToArray(); // 仅使用有效字节
            var chunkMd5 = CalculateChunkHash(chunkData);

            var data = new CommunicationData
            {
                InfoType = InfoType.File,
                FileId = session.FileId,
                FileName = session.FileName,
                FileSize = session.FileSize,
                ChunkIndex = chunkIndex,
                TotalChunks = session.TotalChunks,
                ChunkData = ByteString.CopyFrom(chunkData), // 转换为ByteString
                ChunkMd5 = chunkMd5,
                Priority = session.Priority
            };

            await SendData(data); // 通过优先级队列发送
            Interlocked.Add(ref session.TransferredBytes, bytesRead); // 原子更新进度

            // 每5个块更新进度（高频操作可能影响性能，适当降低频率）
            if (chunkIndex % 5 == 0)
            {
                UpdateProgress(session, TransferStatus.Transferring);
            }
        }

        private async Task SendTransferComplete(FileTransferSession session)
        {
            var completeData = new CommunicationData
            {
                InfoType = InfoType.File,
                FileId = session.FileId,
                Message = "FILE_COMPLETE",
                Md5Hash = session.FileHash,
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

            logger.LogCritical("Connection closed gracefully");
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
