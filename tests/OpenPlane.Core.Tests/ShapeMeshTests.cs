using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Math;
using OpenPlane.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace OpenPlane.Core.Tests
{
    /// <summary>Free-form shape parts: primitives and mesh edits keep shapes closed and pointing outwards.</summary>
    public class ShapeMeshTests
    {
        private readonly ITestOutputHelper _out;
        public ShapeMeshTests(ITestOutputHelper output) { _out = output; }

        // ------------------------------------------------------------------ checks

        /// <summary>Welds corners by position, then every directed edge must appear once and have its reverse once.</summary>
        public static void AssertClosed(PolyMesh m, string label)
        {
            Assert.True(m.Faces.Count > 0, $"{label}: no faces");
            Assert.All(m.Faces, f => Assert.True(f.Length >= 3, $"{label}: a face has {f.Length} corners"));
            Assert.All(m.Faces, f => Assert.All(f, i => Assert.InRange(i, 0, m.Vertices.Count - 1)));
            var key = new Dictionary<(long, long, long), int>();
            int Weld(int i)
            {
                var v = m.Vertices[i];
                var k = ((long)System.Math.Round(v.X * 1e5), (long)System.Math.Round(v.Y * 1e5), (long)System.Math.Round(v.Z * 1e5));
                if (!key.TryGetValue(k, out var id)) key[k] = id = key.Count;
                return id;
            }
            var edges = new Dictionary<(int, int), int>();
            foreach (var f in m.Faces)
                for (int k = 0; k < f.Length; k++)
                {
                    var e = (Weld(f[k]), Weld(f[(k + 1) % f.Length]));
                    if (e.Item1 == e.Item2) continue;
                    edges[e] = edges.TryGetValue(e, out var c) ? c + 1 : 1;
                }
            foreach (var (e, count) in edges)
            {
                Assert.True(count == 1, $"{label}: edge {e} is used {count} times in the same direction");
                Assert.True(edges.ContainsKey((e.Item2, e.Item1)), $"{label}: edge {e} has no partner, so the shape has a hole");
            }
        }

        public static void AssertOutward(PolyMesh m, string label)
        {
            double v = ShapeMesh.SignedVolume(m);
            Assert.True(v > 1e-6, $"{label}: signed volume {v} (faces point inwards)");
        }

        private static void AssertGood(PolyMesh m, string label) { AssertClosed(m, label); AssertOutward(m, label); }

        private static PolyMesh Moved(MeshOpResult r, double amount)
        {
            var m = r.Mesh.Clone();
            foreach (var mo in r.Motion!) m.Vertices[mo.V] = Vec3.FromArray(mo.Origin) + Vec3.FromArray(mo.Dir) * amount;
            return m;
        }

        private static PolyMesh Box() => ShapeMesh.Primitive("box", new[] { 2.0, 1.0, 3.0 });

        private static int FaceFacing(PolyMesh m, Vec3 dir)
            => Enumerable.Range(0, m.Faces.Count).OrderByDescending(f => Vec3.Dot(ShapeMesh.Newell(m, m.Faces[f]).Normalized, dir)).First();

        // ------------------------------------------------------------------ primitives

        [Theory]
        [InlineData("box")]
        [InlineData("cylinder")]
        [InlineData("sphere")]
        [InlineData("cone")]
        [InlineData("capsule")]
        public void PrimitivesAreClosedAndOutward(string kind)
        {
            var m = ShapeMesh.Primitive(kind);
            AssertGood(m, kind);
            var (min, max) = ShapeMesh.Bounds(m.Vertices);
            var size = ShapeMesh.DefaultSize(kind);
            for (int i = 0; i < 3; i++) Assert.Equal(size[i], max.ToArray()[i] - min.ToArray()[i], 3);
            var s = ShapeMesh.NewPrimitiveShape(kind);
            Assert.StartsWith("shape-", s.Id);
            Assert.Equal(kind, s.Source);
            AssertGood(PolyMesh.FromShape(s), kind + " shape");
        }

        [Fact]
        public void BoxIsSixQuadsWithTheRightVolume()
        {
            var m = Box();
            Assert.Equal(8, m.Vertices.Count);
            Assert.Equal(6, m.Faces.Count);
            Assert.All(m.Faces, f => Assert.Equal(4, f.Length));
            Assert.Equal(6.0, ShapeMesh.SignedVolume(m), 6);
        }

        // ------------------------------------------------------------------ edits

        [Fact]
        public void ExtrudeKeepsTheShapeClosedAndOutward()
        {
            var box = Box();
            int top = FaceFacing(box, Vec3.Up);
            var r = ShapeMesh.ExtrudeFaces(box, new[] { top });
            Assert.Equal(new[] { top }, r.Faces);
            Assert.Equal(4, r.Motion!.Count);
            Assert.All(r.Motion, mo => Assert.Equal(1.0, mo.Dir[1], 6));
            var m = Moved(r, 0.5);
            AssertGood(m, "box extruded up");
            Assert.Equal(6 + 2 * 3 * 0.5, ShapeMesh.SignedVolume(m), 6);
            Assert.Equal(10, m.Faces.Count);

            // A region of side faces on a cylinder.
            var cyl = ShapeMesh.Primitive("cylinder");
            var sides = Enumerable.Range(0, cyl.Faces.Count).Where(f => cyl.Faces[f].Length == 4).Take(3).ToList();
            var r2 = ShapeMesh.ExtrudeFaces(cyl, sides);
            AssertGood(Moved(r2, 0.2), "cylinder side region");
            Assert.Equal(cyl.Faces.Count + 8, r2.Mesh.Faces.Count);   // the region's boundary has 8 edges

            var all = Enumerable.Range(0, box.Faces.Count);
            var e = Assert.Throws<MeshOpException>(() => ShapeMesh.ExtrudeFaces(box, all));
            Assert.Contains("unselected", e.Message);
        }

        [Fact]
        public void InsetKeepsTheShapeClosedAndOutward()
        {
            var box = Box();
            int front = FaceFacing(box, Vec3.Forward);
            var r = ShapeMesh.InsetFaces(box, new[] { front });
            Assert.True(r.MaxAmount > 0.49 && r.MaxAmount < 0.51, $"max {r.MaxAmount}");   // the 2 x 1 face: half its height
            var m = Moved(r, 0.2);
            AssertGood(m, "box inset");
            Assert.Equal(6.0, ShapeMesh.SignedVolume(m), 6);
            // The inner face's edges are 0.2 m inside the outer ones.
            var inner = m.Faces[front].Select(i => m.Vertices[i]).ToList();
            Assert.Equal(2 - 0.4, inner.Max(p => p.X) - inner.Min(p => p.X), 6);
            Assert.Equal(1 - 0.4, inner.Max(p => p.Y) - inner.Min(p => p.Y), 6);

            var cyl = ShapeMesh.Primitive("cylinder");
            int cap = Enumerable.Range(0, cyl.Faces.Count).First(f => cyl.Faces[f].Length > 4);
            var rc = ShapeMesh.InsetFaces(cyl, new[] { cap });
            AssertGood(Moved(rc, rc.MaxAmount!.Value * 0.5), "cylinder cap inset");
            // Then extrude the inset cap outwards: a nozzle.
            var nozzle = ShapeMesh.ExtrudeFaces(Moved(rc, 0.1), new[] { cap });
            AssertGood(Moved(nozzle, 0.3), "inset then extruded");
        }

        [Fact]
        public void LoopCutSplitsTheRingAndStaysClosed()
        {
            var box = Box();
            // An edge along x on the top: the ring runs around the box through four faces.
            var top = box.Faces[FaceFacing(box, Vec3.Up)];
            int a = top[0], b = top[1];
            var r = ShapeMesh.LoopCut(box, a, b);
            AssertGood(r.Mesh, "box loop cut");
            Assert.Equal(10, r.Mesh.Faces.Count);
            Assert.Equal(4, r.Verts.Count);
            Assert.Equal(4, r.Edges.Count);
            Assert.Equal(6.0, ShapeMesh.SignedVolume(r.Mesh), 6);

            var three = ShapeMesh.LoopCut(box, a, b, cuts: 3);
            AssertGood(three.Mesh, "three cuts");
            Assert.Equal(6 + 4 * 3, three.Mesh.Faces.Count);

            // Across a cylinder's side: one cut ring around it.
            var cyl = ShapeMesh.Primitive("cylinder");
            var side = cyl.Faces.First(f => f.Length == 4);
            var vertical = Enumerable.Range(0, 4).Select(k => (side[k], side[(k + 1) % 4])).First(e => System.Math.Abs(cyl.Vertices[e.Item1].Z - cyl.Vertices[e.Item2].Z) > 0.5);
            var rc = ShapeMesh.LoopCut(cyl, vertical.Item1, vertical.Item2);
            AssertGood(rc.Mesh, "cylinder around");
            Assert.Equal(16, rc.Verts.Count);
            // Along a cap's rim: the side quads are cut and the n-gon caps take the new corners.
            var rim = Enumerable.Range(0, 4).Select(k => (side[k], side[(k + 1) % 4])).First(e => System.Math.Abs(cyl.Vertices[e.Item1].Z - cyl.Vertices[e.Item2].Z) < 1e-9);
            var rr = ShapeMesh.LoopCut(cyl, rim.Item1, rim.Item2, factor: 0.3);
            AssertGood(rr.Mesh, "cylinder rim");
        }

        [Fact]
        public void MergeAndDeleteBehave()
        {
            var box = Box();
            var top = box.Faces[FaceFacing(box, Vec3.Up)];
            var r = ShapeMesh.Merge(box, new[] { top[0], top[1] });
            Assert.Equal(7, r.Mesh.Vertices.Count);
            AssertGood(r.Mesh, "box with one edge merged");

            var dupes = new PolyMesh { Vertices = box.Vertices.Concat(box.Vertices).ToList(), Faces = box.Faces.Select((f, i) => i % 2 == 0 ? f : f.Select(x => x + 8).ToArray()).ToList() };
            var md = ShapeMesh.MergeByDistance(dupes, 1e-4);
            Assert.Equal(8, md.Mesh.Vertices.Count);
            AssertGood(md.Mesh, "merged by distance");

            var del = ShapeMesh.Delete(box, "faces", null, null, new[] { 0 });
            Assert.Equal(5, del.Mesh.Faces.Count);
            Assert.True(ShapeMesh.OpenEdges(del.Mesh) > 0);
            Assert.Contains("hole", del.Note);
            var delV = ShapeMesh.Delete(box, "verts", new[] { top[0] }, null, null);
            Assert.Equal(3, delV.Mesh.Faces.Count);
            Assert.Equal(7, delV.Mesh.Vertices.Count);
            Assert.Throws<MeshOpException>(() => ShapeMesh.Merge(box, new[] { 1 }));
        }

        [Fact]
        public void SubdivisionAndOrientation()
        {
            var box = Box();
            var (v, f) = ShapeMesh.CatmullClark(box.Vertices, box.Faces);
            var once = new PolyMesh { Vertices = v, Faces = f };
            Assert.Equal(24, once.Faces.Count);
            Assert.Equal(8 + 12 + 6, once.Vertices.Count);
            AssertGood(once, "box smoothed once");
            double vol = ShapeMesh.SignedVolume(once);
            Assert.InRange(vol, 2.0, 6.0);

            var cone = ShapeMesh.SubdivideApply(ShapeMesh.Primitive("cone")).Mesh;
            AssertGood(cone, "cone smoothed");

            // Open surfaces smooth with border rules and stay open only where they were.
            var open = ShapeMesh.Delete(box, "faces", null, null, new[] { 0 }).Mesh;
            var (ov, of) = ShapeMesh.CatmullClark(open.Vertices, open.Faces);
            Assert.True(ShapeMesh.OpenEdges(new PolyMesh { Vertices = ov, Faces = of }) > 0);

            var messy = box.Clone();
            messy.Faces[1] = Enumerable.Reverse(messy.Faces[1]).ToArray();
            messy.Faces[4] = Enumerable.Reverse(messy.Faces[4]).ToArray();
            var fixedUp = ShapeMesh.RecalculateOutward(messy);
            AssertGood(fixedUp.Mesh, "re-oriented");
            var inside = box.Clone();
            for (int i = 0; i < inside.Faces.Count; i++) inside.Faces[i] = Enumerable.Reverse(inside.Faces[i]).ToArray();
            AssertGood(ShapeMesh.RecalculateOutward(inside).Mesh, "inside out, turned back");
            var flipped = ShapeMesh.Flip(box, new[] { 2 });
            Assert.Throws<Xunit.Sdk.TrueException>(() => AssertClosed(flipped.Mesh, "one face flipped"));
        }

        // ------------------------------------------------------------------ shapes in the plane

        private static ShapeSpec Pod(bool mirror = true, string material = ShapeSpec.Skin, int subdivision = 0)
        {
            var s = ShapeMesh.NewPrimitiveShape("capsule", new[] { 0.6, 0.6, 3.0 });
            s.Name = "Pod"; s.Position = new[] { 2.5, -0.4, 0.5 }; s.Mirror = mirror; s.Material = material; s.Subdivision = subdivision;
            s.RotationDeg = new[] { 5.0, 0, 0 };
            return s;
        }

        private static double MeshVolume(MeshData m)
        {
            double vol = 0;
            for (int i = 0; i + 2 < m.Triangles.Length; i += 3)
            {
                var a = V(m, m.Triangles[i]); var b = V(m, m.Triangles[i + 1]); var c = V(m, m.Triangles[i + 2]);
                vol += Vec3.Dot(a, Vec3.Cross(b, c)) / 6;
            }
            return vol;
        }

        private static Vec3 V(MeshData m, int i) => new Vec3(m.Vertices[i * 3], m.Vertices[i * 3 + 1], m.Vertices[i * 3 + 2]);

        [Fact]
        public void ShapePartsAreSavedAsBodyAndCountedByTheFlightModel()
        {
            var bare = Templates.Fighter();
            var with = Templates.Fighter();
            with.Shapes.Add(Pod(material: ShapeSpec.Glass, subdivision: 1));
            var g0 = DesignGenerator.Generate(bare);
            var g1 = DesignGenerator.Generate(with);
            Assert.Equal(g0.Meshes.Count + 2, g1.Meshes.Count);       // the pod and its mirrored twin
            Assert.Equal(g0.Design.Fuselages.Count + 2, g1.Design.Fuselages.Count);
            int index = g1.MeshIndexByItem[with.Shapes.Last().Id];
            Assert.Equal(g0.Meshes.Count, index);                      // shapes come after the fuselages and the template's own shapes
            var pod = g1.Meshes[index]; var twin = g1.Meshes[index + 1];
            Assert.Equal("Pod (left)", twin.Name);
            Assert.All(pod.TriangleMaterials, slot => Assert.Equal(MaterialDef.Glass, DesignMaterials.ForSlot(g1.Design, pod, slot).Kind));   // glass
            Assert.All(twin.TriangleMaterials, slot => Assert.Equal(MaterialDef.Glass, DesignMaterials.ForSlot(g1.Design, twin, slot).Kind));
            Assert.True(MeshVolume(pod) > 0, "pod points outwards");
            Assert.True(MeshVolume(twin) > 0, "mirrored twin points outwards");
            var body = g1.Design.Fuselages[index];
            Assert.True(body.FrontalArea > 0.25 && body.FrontalArea < 0.45, $"frontal {body.FrontalArea}");   // pi 0.3^2 plus the 5 degree tilt
            Assert.True(body.WettedArea > 4);
            Assert.True(body.BoundsMax[0] > 2.7);

            var f0 = FlightModel.Analyze(g0.Design, g0.Meshes);
            var f1 = FlightModel.Analyze(g1.Design, g1.Meshes);
            double Fuselage(FlightReport f) => f.MassBreakdown.Single(i => i.Name == "Fuselage").Kg;
            Assert.True(Fuselage(f1) > Fuselage(f0) + 50, $"fuselage {Fuselage(f0)} -> {Fuselage(f1)}");
            Assert.True(f1.EmptyMassKg > f0.EmptyMassKg);
            Assert.True(g1.Design.Stats!.BoundsMax[0] >= body.BoundsMax[0] - 1e-9);

            // Self-symmetric shapes on the centre line get no twin; skin is material 0.
            var centred = Templates.Fighter();
            var spine = ShapeMesh.NewPrimitiveShape("box", new[] { 0.4, 0.3, 4 });
            spine.Mirror = true; spine.Position = new[] { 0, 0.8, -1.0 };
            centred.Shapes.Add(spine);
            var g2 = DesignGenerator.Generate(centred);
            Assert.Equal(g0.Meshes.Count + 1, g2.Meshes.Count);
            Assert.All(g2.Meshes.Last().TriangleMaterials, m => Assert.Equal(0, m));

            // Negative scale keeps triangles outward.
            var neg = Templates.Fighter();
            var p = Pod(mirror: false); p.Scale = new[] { -1.0, 1, 1 };
            neg.Shapes.Add(p);
            Assert.True(MeshVolume(DesignGenerator.Generate(neg).Meshes.Last()) > 0);

            // An empty shape is left out with a warning.
            var empty = Templates.Fighter();
            empty.Shapes.Add(new ShapeSpec { Id = "s0", Name = "Nothing" });
            Assert.Contains(DesignGenerator.Generate(empty).Notes, n => n.ItemId == "s0" && n.Level == "warning");
        }

        [Fact]
        public void TurnedFixedBodiesBakeIntoTheSavedMesh()
        {
            var source = new List<MeshData> { FuselageLoft.Build(Templates.Fighter().Fuselages[0]) };
            var spec = new PlaneSpec { Name = "Turned" };
            spec.Fuselages.Add(new FuselageSpec { Id = "f1", Name = "Body", Kind = FuselageSpec.MeshKind, MeshIndex = 0, Scale = 1.2, Position = new[] { 0.5, 0.2, 1 }, RotationDeg = new[] { 0, 30.0, 0 }, Mirror = true, SourceFrontalArea = 1.4 });
            var plane = DesignGenerator.Generate(spec, source);
            Assert.Equal(2, plane.Meshes.Count);   // turned off the centre line: it gets a twin
            var body = plane.Design.Fuselages[0];
            Assert.True(body.FrontalArea > 1.4 * 1.2 * 1.2, $"turned sideways it shows more: {body.FrontalArea}");
            Assert.True(body.BoundsMax[0] - body.BoundsMin[0] > 5, "a 30 degree turn swings the long body sideways");
            var baked = BuilderPackage.Bake(spec, plane);
            Assert.Equal(new double[3], baked.Fuselages[0].RotationDeg);
            Assert.Equal(1, baked.Fuselages[0].Scale);
            var again = DesignGenerator.Generate(baked, plane.Meshes).Design.Fuselages[0];
            for (int i = 0; i < 3; i++) { Assert.Equal(body.BoundsMin[i], again.BoundsMin[i], 4); Assert.Equal(body.BoundsMax[i], again.BoundsMax[i], 4); }
            Assert.Equal(body.FrontalArea, again.FrontalArea, 6);
            Assert.Equal(body.WettedArea, again.WettedArea, 3);
        }

        [Fact]
        public void TurnedAndSmoothedLoftsStayClosed()
        {
            var spec = Templates.Fighter();
            var f = spec.Fuselages[0];
            f.Smooth = 2;
            f.RotationDeg = new[] { 4.0, 0, 0 };
            var mesh = FuselageLoft.Build(f);
            int rings = (f.Rings.Count - 1) * 4 + 1;
            Assert.Equal(rings, FuselageLoft.PlaneRings(f).Count);
            var poly = ShapeMesh.FromMesh(mesh, 1e-5);
            AssertGood(poly, "smoothed, turned loft");

            // Custom points: a flat-bottomed ring keeps the loft closed, and frontal area follows the points.
            var g = Templates.Fighter().Fuselages[0];
            var ring = g.Rings[4];
            var pts = FuselageLoft.Offsets(ring, g.Sides).Select(p => new[] { p.X, System.Math.Max(p.Y, -0.3), 0 }).ToList();
            ring.Points = pts;
            var m2 = FuselageLoft.Build(g);
            AssertGood(ShapeMesh.FromMesh(m2, 1e-5), "custom ring");
            var body = FuselageLoft.Measure(g, m2, 0);
            Assert.True(body.FrontalArea > 0.5);
            // Points of another count are resampled.
            ring.Points = pts.Where((p, i) => i % 2 == 0).ToList();
            Assert.Equal(FuselageLoft.ClampSides(g.Sides), FuselageLoft.Offsets(ring, g.Sides).Length);
            AssertGood(ShapeMesh.FromMesh(FuselageLoft.Build(g), 1e-5), "resampled ring");
        }

        // ------------------------------------------------------------------ obj import

        [Fact]
        public void ObjImportReadsAllFaceFormats()
        {
            var obj = string.Join("\n", new[]
            {
                "# a cube, 2 m, with normals, texture coordinates and negative indices",
                "o Cube", "mtllib x.mtl",
                "v -1 -1 -1", "v 1 -1 -1", "v 1 1 -1", "v -1 1 -1", "v -1 -1 1", "v 1 -1 1", "v 1 1 1", "v -1 1 1",
                "vn 0 0 1", "vt 0 0",
                "usemtl skin",
                "f 1//1 4//1 3//1 2//1",          // back (z = -1), right-handed outward winding
                "f 5/1/1 6/1/1 7/1/1 8/1/1",      // front
                "f -8 -7 -3 -4",                  // bottom: 1 2 6 5
                "f 4 8 7 3",                      // top
                "f 1/1 5/1 8/1 4/1",              // left
                "f 2 3 7 6",                      // right
            });
            var r = ShapeMesh.ImportObj(obj);
            Assert.Equal(12, r.Triangles);
            Assert.Null(r.Warning);
            AssertGood(r.Mesh, "obj cube");
            Assert.Equal(8.0, ShapeMesh.SignedVolume(r.Mesh), 6);
            var (min, max) = ShapeMesh.Bounds(r.Mesh.Vertices);
            Assert.Equal(-1, min.X, 6); Assert.Equal(1, max.Z, 6);

            // Inside-out input comes out outward too; broken faces are reported.
            var inverted = obj.Replace("f 1//1 4//1 3//1 2//1", "f 1//1 2//1 3//1 4//1").Replace("f 5/1/1 6/1/1 7/1/1 8/1/1", "f 8 7 6 5")
                .Replace("f -8 -7 -3 -4", "f 5 6 2 1").Replace("f 4 8 7 3", "f 3 7 8 4").Replace("f 1/1 5/1 8/1 4/1", "f 4 8 5 1").Replace("f 2 3 7 6", "f 6 7 3 2") + "\nf 1 1 2";
            var ri = ShapeMesh.ImportObj(inverted);
            AssertGood(ri.Mesh, "inverted obj cube");
            Assert.Contains("broken", ri.Warning);

            Assert.Throws<MeshOpException>(() => ShapeMesh.ImportObj("v 0 0 0\nv 1 0 0\n"));
            Assert.Throws<MeshOpException>(() => ShapeMesh.ImportObj("v 0 0 0\nf 1 2 3\n"));
        }

        [Fact]
        public void FlyoutReferenceModelImportsAtThePlanesSize()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? craftRoot = null;
            while (dir != null && craftRoot == null)
            {
                var c = Path.Combine(dir.FullName, "samples-private", "flyout", "craft");
                if (Directory.Exists(c)) craftRoot = c;
                dir = dir.Parent;
            }
            var objFile = craftRoot == null ? null : Directory.GetFiles(craftRoot, "su57.obj", SearchOption.AllDirectories).FirstOrDefault();
            if (objFile == null) { _out.WriteLine("samples-private su57.obj not found; skipped."); return; }
            var r = ShapeMesh.ImportObj(File.ReadAllText(objFile));
            _out.WriteLine(r.Warning);
            Assert.True(r.Triangles > ShapeMesh.WarnTriangles);
            Assert.Contains("triangles", r.Warning);
            var (min, max) = ShapeMesh.Bounds(r.Mesh.Vertices);
            var size = max - min;
            var converted = FlyoutConverter.Convert(FlyoutCraft.Load(Path.GetDirectoryName(Path.GetDirectoryName(objFile))!)).Design.Stats!;
            _out.WriteLine($"imported {size}, converted length {converted.LengthM:0.00} span {converted.SpanM:0.00} height {converted.HeightM:0.00}");
            Assert.InRange(size.Z, converted.LengthM * 0.85, converted.LengthM * 1.15);
            Assert.InRange(size.X, converted.SpanM * 0.85, converted.SpanM * 1.15);
            // The nose (the thin end) points forward: the front tenth is much narrower than the back tenth.
            double Width(Func<Vec3, bool> where) { var p = r.Mesh.Vertices.Where(where).ToList(); return p.Max(v => v.X) - p.Min(v => v.X); }
            Assert.True(Width(v => v.Z > max.Z - 0.1 * size.Z) < 0.5 * Width(v => v.Z < min.Z + 0.1 * size.Z));
            Assert.True(ShapeMesh.SignedVolume(r.Mesh) > 0);
        }

        // ------------------------------------------------------------------ builder.json version 1

        private static string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name), new UTF8Encoding(false));
        private static string Lf(string s) => s.Replace("\r\n", "\n");

        [Theory]
        [InlineData("v1-fighter")]
        [InlineData("v1-trainer-twin")]
        public void VersionOneBuilderFilesOpenAndRegenerateIdentically(string name)
        {
            var v1Spec = Data(name + ".builder.json");
            var v1Design = Data(name + ".aircraft.json");
            Assert.Contains("\"openplane-builder/1\"", v1Spec);

            // Straight from the JSON: same design, to the byte.
            var spec = PlaneSpec.Upgrade(OplanePackage.FromJson<PlaneSpec>(v1Spec));
            Assert.Equal(PlaneSpec.CurrentFormat, spec.Format);
            Assert.Empty(spec.Shapes); Assert.Empty(spec.References); Assert.NotNull(spec.Editor);
            var again = DesignGenerator.Generate(spec);
            Assert.Equal(Lf(v1Design), Lf(OplanePackage.ToJson(again.Design)));

            // Through a .oplane file written by the old app.
            var designBytes = new UTF8Encoding(false).GetBytes(v1Design);
            var specText = System.Text.RegularExpressions.Regex.Replace(v1Spec, "\"design_hash\": \"[0-9a-f]+\"", $"\"design_hash\": \"{BuilderJson.Sha1(designBytes)}\"");
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (entry, bytes) in new[] { ("aircraft.json", designBytes), ("builder.json", new UTF8Encoding(false).GetBytes(specText)) })
                    using (var s = zip.CreateEntry(entry).Open()) s.Write(bytes, 0, bytes.Length);
                using (var s = new StreamWriter(zip.CreateEntry("meshes.json").Open())) s.Write(OplanePackage.ToJson(again.Meshes));
            }
            ms.Position = 0;
            var opened = BuilderPackage.Read(ms);
            Assert.True(opened.FromBuilderData, opened.Note);
            Assert.Equal(PlaneSpec.CurrentFormat, opened.Spec.Format);
            Assert.Equal(Lf(v1Design), Lf(OplanePackage.ToJson(DesignGenerator.Generate(opened.Spec, opened.SourceMeshes).Design)));
        }

        [Fact]
        public void NewerBuilderFormatsOpenAsFixedShapes()
        {
            var spec = Templates.Trainer();
            spec.Format = "openplane-builder/3";
            var plane = DesignGenerator.Generate(Templates.Trainer());
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var design = new UTF8Encoding(false).GetBytes(OplanePackage.ToJson(plane.Design));
                using (var s = zip.CreateEntry("aircraft.json").Open()) s.Write(design, 0, design.Length);
                using (var s = new StreamWriter(zip.CreateEntry("meshes.json").Open())) s.Write(OplanePackage.ToJson(plane.Meshes));
                using (var s = new StreamWriter(zip.CreateEntry("builder.json").Open())) s.Write(OplanePackage.ToJson(spec));
            }
            ms.Position = 0;
            var opened = BuilderPackage.Read(ms);
            Assert.False(opened.FromBuilderData);
            Assert.Contains("newer", opened.Note);
        }

        // ------------------------------------------------------------------ wings and engines

        [Fact]
        public void TwistSlatsAndOverridesShapeTheWing()
        {
            var w = Templates.Fighter().Wings[0];
            var plain = DesignGenerator.BuildWing(w)[0];
            w.TwistDeg = -3;
            w.Surfaces.Add(new SurfaceSpec { Type = "slat", FromPct = 20, ToPct = 90, ChordPct = 15 });
            w.Surfaces[1].Inputs = new List<InputLink> { new InputLink { Axis = "roll", Gain = -12 } };
            w.Surfaces[1].MaxDeg = 15;
            var panels = DesignGenerator.BuildWing(w);
            var right = panels[0]; var left = panels[1];
            Assert.Equal(-3, right.TipTwistDeg);
            // Washout lifts the tip's trailing edge.
            Assert.True(right.TipTrailing[1] > plain.TipTrailing[1] + 0.01);
            var slat = right.Surfaces.Single(s => s.Role == "slat");
            Assert.Equal("leading", slat.Edge);
            Assert.Equal("flaps", slat.Inputs.Single().Axis);
            Assert.Contains(right.Surfaces, s => s.Role == "fixed" && s.Edge == "leading");
            // The panel's leading edge moved back behind the slat strip.
            Assert.True(right.RootLeading[2] < plain.RootLeading[2]);
            var ail = right.Surfaces.Single(s => s.Role == "aileron");
            Assert.Equal(-12, ail.Inputs.Single().Gain);
            Assert.Equal(15, ail.MaxDeg);
            Assert.Equal(12, left.Surfaces.Single(s => s.Role == "aileron").Inputs.Single().Gain);

            var tail = Templates.Fighter().Wings[2];
            tail.Surfaces[0].Type = "ruddervator";
            var rv = DesignGenerator.BuildWing(tail)[0].Surfaces.Single(s => s.Role == "ruddervator");
            Assert.Equal(new[] { "pitch", "yaw" }, rv.Inputs.Select(i => i.Axis));
        }

        [Fact]
        public void DesignedJetsGetThrustFromSizeAndTemperature()
        {
            // The rule's fit points: Jumo 004B and AL-31F.
            Assert.InRange(DesignGenerator.JetThrustN(0.74, 1050), 8000, 9600);
            Assert.InRange(DesignGenerator.JetThrustN(1.28, 1665), 70000, 82000);
            var spec = Templates.Fighter();
            var e = spec.Engines[0];
            e.Sizing = EngineSpec.SizingDesigned; e.DiameterM = 1.0; e.TurbineTempK = 1500; e.Afterburner = true; e.Variant = "turbofan";
            var d = DesignGenerator.Generate(spec).Design.Engines[0];
            Assert.Equal("turbofan", d.Type);
            Assert.Equal(System.Math.Round(DesignGenerator.JetThrustN(1.0, 1500)), d.MaxThrustN);
            Assert.Equal(System.Math.Round(DesignGenerator.JetThrustN(1.0, 1500) * 1.55), d.AfterburnerThrustN);
            e.Variant = "ramjet";
            Assert.Equal("turbojet", DesignGenerator.Generate(spec).Design.Engines[0].Type);
            e.Variant = "electric"; e.PowerKw = 500; e.PropDiameterM = 2.2;
            var el = DesignGenerator.Generate(spec).Design.Engines[0];
            Assert.Equal("electric-prop", el.Type);
            Assert.Equal(System.Math.Round(DesignGenerator.PropThrustN(500, 2.2)), el.MaxThrustN);
        }
    }
}
