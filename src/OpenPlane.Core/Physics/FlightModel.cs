using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Physics
{
    public enum CheckLevel { Good, Note, Warning, Problem }

    public sealed class FlightCheck
    {
        public CheckLevel Level { get; set; }
        public string Message { get; set; } = "";
        public override string ToString() => $"[{Level}] {Message}";
    }

    public sealed class MassItem
    {
        public string Name { get; set; } = "";
        public double Kg { get; set; }
        public double[] Position { get; set; } = new double[3];
    }

    /// <summary>Everything the builder shows about how a design will fly. Estimates, not a simulation.</summary>
    public sealed class FlightReport
    {
        public double EmptyMassKg { get; set; }
        public double FuelMassKg { get; set; }
        public double GrossMassKg { get; set; }
        public double[] CenterOfMass { get; set; } = new double[3];
        /// <summary>Neutral point (pitch stability) as a z position along the nose axis.</summary>
        public double NeutralPointZ { get; set; }
        public double MeanChordM { get; set; }
        /// <summary>(neutral point behind centre of mass) / mean chord, in percent. Positive is stable.</summary>
        public double StaticMarginPercent { get; set; }
        /// <summary>
        /// The same margin as Nuclear Option's flight model sees it: every lifting part pulls in proportion to its area at its
        /// quarter chord, with no downwash, so tails count fully. This is what the plane does in the game before OpenPlane's
        /// automatic pitch balancing.
        /// </summary>
        public double GameMarginPercent { get; set; }
        /// <summary>
        /// How hard the pitch controls can turn the plane compared with how hard it resists, in the game's flight model (pitch
        /// control area times its arm, over the stability of all the lifting area). Infinity when the plane is neutral or unstable.
        /// See <see cref="GameEstimates.MinPitchReach"/>.
        /// </summary>
        public double PitchReach { get; set; } = double.PositiveInfinity;
        /// <summary>Runway needed to take off, in metres (<see cref="GameEstimates.RunwayNeededM"/>).</summary>
        public double RunwayNeededM { get; set; }
        public string Stability { get; set; } = "";
        public double WingAreaM2 { get; set; }
        public double SpanM { get; set; }
        public double AspectRatio { get; set; }
        public double WingLoadingKgM2 { get; set; }
        public double ThrustToWeight { get; set; }
        public double StallSpeedMs { get; set; }
        public double StallSpeedKnots => StallSpeedMs * 1.94384;
        public List<MassItem> MassBreakdown { get; set; } = new List<MassItem>();
        public List<FlightCheck> Checks { get; set; } = new List<FlightCheck>();
        public bool HasProblems => Checks.Any(c => c.Level == CheckLevel.Problem);
    }

    /// <summary>
    /// Quick estimates from the shape: weight, balance, pitch stability, stall speed and power, with plain warnings.
    /// Methods: panels joined into whole wings, lift slope per wing from its aspect ratio (Helmbold), tail downwash factor 0.6,
    /// area-based structure weights placed where each part is.
    /// Shared by the app (live readouts) and the mod (mass and balance in the game).
    /// </summary>
    public static class FlightModel
    {
        public const double SeaLevelDensity = 1.225;
        public const double Gravity = 9.81;

        private sealed class Lifter
        {
            public WingPanel Panel = null!;
            public double Area, LiftSlope, Efficiency = 1;
            public Vec3 Centroid, AeroCenter;
            public double SpanX, MeanChord;
            public bool Horizontal;
            /// <summary>Share of the panel's lift that acts in pitch (up.y squared: 1 for a flat wing, 0 for a fin, 0.5 for a 45° V-tail).</summary>
            public double PitchShare = 1;
            public int Group;
        }

        /// <summary>Joins panels into whole wings: a panel whose tip edge meets another's root edge, and mirror twins.</summary>
        private static int[] GroupPanels(List<Lifter> list)
        {
            var parent = Enumerable.Range(0, list.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
            const double tol = 0.03;
            bool Near(double[] a, double[] b) => Vec3.Distance(Vec3.FromArray(a), Vec3.FromArray(b)) < tol;
            bool Mirror(double[] a, double[] b) => System.Math.Abs(a[0] + b[0]) < tol && System.Math.Abs(a[1] - b[1]) < tol && System.Math.Abs(a[2] - b[2]) < tol;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var p = list[i].Panel; var q = list[j].Panel;
                    if (list[i].Horizontal != list[j].Horizontal) continue;
                    bool chained = (Near(p.TipLeading, q.RootLeading) && Near(p.TipTrailing, q.RootTrailing))
                                || (Near(q.TipLeading, p.RootLeading) && Near(q.TipTrailing, p.RootTrailing))
                                || (Near(p.RootLeading, q.RootLeading) && Near(p.RootTrailing, q.RootTrailing));
                    bool twins = Mirror(p.RootLeading, q.RootLeading) && Mirror(p.TipLeading, q.TipLeading) && Mirror(p.TipTrailing, q.TipTrailing);
                    if (chained || twins) Union(i, j);
                }
            // Wings interrupted by a body (a centre section between two fuselages, a wing through a nacelle): horizontal panels
            // in line across a gap of up to 2 m, at about the same height, that overlap fore and aft by half a chord.
            (double lo, double hi) Range(WingPanel p, int axis) => (
                new[] { p.RootLeading[axis], p.RootTrailing[axis], p.TipLeading[axis], p.TipTrailing[axis] }.Min(),
                new[] { p.RootLeading[axis], p.RootTrailing[axis], p.TipLeading[axis], p.TipTrailing[axis] }.Max());
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!list[i].Horizontal || !list[j].Horizontal || Find(i) == Find(j)) continue;
                    var p = list[i].Panel; var q = list[j].Panel;
                    var (px0, px1) = Range(p, 0); var (qx0, qx1) = Range(q, 0);
                    double gap = System.Math.Max(qx0 - px1, px0 - qx1);
                    if (gap > 2.0) continue;
                    var (pz0, pz1) = Range(p, 2); var (qz0, qz1) = Range(q, 2);
                    double overlap = System.Math.Min(pz1, qz1) - System.Math.Max(pz0, qz0);
                    double chord = System.Math.Min(list[i].MeanChord, list[j].MeanChord);
                    if (overlap < 0.5 * chord) continue;
                    if (System.Math.Abs(list[i].Centroid.Y - list[j].Centroid.Y) > 1.0) continue;
                    Union(i, j);
                }
            return Enumerable.Range(0, list.Count).Select(Find).ToArray();
        }

        public static FlightReport Analyze(AircraftDesign d, IList<MeshData> meshes)
        {
            var r = new FlightReport();
            var stats = DesignStats.Compute(d, meshes); // never the stored stats: they are not authoritative
            r.SpanM = stats.SpanM;

            // ---- lifting surfaces
            var lifters = new List<Lifter>();
            foreach (var w in d.Wings)
            {
                var quads = new List<(double[] rl, double[] rt, double[] tt, double[] tl)> { (w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading) };
                quads.AddRange(w.Surfaces.Select(s => (s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading)));
                double area = 0; var weighted = Vec3.Zero;
                foreach (var q in quads)
                {
                    double a = DesignStats.QuadArea(q.rl, q.rt, q.tt, q.tl);
                    var c = (Vec3.FromArray(q.rl) + Vec3.FromArray(q.rt) + Vec3.FromArray(q.tt) + Vec3.FromArray(q.tl)) / 4;
                    area += a; weighted += c * a;
                }
                if (area < 1e-6) continue;
                var centroid = weighted / area;
                var rootMid = Vec3.Lerp(Vec3.FromArray(w.RootLeading), Vec3.FromArray(w.RootTrailing), 0.5);
                var tipMid = Vec3.Lerp(Vec3.FromArray(w.TipLeading), Vec3.FromArray(w.TipTrailing), 0.5);
                double spanLen = System.Math.Max(0.05, Vec3.Distance(rootMid, tipMid));
                double meanChord = area / spanLen;
                // Each panel usually has a mirrored twin, so its aspect ratio is that of the pair.
                double ar = (2 * spanLen) * (2 * spanLen) / (2 * area);
                double upY = Vec3.FromArray(w.Up).Normalized.Y;
                lifters.Add(new Lifter
                {
                    Panel = w,
                    Area = area,
                    Centroid = centroid,
                    AeroCenter = centroid + new Vec3(0, 0, 0.25 * meanChord),   // quarter chord ahead of mid-chord (z is forward)
                    LiftSlope = Helmbold(ar),
                    SpanX = System.Math.Abs(tipMid.X - rootMid.X),
                    MeanChord = meanChord,
                    Horizontal = DesignStats.IsHorizontal(w),
                    PitchShare = upY * upY,
                });
            }
            // Flyout builders cut wings into many panels; each panel's lift slope comes from its whole wing's aspect ratio.
            var groupOf = GroupPanels(lifters);
            for (int i = 0; i < lifters.Count; i++) lifters[i].Group = groupOf[i];
            foreach (var g in lifters.GroupBy(l => l.Group))
            {
                var corners = g.SelectMany(l => new[] { l.Panel.RootLeading, l.Panel.RootTrailing, l.Panel.TipLeading, l.Panel.TipTrailing }).ToList();
                bool flat = g.First().Horizontal;
                double lo = corners.Min(c => flat ? c[0] : c[1]), hi = corners.Max(c => flat ? c[0] : c[1]);
                double span = System.Math.Max(0.05, hi - lo), area = g.Sum(l => l.Area);
                // A wing on one side only (a single fin, or a half wing without its twin) behaves like half of a wing twice as wide.
                bool oneSided = flat ? (lo > -0.05 || hi < 0.05) : true;
                double ar = oneSided ? 2 * span * span / area : span * span / area;
                double slope = Helmbold(ar);
                foreach (var l in g) l.LiftSlope = slope;
            }
            var horizontal = lifters.Where(l => l.Horizontal).ToList();
            // The main wing: the largest group of joined horizontal panels.
            var mainGroup = horizontal.GroupBy(l => l.Group).OrderByDescending(g => g.Sum(l => l.Area)).FirstOrDefault()?.ToList() ?? new List<Lifter>();
            r.WingAreaM2 = horizontal.Sum(l => l.Area);

            // ---- mass estimate
            var items = new List<MassItem>();
            var cockpit = d.Cockpit != null ? Vec3.FromArray(d.Cockpit.Position) : Vec3.Zero;
            double finArea = lifters.Where(l => !l.Horizontal).Sum(l => l.Area);
            // Each wing's weight where that wing is (main wing, tail and canards separately).
            foreach (var g in horizontal.GroupBy(l => l.Group))
            {
                var list = g.ToList();
                items.Add(Item(list == mainGroup || g.Key == (mainGroup.FirstOrDefault()?.Group ?? -1) ? "Main wing" : "Tail and other wings", 32 * list.Sum(l => l.Area), Centroid(list)));
            }
            if (finArea > 0) items.Add(Item("Fins", 26 * finArea, Centroid(lifters.Where(l => !l.Horizontal).ToList())));

            double fuselageKg = 0; var fuselageCenter = cockpit;
            if (meshes.Count > 0 && d.Fuselages.Count > 0)
            {
                double wetted = 0; var c = Vec3.Zero;
                // Interior pieces (seats, panels) are inside the body: they add neither skin nor drag.
                foreach (var m in meshes) { if (m.Role == MeshData.Interior) continue; var (a, cen) = MeshAreaCentroid(m); wetted += a; c += cen * a; }
                if (wetted > 0)
                {
                    fuselageCenter = c / wetted;
                    // Cap overlapping and internal pieces: no more than twice a simple tube of the plane's length and largest section.
                    double frontal = d.Fuselages.Where(f => f.MeshIndex < 0 || f.MeshIndex >= meshes.Count || meshes[f.MeshIndex].Role != MeshData.Interior).Select(f => f.FrontalArea).DefaultIfEmpty(0).Max();
                    double diameter = System.Math.Sqrt(4 * System.Math.Max(frontal, 0.05) / System.Math.PI);
                    double tube = System.Math.PI * diameter * System.Math.Max(stats.LengthM, 1) * 0.8;
                    fuselageKg = 18 * System.Math.Min(wetted, 2 * tube);
                    items.Add(Item("Fuselage", fuselageKg, fuselageCenter));
                }
            }

            double engineKg = 0;
            foreach (var e in d.Engines)
            {
                // Jets: thrust-based for modern engines, face-area-based for early ones (Jumo 004 ~745 kg, F404 ~1,036 kg).
                // Pistons ~0.9 kg per kW with their gear; turboprops ~0.3 (a PT6A of 820 kW is ~260 kg); electric ~0.5 with batteries elsewhere.
                double kg = e.Type == "piston-prop" || e.Type == "electric-prop" || e.Type == "turboprop"
                    ? (e.Type == "turboprop" ? 0.3 : e.Type == "electric-prop" ? 0.5 : 0.9) * (e.PowerKw ?? e.MaxThrustN / 25) + 30 * (e.PropDiameterM ?? 2)
                    : e.Type == "turbojet"
                        ? System.Math.Max(0.018 * System.Math.Max(e.MaxThrustN, e.AfterburnerThrustN / 1.55), 1300 * System.Math.Pow(e.DiameterM, 2))
                        : 0.018 * System.Math.Max(e.MaxThrustN, e.AfterburnerThrustN / 1.55);   // fans: light for their size
                engineKg += kg;
                items.Add(Item("Engine" + (e.Title != null ? " " + e.Title : ""), kg, Vec3.FromArray(e.Position)));
            }

            // Guns and their ammunition (a 30 mm cannon with its rounds is about 110 kg).
            foreach (var gun in d.Guns)
                if (gun.CaliberMm > 0) items.Add(Item($"Gun ({gun.CaliberMm:0.#} mm)", (2.0 * gun.CaliberMm + 50) * System.Math.Max(1, gun.Barrels > 2 ? 2 : 1), Vec3.FromArray(gun.Position)));

            double structure = items.Sum(i => i.Kg);
            items.Add(Item("Cockpit and systems", 250 + 0.04 * structure, d.Cockpit != null ? cockpit : fuselageCenter));
            if (d.Gear.Count > 0)
            {
                var gearCenter = d.Gear.Select(g => Vec3.FromArray(g.Position)).Aggregate(Vec3.Zero, (a, b) => a + b) / d.Gear.Count;
                items.Add(Item("Landing gear", 0.035 * structure + 15 * d.Gear.Count, gearCenter));
            }

            // Real airframes get heavier faster than their size: the simple area estimate times (size / 324 m²)^0.6, where size is
            // the body skin plus both sides of every wing. Fitted to the Me 262, He 162 and the stock KR-67 and AB-4 (within ~10 %).
            double size = fuselageKg / 18 + 2 * (r.WingAreaM2 + finArea);
            double sizeFactor = size > 0 ? System.Math.Min(2.0, System.Math.Max(0.5, System.Math.Pow(size / 324, 0.6))) : 1;
            foreach (var i in items)
                if (!i.Name.StartsWith("Engine", StringComparison.Ordinal) && !i.Name.StartsWith("Gun", StringComparison.Ordinal)) i.Kg = System.Math.Round(i.Kg * sizeFactor, 1);

            double estimatedEmpty = items.Sum(i => i.Kg);
            double empty = d.Mass.EmptyKg > 0 ? d.Mass.EmptyKg : estimatedEmpty;
            if (d.Mass.EmptyKg > 0 && estimatedEmpty > 0)
                foreach (var i in items) i.Kg *= empty / estimatedEmpty;

            bool props = d.Engines.Count > 0 && d.Engines.All(e => e.Type.EndsWith("prop", StringComparison.Ordinal));
            double fuel = d.Mass.FuelKg > 0 ? d.Mass.FuelKg : empty * (props ? 0.18 : 0.35);
            var tankPanels = horizontal.Where(l => l.Panel.FuelTank).ToList();
            var fuelAt = tankPanels.Count > 0 ? Centroid(tankPanels) : mainGroup.Count > 0 ? Centroid(mainGroup) : fuselageCenter;
            items.Add(Item("Fuel", fuel, fuelAt));

            r.EmptyMassKg = System.Math.Round(empty);
            r.FuelMassKg = System.Math.Round(fuel);
            r.GrossMassKg = r.EmptyMassKg + r.FuelMassKg;
            r.MassBreakdown = items;

            var com = d.Mass.CenterOfMass != null
                ? Vec3.FromArray(d.Mass.CenterOfMass)
                : items.Aggregate(Vec3.Zero, (a, i) => a + Vec3.FromArray(i.Position) * i.Kg) / System.Math.Max(1e-9, items.Sum(i => i.Kg));
            r.CenterOfMass = com.ToArray();

            // ---- pitch stability (neutral point)
            if (horizontal.Count > 0)
            {
                var mainSet = mainGroup.Count > 0 ? mainGroup : horizontal;
                double mainArea = mainSet.Sum(l => l.Area);
                double mainSpan = System.Math.Max(0.1, mainSet.SelectMany(l => new[] { l.Panel.RootLeading[0], l.Panel.TipLeading[0], l.Panel.RootTrailing[0], l.Panel.TipTrailing[0] }).DefaultIfEmpty(0).Max()
                                                  - mainSet.SelectMany(l => new[] { l.Panel.RootLeading[0], l.Panel.TipLeading[0], l.Panel.RootTrailing[0], l.Panel.TipTrailing[0] }).DefaultIfEmpty(0).Min());
                if (mainSet.All(l => l.Centroid.X > -0.05) || mainSet.All(l => l.Centroid.X < 0.05)) mainSpan *= 2;   // one side drawn
                r.MeanChordM = mainArea / mainSpan;
                double mainAcZ = mainSet.Sum(l => l.AeroCenter.Z * l.Area) / System.Math.Max(1e-9, mainArea);
                // Wings well behind the main wing (tails) sit in its downwash.
                foreach (var l in horizontal)
                    l.Efficiency = l.AeroCenter.Z < mainAcZ - 1.0 * r.MeanChordM ? 0.6 : 1.0;
                double num = horizontal.Sum(l => l.LiftSlope * l.Efficiency * l.PitchShare * l.Area * l.AeroCenter.Z);
                double den = horizontal.Sum(l => l.LiftSlope * l.Efficiency * l.PitchShare * l.Area);
                r.NeutralPointZ = num / System.Math.Max(1e-9, den);
                // The game's rule: lift in proportion to area at the quarter chord, no downwash (PlaneBuilder.MeasurePitch).
                double gameDen = lifters.Sum(l => l.PitchShare * l.Area);
                double gameNp = lifters.Sum(l => l.PitchShare * l.Area * l.AeroCenter.Z) / System.Math.Max(1e-9, gameDen);
                r.GameMarginPercent = 100 * (com.Z - gameNp) / System.Math.Max(0.1, r.MeanChordM);
                // Pitch reach as the game sees it: pitch control area (elevators, elevons, all-moving tails) times its arm, over the
                // stiffness of all the lifting area about the centre of gravity.
                double stiffness = lifters.Sum(l => l.PitchShare * l.Area * (com.Z - l.AeroCenter.Z));
                double control = 0;
                foreach (var l in lifters)
                {
                    var w = l.Panel;
                    if ((w.Inputs ?? new List<InputLink>()).Any(i => i.Axis == "pitch"))
                        control += l.PitchShare * l.Area * System.Math.Abs(com.Z - l.AeroCenter.Z);
                    foreach (var sf in w.Surfaces.Where(sf => sf.Inputs.Any(i => i.Axis == "pitch")))
                    {
                        double a = DesignStats.QuadArea(sf.RootLeading, sf.RootTrailing, sf.TipTrailing, sf.TipLeading);
                        double z = (sf.RootLeading[2] + sf.RootTrailing[2] + sf.TipTrailing[2] + sf.TipLeading[2]) / 4;
                        control += l.PitchShare * a * System.Math.Abs(com.Z - z);
                    }
                }
                r.PitchReach = stiffness > 1e-6 ? control / stiffness : double.PositiveInfinity;
                // The fuselage pushes the neutral point forward (Munk moment): dCm/da = Kf w^2 L / (S c), Kf ~ 0.3 per radian (conservative; tune against in-game flight).
                if (d.Fuselages.Count > 0)
                {
                    var body = d.Fuselages.OrderByDescending(f => f.FrontalArea).First();
                    double width = System.Math.Sqrt(4 * body.FrontalArea / System.Math.PI);
                    double length = body.BoundsMax[2] - body.BoundsMin[2];
                    double cla = den / System.Math.Max(0.01, r.WingAreaM2);
                    double dcm = 0.3 * width * width * length / (System.Math.Max(0.01, r.WingAreaM2) * System.Math.Max(0.1, r.MeanChordM));
                    r.NeutralPointZ += dcm / System.Math.Max(0.5, cla) * r.MeanChordM;
                }
                r.StaticMarginPercent = 100 * (com.Z - r.NeutralPointZ) / System.Math.Max(0.1, r.MeanChordM);
                r.AspectRatio = r.SpanM * r.SpanM / System.Math.Max(0.01, r.WingAreaM2);
            }

            r.WingLoadingKgM2 = r.WingAreaM2 > 0 ? r.GrossMassKg / r.WingAreaM2 : 0;
            double thrust = d.Engines.Sum(e => System.Math.Max(e.MaxThrustN, e.AfterburnerThrustN));
            r.ThrustToWeight = r.GrossMassKg > 0 ? thrust / (r.GrossMassKg * Gravity) : 0;
            r.StallSpeedMs = r.WingAreaM2 > 0 ? System.Math.Sqrt(2 * r.GrossMassKg * Gravity / (SeaLevelDensity * r.WingAreaM2 * 1.3)) : 0;
            r.RunwayNeededM = System.Math.Round(GameEstimates.RunwayInGameM(d.Donor, r.StallSpeedMs, r.ThrustToWeight));

            AddChecks(d, r, lifters);
            if (CockpitView.Blocked(d, meshes))
                r.Checks.Add(new FlightCheck { Level = CheckLevel.Warning, Message =
                    "From the pilot's eye the body's skin blocks the view ahead and above, so in the game the cockpit view would be the inside of the hull. " +
                    "Move the eye up (Cockpit, Pilot's eye), or make that part of the body glass." });
            return r;
        }

        private static void AddChecks(AircraftDesign d, FlightReport r, List<Lifter> lifters)
        {
            void Add(CheckLevel l, string m) => r.Checks.Add(new FlightCheck { Level = l, Message = m });

            if (r.WingAreaM2 <= 0) { Add(CheckLevel.Problem, "There are no wings, so the plane can't fly. Add a wing."); r.Stability = "No wings"; }
            else
            {
                double sm = r.StaticMarginPercent, gm = r.GameMarginPercent;
                // A converted Flyout plane flies in Flyout; its weight here is an estimate, so a strange balance is more likely the
                // estimate than the plane. In the game OpenPlane balances very stiff and very unstable planes automatically.
                bool imported = d.Origin?.Source == "flyout";
                string fixHint = " To change it yourself: move weight forward (or the wings back) for more stability, the other way for less.";
                if (sm < -10)
                {
                    r.Stability = "Very unstable";
                    if (imported)
                        Add(CheckLevel.Warning, $"By OpenPlane's weight estimate the centre of gravity is behind the neutral point ({sm:0}% of a wing chord). The estimate may be off (Flyout files don't store weights). In the game OpenPlane moves the lift back if needed so the autopilot can hold it." + fixHint);
                    else
                        Add(CheckLevel.Problem, $"The centre of gravity is far behind the neutral point ({sm:0}% of a wing chord), so the plane would flip. In the game OpenPlane moves the lift back so it can fly, but it won't handle as designed. Move weight forward, move the wings back, or make the tail bigger.");
                }
                else if (sm < -0.5) { r.Stability = "Unstable"; Add(CheckLevel.Warning, $"Slightly unstable ({sm:0}%). Fly-by-wire can hold it, like a modern fighter, but it will be twitchy. Move weight forward or the wings back to fix it."); }
                else if (sm < 3) { r.Stability = "Neutral"; Add(CheckLevel.Note, $"Almost neutral in pitch ({sm:0}%): very agile, needs a steady hand."); }
                else if (sm <= 25) { r.Stability = "Stable"; Add(CheckLevel.Good, $"Stable in pitch ({sm:0}% static margin)."); }
                else
                {
                    r.Stability = "Very stable";
                    Add(imported ? CheckLevel.Note : CheckLevel.Warning, $"Very stable ({sm:0}%): slow to pitch up and hard to rotate on takeoff. In the game OpenPlane moves the lift forward or enlarges the elevator if needed so it can still lift its nose." + fixHint);
                }
                // Nuclear Option has no downwash, so tails work harder there than in Flyout or real life.
                if (gm > sm + 15 && gm > 30)
                    Add(CheckLevel.Note, $"In Nuclear Option's flight model the tail works harder, so this plane is stiffer in pitch there ({gm:0}% margin). OpenPlane rebalances it when the game loads it.");
            }

            // Pitch control strong enough for the game (OpenPlane makes up the difference in the game, but then the plane flies
            // less like its design).
            if (r.WingAreaM2 > 0 && !double.IsInfinity(r.PitchReach) && r.PitchReach < GameEstimates.MinPitchReach)
            {
                if (r.PitchReach < 0.4)
                    Add(CheckLevel.Warning, $"The elevator is small for how stable this plane is (pitch reach {r.PitchReach:0.0}; the game needs about {GameEstimates.MinPitchReach:0.0} to lift its nose on takeoff). In the game OpenPlane moves the lift forward or enlarges the elevator a lot to make up for it, so it won't fly quite as designed. To fix it yourself: enlarge the elevator, or move the wing forward.");
                else
                    Add(CheckLevel.Note, $"In the game OpenPlane adjusts its pitch balance a little so it can lift its nose on takeoff (pitch reach {r.PitchReach:0.0}; the game needs about {GameEstimates.MinPitchReach:0.0}). A bigger elevator, or the wing a little further forward, makes that unnecessary.");
            }

            // Runway: where the game offers it.
            if (r.RunwayNeededM > 0)
            {
                string where = GameEstimates.WhereItCanTakeOff(r.RunwayNeededM);
                if (r.RunwayNeededM > GameEstimates.LongestRunwayM)
                    Add(CheckLevel.Warning, $"It needs about {r.RunwayNeededM:0} m of runway to take off. {where} More thrust, less weight or more wing area shortens its takeoff run.");
                else
                    Add(CheckLevel.Note, $"It needs about {r.RunwayNeededM:0} m of runway to take off. {where} The game offers it wherever its stock plane is offered, at airbases with a long enough runway.");
            }

            var roles = d.Wings.SelectMany(w => (w.Inputs ?? new List<InputLink>()).Concat(w.Surfaces.SelectMany(s => s.Inputs))).Select(i => i.Axis).ToList();
            if (!roles.Contains("pitch")) Add(CheckLevel.Problem, "Nothing controls pitch. Add an elevator (or elevons) to the tail.");
            if (!roles.Contains("roll")) Add(CheckLevel.Problem, "Nothing controls roll. Add ailerons (or elevons) to the wings.");
            if (!roles.Contains("yaw")) Add(CheckLevel.Warning, "Nothing controls yaw. Add a rudder to the fin.");
            if (!lifters.Any(l => !l.Horizontal) && !d.Wings.Any(w => System.Math.Abs(w.Up[0]) > 0.5))
                Add(CheckLevel.Warning, "There's no fin, so the plane may slide sideways or spin. Add a vertical fin.");

            if (d.Engines.Count == 0) Add(CheckLevel.Problem, "There's no engine. Add one.");
            else if (r.ThrustToWeight < 0.2) Add(CheckLevel.Warning, $"Underpowered: thrust is {r.ThrustToWeight:0.00} of the plane's weight. Takeoffs will be long. Add thrust or lose weight.");
            if (d.Gear.Count == 0) Add(CheckLevel.Warning, "There's no landing gear; the plane can only start in the air.");
            else if (d.Gear.Count < 3) Add(CheckLevel.Warning, "Fewer than three wheels: the plane may tip over on the ground.");
            if (d.Cockpit == null) Add(CheckLevel.Problem, "There's no cockpit. Add one.");
            if (d.Hardpoints.Count == 0)
                Add(CheckLevel.Note, "No hardpoints: in the game it carries only its guns. Add hardpoints where bombs and missiles should hang.");

            if (r.WingLoadingKgM2 > 750) Add(CheckLevel.Warning, $"High wing loading ({r.WingLoadingKgM2:0} kg/m²): fast takeoffs and wide turns. Bigger wings help.");
            if (r.StallSpeedKnots > 175) Add(CheckLevel.Warning, $"Stall speed is high (about {r.StallSpeedKnots:0} kt). Bigger wings or flaps help.");

            // Left/right balance of lifting area.
            double moment = lifters.Sum(l => l.Area * l.Centroid.X), arm = lifters.Sum(l => l.Area * System.Math.Abs(l.Centroid.X));
            if (arm > 0 && System.Math.Abs(moment) / arm > 0.05)
                Add(CheckLevel.Warning, "The left and right sides don't match, so the plane will roll on its own. Turn on mirroring or match the wings.");
        }

        /// <summary>Lift slope per radian of a wing with aspect ratio <paramref name="ar"/> (Helmbold).</summary>
        private static double Helmbold(double ar) => 2 * System.Math.PI * ar / (2 + System.Math.Sqrt(ar * ar + 4));

        private static MassItem Item(string name, double kg, Vec3 at) => new MassItem { Name = name, Kg = System.Math.Round(kg, 1), Position = at.ToArray() };

        private static Vec3 Centroid(List<Lifter> list)
        {
            double a = list.Sum(l => l.Area);
            return a > 0 ? list.Aggregate(Vec3.Zero, (s, l) => s + l.Centroid * l.Area) / a : Vec3.Zero;
        }

        private static (double area, Vec3 centroid) MeshAreaCentroid(MeshData m)
        {
            var v = m.Vertices; var t = m.Triangles;
            double total = 0; var c = Vec3.Zero;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var a = new Vec3(v[t[i] * 3], v[t[i] * 3 + 1], v[t[i] * 3 + 2]);
                var b = new Vec3(v[t[i + 1] * 3], v[t[i + 1] * 3 + 1], v[t[i + 1] * 3 + 2]);
                var cc = new Vec3(v[t[i + 2] * 3], v[t[i + 2] * 3 + 1], v[t[i + 2] * 3 + 2]);
                double area = Vec3.Cross(b - a, cc - a).Length / 2;
                total += area; c += (a + b + cc) / 3 * area;
            }
            return total > 0 ? (total, c / total) : (0, Vec3.Zero);
        }
    }
}
