using System.Linq;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Physics;
using Xunit;

namespace OpenPlane.Core.Tests
{
    /// <summary>The builder warns when the pilot's eye is buried in the body (the game's cockpit camera sits there).</summary>
    public class CockpitViewTests
    {
        private static bool Warned(FlightReport r) => r.Checks.Any(c => c.Message.Contains("blocks the view"));

        [Fact]
        public void TemplatesSeeOutAndABuriedEyeIsCaught()
        {
            foreach (var spec in new[] { Templates.Trainer(), Templates.Fighter(), Templates.Bomber() })
            {
                var plane = DesignGenerator.Generate(spec);
                Assert.False(Warned(FlightModel.Analyze(plane.Design, plane.Meshes)), $"{spec.Name}: the template's eye should see out");

                // The eye moved into the middle of the fuselage, below its skin.
                var f = spec.Fuselages[0];
                var mid = f.Rings.OrderByDescending(r => r.Height).First();
                spec.Cockpit!.Eye = new[] { f.Position[0], f.Position[1] + mid.Y, f.Position[2] + mid.Z };
                var buried = DesignGenerator.Generate(spec);
                Assert.True(Warned(FlightModel.Analyze(buried.Design, buried.Meshes)), $"{spec.Name}: an eye inside the body should be caught");
            }
        }

        [Fact]
        public void TemplateCockpitSitsClearOfTheBody()
        {
            // The Trainer's eye used to be 2 cm above its skin with the instrument panel buried.
            var spec = Templates.Trainer();
            var f = spec.Fuselages[0];
            var eye = spec.Cockpit!.Eye!;
            var rings = f.Rings.OrderByDescending(r => r.Z).ToList();
            double lz = eye[2] - f.Position[2];
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                if (lz > rings[i].Z || lz < rings[i + 1].Z) continue;
                double t = (rings[i].Z - lz) / (rings[i].Z - rings[i + 1].Z);
                double top = f.Position[1] + (rings[i].Y + rings[i].Height / 2) * (1 - t) + (rings[i + 1].Y + rings[i + 1].Height / 2) * t;
                Assert.True(eye[1] >= top + 0.24, $"eye {eye[1]:0.00} m, skin top {top:0.00} m");
            }
        }
    }
}
