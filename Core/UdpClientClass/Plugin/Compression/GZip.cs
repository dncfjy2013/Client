using Client.Core.UdpClientClass.Interface;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Core.UdpClientClass.Plugin.Compression
{
    /// <summary>
    /// 数据压缩插件（GZip）
    /// </summary>
    public class GZip : IUdpPlugin
    {
        public ValueTask<byte[]> OnSendingAsync(byte[] data, CancellationToken ct)
        {
            var compressed = Compress(data);
            return new ValueTask<byte[]>(compressed);
        }

        public ValueTask<byte[]> OnReceivedAsync(byte[] data, CancellationToken ct)
        {
            var decompressed = Decompress(data);
            return new ValueTask<byte[]>(decompressed);
        }

        private byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gs = new GZipStream(ms, CompressionMode.Compress))
            {
                gs.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        private byte[] Decompress(byte[] compressed)
        {
            using var ms = new MemoryStream(compressed);
            using var gs = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gs.CopyTo(outMs);
            return outMs.ToArray();
        }
    }
}
