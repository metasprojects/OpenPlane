using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Preview;
using Xunit;

namespace OpenPlane.Core.Tests
{
    public class PreviewTests
    {
        /// <summary>
        /// Several pictures rendered at once (as the app does for template cards) must all be valid PNGs: every chunk's
        /// CRC right and the image data unpacking to the declared size.
        /// </summary>
        [Fact]
        public void PicturesRenderedInParallelAreValidPngs()
        {
            var designs = Templates.All.Where(t => t.Id != "blank").Select(t => DesignGenerator.Generate(t.Create())).ToList();
            var pngs = new byte[designs.Count * 4][];
            Parallel.For(0, pngs.Length, i =>
            {
                var g = designs[i % designs.Count];
                pngs[i] = ThreeView.RenderPng(g.Design, g.Meshes, 120 + i);
            });
            foreach (var png in pngs) AssertValidPng(png);
        }

        private static void AssertValidPng(byte[] png)
        {
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
            int pos = 8, width = 0, height = 0, colorType = 0;
            var idat = new MemoryStream();
            while (pos < png.Length)
            {
                int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                var body = png.Skip(pos + 8).Take(len).ToArray();
                uint stored = (uint)((png[pos + 8 + len] << 24) | (png[pos + 9 + len] << 16) | (png[pos + 10 + len] << 8) | png[pos + 11 + len]);
                Assert.Equal(Crc(png.Skip(pos + 4).Take(4 + len).ToArray()), stored);
                if (type == "IHDR") { width = (body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3]; height = (body[4] << 24) | (body[5] << 16) | (body[6] << 8) | body[7]; colorType = body[9]; }
                if (type == "IDAT") idat.Write(body, 0, body.Length);
                pos += 12 + len;
            }
            var z = idat.ToArray();
            using (var ds = new DeflateStream(new MemoryStream(z, 2, z.Length - 6), CompressionMode.Decompress))
            using (var raw = new MemoryStream())
            {
                ds.CopyTo(raw);
                int bpp = colorType == 6 ? 4 : 3;
                Assert.Equal((width * bpp + 1) * height, raw.Length);
            }
        }

        private static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
