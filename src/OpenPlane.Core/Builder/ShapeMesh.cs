using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Builder
{
    /// <summary>A polygon mesh being edited: corners and polygons (three or more corner indices, wound outwards).</summary>
    public sealed class PolyMesh
    {
        public List<Vec3> Vertices { get; set; } = new List<Vec3>();
        public List<int[]> Faces { get; set; } = new List<int[]>();

        public PolyMesh Clone() => new PolyMesh { Vertices = Vertices.ToList(), Faces = Faces.Select(f => (int[])f.Clone()).ToList() };

        /// <summary>Reads a shape's mesh. Throws <see cref="MeshOpException"/> when it is damaged.</summary>
        public static PolyMesh FromShape(ShapeSpec s)
        {
            var m = new PolyMesh();
            foreach (var v in s.Vertices ?? new List<double[]>())
            {
                if (v == null || v.Length < 3 || v.Take(3).Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                    throw new MeshOpException("This shape is damaged: one of its points has no proper position.");
                m.Vertices.Add(new Vec3(v[0], v[1], v[2]));
            }
            foreach (var f in s.Faces ?? new List<int[]>())
            {
                if (f == null || f.Length < 3) throw new MeshOpException("This shape is damaged: a face has fewer than three corners.");
                if (f.Any(i => i < 0 || i >= m.Vertices.Count)) throw new MeshOpException("This shape is damaged: a face points at a corner that doesn't exist.");
                m.Faces.Add((int[])f.Clone());
            }
            return m;
        }

        /// <summary>Writes the mesh into a shape (rounded to 0.01 mm, which keeps files small).</summary>
        public void WriteTo(ShapeSpec s)
        {
            s.Vertices = Vertices.Select(v => new[] { R(v.X), R(v.Y), R(v.Z) }).ToList();
            s.Faces = Faces.Select(f => (int[])f.Clone()).ToList();
        }

        private static double R(double v) { var r = System.Math.Round(v, 5); return r == 0 ? 0 : r; }
    }

    /// <summary>A mesh edit the player asked for that can't be done; the message is written for them.</summary>
    public sealed class MeshOpException : Exception
    {
        public MeshOpException(string message) : base(message) { }
    }

    /// <summary>One corner the page slides after an edit: position = Origin + Dir * amount.</summary>
    public sealed class MotionItem
    {
        public int V { get; set; }
        public double[] Origin { get; set; } = new double[3];
        public double[] Dir { get; set; } = new double[3];
    }

    /// <summary>What a mesh edit made: the new mesh, what is selected afterwards, and how the page may slide new corners.</summary>
    public sealed class MeshOpResult
    {
        public MeshOpResult(PolyMesh mesh) { Mesh = mesh; }
        public PolyMesh Mesh { get; }
        public List<int> Verts { get; set; } = new List<int>();
        public List<int[]> Edges { get; set; } = new List<int[]>();
        public List<int> Faces { get; set; } = new List<int>();
        public List<MotionItem>? Motion { get; set; }
        public double? MaxAmount { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>
    /// Free-form shape parts: primitives, Blender-style mesh edits (extrude, inset, loop cut, merge, delete), Catmull-Clark
    /// smoothing, .obj import and turning a shape into a saved mesh. Everything works in the shape's own space; faces are
    /// wound so cross(b - a, c - a) at the first corner points out of the shape, and closed shapes stay closed.
    /// The page's live preview (geometry.js catmullClark, triangulate, shapeMesh) follows the same rules.
    /// </summary>
    public static class ShapeMesh
    {
        public const int WarnTriangles = 20000;
        public const int MaxTriangles = 200000;
        public static readonly string[] PrimitiveKinds = { "box", "cylinder", "sphere", "cone", "capsule" };

        // ------------------------------------------------------------------ primitives

        /// <summary>Default size (m, x y z) of each primitive: about the size of a canopy, pod or fairing.</summary>
        public static double[] DefaultSize(string kind)
        {
            switch (kind)
            {
                case "box": return new[] { 1.0, 0.6, 1.6 };
                case "cylinder": return new[] { 0.8, 0.8, 1.6 };
                case "sphere": return new[] { 1.0, 1.0, 1.0 };
                case "cone": return new[] { 0.8, 0.8, 1.2 };
                case "capsule": return new[] { 0.7, 0.7, 1.6 };
                default: throw new MeshOpException($"OpenPlane doesn't know the shape \"{kind}\". Pick a box, cylinder, sphere, cone or capsule.");
            }
        }

        /// <summary>A closed primitive filling a box of the given size (m), centred on the origin, long axis along z.</summary>
        public static PolyMesh Primitive(string kind, double[]? size = null, int segments = 16)
        {
            var sz = size != null && size.Length >= 3 && size.All(v => v > 0 && !double.IsInfinity(v)) ? size : DefaultSize(kind);
            double sx = sz[0], sy = sz[1], szz = sz[2];
            int n = System.Math.Max(3, System.Math.Min(64, segments));
            var m = new PolyMesh();
            switch (kind)
            {
                case "box":
                {
                    for (int i = 0; i < 8; i++) m.Vertices.Add(new Vec3((i & 1) == 0 ? -sx / 2 : sx / 2, (i & 2) == 0 ? -sy / 2 : sy / 2, (i & 4) == 0 ? -szz / 2 : szz / 2));
                    m.Faces.Add(new[] { 1, 3, 7, 5 }); m.Faces.Add(new[] { 0, 4, 6, 2 });
                    m.Faces.Add(new[] { 2, 6, 7, 3 }); m.Faces.Add(new[] { 0, 1, 5, 4 });
                    m.Faces.Add(new[] { 4, 5, 7, 6 }); m.Faces.Add(new[] { 0, 2, 3, 1 });
                    break;
                }
                case "cylinder":
                {
                    for (int r = 0; r < 2; r++)
                        for (int k = 0; k < n; k++) m.Vertices.Add(Around(k, n, sx / 2, sy / 2, r == 0 ? -szz / 2 : szz / 2));
                    for (int k = 0; k < n; k++) m.Faces.Add(new[] { k, (k + 1) % n, n + (k + 1) % n, n + k });
                    m.Faces.Add(Enumerable.Range(0, n).ToArray());
                    m.Faces.Add(Enumerable.Range(n, n).ToArray());
                    break;
                }
                case "cone":
                {
                    for (int k = 0; k < n; k++) m.Vertices.Add(Around(k, n, sx / 2, sy / 2, -szz / 2));
                    m.Vertices.Add(new Vec3(0, 0, szz / 2));
                    for (int k = 0; k < n; k++) m.Faces.Add(new[] { k, (k + 1) % n, n });
                    m.Faces.Add(Enumerable.Range(0, n).ToArray());
                    break;
                }
                case "sphere":
                {
                    int rings = System.Math.Max(3, n / 2);
                    m.Vertices.Add(new Vec3(0, 0, szz / 2));
                    for (int i = 1; i < rings; i++)
                    {
                        double th = System.Math.PI * i / rings;
                        for (int k = 0; k < n; k++) m.Vertices.Add(Around(k, n, sx / 2 * System.Math.Sin(th), sy / 2 * System.Math.Sin(th), szz / 2 * System.Math.Cos(th)));
                    }
                    int south = m.Vertices.Count;
                    m.Vertices.Add(new Vec3(0, 0, -szz / 2));
                    RingFaces(m, 0, 1, n, rings - 1, south);
                    break;
                }
                case "capsule":
                {
                    double rx = sx / 2, ry = sy / 2, cap = System.Math.Min(System.Math.Min(rx, ry), szz / 2);
                    double half = szz / 2 - cap;
                    int capRings = System.Math.Max(2, n / 4);
                    m.Vertices.Add(new Vec3(0, 0, szz / 2));
                    var levels = new List<(double f, double z)>();
                    for (int j = 1; j <= capRings; j++) { double ph = System.Math.PI / 2 * j / capRings; levels.Add((System.Math.Sin(ph), half + cap * System.Math.Cos(ph))); }
                    for (int j = capRings; j >= 1; j--) { double ph = System.Math.PI / 2 * j / capRings; levels.Add((System.Math.Sin(ph), -half - cap * System.Math.Cos(ph))); }
                    foreach (var (f, z) in levels)
                        for (int k = 0; k < n; k++) m.Vertices.Add(Around(k, n, rx * f, ry * f, z));
                    int south = m.Vertices.Count;
                    m.Vertices.Add(new Vec3(0, 0, -szz / 2));
                    RingFaces(m, 0, 1, n, levels.Count, south);
                    break;
                }
                default: DefaultSize(kind); break;
            }
            OrientConvex(m);
            return m;
        }

        private static Vec3 Around(int k, int n, double rx, double ry, double z)
        {
            double t = 2 * System.Math.PI * k / n;
            return new Vec3(rx * System.Math.Sin(t), ry * System.Math.Cos(t), z);
        }

        /// <summary>Fans to a north pole, quads between rings, fan to a south pole.</summary>
        private static void RingFaces(PolyMesh m, int north, int first, int n, int ringCount, int south)
        {
            for (int k = 0; k < n; k++) m.Faces.Add(new[] { north, first + k, first + (k + 1) % n });
            for (int r = 0; r + 1 < ringCount; r++)
            {
                int a = first + r * n, b = first + (r + 1) * n;
                for (int k = 0; k < n; k++) m.Faces.Add(new[] { a + k, b + k, b + (k + 1) % n, a + (k + 1) % n });
            }
            int last = first + (ringCount - 1) * n;
            for (int k = 0; k < n; k++) m.Faces.Add(new[] { last + k, south, last + (k + 1) % n });
        }

        /// <summary>For convex shapes: turns every face to point away from the centre.</summary>
        private static void OrientConvex(PolyMesh m)
        {
            var c = Vec3.Zero;
            foreach (var v in m.Vertices) c += v;
            c /= System.Math.Max(1, m.Vertices.Count);
            for (int f = 0; f < m.Faces.Count; f++)
            {
                var face = m.Faces[f];
                var centroid = Centroid(m, face);
                if (Vec3.Dot(Newell(m, face), centroid - c) < 0) m.Faces[f] = Reversed(face);
            }
        }

        // ------------------------------------------------------------------ measuring

        /// <summary>Area-weighted normal of a polygon (length = area).</summary>
        public static Vec3 Newell(PolyMesh m, int[] face)
        {
            var n = Vec3.Zero;
            var a = m.Vertices[face[0]];
            for (int k = 1; k + 1 < face.Length; k++)
                n += Vec3.Cross(m.Vertices[face[k]] - a, m.Vertices[face[k + 1]] - a);
            return n / 2;
        }

        public static Vec3 Centroid(PolyMesh m, int[] face)
        {
            var c = Vec3.Zero;
            foreach (var i in face) c += m.Vertices[i];
            return c / face.Length;
        }

        /// <summary>Volume enclosed (positive when the faces point outwards).</summary>
        public static double SignedVolume(PolyMesh m, IEnumerable<int>? faces = null)
        {
            double vol = 0;
            foreach (var fi in faces ?? Enumerable.Range(0, m.Faces.Count))
            {
                var f = m.Faces[fi];
                var a = m.Vertices[f[0]];
                for (int k = 1; k + 1 < f.Length; k++)
                    vol += Vec3.Dot(a, Vec3.Cross(m.Vertices[f[k]], m.Vertices[f[k + 1]])) / 6;
            }
            return vol;
        }

        /// <summary>Edges used by one face only (or by faces pointing the same way): where the mesh is open.</summary>
        public static int OpenEdges(PolyMesh m)
        {
            var directed = new Dictionary<(int, int), int>();
            foreach (var f in m.Faces)
                for (int k = 0; k < f.Length; k++)
                {
                    var e = (f[k], f[(k + 1) % f.Length]);
                    directed[e] = directed.TryGetValue(e, out var c) ? c + 1 : 1;
                }
            return directed.Count(kv => kv.Value != 1 || !directed.ContainsKey((kv.Key.Item2, kv.Key.Item1)));
        }

        private static int[] Reversed(int[] f)
        {
            var r = new int[f.Length];
            r[0] = f[0];
            for (int k = 1; k < f.Length; k++) r[k] = f[f.Length - k];
            return r;
        }

        private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>Drops corners no face uses; returns old index → new index (-1 when dropped).</summary>
        public static int[] Compact(PolyMesh m)
        {
            var used = new bool[m.Vertices.Count];
            foreach (var f in m.Faces) foreach (var i in f) used[i] = true;
            var map = new int[m.Vertices.Count];
            var verts = new List<Vec3>();
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i]) { map[i] = -1; continue; }
                map[i] = verts.Count;
                verts.Add(m.Vertices[i]);
            }
            m.Vertices = verts;
            for (int f = 0; f < m.Faces.Count; f++) m.Faces[f] = m.Faces[f].Select(i => map[i]).ToArray();
            return map;
        }

        // ------------------------------------------------------------------ edits

        /// <summary>Region extrude: the faces get their own corners, joined to the rest by side quads. Distance 0; the page moves them.</summary>
        public static MeshOpResult ExtrudeFaces(PolyMesh source, IEnumerable<int> faces)
        {
            var m = source.Clone();
            var sel = new HashSet<int>(faces.Where(f => f >= 0 && f < m.Faces.Count));
            if (sel.Count == 0) throw new MeshOpException("Select some faces to extrude.");
            var order = sel.OrderBy(f => f).ToList();
            var dirSel = new HashSet<(int, int)>();
            foreach (var f in order) { var c = m.Faces[f]; for (int k = 0; k < c.Length; k++) dirSel.Add((c[k], c[(k + 1) % c.Length])); }
            var boundary = new List<(int a, int b)>();
            foreach (var f in order)
            {
                var c = m.Faces[f];
                for (int k = 0; k < c.Length; k++)
                {
                    int a = c[k], b = c[(k + 1) % c.Length];
                    if (!dirSel.Contains((b, a))) boundary.Add((a, b));
                }
            }
            if (boundary.Count == 0) throw new MeshOpException("Extrude needs some faces left unselected.");

            var normal = Vec3.Zero;
            foreach (var f in order) normal += Newell(m, m.Faces[f]);
            normal = normal.Length > 1e-12 ? normal.Normalized : new Vec3(0, 1, 0);

            var dup = new Dictionary<int, int>();
            var dupOrder = new List<int>();
            foreach (var f in order)
                foreach (var c in m.Faces[f])
                    if (!dup.ContainsKey(c)) { dup[c] = m.Vertices.Count; dupOrder.Add(c); m.Vertices.Add(m.Vertices[c]); }
            foreach (var f in order) m.Faces[f] = m.Faces[f].Select(c => dup[c]).ToArray();
            foreach (var (a, b) in boundary) m.Faces.Add(new[] { a, b, dup[b], dup[a] });

            var map = Compact(m);
            var r = new MeshOpResult(m) { Faces = order, Motion = new List<MotionItem>() };
            foreach (var c in dupOrder)
            {
                int v = map[dup[c]];
                r.Verts.Add(v);
                r.Motion.Add(new MotionItem { V = v, Origin = m.Vertices[v].ToArray(), Dir = normal.ToArray() });
            }
            r.Edges = FaceEdges(m, order);
            return r;
        }

        /// <summary>Individual inset: each face gets a smaller copy inside it, joined by a ring of quads.</summary>
        public static MeshOpResult InsetFaces(PolyMesh source, IEnumerable<int> faces)
        {
            var m = source.Clone();
            var order = faces.Where(f => f >= 0 && f < m.Faces.Count).Distinct().OrderBy(f => f).ToList();
            if (order.Count == 0) throw new MeshOpException("Select some faces to inset.");
            var r = new MeshOpResult(m) { Faces = order, Motion = new List<MotionItem>() };
            double max = double.MaxValue;
            foreach (var f in order)
            {
                var c = m.Faces[f];
                int n = c.Length;
                var pts = c.Select(i => m.Vertices[i]).ToArray();
                var nrm = Newell(m, c).Normalized;
                var centroid = Centroid(m, c);
                var inner = new int[n];
                for (int k = 0; k < n; k++) { inner[k] = m.Vertices.Count; m.Vertices.Add(pts[k]); }
                m.Faces[f] = inner;
                for (int k = 0; k < n; k++) m.Faces.Add(new[] { c[k], c[(k + 1) % n], inner[(k + 1) % n], inner[k] });
                for (int k = 0; k < n; k++)
                {
                    var e1 = (pts[(k + 1) % n] - pts[k]).Normalized;
                    var e0 = (pts[k] - pts[(k + n - 1) % n]).Normalized;
                    var m1 = Vec3.Cross(nrm, e1); var m0 = Vec3.Cross(nrm, e0);
                    double denom = 1 + Vec3.Dot(m0, m1);
                    var dir = denom > 1e-6 ? (m0 + m1) / denom : m1;
                    r.Verts.Add(inner[k]);
                    r.Motion.Add(new MotionItem { V = inner[k], Origin = pts[k].ToArray(), Dir = dir.ToArray() });
                    double d = Vec3.Dot(centroid - pts[k], m1);
                    if (d > 1e-9) max = System.Math.Min(max, d);
                }
            }
            r.MaxAmount = max == double.MaxValue ? 0 : max;
            r.Edges = FaceEdges(m, order);
            return r;
        }

        /// <summary>Loop cut: walks the ring of quads across the edge a-b and cuts it with new edge loops.</summary>
        public static MeshOpResult LoopCut(PolyMesh source, int a, int b, int cuts = 1, double factor = 0.5)
        {
            var m = source.Clone();
            cuts = System.Math.Max(1, System.Math.Min(32, cuts));
            var edgeFaces = new Dictionary<long, List<int>>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                var c = m.Faces[f];
                for (int k = 0; k < c.Length; k++)
                {
                    long key = Key(c[k], c[(k + 1) % c.Length]);
                    if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>();
                    list.Add(f);
                }
            }
            if (a == b || !edgeFaces.ContainsKey(Key(a, b))) throw new MeshOpException("Pick an edge of the shape to cut across.");

            var ring = new List<(int from, int to)> { (a, b) };
            var visited = new HashSet<long> { Key(a, b) };
            var crossed = new List<(int face, int at)>();
            var crossedSet = new HashSet<int>();
            void Walk(int from, int to)
            {
                while (true)
                {
                    int q = -1, at = -1;
                    foreach (var f in edgeFaces[Key(from, to)])
                    {
                        if (crossedSet.Contains(f) || m.Faces[f].Length != 4) continue;
                        q = f; break;
                    }
                    if (q < 0) return;
                    var c = m.Faces[q];
                    for (int i = 0; i < 4; i++)
                        if ((c[i] == from && c[(i + 1) % 4] == to) || (c[i] == to && c[(i + 1) % 4] == from)) { at = i; break; }
                    crossedSet.Add(q);
                    crossed.Add((q, at));
                    (int, int) next = c[at] == from ? (c[(at + 3) % 4], c[(at + 2) % 4]) : (c[(at + 2) % 4], c[(at + 3) % 4]);
                    long nk = Key(next.Item1, next.Item2);
                    if (visited.Contains(nk)) return;
                    visited.Add(nk);
                    ring.Add(next);
                    (from, to) = next;
                }
            }
            Walk(a, b);
            Walk(a, b);
            if (crossed.Count == 0) throw new MeshOpException("A loop cut needs four-sided faces on both sides of the edge.");

            var ts = cuts == 1 ? new[] { System.Math.Max(0.02, System.Math.Min(0.98, factor)) } : Enumerable.Range(1, cuts).Select(j => (double)j / (cuts + 1)).ToArray();
            var cutVerts = new Dictionary<(int, int), int[]>();
            var r = new MeshOpResult(m);
            foreach (var (from, to) in ring)
            {
                var ids = new int[ts.Length];
                for (int j = 0; j < ts.Length; j++)
                {
                    ids[j] = m.Vertices.Count;
                    m.Vertices.Add(Vec3.Lerp(m.Vertices[from], m.Vertices[to], ts[j]));
                    r.Verts.Add(ids[j]);
                }
                cutVerts[(from, to)] = ids;
            }
            int[]? Points(int x, int y)
            {
                if (cutVerts.TryGetValue((x, y), out var p)) return p;
                if (cutVerts.TryGetValue((y, x), out var q)) return Enumerable.Reverse(q).ToArray();
                return null;
            }

            foreach (var (face, at) in crossed)
            {
                var c = m.Faces[face];
                int p0 = c[at], p1 = c[(at + 1) % 4], p2 = c[(at + 2) % 4], p3 = c[(at + 3) % 4];
                var A = new List<int> { p0 }; A.AddRange(Points(p0, p1)!); A.Add(p1);
                var B = new List<int> { p3 }; B.AddRange(Points(p3, p2)!); B.Add(p2);
                for (int j = 0; j + 1 < A.Count; j++)
                {
                    var quad = new[] { A[j], A[j + 1], B[j + 1], B[j] };
                    if (j == 0) m.Faces[face] = quad; else m.Faces.Add(quad);
                    if (j > 0) r.Edges.Add(new[] { A[j], B[j] });
                }
            }
            // Faces next to the ring that weren't cut (end triangles, n-gon caps) get the new corners too, so nothing opens.
            for (int f = 0; f < m.Faces.Count; f++)
            {
                if (crossedSet.Contains(f)) continue;
                var c = m.Faces[f];
                List<int>? grown = null;
                for (int k = 0; k < c.Length; k++)
                {
                    int x = c[k], y = c[(k + 1) % c.Length];
                    var p = visited.Contains(Key(x, y)) ? Points(x, y) : null;
                    if (p != null && grown == null) { grown = c.Take(k).ToList(); }
                    if (grown != null) { grown.Add(x); if (p != null) grown.AddRange(p); }
                }
                if (grown != null) m.Faces[f] = grown.ToArray();
            }
            return r;
        }

        /// <summary>Collapses the selected corners into one: at their centre, or at the first or last one picked.</summary>
        public static MeshOpResult Merge(PolyMesh source, IList<int> verts, string mode = "center")
        {
            var m = source.Clone();
            var list = verts.Where(v => v >= 0 && v < m.Vertices.Count).Distinct().ToList();
            if (list.Count < 2) throw new MeshOpException("Select two or more points to merge.");
            Vec3 target;
            if (mode == "first") target = m.Vertices[list[0]];
            else if (mode == "last") target = m.Vertices[list[list.Count - 1]];
            else { var c = Vec3.Zero; foreach (var v in list) c += m.Vertices[v]; target = c / list.Count; }
            int keep = list[0];
            m.Vertices[keep] = target;
            var remap = Enumerable.Range(0, m.Vertices.Count).ToArray();
            foreach (var v in list) remap[v] = keep;
            int dropped = Rewrite(m, remap);
            var map = Compact(m);
            var r = new MeshOpResult(m) { Verts = new List<int> { map[keep] } };
            r.Note = dropped > 0 ? $"Merged {list.Count} points; {dropped} face{(dropped == 1 ? "" : "s")} collapsed and went away." : $"Merged {list.Count} points.";
            return r;
        }

        /// <summary>Merges every group of corners closer together than the distance (m).</summary>
        public static MeshOpResult MergeByDistance(PolyMesh source, double distance, IList<int>? only = null)
        {
            var m = source.Clone();
            double d = System.Math.Max(1e-9, distance);
            int n = m.Vertices.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var allowed = only != null && only.Count > 0 ? new HashSet<int>(only) : null;
            var grid = new Dictionary<(long, long, long), List<int>>();
            (long, long, long) Cell(Vec3 p) => ((long)System.Math.Floor(p.X / d), (long)System.Math.Floor(p.Y / d), (long)System.Math.Floor(p.Z / d));
            for (int i = 0; i < n; i++)
            {
                if (allowed != null && !allowed.Contains(i)) continue;
                var p = m.Vertices[i]; var c = Cell(p);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                            if (grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var near))
                                foreach (var j in near)
                                    if (Vec3.Distance(p, m.Vertices[j]) <= d) { int a = Find(i), b = Find(j); if (a != b) parent[System.Math.Max(a, b)] = System.Math.Min(a, b); }
                if (!grid.TryGetValue(c, out var cell)) grid[c] = cell = new List<int>();
                cell.Add(i);
            }
            var sums = new Dictionary<int, (Vec3 sum, int count)>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                sums[root] = sums.TryGetValue(root, out var s) ? (s.sum + m.Vertices[i], s.count + 1) : (m.Vertices[i], 1);
            }
            int merged = 0;
            var remap = new int[n];
            for (int i = 0; i < n; i++) { remap[i] = Find(i); if (remap[i] != i) merged++; }
            foreach (var kv in sums) if (kv.Value.count > 1) m.Vertices[kv.Key] = kv.Value.sum / kv.Value.count;
            Rewrite(m, remap);
            Compact(m);
            return new MeshOpResult(m) { Note = merged == 0 ? "No points were close enough to merge." : $"Merged {merged} point{(merged == 1 ? "" : "s")} into their neighbours." };
        }

        /// <summary>Renames corners in every face; drops repeated corners and faces that collapse. Returns how many faces went.</summary>
        private static int Rewrite(PolyMesh m, int[] remap)
        {
            var faces = new List<int[]>();
            int dropped = 0;
            foreach (var f in m.Faces)
            {
                var c = new List<int>();
                foreach (var i in f.Select(i => remap[i]))
                    if (c.Count == 0 || c[c.Count - 1] != i) c.Add(i);
                while (c.Count > 1 && c[0] == c[c.Count - 1]) c.RemoveAt(c.Count - 1);
                if (c.Count < 3 || c.Distinct().Count() != c.Count) { dropped++; continue; }
                faces.Add(c.ToArray());
            }
            m.Faces = faces;
            return dropped;
        }

        /// <summary>Deletes corners, edges or faces (faces that use deleted corners or edges go too).</summary>
        public static MeshOpResult Delete(PolyMesh source, string what, IEnumerable<int>? verts, IEnumerable<int[]>? edges, IEnumerable<int>? faces)
        {
            var m = source.Clone();
            var keep = new List<int[]>();
            int before = m.Faces.Count;
            if (what == "faces")
            {
                var set = new HashSet<int>(faces ?? Enumerable.Empty<int>());
                if (set.Count == 0) throw new MeshOpException("Select some faces to delete.");
                for (int f = 0; f < m.Faces.Count; f++) if (!set.Contains(f)) keep.Add(m.Faces[f]);
            }
            else if (what == "edges")
            {
                var set = new HashSet<long>((edges ?? Enumerable.Empty<int[]>()).Where(e => e != null && e.Length >= 2).Select(e => Key(e[0], e[1])));
                if (set.Count == 0) throw new MeshOpException("Select some edges to delete.");
                foreach (var f in m.Faces)
                {
                    bool hit = false;
                    for (int k = 0; k < f.Length && !hit; k++) hit = set.Contains(Key(f[k], f[(k + 1) % f.Length]));
                    if (!hit) keep.Add(f);
                }
            }
            else
            {
                var set = new HashSet<int>(verts ?? Enumerable.Empty<int>());
                if (set.Count == 0) throw new MeshOpException("Select some points to delete.");
                foreach (var f in m.Faces) if (!f.Any(set.Contains)) keep.Add(f);
            }
            m.Faces = keep;
            Compact(m);
            var r = new MeshOpResult(m);
            int gone = before - keep.Count;
            r.Note = m.Faces.Count == 0 ? "Everything was deleted; the shape is empty now."
                : OpenEdges(m) > 0 ? $"Deleted {gone} face{(gone == 1 ? "" : "s")}. The shape now has a hole; it still counts as body. Undo, or close it by merging points."
                : $"Deleted {gone} face{(gone == 1 ? "" : "s")}.";
            return r;
        }

        /// <summary>One permanent level of Catmull-Clark smoothing.</summary>
        public static MeshOpResult SubdivideApply(PolyMesh source)
        {
            if (source.Faces.Count == 0) throw new MeshOpException("There's nothing to smooth.");
            var (v, f) = CatmullClark(source.Vertices, source.Faces);
            var m = new PolyMesh { Vertices = v, Faces = f };
            if (m.Faces.Count * 2 > MaxTriangles) throw new MeshOpException("That would make the shape too detailed. Smooth it with the Subdivision setting instead.");
            return new MeshOpResult(m) { Note = $"Smoothed once: now {m.Faces.Count:N0} faces." };
        }

        /// <summary>Makes every face point the same way, then outwards (per connected piece, by its enclosed volume).</summary>
        public static MeshOpResult RecalculateOutward(PolyMesh source)
        {
            var m = source.Clone();
            int flipped = OrientOutward(m);
            return new MeshOpResult(m) { Note = flipped == 0 ? "Every face already points outwards." : $"Turned {flipped} face{(flipped == 1 ? "" : "s")} to point outwards." };
        }

        public static int OrientOutward(PolyMesh m)
        {
            int nf = m.Faces.Count;
            var edgeFaces = new Dictionary<long, List<int>>();
            for (int f = 0; f < nf; f++)
            {
                var c = m.Faces[f];
                for (int k = 0; k < c.Length; k++)
                {
                    long key = Key(c[k], c[(k + 1) % c.Length]);
                    if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>();
                    list.Add(f);
                }
            }
            bool Has(int f, int a, int b)
            {
                var c = m.Faces[f];
                for (int k = 0; k < c.Length; k++) if (c[k] == a && c[(k + 1) % c.Length] == b) return true;
                return false;
            }
            var flip = new bool[nf];
            var seen = new bool[nf];
            int changed = 0;
            for (int s = 0; s < nf; s++)
            {
                if (seen[s]) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(s); seen[s] = true;
                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    component.Add(f);
                    var c = m.Faces[f];
                    for (int k = 0; k < c.Length; k++)
                    {
                        int u = c[k], v = c[(k + 1) % c.Length];
                        if (flip[f]) (u, v) = (v, u);
                        foreach (var g in edgeFaces[Key(u, v)])
                        {
                            if (seen[g]) continue;
                            seen[g] = true;
                            flip[g] = Has(g, u, v);
                            queue.Enqueue(g);
                        }
                    }
                }
                foreach (var f in component) if (flip[f]) m.Faces[f] = Reversed(m.Faces[f]);
                if (SignedVolume(m, component) < 0)
                    foreach (var f in component) { m.Faces[f] = Reversed(m.Faces[f]); flip[f] = !flip[f]; }
                changed += component.Count(f => flip[f]);
            }
            return changed;
        }

        public static MeshOpResult Flip(PolyMesh source, IEnumerable<int> faces)
        {
            var m = source.Clone();
            var list = faces.Where(f => f >= 0 && f < m.Faces.Count).Distinct().ToList();
            if (list.Count == 0) throw new MeshOpException("Select some faces to flip.");
            foreach (var f in list) m.Faces[f] = Reversed(m.Faces[f]);
            return new MeshOpResult(m) { Faces = list, Edges = FaceEdges(m, list), Verts = list.SelectMany(f => m.Faces[f]).Distinct().ToList() };
        }

        /// <summary>The edges of some faces, each once.</summary>
        public static List<int[]> FaceEdges(PolyMesh m, IEnumerable<int> faces)
        {
            var seen = new HashSet<long>();
            var result = new List<int[]>();
            foreach (var f in faces)
            {
                var c = m.Faces[f];
                for (int k = 0; k < c.Length; k++)
                {
                    int a = c[k], b = c[(k + 1) % c.Length];
                    if (seen.Add(Key(a, b))) result.Add(new[] { a, b });
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ smoothing and triangles

        /// <summary>
        /// One Catmull-Clark step. Output corners: the moved original corners (same indices), then one per edge in the
        /// order edges are first met (faces in order, corners in order), then one per face. Output faces: for each face and
        /// corner k, [corner k, edge (k, k+1), face, edge (k-1, k)]. Edges with other than two faces are treated as borders:
        /// their point is the midpoint, and a corner on exactly two border edges moves to (n1 + 6v + n2) / 8 (more stays put).
        /// </summary>
        public static (List<Vec3> vertices, List<int[]> faces) CatmullClark(IList<Vec3> verts, IList<int[]> faces)
        {
            int nv = verts.Count, nf = faces.Count;
            var facePts = new Vec3[nf];
            for (int f = 0; f < nf; f++)
            {
                var c = Vec3.Zero;
                foreach (var i in faces[f]) c += verts[i];
                facePts[f] = c / faces[f].Length;
            }
            var edgeIndex = new Dictionary<long, int>();
            var edgeA = new List<int>(); var edgeB = new List<int>();
            var edgeFaceCount = new List<int>(); var edgeFaceSum = new List<Vec3>();
            for (int f = 0; f < nf; f++)
            {
                var c = faces[f];
                for (int k = 0; k < c.Length; k++)
                {
                    int a = c[k], b = c[(k + 1) % c.Length];
                    long key = Key(a, b);
                    if (!edgeIndex.TryGetValue(key, out var e))
                    {
                        e = edgeA.Count; edgeIndex[key] = e;
                        edgeA.Add(a); edgeB.Add(b); edgeFaceCount.Add(0); edgeFaceSum.Add(Vec3.Zero);
                    }
                    edgeFaceCount[e]++; edgeFaceSum[e] += facePts[f];
                }
            }
            int ne = edgeA.Count;
            var edgePts = new Vec3[ne];
            for (int e = 0; e < ne; e++)
                edgePts[e] = edgeFaceCount[e] == 2
                    ? (verts[edgeA[e]] + verts[edgeB[e]] + edgeFaceSum[e]) / 4
                    : (verts[edgeA[e]] + verts[edgeB[e]]) / 2;

            var faceSum = new Vec3[nv]; var faceCount = new int[nv];
            for (int f = 0; f < nf; f++) foreach (var i in faces[f]) { faceSum[i] += facePts[f]; faceCount[i]++; }
            var midSum = new Vec3[nv]; var edgeCount = new int[nv];
            var border = new List<int>[nv];
            for (int e = 0; e < ne; e++)
            {
                int a = edgeA[e], b = edgeB[e];
                var mid = (verts[a] + verts[b]) / 2;
                midSum[a] += mid; midSum[b] += mid; edgeCount[a]++; edgeCount[b]++;
                if (edgeFaceCount[e] != 2)
                {
                    (border[a] ??= new List<int>()).Add(b);
                    (border[b] ??= new List<int>()).Add(a);
                }
            }
            var outV = new List<Vec3>(nv + ne + nf);
            for (int i = 0; i < nv; i++)
            {
                var p = verts[i];
                if (faceCount[i] == 0) outV.Add(p);
                else if (border[i] == null)
                {
                    double n = edgeCount[i];
                    var q = faceSum[i] / faceCount[i];
                    var r = midSum[i] / edgeCount[i];
                    outV.Add((q + 2 * r + (n - 3) * p) / n);
                }
                else if (border[i].Count == 2) outV.Add((verts[border[i][0]] + 6 * p + verts[border[i][1]]) / 8);
                else outV.Add(p);
            }
            outV.AddRange(edgePts);
            outV.AddRange(facePts);
            var outF = new List<int[]>();
            for (int f = 0; f < nf; f++)
            {
                var c = faces[f];
                int m = c.Length;
                for (int k = 0; k < m; k++)
                {
                    int next = edgeIndex[Key(c[k], c[(k + 1) % m])], prev = edgeIndex[Key(c[(k + m - 1) % m], c[k])];
                    outF.Add(new[] { c[k], nv + next, nv + ne + f, nv + prev });
                }
            }
            return (outV, outF);
        }

        /// <summary>Fan triangles from each face's first corner.</summary>
        public static List<int> Triangulate(IEnumerable<int[]> faces)
        {
            var tris = new List<int>();
            foreach (var f in faces)
                for (int k = 1; k + 1 < f.Length; k++) { tris.Add(f[0]); tris.Add(f[k]); tris.Add(f[k + 1]); }
            return tris;
        }

        public static int TriangleCount(IEnumerable<int[]> faces) => faces.Sum(f => System.Math.Max(0, f.Length - 2));

        /// <summary>Most triangles one shape may have once smoothed; smoothing is reduced to stay under it.</summary>
        public const int MaxOutputTriangles = 500000;

        /// <summary>
        /// Triangles a shape has after <paramref name="levels"/> Catmull-Clark steps: an n-sided face becomes n quads in the
        /// first step and every quad becomes 4 in each later one; each quad is 2 triangles.
        /// </summary>
        public static long EstimateTriangles(PolyMesh m, int levels)
        {
            if (levels <= 0) return TriangleCount(m.Faces);
            long corners = m.Faces.Sum(f => (long)f.Length);
            return 2 * corners * (1L << (2 * (levels - 1)));
        }

        /// <summary>
        /// The smoothing level actually used: <paramref name="wanted"/>, lowered until the shape stays under
        /// <see cref="MaxOutputTriangles"/>. Throws when even the unsmoothed shape is over it.
        /// </summary>
        public static int SubdivisionLevelFor(PolyMesh m, int wanted, out long triangles)
        {
            int level = System.Math.Max(0, System.Math.Min(ShapeSpec.MaxSubdivision, wanted));
            triangles = EstimateTriangles(m, level);
            while (level > 0 && triangles > MaxOutputTriangles) { level--; triangles = EstimateTriangles(m, level); }
            if (triangles > MaxOutputTriangles)
                throw new MeshOpException($"it has {triangles:N0} triangles; a shape may have up to {MaxOutputTriangles:N0}. Simplify it first.");
            return level;
        }

        /// <summary>
        /// The shape as saved in the plane: smoothed (Subdivision levels, lowered if the result would be too detailed),
        /// turned into triangles, scaled, turned and moved into aircraft space. Glass faces get triangle material 1.
        /// </summary>
        public static MeshData ToMeshData(ShapeSpec s)
        {
            var poly = PolyMesh.FromShape(s);
            IList<Vec3> v = poly.Vertices; IList<int[]> f = poly.Faces;
            int levels = SubdivisionLevelFor(poly, s.Subdivision, out _);
            for (int i = 0; i < levels && f.Count > 0; i++) { var r = CatmullClark(v, f); v = r.vertices; f = r.faces; }
            var tris = Triangulate(f);
            var q = FuselageLoft.Rotation(s.RotationDeg);
            var sc = s.Scale != null && s.Scale.Length >= 3 ? s.Scale : new double[] { 1, 1, 1 };
            var pos = Vec3.FromArray(s.Position ?? new double[3]);
            var mesh = new MeshData { Name = s.Name, Vertices = new float[v.Count * 3] };
            for (int i = 0; i < v.Count; i++)
            {
                var p = pos + q * new Vec3(v[i].X * sc[0], v[i].Y * sc[1], v[i].Z * sc[2]);
                mesh.Vertices[i * 3] = (float)p.X; mesh.Vertices[i * 3 + 1] = (float)p.Y; mesh.Vertices[i * 3 + 2] = (float)p.Z;
            }
            if (sc[0] * sc[1] * sc[2] < 0)
                for (int i = 0; i + 2 < tris.Count; i += 3) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
            mesh.Triangles = tris.ToArray();
            mesh.TriangleMaterials = Enumerable.Repeat(s.Material == ShapeSpec.Glass ? 1 : 0, tris.Count / 3).ToArray();
            return mesh;
        }

        // ------------------------------------------------------------------ import

        /// <summary>
        /// A fixed mesh as an editable shape: corners closer than <paramref name="weld"/> (m) are joined, each triangle becomes
        /// a face and triangles that collapse are dropped.
        /// </summary>
        public static PolyMesh FromMesh(MeshData mesh, double weld = 1e-5)
        {
            var m = new PolyMesh();
            var index = new Dictionary<(long, long, long), int>();
            int n = mesh.Vertices.Length / 3;
            var map = new int[n];
            for (int i = 0; i < n; i++)
            {
                var p = new Vec3(mesh.Vertices[i * 3], mesh.Vertices[i * 3 + 1], mesh.Vertices[i * 3 + 2]);
                var key = ((long)System.Math.Round(p.X / weld), (long)System.Math.Round(p.Y / weld), (long)System.Math.Round(p.Z / weld));
                if (!index.TryGetValue(key, out var id)) { id = m.Vertices.Count; index[key] = id; m.Vertices.Add(p); }
                map[i] = id;
            }
            var t = mesh.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = map[t[i]], b = map[t[i + 1]], c = map[t[i + 2]];
                if (a == b || b == c || a == c) continue;
                m.Faces.Add(new[] { a, b, c });
            }
            Compact(m);
            return m;
        }

        public sealed class ObjImport
        {
            public ObjImport(PolyMesh mesh, int triangles, List<string> notes) { Mesh = mesh; Triangles = triangles; Notes = notes; }
            public PolyMesh Mesh { get; }
            public int Triangles { get; }
            public List<string> Notes { get; }
            public string? Warning => Notes.Count > 0 ? string.Join(" ", Notes) : null;
        }

        /// <summary>
        /// Reads a Wavefront .obj (v and f lines; v, v/vt, v/vt/vn, v//vn and negative indices; polygons kept; o, g, usemtl
        /// and the rest ignored).
        /// Axis rule, from comparing a Flyout reference model (su57.obj, nose along +x, y up, wings along z) with the plane
        /// built over it: the plane's (x, y, z) = the file's (z, y, x). Swapping two axes also turns the file's right-handed
        /// space into Unity's left-handed one, so faces are rewound. A model longer than 60 m is shrunk by tens until it fits
        /// (su57.obj is in units of 10 cm). The result is centred on its bounds and every face is turned to point outwards.
        /// Models that come in sideways or backwards turn with R in the builder.
        /// </summary>
        public static ObjImport ImportObj(string text)
        {
            var verts = new List<Vec3>();
            var faces = new List<int[]>();
            var notes = new List<string>();
            int skipped = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 2) continue;
                if (line.StartsWith("v ", StringComparison.Ordinal) || line.StartsWith("v\t", StringComparison.Ordinal))
                {
                    var p = line.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 3 || !TryNum(p[0], out var x) || !TryNum(p[1], out var y) || !TryNum(p[2], out var z))
                        throw new MeshOpException("This .obj file has a point OpenPlane can't read: \"" + Short(line) + "\".");
                    verts.Add(new Vec3(z, y, x));
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal) || line.StartsWith("f\t", StringComparison.Ordinal))
                {
                    var p = line.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var f = new List<int>();
                    foreach (var tok in p)
                    {
                        var first = tok.Split('/')[0];
                        if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx == 0)
                            throw new MeshOpException("This .obj file has a face OpenPlane can't read: \"" + Short(line) + "\".");
                        int i = idx > 0 ? idx - 1 : verts.Count + idx;
                        if (i < 0 || i >= verts.Count) throw new MeshOpException("This .obj file has a face that points at a missing point: \"" + Short(line) + "\".");
                        if (f.Count == 0 || f[f.Count - 1] != i) f.Add(i);
                    }
                    if (f.Count > 1 && f[0] == f[f.Count - 1]) f.RemoveAt(f.Count - 1);
                    if (f.Count < 3 || f.Distinct().Count() != f.Count) { skipped++; continue; }
                    f.Reverse(); // x and z were swapped (a mirror), so the winding flips
                    faces.Add(f.ToArray());
                }
            }
            if (faces.Count == 0) throw new MeshOpException("This .obj file has no faces, so there's no shape to import.");
            int triangles = TriangleCount(faces);
            if (triangles > MaxTriangles)
                throw new MeshOpException($"This model has {triangles:N0} triangles; OpenPlane takes up to {MaxTriangles:N0}. Simplify it first (in Blender: the Decimate modifier).");
            if (skipped > 0) notes.Add($"{skipped} broken face{(skipped == 1 ? " was" : "s were")} left out.");

            var m = new PolyMesh { Vertices = verts, Faces = faces };
            // Weld exact duplicates (some exporters split every face), so the shape is closed where the model is.
            var welded = MergeByDistance(m, 1e-7).Mesh;
            m = welded;
            Compact(m);

            var (min, max) = Bounds(m.Vertices);
            var size = max - min;
            double longest = System.Math.Max(size.X, System.Math.Max(size.Y, size.Z));
            double scale = 1;
            while (longest * scale > 60) scale /= 10;
            var centre = (min + max) / 2;
            for (int i = 0; i < m.Vertices.Count; i++) m.Vertices[i] = (m.Vertices[i] - centre) * scale;
            if (scale < 1) notes.Add($"It was {longest:0.#} m long, so it was shrunk to {scale * 100:0.##} % (it was probably made in other units).");
            if (triangles > WarnTriangles) notes.Add($"It has {triangles:N0} triangles, which is a lot for one part; the builder may slow down. A simpler model helps.");
            OrientOutward(m);
            return new ObjImport(m, triangles, notes);
        }


        public static (Vec3 min, Vec3 max) Bounds(IEnumerable<Vec3> pts)
        {
            var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (var p in pts) { min = Vec3.Min(min, p); max = Vec3.Max(max, p); }
            if (min.X > max.X) return (Vec3.Zero, Vec3.Zero);
            return (min, max);
        }

        private static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        private static string Short(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        // ------------------------------------------------------------------ selections

        /// <summary>
        /// The faces a selection covers: listed faces in face mode; otherwise faces whose every corner (vertex mode) or every
        /// edge (edge mode) is selected.
        /// </summary>
        public static List<int> SelectedFaces(PolyMesh m, string mode, IEnumerable<int>? verts, IEnumerable<int[]>? edges, IEnumerable<int>? faces)
        {
            if (mode == "face") return (faces ?? Enumerable.Empty<int>()).Where(f => f >= 0 && f < m.Faces.Count).Distinct().ToList();
            if (mode == "edge")
            {
                var set = new HashSet<long>((edges ?? Enumerable.Empty<int[]>()).Where(e => e != null && e.Length >= 2).Select(e => Key(e[0], e[1])));
                return Enumerable.Range(0, m.Faces.Count).Where(f =>
                {
                    var c = m.Faces[f];
                    for (int k = 0; k < c.Length; k++) if (!set.Contains(Key(c[k], c[(k + 1) % c.Length]))) return false;
                    return true;
                }).ToList();
            }
            var vs = new HashSet<int>(verts ?? Enumerable.Empty<int>());
            return Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].All(vs.Contains)).ToList();
        }

        /// <summary>The corners a selection covers (edge ends and face corners included).</summary>
        public static List<int> SelectedVerts(PolyMesh m, string mode, IEnumerable<int>? verts, IEnumerable<int[]>? edges, IEnumerable<int>? faces)
        {
            var list = new List<int>();
            var seen = new HashSet<int>();
            void Add(int v) { if (v >= 0 && v < m.Vertices.Count && seen.Add(v)) list.Add(v); }
            if (mode == "face") foreach (var f in faces ?? Enumerable.Empty<int>()) { if (f >= 0 && f < m.Faces.Count) foreach (var v in m.Faces[f]) Add(v); }
            else if (mode == "edge") foreach (var e in edges ?? Enumerable.Empty<int[]>()) { if (e != null && e.Length >= 2) { Add(e[0]); Add(e[1]); } }
            else foreach (var v in verts ?? Enumerable.Empty<int>()) Add(v);
            return list;
        }

        public static string NewId() => "shape-" + RandomId(6);

        private static readonly Random Rng = new Random();
        public static string RandomId(int n)
        {
            const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
            lock (Rng) return new string(Enumerable.Range(0, n).Select(_ => chars[Rng.Next(chars.Length)]).ToArray());
        }

        /// <summary>A shape part made from a primitive, named after it.</summary>
        public static ShapeSpec NewPrimitiveShape(string kind, double[]? size = null)
        {
            var mesh = Primitive(kind, size);
            var s = new ShapeSpec { Id = NewId(), Name = char.ToUpperInvariant(kind[0]) + kind.Substring(1), Source = kind };
            mesh.WriteTo(s);
            return s;
        }
    }
}
