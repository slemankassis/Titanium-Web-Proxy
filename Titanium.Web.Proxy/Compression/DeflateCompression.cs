﻿using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    class DeflateCompression : ICompression
    {
        public async Task<byte[]> Compress(byte[] responseBody)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                   await zip.WriteAsync(responseBody, 0, responseBody.Length).ConfigureAwait(false);
                }

                return ms.ToArray();
            }
        }
    }
}
