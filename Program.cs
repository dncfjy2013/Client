using Client;

var manager = new ClientManager(clientCount: 10, maxConcurrent: 1000);
await manager.StartClientsAsync("192.168.21.103", 12345);


//var client = new ClientInstance("127.0.0.1", 12345);
//await client.Connect();

//var fileId = Guid.NewGuid().ToString();
//await client.SendFile("./mpg-creator.rar", fileId, progress =>
//{
//    Console.WriteLine($"传输进度: {progress}%");
//});

while (true)
{

}
