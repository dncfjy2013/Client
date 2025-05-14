using Client.Core.SSLClientClass;
using Client.Core.UdpClientClass;
using Client.Core.UdpClientClass.Config;
using Client.Core.UdpClientClass.DateRetry;
using Client.Core.UdpClientClass.Encoder;
using Client.Core.UdpClientClass.Plugin.Compression;
using Client.Core.UdpClientClass.Plugin.Encryption;
using Client.Core.UdpClientClass.Plugin.Signature;
using Client.Core.UdpClientClass.Protocal;
using System.Net;
using System.Security.Cryptography;

SortedSet<int> sortedIntSet = new SortedSet<int>();

var test = new ThroughputTest(
            serverIp: "127.0.0.1",
            serverPort: 1111,
            clientCount: 1, // 50个并发客户端
            messageCountPerClient: 1 // 每个客户端发送2000条消息
        );
await test.RunTestAsync();


while (true) { }
SSLClientInstance sSLClientInstance = new SSLClientInstance(true);
await sSLClientInstance.ConnectAsync("127.0.0.1", 2222);



// 生成安全密钥（实际应使用安全随机数生成器）
var encryptionKey = new byte[32];
using var rng = new RNGCryptoServiceProvider();
rng.GetBytes(encryptionKey);

var signatureKey = new byte[32];
rng.GetBytes(signatureKey);

// 配置选项
var options = new UdpClientOptions
{
    MaxReconnectAttempts = 3
};

// 创建安全客户端实例
using var client = new UdpClientInstance(
    new RetryTransport(new UdpClientAdapter(), options),
    new JsonEncoder(),
    new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3333),
    options);

// 注册安全插件（顺序重要：先压缩，加密后签名）
client.RegisterPlugin(new GZip());
client.RegisterPlugin(new AES_256_CBC(encryptionKey));
client.RegisterPlugin(new HMAC_SHA256(signatureKey));

// 发送测试消息
var sendData = new Date
{
    id = 1,
    message = "Secure UDP Message"
};

var sendTask = client.SendMessageAsync<Date>(sendData);

// 接收测试消息
var receiveTask = client.ReceiveMessageAsync<Date>();

await Task.WhenAll(sendTask, receiveTask);

Console.WriteLine($"接收成功: ID={receiveTask.Result.Message.id}, 内容={receiveTask.Result.Message.message}");


using (var clientInstance = new Client.Core.HttpClientClass.HttpClientInstance("http://localhost:9999/"))
{
    var getParameters = new Dictionary<string, string>
            {
                { "param1", "value1" },
                { "param2", "value2" }
            };
    await clientInstance.SendGetRequest(getParameters);

    await clientInstance.SendPostRequest("这是 POST 请求的数据", getParameters);

    await clientInstance.SendPutRequest("这是 PUT 请求的数据", getParameters);

    await clientInstance.SendDeleteRequest(getParameters);
}


//LargeFileTransferTest.Main();
// 使用示例
// 执行测试


// 配置测试参数
//var tester = new MassiveFileStressTester("127.0.0.1", 12345);

//Console.WriteLine("开始压力测试...");
//await tester.RunTestAsync();

//Console.WriteLine("测试完成，按任意键退出");
//Console.ReadKey();


//var client = new SocketClientInstance("127.0.0.1", 12345);
//await client.Connect();
//CommunicationData communicationData = new CommunicationData()
//{
//    InfoType = Client.Common.InfoType.Normal,
//    Priority = DataPriority.High,
//    Message = "111"
//};
//client.SendData(communicationData);

//var fileId = Guid.NewGuid().ToString();
//await client.SendFile("./mpg-creator.rar", fileId, progress =>
//{
//    Console.WriteLine($"传输进度: {progress}%");
//});

while (true)
{

}
