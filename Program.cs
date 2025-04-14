using Client;

//StressTester.Main();
// 使用示例
// 执行测试


// 配置测试参数
var testManager = new StressTestManager(
    serverIp: "127.0.0.1",
    serverPort: 12345,
    testFilesDirectory: "TestFiles",  // 存放测试文件的目录
    numClients: 20,                  // 并发客户端数量
    messagesPerClient: 50,           // 每个客户端发送的消息数
    testFileTransfer: true           // 是否测试文件传输
);

// 运行压力测试
await testManager.RunStressTest();

Console.WriteLine("Stress test completed. Press any key to exit...");
Console.ReadKey();
    



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
