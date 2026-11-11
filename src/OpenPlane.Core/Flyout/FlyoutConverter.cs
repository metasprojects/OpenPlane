using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Flyout
{
    public sealed class ConversionResult
    {
        public ConversionResult(AircraftDesign design, List<MeshData> meshes, ImportReport report)
        { Design = design; Meshes = meshes; Report = report; }

        public AircraftDesign Design { get; }
        public List<MeshData> Meshes { get; }
        public ImportReport Report { get; }
    }

    /// <summary>
    /// Turns a Flyout craft into an OpenPlane design. Everything the player shaped (wings, control surfaces, fuselages)
    /// is carried over; Flyout's engines, guns and sensors are mapped to Nuclear Option equivalents; stock Flyout models
    /// (cockpits, decorations) are never copied. Every change is listed in the <see cref="ImportReport"/>.
    /// </summary>
    public static class FlyoutConverter
    {
        public const string ConverterVersion = BuildInfo.Version;

        // Flyout input numbers (InputProcessor "response=<axis>,<gain>"), checked on Satoria-LF-2 and KR-67 Ifrit.
        private static readonly Dictionary<int, string> AxisNames = new Dictionary<int, string>
        {
            [0] = "pitch", [1] = "roll", [2] = "yaw", [3] = "flaps", [4] = "brake", [5] = "throttle", [7] = "gear",
        };

        /// <summary>The control axes a pilot flies with (throttle, gear and unmapped axes are left out).</summary>
        private static readonly HashSet<string> FlightAxes = new HashSet<string> { "pitch", "roll", "yaw", "flaps", "brake" };

        private static readonly string[] DecorationPrefixes =
        {
            "Shapes/", "Primitives/", "Interior/", "LightStrip", "Navlight", "Spot", "Beacon", "StatickWick", "Vent",
            "Antenna", "Battery", "BFCanopyFront", "Gearbox", "DrumMag", "Cockpit",
        };

        /// <summary>Flyout's moving joints: parts hanging on them turn when the joint's inputs move.</summary>
        private static readonly string[] JointKinds = { "HingeJoint", "SwingJoint", "SurfMotor" };

        /// <summary>True for a Flyout helicopter (it has a main rotor). OpenPlane builds fixed-wing planes only.</summary>
        public static bool IsHelicopter(FlyoutCraft craft) => craft.Parts.Any(p => p.Kind == "MainRotor");

        public static ConversionResult Convert(FlyoutCraft craft, string? builder = null)
        {
            var report = new ImportReport();
            var design = new AircraftDesign
            {
                Name = craft.Name,
                Author = builder ?? "",
                Origin = new DesignOrigin
                {
                    Source = "flyout", CraftName = craft.Name, Builder = builder, SourceVersion = craft.SaveVersion,
                    ConvertedWith = "OpenPlane " + ConverterVersion,
                },
            };
            var meshes = new List<MeshData>();
            var handled = new HashSet<int>();
            // Everything hanging under a hardpoint is a payload (missiles, bombs, pods, tanks), never airframe: Flyout missiles
            // are fuselage bodies and cruise missiles carry small jets, so the other converters skip these parts.
            var payload = PayloadParts(craft);

            foreach (var w in craft.Warnings) report.Problem("Flyout file: " + w);
            report.Info($"Read {craft.Parts.Count} parts from Flyout save version {craft.SaveVersion ?? "unknown"}.");

            ConvertWings(craft, design, report, handled, payload);
            ConvertFuselages(craft, design, meshes, report, handled, payload);
            ConvertShapes(craft, design, meshes, report, handled, payload);
            ConvertEngines(craft, design, report, handled, payload);
            ConvertGear(craft, design, report, handled, payload);
            ConvertWeapons(craft, design, report, handled, payload);
            ConvertSensors(craft, design, handled, payload);
            ConvertCockpitAndHandling(craft, design, report, handled, payload);
            ConvertInteriorAndLights(craft, design, meshes, report, handled, payload);
            design.Paint = ReadPaint(craft, payload);
            ReportLeftovers(craft, report, handled);
            CentreOnCentreLine(design, meshes, report);

            design.Donor = Builder.DonorPicker.Pick(design, meshes);
            report.Info($"In the game it is built on the stock plane \"{design.Donor}\": its landing gear, weapons, radar, HUD and sounds come from that plane, moved to fit this one." +
                        (design.Cockpit != null ? " You fly it from its own cockpit." : " It has no cockpit part, so you fly it from the stock plane's cockpit."));
            if (design.Mass.EmptyKg <= 0)
                report.Approximated("Flyout files don't store the plane's weight; OpenPlane estimates it from the parts (wings, body, engines, guns, fuel) and lines its balance point up with the stock plane's in the game.");

            design.Stats = DesignStats.Compute(design, meshes);
            return new ConversionResult(design, meshes, report);
        }

        // ---------------------------------------------------------------- wings

        private static void ConvertWings(FlyoutCraft craft, AircraftDesign design, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            bool thicknessNoted = false;
            int movingPanels = 0;
            foreach (var part in craft.PartsOfKind("ProcWing2").Where(p => !payload.Contains(p.Id)))
            {
                var w = part.Module("ProcWing2");
                if (w == null) { report.Problem($"Wing #{part.Id} has no wing data; skipped."); continue; }
                handled.Add(part.Id);

                Vec3 root = w.GetVec3("root"), tip = w.GetVec3("tip");
                Vec3 rl = root + w.GetVec3("root_l"), rt = root + w.GetVec3("root_t");
                Vec3 tl = tip + w.GetVec3("tip_l"), tt = tip + w.GetVec3("tip_t");

                var panel = new WingPanel
                {
                    Name = part.Title ?? $"Wing {part.Id}",
                    SourcePartId = part.Id,
                    RootLeading = part.ShapePointToWorld(rl).ToArray(),
                    RootTrailing = part.ShapePointToWorld(rt).ToArray(),
                    TipLeading = part.ShapePointToWorld(tl).ToArray(),
                    TipTrailing = part.ShapePointToWorld(tt).ToArray(),
                    Up = part.WorldPose.TransformDirection(Vec3.Up).Normalized.ToArray(),
                    RootTwistDeg = w.GetDouble("root_twist"),
                    TipTwistDeg = w.GetDouble("tip_twist"),
                    ThicknessRaw = w.GetNumbers("thickness"),
                    FuelTank = w.GetBool("fuel_tank"),
                };
                int wingMat = FlyoutDetail.MaterialOf(part, 0, design.Materials);
                if (wingMat >= 0) panel.Material = wingMat;
                // Lift direction from the panel's own corners: builders can tilt a panel (fins, dihedral) by moving its tip
                // point instead of rotating the part. Keep the sign that agrees with the part's "up".
                {
                    var rootMid = Vec3.Lerp(Vec3.FromArray(panel.RootLeading), Vec3.FromArray(panel.RootTrailing), 0.5);
                    var tipMid = Vec3.Lerp(Vec3.FromArray(panel.TipLeading), Vec3.FromArray(panel.TipTrailing), 0.5);
                    var chord = Vec3.FromArray(panel.RootLeading) - Vec3.FromArray(panel.RootTrailing);
                    var normal = Vec3.Cross(chord, tipMid - rootMid).Normalized;
                    var partUp = Vec3.FromArray(panel.Up);
                    if (normal.Length > 0.5)
                        panel.Up = (Vec3.Dot(normal, partUp) < 0 ? -normal : normal).ToArray();
                }
                if (!thicknessNoted)
                {
                    report.Approximated("Wing thickness: drag and stall are worked out for a 10 % thick wing section (Flyout's own thickness numbers are kept in the file).");
                    thicknessNoted = true;
                }

                var wingRoot = Vec3.Lerp(Vec3.FromArray(panel.RootLeading), Vec3.FromArray(panel.RootTrailing), 0.5);
                var wingTip = Vec3.Lerp(Vec3.FromArray(panel.TipLeading), Vec3.FromArray(panel.TipTrailing), 0.5);

                foreach (var child in part.Children)
                {
                    if (child.Kind != "ControlSurf" && child.Kind != "LeadingEdge") continue;
                    var edge = child.Module("WingEdge");
                    if (edge == null) continue;
                    handled.Add(child.Id);

                    Vec3 erl = edge.GetVec3("root_l"), ert = edge.GetVec3("root_t"), etl = edge.GetVec3("tip_l"), ett = edge.GetVec3("tip_t");
                    // The surface's own shape uses its mirror flag. Pick the reading whose root side sits at the wing's root,
                    // so a file with an unexpected flag still comes out right.
                    Vec3 P(Vec3 local, bool flip) => child.WorldPose.TransformPoint(flip ? local.MirrorX : local);
                    bool flip = child.Mirrored;
                    double Fit(bool f) => Vec3.Distance(Vec3.Lerp(P(erl, f), P(ert, f), 0.5), wingRoot)
                                          + Vec3.Distance(Vec3.Lerp(P(etl, f), P(ett, f), 0.5), wingTip);
                    if (Fit(!flip) + 1e-6 < Fit(flip)) flip = !flip;

                    var ip = child.Module("InputProcessor");
                    var inputs = ReadInputs(ip);
                    bool leading = child.Kind == "LeadingEdge";
                    var range = ip?.GetNumbers("range") ?? new double[0];
                    var surface = new SurfaceDef
                    {
                        SourcePartId = child.Id,
                        Edge = leading ? "leading" : "trailing",
                        RootLeading = P(erl, flip).ToArray(),
                        RootTrailing = P(ert, flip).ToArray(),
                        TipLeading = P(etl, flip).ToArray(),
                        TipTrailing = P(ett, flip).ToArray(),
                        MinDeg = range.Length > 0 ? range[0] : 0,
                        MaxDeg = range.Length > 1 ? range[1] : 0,
                        ServoDegPerSec = ip?.GetDouble("speed", 60) ?? 60,
                        Inputs = inputs,
                    };
                    surface.Role = RoleFor(inputs, leading);
                    int surfMat = FlyoutDetail.MaterialOf(child, 0, design.Materials);
                    if (surfMat >= 0) surface.Material = surfMat;
                    // Flyout gives every wing a leading edge and a control surface; unused ones have no depth. Skip those.
                    double rootDepth = Vec3.Distance(Vec3.FromArray(surface.RootLeading), Vec3.FromArray(surface.RootTrailing));
                    double tipDepth = Vec3.Distance(Vec3.FromArray(surface.TipLeading), Vec3.FromArray(surface.TipTrailing));
                    if (rootDepth < 0.01 && tipDepth < 0.01) continue;
                    panel.Surfaces.Add(surface);
                }

                // A panel on a hinge or surface motor that the controls drive turns as a whole (stabilators, turning fins).
                var joint = DrivingJoint(part);
                var jip = joint?.Module("InputProcessor");
                var links = ReadInputs(jip).Where(l => FlightAxes.Contains(l.Axis) && System.Math.Abs(l.Gain) <= 180).ToList();
                if (joint != null && links.Count > 0)
                {
                    panel.Inputs = links;
                    var r = jip!.GetNumbers("range");
                    panel.MinDeg = r.Length > 0 ? r[0] : -System.Math.Abs(links.Max(l => System.Math.Abs(l.Gain)));
                    panel.MaxDeg = r.Length > 1 ? r[1] : System.Math.Abs(links.Max(l => System.Math.Abs(l.Gain)));
                    handled.Add(joint.Id);
                    movingPanels++;
                }

                design.Wings.Add(panel);
            }

            if (movingPanels > 0)
                report.Info(movingPanels == 1 ? "1 wing panel turns as a whole on a hinge (an all-moving tail or fin); it is converted as a control surface." : $"{movingPanels} wing panels turn as a whole on a hinge (all-moving tails or fins); they are converted as control surfaces.");
            if (design.Wings.Count == 0) report.Problem("No wings were found, so this plane can't fly.");
            else report.Info($"Converted {design.Wings.Count} wing panels with {design.Wings.Sum(w => w.Surfaces.Count)} control surfaces and edges.");
        }

        /// <summary>
        /// The hinge or surface motor a wing panel hangs on, looking up through other wing panels and body pieces (a tail made
        /// of several panels on one hinge); null when the panel is fixed to the airframe.
        /// </summary>
        private static FlyoutPart? DrivingJoint(FlyoutPart wing)
        {
            for (var p = wing.Parent; p != null; p = p.Parent)
            {
                if (p.Kind == "HingeJoint" || p.Kind == "SurfMotor") return p;
                if (p.Kind != "ProcWing2" && p.Module("EditableFuselage") == null) return null;
            }
            return null;
        }

        // ---------------------------------------------------------------- shapes

        /// <summary>
        /// Flyout's shape parts (boxes, cylinders, spheres, hemispheres, cones), rebuilt from their sizes with their colours.
        /// One inside the body is an interior piece (a seat, a console): it shows from the cockpit and adds no drag.
        /// </summary>
        private static void ConvertShapes(FlyoutCraft craft, AircraftDesign design, List<MeshData> meshes, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            var hull = meshes.Where(m => m.Role != MeshData.Interior).ToList();
            int outside = 0, inside = 0, tiny = 0;
            foreach (var part in craft.Parts.Where(p => FlyoutDetail.IsShape(p) && !payload.Contains(p.Id)))
            {
                handled.Add(part.Id);
                var mesh = FlyoutDetail.ShapeMesh(part, design.Materials);
                if (mesh == null) { tiny++; continue; }
                var centre = part.WorldPose.Position;
                if (hull.Any(h => FlyoutDetail.Inside(h, centre))) { mesh.Role = MeshData.Interior; inside++; } else outside++;
                var body = Builder.FuselageLoft.MeasureMesh(mesh, meshes.Count);
                body.Name = mesh.Name;
                body.SourcePartId = part.Id;
                body.FrontalArea = Builder.FuselageLoft.ProjectedFrontalArea(mesh);
                meshes.Add(mesh);
                design.Fuselages.Add(body);
            }
            if (outside + inside > 0)
                report.Info($"{Count(outside + inside, "shape part")} rebuilt with {(outside + inside == 1 ? "its" : "their")} colours: {outside} on the outside, {inside} inside the body (seen from the cockpit, no drag).");
            if (tiny > 0) report.Dropped($"{Count(tiny, "shape part")} smaller than 2 cm (or made of cut-out material) {(tiny == 1 ? "wasn't" : "weren't")} drawn.");
        }

        // ---------------------------------------------------------------- cockpit interior and lights

        private static void ConvertInteriorAndLights(FlyoutCraft craft, AircraftDesign design, List<MeshData> meshes, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            int gauges = 0;
            foreach (var part in craft.Parts.Where(p => p.Kind.StartsWith("Interior/", StringComparison.Ordinal) && !payload.Contains(p.Id)))
            {
                var n = FlyoutDetail.Instrument(part);
                if (n == null) continue;
                handled.Add(part.Id);
                design.Instruments.Add(n);
                if (n.Kind == "gauge") gauges++;
            }
            if (design.Instruments.Count > 0)
                report.Approximated($"{Count(design.Instruments.Count, "cockpit instrument")} placed where Flyout had them, as OpenPlane's own working instruments (Flyout's instrument models aren't copied)." +
                                    (gauges > 0 ? $" What {(gauges == 1 ? "the gauge shows" : $"each of the {gauges} gauges shows")} is guessed from its Flyout settings: check {(gauges == 1 ? "it" : "them")} in the builder." : ""));

            foreach (var part in craft.Parts.Where(p => !payload.Contains(p.Id) && p.Module("NavLight") != null))
            {
                var l = FlyoutDetail.Light(part);
                if (l == null) continue;
                handled.Add(part.Id);
                design.Lights.Add(l);
            }
            if (design.Lights.Count > 0)
                report.Info($"{Count(design.Lights.Count, "light")} carried over ({string.Join(", ", design.Lights.GroupBy(l => l.Kind).Select(g => $"{g.Count()} {g.Key}"))}).");

            var cockpit = craft.PartsOfKind("Cockpit").FirstOrDefault(p => !payload.Contains(p.Id));
            if (cockpit != null && design.Cockpit != null)
            {
                var eyeWorld = cockpit.WorldPose.TransformPoint(FlyoutDetail.EyeInCockpit(cockpit, design.Instruments));
                foreach (var m in FlyoutDetail.CockpitStandIns(cockpit, design.Materials, design.Instruments))
                {
                    var body = Builder.FuselageLoft.MeasureMesh(m, meshes.Count);
                    body.Name = m.Name;
                    meshes.Add(m);
                    design.Fuselages.Add(body);
                }
                design.Cockpit.Eye = FlyoutDetail.SeeOver(eyeWorld, cockpit.WorldPose, meshes).ToArray();
                report.Approximated("The cockpit seat and panel are OpenPlane's own simple ones (Flyout's cockpit model isn't copied). In the game you fly from this plane's cockpit; move the eye in the builder if the view sits too high or low.");
            }
        }

        private static List<InputLink> ReadInputs(FlyoutBlock? ip)
        {
            var list = new List<InputLink>();
            if (ip == null) return list;
            foreach (var r in ip.GetAll("response"))
            {
                var n = FlyoutValues.Numbers(r);
                if (n.Length < 2 || System.Math.Abs(n[1]) < 1e-9) continue;
                int axis = (int)n[0];
                list.Add(new InputLink { Axis = AxisNames.TryGetValue(axis, out var name) ? name : "axis" + axis, Gain = n[1] });
            }
            return list;
        }

        private static string RoleFor(List<InputLink> inputs, bool leading)
        {
            if (inputs.Count == 0) return "fixed";
            var axes = new HashSet<string>(inputs.Select(i => i.Axis).Where(FlightAxes.Contains));
            if (axes.Count == 0) return leading ? "slat" : "other";
            if (leading && axes.SetEquals(new[] { "flaps" })) return "slat";
            if (axes.SetEquals(new[] { "yaw", "brake" })) return "airbrake";   // split rudders
            if (axes.SetEquals(new[] { "pitch" })) return "elevator";
            if (axes.SetEquals(new[] { "roll" })) return "aileron";
            if (axes.SetEquals(new[] { "yaw" })) return "rudder";
            if (axes.SetEquals(new[] { "flaps" })) return "flap";
            if (axes.SetEquals(new[] { "brake" })) return "airbrake";
            if (axes.SetEquals(new[] { "pitch", "roll" })) return "elevon";
            if (axes.SetEquals(new[] { "pitch", "yaw" })) return "ruddervator";
            if (axes.SetEquals(new[] { "roll", "flaps" })) return "flaperon";
            return "other";
        }

        // ---------------------------------------------------------------- fuselages

        private static void ConvertFuselages(FlyoutCraft craft, AircraftDesign design, List<MeshData> meshes, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            int holes = 0;
            try { ConvertFuselagesInner(craft, design, meshes, report, handled, payload, ref holes); }
            finally
            {
                if (holes > 0) report.Info($"{Count(holes, "cut-out face")} (Flyout's \"Hole\" material) {(holes == 1 ? "is" : "are")} left open, as in Flyout.");
                int glass = design.Materials.Count(m => m.Kind == MaterialDef.Glass);
                if (design.Materials.Count > 0)
                    report.Info($"Colours and finishes carried over: {Count(design.Materials.Count(m => m.Kind == MaterialDef.Paint), "paint")}" + (glass > 0 ? $" and {glass} glass" : "") +
                                (design.Materials.Any(m => m.Finish != null) ? $"; textured finishes ({string.Join(", ", design.Materials.Where(m => m.Finish != null).Select(m => m.Finish).Distinct())}) are drawn as plain colour for now" : "") + ".");
            }
        }

        private static void ConvertFuselagesInner(FlyoutCraft craft, AircraftDesign design, List<MeshData> meshes, ImportReport report, HashSet<int> handled, HashSet<int> payload, ref int holes)
        {
            foreach (var part in craft.Parts)
            {
                if (payload.Contains(part.Id)) continue;
                var ef = part.Module("EditableFuselage");
                if (ef == null) continue;
                handled.Add(part.Id);

                int rings = ef.GetInt("segments"), points = ef.GetInt("slices");
                var vertBlock = ef.Child("vert");
                var local = new List<Vec3>();
                if (vertBlock != null)
                    foreach (var v in vertBlock.GetAll("v"))
                        if (FlyoutValues.TryVec3(v, out var p)) local.Add(p);

                if (rings < 2 || points < 2 || (long)rings * points != local.Count)
                {
                    report.Problem($"Fuselage #{part.Id}: expected {rings} x {points} points but found {local.Count}; skipped.");
                    continue;
                }

                var ringBlocks = ef.ChildrenNamed("segment").ToList();
                int mirrorVotes = ringBlocks.Count(r => r.GetBool("xmirror"));
                bool xmirror = ringBlocks.Count > 0 && mirrorVotes * 2 > ringBlocks.Count;
                var ringCenters = new List<Vec3>();
                for (int r = 0; r < rings; r++)
                    ringCenters.Add(r < ringBlocks.Count ? ringBlocks[r].GetVec3("offset") : Centroid(local, r * points, points));

                var mesh = new MeshData { Name = part.Title ?? $"Fuselage {part.Id}" };
                var verts = new List<float>();
                var tris = new List<int>();
                var mats = new List<int>();
                AddFuselageHalf(part, local, ringCenters, ringBlocks, rings, points, mirrorHalf: false, verts, tris, mats);
                if (xmirror)
                    AddFuselageHalf(part, local, ringCenters, ringBlocks, rings, points, mirrorHalf: true, verts, tris, mats);
                // Each face's material slot through the part's own materials into the plane's palette; cut-outs ("Hole")
                // aren't drawn, so canopy openings and intakes really are open.
                int slots = mats.Count == 0 ? 1 : mats.Max() + 1;
                var table = new int[slots];
                for (int sl = 0; sl < slots; sl++) table[sl] = FlyoutDetail.MaterialOf(part, sl, design.Materials);
                var keptTris = new List<int>(); var keptMats = new List<int>();
                for (int ti = 0; ti < mats.Count; ti++)
                {
                    if (table[mats[ti]] == FlyoutDetail.Hole) { holes++; continue; }
                    keptTris.Add(tris[ti * 3]); keptTris.Add(tris[ti * 3 + 1]); keptTris.Add(tris[ti * 3 + 2]);
                    keptMats.Add(mats[ti]);
                }
                for (int sl = 0; sl < slots; sl++) if (table[sl] == FlyoutDetail.Hole) table[sl] = -1;
                mesh.Vertices = verts.ToArray();
                mesh.Triangles = keptTris.ToArray();
                mesh.TriangleMaterials = keptMats.ToArray();
                mesh.Materials = table;

                var body = new FuselageBody
                {
                    Name = mesh.Name,
                    SourcePartId = part.Id,
                    MeshIndex = meshes.Count,
                    SkinThickness = ef.GetDouble("thickness"),
                };
                MeasureBody(mesh, local, part, rings, points, xmirror, body);
                meshes.Add(mesh);
                design.Fuselages.Add(body);
            }

            if (design.Fuselages.Count > 0)
                report.Info($"Converted {design.Fuselages.Count} fuselage pieces from their exact shape ({meshes.Sum(m => m.Triangles.Length / 3):N0} triangles).");
            int glassTris = meshes.Sum(m => m.TriangleMaterials.Count(x => design.Materials.Count > 0 && DesignMaterials.ForSlot(design, m, x).Kind == MaterialDef.Glass));
            if (glassTris > 0)
                report.Info($"Glass panels carried over ({glassTris:N0} triangles): see-through in the game, in their Flyout tint.");
        }

        private static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n:N0} {thing}s";

        private static Vec3 Centroid(List<Vec3> pts, int start, int count)
        {
            var sum = Vec3.Zero;
            for (int i = 0; i < count; i++) sum += pts[start + i];
            return sum / count;
        }

        private static void AddFuselageHalf(FlyoutPart part, List<Vec3> local, List<Vec3> ringCenters, List<FlyoutBlock> ringBlocks,
            int rings, int points, bool mirrorHalf, List<float> verts, List<int> tris, List<int> mats)
        {
            int baseIndex = verts.Count / 3;
            Vec3 ToWorld(Vec3 p) => part.ShapePointToWorld(mirrorHalf ? p.MirrorX : p);

            var world = new Vec3[local.Count];
            for (int i = 0; i < local.Count; i++)
            {
                world[i] = ToWorld(local[i]);
                verts.Add((float)world[i].X); verts.Add((float)world[i].Y); verts.Add((float)world[i].Z);
            }

            // Decide winding so normals point away from each ring's centre line.
            double outward = 0;
            for (int r = 0; r < rings - 1; r++)
            {
                var c = ToWorld(Vec3.Lerp(ringCenters[r], ringCenters[r + 1], 0.5));
                for (int s = 0; s < points - 1; s++)
                {
                    int a = r * points + s, b = a + 1, d = a + points, e = d + 1;
                    var n = Vec3.Cross(world[b] - world[a], world[e] - world[a]);
                    var mid = (world[a] + world[b] + world[d] + world[e]) / 4;
                    outward += Vec3.Dot(n, mid - c);
                }
            }
            bool flip = outward < 0;

            for (int r = 0; r < rings - 1; r++)
            {
                var sub = r < ringBlocks.Count ? FlyoutValues.Numbers(ringBlocks[r].Get("submesh")) : new double[0];
                for (int s = 0; s < points - 1; s++)
                {
                    int a = baseIndex + r * points + s, b = a + 1, d = a + points, e = d + 1;
                    int mat = s < sub.Length ? (int)sub[s] : 0;
                    if (!flip) { tris.Add(a); tris.Add(b); tris.Add(e); tris.Add(a); tris.Add(e); tris.Add(d); }
                    else { tris.Add(a); tris.Add(e); tris.Add(b); tris.Add(a); tris.Add(d); tris.Add(e); }
                    mats.Add(mat); mats.Add(mat);
                }
            }
        }

        private static void MeasureBody(MeshData mesh, List<Vec3> local, FlyoutPart part, int rings, int points, bool xmirror, FuselageBody body)
        {
            var v = mesh.Vertices;
            var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < v.Length; i += 3)
            {
                var p = new Vec3(v[i], v[i + 1], v[i + 2]);
                min = Vec3.Min(min, p); max = Vec3.Max(max, p);
            }
            body.BoundsMin = min.ToArray(); body.BoundsMax = max.ToArray();

            double wetted = 0;
            var t = mesh.Triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                var a = new Vec3(v[t[i] * 3], v[t[i] * 3 + 1], v[t[i] * 3 + 2]);
                var b = new Vec3(v[t[i + 1] * 3], v[t[i + 1] * 3 + 1], v[t[i + 1] * 3 + 2]);
                var c = new Vec3(v[t[i + 2] * 3], v[t[i + 2] * 3 + 1], v[t[i + 2] * 3 + 2]);
                wetted += Vec3.Cross(b - a, c - a).Length / 2;
            }
            body.WettedArea = wetted;

            // Frontal area: the largest ring, seen along the aircraft's nose axis (shoelace on x/y).
            double best = 0;
            for (int r = 0; r < rings; r++)
            {
                var ring = new List<Vec3>();
                for (int s = 0; s < points; s++) ring.Add(part.ShapePointToWorld(local[r * points + s]));
                if (xmirror)
                {
                    var other = new List<Vec3>();
                    for (int s = points - 1; s >= 0; s--) other.Add(part.ShapePointToWorld(local[r * points + s].MirrorX));
                    ring.AddRange(other);
                }
                double area = 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    var p = ring[i]; var q = ring[(i + 1) % ring.Count];
                    area += p.X * q.Y - q.X * p.Y;
                }
                best = System.Math.Max(best, System.Math.Abs(area) / 2);
            }
            body.FrontalArea = best;
        }

        // ---------------------------------------------------------------- engines

        private const double SeaLevelDensity = 1.225;

        private static void ConvertEngines(FlyoutCraft craft, AircraftDesign design, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            foreach (var part in craft.Parts)
            {
                if (payload.Contains(part.Id)) continue;
                var turbine = part.Module("STurbine");
                if (turbine == null) continue;
                handled.Add(part.Id);
                double radius = turbine.GetDouble("radius");
                double bpr = turbine.GetDouble("bpr");
                double tt4 = turbine.GetDouble("tt4", 1400);
                bool afterburner = turbine.Children.Any(c => c.Has("tt7"));
                double area = System.Math.PI * radius * radius;
                // Static thrust from engine size and turbine temperature, fitted to two real engines:
                // Jumo 004B (0.74 m, 1050 K) = 8.8 kN and AL-31F (1.28 m, 1665 K) = 76 kN dry. Afterburner adds ~55 %.
                // Above the fitted range the gain is halved (Flyout's 2300 K would otherwise give ~117 kN for a 1.14 m core).
                double tt4Used = tt4 <= 1700 ? tt4 : 1700 + 0.5 * (tt4 - 1700);
                double dry = 0.1697 * area * System.Math.Pow(System.Math.Max(0, tt4Used - 300), 1.766);
                // A separate fan (Flyout's "fan" block) moves much more air: ~40 kN per m² of fan ring at a 1.6 pressure ratio.
                var fan = turbine.Child("fan");
                double fanRadius = fan?.GetDouble("radius") ?? 0, fpr = fan?.GetDouble("fpr", 1.6) ?? 1.6;
                if (fanRadius > radius && fpr > 1)
                    dry += 40000 * System.Math.PI * (fanRadius * fanRadius - radius * radius) * System.Math.Sqrt((fpr - 1) / 0.6);
                if (dry < 1500)
                {
                    var title = !string.IsNullOrWhiteSpace(turbine.Get("title")) ? turbine.Get("title") : !string.IsNullOrWhiteSpace(part.Title) ? part.Title : null;
                    report.Dropped(title != null ? $"A small turbine \"{title}\" (likely a starter or power unit) was left out." : "A small turbine (likely a starter or power unit) was left out.");
                    continue;
                }
                design.Engines.Add(new EngineDef
                {
                    SourcePartId = part.Id,
                    Type = bpr >= 0.2 || fanRadius > radius ? "turbofan" : "turbojet",
                    Title = turbine.Get("title") ?? part.Title,
                    Position = part.WorldPose.Position.ToArray(),
                    ThrustDirection = part.WorldPose.TransformDirection(Vec3.Forward).Normalized.ToArray(),
                    DiameterM = System.Math.Max(radius, fanRadius) * 2,
                    MaxThrustN = System.Math.Round(dry),
                    AfterburnerThrustN = afterburner ? System.Math.Round(dry * 1.55) : 0,
                });
            }

            var props = craft.PartsOfKind("PProp").Where(p => !payload.Contains(p.Id)).ToList();
            foreach (var part in craft.PartsOfKind("ProceduralEngine").Where(p => !payload.Contains(p.Id)))
            {
                handled.Add(part.Id);
                var m = part.Module("ProceduralEngine");
                if (m == null) continue;
                int cylinders = System.Math.Max(1, m.GetInt("cyl_count", 1)) * System.Math.Max(1, m.GetInt("banks", 1));
                double bore = m.GetDouble("bore"), stroke = m.GetDouble("stroke"), rpm = m.GetDouble("rpm_lim", 2500);
                double displacement = cylinders * System.Math.PI / 4 * bore * bore * stroke;     // m^3
                double powerW = 1.0e6 * displacement * rpm / 120;                                  // 4-stroke, ~10 bar BMEP

                var prop = FindProp(craft, part, props);
                double propDiameter = prop?.Module("ProceduralProp")?.GetDouble("scale", 0) ?? 0;
                if (prop != null) handled.Add(prop.Id);
                double thrust = propDiameter > 0
                    ? System.Math.Pow(0.75 * powerW, 2.0 / 3) * System.Math.Pow(2 * SeaLevelDensity * System.Math.PI * propDiameter * propDiameter / 4, 1.0 / 3)
                    : powerW / 40; // no propeller found: ~25 N per kW

                var pushDir = (prop ?? part).WorldPose.TransformDirection(Vec3.Forward).Normalized;
                bool pusher = prop?.Module("ProceduralProp")?.GetBool("pusher") ?? false;
                design.Engines.Add(new EngineDef
                {
                    SourcePartId = part.Id,
                    Type = "piston-prop",
                    Title = part.Title,
                    Position = (prop ?? part).WorldPose.Position.ToArray(),
                    ThrustDirection = (pusher ? -pushDir : pushDir).ToArray(),
                    PowerKw = System.Math.Round(powerW / 1000),
                    PropDiameterM = propDiameter > 0 ? propDiameter : (double?)null,
                    MaxThrustN = System.Math.Round(thrust),
                });
            }

            foreach (var part in craft.Parts.Where(p => p.Kind == "MainRotor" || p.Kind == "TailRotor"))
                handled.Add(part.Id);
            if (craft.Parts.Any(p => p.Kind == "MainRotor"))
                report.Problem("This craft is a helicopter. OpenPlane builds fixed-wing planes only for now, so its rotors were left out.");

            if (design.Engines.Count == 0 && !craft.Parts.Any(p => p.Kind == "MainRotor"))
                report.Problem("No engines were found.");
            else if (design.Engines.Count > 0)
                report.Approximated($"Engine thrust is estimated from Flyout's engine data ({string.Join(", ", design.Engines.Select(e => $"{e.Type} ~{e.MaxThrustN / 1000:0.#} kN" + (e.AfterburnerThrustN > 0 ? $" / {e.AfterburnerThrustN / 1000:0.#} kN with afterburner" : "")))}). You can adjust it in the app.");
        }

        private static FlyoutPart? FindProp(FlyoutCraft craft, FlyoutPart engine, List<FlyoutPart> props)
        {
            var byId = craft.Parts.ToDictionary(p => p.Id);
            foreach (var prop in props)
            {
                // prop -(OutputShaft.input)-> gearbox -(Gear.input = shaft, partId)-> engine
                int id = prop.Module("OutputShaft")?.GetInt("input", -1) ?? -1;
                for (int hops = 0; hops < 4 && id >= 0 && byId.TryGetValue(id, out var link); hops++)
                {
                    if (link.Id == engine.Id) return prop;
                    var gear = link.Module("Gear");
                    var nums = gear != null ? FlyoutValues.Numbers(gear.Get("input")) : new double[0];
                    id = nums.Length > 0 ? (int)nums[nums.Length - 1] : -1;
                }
            }
            // Fall back to the nearest propeller.
            return props.OrderBy(p => Vec3.Distance(p.WorldPose.Position, engine.WorldPose.Position)).FirstOrDefault();
        }

        // ---------------------------------------------------------------- landing gear

        private static void ConvertGear(FlyoutCraft craft, AircraftDesign design, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            foreach (var part in craft.PartsOfKind("RaycastLG").Where(p => !payload.Contains(p.Id)))
            {
                handled.Add(part.Id);
                var g = part.Module("RaycastLG");
                var axes = part.Modules("InputProcessor").SelectMany(ReadInputs).Select(i => i.Axis).ToList();
                design.Gear.Add(new GearDef
                {
                    SourcePartId = part.Id,
                    Position = part.WorldPose.Position.ToArray(),
                    StrutLength = g?.GetDouble("height") ?? 0,
                    Travel = g?.GetDouble("travel") ?? 0,
                    WheelRadius = g?.GetDouble("wheel_rad") ?? 0,
                    Brakes = (g?.GetDouble("brake") ?? 0) > 0,
                    Steerable = axes.Contains("yaw"),
                    Retractable = axes.Contains("gear"),
                });
            }
            foreach (var part in craft.PartsOfKind("BasicWheel").Where(p => !payload.Contains(p.Id)))
            {
                handled.Add(part.Id);
                design.Gear.Add(new GearDef { SourcePartId = part.Id, Position = part.WorldPose.Position.ToArray(), WheelRadius = 0.2, Retractable = false });
            }
            if (design.Gear.Count == 0) report.Problem("No landing gear was found; the plane will need an air start.");
            else report.Approximated($"Landing gear: {Count(design.Gear.Count, "wheel")} found and saved with the plane. In the game the stock plane's gear legs stand under them (moved sideways, the nose leg also along the plane); the wheels keep the stock plane's size and height.");
        }

        // ---------------------------------------------------------------- weapons

        private static void ConvertWeapons(FlyoutCraft craft, AircraftDesign design, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            // A hardpoint hanging under another (a rack's own stations) is part of that station's payload, not a station.
            foreach (var hp in craft.PartsOfKind("Hardpoint").Where(h => !payload.Contains(h.Id)))
            {
                handled.Add(hp.Id);
                var carried = Descendants(hp).ToList();
                foreach (var p in carried) handled.Add(p.Id);
                string kind =
                    carried.Any(p => p.Module("ProcMissile") != null || (p.Module("ProcRocketMotor") != null && p.Module("Warhead") != null)) ? "missile" :
                    carried.Any(p => p.Kind == "Weapons/Rocket") ? "rockets" :
                    carried.Any(p => p.Module("Warhead") != null) ? "bomb" :
                    carried.Any(p => p.Module("PGun") != null) ? "gun-pod" :
                    carried.Any(p => p.Module("FuelTank") != null) ? "tank" :
                    carried.Count > 0 ? "other" : "empty";
                design.Hardpoints.Add(new HardpointDef
                {
                    SourcePartId = hp.Id,
                    Position = hp.WorldPose.Position.ToArray(),
                    Rotation = hp.WorldPose.Rotation.ToArray(),
                    OriginalPayload = kind,
                    Allows = AllowsFor(kind),
                });
            }

            foreach (var gun in craft.PartsOfKind("ProcGun"))
            {
                if (handled.Contains(gun.Id)) continue; // gun pods under hardpoints were counted above
                handled.Add(gun.Id);
                var g = gun.Module("PGun");
                design.Guns.Add(new GunDef
                {
                    SourcePartId = gun.Id,
                    Position = gun.WorldPose.Position.ToArray(),
                    Direction = gun.WorldPose.TransformDirection(Vec3.Forward).Normalized.ToArray(),
                    CaliberMm = g?.GetDouble("caliber") ?? 0,
                    RoundsPerMinute = g?.GetDouble("rpm") ?? 0,
                    MuzzleVelocity = g?.GetDouble("muzzle_vel") ?? 0,
                    Barrels = g?.GetInt("barrel_count", 1) ?? 1,
                });
            }

            // Loose weapon parts (not under a hardpoint) are Flyout's own procedural weapons; they don't carry over.
            int loose = craft.Parts.Count(p => !handled.Contains(p.Id) && (p.Kind.StartsWith("Weapons/", StringComparison.Ordinal) && p.Kind != "Weapons/NoseRadar" && p.Kind != "Weapons/FLIR" && p.Kind != "Weapons/Jammer"));
            foreach (var p in craft.Parts.Where(p => !handled.Contains(p.Id) && p.Kind.StartsWith("Weapons/", StringComparison.Ordinal) && p.Kind != "Weapons/NoseRadar" && p.Kind != "Weapons/FLIR" && p.Kind != "Weapons/Jammer"))
                handled.Add(p.Id);

            if (design.Hardpoints.Count > 0)
                report.Approximated($"{Count(design.Hardpoints.Count, "hardpoint")} found and saved with the plane. In the game the stock plane's weapon stations move onto them and carry Nuclear Option's own weapons, up to what this plane can lift (Flyout's custom missiles and bombs don't carry over).");
            if (design.Guns.Count > 0)
                report.Approximated($"{Count(design.Guns.Count, "gun")} found ({string.Join(", ", design.Guns.Select(g => $"{g.CaliberMm:0.#} mm").Distinct())}); their weight counts in the balance. In the game the stock plane's guns move to where they are and fire Nuclear Option's own ammunition.");
            if (loose > 0)
                report.Dropped($"{Count(loose, "loose weapon part")} that {(loose == 1 ? "isn't" : "aren't")} on a hardpoint {(loose == 1 ? "was" : "were")} left out.");
        }

        private static List<string> AllowsFor(string payload)
        {
            switch (payload)
            {
                case "missile": return new List<string> { "missile-ir", "missile-radar", "missile-ground" };
                case "rockets": return new List<string> { "rockets" };
                case "bomb": return new List<string> { "bomb" };
                case "tank": return new List<string> { "tank" };
                case "gun-pod": return new List<string> { "gun-pod" };
                default: return new List<string> { "missile-ir", "missile-radar", "missile-ground", "bomb", "rockets" };
            }
        }

        private static IEnumerable<FlyoutPart> Descendants(FlyoutPart part)
        {
            var stack = new Stack<FlyoutPart>(part.Children);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                yield return p;
                foreach (var c in p.Children) stack.Push(c);
            }
        }

        /// <summary>Ids of every part under any hardpoint (what it carries, including racks and their own stations).</summary>
        public static HashSet<int> PayloadParts(FlyoutCraft craft)
        {
            var set = new HashSet<int>();
            foreach (var hp in craft.PartsOfKind("Hardpoint"))
                foreach (var p in Descendants(hp)) set.Add(p.Id);
            return set;
        }

        // ---------------------------------------------------------------- sensors, cockpit, handling, paint

        private static void ConvertSensors(FlyoutCraft craft, AircraftDesign design, HashSet<int> handled, HashSet<int> payload)
        {
            foreach (var p in craft.Parts.Where(p => !payload.Contains(p.Id)))
            {
                bool sensor = false;
                if (p.Kind == "Weapons/NoseRadar" || p.Module("Radar") != null) { design.Sensors.Radar = true; sensor = true; }
                if (p.Kind == "IRSTPod" || p.Module("Irst") != null) { design.Sensors.Irst = true; sensor = true; }
                if (p.Kind == "Weapons/FLIR" || p.Module("TargetingPod") != null) { design.Sensors.TargetingPod = true; sensor = true; }
                if (p.Kind == "RWR" || p.Module("RwrMaw") != null) { design.Sensors.RadarWarning = true; sensor = true; }
                if (p.Kind == "Flare" || p.Kind.IndexOf("Flare", StringComparison.OrdinalIgnoreCase) >= 0 || p.Kind.IndexOf("Chaff", StringComparison.OrdinalIgnoreCase) >= 0) { design.Sensors.Countermeasures = true; sensor = true; }
                if (p.Kind == "Weapons/Jammer" || p.Module("Jammer") != null) { design.Sensors.Jammer = true; sensor = true; }
                if (sensor) handled.Add(p.Id);
            }
        }

        private static void ConvertCockpitAndHandling(FlyoutCraft craft, AircraftDesign design, ImportReport report, HashSet<int> handled, HashSet<int> payload)
        {
            var cockpit = craft.PartsOfKind("Cockpit").FirstOrDefault(p => !payload.Contains(p.Id));
            if (cockpit != null)
            {
                handled.Add(cockpit.Id);
                design.Cockpit = new CockpitDef { Position = cockpit.WorldPose.Position.ToArray() };
            }
            else report.Problem("No cockpit was found.");

            var law = craft.TopBlocks("ControlLaw").FirstOrDefault(b => b.Get("name") == "Default") ?? craft.TopBlocks("ControlLaw").FirstOrDefault();
            if (law != null)
            {
                var g = law.GetNumbers("g_pos_limit");
                var a = law.GetNumbers("aoa_pos_limit");
                design.Handling.GLimit = g.Length > 0 ? g.Max() : 0;
                design.Handling.AoaLimitDeg = a.Length > 0 ? a.Max() : 0;
                design.Handling.FlyoutGain = law.GetNumbers("gain");
                design.Handling.FlyoutRateDamp = law.GetNumbers("rate_damp");
                if (design.Handling.GLimit > 0 || design.Handling.AoaLimitDeg > 0)
                    report.Info($"Fly-by-wire limits taken from Flyout: g limit {design.Handling.GLimit:0.#}, angle-of-attack limit {design.Handling.AoaLimitDeg:0.#} deg.");
            }
        }

        private static PaintDef ReadPaint(FlyoutCraft craft, HashSet<int> payload)
        {
            // The most-used colour on the fuselage parts becomes the base colour.
            var counts = new Dictionary<string, int>();
            foreach (var p in craft.Parts.Where(p => !payload.Contains(p.Id) && p.Module("EditableFuselage") != null))
                foreach (var pm in p.Modules("PartMaterials"))
                    foreach (var mat in pm.ChildrenNamed("Material"))
                    {
                        var c = mat.Get("color");
                        if (c == null) continue;
                        counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
                    }
            var paint = new PaintDef();
            if (counts.Count == 0) return paint;
            var best = counts.OrderByDescending(kv => kv.Value).First().Key;
            var rgba = FlyoutValues.Numbers(best);
            if (rgba.Length >= 3) paint.BaseColor = new[] { rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1 };
            return paint;
        }

        // ---------------------------------------------------------------- centre line

        /// <summary>
        /// Twin-body designs are often built around one body, so the plane's centre line isn't at x = 0. Nuclear Option puts the
        /// centre line at x = 0 (the stock plane's), so the whole design moves sideways when its wings balance more than 2 % of
        /// the span away from it.
        /// </summary>
        private static void CentreOnCentreLine(AircraftDesign d, List<MeshData> meshes, ImportReport report)
        {
            double area = 0, moment = 0, minX = double.MaxValue, maxX = double.MinValue;
            foreach (var w in d.Wings)
            {
                if (System.Math.Abs(w.Up[1]) < System.Math.Abs(w.Up[0])) continue;   // fins don't say where the middle is
                double a = DesignStats.QuadArea(w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading);
                double x = (w.RootLeading[0] + w.RootTrailing[0] + w.TipTrailing[0] + w.TipLeading[0]) / 4;
                area += a; moment += a * x;
                foreach (var p in new[] { w.RootLeading, w.RootTrailing, w.TipLeading, w.TipTrailing }) { minX = System.Math.Min(minX, p[0]); maxX = System.Math.Max(maxX, p[0]); }
            }
            if (area <= 0) return;
            double x0 = moment / area, span = maxX - minX;
            if (!(span > 0) || System.Math.Abs(x0) <= 0.02 * span) return;
            // Only when the wings are symmetric about x0 (a twin-body plane), not a lopsided design.
            double mid = (minX + maxX) / 2;
            if (System.Math.Abs(mid - x0) > 0.05 * span) return;
            void S(double[]? p) { if (p != null && p.Length >= 1) p[0] -= x0; }
            foreach (var w in d.Wings)
            {
                S(w.RootLeading); S(w.RootTrailing); S(w.TipLeading); S(w.TipTrailing);
                foreach (var sf in w.Surfaces) { S(sf.RootLeading); S(sf.RootTrailing); S(sf.TipLeading); S(sf.TipTrailing); }
            }
            foreach (var b in d.Fuselages) { S(b.BoundsMin); S(b.BoundsMax); }
            foreach (var m in meshes) for (int i = 0; i + 2 < m.Vertices.Length; i += 3) m.Vertices[i] -= (float)x0;
            foreach (var e in d.Engines) S(e.Position);
            foreach (var g in d.Gear) S(g.Position);
            foreach (var h in d.Hardpoints) S(h.Position);
            foreach (var g in d.Guns) S(g.Position);
            if (d.Cockpit != null) { S(d.Cockpit.Position); S(d.Cockpit.Eye); }
            // The cockpit's instruments and the lights move with it (left behind, they floated between the bodies and the
            // pilot's eye looked out from mid-air).
            foreach (var i in d.Instruments) S(i.Position);
            foreach (var l in d.Lights) S(l.Position);
            S(d.Mass.CenterOfMass);
            report.Info($"The plane's centre line was {System.Math.Abs(x0):0.0} m to the {(x0 > 0 ? "right" : "left")} of the cockpit (a twin-body design); everything was moved so the middle of the plane is on the centre line.");
        }

        // ---------------------------------------------------------------- leftovers and donor

        private static void ReportLeftovers(FlyoutCraft craft, ImportReport report, HashSet<int> handled)
        {
            var decorations = 0;
            int fixedJoints = 0;
            var movingJoints = new List<string>();
            var modParts = new Dictionary<string, int>();
            var other = new Dictionary<string, int>();
            foreach (var p in craft.Parts)
            {
                if (handled.Contains(p.Id)) continue;
                if (p.ResourcePath.StartsWith("*", StringComparison.Ordinal) || p.ResourcePath.Contains("\\"))
                {
                    var name = p.ResourcePath.TrimStart('*').Split('\\')[0];
                    modParts[name] = modParts.TryGetValue(name, out var n) ? n + 1 : 1;
                }
                else if (JointKinds.Contains(p.Kind))
                {
                    var axes = p.Modules("InputProcessor").SelectMany(ReadInputs).Select(i => i.Axis).Where(FlightAxes.Contains).Distinct().ToList();
                    if (axes.Count == 0) fixedJoints++;
                    else movingJoints.Add($"#{p.Id} ({string.Join("/", axes)})");
                }
                else if (DecorationPrefixes.Any(d => p.Kind.StartsWith(d, StringComparison.Ordinal)))
                    decorations++;
                else if (p.Kind == "Prefabs/Wing/Lerx")
                    other["leading-edge root extensions (LERX)"] = other.TryGetValue("leading-edge root extensions (LERX)", out var n2) ? n2 + 1 : 1;
                else
                    other[p.Kind] = other.TryGetValue(p.Kind, out var n3) ? n3 + 1 : 1;
            }
            if (decorations > 0)
                report.Dropped($"{Count(decorations, "small detail part")} (static wicks, vents, antennas, canopy frames, batteries, magazines) {(decorations == 1 ? "was" : "were")} left out.");
            if (fixedJoints > 0)
                report.Info(fixedJoints == 1 ? "1 joint that nothing moves in flight is kept as a solid connection." : $"{fixedJoints} joints that nothing moves in flight are kept as solid connections.");
            if (movingJoints.Count > 0)
                report.Approximated($"{Count(movingJoints.Count, "moving joint")} driven by the controls ({string.Join(", ", movingJoints.Take(6))}{(movingJoints.Count > 6 ? ", ..." : "")}) don't move in the game: what hangs on them (swing wings, folding fins, hinged flaps) stays where it was saved. Check those parts in the builder.");
            foreach (var kv in modParts)
                report.Dropped($"{Count(kv.Value, "part")} from the Flyout mod \"{kv.Key}\" {(kv.Value == 1 ? "was" : "were")} left out (OpenPlane can't read mod parts).");
            foreach (var kv in other)
                report.Dropped($"{kv.Value} x {kv.Key}: this kind of Flyout part isn't converted, so it was left out.");
        }
    }
}
