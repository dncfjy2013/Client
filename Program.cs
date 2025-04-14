using Client;

//StressTester.Main();


var client = new ClientInstance("127.0.0.1", 12345);
await client.Connect();
CommunicationData communicationData = new CommunicationData()
{
    InfoType = Client.Common.InfoType.Normal,
    Priority = DataPriority.High,
    Message = "111"
};
client.SendData(communicationData);

//var fileId = Guid.NewGuid().ToString();
//await client.SendFile("./mpg-creator.rar", fileId, progress =>
//{
//    Console.WriteLine($"传输进度: {progress}%");
//});

while (true)
{

}
