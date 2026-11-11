using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace OpenPlane.Core.Tests
{
    /// <summary>Materials, interior pieces, instruments, lights and the cockpit eye: format, builder and converter.</summary>
    public class MaterialsTests
    {
        private readonly ITestOutputHelper _out;
        public MaterialsTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void OlderFilesReadAsPaintAndGlass()
        {
            var d = new AircraftDesign();
            var m = new MeshData { Triangles = new int[6], TriangleMaterials = new[] { 0, 1 } };
            Assert.Equal(MaterialDef.Paint, DesignMaterials.ForSlot(d, m, 0).Kind);
            Assert.Equal(MaterialDef.Glass, DesignMaterials.ForSlot(d, m, 1).Kind);
            // With a table, slots go through it; an index that isn't in the palette is the paint.
            d.Materials.Add(new MaterialDef { Name = "Red", Color = new double[] { 1, 0, 0, 1 } });
            m.Materials = new[] { 0, 7 };
            Assert.Equal("Red", DesignMaterials.ForSlot(d, m, 0).Name);
            Assert.Equal("Paint", DesignMaterials.ForSlot(d, m, 1).Name);
        }

        [Fact]
        public void PaletteDeduplicates()
        {
            var palette = new List<MaterialDef>();
            int a = DesignMaterials.IndexOf(palette, new MaterialDef { Color = new double[] { 0.5, 0.5, 0.5, 1 } });
            int b = DesignMaterials.IndexOf(palette, new MaterialDef { Color = new double[] { 0.501, 0.5, 0.5, 1 } });
            int c = DesignMaterials.IndexOf(palette, new MaterialDef { Kind = MaterialDef.Glass, Color = new double[] { 0.5, 0.5, 0.5, 1 } });
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(2, palette.Count);
        }

        /// <summary>A template without its starter cockpit kit (palette, canopy, seat, panel, instruments, lights, eye).</summary>
        private static PlaneSpec Plain(PlaneSpec s)
        {
            s.Materials.Clear(); s.Instruments.Clear(); s.Lights.Clear(); s.Shapes.Clear();
            if (s.Cockpit != null) s.Cockpit.Eye = null;
            return s;
        }

        [Fact]
        public void TemplatesComeWithACockpitKit()
        {
            foreach (var spec in new[] { Templates.Trainer(), Templates.Fighter(), Templates.Bomber() })
            {
                var plane = DesignGenerator.Generate(spec);
                var d = plane.Design;
                Assert.Contains(d.Materials, m => m.Kind == MaterialDef.Glass);
                Assert.True(d.Instruments.Count >= 4, $"{spec.Name}: {d.Instruments.Count} instruments");
                Assert.Contains(d.Lights, l => l.Kind == "nav" && l.Position[0] < 0);
                Assert.Contains(d.Lights, l => l.Kind == "nav" && l.Position[0] > 0);
                Assert.Contains(plane.Meshes, m => m.Role == MeshData.Interior);
                Assert.NotNull(d.Cockpit?.Eye);
                // The kit sits inside the fuselage: the eye is above the cockpit position and the seat below it.
                Assert.True(d.Cockpit!.Eye![1] > d.Cockpit.Position[1]);
            }
        }

        [Fact]
        public void BuilderGivesPartsTheirMaterialsInteriorsInstrumentsAndLights()
        {
            var spec = Plain(Templates.Trainer());
            spec.Materials.Add(new MaterialDef { Id = "red", Name = "Red", Color = new double[] { 0.8, 0.1, 0.1, 1 } });
            spec.Materials.Add(new MaterialDef { Id = "glass", Name = "Glass", Kind = MaterialDef.Glass, Color = new double[] { 0.5, 0.6, 0.7, 0.3 } });
            spec.Fuselages[0].MaterialId = "red";
            spec.Wings[0].MaterialId = "red";
            var canopy = ShapeMesh.NewPrimitiveShape("sphere", new double[] { 0.6, 0.5, 1.2 });
            canopy.Id = "canopy"; canopy.Name = "Canopy"; canopy.Position = new double[] { 0, 0.8, 1.5 }; canopy.MaterialId = "glass";
            var seat = ShapeMesh.NewPrimitiveShape("box", new double[] { 0.4, 0.4, 0.4 });
            seat.Id = "seat"; seat.Name = "Seat"; seat.Position = new double[] { 0, 0.3, 1.2 }; seat.Interior = true;
            spec.Shapes.Add(canopy); spec.Shapes.Add(seat);
            spec.Instruments.Add(new InstrumentDef { Id = "g1", Name = "Speed", Kind = "gauge", Reads = "airspeed", Position = new double[] { 0.1, 0.6, 1.8 }, Rotation = new double[] { -0.7071, 0, 0, 0.7071 }, Mirror = true });
            spec.Lights.Add(new LightDef { Id = "nav", Name = "Nav", Kind = "nav", Position = new double[] { 5, 0, 0 }, Color = new double[] { 0, 1, 0, 1 }, Mirror = true });
            spec.Cockpit ??= new CockpitDef();
            spec.Cockpit.Eye = new double[] { 0, 1.1, 1.4 };

            var plane = DesignGenerator.Generate(spec);
            var d = plane.Design;
            Assert.Equal(2, d.Materials.Count);
            int red = d.Materials.FindIndex(m => m.Id == "red"), glass = d.Materials.FindIndex(m => m.Id == "glass");
            Assert.Contains(plane.Meshes, m => m.Materials != null && m.Materials.Contains(red) && m.Role == null);
            Assert.Contains(plane.Meshes, m => m.Materials != null && m.Materials.Contains(glass));
            Assert.Contains(plane.Meshes, m => m.Role == MeshData.Interior);
            Assert.All(d.Wings.Where(w => w.Name == spec.Wings[0].Name), w => Assert.Equal(red, w.Material));
            // Mirrored: two instruments and two lights, the left light red.
            Assert.Equal(2, d.Instruments.Count);
            Assert.Equal(-0.1, d.Instruments[1].Position[0], 6);
            Assert.Equal(2, d.Lights.Count);
            var left = d.Lights.Single(l => l.Position[0] < 0);
            Assert.True(left.Color[0] > 0.9 && left.Color[1] < 0.1, "the left navigation light is red");
            Assert.Equal(1.1, d.Cockpit!.Eye![1], 6);

            // The interior seat adds no drag or weight.
            var withSeat = FlightModel.Analyze(d, plane.Meshes);
            spec.Shapes.Remove(seat);
            var without = DesignGenerator.Generate(spec);
            var noSeat = FlightModel.Analyze(without.Design, without.Meshes);
            Assert.Equal(noSeat.EmptyMassKg, withSeat.EmptyMassKg, 0);

            // Through the file format and back into the builder.
            var json = JsonConvert.SerializeObject(d, OplanePackage.Json);
            var back = JsonConvert.DeserializeObject<AircraftDesign>(json, OplanePackage.Json)!;
            DesignValidator.Check(back, plane.Meshes);
            Assert.Equal(2, back.Materials.Count);
            Assert.Equal(2, back.Instruments.Count);
            var again = SpecFromDesign.Create(back, plane.Meshes);
            Assert.Equal(2, again.Materials.Count);
            Assert.All(again.Materials, m => Assert.False(string.IsNullOrEmpty(m.Id)));
        }

        [Fact]
        public void PlanesWithoutTheNewPartsWriteTheSameFile()
        {
            var plane = DesignGenerator.Generate(Plain(Templates.Fighter()));
            var json = JsonConvert.SerializeObject(plane.Design, OplanePackage.Json);
            Assert.DoesNotContain("\"materials\"", json);
            Assert.DoesNotContain("\"instruments\"", json);
            Assert.DoesNotContain("\"lights\"", json);
            Assert.DoesNotContain("\"stock_livery\"", json);
        }

        [Fact]
        public void ValidatorRepairsMaterialsAndRefusesBrokenOnes()
        {
            var d = new AircraftDesign();
            d.Materials.Add(new MaterialDef { Kind = "chrome", Color = new double[] { 2, -1, 0.5 }, Metallic = 3 });
            d.Instruments.Add(new InstrumentDef { Kind = "radio", Reads = "mood", Size = 9 });
            d.Lights.Add(new LightDef { Kind = "disco" });
            DesignValidator.Check(d, new List<MeshData>());
            Assert.Equal(MaterialDef.Paint, d.Materials[0].Kind);
            Assert.Equal(new double[] { 1, 0, 0.5 }, d.Materials[0].Color);
            Assert.Equal(1, d.Materials[0].Metallic);
            Assert.Equal("gauge", d.Instruments[0].Kind);
            Assert.Null(d.Instruments[0].Reads);
            Assert.Equal(2, d.Instruments[0].Size);
            Assert.Equal("nav", d.Lights[0].Kind);
            d.Materials[0].Color = new double[] { 1 };
            Assert.Throws<InvalidDataException>(() => DesignValidator.Check(d, new List<MeshData>()));
        }

        /// <summary>
        /// The drawing convention (the face looks along the rotation's +y, 12 o'clock is its +z) holds for Flyout's
        /// instruments: across the samples they face the pilot's eye and read the right way up.
        /// </summary>
        [Fact]
        public void ConvertedInstrumentsFaceThePilotTheRightWayUp()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples-private", "flyout", "craft"))) dir = dir.Parent;
            if (dir == null) { _out.WriteLine("samples-private not found; skipped."); return; }
            int facing = 0, total = 0; double up = 0;
            foreach (var f in Directory.GetFiles(Path.Combine(dir.FullName, "samples-private", "flyout", "craft"), "data.txt", SearchOption.AllDirectories))
            {
                var d = FlyoutConverter.Convert(FlyoutCraft.Load(Path.GetDirectoryName(f)!)).Design;
                if (d.Cockpit?.Eye == null) continue;
                var eye = OpenPlane.Core.Math.Vec3.FromArray(d.Cockpit.Eye);
                foreach (var i in d.Instruments.Where(i => i.Kind == "gauge" || i.Kind == "attitude" || i.Kind == "compass"))
                {
                    var q = OpenPlane.Core.Math.Quat.FromArray(i.Rotation);
                    var face = q * new OpenPlane.Core.Math.Vec3(0, 1, 0);
                    var toEye = (eye - OpenPlane.Core.Math.Vec3.FromArray(i.Position)).Normalized;
                    if (OpenPlane.Core.Math.Vec3.Dot(face, toEye) > 0) facing++;
                    up += (q * new OpenPlane.Core.Math.Vec3(0, 0, 1)).Y;
                    total++;
                }
            }
            if (total == 0) return;
            _out.WriteLine($"{facing}/{total} instruments face the eye; mean 12 o'clock up {up / total:F2}");
            Assert.True(facing > total * 0.9, $"{facing}/{total} face the eye");
            Assert.True(up / total > 0.85, $"12 o'clock mean up {up / total:F2}");
        }

        private static string? Sample(string name)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null)
            {
                var root = Path.Combine(dir.FullName, "samples-private", "flyout", "craft");
                if (Directory.Exists(root))
                    return Directory.GetDirectories(root, "*", SearchOption.AllDirectories).FirstOrDefault(d => Path.GetFileName(d) == name && File.Exists(Path.Combine(d, "data.txt")));
                dir = dir.Parent;
            }
            return null;
        }

        [Fact]
        public void FlyoutDetailCarriesOver()
        {
            var folder = Sample("Me-262A-1");
            if (folder == null) { _out.WriteLine("samples-private not found; skipped."); return; }
            var r = FlyoutConverter.Convert(FlyoutCraft.Load(folder));
            var d = r.Design;
            Assert.Contains(d.Materials, m => m.Kind == MaterialDef.Paint);
            Assert.Contains(d.Materials, m => m.Kind == MaterialDef.Glass);
            Assert.All(r.Meshes, m => Assert.NotNull(m.Materials));
            Assert.True(d.Instruments.Count >= 5, $"{d.Instruments.Count} instruments");
            Assert.NotNull(d.Cockpit?.Eye);
            Assert.Contains(r.Meshes, m => m.Role == MeshData.Interior && m.Name == "Seat");
            // Cut-out faces are gone, and the report says how many.
            Assert.Contains(r.Report.Items, i => i.Message.Contains("cut-out faces"));
            foreach (var m in r.Meshes) Assert.Equal(m.Triangles.Length / 3, m.TriangleMaterials.Length);

            var ifrit = Sample("KR-67 Ifrit");
            if (ifrit == null) return;
            var k = FlyoutConverter.Convert(FlyoutCraft.Load(ifrit)).Design;
            Assert.True(k.Lights.Count > 0);
            Assert.Contains(k.Lights, l => l.Kind == "nav");
        }
    }
}
