using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Flyout
{
    /// <summary>
    /// Everything a Flyout builder adds for looks: materials (colours, finishes, glass, cut-outs), shape parts (boxes,
    /// cylinders, spheres, hemispheres), cockpit instruments and lights. Shapes are rebuilt exactly from their sizes; Flyout's
    /// own instrument and cockpit models are never copied: OpenPlane makes its own at the same place and size.
    /// </summary>
    internal static class FlyoutDetail
    {
        /// <summary>A material slot that is a cut-out ("Hole"): its faces aren't drawn.</summary>
        public const int Hole = -2;

        /// <summary>Palette index of a part's material slot: -1 is the plane's paint, <see cref="Hole"/> a cut-out.</summary>
        public static int MaterialOf(FlyoutPart part, int slot, List<MaterialDef> palette)
        {
            foreach (var pm in part.Modules("PartMaterials"))
            {
                if (pm.GetInt("index", 0) != slot) continue;
                var mat = pm.ChildrenNamed("Material").FirstOrDefault();
                string name = (mat?.Get("material") ?? pm.Get("mat") ?? "Default").Trim();
                if (name.Equals("Hole", StringComparison.OrdinalIgnoreCase)) return Hole;
                var c = FlyoutValues.Numbers(mat?.Get("color"));
                var color = new double[] { c.Length > 0 ? c[0] : 0.6, c.Length > 1 ? c[1] : 0.62, c.Length > 2 ? c[2] : 0.64, c.Length > 3 ? c[3] : 1 };
                bool glass = name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0;
                // Flyout glass is often fully clear (alpha 0); keep a faint tint so the canopy reads as glass.
                color[3] = glass ? System.Math.Min(0.85, System.Math.Max(0.18, color[3])) : 1;
                var m = new MaterialDef
                {
                    Name = glass ? "Glass" : name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "Paint" : name,
                    Kind = glass ? MaterialDef.Glass : MaterialDef.Paint,
                    Color = color,
                    Metallic = Clamp01(mat?.GetDouble("metallic", 0) ?? 0),
                    Smoothness = Clamp01(mat?.GetDouble("smoothness", 0.3) ?? 0.3),
                    Finish = glass || name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? null : name,
                };
                m.Id = "m" + palette.Count;
                int i = DesignMaterials.IndexOf(palette, m);
                if (palette[i].Id == null) palette[i].Id = "m" + i;
                return i;
            }
            return slot == 0 ? -1 : Hole;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ---------------------------------------------------------------- shapes

        public static readonly string[] ShapeKinds =
        {
            "Shapes/Cube", "Shapes/Cylinder", "Shapes/Sphere", "Shapes/Cone", "Shapes/Hemisphere",
            "Primitives/Cube", "Primitives/Cylinder", "Primitives/Sphere", "Primitives/Cone", "Primitives/Hemisphere",
            // Flyout's leading-edge root extension is its own model; OpenPlane draws a wedge of the same size instead.
            "Prefabs/Wing/Lerx",
        };

        public static bool IsShape(FlyoutPart p) => ShapeKinds.Contains(p.Kind);

        /// <summary>A unit primitive (fits a 1 m cube, centred on the part) as vertices and outward triangles.</summary>
        public static (List<Vec3> v, List<int> t) Unit(string kind)
        {
            var v = new List<Vec3>(); var t = new List<int>();
            string k = kind.Substring(kind.LastIndexOf('/') + 1);
            const int seg = 20;
            switch (k)
            {
                case "Cube":
                    for (int i = 0; i < 8; i++) v.Add(new Vec3((i & 1) == 0 ? -0.5 : 0.5, (i & 2) == 0 ? -0.5 : 0.5, (i & 4) == 0 ? -0.5 : 0.5));
                    void Quad(int a, int b, int c, int d) { t.AddRange(new[] { a, b, c, a, c, d }); }
                    Quad(0, 2, 3, 1); Quad(4, 5, 7, 6); Quad(0, 1, 5, 4); Quad(2, 6, 7, 3); Quad(0, 4, 6, 2); Quad(1, 3, 7, 5);
                    break;
                case "Cylinder":
                case "Cone":
                {
                    // Along z, length 1, diameter 1 (a cone narrows to a point at +z).
                    bool cone = k == "Cone";
                    for (int i = 0; i < seg; i++)
                    {
                        double a = 2 * System.Math.PI * i / seg;
                        double x = 0.5 * System.Math.Cos(a), y = 0.5 * System.Math.Sin(a);
                        v.Add(new Vec3(x, y, -0.5));
                        v.Add(cone ? new Vec3(0, 0, 0.5) : new Vec3(x, y, 0.5));
                    }
                    int back = v.Count; v.Add(new Vec3(0, 0, -0.5));
                    int front = v.Count; v.Add(new Vec3(0, 0, 0.5));
                    for (int i = 0; i < seg; i++)
                    {
                        int a0 = 2 * i, a1 = 2 * i + 1, b0 = 2 * ((i + 1) % seg), b1 = b0 + 1;
                        t.AddRange(new[] { a0, b0, b1, a0, b1, a1 });
                        t.AddRange(new[] { back, b0, a0 });
                        if (!cone) t.AddRange(new[] { front, a1, b1 });
                    }
                    break;
                }
                case "Lerx":
                {
                    // A wedge: a right triangle in x/z (root along -x from back to front, tip at the back), 1 m thick in y.
                    var tri = new[] { new Vec3(-1.0 / 3, 0, -1.0 / 3), new Vec3(-1.0 / 3, 0, 2.0 / 3), new Vec3(2.0 / 3, 0, -1.0 / 3) };   // centred on its centroid
                    foreach (var p in tri) v.Add(p + new Vec3(0, 0.5, 0));
                    foreach (var p in tri) v.Add(p + new Vec3(0, -0.5, 0));
                    t.AddRange(new[] { 0, 1, 2, 3, 5, 4, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 });
                    break;
                }
                case "Sphere":
                case "Hemisphere":
                {
                    // A hemisphere is the top half (dome along +y) with a flat base.
                    bool half = k == "Hemisphere";
                    int rings = half ? seg / 4 : seg / 2;
                    double top = System.Math.PI / 2, bottom = half ? 0 : -System.Math.PI / 2;
                    for (int r = 0; r <= rings; r++)
                    {
                        double lat = bottom + (top - bottom) * r / rings;
                        for (int i = 0; i < seg; i++)
                        {
                            double a = 2 * System.Math.PI * i / seg;
                            v.Add(new Vec3(0.5 * System.Math.Cos(lat) * System.Math.Cos(a), 0.5 * System.Math.Sin(lat), 0.5 * System.Math.Cos(lat) * System.Math.Sin(a)));
                        }
                    }
                    for (int r = 0; r < rings; r++)
                        for (int i = 0; i < seg; i++)
                        {
                            int a0 = r * seg + i, a1 = r * seg + (i + 1) % seg, b0 = a0 + seg, b1 = a1 + seg;
                            t.AddRange(new[] { a0, b0, b1, a0, b1, a1 });
                        }
                    if (half)
                    {
                        int c = v.Count; v.Add(new Vec3(0, 0, 0));
                        for (int i = 0; i < seg; i++) t.AddRange(new[] { c, i, (i + 1) % seg });
                    }
                    break;
                }
            }
            // Make every triangle face away from the centre (whatever order it was written in).
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                var a = v[t[i]]; var b = v[t[i + 1]]; var cc = v[t[i + 2]];
                var n = Vec3.Cross(b - a, cc - a);
                if (Vec3.Dot(n, (a + b + cc) / 3) < 0) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
            }
            return (v, t);
        }

        /// <summary>The shape as a mesh in craft space, with its material; null when it is a cut-out or too small to see.</summary>
        public static MeshData? ShapeMesh(FlyoutPart part, List<MaterialDef> palette)
        {
            var s = part.ShapeScale;
            if (System.Math.Max(System.Math.Abs(s.X), System.Math.Max(System.Math.Abs(s.Y), System.Math.Abs(s.Z))) < 0.02) return null;
            int mat = MaterialOf(part, 0, palette);
            if (mat == Hole) return null;
            var (v, t) = Unit(part.Kind);
            var verts = new float[v.Count * 3];
            for (int i = 0; i < v.Count; i++)
            {
                var w = part.ShapePointToWorld(v[i]);
                verts[i * 3] = (float)w.X; verts[i * 3 + 1] = (float)w.Y; verts[i * 3 + 2] = (float)w.Z;
            }
            // A mirrored part, or an odd number of negative sizes, turns the faces inside out: rewind them.
            bool flip = part.Mirrored ^ (s.X * s.Y * s.Z < 0);
            var tris = t.ToArray();
            if (flip) for (int i = 0; i + 2 < tris.Length; i += 3) { int tmp = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = tmp; }
            return new MeshData
            {
                Name = part.Title ?? $"{part.Kind.Substring(part.Kind.LastIndexOf('/') + 1)} {part.Id}",
                Vertices = verts, Triangles = tris, TriangleMaterials = new int[tris.Length / 3], Materials = new[] { mat },
            };
        }

        /// <summary>True when the point is inside the closed mesh (a ray along +x crosses its surface an odd number of times).</summary>
        public static bool Inside(MeshData m, Vec3 p)
        {
            int crossings = 0;
            var v = m.Vertices; var t = m.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var a = new Vec3(v[t[i] * 3], v[t[i] * 3 + 1], v[t[i] * 3 + 2]);
                var b = new Vec3(v[t[i + 1] * 3], v[t[i + 1] * 3 + 1], v[t[i + 1] * 3 + 2]);
                var c = new Vec3(v[t[i + 2] * 3], v[t[i + 2] * 3 + 1], v[t[i + 2] * 3 + 2]);
                if (RayHitsX(p, a, b, c)) crossings++;
            }
            return crossings % 2 == 1;
        }

        private static bool RayHitsX(Vec3 o, Vec3 a, Vec3 b, Vec3 c)
        {
            // Möller-Trumbore with the direction (1, 0, 0).
            var dir = new Vec3(1, 0, 0);
            var e1 = b - a; var e2 = c - a;
            var h = Vec3.Cross(dir, e2);
            double det = Vec3.Dot(e1, h);
            if (System.Math.Abs(det) < 1e-12) return false;
            double f = 1 / det;
            var s = o - a;
            double u = f * Vec3.Dot(s, h);
            if (u < 0 || u > 1) return false;
            var q = Vec3.Cross(s, e1);
            double w = f * Vec3.Dot(dir, q);
            if (w < 0 || u + w > 1) return false;
            return f * Vec3.Dot(e2, q) > 1e-9;
        }

        // ---------------------------------------------------------------- instruments and lights

        /// <summary>Flyout's gauge "target" numbers, read from the samples' dial ranges. A guess: the report says so.</summary>
        private static string GaugeReads(int target, double max)
        {
            switch (target)
            {
                case 2: return max > 1000 ? "altitude" : "vertical-speed";
                case 4: return "rpm";
                case 5: return max > 300 && max < 400 ? "heading" : "airspeed";
                case 7: return "g";
                case 1: case 3: return "fuel";
                default: return "throttle";
            }
        }

        public static InstrumentDef? Instrument(FlyoutPart part)
        {
            string k = part.Kind.Substring(part.Kind.IndexOf('/') + 1);
            var pose = part.WorldPose;
            // Flyout's instrument models don't all face the same way: most face their +y, the horizon, angle-of-attack
            // indicator and HUD face -z (back at the pilot) and the mirror -y. OpenPlane's convention is the face along +y
            // with 12 o'clock along +z, so those are turned to match (checked against every sample craft).
            var turn = k == "AttIndicator" || k == "AoaIndic1" || k == "HUD" ? new Quat(-System.Math.Sqrt(0.5), 0, 0, System.Math.Sqrt(0.5))
                     : k == "Mirror" ? new Quat(1, 0, 0, 0)
                     : Quat.Identity;
            var n = new InstrumentDef
            {
                Name = part.Title ?? k, SourcePartId = part.Id,
                Position = pose.Position.ToArray(), Rotation = (pose.Rotation * turn).ToArray(),
            };
            var g = part.Module("Gauge");
            // Each instrument keeps its size in its own block. A dial at scale 1 is about 17 cm across (neighbouring
            // scale-0.5 dials in the samples sit 10 to 11 cm apart); the angle-of-attack indexer is a small one.
            string sizeBlock = k == "AttIndicator" ? "AttitudeIndicator" : k == "AoaIndic1" ? "AOAIndicator1" : k;
            double scale = part.Module(sizeBlock)?.GetDouble("scale", 0.5) ?? 0.5;
            n.Size = System.Math.Min(0.3, System.Math.Max(0.025, (k == "AoaIndic1" ? 0.03 : 0.17) * scale));
            switch (k)
            {
                case "Gauge":
                    n.Kind = "gauge";
                    n.Min = g?.GetDouble("min_val", 0) ?? 0; n.Max = g?.GetDouble("max_val", 1) ?? 1;
                    n.MinDeg = g?.GetDouble("min_deg", 30) ?? 30; n.MaxDeg = g?.GetDouble("max_deg", 330) ?? 330;
                    n.Reads = GaugeReads(g?.GetInt("target", 0) ?? 0, n.Max);
                    break;
                case "Tachometer": n.Kind = "gauge"; n.Reads = "rpm"; n.Min = 0; n.Max = 110; break;
                case "AoaIndic1": n.Kind = "gauge"; n.Reads = "aoa"; n.Min = -10; n.Max = 30; break;
                case "AttIndicator": n.Kind = "attitude"; break;
                case "Compass": n.Kind = "compass"; n.Reads = "heading"; break;
                case "Screen1": n.Kind = "screen"; n.Size = 0.16; break;
                case "HUD": n.Kind = "hud"; n.Size = 0.14; break;
                case "Mirror": n.Kind = "mirror"; n.Size = 0.12; break;
                case "LGLever": n.Kind = "lever"; n.Size = 0.06; break;
                default: return null;
            }
            n.Name = k == "Gauge" ? $"Gauge ({n.Reads})" : n.Name;
            return n;
        }

        public static LightDef? Light(FlyoutPart part)
        {
            var nl = part.Module("NavLight");
            if (nl == null) return null;
            var c = FlyoutValues.Numbers(nl.Get("color"));
            var color = new double[] { c.Length > 0 ? c[0] : 1, c.Length > 1 ? c[1] : 1, c.Length > 2 ? c[2] : 1, 1 };
            string kind = part.Kind == "LightStrip" ? "strip"
                        : part.Kind == "Spot" ? "landing"
                        : (nl.GetDouble("interval", 0) > 0 ? (part.Kind == "Beacon" ? "beacon" : "strobe") : "nav");
            var s = part.ShapeScale;
            return new LightDef
            {
                Name = part.Title ?? part.Kind, Kind = kind, SourcePartId = part.Id, Color = color,
                Position = part.WorldPose.Position.ToArray(), Rotation = part.WorldPose.Rotation.ToArray(),
                Size = kind == "strip" ? System.Math.Max(0.1, System.Math.Max(System.Math.Abs(s.X), System.Math.Abs(s.Z)) * 0.5) : 0.12,
            };
        }

        /// <summary>
        /// OpenPlane's own simple cockpit furniture where Flyout's stock cockpit model would be: a seat, and an instrument
        /// panel when the craft has panels switched on. Interior pieces, seen from the cockpit.
        /// </summary>
        /// <summary>The cockpit's seat offset across the plane (side-by-side seats: the AB-4's pilot sits 0.39 m left).</summary>
        public static double SeatOffset(FlyoutPart cockpit)
        {
            double o = cockpit.Module("CockpitCustomization")?.GetDouble("offset", 0) ?? 0;
            return double.IsNaN(o) || System.Math.Abs(o) > 3 ? 0 : o;
        }

        /// <summary>
        /// Where the pilot's eye is, in the cockpit part's frame. Flyout planes mount their gauges 0.75 to 0.85 m up and about
        /// 0.3 m ahead of the cockpit origin, so an eye 0.7 m straight above the origin sat level with the gauges, 30 cm from
        /// them, the panel filling the view. With instruments on the front panel the eye sits where a seated pilot's does:
        /// 0.6 m behind the panel's middle and a little above it. Without them, 0.72 m above the origin. Either way moved
        /// sideways by the seat offset.
        /// </summary>
        public static Vec3 EyeInCockpit(FlyoutPart cockpit, IList<InstrumentDef>? instruments)
        {
            double x = SeatOffset(cockpit);
            var pose = cockpit.WorldPose;
            var inverse = new Quat(-pose.Rotation.X, -pose.Rotation.Y, -pose.Rotation.Z, pose.Rotation.W);
            var front = new List<Vec3>();
            foreach (var i in instruments ?? new List<InstrumentDef>())
            {
                if (i.Kind != "gauge" && i.Kind != "attitude" && i.Kind != "compass" && i.Kind != "screen") continue;
                var local = inverse * (Vec3.FromArray(i.Position) - pose.Position);
                if (local.Z < 0.05 || local.Z > 1.3 || System.Math.Abs(local.X - x) > 0.7) continue;
                front.Add(local);
            }
            if (front.Count == 0) return new Vec3(x, 0.72, 0);
            double y = front.Average(p => p.Y), z = front.Average(p => p.Z);
            return new Vec3(x, System.Math.Max(0.72, y + 0.17), System.Math.Min(0, z - 0.6));
        }

        /// <summary>
        /// Raises the eye (by at most 0.35 m) so the pilot sees over the interior pieces just ahead (an engine block or a big
        /// console in front of a cockpit without instruments filled the Satoria's view).
        /// </summary>
        public static Vec3 SeeOver(Vec3 eyeWorld, Pose cockpit, IEnumerable<MeshData> meshes)
        {
            var inverse = new Quat(-cockpit.Rotation.X, -cockpit.Rotation.Y, -cockpit.Rotation.Z, cockpit.Rotation.W);
            var eye = inverse * (eyeWorld - cockpit.Position);
            double top = double.NegativeInfinity;
            foreach (var m in meshes.Where(m => m.Role == MeshData.Interior && m.Vertices != null))
                for (int i = 0; i + 2 < m.Vertices.Length; i += 3)
                {
                    var p = inverse * (new Vec3(m.Vertices[i], m.Vertices[i + 1], m.Vertices[i + 2]) - cockpit.Position);
                    if (p.Z > eye.Z + 0.2 && p.Z < eye.Z + 1.6 && System.Math.Abs(p.X - eye.X) < 0.45) top = System.Math.Max(top, p.Y);
                }
            if (double.IsNegativeInfinity(top) || top < eye.Y - 0.05) return eyeWorld;
            double raised = System.Math.Min(top + 0.1, eye.Y + 0.35);
            return cockpit.TransformPoint(new Vec3(eye.X, raised, eye.Z));
        }

        /// <summary>
        /// A simple seat and (when the cockpit shows panels) an instrument panel in place of Flyout's cockpit model. The
        /// panel goes just behind the instruments that face the pilot, sized to hold them; without any, where Flyout's
        /// panel usually is.
        /// </summary>
        public static List<MeshData> CockpitStandIns(FlyoutPart cockpit, List<MaterialDef> palette, IList<InstrumentDef>? instruments = null)
        {
            var list = new List<MeshData>();
            var cc = cockpit.Module("CockpitCustomization");
            int dark = DesignMaterials.IndexOf(palette, new MaterialDef { Id = "m" + palette.Count, Name = "Cockpit", Color = new double[] { 0.16, 0.17, 0.18, 1 }, Metallic = 0.1, Smoothness = 0.35 });
            if (palette[dark].Id == null) palette[dark].Id = "m" + dark;
            MeshData Box(string name, Vec3 centre, Vec3 size)
            {
                var (v, t) = Unit("Shapes/Cube");
                var verts = new float[v.Count * 3];
                for (int i = 0; i < v.Count; i++)
                {
                    var local = new Vec3(v[i].X * size.X, v[i].Y * size.Y, v[i].Z * size.Z) + centre;
                    var w = cockpit.WorldPose.TransformPoint(local);
                    verts[i * 3] = (float)w.X; verts[i * 3 + 1] = (float)w.Y; verts[i * 3 + 2] = (float)w.Z;
                }
                return new MeshData { Name = name, Vertices = verts, Triangles = t.ToArray(), TriangleMaterials = new int[t.Count / 3], Materials = new[] { dark }, Role = MeshData.Interior };
            }
            // Under the pilot: the eye's position across and along the cockpit (see EyeInCockpit), the seat just in front of it.
            var eye = EyeInCockpit(cockpit, instruments);
            list.Add(Box("Seat", new Vec3(eye.X, 0.12, eye.Z + 0.18), new Vec3(0.46, 0.12, 0.46)));
            list.Add(Box("Seat back", new Vec3(eye.X, 0.5, eye.Z - 0.1), new Vec3(0.46, 0.7, 0.1)));
            if (cc == null || cc.GetBool("panels"))
            {
                var pose = cockpit.WorldPose;
                var inverse = new Quat(-pose.Rotation.X, -pose.Rotation.Y, -pose.Rotation.Z, pose.Rotation.W);
                // Instruments on the front panel, in the cockpit's own frame: ahead of the seat, near the middle, facing back.
                var front = new List<(Vec3 p, double r)>();
                foreach (var i in instruments ?? new List<InstrumentDef>())
                {
                    if (i.Kind != "gauge" && i.Kind != "attitude" && i.Kind != "compass" && i.Kind != "screen") continue;
                    var local = inverse * (Vec3.FromArray(i.Position) - pose.Position);
                    var face = inverse * (Quat.FromArray(i.Rotation) * new Vec3(0, 1, 0));
                    if (local.Z < 0.1 || local.Z > 1.3 || System.Math.Abs(local.X) > 0.6 || face.Z > -0.4) continue;
                    front.Add((local, i.Kind == "screen" ? i.Size * 0.6 : i.Size * 0.56));
                }
                if (front.Count > 0)
                {
                    double halfWidth = front.Max(f => System.Math.Abs(f.p.X) + f.r) + 0.06;
                    double low = front.Min(f => f.p.Y - f.r) - 0.05, high = front.Max(f => f.p.Y + f.r) + 0.05;
                    // Its top stays below the pilot's line of sight.
                    high = System.Math.Min(high, eye.Y - 0.08);
                    if (high - low < 0.12) low = high - 0.12;
                    double depth = 0.04, back = front.Max(f => f.p.Z) + 0.006;
                    list.Add(Box("Instrument panel", new Vec3(0, (low + high) / 2, back + depth / 2),
                                 new Vec3(System.Math.Max(0.45, halfWidth * 2), System.Math.Max(0.16, high - low), depth)));
                }
                else list.Add(Box("Instrument panel", new Vec3(0, 0.55, 0.62), new Vec3(0.62, 0.28, 0.05)));
            }
            return list;
        }
    }
}
