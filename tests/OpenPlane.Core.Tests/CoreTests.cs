using System.Linq;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Math;
using Xunit;

namespace OpenPlane.Core.Tests
{
    public class ParserTests
    {
        private const string Sample = @"My Craft
Version=0.2504
launch_rot=0.0

ControlLaw
{
 name=Default
 gain=1,1,1
}
Part
{
 resource_path=Parts/ControlSurf
 id=4
 parent_id=2
 *InputProcessor
 {
  range=-20,20
  response=0,-14
  response=1,-14
 }
 *ModelSwitcher
 {
 }
  {
 {
 }
  }
 warnings
 {
  stall=0.00
 }
}
|---------------------------------------------------------------------|
";

        [Fact]
        public void ReadsBlocksValuesAndRepeatedKeys()
        {
            var parser = new FlyoutParser();
            var root = parser.Parse(Sample);
            Assert.Empty(parser.Warnings);
            Assert.Equal("0.2504", root.Get("Version"));
            Assert.Equal("My Craft", root.Values.First().Key);
            Assert.Null(root.Values.First().Value);

            var law = root.Child("ControlLaw");
            Assert.NotNull(law);
            Assert.Equal(new double[] { 1, 1, 1 }, law!.GetNumbers("gain"));

            var part = root.Child("Part")!;
            Assert.Equal(4, part.GetInt("id"));
            var ip = part.Child("*InputProcessor")!;
            Assert.Equal(new[] { "0,-14", "1,-14" }, ip.GetAll("response").ToArray());
            Assert.NotNull(part.Child("warnings"));
            Assert.Equal(0.0, part.Child("warnings")!.GetDouble("stall"));
            Assert.Equal(2, part.Children.Count(c => c.Name == "*ModelSwitcher" || c.Name == ""));
        }

        [Fact]
        public void ToleratesUnbalancedBraces()
        {
            var parser = new FlyoutParser();
            var root = parser.Parse("A\n{\nx=1\n}\n}\nB\n{\ny=2\n");
            Assert.Equal(2, parser.Warnings.Count);
            Assert.Equal(1, root.Child("A")!.GetInt("x"));
            Assert.Equal(2, root.Child("B")!.GetInt("y"));
        }

        [Fact]
        public void ParsesFlyoutNumberFormats()
        {
            Assert.True(FlyoutValues.TryVec3("-2,2.980232E-08,-0.1949039", out var v));
            Assert.Equal(-2, v.X, 9);
            Assert.Equal(2.980232e-08, v.Y, 12);
            Assert.Equal(new[] { 0.208, 0.208, 0.208, 1.0 }, FlyoutValues.Numbers("RGBA(0.208, 0.208, 0.208, 1.000)"));
        }
    }

    public class MathTests
    {
        [Fact]
        public void RotatesLikeUnity()
        {
            var q = Quat.AngleAxis(90, Vec3.Up);            // yaw right 90 degrees
            var v = q * Vec3.Forward;
            Assert.Equal(1, v.X, 9); Assert.Equal(0, v.Z, 9);

            var parent = new Pose(new Vec3(1, 0, 0), q);
            var child = parent.Compose(new Pose(new Vec3(0, 0, 2), Quat.Identity));
            Assert.Equal(3, child.Position.X, 9);
            Assert.Equal(0, child.Position.Z, 9);
        }

        [Fact]
        public void MirroredRotationMirrorsDirections()
        {
            var q = new Quat(-0.008715, 0.000457, 0.052335, 0.998592).Normalized;
            var d = q * new Vec3(-1, 0.2, 0.3);
            var m = q.MirrorX * new Vec3(1, 0.2, 0.3);
            Assert.Equal(-d.X, m.X, 9); Assert.Equal(d.Y, m.Y, 9); Assert.Equal(d.Z, m.Z, 9);
        }
    }
}
