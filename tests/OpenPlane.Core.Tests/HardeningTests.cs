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
    /// <summary>Damaged or hostile files, limits, identity and the converter's payload rule.</summary>
    public class HardeningTests
    {
        private readonly ITestOutputHelper _out;
        public HardeningTests(ITestOutputHelper output) { _out = output; }

        private static GeneratedPlane Fighter() => DesignGenerator.Generate(Templates.Fighter());

        /// <summary>A .oplane built by hand from entries (name, text or bytes).</summary>
        private static byte[] Zip(params (string name, object content)[] entries)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in entries)
                {
                    var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                    var bytes = content as byte[] ?? new UTF8Encoding(false).GetBytes((string)content);
                    using (var s = e.Open()) s.Write(bytes, 0, bytes.Length);
                }
            return ms.ToArray();
        }

        private static string DesignJson(Action<AircraftDesign>? change = null)
        {
            var g = Fighter();
            change?.Invoke(g.Design);
            return OplanePackage.ToJson(g.Design);
        }

        private static string MeshesJson() => Newtonsoft.Json.JsonConvert.SerializeObject(Fighter().Meshes, OplanePackage.Json);

        private static InvalidDataException Refused(byte[] file) => Assert.Throws<InvalidDataException>(() => OplanePackage.Read(new MemoryStream(file)));

        [Fact]
        public void ANormalPlaneReads()
        {
            var c = OplanePackage.Read(new MemoryStream(Zip((OplanePackage.DesignEntry, DesignJson()), (OplanePackage.MeshesEntry, MeshesJson()))));
            Assert.NotEmpty(c.Meshes);
            Assert.Equal(64, c.ContentHash.Length);
        }

        [Fact]
        public void NonFiniteNumbersAreRefused()
        {
            var json = DesignJson().Replace("\"root_twist_deg\": 0.0", "\"root_twist_deg\": NaN");
            Assert.Contains("NaN", json);
            var e = Refused(Zip((OplanePackage.DesignEntry, json), (OplanePackage.MeshesEntry, MeshesJson())));
            _out.WriteLine(e.Message);
            Assert.Contains("root_twist_deg", e.Message);

            // Writing one is refused too, instead of silently becoming 0.
            var g = Fighter();
            g.Design.Wings[0].TipLeading[1] = double.PositiveInfinity;
            var path = Path.Combine(Path.GetTempPath(), "openplane-nan-" + Guid.NewGuid().ToString("N") + ".oplane");
            Assert.Throws<InvalidDataException>(() => OplanePackage.Write(path, g.Design, g.Meshes));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void BrokenMeshesAndShapesAreRefused()
        {
            var g = Fighter();
            var bad = g.Meshes.Select(m => new MeshData { Name = m.Name, Vertices = m.Vertices, Triangles = m.Triangles.ToArray(), TriangleMaterials = m.TriangleMaterials }).ToList();
            bad[0].Triangles[0] = bad[0].Vertices.Length; // points past the last corner
            var meshes = Newtonsoft.Json.JsonConvert.SerializeObject(bad, OplanePackage.Json);
            Assert.Contains("corner", Refused(Zip((OplanePackage.DesignEntry, DesignJson()), (OplanePackage.MeshesEntry, meshes))).Message);

            bad[0].Triangles = bad[0].Triangles.Take(bad[0].Triangles.Length - 1).ToArray(); // not whole triangles
            meshes = Newtonsoft.Json.JsonConvert.SerializeObject(bad, OplanePackage.Json);
            Assert.Contains("multiple of 3", Refused(Zip((OplanePackage.DesignEntry, DesignJson()), (OplanePackage.MeshesEntry, meshes))).Message);

            // A body pointing at a mesh that isn't there, a point with two numbers, a rotation with three.
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Fuselages[0].MeshIndex = 99)), (OplanePackage.MeshesEntry, MeshesJson())));
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Wings[0].RootLeading = new double[] { 1, 2 })), (OplanePackage.MeshesEntry, MeshesJson())));
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Hardpoints.Add(new HardpointDef { Rotation = new double[] { 0, 0, 1 } }))), (OplanePackage.MeshesEntry, MeshesJson())));
        }

        [Fact]
        public void OnlyFormatOneIsRead()
        {
            Assert.Contains("format", Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Format = null!)))).Message);
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Format = "openplane/2"))));
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Format = "openplane/10"))));
            Refused(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Format = "something/1"))));
            OplanePackage.Read(new MemoryStream(Zip((OplanePackage.DesignEntry, DesignJson(d => d.Format = "openplane/1.1")), (OplanePackage.MeshesEntry, MeshesJson()))));
        }

        [Fact]
        public void OversizedFilesAreRefusedBeforeTheyAreRead()
        {
            // Too many entries.
            var many = Enumerable.Range(0, OplanePackage.Limits.MaxEntries + 1).Select(i => ($"x{i}.txt", (object)"x")).ToList();
            many.Add((OplanePackage.DesignEntry, DesignJson()));
            Assert.Contains("entries", Refused(Zip(many.ToArray())).Message);

            // An entry that unpacks bigger than the design limit (zeros compress to almost nothing).
            var bomb = new byte[(int)OplanePackage.Limits.MaxDesignBytes + 1024];
            Assert.Contains("too big", Refused(Zip((OplanePackage.DesignEntry, bomb))).Message);
        }

        [Fact]
        public void AnEntryBiggerThanItsZipSaysIsNotRead()
        {
            // A stored entry whose declared uncompressed size (in both headers) is smaller than its data.
            var text = DesignJson();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e = zip.CreateEntry(OplanePackage.DesignEntry, CompressionLevel.NoCompression);
                using (var s = e.Open()) { var b = Encoding.UTF8.GetBytes(text); s.Write(b, 0, b.Length); }
            }
            var bytes = ms.ToArray();
            int real = Encoding.UTF8.GetByteCount(text);
            int patched = 0;
            for (int i = 0; i + 4 <= bytes.Length; i++)
            {
                if (BitConverter.ToInt32(bytes, i) == real && (i >= 22 && BitConverter.ToUInt32(bytes, i - 22) == 0x04034b50 || i >= 24 && BitConverter.ToUInt32(bytes, i - 24) == 0x02014b50))
                {
                    var small = BitConverter.GetBytes(64);
                    Array.Copy(small, 0, bytes, i, 4);
                    patched++;
                }
            }
            Assert.True(patched >= 1);
            Assert.ThrowsAny<Exception>(() => OplanePackage.Read(new MemoryStream(bytes)));
        }

        [Fact]
        public void ContentHashIgnoresTheZipAndExtras()
        {
            var design = DesignJson(); var meshes = MeshesJson();
            var a = OplanePackage.Read(new MemoryStream(Zip((OplanePackage.DesignEntry, design), (OplanePackage.MeshesEntry, meshes))));
            var b = OplanePackage.Read(new MemoryStream(Zip((OplanePackage.MeshesEntry, meshes), ("builder.json", "{}"), (OplanePackage.DesignEntry, design), ("thumb.png", new byte[] { 1, 2, 3 }))));
            Assert.Equal(a.ContentHash, b.ContentHash);
            var c = OplanePackage.Read(new MemoryStream(Zip((OplanePackage.DesignEntry, design.Replace("\"Fighter\"", "\"Fighter 2\"")), (OplanePackage.MeshesEntry, meshes))));
            Assert.NotEqual(a.ContentHash, c.ContentHash);
            // The length prefix: moving bytes from one entry to the other changes the hash.
            Assert.NotEqual(OplanePackage.HashContent(new byte[] { 1, 2 }, new byte[] { 3 }), OplanePackage.HashContent(new byte[] { 1 }, new byte[] { 2, 3 }));
        }

        [Fact]
        public void GameKeysComeFromTheIdOrTheContent()
        {
            var d = Fighter().Design;
            d.Id = "  My Plane/ID!! ";
            Assert.Equal("op.my-plane-id", OplanePackage.GameKey(d, "ab"));
            d.Id = null;
            var hash = new string('c', 64);
            Assert.Equal("op.h" + new string('c', 16), OplanePackage.GameKey(d, hash));
            Assert.Matches("^[0-9a-f]{16}$", OplanePackage.NewId());
        }

        [Fact]
        public void SharingWithoutReferencesKeepsIdentityAndUnknownKeys()
        {
            var g = Fighter();
            g.Design.Id = "abc123";
            var builder = "{\n  \"format\": \"openplane-builder/2\",\n  \"future_key\": { \"x\": 1 },\n  \"references\": [ { \"id\": \"r\", \"image\": \"" + new string('a', 40) + ".png\" } ]\n}";
            var file = Zip((OplanePackage.DesignEntry, OplanePackage.ToJson(g.Design)), (OplanePackage.MeshesEntry, Newtonsoft.Json.JsonConvert.SerializeObject(g.Meshes, OplanePackage.Json)),
                (BuilderPackage.SpecEntry, builder), (BuilderPackage.ReferencesFolder + new string('a', 40) + ".png", new byte[] { 1, 2, 3 }));
            var shared = BuilderPackage.WithoutReferences(file);
            using (var zip = new ZipArchive(new MemoryStream(shared)))
            {
                Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(BuilderPackage.ReferencesFolder));
                using (var r = new StreamReader(zip.GetEntry(BuilderPackage.SpecEntry)!.Open()))
                {
                    var text = r.ReadToEnd();
                    Assert.Contains("future_key", text);
                    Assert.DoesNotContain("references", text);
                }
            }
            var before = OplanePackage.Read(new MemoryStream(file));
            var after = OplanePackage.Read(new MemoryStream(shared));
            Assert.Equal(before.ContentHash, after.ContentHash);
            Assert.Equal(OplanePackage.GameKey(before.Design, before.ContentHash), OplanePackage.GameKey(after.Design, after.ContentHash));
        }

        [Fact]
        public void StoredStatsAreNeverTrusted()
        {
            var g = Fighter();
            var honest = FlightModel.Analyze(g.Design, g.Meshes);
            g.Design.Stats = new DesignStatsResult(); // "stats": {} in a file
            var tricked = FlightModel.Analyze(g.Design, g.Meshes);
            Assert.Equal(honest.EmptyMassKg, tricked.EmptyMassKg);
            Assert.Equal(honest.StaticMarginPercent, tricked.StaticMarginPercent, 6);
        }

        [Fact]
        public void FlyoutNumbersMustBeFinite()
        {
            Assert.False(FlyoutValues.TryDouble("NaN", out _));
            Assert.False(FlyoutValues.TryDouble("Infinity", out _));
            Assert.False(FlyoutValues.TryDouble("-Infinity", out _));
            Assert.Equal(new double[] { 1, 3 }, FlyoutValues.Numbers("1,NaN,3"));
            Assert.True(FlyoutValues.TryDouble("2.5", out var v) && v == 2.5);
        }

        private const string LoopCraft = @"Loop
Version=0.2504
Part
{
 resource_path=Parts/Box
 id=1
 parent_id=3
 position=1,0,0
}
Part
{
 resource_path=Parts/Box
 id=2
 parent_id=1
 position=0,1,0
}
Part
{
 resource_path=Parts/Box
 id=3
 parent_id=2
 position=0,0,1
}
";

        [Fact]
        public void APartLoopIsCutAndEveryPartComposesWithAKnownParent()
        {
            var craft = FlyoutCraft.FromText("loop", LoopCraft, null);
            Assert.Contains(craft.Warnings, w => w.Contains("own ancestor"));
            var byId = craft.Parts.ToDictionary(p => p.Id);
            Assert.True(Vec3.Distance(byId[1].WorldPose.Position, new Vec3(1, 0, 0)) < 1e-9);
            Assert.True(Vec3.Distance(byId[2].WorldPose.Position, new Vec3(1, 1, 0)) < 1e-9);
            Assert.True(Vec3.Distance(byId[3].WorldPose.Position, new Vec3(1, 1, 1)) < 1e-9);
        }

        [Fact]
        public void AHugeFuselageGridDoesNotOverflow()
        {
            const string text = @"Big
Version=0.2504
Part
{
 resource_path=Parts/EditableFuselage16
 id=1
 parent_id=-1
 *EditableFuselage
 {
  segments=65536
  slices=65536
 }
}
";
            var r = FlyoutConverter.Convert(FlyoutCraft.FromText("big", text, null));
            Assert.Empty(r.Design.Fuselages);
            Assert.Contains(r.Report.Items, i => i.Level == ReportLevel.Problem && i.Message.Contains("expected"));
        }

        [Fact]
        public void SmoothingIsLoweredOrRefusedAboveTheTriangleCap()
        {
            PolyMesh Triangles(int n) => new PolyMesh
            {
                Vertices = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) },
                Faces = Enumerable.Range(0, n).Select(_ => new[] { 0, 1, 2 }).ToList(),
            };
            // 70,000 triangles: one level gives 420,000, two would give 1.68 million.
            Assert.Equal(1, ShapeMesh.SubdivisionLevelFor(Triangles(70000), 3, out var est));
            Assert.True(est <= ShapeMesh.MaxOutputTriangles);
            Assert.Equal(3, ShapeMesh.SubdivisionLevelFor(Triangles(100), 3, out _));
            Assert.Throws<MeshOpException>(() => ShapeMesh.SubdivisionLevelFor(Triangles(ShapeMesh.MaxOutputTriangles + 1), 0, out _));
            // The estimate matches what Catmull-Clark really makes.
            var cube = ShapeMesh.Primitive("box");
            var once = ShapeMesh.CatmullClark(cube.Vertices, cube.Faces);
            var twice = ShapeMesh.CatmullClark(once.vertices, once.faces);
            Assert.Equal(ShapeMesh.TriangleCount(twice.faces), ShapeMesh.EstimateTriangles(cube, 2));
        }

        [Fact]
        public void OverlappingSurfacesAreTrimmedAgainstTheFurthestEnd()
        {
            var w = new WingSpec
            {
                Id = "w", Name = "Wing", Mirror = false, Root = new double[] { 0, 0, 0 }, Span = 10, RootChord = 2, TipChord = 2,
                Surfaces = new List<SurfaceSpec>
                {
                    new SurfaceSpec { Type = "aileron", FromPct = 0, ToPct = 80, ChordPct = 25 },
                    new SurfaceSpec { Type = "flap", FromPct = 10, ToPct = 30, ChordPct = 25 },
                    new SurfaceSpec { Type = "airbrake", FromPct = 50, ToPct = 90, ChordPct = 25 },
                },
            };
            var notes = new List<BuilderNote>();
            var panel = DesignGenerator.BuildWing(w, notes).Single();
            var spans = panel.Surfaces.Select(s => (role: s.Role, from: s.RootLeading[0], to: s.TipLeading[0])).OrderBy(s => s.from).ToList();
            foreach (var s in spans) _out.WriteLine($"{s.role}: {s.from:0.00} - {s.to:0.00}");
            for (int i = 1; i < spans.Count; i++) Assert.True(spans[i].from >= spans[i - 1].to - 1e-6, "surfaces overlap");
            Assert.Contains(notes, n => n.Message.Contains("left out"));
        }

        [Fact]
        public void MirroredLoftsOnTheCentreLineGetATwinOnlyWhenLopsided()
        {
            FuselageSpec Loft(double x, double yaw) => new FuselageSpec
            {
                Id = "f", Name = "Pod", Mirror = true, Position = new[] { x, 0, 0 }, RotationDeg = new[] { 0, yaw, 0 }, Sides = 16,
                Rings = new List<FuselageRing> { new FuselageRing { Z = 2 }, new FuselageRing { Z = -2 } },
            };
            int Bodies(FuselageSpec f) => DesignGenerator.Generate(new PlaneSpec { Fuselages = new List<FuselageSpec> { f } }).Design.Fuselages.Count;
            Assert.Equal(1, Bodies(Loft(0, 0)));       // symmetric on the centre line (as in version 1)
            Assert.Equal(1, Bodies(Loft(0.005, 0)));   // within the centre tolerance (as in version 1)
            Assert.Equal(2, Bodies(Loft(0, 20)));      // turned: not its own mirror image
            Assert.Equal(2, Bodies(Loft(1, 0)));       // off the centre line (as in version 1)
        }

        [Fact]
        public void AFixedBodyWithoutItsMeshIsNotSavedAsAnotherBody()
        {
            var spec = new PlaneSpec
            {
                Fuselages = new List<FuselageSpec>
                {
                    new FuselageSpec { Id = "a", Name = "Kept", Kind = FuselageSpec.MeshKind, MeshIndex = 0 },
                    new FuselageSpec { Id = "b", Name = "Lost", Kind = FuselageSpec.MeshKind, MeshIndex = 5 },
                },
            };
            var source = new List<MeshData> { Fighter().Meshes[0] };
            var plane = DesignGenerator.Generate(spec, source);
            var baked = BuilderPackage.Bake(spec, plane);
            Assert.Equal(0, baked.Fuselages.Single(f => f.Id == "a").MeshIndex);
            Assert.Equal(-1, baked.Fuselages.Single(f => f.Id == "b").MeshIndex);
        }

        [Fact]
        public void DonorsAreChosenBySize()
        {
            Assert.Equal("Fighter1", DonorPicker.PickJet(12.5, 3400));      // Me-262: two jets, 12.5 m, 3.4 t
            Assert.Equal("Fighter1", DonorPicker.PickJet(15.8, 8700));
            Assert.Equal("Multirole1", DonorPicker.PickJet(19, 16000));
            Assert.Equal("FastBomber1", DonorPicker.PickJet(26, 32000));
            Assert.Equal("Fighter1", DonorPicker.PickJet(0, 0));
        }

        // ------------------------------------------------------------------ converter: weapons are never airframe

        private static string? CraftFolder(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples-private", "flyout", "craft"))) dir = dir.Parent;
            if (dir == null) return null;
            return Directory.GetDirectories(Path.Combine(dir.FullName, "samples-private", "flyout", "craft"), "*", SearchOption.AllDirectories)
                .FirstOrDefault(d => Path.GetFileName(d) == name && File.Exists(Path.Combine(d, "data.txt")));
        }

        [Fact]
        public void HardpointPayloadsNeverBecomeAirframe()
        {
            var folder = CraftFolder("AB-4 Alkyon");
            if (folder == null) { _out.WriteLine("samples-private not found; skipped."); return; }
            var craft = FlyoutCraft.Load(folder);
            var payload = FlyoutConverter.PayloadParts(craft);
            Assert.NotEmpty(payload);
            var d = FlyoutConverter.Convert(craft).Design;
            Assert.DoesNotContain(d.Fuselages, f => f.SourcePartId.HasValue && payload.Contains(f.SourcePartId.Value));
            Assert.DoesNotContain(d.Engines, e => e.SourcePartId.HasValue && payload.Contains(e.SourcePartId.Value));
            Assert.DoesNotContain(d.Wings, w => w.SourcePartId.HasValue && payload.Contains(w.SourcePartId.Value));
            Assert.DoesNotContain(d.Gear, g => g.SourcePartId.HasValue && payload.Contains(g.SourcePartId.Value));
            Assert.DoesNotContain(d.Hardpoints, h => h.SourcePartId.HasValue && payload.Contains(h.SourcePartId.Value)); // no nested stations
            // The missiles' small motors (~1.8 kN each) are gone; the bomber's own engines stay.
            Assert.All(d.Engines, e => Assert.True(e.MaxThrustN > 5000, $"{e.Title}: {e.MaxThrustN} N"));
            _out.WriteLine($"AB-4: {d.Fuselages.Count} bodies, {d.Engines.Count} engines, {d.Hardpoints.Count} hardpoints, donor {d.Donor}");
        }

        [Fact]
        public void ValidatorRepairsSmallMistakesAndRefusesAbsurdSizes()
        {
            var d = new AircraftDesign();
            d.Wings.Add(new WingPanel
            {
                RootLeading = new double[] { 0, 0, 1 }, RootTrailing = new double[] { 0, 0, -1 }, TipLeading = new double[] { 3, 0, 0.5 }, TipTrailing = new double[] { 3, 0, -0.5 },
                Up = new double[] { 0, 0, 0 }, Inputs = new List<InputLink> { new InputLink { Axis = "pitch", Gain = 20 } }, MinDeg = 20, MaxDeg = -20,
                Surfaces = new List<SurfaceDef> { new SurfaceDef { Role = "wobbler", Edge = "sideways", MinDeg = 10, MaxDeg = -10 } },
            });
            DesignValidator.Check(d, new List<MeshData>());
            var w = d.Wings[0];
            Assert.Equal(new double[] { 0, 1, 0 }, w.Up);
            Assert.True(w.MinDeg <= w.MaxDeg);
            Assert.Equal("other", w.Surfaces[0].Role);
            Assert.Equal("trailing", w.Surfaces[0].Edge);
            Assert.True(w.Surfaces[0].MinDeg <= w.Surfaces[0].MaxDeg);

            w.TipLeading = new double[] { 5000, 0, 0 };
            Assert.Throws<InvalidDataException>(() => DesignValidator.Check(d, new List<MeshData>()));
        }

        [Theory]
        [InlineData("Sukhoi Su-57 'Felon'")]
        [InlineData("SAH-46 Chicane")]
        [InlineData("AF-02-Grève-A1")]
        [InlineData("KR-67 Ifrit")]
        public void NoSampleTurnsItsWeaponsIntoBodies(string name)
        {
            var folder = CraftFolder(name);
            if (folder == null) { _out.WriteLine("samples-private not found; skipped."); return; }
            var craft = FlyoutCraft.Load(folder);
            var payload = FlyoutConverter.PayloadParts(craft);
            var d = FlyoutConverter.Convert(craft).Design;
            Assert.DoesNotContain(d.Fuselages, f => f.SourcePartId.HasValue && payload.Contains(f.SourcePartId.Value));
            Assert.DoesNotContain(d.Engines, e => e.SourcePartId.HasValue && payload.Contains(e.SourcePartId.Value));
            _out.WriteLine($"{name}: {payload.Count} payload parts; {d.Fuselages.Count} bodies, {d.Engines.Count} engines, {d.Hardpoints.Count} hardpoints, donor {d.Donor}");
        }
    }
}
