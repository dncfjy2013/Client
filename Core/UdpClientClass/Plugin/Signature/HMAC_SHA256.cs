using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Plugin.Signature
{
    /// <summary>
    /// HMAC-SHA256签名验证插件
    /// </summary>
    public class HMAC_SHA256 : IUdpPlugin
    {
        private readonly byte[] _key;

        public HMAC_SHA256(byte[] key)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("HMAC-SHA256需要32字节密钥", nameof(key));
            _key = key;
        }

        public ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct)
        {
            using var hmac = new HMACSHA256(_key);
            var hash = hmac.ComputeHash(data);

            // 签名格式：32字节HMAC + 原始数据
            var combined = new byte[hash.Length + data.Length];
            Buffer.BlockCopy(hash, 0, combined, 0, hash.Length);
            Buffer.BlockCopy(data, 0, combined, hash.Length, data.Length);

            return new ValueTask<byte[]>(combined);
        }

        public ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct)
        {
            if (data.Length < 32)
                throw new CryptographicException("无效的签名长度");

            // 分离签名和原始数据
            var hash = new byte[32];
            var payload = new byte[data.Length - 32];
            Array.Copy(data, 0, hash, 0, 32);
            Array.Copy(data, 32, payload, 0, payload.Length);

            // 验证签名
            using var hmac = new HMACSHA256(_key);
            var computedHash = hmac.ComputeHash(payload);

            if (!hash.SequenceEqual(computedHash))
                throw new CryptographicException("签名验证失败");

            return new ValueTask<byte[]>(payload);
        }
    }
}
