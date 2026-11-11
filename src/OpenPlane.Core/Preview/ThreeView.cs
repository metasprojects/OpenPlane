using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Preview
{
    /// <summary>
    /// Draws a plane as a three-view (top, side, front) PNG with no outside libraries. Used for thumbnails and for
    /// checking conversions against the builder's screenshots.
    /// </summary>
    public static class ThreeView
    {
        // Transparent background and mid-tone colours, so the picture reads on a black or a bone page alike.
        private static readonly byte[] Background = { 0, 0, 0, 0 };
        private static readonly byte[] Body = { 92, 95, 102, 255 };
        private static readonly byte[] WingFill = { 142, 147, 156, 255 };
        private static readonly byte[] SurfaceFill = { 255, 69, 56, 255 };
        private static readonly byte[] SlatFill = { 255, 181, 71, 255 };
        private static readonly byte[] EngineMark = { 237, 235, 230, 255 };
        private static readonly byte[] GearMark = { 200, 203, 209, 255 };
        private static readonly byte[] HardpointMark = { 95, 213, 150, 255 };
        private static readonly byte[] GunMark = { 255, 181, 71, 255 };
        private static readonly byte[] Divider = { 142, 147, 156, 90 };

        public static byte[] RenderPng(AircraftDesign design, IList<MeshData> meshes, int panelSize = 400)
        {
            var c = new Canvas(panelSize * 3, panelSize);
            c.Clear(Background);
            var stats = DesignStats.Compute(design, meshes);
            var min = Vec3.FromArray(stats.BoundsMin); var max = Vec3.FromArray(stats.BoundsMax);
            var center = (min + max) / 2;
            double extent = System.Math.Max(System.Math.Max(max.X - min.X, max.Y - min.Y), max.Z - min.Z);
            if (extent <= 0) extent = 1;
            double scale = panelSize * 0.9 / extent;

            // Top: x right, nose (+z) up. Side: nose (+z) right, y up. Front: viewer in front, plane's right on screen left.
            var views = new Func<Vec3, (double, double)>[]
            {
                p => (panelSize * 0.5 + (p.X - center.X) * scale, panelSize * 0.5 - (p.Z - center.Z) * scale),
                p => (panelSize * 1.5 + (p.Z - center.Z) * scale, panelSize * 0.5 - (p.Y - center.Y) * scale),
                p => (panelSize * 2.5 - (p.X - center.X) * scale, panelSize * 0.5 - (p.Y - center.Y) * scale),
            };

            // A plane with its own colours is drawn in them (glass last, interior pieces left out); one without, in the
            // diagram colours (control surfaces red, slats orange).
            bool painted = design.Materials != null && design.Materials.Count > 0;
            byte[] Rgb(MaterialDef m) => new byte[] { B(m.Color[0]), B(m.Color[1]), B(m.Color[2]), 255 };
            foreach (var view in views)
            {
                foreach (bool glassPass in new[] { false, true })
                    foreach (var m in meshes)
                    {
                        if (m.Role == MeshData.Interior) continue;
                        var v = m.Vertices; var t = m.Triangles;
                        for (int i = 0; i + 2 < t.Length; i += 3)
                        {
                            int slot = m.TriangleMaterials != null && i / 3 < m.TriangleMaterials.Length ? m.TriangleMaterials[i / 3] : 0;
                            var mat = painted ? DesignMaterials.ForSlot(design, m, slot) : null;
                            bool glass = mat != null && mat.Kind == MaterialDef.Glass;
                            if (glass != glassPass) continue;
                            var a = view(new Vec3(v[t[i] * 3], v[t[i] * 3 + 1], v[t[i] * 3 + 2]));
                            var b = view(new Vec3(v[t[i + 1] * 3], v[t[i + 1] * 3 + 1], v[t[i + 1] * 3 + 2]));
                            var cc = view(new Vec3(v[t[i + 2] * 3], v[t[i + 2] * 3 + 1], v[t[i + 2] * 3 + 2]));
                            c.FillTriangle(a, b, cc, mat != null ? Rgb(mat) : Body);
                        }
                    }
                foreach (var w in design.Wings)
                {
                    Quad(c, view, w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading, painted ? Rgb(DesignMaterials.At(design, w.Material)) : WingFill);
                    foreach (var s in w.Surfaces)
                        Quad(c, view, s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading,
                             painted ? Rgb(DesignMaterials.At(design, s.Material ?? w.Material)) : s.Edge == "leading" ? SlatFill : SurfaceFill);
                }
                foreach (var e in design.Engines) Dot(c, view(Vec3.FromArray(e.Position)), 5, EngineMark);
                foreach (var g in design.Gear) Dot(c, view(Vec3.FromArray(g.Position)), 3, GearMark);
                foreach (var h in design.Hardpoints) Dot(c, view(Vec3.FromArray(h.Position)), 3, HardpointMark);
                foreach (var g in design.Guns)
                {
                    var p = Vec3.FromArray(g.Position);
                    c.Line(view(p), view(p + Vec3.FromArray(g.Direction) * (extent * 0.06)), GunMark);
                }
            }
            for (int y = 0; y < panelSize; y++) { c.Set(panelSize, y, Divider); c.Set(panelSize * 2, y, Divider); }
            return c.ToPng();
        }

        private static byte B(double v) => (byte)System.Math.Round(255 * (v < 0 ? 0 : v > 1 ? 1 : v));

        private static void Quad(Canvas c, Func<Vec3, (double, double)> view, double[] a, double[] b, double[] d, double[] e, byte[] color)
        {
            var pa = view(Vec3.FromArray(a)); var pb = view(Vec3.FromArray(b));
            var pd = view(Vec3.FromArray(d)); var pe = view(Vec3.FromArray(e));
            c.FillTriangle(pa, pb, pd, color);
            c.FillTriangle(pa, pd, pe, color);
        }

        private static void Dot(Canvas c, (double x, double y) p, int r, byte[] color)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) c.Set((int)p.x + dx, (int)p.y + dy, color);
        }

        /// <summary>A tiny RGBA canvas with triangle fill and a PNG writer.</summary>
        private sealed class Canvas
        {
            private readonly int _w, _h;
            private readonly byte[] _px;
            public Canvas(int w, int h) { _w = w; _h = h; _px = new byte[w * h * 4]; }

            public void Clear(byte[] rgba) { for (int i = 0; i < _px.Length; i += 4) { _px[i] = rgba[0]; _px[i + 1] = rgba[1]; _px[i + 2] = rgba[2]; _px[i + 3] = rgba[3]; } }

            public void Set(int x, int y, byte[] rgb)
            {
                if (x < 0 || y < 0 || x >= _w || y >= _h) return;
                int i = (y * _w + x) * 4; _px[i] = rgb[0]; _px[i + 1] = rgb[1]; _px[i + 2] = rgb[2]; _px[i + 3] = rgb[3];
            }

            public void FillTriangle((double x, double y) a, (double x, double y) b, (double x, double y) c, byte[] rgb)
            {
                int minX = System.Math.Max(0, (int)System.Math.Floor(System.Math.Min(a.x, System.Math.Min(b.x, c.x))));
                int maxX = System.Math.Min(_w - 1, (int)System.Math.Ceiling(System.Math.Max(a.x, System.Math.Max(b.x, c.x))));
                int minY = System.Math.Max(0, (int)System.Math.Floor(System.Math.Min(a.y, System.Math.Min(b.y, c.y))));
                int maxY = System.Math.Min(_h - 1, (int)System.Math.Ceiling(System.Math.Max(a.y, System.Math.Max(b.y, c.y))));
                double area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (System.Math.Abs(area) < 1e-9)
                {
                    Line(a, b, rgb); Line(b, c, rgb); return; // degenerate: draw as a sliver
                }
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double w0 = (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
                        double w1 = (c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x);
                        double w2 = (a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x);
                        if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0)) Set(x, y, rgb);
                    }
            }

            public void Line((double x, double y) a, (double x, double y) b, byte[] rgb)
            {
                double dx = b.x - a.x, dy = b.y - a.y;
                int steps = (int)System.Math.Max(1, System.Math.Ceiling(System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy))));
                for (int i = 0; i <= steps; i++) Set((int)(a.x + dx * i / steps), (int)(a.y + dy * i / steps), rgb);
            }

            public byte[] ToPng()
            {
                int row = _w * 4;
                var raw = new byte[(row + 1) * _h];
                for (int y = 0; y < _h; y++)
                {
                    raw[y * (row + 1)] = 0; // filter: none
                    Buffer.BlockCopy(_px, y * row, raw, y * (row + 1) + 1, row);
                }
                byte[] deflated;
                using (var ms = new MemoryStream())
                {
                    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) ds.Write(raw, 0, raw.Length);
                    deflated = ms.ToArray();
                }
                uint adler = Adler32(raw);
                var zlib = new byte[deflated.Length + 6];
                zlib[0] = 0x78; zlib[1] = 0x9C;
                Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
                zlib[zlib.Length - 4] = (byte)(adler >> 24); zlib[zlib.Length - 3] = (byte)(adler >> 16);
                zlib[zlib.Length - 2] = (byte)(adler >> 8); zlib[zlib.Length - 1] = (byte)adler;

                using (var png = new MemoryStream())
                {
                    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                    var ihdr = new byte[13];
                    WriteBE(ihdr, 0, (uint)_w); WriteBE(ihdr, 4, (uint)_h);
                    ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // 8-bit RGBA
                    Chunk(png, "IHDR", ihdr);
                    Chunk(png, "IDAT", zlib);
                    Chunk(png, "IEND", new byte[0]);
                    return png.ToArray();
                }
            }

            private static void Chunk(Stream s, string type, byte[] data)
            {
                var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len, 0, 4);
                var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4);
                s.Write(data, 0, data.Length);
                uint crc = Crc32(t, 0xFFFFFFFFu); crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
                var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c, 0, 4);
            }

            private static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

            // Built once, before first use, by the runtime (thread-safe). A lazily filled table let a second thread read
            // it half-built, which wrote wrong checksums and made browsers refuse the picture.
            private static readonly uint[] CrcTable = MakeCrcTable();

            private static uint[] MakeCrcTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                return table;
            }

            private static uint Crc32(byte[] data, uint crc)
            {
                foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return crc;
            }

            private static uint Adler32(byte[] data)
            {
                uint a = 1, b = 0;
                foreach (var d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
                return (b << 16) | a;
            }
        }
    }
}
