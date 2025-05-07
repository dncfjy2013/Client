using Client.Core.UdpClientClass;
using System.Net;

//var test = new ThroughputTest(
//            serverIp: "127.0.0.1",
//            serverPort: 1111,
//            clientCount: 50, // 50个并发客户端
//            messageCountPerClient: 2000 // 每个客户端发送2000条消息
//        );

//await test.RunTestAsync();

// 配置选项
var options = new UdpClientOptions
{
    MaxReconnectAttempts = 3
};

// 创建数据客户端
using var client = new UdpClientInstance(
    new RetryTransport(new UdpClientAdapter(), options),
    new JsonEncoder(),
    new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3333),
    options);

// 注册压缩插件
client.RegisterPlugin(new CompressionPlugin());

// 发送测试消息
var sendData = new Date
{
    id = 1,
    message = "Hello UDP"
};

var sendTask = client.SendMessageAsync<Date>(sendData);

// 接收测试消息
var receiveTask = client.ReceiveMessageAsync<Date>();

await Task.WhenAll(sendTask, receiveTask);

Console.WriteLine($"接收成功: ID={receiveTask.Result.Message.id}, 内容={receiveTask.Result.Message.message}");
        


//using (var clientInstance = new HttpClientInstance("http://localhost:9999/"))
//{
//    var getParameters = new Dictionary<string, string>
//            {
//                { "param1", "value1" },
//                { "param2", "value2" }
//            };
//    await clientInstance.SendGetRequest(getParameters);

//    await clientInstance.SendPostRequest("这是 POST 请求的数据", getParameters);

//    await clientInstance.SendPutRequest("这是 PUT 请求的数据", getParameters);

//    await clientInstance.SendDeleteRequest(getParameters);
//}


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
