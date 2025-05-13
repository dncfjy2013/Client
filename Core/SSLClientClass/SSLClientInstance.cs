using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

        private static X509Certificate2 CreateClientCertificate(string subjectName)
        {
            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={subjectName}"),
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // 添加基本约束
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: true));

            // 添加密钥用法
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature,
                    critical: true));

            // 添加增强密钥用法
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },  // 客户端认证
                    critical: false));

            // 自签名客户端证书（注意：生产环境中应该由CA签名）
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            return new X509Certificate2(
                certificate.Export(X509ContentType.Pfx, "password"),
                "password",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
    }
}