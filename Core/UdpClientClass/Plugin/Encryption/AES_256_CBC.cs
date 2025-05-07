using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Plugin.Encryption
{
    /// <summary>
    /// AES-256-CBC加密插件
    /// </summary>
    public class AES_256_CBC : IUdpPlugin
    {
        private readonly byte[] _key;

        public AES_256_CBC(byte[] key)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("AES-256需要32字节密钥", nameof(key));
            _key = key;
        }

        public ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.GenerateIV();

            // 加密流程：IV(16字节) + 密文
            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();

            ms.Write(aes.IV, 0, aes.IV.Length); // 写入初始化向量

            using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();

            return new ValueTask<byte[]>(ms.ToArray());
        }

        public ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct)
        {
            using var aes = Aes.Create();
            aes.Key = _key;

            // 分离IV和密文
            var iv = new byte[16];
            Array.Copy(data, 0, iv, 0, iv.Length);
            aes.IV = iv;

            var cipherText = new byte[data.Length - iv.Length];
            Array.Copy(data, iv.Length, cipherText, 0, cipherText.Length);

            // 解密流程
            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(cipherText);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var outMs = new MemoryStream();
            cs.CopyTo(outMs);

            return new ValueTask<byte[]>(outMs.ToArray());
        }
    }
}
