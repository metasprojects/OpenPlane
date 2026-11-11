using System;
using System.IO;
using System.Linq;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Physics;
using Xunit;

namespace OpenPlane.Core.Tests
{
    public class FlightModelTests
    {
        [Fact]
        public void RunwayEstimateIsPhysicalAndNeverBelowTheStockSetting()
        {
            // A Me 262-like jet: stall about 62 m/s, thrust 0.34 of its weight: around a kilometre and a half.
            double jet = GameEstimates.RunwayNeededM(62, 0.34);
            Assert.InRange(jet, 1000, 2000);
            // More thrust or a lower stall speed shortens it.
            Assert.True(GameEstimates.RunwayNeededM(62, 0.6) < jet);
            Assert.True(GameEstimates.RunwayNeededM(50, 0.34) < jet);
            Assert.Equal(0, GameEstimates.RunwayNeededM(0, 0.5));
            // The game never offers a plane at a runway shorter than its stock plane's setting.
            Assert.Equal(1200, GameEstimates.RunwayInGameM("Fighter1", 30, 2));
            Assert.Equal(100, GameEstimates.RunwayInGameM("COIN", 20, 1.5));
            Assert.Contains("too short", GameEstimates.WhereItCanTakeOff(1200));
            Assert.Contains("only start in the air", GameEstimates.WhereItCanTakeOff(5000));
            Assert.Equal("the desert airbase", GameEstimates.AirbaseName("airbase_desert1"));
        }

        private static string? Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var c = Path.Combine(dir.FullName, "samples-private", "flyout", "craft");
                if (Directory.Exists(c)) return c;
                dir = dir.Parent;
            }
            return null;
        }

        [Fact]
        public void EveryConvertedCraftGetsFiniteNumbers()
        {
            var root = Root(); if (root == null) return;
            foreach (var folder in Directory.GetFiles(root, "data.txt", SearchOption.AllDirectories).Select(Path.GetDirectoryName))
            {
                var r = FlyoutConverter.Convert(FlyoutCraft.Load(folder!));
                var f = FlightModel.Analyze(r.Design, r.Meshes);
                foreach (var v in new[] { f.EmptyMassKg, f.GrossMassKg, f.NeutralPointZ, f.StaticMarginPercent, f.ThrustToWeight, f.StallSpeedMs, f.WingLoadingKgM2 })
                    Assert.False(double.IsNaN(v) || double.IsInfinity(v), $"{r.Design.Name}: a flight number is not finite");
                Assert.True(f.EmptyMassKg > 200, $"{r.Design.Name}: empty mass {f.EmptyMassKg}");
                Assert.NotEmpty(f.Checks);
            }
        }

        [Fact]
        public void JetThrustMatchesTheRealEngine()
        {
            // The Me 262 replica's Jumo 004B engines: real static thrust 8.8 kN each.
            var root = Root(); if (root == null) return;
            var folder = Directory.GetDirectories(root, "Me-262A-1", SearchOption.AllDirectories).First();
            var r = FlyoutConverter.Convert(FlyoutCraft.Load(folder));
            var jets = r.Design.Engines.Where(e => e.Type == "turbojet").ToList();
            Assert.Equal(2, jets.Count);  // the small power turbine is left out
            Assert.All(jets, e => Assert.InRange(e.MaxThrustN, 7500, 10500));
            var f = FlightModel.Analyze(r.Design, r.Meshes);
            Assert.InRange(f.EmptyMassKg, 3000, 4600);  // real Me 262 A-1a empty: 3,795 kg
        }
    }
}
