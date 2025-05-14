using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Client.Core.SSLClientClass
{
    public class SSLClientInstance
    {
        private readonly string _certPath;
        private readonly string _certPassword;
        private readonly bool _useClientCertificate;

        public SSLClientInstance(bool useClientCertificate = false, string certPath = "", string certPassword = "")
        {
            _certPath = certPath;
            _certPassword = certPassword;
            _useClientCertificate = useClientCertificate;

            LoadSavedThumbprints();
        }

        public async Task ConnectAsync(string server, int port)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(server, port);

                using var stream = client.GetStream();
                using var sslStream = new SslStream(stream, false, ValidateServerCertificate);

                // 准备客户端证书（如果需要）
                X509CertificateCollection? clientCertificates = null;
                if (_useClientCertificate)
                {
                    X509Certificate2 clientCert;

                    if (!string.IsNullOrEmpty(_certPath))
                    {
                        // 从文件加载现有证书
                        clientCert = new X509Certificate2(_certPath, _certPassword);
                    }
                    else
                    {
                        // 动态创建自签名客户端证书（仅用于测试）
                        clientCert = CreateClientCertificate($"client-{Guid.NewGuid()}");
                        Console.WriteLine("已创建临时客户端证书");
                    }

                    clientCertificates = new X509CertificateCollection { clientCert };
                }

                // 客户端认证
                await sslStream.AuthenticateAsClientAsync(
                    targetHost: server,
                    clientCertificates: clientCertificates,
                    enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                    checkCertificateRevocation: false
                );

                Console.WriteLine("已成功建立SSL连接");

                // 发送消息
                var message = "Hello, server!";
                var messageData = Encoding.UTF8.GetBytes(message);
                await sslStream.WriteAsync(messageData, 0, messageData.Length);
                Console.WriteLine($"已发送: {message}");

                // 接收响应
                var buffer = new byte[1024];
                var bytesRead = await sslStream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"收到响应: {response}");
            }
            catch (AuthenticationException authEx)
            {
                Console.WriteLine($"SSL认证失败: {authEx.Message}");
                if (authEx.InnerException != null)
                {
                    Console.WriteLine($"内部异常: {authEx.InnerException.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"连接错误: {ex.Message}");
            }
        }

        // 自定义证书验证回调
        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            //// 在生产环境中，应实现更严格的验证逻辑
            //Console.WriteLine($"证书验证状态: {sslPolicyErrors}");

            //if (sslPolicyErrors == SslPolicyErrors.None)
            //    return true;

            //// 允许自签名证书（仅用于开发环境！）
            //Console.WriteLine("警告: 接受了不受信任的证书");
            return true;
        }

        private static readonly string defaultCertPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YourApp", "Certificates", "client_certificate.pfx");

        private static readonly string thumbprintFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YourApp", "Certificates", "cert_thumbprint.json");

        private static Dictionary<string, string> certificateThumbprints = new Dictionary<string, string>();


        public static X509Certificate2 CreateClientCertificate(string subjectName)
        {
            // 检查默认路径下是否存在证书文件
            if (File.Exists(defaultCertPath))
            {
                try
                {
                    var LoadCertificate = new X509Certificate2(
                        defaultCertPath,
                        "password",
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                    string thumbprint = GetCertificateThumbprint(LoadCertificate);

                    // 验证指纹
                    if (certificateThumbprints.TryGetValue(subjectName, out string? savedThumbprint) &&
                        !string.Equals(thumbprint, savedThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"警告: 证书指纹不匹配! 保存的: {savedThumbprint}, 实际: {thumbprint}");
                        // 可以选择抛出异常或重新生成证书
                    }

                    certificateThumbprints[subjectName] = thumbprint;
                    SaveThumbprints();

                    Console.WriteLine($"已加载现有证书: {subjectName}, 指纹: {thumbprint}");
                    return LoadCertificate;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"加载证书失败: {ex.Message}，将重新创建证书。");
                }
            }

            // 创建新证书
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={subjectName}"),
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // 添加证书扩展
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));

            // 生成自签名证书
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            var certWithKey = new X509Certificate2(
                certificate.Export(X509ContentType.Pfx, "password"),
                "password",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

            // 保存证书到默认路径
            Directory.CreateDirectory(Path.GetDirectoryName(defaultCertPath));
            File.WriteAllBytes(defaultCertPath, certWithKey.Export(X509ContentType.Pfx, "password"));

            // 计算并保存指纹
            string newThumbprint = GetCertificateThumbprint(certWithKey);
            certificateThumbprints[subjectName] = newThumbprint;
            SaveThumbprints();

            Console.WriteLine($"已创建新证书: {subjectName}, 指纹: {newThumbprint}");
            return certWithKey;
        }

        private static string GetCertificateThumbprint(X509Certificate certificate)
        {
            using var cert = new X509Certificate2(certificate);
            return BitConverter.ToString(cert.GetCertHash()).Replace("-", "").ToUpperInvariant();
        }

        private static void SaveThumbprints()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(thumbprintFilePath));
                string json = JsonSerializer.Serialize(certificateThumbprints, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(thumbprintFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存指纹文件失败: {ex.Message}");
            }
        }

        private static void LoadSavedThumbprints()
        {
            try
            {
                if (File.Exists(thumbprintFilePath))
                {
                    string json = File.ReadAllText(thumbprintFilePath);
                    var savedThumbprints = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                    if (savedThumbprints != null)
                    {
                        certificateThumbprints = savedThumbprints;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载指纹文件失败: {ex.Message}");
            }
        }
    }
}