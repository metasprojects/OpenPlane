using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Math;
using OpenPlane.Core.Preview;
using Xunit;
using Xunit.Abstractions;

namespace OpenPlane.Core.Tests
{
    /// <summary>
    /// Checks the converter against real Flyout craft. The craft are private test files (samples-private/, never
    /// committed); without them these tests pass without checking anything and say so in the output.
    /// </summary>
    public class SampleCraftTests
    {
        private readonly ITestOutputHelper _out;
        public SampleCraftTests(ITestOutputHelper output) { _out = output; }

        private static readonly Lazy<string?> CraftRoot = new Lazy<string?>(() =>
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "samples-private", "flyout", "craft");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        });

        private static readonly Dictionary<string, FlyoutCraft> Cache = new Dictionary<string, FlyoutCraft>();

        private FlyoutCraft? Craft(string name)
        {
            if (CraftRoot.Value == null) { _out.WriteLine("samples-private not found; skipped."); return null; }
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var c)) return c;
                var folder = Directory.GetDirectories(CraftRoot.Value, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(d => Path.GetFileName(d) == name && File.Exists(Path.Combine(d, "data.txt")));
                Assert.True(folder != null, $"sample craft {name} is missing");
                c = FlyoutCraft.Load(folder!);
                Cache[name] = c;
                return c;
            }
        }

        private IEnumerable<string> AllCraftFolders()
            => CraftRoot.Value == null ? Enumerable.Empty<string>()
               : Directory.GetFiles(CraftRoot.Value, "data.txt", SearchOption.AllDirectories).Select(Path.GetDirectoryName)!;

        [Fact]
        public void EveryCraftConvertsAndRoundTrips()
        {
            int count = 0;
            foreach (var folder in AllCraftFolders())
            {
                var craft = FlyoutCraft.Load(folder);
                Assert.NotEmpty(craft.Parts);
                var result = FlyoutConverter.Convert(craft);
                Assert.NotEmpty(result.Design.Wings);
                Assert.NotEmpty(result.Design.Fuselages);
                Assert.DoesNotContain(result.Report.Items, i => i.Message.Contains("expected") && i.Message.Contains("points"));
                foreach (var m in result.Meshes)
                {
                    Assert.Equal(0, m.Triangles.Length % 3);
                    Assert.Equal(m.Triangles.Length / 3, m.TriangleMaterials.Length);
                    Assert.True(m.Triangles.All(t => t >= 0 && t < m.Vertices.Length / 3));
                }

                var path = Path.Combine(Path.GetTempPath(), "openplane-test-" + Guid.NewGuid().ToString("N") + ".oplane");
                try
                {
                    var png = ThreeView.RenderPng(result.Design, result.Meshes, 120);
                    Assert.Equal(new byte[] { 137, 80, 78, 71 }, png.Take(4).ToArray());
                    OplanePackage.Write(path, result.Design, result.Meshes, result.Report, png);
                    var back = OplanePackage.Read(path);
                    Assert.Equal(result.Design.Name, back.Design.Name);
                    Assert.Equal(result.Design.Wings.Count, back.Design.Wings.Count);
                    Assert.Equal(result.Meshes.Sum(m => m.Triangles.Length), back.Meshes.Sum(m => m.Triangles.Length));
                    Assert.NotNull(back.Thumbnail);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
                count++;
            }
            _out.WriteLine($"{count} craft converted.");
        }

        [Theory]
        [InlineData("Me-262A-1")]
        [InlineData("KR-67 Ifrit")]
        [InlineData("Satoria-LF-2")]
        [InlineData("Ta-183")]
        public void MirroredTwinsLandOppositeEachOther(string name)
        {
            var craft = Craft(name); if (craft == null) return;
            var byId = craft.Parts.ToDictionary(p => p.Id);
            int pairs = 0;
            foreach (var p in craft.Parts.Where(p => p.MirroredId.HasValue && byId.ContainsKey(p.MirroredId.Value)))
            {
                var twin = byId[p.MirroredId!.Value];
                Assert.True(Vec3.Distance(p.WorldPose.Position, twin.WorldPose.Position.MirrorX) < 0.002,
                    $"{name}: part #{p.Id} and its twin #{twin.Id} are not mirror images");
                pairs++;
            }
            Assert.True(pairs > 10);
        }

        [Fact]
        public void ElevatorHangsOffTheTailplaneTrailingEdge()
        {
            var craft = Craft("Satoria-LF-2"); if (craft == null) return;
            var design = FlyoutConverter.Convert(craft).Design;
            var tailplanes = design.Wings.Where(w => w.Surfaces.Any(s => s.Role == "elevator")).ToList();
            Assert.Equal(2, tailplanes.Count);
            foreach (var w in tailplanes)
            {
                var elevator = w.Surfaces.Single(s => s.Role == "elevator");
                Assert.True(Vec3.Distance(Vec3.FromArray(elevator.RootLeading), Vec3.FromArray(w.RootTrailing)) < 0.001);
                Assert.True(Vec3.Distance(Vec3.FromArray(elevator.TipLeading), Vec3.FromArray(w.TipTrailing)) < 0.001);
            }
            // Left and right tailplanes are mirror images.
            var a = tailplanes[0]; var b = tailplanes[1];
            Assert.True(Vec3.Distance(Vec3.FromArray(a.TipTrailing), Vec3.FromArray(b.TipTrailing).MirrorX) < 0.001);
        }

        [Fact]
        public void LightPlaneHasEveryControl()
        {
            var craft = Craft("Satoria-LF-2"); if (craft == null) return;
            var design = FlyoutConverter.Convert(craft).Design;
            var roles = design.Wings.SelectMany(w => w.Surfaces).Select(s => s.Role).ToList();
            Assert.Contains("elevator", roles);
            Assert.Contains("rudder", roles);
            Assert.Contains("aileron", roles);
            Assert.Contains("flap", roles);
            var ailerons = design.Wings.SelectMany(w => w.Surfaces).Where(s => s.Role == "aileron").ToList();
            Assert.Equal(2, ailerons.Count);
            // Opposite sides move opposite ways for roll.
            Assert.True(ailerons[0].Inputs[0].Gain * ailerons[1].Inputs[0].Gain < 0);
            Assert.Single(design.Engines);
            Assert.Equal("piston-prop", design.Engines[0].Type);
            Assert.Equal("COIN", design.Donor);
        }

        [Fact]
        public void ReplicaMatchesTheRealAircraftSize()
        {
            // Messerschmitt Me 262 A-1a: span 12.48 m, length 10.60 m.
            var craft = Craft("Me-262A-1"); if (craft == null) return;
            var s = FlyoutConverter.Convert(craft).Design.Stats!;
            Assert.InRange(s.SpanM, 12.48 - 0.3, 12.48 + 0.3);
            Assert.InRange(s.LengthM, 10.60 - 0.3, 10.60 + 0.3);
        }

        [Fact]
        public void ResizedPartsAreScaled()
        {
            // The He-162 replica has two bodies stored 23.5 m long and shrunk to 4 % with *Resizable.
            var craft = Craft("He-162A-2"); if (craft == null) return;
            var s = FlyoutConverter.Convert(craft).Design.Stats!;
            Assert.InRange(s.LengthM, 8.5, 11.0);
        }

        [Fact]
        public void TwinJetFighterKeepsItsSystems()
        {
            var craft = Craft("KR-67 Ifrit"); if (craft == null) return;
            var d = FlyoutConverter.Convert(craft).Design;
            Assert.Equal(2, d.Engines.Count);
            Assert.All(d.Engines, e => Assert.True(e.AfterburnerThrustN > e.MaxThrustN));
            Assert.Equal(3, d.Gear.Count);
            Assert.Equal(8, d.Hardpoints.Count);
            // Its tail surfaces lean ~40 degrees from horizontal (a V-tail), so four panels have a strong sideways lift component.
            Assert.Equal(4, d.Wings.Count(w => System.Math.Abs(w.Up[0]) > 0.5 && System.Math.Abs(w.Up[1]) > 0.5));
            Assert.Equal("Multirole1", d.Donor);
        }

        [Fact]
        public void HelicoptersAreFlagged()
        {
            var craft = Craft("SAH-46 Chicane"); if (craft == null) return;
            Assert.True(FlyoutConverter.IsHelicopter(craft));
            var r = FlyoutConverter.Convert(craft).Report;
            Assert.Contains(r.Items, i => i.Level == ReportLevel.Problem && i.Message.Contains("helicopter"));
        }

        /// <summary>Every fixed-wing sample can be flown: something controls pitch and roll, including all-moving tails.</summary>
        [Fact]
        public void EveryPlaneHasPitchAndRollControl()
        {
            foreach (var folder in AllCraftFolders())
            {
                var craft = FlyoutCraft.Load(folder);
                if (FlyoutConverter.IsHelicopter(craft)) continue;
                var r = FlyoutConverter.Convert(craft);
                var f = OpenPlane.Core.Physics.FlightModel.Analyze(r.Design, r.Meshes);
                _out.WriteLine($"{craft.Name}: margin {f.StaticMarginPercent:0}% (game {f.GameMarginPercent:0}%), empty {f.EmptyMassKg} kg");
                Assert.DoesNotContain(f.Checks, c => c.Message.StartsWith("Nothing controls pitch") || c.Message.StartsWith("Nothing controls roll"));
                Assert.InRange(f.StaticMarginPercent, -40, 80);   // an estimate, but never absurd
            }
        }

        [Fact]
        public void AllMovingTailsBecomeControls()
        {
            var craft = Craft("Sukhoi Su-57 'Felon'"); if (craft == null) return;
            var d = FlyoutConverter.Convert(craft).Design;
            var moving = d.Wings.Where(w => w.Inputs.Count > 0).ToList();
            Assert.True(moving.Count >= 2, "the tailplanes turn as a whole");
            Assert.Contains(moving, w => w.Inputs.Any(i => i.Axis == "pitch"));
            // They survive opening the plane in the builder and saving it again.
            var spec = OpenPlane.Core.Builder.SpecFromDesign.Create(d, FlyoutConverter.Convert(craft).Meshes);
            var again = OpenPlane.Core.Builder.DesignGenerator.Generate(spec, FlyoutConverter.Convert(craft).Meshes).Design;
            Assert.Equal(moving.Count, again.Wings.Count(w => w.Inputs.Count > 0));
            // And through the file format.
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(d, OplanePackage.Json);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<AircraftDesign>(json, OplanePackage.Json)!;
            Assert.Equal(moving.Count, back.Wings.Count(w => w.Inputs.Count > 0));
        }

        [Fact]
        public void TwinBodyPlanesAreCentred()
        {
            var craft = Craft("AF-02-Grève-A1"); if (craft == null) return;
            var r = FlyoutConverter.Convert(craft);
            double area = 0, moment = 0;
            foreach (var w in r.Design.Wings.Where(w => System.Math.Abs(w.Up[1]) > 0.7))
            {
                double a = DesignStats.QuadArea(w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading);
                area += a; moment += a * (w.RootLeading[0] + w.TipLeading[0] + w.RootTrailing[0] + w.TipTrailing[0]) / 4;
            }
            Assert.InRange(moment / area, -0.3, 0.3);
            Assert.Contains(r.Report.Items, i => i.Message.Contains("centre line"));
            // The cockpit moves as one: the eye stays in its body and the instruments in front of it (in game run 13 they
            // were left between the bodies, and the cockpit view looked out from mid-air).
            var eye = r.Design.Cockpit!.EyeOrDefault();
            Assert.InRange(eye[0] - r.Design.Cockpit.Position[0], -1.5, 1.5);
            Assert.Contains(r.Design.Fuselages, f => eye[0] >= f.BoundsMin[0] && eye[0] <= f.BoundsMax[0] && eye[2] >= f.BoundsMin[2] && eye[2] <= f.BoundsMax[2]);
            Assert.NotEmpty(r.Design.Instruments);
            // This one has a cockpit in each body: every instrument sits by one of them.
            Assert.All(r.Design.Instruments, i => Assert.InRange(System.Math.Abs(System.Math.Abs(i.Position[0]) - System.Math.Abs(eye[0])), 0, 2.0));
        }

        /// <summary>The weight estimate lands near real planes the samples replicate.</summary>
        [Theory]
        [InlineData("Me-262A-1", 3795)]   // Messerschmitt Me 262 A-1a, empty
        [InlineData("He-162A-2", 1660)]   // Heinkel He 162 A-2, empty
        [InlineData("AB-4 Alkyon", 34100)] // the stock Alkyon in Nuclear Option
        public void WeightsAreRealistic(string name, double realEmptyKg)
        {
            var craft = Craft(name); if (craft == null) return;
            var r = FlyoutConverter.Convert(craft);
            var f = OpenPlane.Core.Physics.FlightModel.Analyze(r.Design, r.Meshes);
            _out.WriteLine($"{name}: {f.EmptyMassKg} kg empty (real {realEmptyKg})");
            Assert.InRange(f.EmptyMassKg, realEmptyKg * 0.75, realEmptyKg * 1.3);
        }
    }
}
