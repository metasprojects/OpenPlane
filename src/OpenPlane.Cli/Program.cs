using System;
using System.IO;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Physics;
using OpenPlane.Core.Preview;

namespace OpenPlane.Cli
{
    /// <summary>
    /// openplane convert &lt;craft folder&gt; [-o plane.oplane] [--png view.png] [--builder name]
    /// openplane inspect &lt;file.oplane&gt;
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Craft and part names are often not plain English (Grève, Ä, ü): print them as they are.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try
            {
                if (args.Length >= 2 && args[0] == "convert") return Convert(args);
                if (args.Length >= 2 && args[0] == "inspect") return Inspect(args[1]);
                if (args.Length >= 2 && args[0] == "template") return Template(args);
                if (args.Length >= 1 && args[0] == "starters") return Starters(args);
                Console.WriteLine("Usage:\n  openplane convert <Flyout craft folder> [-o plane.oplane] [--png view.png] [--builder name]\n  openplane inspect <plane.oplane>\n  openplane template <trainer|fighter|bomber|strike> [-o plane.oplane]   (a builder template, as the app makes it)\n  openplane starters [-o folder]   (the four starter planes, as the app adds them)");
                return 2;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Error: " + e.Message);
                return 1;
            }
        }

        private static string? Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int Convert(string[] args)
        {
            var folder = args[1];
            var craft = FlyoutCraft.Load(folder);
            if (FlyoutConverter.IsHelicopter(craft))
            {
                Console.Error.WriteLine($"\"{craft.Name}\" is a helicopter. OpenPlane builds fixed-wing planes only for now, so it can't convert helicopters yet.");
                return 1;
            }
            var result = FlyoutConverter.Convert(craft, Option(args, "--builder"));
            result.Design.Id ??= OplanePackage.NewId();
            var png = ThreeView.RenderPng(result.Design, result.Meshes, 300);
            var output = Option(args, "-o") ?? Path.Combine(Directory.GetCurrentDirectory(), Sanitize(craft.Name) + OplanePackage.Extension);
            OplanePackage.Write(output, result.Design, result.Meshes, result.Report, png);
            var pngPath = Option(args, "--png");
            if (pngPath != null) File.WriteAllBytes(pngPath, ThreeView.RenderPng(result.Design, result.Meshes, 500));
            Print(result.Design, result.Report);
            bool flightProblems = PrintFlight(result.Design, result.Meshes);
            Console.WriteLine($"Saved {output}");
            return result.Report.HasProblems || flightProblems ? 3 : 0;
        }

        private static int Template(string[] args)
        {
            var t = OpenPlane.Core.Builder.Templates.Find(args[1]) ?? throw new ArgumentException($"There is no template called \"{args[1]}\".");
            var spec = t.Create();
            var plane = OpenPlane.Core.Builder.DesignGenerator.Generate(spec);
            plane.Design.Id ??= OplanePackage.NewId();
            var output = Option(args, "-o") ?? Path.Combine(Directory.GetCurrentDirectory(), Sanitize(spec.Name) + OplanePackage.Extension);
            OpenPlane.Core.Builder.BuilderPackage.Write(output, spec, plane, null, ThreeView.RenderPng(plane.Design, plane.Meshes, 300));
            Print(plane.Design, new ImportReport());
            bool problems = PrintFlight(plane.Design, plane.Meshes);
            Console.WriteLine($"Saved {output}");
            return problems ? 3 : 0;
        }

        /// <summary>openplane starters [-o folder]: the starter planes, exactly as the app adds them.</summary>
        private static int Starters(string[] args)
        {
            var folder = Option(args, "-o") ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(folder);
            bool problems = false;
            for (int i = 0; i < OpenPlane.Core.Builder.Templates.Starters.Count; i++)
            {
                var spec = OpenPlane.Core.Builder.Templates.Starter(i);
                var plane = OpenPlane.Core.Builder.DesignGenerator.Generate(spec);
                var output = Path.Combine(folder, Sanitize(spec.Name) + OplanePackage.Extension);
                OpenPlane.Core.Builder.BuilderPackage.Write(output, spec, plane, null, ThreeView.RenderPng(plane.Design, plane.Meshes, 300));
                problems |= PrintFlight(plane.Design, plane.Meshes);
                Console.WriteLine($"Saved {output}");
            }
            return problems ? 3 : 0;
        }

        private static int Inspect(string path)
        {
            var c = OplanePackage.Read(path);
            Print(c.Design, c.Report ?? new ImportReport());
            PrintFlight(c.Design, c.Meshes);
            Console.WriteLine($"Meshes: {c.Meshes.Count}, triangles: {c.Meshes.Sum(m => m.Triangles.Length / 3):N0}, thumbnail: {(c.Thumbnail != null ? "yes" : "no")}");
            return 0;
        }

        private static void Print(AircraftDesign d, ImportReport report)
        {
            var s = d.Stats ?? new DesignStatsResult();
            Console.WriteLine($"{d.Name}  (donor {d.Donor})");
            Console.WriteLine($"  span {s.SpanM:0.00} m, length {s.LengthM:0.00} m, height {s.HeightM:0.00} m, wing area {s.WingAreaM2:0.0} m2, fin area {s.FinAreaM2:0.0} m2");
            Console.WriteLine($"  wings {d.Wings.Count}, surfaces {d.Wings.Sum(w => w.Surfaces.Count)}, fuselages {d.Fuselages.Count}, engines {d.Engines.Count}, gear {d.Gear.Count}, hardpoints {d.Hardpoints.Count}, guns {d.Guns.Count}");
            foreach (var line in report.Lines()) Console.WriteLine("  " + line);
        }

        /// <summary>Prints the flight estimate; true when its checks found a problem.</summary>
        private static bool PrintFlight(AircraftDesign d, System.Collections.Generic.IList<MeshData> meshes)
        {
            var f = FlightModel.Analyze(d, meshes);
            Console.WriteLine($"  flight: empty {f.EmptyMassKg:0} kg, gross {f.GrossMassKg:0} kg, CG z {f.CenterOfMass[2]:0.00}, NP z {f.NeutralPointZ:0.00}, MAC {f.MeanChordM:0.00} m, margin {f.StaticMarginPercent:0}% ({f.Stability}; in game {f.GameMarginPercent:0}%), T/W {f.ThrustToWeight:0.00}, stall {f.StallSpeedKnots:0} kt, loading {f.WingLoadingKgM2:0} kg/m2");
            Console.WriteLine("  weight: " + string.Join(", ", f.MassBreakdown.GroupBy(m => m.Name).Select(g => $"{g.Key} {g.Sum(m => m.Kg):0} kg")));
            foreach (var c in f.Checks) Console.WriteLine("    " + c);
            return f.HasProblems;
        }

        private static string Sanitize(string name) => OpenPlane.Core.FileNames.Sanitize(name);
    }
}
