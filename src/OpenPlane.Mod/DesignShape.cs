using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// The design's shape seen from above: for each spot on a fine grid along and across the plane, the lowest and highest
    /// point of the design's surfaces there. A stock piece (canopy glass, a cockpit frame, a gun barrel) whose points lie
    /// above, below or beside that shape would show outside the design, so it is hidden. Unlike one box around the whole
    /// design, this follows the fuselage's top and the wing's thickness, so a stock canopy sitting on top of the fuselage
    /// counts as outside. It works with open or overlapping meshes (converted planes): only the surfaces' span is used.
    /// </summary>
    internal sealed class DesignShape
    {
        private readonly float _x0, _z0, _cell;
        private readonly int _nx, _nz;
        private readonly float[] _lo, _hi;

        private DesignShape(float x0, float z0, float cell, int nx, int nz)
        {
            _x0 = x0; _z0 = z0; _cell = cell; _nx = nx; _nz = nz;
            _lo = new float[nx * nz];
            _hi = new float[nx * nz];
            for (int i = 0; i < _lo.Length; i++) { _lo[i] = float.PositiveInfinity; _hi[i] = float.NegativeInfinity; }
        }

        /// <summary>The shape of these meshes, each moved by <paramref name="offset"/> (the design's origin in the plane).</summary>
        public static DesignShape? From(IEnumerable<Mesh?> meshes, Vector3 offset)
        {
            var list = new List<(Vector3[] v, int[] t)>();
            var box = new Bounds();
            bool any = false;
            foreach (var m in meshes)
            {
                if (m == null || !m.isReadable) continue;
                var v = m.vertices;
                var t = m.triangles;
                if (v.Length == 0 || t.Length == 0) continue;
                for (int i = 0; i < v.Length; i++) v[i] += offset;
                list.Add((v, t));
                var b = new Bounds(m.bounds.center + offset, m.bounds.size);
                if (!any) { box = b; any = true; } else box.Encapsulate(b);
            }
            if (!any) return null;
            float cell = Mathf.Max(0.04f, Mathf.Max(box.size.x, box.size.z) / 320f);
            int nx = Mathf.CeilToInt(box.size.x / cell) + 3, nz = Mathf.CeilToInt(box.size.z / cell) + 3;
            var shape = new DesignShape(box.min.x - cell, box.min.z - cell, cell, nx, nz);
            foreach (var (v, t) in list)
                for (int i = 0; i + 2 < t.Length; i += 3)
                    shape.AddTriangle(v[t[i]], v[t[i + 1]], v[t[i + 2]]);
            return shape;
        }

        private void Mark(int ix, int iz, float y)
        {
            if (ix < 0 || iz < 0 || ix >= _nx || iz >= _nz) return;
            int k = iz * _nx + ix;
            if (y < _lo[k]) _lo[k] = y;
            if (y > _hi[k]) _hi[k] = y;
        }

        private void MarkPoint(Vector3 p) => Mark(Mathf.FloorToInt((p.x - _x0) / _cell), Mathf.FloorToInt((p.z - _z0) / _cell), p.y);

        private void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            // The edges, sampled finely (a wall seen edge-on from above, like a fin, has no area to fill).
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                float len = new Vector2(q.x - p.x, q.z - p.z).magnitude;
                int n = Mathf.Max(1, Mathf.CeilToInt(len / (_cell * 0.5f)));
                for (int s = 0; s <= n; s++) MarkPoint(Vector3.Lerp(p, q, s / (float)n));
            }
            // The inside: every grid spot the triangle covers seen from above, at the triangle's height there.
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            int ix0 = Mathf.FloorToInt((minX - _x0) / _cell), ix1 = Mathf.FloorToInt((maxX - _x0) / _cell);
            int iz0 = Mathf.FloorToInt((minZ - _z0) / _cell), iz1 = Mathf.FloorToInt((maxZ - _z0) / _cell);
            if (ix1 - ix0 < 1 && iz1 - iz0 < 1) return;
            float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(d) < 1e-9f) return;
            for (int iz = Mathf.Max(0, iz0); iz <= Mathf.Min(_nz - 1, iz1); iz++)
                for (int ix = Mathf.Max(0, ix0); ix <= Mathf.Min(_nx - 1, ix1); ix++)
                {
                    float x = _x0 + (ix + 0.5f) * _cell, z = _z0 + (iz + 0.5f) * _cell;
                    float w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                    float w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                    float w3 = 1 - w1 - w2;
                    if (w1 < -1e-4f || w2 < -1e-4f || w3 < -1e-4f) continue;
                    Mark(ix, iz, w1 * a.y + w2 * b.y + w3 * c.y);
                }
        }

        /// <summary>
        /// True when the point (in the plane's frame) is above, below or beside the design by more than
        /// <paramref name="tolerance"/> metres. The spots around it count too, so a point right at the design's edge is inside.
        /// </summary>
        public bool Outside(Vector3 p, float tolerance)
        {
            int ix = Mathf.FloorToInt((p.x - _x0) / _cell), iz = Mathf.FloorToInt((p.z - _z0) / _cell);
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = ix + dx, z = iz + dz;
                    if (x < 0 || z < 0 || x >= _nx || z >= _nz) continue;
                    int k = z * _nx + x;
                    if (_lo[k] < lo) lo = _lo[k];
                    if (_hi[k] > hi) hi = _hi[k];
                }
            if (lo > hi) return true;
            return p.y > hi + tolerance || p.y < lo - tolerance;
        }

        /// <summary>
        /// How much of a stock mesh shows outside the design (0 to 1): its vertices when the game lets them be read, otherwise
        /// points spread through the middle of its bounds (which a round part fills).
        /// </summary>
        public float OutsideShare(Mesh mesh, Matrix4x4 toPlane)
        {
            var points = new List<Vector3>();
            if (mesh.isReadable)
            {
                try
                {
                    var v = mesh.vertices;
                    int step = Math.Max(1, v.Length / 400);
                    for (int i = 0; i < v.Length; i += step) points.Add(v[i]);
                }
                catch { points.Clear(); }
            }
            if (points.Count == 0)
            {
                var b = mesh.bounds;
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                        for (int k = -1; k <= 1; k++)
                            points.Add(b.center + Vector3.Scale(b.extents, new Vector3(i, j, k) * 0.6f));
            }
            int outside = 0;
            foreach (var p in points) if (Outside(toPlane.MultiplyPoint3x4(p), 0.05f)) outside++;
            return outside / (float)points.Count;
        }
    }
}
