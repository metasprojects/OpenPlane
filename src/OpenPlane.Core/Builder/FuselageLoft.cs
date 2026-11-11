using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Builder
{
    /// <summary>
    /// Builds a closed fuselage mesh through a list of rings (superellipse cross-sections, or custom point loops). The
    /// app's live preview (wwwroot/js/builder/geometry.js) uses the same algorithm; the saved plane always uses this one.
    /// Triangles are wound so cross(b - a, c - a) points out of the body. Open ends are closed with flat caps; a ring
    /// with no size becomes a single tip point. Optional: Catmull-Rom smoothing along the body and a turn of the whole
    /// body about its position.
    /// </summary>
    public static class FuselageLoft
    {
        public const int MinSides = 8;
        public const int MaxSides = 64;
        public const int MaxSmooth = 3;
        /// <summary>Rings smaller than this (m) are treated as a point.</summary>
        public const double PointSize = 1e-3;

        /// <summary>Superellipse exponent for a roundness value: 1 gives 2 (an ellipse), 0 gives 10 (a rounded box).</summary>
        public static double Exponent(double roundness)
        {
            double r = System.Math.Max(0, System.Math.Min(1, roundness));
            return 2 / (0.2 + 0.8 * r);
        }

        public static int ClampSides(int sides)
        {
            int n = System.Math.Max(MinSides, System.Math.Min(MaxSides, sides));
            return n % 2 == 0 ? n : n + 1;
        }

        /// <summary>Rings in nose-to-tail order (highest z first), as the mesh uses them.</summary>
        public static List<FuselageRing> OrderedRings(FuselageSpec spec)
            => spec.Rings.Select((r, i) => (r, i)).OrderByDescending(t => t.r.Z).ThenBy(t => t.i).Select(t => t.r).ToList();

        /// <summary>True when a ring has a usable custom cross-section.</summary>
        public static bool HasPoints(FuselageRing ring) => ring.Points != null && ring.Points.Count(p => p != null && p.Length >= 2) >= 3;

        /// <summary>
        /// The n offsets of a ring's points from its centre (0, Y, Z), starting at the top and going towards +x. Custom points
        /// are resampled to n when their count differs.
        /// </summary>
        public static Vec3[] Offsets(FuselageRing ring, int sides)
        {
            int n = ClampSides(sides);
            if (HasPoints(ring))
            {
                var src = ring.Points!.Where(p => p != null && p.Length >= 2).Select(p => new Vec3(p[0], p[1], p.Length > 2 ? p[2] : 0)).ToList();
                return src.Count == n ? src.ToArray() : Resample(src, n);
            }
            double e = 2 / Exponent(ring.Roundness);
            double a = System.Math.Max(0, ring.Width) / 2, b = System.Math.Max(0, ring.Height) / 2;
            var pts = new Vec3[n];
            for (int k = 0; k < n; k++)
            {
                double t = 2 * System.Math.PI * k / n;
                double s = System.Math.Sin(t), c = System.Math.Cos(t);
                pts[k] = new Vec3(a * SignedPow(s, e), b * SignedPow(c, e), 0);
            }
            return pts;
        }

        /// <summary>Points spaced evenly by length along a closed polygon, starting at its first point.</summary>
        public static Vec3[] Resample(IList<Vec3> loop, int n)
        {
            int m = loop.Count;
            var result = new Vec3[n];
            if (m == 0) return result;
            var cum = new double[m + 1];
            for (int i = 0; i < m; i++) cum[i + 1] = cum[i] + Vec3.Distance(loop[i], loop[(i + 1) % m]);
            double total = cum[m];
            if (total < 1e-12) { for (int k = 0; k < n; k++) result[k] = loop[0]; return result; }
            int seg = 0;
            for (int k = 0; k < n; k++)
            {
                double d = total * k / n;
                while (seg < m - 1 && cum[seg + 1] < d) seg++;
                double len = cum[seg + 1] - cum[seg];
                double t = len > 1e-12 ? (d - cum[seg]) / len : 0;
                result[k] = Vec3.Lerp(loop[seg], loop[(seg + 1) % m], t);
            }
            return result;
        }

        /// <summary>The points of one ring in aircraft space (body offset applied, no turn), starting at the top and going towards +x.</summary>
        public static Vec3[] RingPoints(FuselageRing ring, double[] position, int sides)
        {
            var center = new Vec3(position[0], position[1] + ring.Y, position[2] + ring.Z);
            var off = Offsets(ring, sides);
            var pts = new Vec3[off.Length];
            for (int k = 0; k < off.Length; k++) pts[k] = center + off[k];
            return pts;
        }

        /// <summary>The points of one ring in the fuselage's own space (before its turn and position).</summary>
        public static Vec3[] RingLocalPoints(FuselageRing ring, int sides) => RingPoints(ring, new double[3], sides);

        private static double SignedPow(double v, double p)
        {
            if (System.Math.Abs(v) < 1e-12) return 0;
            return System.Math.Sign(v) * System.Math.Pow(System.Math.Abs(v), p);
        }

        public static bool IsPoint(FuselageRing r)
        {
            if (HasPoints(r)) return r.Points!.Where(p => p != null && p.Length >= 2).All(p => System.Math.Sqrt(p[0] * p[0] + p[1] * p[1] + (p.Length > 2 ? p[2] * p[2] : 0)) < PointSize);
            return r.Width < PointSize && r.Height < PointSize;
        }

        /// <summary>The body's turn (identity when every angle is 0).</summary>
        public static Quat Rotation(double[]? eulerDeg)
        {
            if (eulerDeg == null || eulerDeg.Length < 3 || (eulerDeg[0] == 0 && eulerDeg[1] == 0 && eulerDeg[2] == 0)) return Quat.Identity;
            return EulerToQuat(eulerDeg);
        }

        /// <summary>Unity's Euler rule: turn about z, then x, then y (q = qy * qx * qz).</summary>
        public static Quat EulerToQuat(double[] deg)
        {
            var qx = Quat.AngleAxis(deg[0], Vec3.Right);
            var qy = Quat.AngleAxis(deg[1], Vec3.Up);
            var qz = Quat.AngleAxis(deg[2], Vec3.Forward);
            return qy * qx * qz;
        }

        public static bool IsTurned(double[]? eulerDeg) => eulerDeg != null && eulerDeg.Length >= 3 && (eulerDeg[0] != 0 || eulerDeg[1] != 0 || eulerDeg[2] != 0);

        /// <summary>A loft's rings in aircraft space, nose first: each ring's points (one point for a tip) and whether it is a tip.</summary>
        public static List<(Vec3[] pts, bool point)> PlaneRings(FuselageSpec spec)
        {
            var rings = OrderedRings(spec);
            int n = ClampSides(spec.Sides);
            var pos = spec.Position ?? new double[3];
            var result = new List<(Vec3[] pts, bool point)>();
            int smooth = System.Math.Max(0, System.Math.Min(MaxSmooth, spec.Smooth));
            bool turned = IsTurned(spec.RotationDeg);

            if (smooth == 0 && !turned)
            {
                // The v1 path, kept to the last bit so old planes regenerate identically.
                foreach (var r in rings)
                {
                    if (IsPoint(r)) result.Add((new[] { new Vec3(pos[0], pos[1] + r.Y, pos[2] + r.Z) }, true));
                    else result.Add((RingPoints(r, pos, n), false));
                }
                return result;
            }

            // Rings in the body's own space, tips as n coincident points so they can be interpolated.
            var local = new List<(Vec3[] pts, bool point)>();
            foreach (var r in rings)
            {
                var c = new Vec3(0, r.Y, r.Z);
                if (IsPoint(r)) local.Add((Enumerable.Repeat(c, n).ToArray(), true));
                else local.Add((RingLocalPoints(r, n), false));
            }
            if (smooth > 0 && local.Count >= 2) local = SmoothRings(local, smooth);

            var q = Rotation(spec.RotationDeg);
            var p0 = Vec3.FromArray(pos);
            Vec3 T(Vec3 v) => turned ? p0 + q * v : v + p0;
            foreach (var (pts, point) in local)
            {
                if (point)
                {
                    var c = Vec3.Zero;
                    foreach (var p in pts) c += p;
                    result.Add((new[] { T(c / pts.Length) }, true));
                }
                else result.Add((pts.Select(T).ToArray(), false));
            }
            return result;
        }

        /// <summary>Inserts 2^level - 1 Catmull-Rom rings in every gap (uniform, end rings repeated as tangents).</summary>
        public static List<(Vec3[] pts, bool point)> SmoothRings(List<(Vec3[] pts, bool point)> rings, int level)
        {
            int steps = 1 << level;
            int m = rings.Count;
            var result = new List<(Vec3[] pts, bool point)>();
            for (int i = 0; i < m; i++)
            {
                result.Add(rings[i]);
                if (i == m - 1) break;
                var a = rings[i > 0 ? i - 1 : i].pts; var b = rings[i].pts; var c = rings[i + 1].pts; var d = rings[i + 2 < m ? i + 2 : i + 1].pts;
                int n = b.Length;
                for (int j = 1; j < steps; j++)
                {
                    double t = (double)j / steps;
                    var pts = new Vec3[n];
                    for (int k = 0; k < n; k++) pts[k] = CatmullRom(a[k], b[k], c[k], d[k], t);
                    double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                    result.Add((pts, maxX - minX < PointSize && maxY - minY < PointSize));
                }
            }
            return result;
        }

        public static Vec3 CatmullRom(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (3 * p1 - p0 - 3 * p2 + p3) * t3);
        }

        /// <summary>Builds the mesh. Returns an empty mesh when there are fewer than two rings.</summary>
        public static MeshData Build(FuselageSpec spec)
        {
            var mesh = new MeshData { Name = spec.Name };
            if (spec.Rings.Count < 2) return mesh;
            var rings = PlaneRings(spec);
            int n = ClampSides(spec.Sides);

            var verts = new List<Vec3>();
            var tris = new List<int>();
            // For each ring: the index of its first vertex, and whether it is a single point.
            var start = new int[rings.Count];
            var point = new bool[rings.Count];
            for (int i = 0; i < rings.Count; i++)
            {
                start[i] = verts.Count;
                point[i] = rings[i].point;
                verts.AddRange(rings[i].pts);
            }

            for (int i = 0; i + 1 < rings.Count; i++)
            {
                int a0 = start[i], b0 = start[i + 1];
                if (point[i] && point[i + 1]) continue;
                for (int k = 0; k < n; k++)
                {
                    int k1 = (k + 1) % n;
                    if (point[i]) { tris.Add(a0); tris.Add(b0 + k1); tris.Add(b0 + k); }
                    else if (point[i + 1]) { tris.Add(a0 + k); tris.Add(a0 + k1); tris.Add(b0); }
                    else
                    {
                        tris.Add(a0 + k); tris.Add(a0 + k1); tris.Add(b0 + k1);
                        tris.Add(a0 + k); tris.Add(b0 + k1); tris.Add(b0 + k);
                    }
                }
            }

            // Flat caps on open ends (their own vertices, so edges stay crisp).
            if (!point[0]) AddCap(verts, tris, rings[0].pts, front: true);
            int last = rings.Count - 1;
            if (!point[last]) AddCap(verts, tris, rings[last].pts, front: false);

            mesh.Vertices = new float[verts.Count * 3];
            for (int i = 0; i < verts.Count; i++)
            {
                mesh.Vertices[i * 3] = (float)verts[i].X;
                mesh.Vertices[i * 3 + 1] = (float)verts[i].Y;
                mesh.Vertices[i * 3 + 2] = (float)verts[i].Z;
            }
            mesh.Triangles = tris.ToArray();
            mesh.TriangleMaterials = new int[tris.Count / 3];
            return mesh;
        }

        private static void AddCap(List<Vec3> verts, List<int> tris, Vec3[] ring, bool front)
        {
            int n = ring.Length;
            var c = Vec3.Zero;
            foreach (var p in ring) c += p;
            c /= n;
            int ci = verts.Count;
            verts.Add(c);
            int s = verts.Count;
            verts.AddRange(ring);
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n;
                if (front) { tris.Add(ci); tris.Add(s + k1); tris.Add(s + k); }
                else { tris.Add(s + k); tris.Add(s + k1); tris.Add(ci); }
            }
        }

        /// <summary>Bounds, wetted area and frontal area (largest ring seen front-on) of a lofted body.</summary>
        public static FuselageBody Measure(FuselageSpec spec, MeshData mesh, int meshIndex)
        {
            var body = MeasureMesh(mesh, meshIndex);
            body.Name = spec.Name;
            body.SkinThickness = spec.SkinThickness;
            double best = 0;
            if (spec.Smooth <= 0 && !IsTurned(spec.RotationDeg))
            {
                int n = ClampSides(spec.Sides);
                foreach (var r in spec.Rings)
                    if (!IsPoint(r)) best = System.Math.Max(best, PolygonAreaXY(RingPoints(r, spec.Position ?? new double[3], n)));
            }
            else
                foreach (var (pts, point) in PlaneRings(spec))
                    if (!point) best = System.Math.Max(best, PolygonAreaXY(pts));
            body.FrontalArea = best;
            return body;
        }

        /// <summary>Bounds and wetted area of any mesh (frontal area left at 0).</summary>
        public static FuselageBody MeasureMesh(MeshData mesh, int meshIndex)
        {
            var body = new FuselageBody { Name = mesh.Name, MeshIndex = meshIndex };
            var v = mesh.Vertices;
            if (v.Length < 3) return body;
            var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                var p = new Vec3(v[i], v[i + 1], v[i + 2]);
                min = Vec3.Min(min, p); max = Vec3.Max(max, p);
            }
            body.BoundsMin = min.ToArray();
            body.BoundsMax = max.ToArray();
            double wetted = 0;
            var t = mesh.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var a = Vertex(v, t[i]); var b = Vertex(v, t[i + 1]); var c = Vertex(v, t[i + 2]);
                wetted += Vec3.Cross(b - a, c - a).Length / 2;
            }
            body.WettedArea = wetted;
            return body;
        }

        /// <summary>Area seen front-on (along z) of a closed mesh: half the sum of every triangle's area projected on x-y.</summary>
        public static double ProjectedFrontalArea(MeshData mesh)
        {
            var v = mesh.Vertices; var t = mesh.Triangles;
            double sum = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var a = Vertex(v, t[i]); var b = Vertex(v, t[i + 1]); var c = Vertex(v, t[i + 2]);
                sum += System.Math.Abs(Vec3.Cross(b - a, c - a).Z) / 2;
            }
            return sum / 2;
        }

        internal static Vec3 Vertex(float[] v, int i) => new Vec3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

        /// <summary>Area of a closed polygon seen along the nose axis (shoelace on x and y).</summary>
        public static double PolygonAreaXY(IList<Vec3> ring)
        {
            double area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i]; var q = ring[(i + 1) % ring.Count];
                area += p.X * q.Y - q.X * p.Y;
            }
            return System.Math.Abs(area) / 2;
        }

        /// <summary>A copy of a mesh reflected across x = 0, with triangles rewound so they still face outwards.</summary>
        public static MeshData MirrorX(MeshData m, string name)
        {
            var v = (float[])m.Vertices.Clone();
            for (int i = 0; i < v.Length; i += 3) v[i] = -v[i];
            var t = (int[])m.Triangles.Clone();
            for (int i = 0; i + 2 < t.Length; i += 3) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
            return new MeshData { Name = name, Vertices = v, Triangles = t, TriangleMaterials = (int[])m.TriangleMaterials.Clone(), Materials = (int[]?)m.Materials?.Clone(), Role = m.Role };
        }

        /// <summary>A copy of a mesh scaled about a pivot and moved.</summary>
        public static MeshData Transform(MeshData m, Vec3 pivot, double scale, Vec3 offset, string name)
        {
            var v = new float[m.Vertices.Length];
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                var p = pivot + (new Vec3(m.Vertices[i], m.Vertices[i + 1], m.Vertices[i + 2]) - pivot) * scale + offset;
                v[i] = (float)p.X; v[i + 1] = (float)p.Y; v[i + 2] = (float)p.Z;
            }
            return new MeshData
            {
                Name = name, Vertices = v, Triangles = (int[])m.Triangles.Clone(),
                TriangleMaterials = m.TriangleMaterials.Length == m.Triangles.Length / 3 ? (int[])m.TriangleMaterials.Clone() : new int[m.Triangles.Length / 3],
                Materials = (int[]?)m.Materials?.Clone(), Role = m.Role,
            };
        }

        /// <summary>A copy of a mesh scaled and turned about a pivot, then moved: p' = pivot + q (s (p - pivot)) + offset.</summary>
        public static MeshData Transform(MeshData m, Vec3 pivot, double scale, Quat turn, Vec3 offset, string name)
        {
            var v = new float[m.Vertices.Length];
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                var p = pivot + turn * ((new Vec3(m.Vertices[i], m.Vertices[i + 1], m.Vertices[i + 2]) - pivot) * scale) + offset;
                v[i] = (float)p.X; v[i + 1] = (float)p.Y; v[i + 2] = (float)p.Z;
            }
            return new MeshData
            {
                Name = name, Vertices = v, Triangles = (int[])m.Triangles.Clone(),
                TriangleMaterials = m.TriangleMaterials.Length == m.Triangles.Length / 3 ? (int[])m.TriangleMaterials.Clone() : new int[m.Triangles.Length / 3],
                Materials = (int[]?)m.Materials?.Clone(), Role = m.Role,
            };
        }

        /// <summary>
        /// True when the mesh is its own mirror image across x = 0 (every vertex has a reflected partner within 2 mm),
        /// so a mirrored copy would only overlap it.
        /// </summary>
        public static bool IsSelfMirrored(MeshData m)
        {
            var v = m.Vertices;
            if (v.Length < 3) return true;
            const double grid = 500; // 2 mm cells
            var cells = new HashSet<(long, long, long)>();
            for (int i = 0; i + 2 < v.Length; i += 3)
                cells.Add(((long)System.Math.Round(v[i] * grid), (long)System.Math.Round(v[i + 1] * grid), (long)System.Math.Round(v[i + 2] * grid)));
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                long x = (long)System.Math.Round(-v[i] * grid), y = (long)System.Math.Round(v[i + 1] * grid), z = (long)System.Math.Round(v[i + 2] * grid);
                if (!cells.Contains((x, y, z)) && !cells.Contains((x - 1, y, z)) && !cells.Contains((x + 1, y, z))) return false;
            }
            return true;
        }

        /// <summary>The centre of a mesh's bounding box (the pivot used for scaling fixed meshes).</summary>
        public static Vec3 BoundsCenter(MeshData m)
        {
            var b = MeasureMesh(m, 0);
            return (Vec3.FromArray(b.BoundsMin) + Vec3.FromArray(b.BoundsMax)) / 2;
        }
    }
}
