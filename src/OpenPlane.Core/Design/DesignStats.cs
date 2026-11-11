using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Design
{
    /// <summary>Size, areas and thrust worked out from a design's shape.</summary>
    public static class DesignStats
    {
        /// <summary>Area of a four-sided panel from its corners (half the cross product of the diagonals).</summary>
        public static double QuadArea(double[] rootLeading, double[] rootTrailing, double[] tipTrailing, double[] tipLeading)
        {
            var d1 = Vec3.FromArray(tipTrailing) - Vec3.FromArray(rootLeading);
            var d2 = Vec3.FromArray(tipLeading) - Vec3.FromArray(rootTrailing);
            return Vec3.Cross(d1, d2).Length / 2;
        }

        public static double PanelArea(WingPanel w) => QuadArea(w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading);
        public static double SurfaceArea(SurfaceDef s) => QuadArea(s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading);

        /// <summary>True for panels that lift more upwards than sideways (wings, tailplanes); false for fins, canted fins included.</summary>
        public static bool IsHorizontal(WingPanel w) => System.Math.Abs(w.Up[1]) > System.Math.Abs(w.Up[0]);

        public static DesignStatsResult Compute(AircraftDesign d, IList<MeshData> meshes)
        {
            var pts = new List<Vec3>();
            foreach (var w in d.Wings)
            {
                pts.Add(Vec3.FromArray(w.RootLeading)); pts.Add(Vec3.FromArray(w.RootTrailing));
                pts.Add(Vec3.FromArray(w.TipLeading)); pts.Add(Vec3.FromArray(w.TipTrailing));
                foreach (var s in w.Surfaces)
                {
                    pts.Add(Vec3.FromArray(s.RootLeading)); pts.Add(Vec3.FromArray(s.RootTrailing));
                    pts.Add(Vec3.FromArray(s.TipLeading)); pts.Add(Vec3.FromArray(s.TipTrailing));
                }
            }
            var wingPts = pts.ToList();
            foreach (var f in d.Fuselages) { pts.Add(Vec3.FromArray(f.BoundsMin)); pts.Add(Vec3.FromArray(f.BoundsMax)); }

            var r = new DesignStatsResult();
            if (pts.Count > 0)
            {
                var min = pts.Aggregate(Vec3.Min); var max = pts.Aggregate(Vec3.Max);
                r.BoundsMin = min.ToArray(); r.BoundsMax = max.ToArray();
                r.LengthM = max.Z - min.Z;
                r.HeightM = max.Y - min.Y;
            }
            if (wingPts.Count > 0) r.SpanM = wingPts.Max(p => p.X) - wingPts.Min(p => p.X);

            foreach (var w in d.Wings)
            {
                double a = PanelArea(w) + w.Surfaces.Sum(SurfaceArea);
                if (IsHorizontal(w)) r.WingAreaM2 += a; else r.FinAreaM2 += a;
            }
            r.TotalStaticThrustN = d.Engines.Sum(e => System.Math.Max(e.MaxThrustN, e.AfterburnerThrustN));
            return r;
        }
    }
}
