using Client.Common;
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
        private System.Timers.Timer _heartbeatTimer; // 新增心跳定时器
        //private readonly object _sendLock = new object(); // 发送锁保证线程安全
        private bool _isConnected = false;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _heartbeatSendLock = new SemaphoreSlim(1, 1);
        private int _currentSeq = 1; // 当前发送序列号
        private int _expectedAck = 1; // 期望确认号
        private readonly Dictionary<int, Date> _pendingMessages = new();
        private readonly System.Timers.Timer _resendTimer; // 新增重传定时器

        // 修改后的心跳相关代码片段
        private readonly SemaphoreSlim _heartbeatLock = new SemaphoreSlim(1, 1);
        private int _heartbeatCountout = 0;
        // 新增文件传输相关字段
        private readonly ConcurrentDictionary<string, FileTransferState> _activeTransfers = new();
        private readonly object _transferLock = new object();
        // 初始化信号量（在构造函数中）
        private readonly SemaphoreSlim _transferSemaphore = new SemaphoreSlim(1, 1);
        private bool _skipNextHeartbeat = false;

        private readonly Queue<int> _sendWindow = new Queue<int>();
        private const int WindowSize = 100; // 窗口大小可配置

        public ClientInstance(string serverIp, int port)
        {
            _serverIp = serverIp;
            _port = port;

            _resendTimer = new System.Timers.Timer(1000); // 每秒检查重传
            _resendTimer.Elapsed += CheckResends;
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
            _isConnected = true;

            _resendTimer.Start(); // 启动重传检查定时器
        }
        // 新增重传检查方法
        private async void CheckResends(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            var toResend = _pendingMessages
                .Where(p => (now - p.Value.FirstSentTime).TotalMilliseconds >
                           (p.Value.RetryCount + 1) * 5000)
                .ToList();

            foreach (var item in toResend)
            {
                try
                {
                    Console.WriteLine($"Resending SeqNum={item.Key} (Attempt {item.Value.RetryCount + 1})");
                    await SendRawData(item.Value.communicationData);
                    item.Value.RetryCount++;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var ack = await Task.Run(() => ReceiveRawData(cts.Token));
                    if(ack!= null && ack.InfoType != InfoType.HeartBeat)
                    {
                        _pendingMessages.Remove(ack.AckNum);
                    }
                }
                catch (Exception ex)
                {
                    HandleSendFailure(ex, item.Value.communicationData);
                }
            }
        }
        private void StartHeartbeat()
        {
            // 设置心跳间隔（示例为3秒）
            _heartbeatTimer = new System.Timers.Timer(3000);
            _heartbeatTimer.Elapsed += (sender, e) => SendHeartbeatAsync();
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

        private async void SendHeartbeatAsync()
        {
            if (!_isConnected || _skipNextHeartbeat) 
            {
                _skipNextHeartbeat = false;
                return; 
            }

            try
            {
                await _heartbeatLock.WaitAsync();

                var heartbeatData = new CommunicationData
                {
                    Message = "Heartbeat",
                    InfoType = InfoType.HeartBeat,
                };

                // 通过统一发送接口发送心跳包
                await SendRawData(heartbeatData);

                // 使用独立定时器等待ACK（避免阻塞主线程）
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ack = await Task.Run(() => ReceiveRawData(cts.Token));
                
                if(ack == null)
                {
                    _heartbeatCountout++;
                    if (_heartbeatCountout > 100)
                    {
                        Console.WriteLine($"server is not avalable");
                    }
                }
            }
            finally
            {
                _heartbeatLock.Release();
            }
        }

        public async Task SendData(CommunicationData data)
        {
            // 使用独立信号量保证心跳包发送优先级
            SemaphoreSlim lockToTake = data.InfoType == InfoType.HeartBeat
                ? _heartbeatSendLock
                : _sendLock;
            if(data.InfoType != InfoType.HeartBeat)
            {
                if(!_pendingMessages.TryGetValue(data.SeqNum, out _))
                {
                    Date date = new Date() { communicationData = data, FirstSentTime = DateTime.Now, RetryCount = 0 };
                    _pendingMessages.Add(data.SeqNum, date);
                }
                data.SeqNum = _currentSeq;
                _currentSeq++;
            }
            await lockToTake.WaitAsync();
            try
            {
                await SendRawData(data);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ack = await Task.Run(() => ReceiveRawData(cts.Token));
                if(ack != null)
                {
                    if (data.InfoType == InfoType.HeartBeat)
                    {
                        Console.WriteLine($"Sent: {data.InfoType}");
                    }
                    else
                        Console.WriteLine($"Sent: {data.InfoType}, Seq={data.SeqNum}, Ack={ack.AckNum}");
                }

                _skipNextHeartbeat = true;
            }
            finally
            {
                lockToTake.Release();
            }
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

            // 停止所有定时器
            _heartbeatTimer?.Stop();
            _resendTimer?.Stop(); // 新增停止重传定时器

            // 清理资源
            _heartbeatTimer?.Dispose();

            // 清除所有待处理消息
            _pendingMessages.Clear();

            // 安全关闭socket
            try
            {
                _clientSocket?.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException) { /* 忽略关闭异常 */ }
            finally
            {
                _clientSocket?.Close();
            }

            Console.WriteLine("Connection closed gracefully");
        }
    }
}
