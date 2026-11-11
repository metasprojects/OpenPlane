using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Builder
{
    /// <summary>A design made from a <see cref="PlaneSpec"/>, ready to analyse or save.</summary>
    public sealed class GeneratedPlane
    {
        public GeneratedPlane(AircraftDesign design, List<MeshData> meshes, List<BuilderNote> notes)
        { Design = design; Meshes = meshes; Notes = notes; }

        public AircraftDesign Design { get; }
        /// <summary>Output meshes; <see cref="FuselageBody.MeshIndex"/> points into this list.</summary>
        public List<MeshData> Meshes { get; }
        public List<BuilderNote> Notes { get; }
        /// <summary>For each fuselage item (by id), the index of its first output mesh.</summary>
        public Dictionary<string, int> MeshIndexByItem { get; } = new Dictionary<string, int>();
    }

    /// <summary>Picks the stock Nuclear Option plane a design is built on.</summary>
    public static class DonorPicker
    {
        public const string Auto = "auto";

        /// <summary>Stock aircraft keys the mod can build on.</summary>
        public static readonly string[] StockKeys = { "COIN", "Fighter1", "Multirole1", "CAS1", "FastBomber1", "QuadVTOL1", "VTOLTrainer1" };

        /// <summary>A stock plane "auto" can pick, with its size and empty mass (read from the game's prefabs).</summary>
        public sealed class StockPlane
        {
            public StockPlane(string key, double lengthM, double emptyKg, bool prop) { Key = key; LengthM = lengthM; EmptyKg = emptyKg; Prop = prop; }
            public string Key { get; }
            public double LengthM { get; }
            public double EmptyKg { get; }
            public bool Prop { get; }
        }

        /// <summary>
        /// The donors "auto" chooses from. Numbers from the game's prefabs (AircraftDefinition length and mass):
        /// CI-22 Cricket 10.4 m / 3,100 kg, FS-12 Revoker 15.8 m / 8,680 kg, KR-67 Ifrit 19.0 m / 16,040 kg,
        /// AB-4 Alkyon 24.9 m / 34,100 kg. Other stock planes can still be chosen by hand.
        /// </summary>
        public static readonly StockPlane[] AutoCandidates =
        {
            new StockPlane("COIN", 10.4, 3100, prop: true),
            new StockPlane("Fighter1", 15.8, 8680, prop: false),
            new StockPlane("Multirole1", 19.0, 16040, prop: false),
            new StockPlane("FastBomber1", 24.9, 34100, prop: false),
        };

        /// <summary>
        /// Any propeller engine: COIN. Otherwise the jet donor whose length and empty mass are closest to the design's
        /// (smallest sum of the two size ratios, in log terms), because the mod keeps the donor's internals and balance
        /// points: a donor much bigger or heavier than the design flies it badly.
        /// </summary>
        public static string Pick(AircraftDesign d, IList<MeshData> meshes)
        {
            if (d.Engines.Any(e => e.Type.EndsWith("prop", StringComparison.Ordinal))) return "COIN";
            var stats = DesignStats.Compute(d, meshes);
            double mass = d.Mass.EmptyKg > 0 ? d.Mass.EmptyKg : Physics.FlightModel.Analyze(d, meshes).EmptyMassKg;
            return PickJet(stats.LengthM, mass);
        }

        /// <summary>The jet donor closest in length and empty mass (Fighter1 when either is unknown).</summary>
        public static string PickJet(double lengthM, double emptyKg)
        {
            if (!(lengthM > 0) || !(emptyKg > 0) || double.IsInfinity(lengthM) || double.IsInfinity(emptyKg)) return "Fighter1";
            StockPlane? best = null; double bestScore = double.MaxValue;
            foreach (var s in AutoCandidates.Where(c => !c.Prop))
            {
                double score = System.Math.Abs(System.Math.Log(lengthM / s.LengthM)) + System.Math.Abs(System.Math.Log(emptyKg / s.EmptyKg));
                if (score < bestScore) { bestScore = score; best = s; }
            }
            return best?.Key ?? "Fighter1";
        }

        public static bool IsStock(string key) => StockKeys.Contains(key);
    }

    /// <summary>Turns the builder's editable plane into an <see cref="AircraftDesign"/> with explicit panels and meshes.</summary>
    public static class DesignGenerator
    {
        /// <summary>Parts closer to the centre line than this (m) are not mirrored (they would overlap their twin).</summary>
        public const double CentreTolerance = 0.01;

        /// <summary>Fixed shapes (meshes, exact panels) kept from other planes get a twin unless they are exactly centred.</summary>
        public const double FixedShapeTolerance = 1e-4;

        /// <summary>Most triangles one plane may have in all (the same limit the game mod reads up to).</summary>
        public const int MaxTotalTriangles = OplanePackage.Limits.MaxTotalTriangles;

        /// <summary>Palette index by material id for the plane being generated.</summary>
        [System.ThreadStatic] private static Dictionary<string, int>? _palette;
        [System.ThreadStatic] private static List<MaterialDef>? _paletteList;

        /// <summary>The palette index of a material id, or null (the plane's paint).</summary>
        private static int? Mat(string? id) => id != null && _palette != null && _palette.TryGetValue(id, out var i) ? i : (int?)null;

        /// <summary>The palette index of a glass for older "glass" parts (the palette's first, or a default one added).</summary>
        private static int GlassIndex()
        {
            var list = _paletteList!;
            for (int i = 0; i < list.Count; i++) if (list[i].Kind == MaterialDef.Glass) return i;
            list.Add(MaterialDef.DefaultGlass());
            return list.Count - 1;
        }

        public static GeneratedPlane Generate(PlaneSpec spec, IList<MeshData>? sourceMeshes = null)
        {
            var previous = (_palette, _paletteList);
            try { return GenerateInner(spec, sourceMeshes); }
            finally { (_palette, _paletteList) = previous; }
        }

        private static GeneratedPlane GenerateInner(PlaneSpec spec, IList<MeshData>? sourceMeshes)
        {
            sourceMeshes ??= new List<MeshData>();
            var notes = new List<BuilderNote>();
            var meshes = new List<MeshData>();
            var d = new AircraftDesign
            {
                Id = string.IsNullOrWhiteSpace(spec.Id) ? null : spec.Id,
                Name = string.IsNullOrWhiteSpace(spec.Name) ? "Untitled plane" : spec.Name.Trim(),
                Author = spec.Author ?? "",
                Description = spec.Description,
                Origin = spec.Origin ?? new DesignOrigin { Source = "openplane", Builder = string.IsNullOrWhiteSpace(spec.Author) ? null : spec.Author },
                Cockpit = spec.Cockpit != null ? new CockpitDef { Position = Copy(spec.Cockpit.Position), Eye = spec.Cockpit.Eye != null ? Copy(spec.Cockpit.Eye) : null, OwnView = spec.Cockpit.OwnView } : null,
                Sensors = spec.Sensors ?? new SensorSet(),
                Handling = spec.Handling ?? new HandlingDef(),
                Mass = spec.Mass ?? new MassDef(),
                Paint = spec.Paint ?? new PaintDef(),
            };

            // The palette, in the spec's order (converted planes keep their indices).
            _paletteList = (spec.Materials ?? new List<MaterialDef>()).Where(m => m != null).Select(m => m.Clone()).ToList();
            _palette = new Dictionary<string, int>();
            for (int i = 0; i < _paletteList.Count; i++) if (!string.IsNullOrEmpty(_paletteList[i].Id) && !_palette.ContainsKey(_paletteList[i].Id!)) _palette[_paletteList[i].Id!] = i;
            d.Materials = _paletteList;

            var indexByItem = new Dictionary<string, int>();
            int budget = MaxTotalTriangles;
            foreach (var f in spec.Fuselages) AddFuselage(f, sourceMeshes, d, meshes, notes, indexByItem, ref budget);
            foreach (var s in spec.Shapes ?? new List<ShapeSpec>()) AddShape(s, d, meshes, notes, indexByItem, ref budget);
            foreach (var w in spec.Wings) d.Wings.AddRange(BuildWing(w, notes));
            foreach (var e in spec.Engines) foreach (var p in Placements(e.Position, e.Mirror)) d.Engines.Add(MakeEngine(e, p.pos));
            foreach (var g in spec.Gear)
                foreach (var p in Placements(g.Position, g.Mirror))
                    d.Gear.Add(new GearDef
                    {
                        SourcePartId = g.SourcePartId, Position = p.pos, StrutLength = System.Math.Max(0, g.StrutLength),
                        Travel = System.Math.Max(0, g.Travel), WheelRadius = System.Math.Max(0.05, g.WheelRadius),
                        Steerable = g.Steerable, Brakes = g.Brakes, Retractable = g.Retractable,
                    });
            foreach (var h in spec.Hardpoints)
            {
                if (h.Allows.Count == 0)
                    notes.Add(new BuilderNote { Level = "warning", ItemId = h.Id, Message = $"\"{h.Name}\" doesn't allow any weapons, so it will stay empty. Tick at least one weapon type." });
                foreach (var p in Placements(h.Position, h.Mirror))
                    d.Hardpoints.Add(new HardpointDef
                    {
                        SourcePartId = h.SourcePartId, Position = p.pos,
                        Rotation = p.mirrored ? MirrorRotation(h.Rotation) : Copy(h.Rotation, 4),
                        OriginalPayload = h.OriginalPayload, Allows = h.Allows.Distinct().ToList(), Internal = h.Internal,
                    });
            }
            foreach (var g in spec.Guns)
                foreach (var p in Placements(g.Position, g.Mirror))
                {
                    var dir = Copy(g.Direction);
                    if (p.mirrored) dir[0] = -dir[0];
                    d.Guns.Add(new GunDef
                    {
                        SourcePartId = g.SourcePartId, Position = p.pos, Direction = dir, CaliberMm = g.CaliberMm,
                        RoundsPerMinute = g.RoundsPerMinute, MuzzleVelocity = g.MuzzleVelocity, Barrels = System.Math.Max(1, g.Barrels),
                    });
                }

            foreach (var n in spec.Instruments ?? new List<InstrumentDef>())
                foreach (var p in Placements(n.Position, n.Mirror))
                    d.Instruments.Add(new InstrumentDef
                    {
                        Id = n.Id, Name = n.Name, Kind = n.Kind, Reads = n.Reads, Position = p.pos, Rotation = p.mirrored ? MirrorRotation(n.Rotation) : Copy(n.Rotation, 4),
                        Size = n.Size, Min = n.Min, Max = n.Max, MinDeg = n.MinDeg, MaxDeg = n.MaxDeg, SourcePartId = n.SourcePartId,
                    });
            foreach (var l in spec.Lights ?? new List<LightDef>())
                foreach (var p in Placements(l.Position, l.Mirror))
                {
                    var color = Copy(l.Color, l.Color?.Length == 3 ? 3 : 4);
                    // The other side's navigation light: red and green swap.
                    if (p.mirrored && l.Kind == "nav" && color.Length >= 3) { var r = color[0]; color[0] = color[1]; color[1] = r; }
                    d.Lights.Add(new LightDef
                    {
                        Id = l.Id, Name = l.Name, Kind = l.Kind, Position = p.pos, Rotation = p.mirrored ? MirrorRotation(l.Rotation) : Copy(l.Rotation, 4),
                        Color = color, Size = l.Size, SourcePartId = l.SourcePartId,
                    });
                }

            d.Stats = DesignStats.Compute(d, meshes);
            d.Donor = string.IsNullOrWhiteSpace(spec.Donor) || spec.Donor == DonorPicker.Auto ? DonorPicker.Pick(d, meshes) : spec.Donor;
            var result = new GeneratedPlane(d, meshes, notes);
            foreach (var kv in indexByItem) result.MeshIndexByItem[kv.Key] = kv.Value;
            return result;
        }

        // ---------------------------------------------------------------- fuselages

        private static void AddFuselage(FuselageSpec f, IList<MeshData> sourceMeshes, AircraftDesign d, List<MeshData> meshes, List<BuilderNote> notes, Dictionary<string, int> indexByItem, ref int budget)
        {
            if (f.Kind == FuselageSpec.MeshKind)
            {
                if (f.MeshIndex < 0 || f.MeshIndex >= sourceMeshes.Count)
                {
                    notes.Add(new BuilderNote { Level = "warning", ItemId = f.Id, Message = $"The shape of \"{f.Name}\" is missing from this file, so it was left out." });
                    return;
                }
                var src = sourceMeshes[f.MeshIndex];
                if (!Fits(f.Id, f.Name, src.Triangles.Length / 3 * (f.Mirror ? 2 : 1), ref budget, notes)) return;
                double scale = f.Scale > 0 ? f.Scale : 1;
                bool turned = FuselageLoft.IsTurned(f.RotationDeg);
                var offset = Vec3.FromArray(f.Position ?? new double[3]);
                var mesh = turned
                    ? FuselageLoft.Transform(src, FuselageLoft.BoundsCenter(src), scale, FuselageLoft.Rotation(f.RotationDeg), offset, f.Name)
                    : FuselageLoft.Transform(src, FuselageLoft.BoundsCenter(src), scale, offset, f.Name);
                ApplyLook(mesh, f.MaterialId, f.Interior, keepOwn: true);
                // A turned body shows a different face to the air: measure it again.
                double frontal = turned ? FuselageLoft.ProjectedFrontalArea(mesh) : f.SourceFrontalArea * scale * scale;
                if (!string.IsNullOrEmpty(f.Id)) indexByItem[f.Id] = meshes.Count;
                AddBody(f, mesh, frontal, d, meshes);
                if (f.Mirror && !FuselageLoft.IsSelfMirrored(mesh))
                    AddBody(f, FuselageLoft.MirrorX(mesh, f.Name + " (left)"), frontal, d, meshes);
                return;
            }

            if (f.Rings.Count < 2)
            {
                notes.Add(new BuilderNote { Level = "warning", ItemId = f.Id, Message = $"\"{f.Name}\" needs at least two rings to have a shape." });
                return;
            }
            if (!Fits(f.Id, f.Name, EstimateLoftTriangles(f) * (f.Mirror ? 2 : 1), ref budget, notes)) return;
            var loft = FuselageLoft.Build(f);
            ApplyLook(loft, f.MaterialId, f.Interior, keepOwn: false);
            if (!string.IsNullOrEmpty(f.Id)) indexByItem[f.Id] = meshes.Count;
            var body = FuselageLoft.Measure(f, loft, meshes.Count);
            body.SourcePartId = f.SourcePartId;
            meshes.Add(loft);
            d.Fuselages.Add(body);
            if (f.Mirror && LoftNeedsTwin(f, loft))
            {
                var twin = FuselageLoft.MirrorX(loft, f.Name + " (left)");
                var tb = FuselageLoft.MeasureMesh(twin, meshes.Count);
                tb.Name = twin.Name; tb.FrontalArea = body.FrontalArea; tb.SkinThickness = f.SkinThickness;
                meshes.Add(twin);
                d.Fuselages.Add(tb);
            }
        }

        /// <summary>
        /// A mirrored loft gets a twin when it sits off the centre line (as in version 1), or when it is near the centre
        /// line but not its own mirror image (turned, or with a lopsided cross-section). Version 1 lofts on the centre
        /// line are symmetric, so their output is unchanged.
        /// </summary>
        private static bool LoftNeedsTwin(FuselageSpec f, MeshData loft)
        {
            double x = (f.Position ?? new double[3])[0];
            if (System.Math.Abs(x) > CentreTolerance) return true;
            if (x == 0) return !FuselageLoft.IsSelfMirrored(loft);
            // Judge the shape itself, without the tiny offset (which v1 also ignored).
            var centred = FuselageLoft.Transform(loft, new Vec3(0, 0, 0), 1, new Vec3(-x, 0, 0), loft.Name);
            return !FuselageLoft.IsSelfMirrored(centred);
        }

        /// <summary>Roughly how many triangles a loft makes (before it is built, so a huge one is never built).</summary>
        public static long EstimateLoftTriangles(FuselageSpec f)
        {
            long n = FuselageLoft.ClampSides(f.Sides);
            long smooth = System.Math.Max(0, System.Math.Min(FuselageLoft.MaxSmooth, f.Smooth));
            long rings = (long)(f.Rings.Count - 1) * (1L << (int)smooth) + 1;
            return rings * 2 * n + 2 * n;
        }

        /// <summary>Takes <paramref name="triangles"/> from the plane's budget, or leaves the part out with a note.</summary>
        private static bool Fits(string? id, string name, long triangles, ref int budget, List<BuilderNote> notes)
        {
            if (triangles <= budget) { budget -= (int)triangles; return true; }
            notes.Add(new BuilderNote
            {
                Level = "warning", ItemId = id,
                Message = $"\"{name}\" was left out: the plane would have more than {MaxTotalTriangles:N0} triangles, the most the game loads. Use fewer rings or sides, or less smoothing.",
            });
            return false;
        }

        /// <summary>
        /// Gives a body mesh its look: the chosen palette material (for a converted body: on its paint slots only, glass stays
        /// glass), or its own materials, and the interior flag.
        /// </summary>
        private static void ApplyLook(MeshData mesh, string? materialId, bool interior, bool keepOwn)
        {
            if (interior) mesh.Role = MeshData.Interior;
            var chosen = Mat(materialId);
            if (chosen == null) return;
            if (!keepOwn || mesh.TriangleMaterials.Length == 0) { mesh.TriangleMaterials = new int[mesh.TriangleMaterials.Length]; mesh.Materials = new[] { chosen.Value }; return; }
            int slots = mesh.TriangleMaterials.Length == 0 ? 1 : mesh.TriangleMaterials.Max() + 1;
            var table = new int[slots];
            for (int s = 0; s < slots; s++)
            {
                int current = mesh.Materials != null && s < mesh.Materials.Length ? mesh.Materials[s] : (s == 0 ? -1 : GlassIndex());
                bool isPaint = current < 0 || current >= _paletteList!.Count || _paletteList[current].Kind == MaterialDef.Paint;
                table[s] = isPaint ? chosen.Value : current;
            }
            mesh.Materials = table;
        }

        private static void AddBody(FuselageSpec f, MeshData mesh, double frontal, AircraftDesign d, List<MeshData> meshes)
        {
            var body = FuselageLoft.MeasureMesh(mesh, meshes.Count);
            body.Name = mesh.Name;
            body.SourcePartId = f.SourcePartId;
            body.SkinThickness = f.SkinThickness;
            body.FrontalArea = frontal > 0 ? frontal : EstimateFrontal(body);
            meshes.Add(mesh);
            d.Fuselages.Add(body);
        }

        private static double EstimateFrontal(FuselageBody b)
            => System.Math.PI / 4 * (b.BoundsMax[0] - b.BoundsMin[0]) * (b.BoundsMax[1] - b.BoundsMin[1]);

        // ---------------------------------------------------------------- shape parts

        /// <summary>A shape part becomes a fuselage mesh (and its mirrored twin), so the game draws it and it counts as body.</summary>
        private static void AddShape(ShapeSpec s, AircraftDesign d, List<MeshData> meshes, List<BuilderNote> notes, Dictionary<string, int> indexByItem, ref int budget)
        {
            MeshData mesh;
            int open;
            try
            {
                var poly = PolyMesh.FromShape(s);
                int wanted = System.Math.Max(0, System.Math.Min(ShapeSpec.MaxSubdivision, s.Subdivision));
                int levels = ShapeMesh.SubdivisionLevelFor(poly, wanted, out long estimate);
                if (!Fits(s.Id, s.Name, estimate * (s.Mirror ? 2 : 1), ref budget, notes)) return;
                if (levels < wanted)
                    notes.Add(new BuilderNote { Level = "note", ItemId = s.Id, Message = $"\"{s.Name}\" is smoothed {levels} time{(levels == 1 ? "" : "s")} instead of {wanted}, to stay under {ShapeMesh.MaxOutputTriangles:N0} triangles." });
                mesh = ShapeMesh.ToMeshData(s);
                if (Mat(s.MaterialId) is int si) { mesh.TriangleMaterials = new int[mesh.TriangleMaterials.Length]; mesh.Materials = new[] { si }; }
                else if (s.Material == ShapeSpec.Glass) { mesh.TriangleMaterials = new int[mesh.TriangleMaterials.Length]; mesh.Materials = new[] { GlassIndex() }; }
                if (s.Interior) mesh.Role = MeshData.Interior;
                open = ShapeMesh.OpenEdges(poly);
            }
            catch (MeshOpException e)
            {
                notes.Add(new BuilderNote { Level = "warning", ItemId = s.Id, Message = $"\"{s.Name}\" was left out: {e.Message}" });
                return;
            }
            if (mesh.Triangles.Length == 0)
            {
                notes.Add(new BuilderNote { Level = "warning", ItemId = s.Id, Message = $"\"{s.Name}\" has no faces, so it was left out." });
                return;
            }
            if (open > 0)
                notes.Add(new BuilderNote { Level = "note", ItemId = s.Id, Message = $"\"{s.Name}\" has a hole in it. It still counts as body, but closing it gives better drag and weight estimates." });
            if (!string.IsNullOrEmpty(s.Id)) indexByItem[s.Id] = meshes.Count;
            AddShapeBody(s, mesh, d, meshes);
            if (s.Mirror && !FuselageLoft.IsSelfMirrored(mesh))
                AddShapeBody(s, FuselageLoft.MirrorX(mesh, s.Name + " (left)"), d, meshes);
        }

        private static void AddShapeBody(ShapeSpec s, MeshData mesh, AircraftDesign d, List<MeshData> meshes)
        {
            var body = FuselageLoft.MeasureMesh(mesh, meshes.Count);
            body.Name = mesh.Name;
            body.SourcePartId = s.SourcePartId;
            body.FrontalArea = FuselageLoft.ProjectedFrontalArea(mesh);
            meshes.Add(mesh);
            d.Fuselages.Add(body);
        }

        // ---------------------------------------------------------------- wings

        /// <summary>The panels (right side first, then the mirrored left twin) a wing item produces.</summary>
        public static List<WingPanel> BuildWing(WingSpec w, List<BuilderNote>? notes = null)
        {
            notes ??= new List<BuilderNote>();
            var right = w.Kind == WingSpec.PanelsKind ? TransformPanels(w) : new List<WingPanel> { ParametricPanel(w, notes) };
            if (Mat(w.MaterialId) is int wm) foreach (var p in right) p.Material = wm;
            var result = new List<WingPanel>();
            double tol = w.Kind == WingSpec.PanelsKind ? FixedShapeTolerance : CentreTolerance;
            bool twin = w.Mirror && right.SelectMany(Corners).Any(p => System.Math.Abs(p.X) > tol);
            foreach (var p in right)
            {
                if (twin && w.Kind == WingSpec.WingKind) p.Name = w.Name + " (right)";
                result.Add(p);
            }
            if (twin)
                foreach (var p in right) result.Add(MirrorPanel(p, w.Kind == WingSpec.WingKind ? w.Name + " (left)" : p.Name + " (left)"));
            return result;
        }

        private static IEnumerable<Vec3> Corners(WingPanel p)
        {
            yield return Vec3.FromArray(p.RootLeading); yield return Vec3.FromArray(p.RootTrailing);
            yield return Vec3.FromArray(p.TipLeading); yield return Vec3.FromArray(p.TipTrailing);
        }

        private const double Deg = System.Math.PI / 180;

        /// <summary>Leading-edge and trailing-edge lines of a wing item (aircraft space) and its lift direction.</summary>
        public static (Vec3 rootLE, Vec3 rootTE, Vec3 tipLE, Vec3 tipTE, Vec3 up) Planform(WingSpec w)
        {
            double span = System.Math.Max(0.05, w.Span);
            double rc = System.Math.Max(0.05, w.RootChord), tc = System.Math.Max(0.02, w.TipChord);
            double theta = (w.Vertical ? 90 - w.DihedralDeg : w.DihedralDeg) * Deg;
            var spanDir = new Vec3(System.Math.Cos(theta), System.Math.Sin(theta), 0);
            var u0 = new Vec3(-System.Math.Sin(theta), System.Math.Cos(theta), 0);
            var c0 = new Vec3(0, 0, -1);
            double inc = w.IncidenceDeg * Deg;
            var chordDir = c0 * System.Math.Cos(inc) - u0 * System.Math.Sin(inc);
            var up = (u0 * System.Math.Cos(inc) + c0 * System.Math.Sin(inc)).Normalized;
            var rootLE = Vec3.FromArray(w.Root ?? new double[3]);
            var tipLE = rootLE + spanDir * span + new Vec3(0, 0, -span * System.Math.Tan(Clamp(w.SweepDeg, -80, 80) * Deg));
            var tipChordDir = chordDir;
            if (w.TwistDeg != 0)
            {
                double it = (w.IncidenceDeg + Clamp(w.TwistDeg, -30, 30)) * Deg;
                tipChordDir = c0 * System.Math.Cos(it) - u0 * System.Math.Sin(it);
            }
            return (rootLE, rootLE + chordDir * rc, tipLE, tipLE + tipChordDir * tc, up);
        }

        /// <summary>Slats always sit on the leading edge; other surfaces where their Edge says.</summary>
        public static bool IsLeadingSurface(SurfaceSpec s) => s.Type == "slat" || s.Edge == "leading";

        private static List<(SurfaceSpec spec, double s0, double s1, double c)> Strips(WingSpec w, IEnumerable<SurfaceSpec> surfaces, List<BuilderNote> notes)
        {
            // Sorted along the span, overlaps trimmed, unknown types dropped.
            var segs = new List<(SurfaceSpec spec, double s0, double s1, double c)>();
            foreach (var s in surfaces)
            {
                double a = Clamp(System.Math.Min(s.FromPct, s.ToPct), 0, 100) / 100, b = Clamp(System.Math.Max(s.FromPct, s.ToPct), 0, 100) / 100;
                double c = Clamp(s.ChordPct, 5, 60) / 100;
                if (b - a < 0.005) continue;
                segs.Add((s, a, b, c));
            }
            segs = segs.OrderBy(s => s.s0).ToList();
            // Trim each strip against the furthest end reached so far (not just the previous strip, which may be shorter).
            int furthest = 0;
            for (int i = 1; i < segs.Count; i++)
            {
                double end = segs[furthest].s1;
                if (segs[i].s0 < end - 1e-9)
                {
                    notes.Add(new BuilderNote { Level = "note", ItemId = w.Id, Message = segs[i].s1 <= end + 1e-9
                        ? $"Two control surfaces overlap on \"{w.Name}\"; the {Label(segs[i].spec.Type)} lies inside the {Label(segs[furthest].spec.Type)}, so it was left out."
                        : $"Two control surfaces overlap on \"{w.Name}\"; the {Label(segs[i].spec.Type)} was shortened to start where the {Label(segs[furthest].spec.Type)} ends." });
                    segs[i] = (segs[i].spec, end, System.Math.Max(end, segs[i].s1), segs[i].c);
                }
                if (segs[i].s1 > segs[furthest].s1) furthest = i;
            }
            return segs.Where(s => s.s1 - s.s0 >= 0.005).ToList();
        }

        private static WingPanel ParametricPanel(WingSpec w, List<BuilderNote> notes)
        {
            var (rl, rt, tl, tt, up) = Planform(w);
            if (w.Span < 0.05 || w.RootChord < 0.05)
                notes.Add(new BuilderNote { Level = "warning", ItemId = w.Id, Message = $"\"{w.Name}\" is very small; give it a span and chord of at least a few centimetres." });

            Vec3 P(double s, double f) => Vec3.Lerp(Vec3.Lerp(rl, tl, s), Vec3.Lerp(rt, tt, s), f);

            var used = w.Surfaces.Where(s => s != null && s.Type != "none").ToList();
            var segs = Strips(w, used.Where(s => !IsLeadingSurface(s)), notes);
            var lead = Strips(w, used.Where(IsLeadingSurface), notes);

            double cmax = segs.Count > 0 ? segs.Max(s => s.c) : 0;
            double lmax = lead.Count > 0 ? lead.Max(s => s.c) : 0;
            if (lmax + cmax > 0.9)
            {
                // Leading and trailing strips together may take at most 90 % of the chord.
                double f = (0.9 - cmax) / lmax;
                lead = lead.Select(s => (s.spec, s.s0, s.s1, s.c * f)).ToList();
                lmax = 0.9 - cmax;
                notes.Add(new BuilderNote { Level = "note", ItemId = w.Id, Message = $"The leading-edge strips on \"{w.Name}\" were made narrower so the surfaces leave a tenth of the chord fixed." });
            }
            double fp = 1 - cmax;
            var panel = new WingPanel
            {
                Name = w.Name,
                RootLeading = (lmax > 0 ? P(0, lmax) : rl).ToArray(), RootTrailing = P(0, fp).ToArray(),
                TipLeading = (lmax > 0 ? P(1, lmax) : tl).ToArray(), TipTrailing = P(1, fp).ToArray(),
                Up = up.ToArray(),
                TipTwistDeg = w.TwistDeg,
                ThicknessRatio = Clamp(w.ThicknessRatio, 0.02, 0.3),
            };
            double midX = (rl.X + tl.X) / 2;

            SurfaceDef Strip(double s0, double s1, double f0, double f1, string edge = "trailing") => new SurfaceDef
            {
                Role = "fixed", Edge = edge,
                RootLeading = P(s0, f0).ToArray(), RootTrailing = P(s0, f1).ToArray(),
                TipLeading = P(s1, f0).ToArray(), TipTrailing = P(s1, f1).ToArray(),
            };

            double covered = 0;
            foreach (var s in segs)
            {
                if (cmax > 0 && s.s0 > covered + 1e-6) panel.Surfaces.Add(Strip(covered, s.s0, fp, 1));
                if (s.c < cmax - 1e-9) panel.Surfaces.Add(Strip(s.s0, s.s1, fp, 1 - s.c));
                var surf = Strip(s.s0, s.s1, 1 - s.c, 1);
                ApplyRole(surf, s.spec, midX, up);
                panel.Surfaces.Add(surf);
                covered = s.s1;
            }
            if (cmax > 0 && covered < 1 - 1e-6) panel.Surfaces.Add(Strip(covered, 1, fp, 1));

            covered = 0;
            foreach (var s in lead)
            {
                if (s.s0 > covered + 1e-6) panel.Surfaces.Add(Strip(covered, s.s0, 0, lmax, "leading"));
                if (s.c < lmax - 1e-9) panel.Surfaces.Add(Strip(s.s0, s.s1, s.c, lmax, "leading"));
                var surf = Strip(s.s0, s.s1, 0, s.c, "leading");
                ApplyRole(surf, s.spec, midX, up);
                panel.Surfaces.Add(surf);
                covered = s.s1;
            }
            if (lmax > 0 && covered < 1 - 1e-6) panel.Surfaces.Add(Strip(covered, 1, 0, lmax, "leading"));
            return panel;
        }

        private static string Label(string type) => type == "elevon" ? "elevon" : type;

        /// <summary>A surface's role from its type, then the player's travel, servo and input overrides.</summary>
        public static void ApplyRole(SurfaceDef d, SurfaceSpec s, double panelMidX, Vec3 up)
        {
            ApplyRole(d, s.Type, panelMidX, up);
            if (s.MinDeg.HasValue) d.MinDeg = s.MinDeg.Value;
            if (s.MaxDeg.HasValue) d.MaxDeg = s.MaxDeg.Value;
            if (s.ServoDegPerSec.HasValue) d.ServoDegPerSec = System.Math.Max(1, s.ServoDegPerSec.Value);
            if (s.Inputs != null)
                d.Inputs = s.Inputs.Where(i => i != null && !string.IsNullOrWhiteSpace(i.Axis))
                    .Select(i => new InputLink { Axis = i.Axis, Gain = panelMidX < 0 && MirrorsGain(i.Axis) ? -i.Gain : i.Gain }).ToList();
        }

        /// <summary>Sets a surface's role, travel and inputs. Gains follow the convention seen in Flyout files:
        /// the right aileron has a negative roll gain, elevators a positive pitch gain, and a rudder's yaw gain has the
        /// opposite sign to its panel's sideways lift direction.</summary>
        public static void ApplyRole(SurfaceDef s, string type, double panelMidX, Vec3 up)
        {
            double rollSign = panelMidX >= 0 ? -1 : 1;
            double yawSign = up.X > 1e-6 ? -1 : 1;
            s.Inputs.Clear();
            switch (type)
            {
                case "aileron":
                    s.Role = "aileron"; s.MinDeg = -20; s.MaxDeg = 20;
                    s.Inputs.Add(new InputLink { Axis = "roll", Gain = 20 * rollSign });
                    break;
                case "elevator":
                    s.Role = "elevator"; s.MinDeg = -20; s.MaxDeg = 20;
                    s.Inputs.Add(new InputLink { Axis = "pitch", Gain = 20 });
                    break;
                case "rudder":
                    s.Role = "rudder"; s.MinDeg = -20; s.MaxDeg = 20;
                    s.Inputs.Add(new InputLink { Axis = "yaw", Gain = 20 * yawSign });
                    break;
                case "flap":
                    s.Role = "flap"; s.MinDeg = 0; s.MaxDeg = 30;
                    s.Inputs.Add(new InputLink { Axis = "flaps", Gain = 30 });
                    break;
                case "elevon":
                    s.Role = "elevon"; s.MinDeg = -25; s.MaxDeg = 25;
                    s.Inputs.Add(new InputLink { Axis = "pitch", Gain = 20 });
                    s.Inputs.Add(new InputLink { Axis = "roll", Gain = 20 * rollSign });
                    break;
                case "flaperon":
                    s.Role = "flaperon"; s.MinDeg = -20; s.MaxDeg = 30;
                    s.Inputs.Add(new InputLink { Axis = "roll", Gain = 20 * rollSign });
                    s.Inputs.Add(new InputLink { Axis = "flaps", Gain = 30 });
                    break;
                case "ruddervator":
                    s.Role = "ruddervator"; s.MinDeg = -20; s.MaxDeg = 20;
                    s.Inputs.Add(new InputLink { Axis = "pitch", Gain = 20 });
                    s.Inputs.Add(new InputLink { Axis = "yaw", Gain = 20 * yawSign });
                    break;
                case "airbrake":
                    s.Role = "airbrake"; s.MinDeg = 0; s.MaxDeg = 60;
                    s.Inputs.Add(new InputLink { Axis = "brake", Gain = 60 });
                    break;
                case "slat":
                    s.Role = "slat"; s.MinDeg = 0; s.MaxDeg = 25;
                    s.Inputs.Add(new InputLink { Axis = "flaps", Gain = 25 });
                    break;
                default:
                    s.Role = "fixed"; s.MinDeg = 0; s.MaxDeg = 0;
                    break;
            }
            s.ServoDegPerSec = type == "flap" || type == "slat" ? 20 : type == "airbrake" ? 45 : 90;
        }

        private static List<WingPanel> TransformPanels(WingSpec w)
        {
            var pts = w.Panels.SelectMany(Corners).ToList();
            var pivot = pts.Count > 0 ? pts.Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Count : Vec3.Zero;
            double scale = w.Scale > 0 ? w.Scale : 1;
            var offset = Vec3.FromArray(w.Offset ?? new double[3]);
            double[] T(double[] p) => (pivot + (Vec3.FromArray(p) - pivot) * scale + offset).ToArray();
            return w.Panels.Select(p => new WingPanel
            {
                Name = p.Name, SourcePartId = p.SourcePartId,
                RootLeading = T(p.RootLeading), RootTrailing = T(p.RootTrailing), TipLeading = T(p.TipLeading), TipTrailing = T(p.TipTrailing),
                Up = Copy(p.Up), RootTwistDeg = p.RootTwistDeg, TipTwistDeg = p.TipTwistDeg,
                ThicknessRaw = p.ThicknessRaw, ThicknessRatio = p.ThicknessRatio, FuelTank = p.FuelTank, Material = p.Material,
                Inputs = (p.Inputs ?? new List<InputLink>()).Select(i => new InputLink { Axis = i.Axis, Gain = i.Gain }).ToList(),
                MinDeg = p.MinDeg, MaxDeg = p.MaxDeg,
                Surfaces = p.Surfaces.Select(s => new SurfaceDef
                {
                    SourcePartId = s.SourcePartId, Role = s.Role, Edge = s.Edge,
                    RootLeading = T(s.RootLeading), RootTrailing = T(s.RootTrailing), TipLeading = T(s.TipLeading), TipTrailing = T(s.TipTrailing),
                    MinDeg = s.MinDeg, MaxDeg = s.MaxDeg, ServoDegPerSec = s.ServoDegPerSec, Material = s.Material,
                    Inputs = s.Inputs.Select(i => new InputLink { Axis = i.Axis, Gain = i.Gain }).ToList(),
                }).ToList(),
            }).ToList();
        }

        /// <summary>The left twin of a panel: x reflected, lift direction reflected, roll and yaw gains reversed.</summary>
        public static WingPanel MirrorPanel(WingPanel p, string name)
        {
            return new WingPanel
            {
                Name = name, SourcePartId = p.SourcePartId,
                RootLeading = MirrorPoint(p.RootLeading), RootTrailing = MirrorPoint(p.RootTrailing),
                TipLeading = MirrorPoint(p.TipLeading), TipTrailing = MirrorPoint(p.TipTrailing),
                Up = MirrorPoint(p.Up), RootTwistDeg = p.RootTwistDeg, TipTwistDeg = p.TipTwistDeg,
                ThicknessRaw = p.ThicknessRaw, ThicknessRatio = p.ThicknessRatio, FuelTank = p.FuelTank, Material = p.Material,
                Inputs = (p.Inputs ?? new List<InputLink>()).Select(i => new InputLink { Axis = i.Axis, Gain = MirrorsGain(i.Axis) ? -i.Gain : i.Gain }).ToList(),
                MinDeg = p.MinDeg, MaxDeg = p.MaxDeg,
                Surfaces = p.Surfaces.Select(s => new SurfaceDef
                {
                    SourcePartId = s.SourcePartId, Role = s.Role, Edge = s.Edge,
                    RootLeading = MirrorPoint(s.RootLeading), RootTrailing = MirrorPoint(s.RootTrailing),
                    TipLeading = MirrorPoint(s.TipLeading), TipTrailing = MirrorPoint(s.TipTrailing),
                    MinDeg = s.MinDeg, MaxDeg = s.MaxDeg, ServoDegPerSec = s.ServoDegPerSec, Material = s.Material,
                    Inputs = s.Inputs.Select(i => new InputLink { Axis = i.Axis, Gain = MirrorsGain(i.Axis) ? -i.Gain : i.Gain }).ToList(),
                }).ToList(),
            };
        }

        /// <summary>Roll and yaw reverse on the other side of the plane; pitch, flaps and brakes don't.</summary>
        public static bool MirrorsGain(string axis) => axis == "roll" || axis == "yaw";

        // ---------------------------------------------------------------- engines and helpers

        /// <summary>True when an engine drives a propeller (its variant wins over its type).</summary>
        public static bool IsProp(EngineSpec e)
            => e.Variant != null ? e.Variant == "piston" || e.Variant == "turboprop" || e.Variant == "electric" : e.Type == EngineSpec.Prop;

        /// <summary>The design's engine type: the variant's (ramjets become turbojets), else the converted type, else from Type.</summary>
        public static string DesignEngineType(EngineSpec e)
        {
            switch (e.Variant)
            {
                case "turbofan": return "turbofan";
                case "turbojet": case "ramjet": return "turbojet";
                case "piston": return "piston-prop";
                case "turboprop": return "turboprop";
                case "electric": return "electric-prop";
            }
            return e.SourceType ?? (e.Type == EngineSpec.Prop ? "piston-prop" : "turbojet");
        }

        /// <summary>
        /// Dry static thrust (N) of a jet from its size and turbine temperature, fitted to the Jumo 004B (0.74 m, 1050 K,
        /// 8.8 kN) and the AL-31F (1.28 m, 1665 K, 76 kN): 0.1697 A (tt4 - 300)^1.766 with A the face area (m²). The same rule
        /// the Flyout converter uses.
        /// </summary>
        public static double JetThrustN(double diameterM, double turbineTempK)
        {
            double r = System.Math.Max(0, diameterM) / 2;
            return 0.1697 * System.Math.PI * r * r * System.Math.Pow(System.Math.Max(0, turbineTempK - 300), 1.766);
        }

        private static EngineDef MakeEngine(EngineSpec e, double[] pos)
        {
            bool prop = IsProp(e);
            var def = new EngineDef
            {
                SourcePartId = e.SourcePartId,
                Type = DesignEngineType(e),
                Title = string.IsNullOrWhiteSpace(e.Title) ? e.Name : e.Title,
                Position = pos,
                ThrustDirection = new double[] { 0, 0, 1 },
                Estimated = e.Estimated,
            };
            if (prop)
            {
                def.PowerKw = System.Math.Max(0, e.PowerKw);
                def.PropDiameterM = e.PropDiameterM > 0 ? e.PropDiameterM : (double?)null;
                def.MaxThrustN = System.Math.Round(PropThrustN(e.PowerKw, e.PropDiameterM));
            }
            else if (e.Sizing == EngineSpec.SizingDesigned)
            {
                def.DiameterM = System.Math.Max(0, e.DiameterM);
                double dry = JetThrustN(e.DiameterM, e.TurbineTempK);
                def.MaxThrustN = System.Math.Round(dry);
                def.AfterburnerThrustN = e.Afterburner ? System.Math.Round(dry * 1.55) : 0;
            }
            else
            {
                def.DiameterM = System.Math.Max(0, e.DiameterM);
                def.MaxThrustN = System.Math.Max(0, e.ThrustKn) * 1000;
                def.AfterburnerThrustN = e.AfterburnerKn > e.ThrustKn ? e.AfterburnerKn * 1000 : 0;
            }
            return def;
        }

        /// <summary>Static thrust of a propeller from power and diameter (momentum theory, 75 % efficient).
        /// The same formula the Flyout converter uses, so built and converted planes compare fairly.</summary>
        public static double PropThrustN(double powerKw, double diameterM)
        {
            double powerW = System.Math.Max(0, powerKw) * 1000;
            if (diameterM <= 0) return powerW / 40;
            return System.Math.Pow(0.75 * powerW, 2.0 / 3) * System.Math.Pow(2 * FlightModelDensity * System.Math.PI * diameterM * diameterM / 4, 1.0 / 3);
        }

        private const double FlightModelDensity = 1.225;

        private static IEnumerable<(double[] pos, bool mirrored)> Placements(double[] position, bool mirror)
        {
            var p = Copy(position);
            yield return (p, false);
            if (mirror && System.Math.Abs(p[0]) > CentreTolerance) yield return (MirrorPoint(p), true);
        }

        public static double[] MirrorPoint(double[] p) => new[] { -p[0], p[1], p[2] };

        /// <summary>A rotation reflected across x = 0.</summary>
        public static double[] MirrorRotation(double[] q)
        {
            var c = Copy(q, 4);
            return new[] { c[0], -c[1], -c[2], c[3] };
        }

        internal static double[] Copy(double[]? a, int n = 3)
        {
            var r = new double[n];
            if (a != null) Array.Copy(a, r, System.Math.Min(n, a.Length));
            if (n == 4 && (a == null || a.Length < 4)) r[3] = 1;
            return r;
        }

        internal static double Clamp(double v, double lo, double hi) => double.IsNaN(v) ? lo : System.Math.Max(lo, System.Math.Min(hi, v));
    }
}
