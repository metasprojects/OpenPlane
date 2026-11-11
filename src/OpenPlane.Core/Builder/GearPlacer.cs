using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;
using OpenPlane.Core.Physics;

namespace OpenPlane.Core.Builder
{
    /// <summary>
    /// Puts tricycle landing gear under a plane: main wheels just behind the centre of gravity (15 degree tip-back
    /// angle), a steerable nose wheel near the nose carrying roughly a tenth of the weight, struts long enough to keep
    /// the body, wings and propeller clear of the ground, and a track wide enough not to tip over.
    /// </summary>
    public static class GearPlacer
    {
        private const double TipBackDeg = 15;

        public static List<GearSpec> AutoPlace(PlaneSpec spec, IList<MeshData>? sourceMeshes = null)
        {
            var bare = BuilderJson.Clone(spec);
            bare.Gear.Clear();
            var gen = DesignGenerator.Generate(bare, sourceMeshes);
            var d = gen.Design;
            var flight = FlightModel.Analyze(d, gen.Meshes);
            var stats = DesignStats.Compute(d, gen.Meshes);

            var bodyTris = new List<(Vec3 a, Vec3 b, Vec3 c)>();
            foreach (var m in gen.Meshes)
                for (int i = 0; i + 2 < m.Triangles.Length; i += 3)
                    bodyTris.Add((FuselageLoft.Vertex(m.Vertices, m.Triangles[i]), FuselageLoft.Vertex(m.Vertices, m.Triangles[i + 1]), FuselageLoft.Vertex(m.Vertices, m.Triangles[i + 2])));
            var wingTris = new List<(Vec3 a, Vec3 b, Vec3 c)>();
            foreach (var w in d.Wings)
            {
                AddQuad(wingTris, w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading);
                foreach (var s in w.Surfaces) AddQuad(wingTris, s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading);
            }

            var pts = bodyTris.SelectMany(t => new[] { t.a, t.b, t.c }).Concat(wingTris.SelectMany(t => new[] { t.a, t.b, t.c })).ToList();
            if (pts.Count == 0) pts.Add(Vec3.Zero);
            double bottom = pts.Min(p => p.Y);
            foreach (var e in d.Engines)
            {
                double r = (e.PropDiameterM ?? 0) > 0 ? e.PropDiameterM!.Value / 2 : e.DiameterM / 2;
                bottom = System.Math.Min(bottom, e.Position[1] - r);
            }
            double noseZ = pts.Max(p => p.Z), tailZ = pts.Min(p => p.Z);
            double length = System.Math.Max(1, noseZ - tailZ);
            var cg = Vec3.FromArray(flight.CenterOfMass);
            double mac = flight.MeanChordM > 0.1 ? flight.MeanChordM : length * 0.15;

            double clearance = Clamp(0.035 * length, 0.25, 0.6);
            double ground = bottom - clearance;
            double gross = System.Math.Max(500, flight.GrossMassKg);
            double wheel = System.Math.Round(Clamp(0.17 + 0.000012 * gross, 0.2, 0.6), 2);
            double noseWheel = System.Math.Round(System.Math.Max(0.15, wheel * 0.75), 2);

            double hCg = System.Math.Max(0.3, cg.Y - ground);
            double mainZ = cg.Z - System.Math.Max(System.Math.Max(hCg * System.Math.Tan(TipBackDeg * System.Math.PI / 180), 0.12 * mac), 0.2);
            double noseGearZ = System.Math.Min(noseZ - 0.12 * length, mainZ + 11 * (cg.Z - mainZ));
            noseGearZ = System.Math.Max(noseGearZ, cg.Z + 0.12 * length);
            noseGearZ = System.Math.Min(noseGearZ, noseZ - 0.05 * length);

            double halfWidth = SectionHalfWidth(bodyTris, mainZ);
            double halfTrack = System.Math.Max(System.Math.Max(0.5 * hCg, 0.85 * halfWidth), 0.6);
            if (stats.SpanM > 0) halfTrack = System.Math.Min(halfTrack, 0.3 * stats.SpanM);
            halfTrack = System.Math.Round(halfTrack, 2);

            // Mount each strut a little inside the lowest skin above it (fuselage first, then wing).
            double Mount(double x, double z)
            {
                double? y = LowestHit(bodyTris, x, z) ?? LowestHit(wingTris, x, z);
                return (y ?? cg.Y - 0.3) + 0.05;
            }
            double mainMount = Mount(halfTrack, mainZ), noseMount = Mount(0, noseGearZ);
            double R(double v) => System.Math.Round(v, 2);
            double mainStrut = System.Math.Max(0.2, mainMount - ground - wheel), noseStrut = System.Math.Max(0.2, noseMount - ground - noseWheel);

            return new List<GearSpec>
            {
                new GearSpec
                {
                    Id = "gear-nose", Name = "Nose gear", Kind = "nose", Mirror = false,
                    Position = new[] { 0, R(noseMount), R(noseGearZ) }, StrutLength = R(noseStrut),
                    WheelRadius = noseWheel, Travel = R(0.2 * noseStrut), Steerable = true, Brakes = false, Retractable = true,
                },
                new GearSpec
                {
                    Id = "gear-main", Name = "Main gear", Kind = "main", Mirror = true,
                    Position = new[] { halfTrack, R(mainMount), R(mainZ) }, StrutLength = R(mainStrut),
                    WheelRadius = wheel, Travel = R(0.2 * mainStrut), Steerable = false, Brakes = true, Retractable = true,
                },
            };
        }

        private static void AddQuad(List<(Vec3, Vec3, Vec3)> list, double[] a, double[] b, double[] c, double[] d)
        {
            Vec3 pa = Vec3.FromArray(a), pb = Vec3.FromArray(b), pc = Vec3.FromArray(c), pd = Vec3.FromArray(d);
            list.Add((pa, pb, pc));
            list.Add((pa, pc, pd));
        }

        /// <summary>The lowest point where a vertical line at (x, z) crosses the triangles, or null.</summary>
        public static double? LowestHit(IEnumerable<(Vec3 a, Vec3 b, Vec3 c)> tris, double x, double z)
        {
            double? best = null;
            foreach (var (a, b, c) in tris)
            {
                double det = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                if (System.Math.Abs(det) < 1e-12) continue;
                double u = ((x - a.X) * (c.Z - a.Z) - (c.X - a.X) * (z - a.Z)) / det;
                double v = ((b.X - a.X) * (z - a.Z) - (x - a.X) * (b.Z - a.Z)) / det;
                if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                double y = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
                if (best == null || y < best) best = y;
            }
            return best;
        }

        /// <summary>Half the width of the body where it crosses the plane at station z (0 when nothing crosses).</summary>
        public static double SectionHalfWidth(IEnumerable<(Vec3 a, Vec3 b, Vec3 c)> tris, double z)
        {
            double best = 0;
            foreach (var (a, b, c) in tris)
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    if ((p.Z - z) * (q.Z - z) > 0 || System.Math.Abs(p.Z - q.Z) < 1e-12) continue;
                    double t = (z - p.Z) / (q.Z - p.Z);
                    best = System.Math.Max(best, System.Math.Abs(p.X + t * (q.X - p.X)));
                }
            return best;
        }

        /// <summary>The lowest wheel contact point (y), or null without gear.</summary>
        public static double? GroundLevel(AircraftDesign d)
            => d.Gear.Count == 0 ? (double?)null : d.Gear.Min(g => g.Position[1] - g.StrutLength - g.WheelRadius);

        private static double Clamp(double v, double lo, double hi) => System.Math.Max(lo, System.Math.Min(hi, v));
    }
}
