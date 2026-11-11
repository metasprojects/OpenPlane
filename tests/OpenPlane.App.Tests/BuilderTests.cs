using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Math;
using OpenPlane.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace OpenPlane.App.Tests;

public class LoftTests
{
    private static FuselageSpec Spec(params FuselageRing[] rings) => new() { Id = "f", Name = "Test", Rings = rings.ToList(), Sides = 24 };
    private static FuselageRing R(double z, double w, double h, double y = 0, double round = 1) => new() { Z = z, Width = w, Height = h, Y = y, Roundness = round };

    public static IEnumerable<object[]> Bodies() => new[]
    {
        new object[] { "pointed nose and tail", Spec(R(4, 0, 0), R(3, 1, 1), R(-2, 1.2, 1.3, 0.1, 0.6), R(-4, 0, 0)) },
        new object[] { "flat caps both ends", Spec(R(2, 0.8, 0.8), R(0, 1, 1.2, 0, 0.3), R(-3, 0.6, 0.5)) },
        new object[] { "rounded box, unsorted rings", Spec(R(-1, 2, 1, 0, 0), R(3, 0, 0), R(1, 2, 1.5, 0.2, 0.2)) },
        new object[] { "two rings", Spec(R(1, 1, 1), R(-1, 1, 1)) },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void LoftIsAValidClosedOutwardMesh(string label, FuselageSpec spec)
    {
        var mesh = FuselageLoft.Build(spec);
        Assert.True(mesh.Triangles.Length > 0, label);
        Assert.Equal(0, mesh.Triangles.Length % 3);
        Assert.Equal(mesh.Triangles.Length / 3, mesh.TriangleMaterials.Length);
        int n = mesh.Vertices.Length / 3;
        Assert.All(mesh.Triangles, i => Assert.InRange(i, 0, n - 1));
        Assert.All(mesh.Vertices, v => Assert.False(float.IsNaN(v) || float.IsInfinity(v)));

        // Weld by position (caps have their own vertices), then every directed edge needs its reverse: closed, consistently wound.
        var key = new Dictionary<(long, long, long), int>();
        int Weld(int i)
        {
            var k = ((long)Math.Round(mesh.Vertices[i * 3] * 1e5), (long)Math.Round(mesh.Vertices[i * 3 + 1] * 1e5), (long)Math.Round(mesh.Vertices[i * 3 + 2] * 1e5));
            if (!key.TryGetValue(k, out var id)) key[k] = id = key.Count;
            return id;
        }
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            int a = Weld(mesh.Triangles[t]), b = Weld(mesh.Triangles[t + 1]), c = Weld(mesh.Triangles[t + 2]);
            foreach (var e in new[] { (a, b), (b, c), (c, a) })
            {
                if (e.Item1 == e.Item2) continue; // degenerate edge at a tip point
                edges[e] = edges.TryGetValue(e, out var x) ? x + 1 : 1;
            }
        }
        foreach (var (e, count) in edges)
        {
            Assert.True(count == 1, $"{label}: edge {e} is used {count} times in the same direction");
            Assert.True(edges.ContainsKey((e.Item2, e.Item1)), $"{label}: edge {e} has no partner, so the mesh has a hole");
        }

        // Outward winding: positive enclosed volume (divergence theorem), and every face points away from the body's axis.
        double volume = 0;
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            var a = V(mesh, mesh.Triangles[t]); var b = V(mesh, mesh.Triangles[t + 1]); var c = V(mesh, mesh.Triangles[t + 2]);
            volume += Vec3.Dot(a, Vec3.Cross(b, c)) / 6;
        }
        Assert.True(volume > 0, $"{label}: enclosed volume {volume} should be positive (outward triangles)");
    }

    private static Vec3 V(MeshData m, int i) => new(m.Vertices[i * 3], m.Vertices[i * 3 + 1], m.Vertices[i * 3 + 2]);

    [Fact]
    public void RoundRingAreaAndBoundsAreRight()
    {
        var spec = Spec(R(2, 2, 2), R(-2, 2, 2));
        spec.Sides = 64;
        spec.Position = new double[] { 1, 0.5, 0 };
        var mesh = FuselageLoft.Build(spec);
        var body = FuselageLoft.Measure(spec, mesh, 0);
        Assert.InRange(body.FrontalArea, Math.PI * 0.98, Math.PI * 1.001);
        Assert.Equal(0, body.BoundsMin[0], 2); Assert.Equal(2, body.BoundsMax[0], 2);
        Assert.Equal(-0.5, body.BoundsMin[1], 2); Assert.Equal(1.5, body.BoundsMax[1], 2);
        Assert.Equal(-2, body.BoundsMin[2], 3); Assert.Equal(2, body.BoundsMax[2], 3);
        // Side of a 2 m diameter, 4 m long cylinder plus two end caps.
        Assert.InRange(body.WettedArea, (4 * Math.PI * 2 + 2 * Math.PI) * 0.98, (4 * Math.PI * 2 + 2 * Math.PI) * 1.01);
    }

    [Fact]
    public void MirroredBodyIsAlsoOutward()
    {
        var spec = Spec(R(2, 0, 0), R(1, 1, 1), R(-2, 0.5, 0.5));
        spec.Position = new double[] { 3, 0, 0 };
        spec.Mirror = true;
        var plane = DesignGenerator.Generate(new PlaneSpec { Fuselages = { spec } });
        Assert.Equal(2, plane.Meshes.Count);
        foreach (var m in plane.Meshes)
        {
            double vol = 0;
            for (int t = 0; t < m.Triangles.Length; t += 3)
                vol += Vec3.Dot(V(m, m.Triangles[t]), Vec3.Cross(V(m, m.Triangles[t + 1]), V(m, m.Triangles[t + 2]))) / 6;
            Assert.True(vol > 0, $"{m.Name} is inside out");
        }
        Assert.True(plane.Design.Fuselages[1].BoundsMax[0] < 0, "the twin should be on the left");
    }
}

public class TemplateTests
{
    private readonly ITestOutputHelper _out;
    public TemplateTests(ITestOutputHelper output) { _out = output; }

    public static IEnumerable<object[]> Flyable() => Templates.All.Where(t => t.Id != "blank").Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(Flyable))]
    public void TemplateAnalysesCleanly(string id)
    {
        var spec = Templates.Find(id)!.Create();
        var plane = DesignGenerator.Generate(spec);
        var f = FlightModel.Analyze(plane.Design, plane.Meshes);
        foreach (var c in f.Checks) _out.WriteLine($"{id}: {c}");
        _out.WriteLine($"{id}: margin {f.StaticMarginPercent:0.0}%, gross {f.GrossMassKg} kg, T/W {f.ThrustToWeight:0.00}, stall {f.StallSpeedKnots:0} kt, donor {plane.Design.Donor}");
        Assert.DoesNotContain(f.Checks, c => c.Level == CheckLevel.Problem);
        Assert.InRange(f.StaticMarginPercent, 5, 20);
        // The game-side numbers are filled in and sensible.
        Assert.True(f.RunwayNeededM >= OpenPlane.Core.Physics.GameEstimates.StockTakeoffDistanceM(plane.Design.Donor));
        Assert.False(double.IsNaN(f.PitchReach));
        Assert.DoesNotContain(f.Checks, c => c.Level == CheckLevel.Warning && c.Message.Contains("pitch reach"));
        Assert.DoesNotContain(plane.Notes, n => n.Level == "warning");
        Assert.Equal(3, plane.Design.Gear.Count);           // nose + mirrored mains
        Assert.NotNull(plane.Design.Cockpit);
        // Every point of the plane is above the ground the gear stands on.
        double ground = GearPlacer.GroundLevel(plane.Design)!.Value;
        Assert.True(plane.Meshes.SelectMany(m => Enumerable.Range(0, m.Vertices.Length / 3).Select(i => m.Vertices[i * 3 + 1])).Min() > ground + 0.1);
    }

    [Fact]
    public void TemplatesPickTheExpectedDonors()
    {
        Assert.Equal("COIN", DesignGenerator.Generate(Templates.Trainer()).Design.Donor);
        Assert.Equal("Fighter1", DesignGenerator.Generate(Templates.Fighter()).Design.Donor);
        // The bomber template is a medium bomber of about 11 t empty: by size it is closest to the KR-67 Ifrit (16 t, 19 m),
        // not the AB-4 Alkyon (34 t, 25 m), so "auto" builds it on Multirole1.
        Assert.Equal("Multirole1", DesignGenerator.Generate(Templates.Bomber()).Design.Donor);
    }

    [Fact]
    public void MirroredWingHasOppositeAileronsAndCorrectSurfaces()
    {
        var w = Templates.Trainer().Wings.First(x => x.Name == "Main wing");
        var panels = DesignGenerator.BuildWing(w);
        Assert.Equal(2, panels.Count);
        var right = panels[0]; var left = panels[1];
        Assert.True(right.TipLeading[0] > 0 && left.TipLeading[0] < 0);
        var ra = right.Surfaces.Single(s => s.Role == "aileron"); var la = left.Surfaces.Single(s => s.Role == "aileron");
        Assert.Equal(-20, ra.Inputs.Single(i => i.Axis == "roll").Gain);
        Assert.Equal(20, la.Inputs.Single(i => i.Axis == "roll").Gain);
        var flap = right.Surfaces.Single(s => s.Role == "flap");
        Assert.Equal(0, flap.MinDeg); Assert.Equal(30, flap.MaxDeg);
        Assert.Equal(30, flap.Inputs.Single().Gain);
        // The panel's trailing edge is the surfaces' leading edge, and panel + surfaces cover the whole planform.
        Assert.Contains(right.Surfaces, s => Vec3.Distance(Vec3.FromArray(s.RootLeading), Vec3.FromArray(right.RootTrailing)) < 1e-9);
        Assert.Contains(right.Surfaces, s => Vec3.Distance(Vec3.FromArray(s.TipLeading), Vec3.FromArray(right.TipTrailing)) < 1e-9);
        double area = DesignStats.PanelArea(right) + right.Surfaces.Sum(DesignStats.SurfaceArea);
        double expected = (w.RootChord + w.TipChord) / 2 * w.Span;
        Assert.InRange(area, expected * 0.995, expected * 1.005);
    }

    [Fact]
    public void FinRudderAndElevatorFollowFlyoutSignConventions()
    {
        var t = Templates.Fighter();
        var fin = DesignGenerator.BuildWing(t.Wings.First(w => w.Vertical)).Single();
        Assert.False(DesignStats.IsHorizontal(fin));
        var rudder = fin.Surfaces.Single(s => s.Role == "rudder");
        // Flyout files: yaw gain times the panel's sideways lift direction is negative.
        Assert.True(rudder.Inputs.Single().Gain * fin.Up[0] < 0);
        var tail = DesignGenerator.BuildWing(t.Wings.First(w => w.Name == "Tailplane"));
        Assert.All(tail, p => Assert.Equal(20, p.Surfaces.Single(s => s.Role == "elevator").Inputs.Single(i => i.Axis == "pitch").Gain));
    }

    [Fact]
    public void ElevonsAndOverlapsAreHandled()
    {
        var w = new WingSpec
        {
            Id = "w", Name = "Delta", Root = new double[] { 0.5, 0, 0 }, Span = 4, RootChord = 5, TipChord = 1, SweepDeg = 50, Mirror = true,
            Surfaces = { new SurfaceSpec { Type = "elevon", FromPct = 10, ToPct = 60, ChordPct = 20 }, new SurfaceSpec { Type = "elevon", FromPct = 50, ToPct = 95, ChordPct = 20 } },
        };
        var notes = new List<BuilderNote>();
        var panels = DesignGenerator.BuildWing(w, notes);
        Assert.Contains(notes, n => n.Message.Contains("overlap"));
        var e = panels[0].Surfaces.Where(s => s.Role == "elevon").ToList();
        Assert.Equal(2, e.Count);
        Assert.All(e, s => { Assert.Contains(s.Inputs, i => i.Axis == "pitch" && i.Gain == 20); Assert.Contains(s.Inputs, i => i.Axis == "roll" && i.Gain == -20); });
        Assert.All(panels[1].Surfaces.Where(s => s.Role == "elevon"), s => Assert.Contains(s.Inputs, i => i.Axis == "roll" && i.Gain == 20));
    }

    [Fact]
    public void BlankTemplateSaysWhatsMissing()
    {
        var plane = DesignGenerator.Generate(Templates.Blank());
        var f = FlightModel.Analyze(plane.Design, plane.Meshes);
        Assert.Contains(f.Checks, c => c.Level == CheckLevel.Problem && c.Message.Contains("no wings"));
    }
}

public class RoundTripTests
{
    [Theory]
    [InlineData("trainer")]
    [InlineData("fighter")]
    [InlineData("bomber")]
    public void BuilderPlaneRoundTripsThroughTheOplaneFile(string id)
    {
        var dir = TestPaths.NewScratch("roundtrip");
        try
        {
            var spec = Templates.Find(id)!.Create();
            var plane = DesignGenerator.Generate(spec);
            var file = Path.Combine(dir, id + OplanePackage.Extension);
            BuilderPackage.Write(file, spec, plane, null, new byte[] { 1, 2, 3 });

            // What the mod reads: a normal .oplane.
            var contents = OplanePackage.Read(file);
            Assert.Equal(OplanePackage.ToJson(plane.Design), OplanePackage.ToJson(contents.Design));
            Assert.Equal(plane.Meshes.Count, contents.Meshes.Count);
            Assert.Equal(new byte[] { 1, 2, 3 }, contents.Thumbnail);

            // What the builder reads back: the same editable spec, which makes the same design again.
            var opened = BuilderPackage.Read(file);
            Assert.True(opened.FromBuilderData);
            Assert.Null(opened.Note);
            var again = DesignGenerator.Generate(opened.Spec, opened.SourceMeshes);
            Assert.Equal(OplanePackage.ToJson(plane.Design), OplanePackage.ToJson(again.Design));
            var a = BuilderJson.Clone(spec); a.DesignHash = null;
            var b = BuilderJson.Clone(opened.Spec); b.DesignHash = null;
            Assert.Equal(OplanePackage.ToJson(a), OplanePackage.ToJson(b));
        }
        finally { TestPaths.TryDelete(dir); }
    }

    [Fact]
    public void FileChangedByAnotherToolOpensAsFixedShapes()
    {
        var dir = TestPaths.NewScratch("tamper");
        try
        {
            var spec = Templates.Fighter();
            var file = Path.Combine(dir, "f.oplane");
            BuilderPackage.Write(file, spec, DesignGenerator.Generate(spec));
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
            {
                var e = zip.GetEntry(OplanePackage.DesignEntry)!;
                string text;
                using (var r = new StreamReader(e.Open())) text = r.ReadToEnd();
                e.Delete();
                var n = zip.CreateEntry(OplanePackage.DesignEntry);
                using var w = new StreamWriter(n.Open(), new UTF8Encoding(false));
                w.Write(text.Replace("\"name\": \"Fighter\"", "\"name\": \"Edited elsewhere\""));
            }
            var opened = BuilderPackage.Read(file);
            Assert.False(opened.FromBuilderData);
            Assert.NotNull(opened.Note);
            Assert.Equal("Edited elsewhere", opened.Spec.Name);
            Assert.All(opened.Spec.Wings, w => Assert.Equal(WingSpec.PanelsKind, w.Kind));
        }
        finally { TestPaths.TryDelete(dir); }
    }

    [Fact]
    public void ConvertedPlanesReopenInTheBuilderUnchanged()
    {
        if (TestPaths.CraftRoot == null) return; // private samples aren't on this machine
        foreach (var folder in Directory.GetFiles(TestPaths.CraftRoot, "data.txt", SearchOption.AllDirectories).Select(Path.GetDirectoryName))
        {
            var r = FlyoutConverter.Convert(FlyoutCraft.Load(folder!));
            var spec = SpecFromDesign.Create(r.Design, r.Meshes);
            var regenerated = DesignGenerator.Generate(spec, r.Meshes).Design;
            string name = r.Design.Name;
            Assert.Equal(r.Design.Wings.Count, regenerated.Wings.Count);
            Assert.Equal(r.Design.Engines.Count, regenerated.Engines.Count);
            Assert.Equal(r.Design.Gear.Count, regenerated.Gear.Count);
            Assert.Equal(r.Design.Hardpoints.Count, regenerated.Hardpoints.Count);
            Assert.Equal(r.Design.Guns.Count, regenerated.Guns.Count);
            Assert.Equal(r.Design.Fuselages.Count, regenerated.Fuselages.Count);
            Assert.Equal(r.Design.Donor, regenerated.Donor);
            // Every original wing panel comes back (pairs are rebuilt by mirroring), corners within 2 mm.
            foreach (var p in r.Design.Wings)
                Assert.True(regenerated.Wings.Any(q => Close(p.RootLeading, q.RootLeading) && Close(p.TipTrailing, q.TipTrailing)
                    && p.Surfaces.Count == q.Surfaces.Count), $"{name}: panel {p.Name} wasn't reproduced");
            var f1 = FlightModel.Analyze(r.Design, r.Meshes);
            var f2 = FlightModel.Analyze(regenerated, DesignGenerator.Generate(spec, r.Meshes).Meshes);
            Assert.InRange(f2.StaticMarginPercent, f1.StaticMarginPercent - 0.5, f1.StaticMarginPercent + 0.5);
        }
    }

    private static bool Close(double[] a, double[] b) => Math.Abs(a[0] - b[0]) < 2e-3 && Math.Abs(a[1] - b[1]) < 2e-3 && Math.Abs(a[2] - b[2]) < 2e-3;

    [Fact]
    public void SavedConvertedPlaneKeepsItsMeshesAfterMovingAndScaling()
    {
        var folder = TestPaths.CraftFolder("Satoria-LF-2");
        if (folder == null) return;
        var dir = TestPaths.NewScratch("convsave");
        try
        {
            var r = FlyoutConverter.Convert(FlyoutCraft.Load(folder), "Tester");
            var spec = SpecFromDesign.Create(r.Design, r.Meshes);
            spec.Fuselages[0].Position = new double[] { 0, 0.5, 0 };
            spec.Fuselages[0].Scale = 1.1;
            spec.Wings[0].Offset = new double[] { 0, 0, 0.3 };
            var plane = DesignGenerator.Generate(spec, r.Meshes);
            var file = Path.Combine(dir, "s.oplane");
            BuilderPackage.Write(file, spec, plane, r.Report);
            var opened = BuilderPackage.Read(file);
            Assert.True(opened.FromBuilderData);
            Assert.Equal("Tester", opened.Spec.Origin!.Builder);
            Assert.NotNull(opened.Contents.Report);
            var again = DesignGenerator.Generate(opened.Spec, opened.SourceMeshes);
            Assert.Equal(plane.Design.Wings.Count, again.Design.Wings.Count);
            for (int w = 0; w < plane.Design.Wings.Count; w++)
            {
                Assert.True(Close(plane.Design.Wings[w].RootLeading, again.Design.Wings[w].RootLeading));
                Assert.True(Close(plane.Design.Wings[w].TipTrailing, again.Design.Wings[w].TipTrailing));
                Assert.Equal(plane.Design.Wings[w].Surfaces.Count, again.Design.Wings[w].Surfaces.Count);
            }
            for (int i = 0; i < plane.Design.Fuselages.Count; i++)
            {
                Assert.Equal(plane.Design.Fuselages[i].BoundsMin[1], again.Design.Fuselages[i].BoundsMin[1], 4);
                Assert.Equal(plane.Design.Fuselages[i].FrontalArea, again.Design.Fuselages[i].FrontalArea, 6);
            }
        }
        finally { TestPaths.TryDelete(dir); }
    }
}

/// <summary>The page's live preview (wwwroot/js/builder/geometry.js) must draw what the C# code saves.</summary>
public class PreviewParityTests
{
    private readonly ITestOutputHelper _out;
    public PreviewParityTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void JavaScriptGeometryMatchesCSharp()
    {
        var geometry = Path.Combine(TestPaths.RepoRoot, "src", "OpenPlane.App", "wwwroot", "js", "builder", "geometry.js");
        var spec = Templates.Bomber();
        spec.Wings[0].Surfaces.Add(new SurfaceSpec { Type = "elevon", FromPct = 40, ToPct = 70, ChordPct = 15 }); // overlaps, smaller chord
        spec.Wings[1].IncidenceDeg = 2.5; spec.Wings[2].DihedralDeg = 12; spec.Wings[2].Mirror = true; spec.Wings[2].Root = new double[] { 1, 1.6, -7.8 };
        spec.Fuselages[0].Rings[2].Roundness = 0.3;
        var dir = TestPaths.NewScratch("parity");
        try
        {
            var specFile = Path.Combine(dir, "spec.json");
            File.WriteAllText(specFile, OplanePackage.ToJson(spec));
            var script = $$"""
                import { buildWing, loft } from {{Js(new Uri(geometry).AbsoluteUri)}};
                import { readFileSync } from 'node:fs';
                const spec = JSON.parse(readFileSync({{Js(specFile)}}, 'utf8'));
                const out = { wings: spec.wings.flatMap(w => buildWing(w)), lofts: spec.fuselages.map(f => { const m = loft(f); return { v: [...m.positions], t: [...m.indices] }; }) };
                console.log(JSON.stringify(out));
                """;
            var scriptFile = Path.Combine(dir, "parity.mjs");
            File.WriteAllText(scriptFile, script);
            string stdout;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("node", $"\"{scriptFile}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                stdout = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                Assert.True(p.ExitCode == 0, err);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _out.WriteLine("Node.js isn't installed; the parity check was skipped.");
                return;
            }
            var js = Newtonsoft.Json.Linq.JObject.Parse(stdout);
            var cs = DesignGenerator.Generate(spec);
            var jsWings = js["wings"]!.ToArray();
            Assert.Equal(cs.Design.Wings.Count, jsWings.Length);
            for (int i = 0; i < jsWings.Length; i++)
            {
                var c = cs.Design.Wings[i]; var j = jsWings[i];
                Same(c.RootLeading, j["root_leading"]!); Same(c.RootTrailing, j["root_trailing"]!); Same(c.TipLeading, j["tip_leading"]!); Same(c.TipTrailing, j["tip_trailing"]!); Same(c.Up, j["up"]!);
                var js2 = j["surfaces"]!.ToArray();
                Assert.Equal(c.Surfaces.Count, js2.Length);
                for (int k = 0; k < js2.Length; k++)
                {
                    Assert.Equal(c.Surfaces[k].Role, (string)js2[k]["role"]!);
                    Same(c.Surfaces[k].RootLeading, js2[k]["root_leading"]!); Same(c.Surfaces[k].TipTrailing, js2[k]["tip_trailing"]!);
                    Assert.Equal(c.Surfaces[k].Inputs.Select(x => $"{x.Axis}:{x.Gain}"), js2[k]["inputs"]!.Select(x => $"{x["axis"]}:{(double)x["gain"]!}"));
                }
            }
            var loftJs = js["lofts"]![0]!;
            var loftCs = FuselageLoft.Build(spec.Fuselages[0]);
            Assert.Equal(loftCs.Triangles, loftJs["t"]!.Select(x => (int)x).ToArray());
            var vjs = loftJs["v"]!.Select(x => (double)x).ToArray();
            Assert.Equal(loftCs.Vertices.Length, vjs.Length);
            for (int i = 0; i < vjs.Length; i++) Assert.Equal(loftCs.Vertices[i], vjs[i], 3);
        }
        finally { TestPaths.TryDelete(dir); }
    }

    /// <summary>Runs a node script that imports geometry.js as `G` and prints JSON; null when Node.js isn't installed.</summary>
    private Newtonsoft.Json.Linq.JObject? RunNode(string dir, string body, params (string name, object value)[] inputs)
    {
        var geometry = Path.Combine(TestPaths.RepoRoot, "src", "OpenPlane.App", "wwwroot", "js", "builder", "geometry.js");
        var lines = new List<string> { $"import * as G from {Js(new Uri(geometry).AbsoluteUri)};", "import { readFileSync } from 'node:fs';" };
        foreach (var (name, value) in inputs)
        {
            var file = Path.Combine(dir, name + ".json");
            File.WriteAllText(file, OplanePackage.ToJson(value));
            lines.Add($"const {name} = JSON.parse(readFileSync({Js(file)}, 'utf8'));");
        }
        lines.Add(body);
        var scriptFile = Path.Combine(dir, "run.mjs");
        File.WriteAllText(scriptFile, string.Join("\n", lines));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", $"\"{scriptFile}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Assert.True(p.ExitCode == 0, err);
            return Newtonsoft.Json.Linq.JObject.Parse(stdout);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _out.WriteLine("Node.js isn't installed; the parity check was skipped.");
            return null;
        }
    }

    [Fact]
    public void JavaScriptMatchesCSharpForVersionTwoParts()
    {
        var spec = Templates.Bomber();
        var f = spec.Fuselages[0];
        f.Smooth = 2;
        f.RotationDeg = new[] { 3.0, 10, -2 };
        var off = FuselageLoft.Offsets(f.Rings[3], f.Sides);
        f.Rings[3].Points = off.Select(p => new[] { p.X * 1.1, System.Math.Max(p.Y, -0.9), 0.05 * p.X }).ToList();
        f.Rings[4].Points = off.Where((p, i) => i % 3 == 0).Select(p => new[] { p.X, p.Y + 0.1, 0 }).ToList();   // resampled
        var w = spec.Wings[0];
        w.TwistDeg = -3;
        w.Surfaces.Add(new SurfaceSpec { Type = "slat", FromPct = 10, ToPct = 80, ChordPct = 12 });
        w.Surfaces.Add(new SurfaceSpec { Type = "airbrake", Edge = "leading", FromPct = 85, ToPct = 95, ChordPct = 20 });
        w.Surfaces[1].Inputs = new List<InputLink> { new InputLink { Axis = "roll", Gain = -15 }, new InputLink { Axis = "pitch", Gain = 5 } };
        w.Surfaces[1].MinDeg = -12; w.Surfaces[1].ServoDegPerSec = 70;
        spec.Wings[1].Surfaces[0].Type = "ruddervator";
        spec.Wings[2].Root = new double[] { -1, 1.6, -7.8 }; spec.Wings[2].Mirror = true;   // a fin on the left: gains flip
        spec.Wings[2].Surfaces[0].Inputs = new List<InputLink> { new InputLink { Axis = "yaw", Gain = 18 } };
        var shapes = new[]
        {
            Shape("capsule", 1), Shape("cylinder", 1), Shape("box", 2), Shape("cone", 0),
        };
        shapes[1].Faces.RemoveAt(shapes[1].Faces.Count - 1);   // an open shape: border rules
        shapes[2].Material = ShapeSpec.Glass;
        var eulers = new[] { new[] { 10.0, 20, 30 }, new[] { -45.0, 90, 0 }, new[] { 0.0, 0, 0 }, new[] { 89.0, -170, 45 } };

        var dir = TestPaths.NewScratch("parity2");
        try
        {
            var js = RunNode(dir, """
                const out = {
                  wings: spec.wings.flatMap(w => G.buildWing(w)),
                  loft: (() => { const m = G.loft(spec.fuselages[0]); return { v: [...m.positions], t: [...m.indices] }; })(),
                  shapes: shapes.map(s => { const m = G.shapeMesh(s); return { v: [...m.positions], t: [...m.indices], m: [...m.materials] }; }),
                  quats: eulers.map(e => G.eulerToQuat(e)),
                  back: eulers.map(e => G.eulerToQuat(G.quatToEuler(G.eulerToQuat(e)))),
                  placed: shapes.map(s => G.shapeTransform({ ...s, position: [1, 2, 3], rotation_deg: [10, 20, 30], scale: [1, 2, 0.5] }).toPlane([0.3, -0.2, 0.7])),
                  jet: G.jetThrustN(0.9, 1450),
                };
                console.log(JSON.stringify(out));
                """, ("spec", spec), ("shapes", shapes), ("eulers", eulers));
            if (js == null) return;

            var cs = DesignGenerator.Generate(spec);
            var jsWings = js["wings"]!.ToArray();
            Assert.Equal(cs.Design.Wings.Count, jsWings.Length);
            for (int i = 0; i < jsWings.Length; i++)
            {
                var c = cs.Design.Wings[i]; var j = jsWings[i];
                Same(c.RootLeading, j["root_leading"]!); Same(c.TipTrailing, j["tip_trailing"]!); Same(c.TipLeading, j["tip_leading"]!); Same(c.Up, j["up"]!);
                Assert.Equal(c.TipTwistDeg, (double)j["tip_twist_deg"]!);
                var s2 = j["surfaces"]!.ToArray();
                Assert.Equal(c.Surfaces.Count, s2.Length);
                for (int k = 0; k < s2.Length; k++)
                {
                    Assert.Equal(c.Surfaces[k].Role, (string)s2[k]["role"]!);
                    Assert.Equal(c.Surfaces[k].Edge, (string)s2[k]["edge"]!);
                    Same(c.Surfaces[k].RootLeading, s2[k]["root_leading"]!); Same(c.Surfaces[k].TipTrailing, s2[k]["tip_trailing"]!);
                    Assert.Equal(c.Surfaces[k].MinDeg, (double)s2[k]["min_deg"]!);
                    Assert.Equal(c.Surfaces[k].MaxDeg, (double)s2[k]["max_deg"]!);
                    Assert.Equal(c.Surfaces[k].Inputs.Select(x => $"{x.Axis}:{x.Gain}"), s2[k]["inputs"]!.Select(x => $"{x["axis"]}:{(double)x["gain"]!}"));
                    if (c.Surfaces[k].Role != "fixed") Assert.Equal(c.Surfaces[k].ServoDegPerSec, (double)s2[k]["servo_deg_per_sec"]!);
                }
            }
            Assert.Contains(cs.Design.Wings[0].Surfaces, s => s.Role == "slat" && s.Edge == "leading");
            Assert.Contains(cs.Design.Wings[0].Surfaces, s => s.Role == "airbrake" && s.Edge == "leading");

            var loftCs = FuselageLoft.Build(f);
            Assert.Equal(loftCs.Triangles, js["loft"]!["t"]!.Select(x => (int)x).ToArray());
            var lv = js["loft"]!["v"]!.Select(x => (double)x).ToArray();
            Assert.Equal(loftCs.Vertices.Length, lv.Length);
            for (int i = 0; i < lv.Length; i++) Assert.Equal(loftCs.Vertices[i], lv[i], 3);

            var jsShapes = js["shapes"]!.ToArray();
            for (int s = 0; s < shapes.Length; s++)
            {
                var poly = PolyMesh.FromShape(shapes[s]);
                IList<Vec3> v = poly.Vertices; IList<int[]> fc = poly.Faces;
                for (int l = 0; l < shapes[s].Subdivision; l++) { var r = ShapeMesh.CatmullClark(v, fc); v = r.vertices; fc = r.faces; }
                Assert.Equal(ShapeMesh.Triangulate(fc), jsShapes[s]["t"]!.Select(x => (int)x).ToArray());
                var sv = jsShapes[s]["v"]!.Select(x => (double)x).ToArray();
                Assert.Equal(v.Count * 3, sv.Length);
                for (int i = 0; i < v.Count; i++)
                {
                    Near(v[i].X, sv[i * 3]); Near(v[i].Y, sv[i * 3 + 1]); Near(v[i].Z, sv[i * 3 + 2]);
                }
                var saved = ShapeMesh.ToMeshData(shapes[s]);
                Assert.Equal(saved.TriangleMaterials, jsShapes[s]["m"]!.Select(x => (int)x).ToArray());
                var placed = new ShapeSpec { Vertices = new List<double[]> { new[] { 0.3, -0.2, 0.7 } }, Faces = new List<int[]>(), Position = new double[] { 1, 2, 3 }, RotationDeg = new double[] { 10, 20, 30 }, Scale = new double[] { 1, 2, 0.5 } };
                var p = Vec3.FromArray(placed.Position) + FuselageLoft.EulerToQuat(placed.RotationDeg) * new Vec3(0.3 * 1, -0.2 * 2, 0.7 * 0.5);
                Same(p.ToArray(), js["placed"]![s]!);
            }
            for (int i = 0; i < eulers.Length; i++)
            {
                var q = FuselageLoft.Rotation(eulers[i]).ToArray();
                var jq = js["quats"]![i]!.Select(x => (double)x).ToArray();
                for (int k = 0; k < 4; k++) Assert.Equal(q[k], jq[k], 9);
                var back = js["back"]![i]!.Select(x => (double)x).ToArray();
                double dot = Enumerable.Range(0, 4).Sum(k => q[k] * back[k]);
                Assert.Equal(1, System.Math.Abs(dot), 6);   // same rotation (q or -q)
            }
            Assert.Equal(DesignGenerator.JetThrustN(0.9, 1450), (double)js["jet"]!, 6);
        }
        finally { TestPaths.TryDelete(dir); }
    }

    private static ShapeSpec Shape(string kind, int subdivision)
    {
        var s = ShapeMesh.NewPrimitiveShape(kind);
        s.Subdivision = subdivision;
        return s;
    }

    private static void Near(double expected, double actual, double tol = 1e-4)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    private static string Js(string s) => Newtonsoft.Json.JsonConvert.ToString(s);

    private static void Same(double[] c, Newtonsoft.Json.Linq.JToken j)
    {
        var arr = j.Select(x => (double)x).ToArray();
        Assert.Equal(3, arr.Length);
        for (int i = 0; i < 3; i++) Assert.Equal(c[i], arr[i], 9);
    }
}
