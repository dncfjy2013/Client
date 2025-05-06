using Client;
using Client.Test;

var test = new ThroughputTest(
            serverIp: "127.0.0.1",
            serverPort: 1111,
            clientCount: 50, // 50个并发客户端
            messageCountPerClient: 2000 // 每个客户端发送2000条消息
        );

await test.RunTestAsync();
//LargeFileTransferTest.Main();
// 使用示例
// 执行测试


// 配置测试参数
//var tester = new MassiveFileStressTester("127.0.0.1", 12345);

//Console.WriteLine("开始压力测试...");
//await tester.RunTestAsync();

//Console.WriteLine("测试完成，按任意键退出");
//Console.ReadKey();


//var client = new ClientInstance("127.0.0.1", 12345);
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
