using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Builder
{
    public static partial class Templates
    {
        // ------------------------------------------------------------------ the strike fighter (OP-4 Lanner)
        //
        // A single-seat, twin-engine multirole strike fighter built to show every part of the builder at once, sized to the
        // KR-67 Ifrit (19 m, 16 t) it flies on in the game so the game's flight model maps cleanly onto it:
        // - chined forebody with a separate radome, a cockpit tub sunk into the hull, a bubble canopy, twin engine nacelles
        //   with caret-style intakes and titanium nozzle shrouds, a beaver tail between the engines;
        // - a 42° cropped-delta wing (60 m² reference area, 5 % thick, 2° anhedral, 3° washout) with full-span leading-edge
        //   slats, inboard flaperons and outboard ailerons; leading-edge root extensions over the intakes;
        // - a 40° canted all-moving V-tail (pitch and yaw), like the Ifrit's;
        // - two afterburning turbofans (84 kN dry, 130 kN wet each), a 30 mm cannon in the right wing root;
        // - four internal bays (two on the centre line, one each side) and four underwing pylons;
        // - a full cockpit: seat, consoles, throttles, side-stick, instrument panel, glare shield, HUD, three displays,
        //   standby instruments, mirrors; and a full set of lights.
        // Numbers: 15.8 t empty, 8.2 t internal fuel, +9 g, 30° angle-of-attack limit.

        private const int LannerSides = 48;

        private static double Rd(double v) => System.Math.Round(v, 4);

        /// <summary>
        /// A chined cross-section: <see cref="LannerSides"/> points from the top towards +x. The upper half is a superellipse of
        /// exponent <paramref name="pu"/> (below 2 gives a pointed, chined side), the lower half of exponent <paramref name="pl"/>.
        /// With a tub (half width above 0) the top is sunk to <paramref name="floor"/> inside the tub and cut to
        /// <paramref name="sill"/> outside it: the open cockpit. Offsets are from the ring's centre.
        /// </summary>
        private static List<double[]> ChineSection(double halfW, double up, double down, double pu, double pl,
                                                   double tubHalfW = 0, double floor = 0, double sill = 0)
        {
            var pts = new List<double[]>();
            for (int k = 0; k < LannerSides; k++)
            {
                double t = 2 * System.Math.PI * k / LannerSides;
                double sn = System.Math.Sin(t), cs = System.Math.Cos(t);
                bool top = cs >= 0;
                double e = 2 / (top ? pu : pl);
                double x = halfW * SPow(sn, e);
                double y = (top ? up : down) * SPow(cs, e);
                if (tubHalfW > 0 && top)
                    y = System.Math.Abs(x) <= tubHalfW + 1e-9 ? System.Math.Min(y, floor) : System.Math.Min(y, sill);
                pts.Add(new[] { Rd(x), Rd(y), 0.0 });
            }
            return pts;
        }

        private static double SPow(double v, double p) => System.Math.Abs(v) < 1e-12 ? 0 : System.Math.Sign(v) * System.Math.Pow(System.Math.Abs(v), p);

        private static FuselageRing CRing(double z, double y, double halfW, double up, double down, double pu = 1.35, double pl = 2.3,
                                          double tubHalfW = 0, double floor = 0, double sill = 0)
            => new FuselageRing
            {
                Z = z, Y = y, Width = Rd(2 * halfW), Height = Rd(up + down), Roundness = 0.8,
                Points = halfW <= 0 ? null : ChineSection(halfW, up, down, pu, pl, tubHalfW, floor, sill),
            };

        private static ShapeSpec Prim(string id, string name, string kind, double[] size, double[] pos, string? material, bool interior = false,
                                      double[]? rotationDeg = null, bool mirror = false)
        {
            var sh = ShapeMesh.NewPrimitiveShape(kind, size);
            sh.Id = id; sh.Name = name; sh.Position = pos; sh.MaterialId = material; sh.Interior = interior; sh.Mirror = mirror;
            if (rotationDeg != null) sh.RotationDeg = rotationDeg;
            return sh;
        }

        /// <summary>
        /// An open bubble canopy: half-superellipse sections from the windscreen to the spine. Each station is
        /// (z, half width, base height, height above the base). Faces are wound outwards.
        /// </summary>
        private static ShapeSpec Bubble(string id, string name, (double z, double hw, double baseY, double h)[] stations, string material)
        {
            const int around = 20;
            var s = new ShapeSpec { Id = id, Name = name, MaterialId = material, Source = "mesh" };
            foreach (var st in stations)
                for (int j = 0; j <= around; j++)
                {
                    double th = System.Math.PI * j / around;                 // 0 at +x, pi at -x
                    double c = System.Math.Cos(th), sn = System.Math.Sin(th);
                    s.Vertices.Add(new[] { Rd(st.hw * SPow(c, 0.8)), Rd(st.baseY + st.h * SPow(sn, 0.7)), Rd(st.z) });
                }
            int row = around + 1;
            var centre = new Vec3(0, stations.Average(t => t.baseY), stations.Average(t => t.z));
            for (int i = 0; i + 1 < stations.Length; i++)
                for (int j = 0; j < around; j++)
                {
                    int a = i * row + j, b = a + 1, c = (i + 1) * row + j + 1, d = (i + 1) * row + j;
                    var face = new[] { a, b, c, d };
                    var pa = Vec3.FromArray(s.Vertices[a]); var pb = Vec3.FromArray(s.Vertices[b]); var pc = Vec3.FromArray(s.Vertices[c]);
                    var n = Vec3.Cross(pb - pa, pc - pa);
                    var mid = (pa + pb + pc + Vec3.FromArray(s.Vertices[d])) / 4;
                    if (Vec3.Dot(n, mid - centre) < 0) face = new[] { d, c, b, a };
                    s.Faces.Add(face);
                }
            return s;
        }

        private static double[] Q(double xDeg)
        {
            double h = xDeg * System.Math.PI / 360;
            return new[] { Rd(System.Math.Sin(h)), 0, 0, Rd(System.Math.Cos(h)) };
        }

        private static double[] QYX(double yDeg, double xDeg)
        {
            // Unity order for (x, y, 0) Euler angles: q = qy * qx.
            var q = Quat.AngleAxis(yDeg, Vec3.Up) * Quat.AngleAxis(xDeg, Vec3.Right);
            return new[] { Rd(q.X), Rd(q.Y), Rd(q.Z), Rd(q.W) };
        }

        /// <summary>An instrument rotation whose face (+y) looks along <paramref name="normal"/>, its 12 o'clock (+z) as near up as it can be.</summary>
        private static double[] FaceTowards(Vec3 normal)
        {
            var y = normal.Normalized;
            var up = System.Math.Abs(Vec3.Dot(y, Vec3.Up)) > 0.95 ? Vec3.Forward : Vec3.Up;
            var z = (up - y * Vec3.Dot(up, y)).Normalized;
            var x = Vec3.Cross(y, z);
            // Rotation matrix with columns x, y, z to a quaternion.
            double m00 = x.X, m01 = y.X, m02 = z.X, m10 = x.Y, m11 = y.Y, m12 = z.Y, m20 = x.Z, m21 = y.Z, m22 = z.Z;
            double tr = m00 + m11 + m22, qw, qx, qy, qz;
            if (tr > 0)
            {
                double S = System.Math.Sqrt(tr + 1.0) * 2;
                qw = 0.25 * S; qx = (m21 - m12) / S; qy = (m02 - m20) / S; qz = (m10 - m01) / S;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double S = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                qw = (m21 - m12) / S; qx = 0.25 * S; qy = (m01 + m10) / S; qz = (m02 + m20) / S;
            }
            else if (m11 > m22)
            {
                double S = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                qw = (m02 - m20) / S; qx = (m01 + m10) / S; qy = 0.25 * S; qz = (m12 + m21) / S;
            }
            else
            {
                double S = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                qw = (m10 - m01) / S; qx = (m02 + m20) / S; qy = (m12 + m21) / S; qz = 0.25 * S;
            }
            return new[] { Rd(qx), Rd(qy), Rd(qz), Rd(qw) };
        }

        /// <summary>
        /// A band arching over the cockpit from sill to sill at station <paramref name="z"/>, following the canopy's section
        /// (half width, base height, height): a canopy frame. <paramref name="thick"/> outwards, <paramref name="width"/> along the plane.
        /// </summary>
        private static ShapeSpec Arch(string id, string name, double z, double hw, double baseY, double h, double thick, double width, string material)
        {
            const int around = 20;
            var s = new ShapeSpec { Id = id, Name = name, MaterialId = material, Source = "mesh" };
            var axis = new Vec3(0, baseY, z);
            // Rings: 0 inner back, 1 outer back, 2 outer front, 3 inner front (each around + 1 points, from +x over the top to -x).
            for (int ring = 0; ring < 4; ring++)
                for (int j = 0; j <= around; j++)
                {
                    double th = System.Math.PI * j / around;
                    var p = new Vec3(hw * SPow(System.Math.Cos(th), 0.8), baseY + h * SPow(System.Math.Sin(th), 0.7), 0);
                    var outward = new Vec3(p.X, p.Y - baseY, 0).Normalized;
                    if (ring == 1 || ring == 2) p += outward * thick;
                    double pz = z + (ring == 2 || ring == 3 ? width / 2 : -width / 2);
                    s.Vertices.Add(new[] { Rd(p.X), Rd(p.Y), Rd(pz) });
                }
            int row = around + 1;
            void Quad(int a, int b, int c, int d, Vec3 want)
            {
                var pa = Vec3.FromArray(s.Vertices[a]); var pb = Vec3.FromArray(s.Vertices[b]); var pc = Vec3.FromArray(s.Vertices[c]);
                s.Faces.Add(Vec3.Dot(Vec3.Cross(pb - pa, pc - pa), want) >= 0 ? new[] { a, b, c, d } : new[] { d, c, b, a });
            }
            for (int j = 0; j < around; j++)
            {
                var mid = Vec3.FromArray(s.Vertices[j]);
                var radial = new Vec3(mid.X, mid.Y - axis.Y, 0).Normalized;
                for (int r = 0; r < 4; r++)
                {
                    int r2 = (r + 1) % 4;
                    // r 0: back face (inner back -> outer back) faces -z; r 1: outer face; r 2: front face; r 3: inner face.
                    var want = r == 0 ? -Vec3.Forward : r == 1 ? radial : r == 2 ? Vec3.Forward : -radial;
                    Quad(r * row + j, r * row + j + 1, r2 * row + j + 1, r2 * row + j, want);
                }
            }
            // The two feet on the sills.
            foreach (int j in new[] { 0, around })
                Quad(0 * row + j, 1 * row + j, 2 * row + j, 3 * row + j, -Vec3.Up);
            return s;
        }

        public static PlaneSpec StrikeFighter()
        {
            var s = new PlaneSpec
            {
                Name = "Strike fighter",
                Donor = "Multirole1",
                Paint = new PaintDef { BaseColor = new[] { 0.55, 0.58, 0.61, 1 }, Metallic = 0.12, Smoothness = 0.42 },
                Sensors = new SensorSet { Radar = true, Irst = true, TargetingPod = true, RadarWarning = true, Countermeasures = true, Jammer = true },
                Handling = new HandlingDef { GLimit = 9, AoaLimitDeg = 30 },
                // Balance set by the designer: radar, avionics, gun ammunition and the forward fuselage tanks put the loaded centre of
                // gravity at about 34 % of the mean chord (a relaxed-stability fighter), which the simple area estimate cannot see.
                Mass = new MassDef { EmptyKg = 15800, FuelKg = 8200, CenterOfMass = new[] { 0.0, 0.05, -1.72 } },
            };
            s.Materials.Add(new MaterialDef { Id = "radome", Name = "Radome grey", Color = new[] { 0.32, 0.34, 0.36, 1.0 }, Metallic = 0.0, Smoothness = 0.3 });
            s.Materials.Add(new MaterialDef { Id = "glass", Name = "Canopy glass (gold tint)", Kind = MaterialDef.Glass, Color = new[] { 0.72, 0.64, 0.42, 0.26 }, Metallic = 0.55, Smoothness = 0.95 });
            s.Materials.Add(new MaterialDef { Id = "cockpit", Name = "Cockpit grey", Color = new[] { 0.16, 0.17, 0.18, 1.0 }, Metallic = 0.1, Smoothness = 0.3 });
            s.Materials.Add(new MaterialDef { Id = "console", Name = "Console black", Color = new[] { 0.06, 0.06, 0.07, 1.0 }, Metallic = 0.05, Smoothness = 0.25 });
            s.Materials.Add(new MaterialDef { Id = "seat", Name = "Seat cushion", Color = new[] { 0.22, 0.24, 0.2, 1.0 }, Metallic = 0.0, Smoothness = 0.15 });
            s.Materials.Add(new MaterialDef { Id = "metal", Name = "Titanium", Color = new[] { 0.52, 0.5, 0.47, 1.0 }, Metallic = 0.9, Smoothness = 0.55 });
            s.Materials.Add(new MaterialDef { Id = "intake", Name = "Intake dark", Color = new[] { 0.04, 0.04, 0.045, 1.0 }, Metallic = 0.0, Smoothness = 0.1 });
            s.Materials.Add(new MaterialDef { Id = "panel", Name = "Panel grey", Color = new[] { 0.47, 0.5, 0.53, 1.0 }, Metallic = 0.1, Smoothness = 0.4 });
            s.Materials.Add(new MaterialDef { Id = "yellow", Name = "Ejection yellow", Color = new[] { 0.95, 0.76, 0.1, 1.0 }, Metallic = 0.0, Smoothness = 0.4 });
            s.Materials.Add(new MaterialDef { Id = "sensor", Name = "Sensor window", Kind = MaterialDef.Glass, Color = new[] { 0.08, 0.1, 0.12, 0.85 }, Metallic = 0.6, Smoothness = 0.95 });

            // ---- body: radome, chined forebody with the cockpit tub, spine and beaver tail
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "radome", Name = "Radome", Sides = LannerSides, MaterialId = "radome",
                Rings = new List<FuselageRing>
                {
                    new FuselageRing { Z = 10.20, Y = -0.05, Width = 0, Height = 0 },
                    CRing(9.90, -0.05, 0.15, 0.13, 0.13, 1.8, 2.0),
                    CRing(9.40, -0.04, 0.32, 0.28, 0.27, 1.6, 2.1),
                    CRing(8.80, -0.02, 0.45, 0.40, 0.38, 1.45, 2.2),
                },
            });
            const double tubHalf = 0.38, tubFloor = -0.28, tubSill = 0.50;   // offsets from the tub rings' centre (y 0.08)
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "fus1", Name = "Fuselage", Sides = LannerSides,
                Rings = new List<FuselageRing>
                {
                    CRing(8.80, -0.02, 0.45, 0.40, 0.38, 1.45, 2.2),
                    CRing(8.00, 0.02, 0.55, 0.50, 0.48),
                    CRing(7.30, 0.05, 0.62, 0.57, 0.55),
                    CRing(7.00, 0.07, 0.66, 0.60, 0.57),
                    // The cockpit tub: open between the sills from the instrument panel to the seat's bulkhead.
                    CRing(6.92, 0.08, 0.68, 0.56, 0.60, tubHalfW: tubHalf, floor: tubFloor, sill: tubSill),
                    CRing(6.30, 0.08, 0.70, 0.56, 0.62, tubHalfW: tubHalf, floor: tubFloor, sill: tubSill),
                    CRing(5.60, 0.08, 0.71, 0.56, 0.62, tubHalfW: tubHalf, floor: tubFloor, sill: tubSill),
                    CRing(5.05, 0.08, 0.72, 0.56, 0.62, tubHalfW: tubHalf, floor: tubFloor, sill: tubSill),
                    CRing(4.95, 0.09, 0.72, 0.58, 0.62),
                    CRing(4.20, 0.10, 0.73, 0.62, 0.61),
                    CRing(3.60, 0.10, 0.72, 0.62, 0.60, 1.5, 2.6),
                    CRing(2.00, 0.12, 0.74, 0.60, 0.58, 1.6, 3.0),
                    CRing(0.00, 0.14, 0.72, 0.56, 0.55, 1.7, 3.0),
                    CRing(-2.50, 0.16, 0.68, 0.50, 0.50, 1.8, 3.0),
                    CRing(-5.00, 0.18, 0.55, 0.38, 0.36, 1.9, 2.6),
                    CRing(-7.20, 0.18, 0.42, 0.16, 0.14, 2.0, 2.4),
                    CRing(-8.20, 0.18, 0.25, 0.08, 0.08, 2.0, 2.0),
                    new FuselageRing { Z = -8.50, Y = 0.18, Width = 0, Height = 0 },
                },
            });
            // ---- engine nacelles (mirrored), with caret-style intakes and titanium nozzle shrouds
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "nacelle", Name = "Engine nacelle", Sides = 32, Mirror = true, Position = new[] { 1.25, -0.22, 0 },
                Rings = new List<FuselageRing>
                {
                    new FuselageRing { Z = 3.60, Y = 0.00, Width = 0.94, Height = 1.10, Roundness = 0.15 },
                    new FuselageRing { Z = 2.70, Y = 0.00, Width = 1.04, Height = 1.13, Roundness = 0.3 },
                    new FuselageRing { Z = 0.50, Y = 0.00, Width = 1.12, Height = 1.12, Roundness = 0.55 },
                    new FuselageRing { Z = -3.50, Y = 0.00, Width = 1.14, Height = 1.10, Roundness = 0.8 },
                    new FuselageRing { Z = -6.60, Y = 0.00, Width = 1.10, Height = 1.06, Roundness = 1.0 },
                    new FuselageRing { Z = -7.55, Y = 0.00, Width = 1.07, Height = 1.04, Roundness = 1.0 },
                },
            });
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "nozzle", Name = "Nozzle shroud", Sides = 32, Mirror = true, Position = new[] { 1.25, -0.22, 0 }, MaterialId = "metal",
                Rings = new List<FuselageRing>
                {
                    new FuselageRing { Z = -7.55, Y = 0.00, Width = 1.07, Height = 1.04, Roundness = 1.0 },
                    new FuselageRing { Z = -8.10, Y = 0.00, Width = 1.03, Height = 1.01, Roundness = 1.0 },
                    new FuselageRing { Z = -8.45, Y = 0.00, Width = 0.96, Height = 0.95, Roundness = 1.0 },
                },
            });

            // ---- wings
            s.Wings.Add(new WingSpec
            {
                Id = "wing1", Name = "Main wing", Root = new[] { 0.0, 0.20, 2.60 }, Span = 7.0, RootChord = 7.14, TipChord = 1.43,
                SweepDeg = 42, DihedralDeg = -2, IncidenceDeg = 0.5, TwistDeg = -3, ThicknessRatio = 0.05, Mirror = true,
                Surfaces = new List<SurfaceSpec>
                {
                    new SurfaceSpec { Type = "slat", Edge = "leading", FromPct = 27, ToPct = 97, ChordPct = 14 },
                    new SurfaceSpec { Type = "flaperon", FromPct = 27, ToPct = 58, ChordPct = 26, MinDeg = -20, MaxDeg = 35, ServoDegPerSec = 70 },
                    new SurfaceSpec { Type = "aileron", FromPct = 58, ToPct = 92, ChordPct = 24, MinDeg = -25, MaxDeg = 25, ServoDegPerSec = 90,
                                      Inputs = new List<InputLink> { new InputLink { Axis = "roll", Gain = -25 } } },
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "lerx", Name = "Leading-edge root extension", Root = new[] { 0.70, 0.26, 5.00 }, Span = 1.12, RootChord = 3.02, TipChord = 0.06,
                SweepDeg = 74.5, DihedralDeg = 0, ThicknessRatio = 0.04, Mirror = true,
            });
            s.Wings.Add(new WingSpec
            {
                Id = "vtail", Name = "V-tail", Root = new[] { 1.25, 0.30, -4.60 }, Span = 3.8, RootChord = 3.5, TipChord = 1.2,
                SweepDeg = 42, DihedralDeg = 40, ThicknessRatio = 0.04, Mirror = true,
                // All-moving ruddervators: together for pitch, apart for yaw.
                Moving = new SurfaceSpec
                {
                    Type = "ruddervator", MinDeg = -25, MaxDeg = 25, ServoDegPerSec = 60,
                    Inputs = new List<InputLink> { new InputLink { Axis = "pitch", Gain = 25 }, new InputLink { Axis = "yaw", Gain = 12 } },
                },
            });

            // ---- engines, gun, stations
            s.Engines.Add(new EngineSpec
            {
                Id = "eng1", Name = "Afterburning turbofan", Type = EngineSpec.Jet, Variant = "turbofan", Title = "LT-130",
                ThrustKn = 84, AfterburnerKn = 130, DiameterM = 1.05, Position = new[] { 1.25, -0.22, -7.43 }, Mirror = true,
            });
            s.Guns.Add(new GunSpec { Id = "gun1", Name = "30 mm cannon", CaliberMm = 30, RoundsPerMinute = 1500, MuzzleVelocity = 870, Position = new[] { 0.80, 0.30, 3.70 } });
            var all = new List<string> { "missile-ir", "missile-radar", "missile-ground", "bomb", "rockets", "tank" };
            s.Hardpoints.Add(new HardpointSpec { Id = "hp-in", Name = "Inner pylon", Position = new[] { 3.20, -0.36, -1.60 }, Mirror = true, Allows = new List<string>(all) });
            s.Hardpoints.Add(new HardpointSpec { Id = "hp-out", Name = "Outer pylon", Position = new[] { 4.80, -0.42, -2.75 }, Mirror = true, Allows = new List<string> { "missile-ir", "missile-radar", "missile-ground", "bomb", "rockets" } });
            s.Hardpoints.Add(new HardpointSpec { Id = "bay-f", Name = "Main bay, front", Position = new[] { 0, -0.42, 0.90 }, Mirror = false, Internal = true, Allows = new List<string> { "missile-radar", "missile-ground", "bomb" } });
            s.Hardpoints.Add(new HardpointSpec { Id = "bay-r", Name = "Main bay, rear", Position = new[] { 0, -0.42, -2.20 }, Mirror = false, Internal = true, Allows = new List<string> { "missile-radar", "missile-ground", "bomb" } });
            s.Hardpoints.Add(new HardpointSpec { Id = "bay-s", Name = "Side bay", Position = new[] { 1.02, -0.70, 0.40 }, Mirror = true, Internal = true, Allows = new List<string> { "missile-ir" } });

            // ---- outside details
            s.Shapes.Add(Bubble("canopy", "Canopy", new[]
            {
                (7.30, 0.36, 0.66, 0.02), (7.05, 0.42, 0.62, 0.26), (6.70, 0.46, 0.58, 0.58), (6.30, 0.47, 0.58, 0.80),
                (5.90, 0.47, 0.58, 0.88), (5.45, 0.46, 0.58, 0.84), (5.00, 0.43, 0.60, 0.68), (4.55, 0.36, 0.64, 0.40),
                (4.10, 0.24, 0.70, 0.12), (3.80, 0.12, 0.72, 0.02),
            }, "glass"));
            s.Shapes.Add(Prim("rail", "Canopy rail", "box", new[] { 0.05, 0.05, 2.70 }, new[] { 0.465, 0.60, 5.95 }, "cockpit", mirror: true));
            s.Shapes.Add(Prim("intake-face", "Intake", "box", new[] { 0.86, 1.00, 0.04 }, new[] { 1.25, -0.22, 3.61 }, "intake", mirror: true));
            s.Shapes.Add(Prim("eo", "Sensor ball", "sphere", new[] { 0.24, 0.24, 0.24 }, new[] { 0.26, 0.60, 7.55 }, "sensor"));
            s.Shapes.Add(Prim("gun-port", "Gun port", "cylinder", new[] { 0.11, 0.11, 0.30 }, new[] { 0.80, 0.30, 3.75 }, "intake"));
            s.Shapes.Add(Prim("pitot", "Pitot probe", "cylinder", new[] { 0.03, 0.03, 0.45 }, new[] { 0.30, 0.22, 8.70 }, "metal", mirror: true));
            s.Shapes.Add(Prim("antenna", "Spine antenna", "box", new[] { 0.025, 0.20, 0.30 }, new[] { 0, 0.80, 1.60 }, "panel"));
            s.Shapes.Add(Prim("bay-door", "Main bay door", "box", new[] { 0.62, 0.025, 4.40 }, new[] { 0.32, -0.40, -0.65 }, "panel", mirror: true));
            s.Shapes.Add(Prim("side-bay-door", "Side bay door", "box", new[] { 0.40, 0.025, 1.90 }, new[] { 1.02, -0.77, 0.45 }, "panel", mirror: true));

            // ---- cockpit: seat, consoles, panel, glare shield (interior pieces, seen from the cockpit)
            var eye = new[] { 0.0, 1.00, 5.95 };
            s.Cockpit = new CockpitDef { Position = new[] { 0.0, 0.18, 5.95 }, Eye = eye };
            s.Shapes.Add(Prim("seat", "Seat pan", "box", new[] { 0.46, 0.12, 0.48 }, new[] { 0, 0.12, 5.92 }, "seat", true));
            s.Shapes.Add(Prim("seat-back", "Seat back", "box", new[] { 0.48, 0.80, 0.12 }, new[] { 0, 0.56, 5.60 }, "seat", true, new[] { -15.0, 0, 0 }));
            s.Shapes.Add(Prim("seat-frame", "Ejection seat frame", "box", new[] { 0.56, 1.05, 0.10 }, new[] { 0, 0.52, 5.50 }, "console", true, new[] { -15.0, 0, 0 }));
            s.Shapes.Add(Prim("headrest", "Headrest", "box", new[] { 0.32, 0.22, 0.16 }, new[] { 0, 1.04, 5.62 }, "cockpit", true, new[] { -15.0, 0, 0 }));
            s.Shapes.Add(Prim("handle", "Ejection handle", "box", new[] { 0.20, 0.03, 0.05 }, new[] { 0, 0.19, 6.18 }, "yellow", true));
            s.Shapes.Add(Prim("console", "Side console", "box", new[] { 0.18, 0.66, 1.00 }, new[] { 0.29, 0.11, 6.00 }, "console", true, mirror: true));
            s.Shapes.Add(Prim("floor", "Cockpit floor", "box", new[] { 0.74, 0.03, 1.80 }, new[] { 0, -0.19, 6.00 }, "cockpit", true));
            s.Shapes.Add(Prim("pedals", "Rudder pedals", "box", new[] { 0.36, 0.10, 0.06 }, new[] { 0, -0.05, 6.78 }, "console", true));
            var panelCentre = new Vec3(0, 0.52, 6.66);
            double tilt = 15 * System.Math.PI / 180;
            var n = new Vec3(0, System.Math.Sin(tilt), -System.Math.Cos(tilt));   // towards the pilot, tipped back
            var u = new Vec3(0, System.Math.Cos(tilt), System.Math.Sin(tilt));     // up the panel
            s.Shapes.Add(Prim("panel", "Instrument panel", "box", new[] { 0.80, 0.40, 0.05 }, (panelCentre - n * 0.025).ToArray(), "console", true, new[] { 15.0, 0, 0 }));
            s.Shapes.Add(Prim("glare", "Glare shield", "box", new[] { 0.84, 0.04, 0.30 }, new[] { 0, 0.74, 6.80 }, "console", true));
            s.Shapes.Add(Prim("bulkhead", "Rear bulkhead", "box", new[] { 0.76, 0.70, 0.04 }, new[] { 0, 0.20, 5.12 }, "cockpit", true));

            // ---- instruments on the panel (its face plane), on the consoles and on the canopy bow
            double[] OnPanel(double px, double pv) { var p = panelCentre + new Vec3(px, 0, 0) + u * pv + n * 0.006; return new[] { Rd(p.X), Rd(p.Y), Rd(p.Z) }; }
            var face = Q(-75);   // the face looks at the pilot: +y turned to n
            InstrumentDef G(string id, string name, string reads, double px, double pv, double size, double min, double max, double minDeg = 30, double maxDeg = 330)
                => new InstrumentDef { Id = id, Name = name, Kind = "gauge", Reads = reads, Position = OnPanel(px, pv), Rotation = face, Size = size, Min = min, Max = max, MinDeg = minDeg, MaxDeg = maxDeg };
            s.Instruments.Add(G("aoa", "Angle of attack", "aoa", -0.335, 0.12, 0.060, 0, 35));
            s.Instruments.Add(G("airspeed", "Airspeed", "airspeed", -0.235, 0.12, 0.075, 0, 1600));
            s.Instruments.Add(new InstrumentDef { Id = "adi", Name = "Attitude", Kind = "attitude", Position = OnPanel(-0.12, 0.12), Rotation = face, Size = 0.085 });
            s.Instruments.Add(G("altimeter", "Altimeter", "altitude", 0.12, 0.12, 0.075, 0, 20000));
            s.Instruments.Add(G("vsi", "Vertical speed", "vertical-speed", 0.235, 0.12, 0.075, -150, 150, 100, 440));
            s.Instruments.Add(G("gmeter", "G meter", "g", 0.335, 0.12, 0.060, -3, 10));
            s.Instruments.Add(new InstrumentDef { Id = "mfd-l", Name = "Left display (engine)", Kind = "screen", Reads = "engine", Position = OnPanel(-0.22, -0.02), Rotation = face, Size = 0.16 });
            s.Instruments.Add(new InstrumentDef { Id = "mfd-c", Name = "Centre display (flight)", Kind = "screen", Reads = "flight", Position = OnPanel(0, -0.02), Rotation = face, Size = 0.16 });
            s.Instruments.Add(new InstrumentDef { Id = "mfd-r", Name = "Right display (navigation)", Kind = "screen", Reads = "nav", Position = OnPanel(0.22, -0.02), Rotation = face, Size = 0.16 });
            s.Instruments.Add(G("rpm", "Engine speed", "rpm", -0.355, -0.02, 0.055, 0, 110));
            s.Instruments.Add(G("fuel", "Fuel", "fuel", 0.355, -0.02, 0.055, 0, 1));
            s.Instruments.Add(G("radalt", "Radar altimeter", "radar-altitude", -0.21, -0.15, 0.060, 0, 1500));
            s.Instruments.Add(G("throttle", "Throttle position", "throttle", -0.10, -0.15, 0.050, 0, 1));
            s.Instruments.Add(new InstrumentDef { Id = "hsi", Name = "Heading", Kind = "compass", Reads = "heading", Position = OnPanel(0, -0.15), Rotation = face, Size = 0.075 });
            s.Instruments.Add(G("clock", "Clock", "clock", 0.10, -0.15, 0.050, 0, 12, 0, 360));
            s.Instruments.Add(new InstrumentDef { Id = "sadi", Name = "Standby attitude", Kind = "attitude", Position = OnPanel(0.21, -0.15), Rotation = face, Size = 0.055 });
            s.Instruments.Add(new InstrumentDef { Id = "gear-handle", Name = "Gear handle", Kind = "lever", Reads = "gear", Position = OnPanel(-0.355, -0.15), Rotation = face, Size = 0.07 });
            s.Instruments.Add(new InstrumentDef { Id = "hud", Name = "Head-up display", Kind = "hud", Position = new[] { 0, 0.86, 6.80 }, Rotation = Q(-70), Size = 0.16 });
            var upFwd = Q(15);
            s.Instruments.Add(new InstrumentDef { Id = "throttle-l", Name = "Left throttle", Kind = "lever", Reads = "throttle", Position = new[] { -0.31, 0.445, 6.12 }, Rotation = upFwd, Size = 0.13 });
            s.Instruments.Add(new InstrumentDef { Id = "throttle-r", Name = "Right throttle", Kind = "lever", Reads = "throttle", Position = new[] { -0.26, 0.445, 6.12 }, Rotation = upFwd, Size = 0.13 });
            s.Instruments.Add(new InstrumentDef { Id = "flap-lever", Name = "Flap lever", Kind = "lever", Position = new[] { -0.30, 0.445, 5.75 }, Rotation = upFwd, Size = 0.06 });
            s.Instruments.Add(new InstrumentDef { Id = "stick", Name = "Side-stick", Kind = "lever", Reads = "stick", Position = new[] { 0.29, 0.445, 6.18 }, Rotation = Q(5), Size = 0.11 });
            s.Instruments.Add(new InstrumentDef { Id = "console-l", Name = "Radio panel", Kind = "screen", Position = new[] { -0.29, 0.446, 6.35 }, Rotation = new double[] { 0, 0, 0, 1 }, Size = 0.11 });
            s.Instruments.Add(new InstrumentDef { Id = "console-r", Name = "Systems panel", Kind = "screen", Position = new[] { 0.29, 0.446, 5.80 }, Rotation = new double[] { 0, 0, 0, 1 }, Size = 0.11 });
            // The windscreen bow, where the windscreen meets the canopy, and the three rear-view mirrors on it, each turned to
            // the pilot's eye.
            const double bowZ = 6.62, bowHw = 0.462, bowBase = 0.58, bowH = 0.62;
            s.Shapes.Add(Arch("bow", "Windscreen bow", bowZ, bowHw, bowBase, bowH, 0.035, 0.06, "cockpit"));
            var eyeV = Vec3.FromArray(eye);
            double[] OnBow(double phiDeg, double inset)
            {
                double phi = phiDeg * System.Math.PI / 180;
                var p = new Vec3(bowHw * SPow(System.Math.Cos(phi), 0.8), bowBase + bowH * SPow(System.Math.Sin(phi), 0.7), bowZ - 0.03);
                var toEye = (eyeV - p).Normalized;
                p += toEye * inset;
                return new[] { Rd(p.X), Rd(p.Y), Rd(p.Z) };
            }
            var mc = OnBow(90, 0.05);
            s.Instruments.Add(new InstrumentDef { Id = "mirror-c", Name = "Centre mirror", Kind = "mirror", Position = mc, Rotation = FaceTowards(eyeV - Vec3.FromArray(mc)), Size = 0.11 });
            var ms = OnBow(50, 0.05);
            s.Instruments.Add(new InstrumentDef { Id = "mirror-s", Name = "Side mirror", Kind = "mirror", Position = ms, Rotation = FaceTowards(eyeV - Vec3.FromArray(ms)), Size = 0.09, Mirror = true });

            // ---- lights
            var tip = new[] { 7.02, 0.20 - 7.0 * System.Math.Sin(2 * System.Math.PI / 180), 0 };
            s.Lights.Add(new LightDef { Id = "nav", Name = "Navigation light", Kind = "nav", Position = new[] { tip[0], Rd(tip[1]), -4.30 }, Color = new[] { 0.1, 1.0, 0.2, 1 }, Size = 0.11, Mirror = true });
            s.Lights.Add(new LightDef { Id = "strobe-wing", Name = "Wingtip strobe", Kind = "strobe", Position = new[] { tip[0], Rd(tip[1]), -4.95 }, Color = new[] { 1.0, 1.0, 1.0, 1 }, Size = 0.09, Mirror = true });
            s.Lights.Add(new LightDef { Id = "nav-tail", Name = "Tail light", Kind = "nav", Position = new[] { 0, 0.18, -8.56 }, Color = new[] { 1.0, 1.0, 1.0, 1 }, Size = 0.10 });
            double c40 = System.Math.Cos(40 * System.Math.PI / 180), s40 = System.Math.Sin(40 * System.Math.PI / 180);
            var vtip = new[] { Rd(1.25 + 3.8 * c40), Rd(0.30 + 3.8 * s40), Rd(-4.60 - 3.8 * System.Math.Tan(42 * System.Math.PI / 180) - 0.85) };
            s.Lights.Add(new LightDef { Id = "strobe-tail", Name = "Tail strobe", Kind = "strobe", Position = vtip, Color = new[] { 1.0, 1.0, 1.0, 1 }, Size = 0.09, Mirror = true });
            s.Lights.Add(new LightDef { Id = "beacon-top", Name = "Upper beacon", Kind = "beacon", Position = new[] { 0, 0.72, -1.20 }, Color = new[] { 1.0, 0.12, 0.08, 1 }, Size = 0.10 });
            s.Lights.Add(new LightDef { Id = "beacon-bottom", Name = "Lower beacon", Kind = "beacon", Position = new[] { 0, -0.46, 2.80 }, Color = new[] { 1.0, 0.12, 0.08, 1 }, Size = 0.10 });
            s.Lights.Add(new LightDef { Id = "landing", Name = "Landing light", Kind = "landing", Position = new[] { 0.10, -0.62, 5.70 }, Color = new[] { 1.0, 0.97, 0.9, 1 }, Size = 0.12 });
            s.Lights.Add(new LightDef { Id = "taxi", Name = "Taxi light", Kind = "landing", Position = new[] { -0.10, -0.62, 5.70 }, Color = new[] { 1.0, 0.97, 0.9, 1 }, Size = 0.09 });
            var formation = new[] { 0.55, 1.0, 0.45, 1 };
            var alongBody = new[] { 0, Rd(System.Math.Sin(System.Math.PI / 4)), 0, Rd(System.Math.Cos(System.Math.PI / 4)) };   // the strip turned 90° to lie along z
            s.Lights.Add(new LightDef { Id = "strip-nose", Name = "Formation light, nose", Kind = "strip", Position = new[] { 0.64, 0.12, 7.40 }, Rotation = alongBody, Color = formation, Size = 0.50, Mirror = true });
            s.Lights.Add(new LightDef { Id = "strip-nacelle", Name = "Formation light, nacelle", Kind = "strip", Position = new[] { 1.81, -0.10, -4.60 }, Rotation = alongBody, Color = formation, Size = 0.50, Mirror = true });
            s.Lights.Add(new LightDef { Id = "strip-tail", Name = "Formation light, tail", Kind = "strip", Position = new[] { Rd(1.25 + 2.8 * c40 + 0.03), Rd(0.30 + 2.8 * s40), -7.65 }, Rotation = QYX(90, 0), Color = formation, Size = 0.45, Mirror = true });

            RedTail(s, "vtail");
            s.Gear = GearPlacer.AutoPlace(s);
            // The main legs come down from the wing roots just outside the nacelles, as the Ifrit's do.
            var main = s.Gear.FirstOrDefault(g => g.Kind == "main");
            if (main != null)
            {
                double ground = main.Position[1] - main.StrutLength - main.WheelRadius;
                main.Position = new[] { 1.95, 0.02, main.Position[2] };
                main.StrutLength = System.Math.Round(main.Position[1] - ground - main.WheelRadius, 2);
                main.Travel = System.Math.Round(0.2 * main.StrutLength, 2);
                // The nose leg under the back of the cockpit, 6.5 m ahead of the centre of gravity like the Ifrit's, so the
                // stock leg needn't move along the plane (moved 0.9 m aft it carried more of the weight and broke taxiing).
                var nose = s.Gear.FirstOrDefault(g => g.Kind == "nose");
                if (nose != null)
                {
                    double noseZ = System.Math.Round(s.Mass.CenterOfMass![2] + 6.54, 2);
                    nose.Position = new[] { 0, -0.48, noseZ };
                    nose.StrutLength = System.Math.Round(nose.Position[1] - ground - nose.WheelRadius, 2);
                    nose.Travel = System.Math.Round(0.2 * nose.StrutLength, 2);
                }
            }
            return s;
        }
    }
}
