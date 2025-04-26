using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Client.Test
{
    public class LargeFileTransferTest
    {
        private const string ServerIp = "127.0.0.1"; // 替换为实际服务器IP
        private const int ServerPort = 12345; // 服务器监听端口
        private const string TestFileDir = @"D:\LargeFileTest\"; // 测试文件目录
        private const string TestFileName = "TestFile_25GB.dat"; // 测试文件名
        private const long TestFileSize = 25L * 1024 * 1024 * 1024; // 25GB

        public async Task RunTest()
        {
            // 1. 初始化客户端并连接
            var client = new ClientInstance(ServerIp, ServerPort);
            client.OnFileTransferProgress += HandleTransferProgress;

            try
            {
                await client.Connect();
                await RunFileTransferTest(client);
            }
            finally
            {
                client.Dispose();
            }
        }

        private async Task RunFileTransferTest(ClientInstance client)
        {
            // 2. 准备测试文件（若不存在则生成）
            PrepareTestFile();

            // 3. 执行文件传输
            var uploadTask = client.UploadFileAsync(Path.Combine(TestFileDir, TestFileName));
            await uploadTask; // 等待传输完成（或通过进度回调判断）

            // 4. 验证服务器端文件完整性
            await VerifyFileIntegrityOnServer();
        }

        private void PrepareTestFile()
        {
            if (!Directory.Exists(TestFileDir))
                Directory.CreateDirectory(TestFileDir);

            var filePath = Path.Combine(TestFileDir, TestFileName);
            if (!File.Exists(filePath))
            {
                using var fs = File.Create(filePath);
                fs.SetLength(TestFileSize); // 创建指定大小的空文件（或使用实际大文件）
            }
        }

        private async Task VerifyFileIntegrityOnServer()
        {
            // 假设服务器接收路径为 "ServerReceived/"
            var serverFilePath = Path.Combine("ServerReceived", TestFileName);
            if (!File.Exists(serverFilePath))
                throw new Exception("服务器端文件未找到");

            // 验证文件大小
            var serverFileSize = new FileInfo(serverFilePath).Length;
            if (serverFileSize != TestFileSize)
                throw new Exception($"文件大小不一致：客户端{TestFileSize} vs 服务器{serverFileSize}");

            // 验证MD5哈希
            var clientHash = await CalculateMD5(Path.Combine(TestFileDir, TestFileName));
            var serverHash = await CalculateMD5(serverFilePath);
            if (clientHash != serverHash)
                throw new Exception("文件MD5哈希不一致");

            Console.WriteLine("文件传输验证通过：大小和MD5均一致");
        }

        private async Task<string> CalculateMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await md5.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private void HandleTransferProgress(Client.FileTransferProgress progress)
        {
            switch (progress.Status)
            {
                case (Client.TransferStatus)TransferStatus.Preparing:
                    Console.WriteLine($"[准备] {progress.FileName} - {progress.TotalBytes / (1024d * 1024 * 1024):F2} GB");
                    break;

                case (Client.TransferStatus)TransferStatus.Transferring:
                    var progressPct = (double)progress.TransferredBytes / progress.TotalBytes * 100;
                    Console.WriteLine($"[进行中] {progress.FileName}: {progressPct:F2}% 已传输");
                    break;

                case (Client.TransferStatus)TransferStatus.Completed:
                    Console.WriteLine($"[完成] {progress.FileName} - 传输成功");
                    break;

                case (Client.TransferStatus)TransferStatus.Failed:
                    throw new Exception($"传输失败：{progress.FileName}");
            }
        }

        public enum TransferStatus
        {
            Preparing,
            Transferring,
            Completed,
            Failed
        }

        public static void Main()
        {
            var test = new LargeFileTransferTest();
            test.RunTest().GetAwaiter().GetResult();
        }
    }
}