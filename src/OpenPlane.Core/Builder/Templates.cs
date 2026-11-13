using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;

namespace OpenPlane.Core.Builder
{
    /// <summary>A starting point for a new plane.</summary>
    public sealed class PlaneTemplate
    {
        public PlaneTemplate(string id, string name, string description, System.Func<PlaneSpec> create)
        { Id = id; Name = name; Description = description; Create = create; }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        /// <summary>Makes a fresh copy every time.</summary>
        public System.Func<PlaneSpec> Create { get; }
    }

    /// <summary>
    /// Built-in starting planes. Each one is a complete plane: it analyses with no problems and a static margin
    /// between 5 and 20 percent (checked by tests), and its gear is placed by <see cref="GearPlacer"/>.
    /// </summary>
    public static partial class Templates
    {
        public static readonly IReadOnlyList<PlaneTemplate> All = new List<PlaneTemplate>
        {
            new PlaneTemplate("trainer", "Trainer", "A forgiving propeller plane with a straight wing. Slow, stable and easy to land.", Trainer),
            new PlaneTemplate("fighter", "Fighter", "A single-jet fighter with a swept wing and an afterburner. Fast and agile.", Fighter),
            new PlaneTemplate("bomber", "Bomber", "A large twin-jet bomber with a long, slightly swept wing and lots of hardpoints.", Bomber),
            new PlaneTemplate("strike", "Strike fighter", "A twin-jet multirole strike fighter with an all-moving V-tail, internal bays, wing pylons and a full cockpit. Built on the KR-67 Ifrit.", StrikeFighter),
            new PlaneTemplate("blank", "Blank", "An empty plane. Add a fuselage, wings, an engine and a cockpit yourself.", Blank),
        };

        public static PlaneTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// The starter planes: the trainer, fighter and bomber templates as ready-made planes with fixed ids and names, so
        /// every player who adds them gets exactly the same planes (they match in multiplayer lobbies) and adding them
        /// twice changes nothing. Shipped in the app (one click on My planes) and as a plane pack with each release.
        /// </summary>
        public static readonly IReadOnlyList<(string Id, string Name, string Template, string Description)> Starters = new[]
        {
            ("57a47e4000000001", "OP-1 Wren", "trainer", "OpenPlane's starter trainer: a forgiving propeller plane with a straight wing. Slow, stable and easy to land."),
            ("57a47e4000000002", "OP-2 Kite", "fighter", "OpenPlane's starter fighter: a single-jet fighter with a swept wing and an afterburner."),
            ("57a47e4000000003", "OP-3 Heron", "bomber", "OpenPlane's starter bomber: a large twin-jet bomber with a long, slightly swept wing and lots of hardpoints."),
            ("57a47e4000000004", "OP-4 Lanner", "strike", "OpenPlane's starter strike fighter: a twin-jet multirole fighter with an all-moving V-tail, internal bays, four pylons, a 30 mm cannon and a full cockpit."),
        };

        /// <summary>The builder spec of starter plane <paramref name="index"/> (see <see cref="Starters"/>).</summary>
        public static PlaneSpec Starter(int index)
        {
            var (id, name, template, description) = Starters[index];
            var spec = Find(template)!.Create();
            spec.Id = id;
            spec.Name = name;
            spec.Author = "Meta";
            spec.Description = description;
            return spec;
        }

        public static PlaneSpec Blank() => new PlaneSpec { Name = "New plane" };

        /// <summary>
        /// A starter cockpit so every template shows what the builder can do: glass and a dark cockpit colour in the
        /// palette, a glass canopy, a seat and an instrument panel (interior pieces), four working instruments, navigation
        /// lights on the wingtips and a strobe on the fin. Everything sits relative to the cockpit eye.
        /// </summary>
        private static void AddCockpitKit(PlaneSpec s)
        {
            if (s.Cockpit == null) return;
            // The kit sits on top of the body: the eye clear of the skin and the panel's top above it, or the cockpit view
            // would be the inside of the hull (the Trainer's eye was 2 cm above its skin, its panel buried).
            double Top(double z)
            {
                double best = double.NegativeInfinity;
                foreach (var f in s.Fuselages.Where(f => f.Kind == FuselageSpec.LoftKind && f.Rings.Count >= 2 && System.Math.Abs(f.Position[0]) < 0.5))
                {
                    var rings = f.Rings.OrderByDescending(r => r.Z).ToList();
                    double lz = z - f.Position[2];
                    for (int i = 0; i + 1 < rings.Count; i++)
                    {
                        var a = rings[i]; var b = rings[i + 1];
                        if (lz > a.Z || lz < b.Z) continue;
                        double t = a.Z - b.Z < 1e-9 ? 0 : (a.Z - lz) / (a.Z - b.Z);
                        best = System.Math.Max(best, f.Position[1] + (a.Y + a.Height / 2) * (1 - t) + (b.Y + b.Height / 2) * t);
                    }
                }
                return best;
            }
            var p0 = s.Cockpit.Position;
            double topAtEye = Top(p0[2]), topAtPanel = Top(p0[2] + 0.62), raise = 0;
            if (!double.IsNegativeInfinity(topAtEye)) raise = System.Math.Max(raise, topAtEye + 0.25 - (p0[1] + 0.25));
            if (!double.IsNegativeInfinity(topAtPanel)) raise = System.Math.Max(raise, topAtPanel + 0.06 - (p0[1] + 0.25 - 0.3));
            if (raise > 0) s.Cockpit.Position = new[] { p0[0], p0[1] + raise, p0[2] };
            var p = s.Cockpit.Position;
            var eye = new[] { p[0], p[1] + 0.25, p[2] };
            s.Cockpit.Eye = eye;
            double[] At(double dx, double dy, double dz) => new[] { eye[0] + dx, eye[1] + dy, eye[2] + dz };
            s.Materials.Add(new MaterialDef { Id = "glass", Name = "Canopy glass", Kind = MaterialDef.Glass, Color = new[] { 0.55, 0.66, 0.74, 0.28 }, Metallic = 0.2, Smoothness = 0.95 });
            s.Materials.Add(new MaterialDef { Id = "cockpit", Name = "Cockpit grey", Color = new[] { 0.17, 0.18, 0.19, 1.0 }, Metallic = 0.1, Smoothness = 0.35 });

            ShapeSpec Shape(string id, string name, string kind, double[] size, double[] pos, string material, bool interior)
            {
                var sh = ShapeMesh.NewPrimitiveShape(kind, size);
                sh.Id = id; sh.Name = name; sh.Position = pos; sh.MaterialId = material; sh.Interior = interior;
                return sh;
            }
            s.Shapes.Add(Shape("canopy", "Canopy", "capsule", new[] { 0.72, 0.62, 1.7 }, At(0, -0.05, 0.05), "glass", false));
            s.Shapes.Add(Shape("seat", "Seat", "box", new[] { 0.46, 0.12, 0.46 }, At(0, -0.82, -0.1), "cockpit", true));
            s.Shapes.Add(Shape("seat-back", "Seat back", "box", new[] { 0.46, 0.72, 0.1 }, At(0, -0.45, -0.36), "cockpit", true));
            s.Shapes.Add(Shape("panel", "Instrument panel", "box", new[] { 0.62, 0.26, 0.05 }, At(0, -0.3, 0.62), "cockpit", true));

            // Facing the pilot: the face's +y turned to -z.
            var facing = new[] { -0.7071, 0, 0, 0.7071 };
            s.Instruments.Add(new InstrumentDef { Id = "airspeed", Name = "Airspeed", Kind = "gauge", Reads = "airspeed", Position = At(-0.17, -0.27, 0.59), Rotation = facing, Size = 0.075, Min = 0, Max = 1000, MinDeg = 30, MaxDeg = 330 });
            s.Instruments.Add(new InstrumentDef { Id = "horizon", Name = "Artificial horizon", Kind = "attitude", Position = At(0, -0.27, 0.59), Rotation = facing, Size = 0.09 });
            s.Instruments.Add(new InstrumentDef { Id = "altimeter", Name = "Altimeter", Kind = "gauge", Reads = "altitude", Position = At(0.17, -0.27, 0.59), Rotation = facing, Size = 0.075, Min = 0, Max = 10000, MinDeg = 30, MaxDeg = 330 });
            s.Instruments.Add(new InstrumentDef { Id = "compass", Name = "Compass", Kind = "compass", Reads = "heading", Position = At(0, -0.38, 0.59), Rotation = facing, Size = 0.06 });

            // Navigation lights on the main wingtips (green on the right; the mirrored copy is red), a strobe on top of the fin.
            var wing = s.Wings.Where(w => !w.Vertical).OrderByDescending(w => w.Span * (w.RootChord + w.TipChord)).FirstOrDefault();
            if (wing != null)
            {
                double sweep = System.Math.Tan(wing.SweepDeg * System.Math.PI / 180);
                double dih = wing.DihedralDeg * System.Math.PI / 180;
                var tip = new[] { wing.Root[0] + wing.Span * System.Math.Cos(dih), wing.Root[1] + wing.Span * System.Math.Sin(dih), wing.Root[2] - wing.Span * sweep - wing.TipChord * 0.3 };
                s.Lights.Add(new LightDef { Id = "nav", Name = "Navigation lights", Kind = "nav", Position = tip, Color = new[] { 0.1, 1.0, 0.2, 1 }, Size = 0.12, Mirror = true });
            }
            var fin = s.Wings.FirstOrDefault(w => w.Vertical);
            if (fin != null)
                s.Lights.Add(new LightDef { Id = "strobe", Name = "Tail strobe", Kind = "strobe", Position = new[] { fin.Root[0], fin.Root[1] + fin.Span, fin.Root[2] - fin.Span * System.Math.Tan(fin.SweepDeg * System.Math.PI / 180) - fin.TipChord * 0.5 }, Color = new[] { 1.0, 1.0, 1.0, 1 }, Size = 0.1 });
        }

        /// <summary>OpenPlane's red on the tail of every template (and so every starter plane), so they read well in the air.</summary>
        private static void RedTail(PlaneSpec s, params string[] wingIds)
        {
            s.Materials.Add(new MaterialDef { Id = "tail", Name = "Tail red", Color = new[] { 1.0, 0.27, 0.22, 1.0 }, Metallic = 0.05, Smoothness = 0.45 });
            foreach (var w in s.Wings) if (wingIds.Contains(w.Id)) w.MaterialId = "tail";
        }

        public static PlaneSpec Trainer()
        {
            var s = new PlaneSpec { Name = "Trainer", Paint = new PaintDef { BaseColor = new[] { 0.86, 0.87, 0.85, 1 } } };
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "fus1", Name = "Fuselage", Sides = 24,
                Rings = new List<FuselageRing>
                {
                    Ring(4.25, 0.00, 0.30, 0.30, 1.0),
                    Ring(3.95, 0.00, 0.85, 0.90, 0.85),
                    Ring(2.80, 0.05, 1.05, 1.15, 0.75),
                    Ring(1.00, 0.15, 1.05, 1.40, 0.75),
                    Ring(-0.80, 0.15, 0.90, 1.15, 0.75),
                    Ring(-3.20, 0.25, 0.45, 0.65, 0.8),
                    Ring(-4.90, 0.35, 0.14, 0.30, 0.8),
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing1", Name = "Main wing", Root = new[] { 0.45, -0.35, 2.5 }, Span = 4.6, RootChord = 2.0, TipChord = 1.1,
                SweepDeg = 2, DihedralDeg = 5, IncidenceDeg = 1.5, Mirror = true,
                Surfaces = new List<SurfaceSpec>
                {
                    new SurfaceSpec { Type = "flap", FromPct = 4, ToPct = 55, ChordPct = 25 },
                    new SurfaceSpec { Type = "aileron", FromPct = 58, ToPct = 95, ChordPct = 25 },
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing2", Name = "Tailplane", Root = new[] { 0.15, 0.25, -3.55 }, Span = 1.75, RootChord = 1.25, TipChord = 0.7,
                SweepDeg = 8, Mirror = true,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "elevator", FromPct = 0, ToPct = 100, ChordPct = 35 } },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing3", Name = "Fin", Root = new[] { 0, 0.55, -3.3 }, Span = 1.5, RootChord = 1.45, TipChord = 0.7,
                SweepDeg = 30, Vertical = true, Mirror = false,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "rudder", FromPct = 0, ToPct = 100, ChordPct = 35 } },
            });
            s.Engines.Add(new EngineSpec { Id = "eng1", Name = "Turboprop engine", Type = EngineSpec.Prop, Variant = "turboprop", PowerKw = 820, PropDiameterM = 2.4, Position = new[] { 0, 0.05, 4.35 } });
            s.Cockpit = new CockpitDef { Position = new[] { 0, 0.6, 0.7 } };
            s.Hardpoints.Add(new HardpointSpec
            {
                Id = "hp1", Name = "Wing station", Position = new[] { 2.3, -0.35, 1.6 }, Mirror = true,
                Allows = new List<string> { "rockets", "bomb", "missile-ground" },
            });
            AddCockpitKit(s);
            RedTail(s, "wing2", "wing3");
            s.Gear = GearPlacer.AutoPlace(s);
            return s;
        }

        public static PlaneSpec Fighter()
        {
            var s = new PlaneSpec { Name = "Fighter", Paint = new PaintDef { BaseColor = new[] { 0.55, 0.58, 0.62, 1 }, Smoothness = 0.4 } };
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "fus1", Name = "Fuselage", Sides = 28,
                Rings = new List<FuselageRing>
                {
                    Ring(7.6, -0.05, 0.00, 0.00, 1.0),
                    Ring(6.6, 0.00, 0.62, 0.62, 1.0),
                    Ring(4.8, 0.10, 1.05, 1.15, 0.9),
                    Ring(3.0, 0.15, 1.45, 1.35, 0.6),
                    Ring(0.0, 0.05, 1.55, 1.25, 0.5),
                    Ring(-3.0, 0.05, 1.40, 1.15, 0.6),
                    Ring(-5.4, 0.05, 1.10, 1.00, 0.9),
                    Ring(-6.6, 0.05, 0.95, 0.95, 1.0),
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing1", Name = "Main wing", Root = new[] { 0.7, -0.15, -0.3 }, Span = 4.2, RootChord = 4.2, TipChord = 1.2,
                SweepDeg = 38, DihedralDeg = -2, IncidenceDeg = 0, Mirror = true, ThicknessRatio = 0.05,
                Surfaces = new List<SurfaceSpec>
                {
                    new SurfaceSpec { Type = "flap", FromPct = 3, ToPct = 50, ChordPct = 22 },
                    new SurfaceSpec { Type = "aileron", FromPct = 52, ToPct = 92, ChordPct = 22 },
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing2", Name = "Tailplane", Root = new[] { 0.75, -0.1, -4.2 }, Span = 1.9, RootChord = 2.1, TipChord = 0.75,
                SweepDeg = 40, DihedralDeg = -6, Mirror = true, ThicknessRatio = 0.05,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "elevator", FromPct = 0, ToPct = 100, ChordPct = 40 } },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing3", Name = "Fin", Root = new[] { 0, 0.6, -3.4 }, Span = 2.6, RootChord = 3.0, TipChord = 1.0,
                SweepDeg = 45, Vertical = true, Mirror = false, ThicknessRatio = 0.05,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "rudder", FromPct = 5, ToPct = 95, ChordPct = 30 } },
            });
            s.Engines.Add(new EngineSpec { Id = "eng1", Name = "Jet engine", Type = EngineSpec.Jet, ThrustKn = 49, AfterburnerKn = 79, DiameterM = 0.89, Position = new[] { 0, 0.05, -5.27 } });   // its nozzle at the tail
            s.Cockpit = new CockpitDef { Position = new[] { 0, 0.75, 3.9 } };
            s.Guns.Add(new GunSpec { Id = "gun1", Name = "Cannon", CaliberMm = 20, Position = new[] { 0.55, 0.35, 3.2 }, Mirror = false });
            s.Hardpoints.Add(new HardpointSpec
            {
                Id = "hp1", Name = "Wingtip rail", Position = new[] { 4.85, -0.32, -4.1 }, Mirror = true,
                Allows = new List<string> { "missile-ir" },
            });
            s.Hardpoints.Add(new HardpointSpec
            {
                Id = "hp2", Name = "Outer wing", Position = new[] { 3.4, -0.45, -3.4 }, Mirror = true,
                Allows = new List<string> { "missile-ir", "missile-radar", "missile-ground", "bomb", "rockets" },
            });
            s.Hardpoints.Add(new HardpointSpec
            {
                Id = "hp3", Name = "Inner wing", Position = new[] { 2.0, -0.5, -2.8 }, Mirror = true,
                Allows = new List<string> { "missile-radar", "missile-ground", "bomb" },
            });
            s.Sensors = new SensorSet { Radar = true, RadarWarning = true, Countermeasures = true };
            AddCockpitKit(s);
            RedTail(s, "wing3");
            s.Gear = GearPlacer.AutoPlace(s);
            return s;
        }

        public static PlaneSpec Bomber()
        {
            var s = new PlaneSpec { Name = "Bomber", Paint = new PaintDef { BaseColor = new[] { 0.42, 0.45, 0.42, 1 } } };
            s.Fuselages.Add(new FuselageSpec
            {
                Id = "fus1", Name = "Fuselage", Sides = 28,
                Rings = new List<FuselageRing>
                {
                    Ring(13.2, -0.3, 0.00, 0.00, 1.0),
                    Ring(11.8, -0.1, 1.40, 1.30, 1.0),
                    Ring(9.6, 0.05, 2.30, 2.40, 0.9),
                    Ring(5.0, 0.10, 2.60, 2.70, 0.85),
                    Ring(-4.0, 0.10, 2.60, 2.70, 0.85),
                    Ring(-9.5, 0.50, 1.50, 1.80, 0.85),
                    Ring(-12.6, 0.90, 0.40, 0.60, 0.9),
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing1", Name = "Main wing", Root = new[] { 1.2, 0.6, 3.3 }, Span = 13.0, RootChord = 5.4, TipChord = 1.8,
                SweepDeg = 18, DihedralDeg = 3, IncidenceDeg = 1, Mirror = true, ThicknessRatio = 0.12,
                Surfaces = new List<SurfaceSpec>
                {
                    new SurfaceSpec { Type = "flap", FromPct = 3, ToPct = 60, ChordPct = 25 },
                    new SurfaceSpec { Type = "aileron", FromPct = 63, ToPct = 95, ChordPct = 25 },
                },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing2", Name = "Tailplane", Root = new[] { 0.5, 1.0, -8.6 }, Span = 4.3, RootChord = 3.4, TipChord = 1.4,
                SweepDeg = 25, DihedralDeg = 4, Mirror = true,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "elevator", FromPct = 0, ToPct = 100, ChordPct = 35 } },
            });
            s.Wings.Add(new WingSpec
            {
                Id = "wing3", Name = "Fin", Root = new[] { 0, 1.6, -7.8 }, Span = 4.6, RootChord = 4.6, TipChord = 1.8,
                SweepDeg = 35, Vertical = true, Mirror = false,
                Surfaces = new List<SurfaceSpec> { new SurfaceSpec { Type = "rudder", FromPct = 0, ToPct = 100, ChordPct = 32 } },
            });
            s.Engines.Add(new EngineSpec { Id = "eng1", Name = "Jet engine", Type = EngineSpec.Jet, ThrustKn = 70, AfterburnerKn = 0, DiameterM = 1.3, Position = new[] { 5.2, 0.05, 1.2 }, Mirror = true });
            s.Cockpit = new CockpitDef { Position = new[] { 0, 1.0, 10.4 } };
            s.Hardpoints.Add(new HardpointSpec { Id = "hp1", Name = "Bomb bay front", Position = new[] { 0, -0.8, 3.0 }, Mirror = false, Internal = true, Allows = new List<string> { "bomb", "missile-ground" } });
            s.Hardpoints.Add(new HardpointSpec { Id = "hp2", Name = "Bomb bay rear", Position = new[] { 0, -0.8, -0.2 }, Mirror = false, Internal = true, Allows = new List<string> { "bomb", "missile-ground" } });
            s.Hardpoints.Add(new HardpointSpec { Id = "hp3", Name = "Wing pylon", Position = new[] { 8.2, 0.6, 0.1 }, Mirror = true, Allows = new List<string> { "bomb", "missile-ground", "missile-radar" } });
            s.Sensors = new SensorSet { Radar = true, RadarWarning = true, Countermeasures = true, Jammer = true };
            AddCockpitKit(s);
            RedTail(s, "wing3");
            s.Gear = GearPlacer.AutoPlace(s);
            return s;
        }

        private static FuselageRing Ring(double z, double y, double width, double height, double roundness)
            => new FuselageRing { Z = z, Y = y, Width = width, Height = height, Roundness = roundness };
    }
}
