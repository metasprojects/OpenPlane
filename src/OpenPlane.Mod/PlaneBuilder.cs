using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.SavedMission;
using OpenPlane.Core.Design;
using OpenPlane.Core.Physics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Makes a copy of a stock airframe fly and look like an OpenPlane design.
    /// The stock plane keeps its proven internals (joints, damage model, cockpit, gear, weapons, systems); OpenPlane
    /// hides its outer skin, draws the design's own shape, and retunes every lifting part so the plane's lift, control
    /// authority, thrust, weight and balance follow the design.
    /// </summary>
    internal static class PlaneBuilder
    {
        /// <summary>Largest centerOfLift offset (m) on a stock control surface; design control surfaces are held to it.</summary>
        private const float MaxControlLiftOffset = 1.5f;

        private static Mesh? _empty;
        private static Mesh Empty => _empty != null ? _empty : (_empty = new Mesh { name = "openplane-hidden" });

        /// <summary>The report of the plane being built (player-facing notes for the app), while <see cref="Apply"/> runs.</summary>
        private static PlaneReport? _report;
        private static void Change(string text) => _report?.Changes.Add(text);
        private static void Warn(string text) => _report?.Warnings.Add(text);
        private static string _donorName = "stock plane";
        /// <summary>The skin step's test of the stock pieces against the design's shape, run once the weapons and gear are fitted.</summary>
        private static Action? _stockPiecesPass;

        public static void Apply(AircraftDefinition def, AircraftDefinition donor, AircraftDesign design, List<MeshData> meshes, PlaneReport? report = null)
        {
            _report = report;
            _donorName = string.IsNullOrEmpty(donor.unitName) ? donor.jsonKey : donor.unitName;
            if (report != null) report.BuiltOn = _donorName;
            try { ApplyInner(def, donor, design, meshes); }
            finally { _report = null; }
        }

        private static void ApplyInner(AircraftDefinition def, AircraftDefinition donor, AircraftDesign design, List<MeshData> meshes)
        {
            var credit = design.Origin?.Source == "flyout"
                ? $"Converted from Flyout with OpenPlane. Original craft \"{design.Origin.CraftName}\"" +
                  (string.IsNullOrEmpty(design.Origin.Builder) ? "." : $" by {design.Origin.Builder}.")
                : "Built with OpenPlane" + (string.IsNullOrEmpty(design.Author) ? "." : $" by {design.Author}.");
            def.description = (string.IsNullOrEmpty(design.Description) ? "" : design.Description + "\n\n") + credit;

            // Never trust stats stored in the file ("stats": {} would say length 0): work them out from the shape.
            var stats = DesignStats.Compute(design, meshes);
            if (Positive(stats.SpanM) && Positive(stats.LengthM))
            {
                def.length = (float)stats.LengthM;
                def.width = (float)stats.SpanM;
                if (Positive(stats.HeightM)) def.height = (float)stats.HeightM;
            }

            var flight = FlightModel.Analyze(design, meshes);
            Facts[def] = new PlaneFacts { ThrustToWeight = Positive(flight.ThrustToWeight) ? (float)flight.ThrustToWeight : 0f };
            var prefab = def.unitPrefab;
            var root = prefab.transform;
            var aircraft = prefab.GetComponent<Aircraft>();
            var notes = new List<string>();

            // The stock centre of gravity, measured before any step changes the copy.
            var donorCg = DonorCg(prefab);
            // The centre of gravity each plane really flies with: its structure plus fuel (half tanks), the copy's fuel scaled
            // to the design's. Fuel scales differently from the structure, so it moves the balance point (the AB-4 flew 7 times
            // stiffer in pitch than balanced for without it).
            float massRatio = donor.mass > 0 && Positive(flight.EmptyMassKg) ? Mathf.Clamp((float)(flight.EmptyMassKg / donor.mass), 0.1f, 10f) : 1f;
            var stockLoadedCg = LoadedCg(prefab, donorCg, 1f, 1f);
            var loadedCg = LoadedCg(prefab, donorCg, massRatio, FuelScale(prefab, flight));
            notes.Add($"loaded centre of gravity z {loadedCg.z:0.00} (stock {stockLoadedCg.z:0.00})");
            // The stock lifting parts as they are, for the pitch balance check after the lift is retuned.
            StockAeroSet? stockAero = null;
            try { stockAero = SnapshotAero(prefab, stockLoadedCg); }
            catch (Exception e) { notes.Add("pitch balance skipped: " + e.Message); }
            var offset = Offset(root, aircraft, design, flight, donorCg, notes);
            Vector3 ToRoot(double[] p) => new Vector3((float)p[0], (float)p[1], (float)p[2]) + offset;

            _stockPiecesPass = null;
            Step("skin", notes, () => HideSkinAndDraw(prefab, aircraft, design, meshes, offset, notes));
            Step("aerodynamics", notes, () => RetuneAero(prefab, design, stats, donor, ToRoot, donorCg, notes));
            if (stockAero != null)
            {
                Step("pitch balance", notes, () => BalancePitch(prefab, flight, stats, loadedCg, stockAero, notes));
                Step("flaps", notes, () => ScaleHighLift(prefab, stockAero, notes));
            }
            Step("drag", notes, () => ScaleDrag(prefab, donor, stats, notes));
            Step("engines", notes, () => ScaleEngines(prefab, design, notes));
            Step("mass", notes, () => SetMass(def, donor, prefab, flight, notes));
            Step("fuel", notes, () => ScaleFuel(prefab, flight, notes));
            Step("weapons", notes, () => FitWeapons(def, donor, prefab, design, flight, ToRoot, notes));
            if (stockAero != null) Step("joints", notes, () => StrengthenJoints(prefab, donor, stockAero, flight, notes));
            Step("landing gear", notes, () => FitGear(prefab, design, ToRoot, loadedCg, notes));
            // After the weapons and gear steps: it needs to know which weapon stations stay open and where they went.
            Step("stock pieces", notes, () => { var pass = _stockPiecesPass; _stockPiecesPass = null; pass?.Invoke(); });
            Step("handling", notes, () => TuneParameters(def, donor, design, flight, notes));

            Plugin.Log.LogInfo($"  {design.Name}: span {stats.SpanM:0.0} m, length {stats.LengthM:0.0} m, empty {flight.EmptyMassKg:0} kg, " +
                               $"stability {flight.Stability} ({flight.StaticMarginPercent:0}%), T/W {flight.ThrustToWeight:0.00}. " + string.Join("; ", notes));
        }

        private static bool Positive(double v) => v > 0 && !double.IsInfinity(v);   // false for NaN too
        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private static void Step(string name, List<string> notes, Action action)
        {
            try { action(); }
            catch (Exception e) { notes.Add($"{name} FAILED: {e.Message}"); Plugin.Log.LogWarning($"PlaneBuilder {name}: {e}"); }
        }

        /// <summary>
        /// Where design coordinates sit in the stock prefab: the design's centre of gravity is placed on the stock plane's.
        /// Scaling every part's mass by the same ratio leaves the real centre of gravity where the stock plane has it, so the
        /// design's lift, drawn shape and balance must be lined up with that point (lining up the cockpits instead put the
        /// lift metres ahead of the real centre of gravity on planes with a short nose-to-CG distance). The cockpit then
        /// ends up wherever the design has it relative to its centre of gravity; that distance is logged.
        /// </summary>
        private static Vector3 Offset(Transform root, Aircraft? aircraft, AircraftDesign design, FlightReport flight, Vector3 donorCg, List<string> notes)
        {
            var designCg = new Vector3((float)flight.CenterOfMass[0], (float)flight.CenterOfMass[1], (float)flight.CenterOfMass[2]);
            if (!Finite(designCg) || !Finite(donorCg)) { notes.Add("centre of gravity unknown; design placed at the stock origin"); return Vector3.zero; }
            var o = donorCg - designCg;
            o.x = 0;
            var text = $"centre of gravity z: design {designCg.z:0.00}, stock {donorCg.z:0.00}";
            if (design.Cockpit != null && aircraft != null && aircraft.cockpit != null)
            {
                var stockCockpit = root.InverseTransformPoint(aircraft.cockpit.transform.position);
                var designCockpit = new Vector3((float)design.Cockpit.Position[0], (float)design.Cockpit.Position[1], (float)design.Cockpit.Position[2]) + o;
                var moved = new Vector2(designCockpit.y - stockCockpit.y, designCockpit.z - stockCockpit.z).magnitude;
                text += $"; the design's cockpit is {moved:0.00} m from the stock cockpit";
                // Planes fly from their own cockpit (HideSkinAndDraw); only one that turns that off sits in the stock cockpit.
                if (moved > 1.5f && design.Cockpit.OwnView == false)
                    Change($"In the cockpit you sit in the {_donorName}'s cockpit, {moved:0.0} m from where your design's cockpit is (\"Fly from this cockpit\" is off in the builder).");
            }
            notes.Add(text);
            return o;
        }

        /// <summary>
        /// The stock plane's centre of gravity in root space: every part's mass (inactive ones too) at its
        /// <see cref="PartCentre"/>.
        /// </summary>
        /// <summary>How ScaleFuel will scale the stock tanks: the design's fuel over the stock plane's.</summary>
        private static float FuelScale(GameObject prefab, FlightReport flight)
        {
            double stock = prefab.GetComponentsInChildren<FuelTank>(true).Sum(t => (double)Mathf.Max(0, t.fuelCapacity));
            return stock > 0 && Positive(flight.FuelMassKg) ? (float)(flight.FuelMassKg / stock) : 1f;
        }

        /// <summary>
        /// Structure (every part's mass times <paramref name="massRatio"/>, centred on <paramref name="structureCg"/>) plus fuel at
        /// half tanks, each tank's fuel (times <paramref name="fuelScale"/>) at its part's centre, where the game adds it.
        /// </summary>
        private static Vector3 LoadedCg(GameObject prefab, Vector3 structureCg, float massRatio, float fuelScale)
        {
            var root = prefab.transform;
            double structure = prefab.GetComponentsInChildren<UnitPart>(true).Sum(p => (double)Mathf.Max(0, p.mass)) * massRatio;
            var sum = structureCg * (float)structure;
            double total = structure;
            foreach (var t in prefab.GetComponentsInChildren<FuelTank>(true))
            {
                var part = t.GetComponent<UnitPart>() ?? t.GetComponentInParent<UnitPart>();
                if (part == null) continue;
                double m = Mathf.Max(0, t.fuelCapacity) * fuelScale * 0.5;
                if (!(m > 0)) continue;
                sum += PartCentre(root, part) * (float)m;
                total += m;
            }
            var cg = total > 0 ? sum / (float)total : structureCg;
            return Finite(cg) ? cg : structureCg;
        }

        private static Vector3 DonorCg(GameObject prefab)
        {
            var root = prefab.transform;
            double total = 0; var sum = Vector3.zero;
            foreach (var part in prefab.GetComponentsInChildren<UnitPart>(true))
            {
                if (!(part.mass > 0)) continue;
                sum += PartCentre(root, part) * part.mass;
                total += part.mass;
            }
            return total > 0 ? sum / (float)total : Vector3.zero;
        }

        // ------------------------------------------------------------------ visuals

        private static bool IsSkin(Renderer r)
            => r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name.IndexOf("AircraftSkin", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void HideSkinAndDraw(GameObject prefab, Aircraft? aircraft, AircraftDesign design, List<MeshData> meshes, Vector3 offset, List<string> notes)
        {
            // Build the design's meshes first: the stock skin is only hidden once there is something to show instead.
            var (bodyG, interiorG, wingsG) = DesignVisuals.Build(design, meshes);
            Mesh? body = bodyG.Mesh, wings = wingsG.Mesh, inside = interiorG.Mesh;
            int tris = new[] { body, wings, inside }.Where(m => m != null).Sum(m => m!.triangles.Length / 3);
            Material? skin = null;
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && IsSkin(r)) { skin = r.sharedMaterial; break; }
            if (tris == 0 || skin == null)
            {
                notes.Add(tris == 0 ? "the design has no shape to draw, so the stock skin was kept" : "no stock skin material found, so the stock skin was kept");
                foreach (var m in new[] { body, wings, inside }) if (m != null) Object.Destroy(m);
                return;
            }

            // The design's own colours, glass and lights, unless it asks for the stock livery (or has no colours of its own).
            var mats = new DesignVisuals.MaterialFactory(prefab, skin);
            bool stockLivery = design.Paint?.StockLivery == true || design.Materials == null || design.Materials.Count == 0 || !mats.CanPaint;
            // Fly from the design's own cockpit when it has one (and doesn't turn that off).
            bool ownView = design.Cockpit != null && design.Cockpit.OwnView != false && aircraft != null;
            var visual = new GameObject("OpenPlane Visual");
            visual.transform.SetParent(prefab.transform, false);
            visual.transform.localPosition = offset;
            MeshRenderer Renderer(string name, Mesh mesh, Material[] materials, bool shadows = true)
            {
                var go = new GameObject(name);
                go.transform.SetParent(visual.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = materials;
                mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                return mr;
            }
            List<MeshRenderer> Add(DesignVisuals.Group g, string name, bool twoSided)
            {
                var made = new List<MeshRenderer>();
                if (g.Mesh == null) return made;
                var paint = Enumerable.Range(0, g.Keys.Count).Where(i => DesignVisuals.IsPaint(design, g.Keys[i])).ToList();
                var other = Enumerable.Range(0, g.Keys.Count).Where(i => !paint.Contains(i)).ToList();
                if (!stockLivery || paint.Count == 0)
                    made.Add(Renderer(name, g.Mesh, g.Keys.Select(k => mats.For(design, k, stockLivery, twoSided)).ToArray()));
                else if (other.Count == 0)
                    made.Add(Renderer(name, g.Mesh, g.Keys.Select(_ => skin).ToArray()));
                else
                {
                    // The stock livery: the painted skin on a renderer of its own (the game's livery and damage shading go
                    // through it), glass and lights on another so they stay see-through and lit.
                    made.Add(Renderer(name, DesignVisuals.Subset(g.Mesh, paint, g.Mesh.name + " paint"), paint.Select(_ => skin).ToArray()));
                    made.Add(Renderer(name + " glass", DesignVisuals.Subset(g.Mesh, other, g.Mesh.name + " glass"), other.Select(i => mats.For(design, g.Keys[i], true, false)).ToArray(), false));
                }
                return made;
            }
            var bodyRenderers = Add(bodyG, "OpenPlane Body", false);
            var wingRenderers = Add(wingsG, "OpenPlane Wings", false);
            // Control surfaces and all-moving panels on hinges of their own, so they can turn (linked after the lift step).
            _movingVisuals.Clear();
            foreach (var piece in DesignVisuals.LastMoving)
            {
                if (piece.Group.Mesh == null) continue;
                var mid = (piece.HingeA + piece.HingeB) / 2;
                var pivot = new GameObject("OpenPlane Surface " + piece.Key).transform;
                pivot.SetParent(visual.transform, false);
                pivot.localPosition = mid;
                var shifted = DesignVisuals.Shifted(piece.Group.Mesh, -mid);
                var g = new DesignVisuals.Group { Mesh = shifted };
                g.Keys.AddRange(piece.Group.Keys);
                foreach (var r in Add(g, "surface " + piece.Key, false))
                {
                    r.transform.SetParent(pivot, false);
                    wingRenderers.Add(r);
                }
                _movingVisuals[piece.Key] = (pivot, piece.HingeB - piece.HingeA);
            }
            var interiorRenderers = Add(interiorG, "OpenPlane Interior", !stockLivery);
            // From the design's own cockpit the inside of the hull shows (as in Flyout), in a dark cockpit grey.
            if (ownView && bodyG.Mesh != null && mats.CanPaint)
            {
                var painted = Enumerable.Range(0, bodyG.Keys.Count).Where(i => DesignVisuals.IsPaint(design, bodyG.Keys[i])).ToList();
                if (painted.Count > 0)
                    interiorRenderers.Add(Renderer("OpenPlane Hull Inside", DesignVisuals.Inside(DesignVisuals.Subset(bodyG.Mesh, painted, "hull"), bodyG.Mesh.name + " inside"),
                                                   new[] { mats.Solid(new Color(0.22f, 0.23f, 0.24f), 0.1f, 0.3f) }, false));
            }
            int instruments = 0, lamps = 0;
            if (mats.CanPaint)
            {
                try { instruments = DesignVisuals.AddInstruments(visual.transform, design, mats); }
                catch (Exception e) { notes.Add("instruments FAILED: " + e.Message); }
                try { lamps = DesignVisuals.AddLights(visual.transform, design, mats); }
                catch (Exception e) { notes.Add("lights FAILED: " + e.Message); }
            }
            if (!stockLivery) Change("It is drawn in its own colours, glass and finishes (the stock livery isn't used; tick \"Use the stock livery\" in the builder to use it).");
            if (instruments > 0) Change($"{instruments} cockpit instruments work: gauges, horizon and compass read the plane's real speed, height, engine and attitude.");
            if (lamps > 0) Change($"{lamps} lights are lit; strobes and beacons blink.");
            notes.Add($"drawn with {(stockLivery ? "the stock livery" : $"{(design.Materials?.Count ?? 0)} materials")}, {instruments} instruments, {lamps} lights");

            int hidden = 0;
            var ours = bodyRenderers.Concat(wingRenderers).Concat(interiorRenderers).Cast<Renderer>().ToList();
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (ours.Contains(r) || !IsSkin(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) { mf.sharedMesh = Empty; hidden++; }
            }
            foreach (var r in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (IsSkin(r)) { r.sharedMesh = Empty; hidden++; }

            // Liveries and damage shading go through the root part's damage-material renderers (UnitPart.SetLivery);
            // the cockpit view hides Aircraft.exteriorRenderers. Register the design's renderers with both.
            var rootPart = prefab.GetComponent<UnitPart>() ?? (aircraft != null ? aircraft.cockpit : null) ?? prefab.GetComponentInChildren<UnitPart>(true);
            if (rootPart != null)
            {
                if (rootPart.damageMaterial == null) rootPart.damageMaterial = new DamageMaterial { indices = new byte[0] };
                if (rootPart.damageMaterial.renderers == null) rootPart.damageMaterial.renderers = new List<Renderer>();
                foreach (var r in ours)
                    if (r.sharedMaterials.All(m => m == skin) && !rootPart.damageMaterial.renderers.Contains(r)) rootPart.damageMaterial.renderers.Add(r);
            }
            // Level-of-detail groups: the stock plane's low-detail meshes (levels 1 and up, a different shader) would show the
            // stock silhouette from a distance, and the design's renderers in no group would never be culled. The low-detail
            // stock meshes are emptied and the design's renderers join every level.
            int lodHidden = 0;
            foreach (var lg in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                var level0 = new HashSet<Renderer>((lods[0].renderers ?? new Renderer[0]).Where(r => r != null));
                for (int i = 0; i < lods.Length; i++)
                {
                    var keep = new List<Renderer>();
                    foreach (var r in lods[i].renderers ?? new Renderer[0])
                    {
                        if (r == null) continue;
                        if (i > 0 && !level0.Contains(r) && !ours.Contains(r))
                        {
                            if (r is SkinnedMeshRenderer smr) smr.sharedMesh = Empty;
                            else if (r.GetComponent<MeshFilter>() is MeshFilter mf) mf.sharedMesh = Empty;
                            lodHidden++;
                            continue;
                        }
                        keep.Add(r);
                    }
                    foreach (var r in ours) if (!keep.Contains(r)) keep.Add(r);
                    lods[i].renderers = keep.ToArray();
                }
                lg.SetLODs(lods);
            }
            if (lodHidden > 0) notes.Add($"{lodHidden} stock low-detail meshes hidden");

            // Stock pieces that aren't skin (canopy glass, cockpit frames, gun barrels, exhausts) sit where the stock plane has
            // them, so on a design of another shape they show outside it. Each is tested against the design's shape seen from
            // above (DesignShape): a piece inside it can't be seen and stays; one that shows outside is hidden, except the
            // cockpit's own pieces when the design has no cockpit view of its own (those are shown only from the cockpit,
            // where the pilot needs them). Landing gear, weapon stations and the guns the design places keep working and stay
            // visible; the stock propeller (the design draws its own) and the gear once stowed are hidden in flight by
            // StockPieces, since the game switches those renderers itself.
            var shape = DesignShape.From(new[] { body, wings, inside }.Concat(DesignVisuals.LastMoving.Select(p => p.Group.Mesh)), offset);
            if (shape != null) _stockPiecesPass = () =>
            {
                var root = prefab.transform;
                var keep = new List<Transform>();
                var keepRenderers = new HashSet<Renderer>();
                foreach (var g in prefab.GetComponentsInChildren<LandingGear>(true)) keep.Add(LegRoot(root, g.transform));
                foreach (var p in prefab.GetComponentsInChildren<ConstantSpeedProp>(true)) keep.Add(p.transform);
                foreach (var p in prefab.GetComponentsInChildren<PropFan>(true)) keep.Add(p.transform);
                bool designGuns = design.Guns != null && design.Guns.Count > 0;
                var wm = prefab.GetComponentInChildren<WeaponManager>(true);
                if (wm?.hardpointSets != null)
                    foreach (var set in wm.hardpointSets)
                    {
                        if (set?.hardpoints == null) continue;
                        bool gun = IsGunSet(set);
                        // Kept: stations still offered (moved onto the design's hardpoints) and built-in guns moved to the
                        // design's guns. A closed station (its gun mount or pylon, like the CI-22's wing-root guns) and the
                        // built-in guns of a design without guns are tested like any other piece (built-in guns still fire).
                        bool open = set.weaponOptions != null && set.weaponOptions.Any(m => m != null);
                        if (gun ? !designGuns : !open) continue;
                        foreach (var hp in set.hardpoints)
                        {
                            if (hp?.transform == null) continue;
                            keep.Add(hp.transform);
                            foreach (var r in PylonRenderers(hp)) keepRenderers.Add(r);
                            foreach (var w in hp.BuiltInWeapons ?? new Weapon[0]) if (w != null) keep.Add(w.transform);
                            foreach (var t in hp.BuiltInTurrets ?? new Turret[0]) if (t != null) keep.Add(t.transform);
                        }
                    }
                var cockpitPart = aircraft != null ? aircraft.cockpit : null;
                var cockpitList = (aircraft?.cockpitRenderers ?? new Renderer[0]).Where(r => r != null).ToList();
                int cockpitOnly = 0, hiddenOutside = 0;
                foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (ours.Contains(r) || IsSkin(r) || cockpitList.Contains(r) || keepRenderers.Contains(r)) continue;
                    if (r.transform.IsChildOf(visual.transform)) continue;
                    if (keep.Any(k => r.transform == k || r.transform.IsChildOf(k))) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                    if (mesh == null || mesh == Empty) continue;
                    bool underCockpit = cockpitPart != null && r.transform.IsChildOf(cockpitPart.transform);
                    if (ownView && underCockpit)
                    {
                        // Flying from the design's cockpit: the stock canopy, frames and pilot would sit in the way.
                        if (r is SkinnedMeshRenderer s0) s0.sharedMesh = Empty; else if (mf != null) mf.sharedMesh = Empty;
                        hiddenOutside++;
                        continue;
                    }
                    if (shape.OutsideShare(mesh, root.worldToLocalMatrix * r.transform.localToWorldMatrix) <= 0.04f) continue;
                    if (underCockpit)
                    {
                        // The game turns cockpit renderers on only in the cockpit view.
                        cockpitList.Add(r);
                        r.enabled = false;
                        cockpitOnly++;
                    }
                    else
                    {
                        if (r is SkinnedMeshRenderer s2) s2.sharedMesh = Empty; else if (mf != null) mf.sharedMesh = Empty;
                        hiddenOutside++;
                    }
                }
                if (aircraft != null && cockpitOnly > 0 && !ownView) aircraft.cockpitRenderers = cockpitList.ToArray();
                if (cockpitOnly + hiddenOutside > 0)
                {
                    notes.Add($"stock pieces outside the design: {cockpitOnly} shown only from the cockpit, {hiddenOutside} hidden");
                    Change($"Pieces of the {_donorName} that would stick out of your plane's shape (canopy, cockpit frame, gun barrels, exhausts) are hidden; the canopy still shows from inside the cockpit.");
                }
            };

            // The design's propellers, turning with the engine; the stock propeller and the stowed gear hide in flight.
            int props = mats.CanPaint ? DesignVisuals.AddPropellers(visual.transform, design, mats) : 0;
            if (props > 0) Change($"{props} propellers are drawn where you placed them and turn with the engine (the {_donorName}'s own propeller is hidden).");
            int jets = mats.CanPaint ? DesignVisuals.AddJets(visual.transform, design, mats) : 0;
            if (jets > 0) notes.Add($"{jets} jet engines drawn");
            var pieces = visual.AddComponent<StockPieces>();
            pieces.HideProps = props > 0 || !design.Engines.Any(DesignVisuals.IsProp);
            notes.Add($"{props} propellers drawn" + (pieces.HideProps ? ", the stock propeller hidden" : "") + ", stowed gear hidden in flight");

            if (aircraft != null && ownView && design.Cockpit != null)
            {
                // The view moves to the design's eye; the stock cockpit interior never shows (the design's own does, always).
                var eye = design.Cockpit.EyeOrDefault();
                var eyeWorld = prefab.transform.TransformPoint(new Vector3((float)eye[0], (float)eye[1], (float)eye[2]) + offset);
                // A view point of our own, fixed to the cockpit part. The stock one is a bone on the animated pilot's head
                // ("helmetCamPoint"), which the animation puts back every frame, so moving it doesn't stick.
                var vp = new GameObject("OpenPlane eye").transform;
                vp.SetParent(aircraft.cockpit != null ? aircraft.cockpit.transform : prefab.transform, false);
                vp.position = eyeWorld;
                vp.rotation = prefab.transform.rotation;
                aircraft.cockpitViewPoint = vp;
                aircraft.cockpitRenderers = new Renderer[0];
                Change("In the cockpit you sit in this plane's own cockpit, at the eye position set in the builder.");
            }
            else if (aircraft != null && bodyRenderers.Count > 0)
            {
                // Only the body: from the stock cockpit the pilot still sees the wings.
                var list = (aircraft.exteriorRenderers ?? new Renderer[0]).ToList();
                foreach (var r in bodyRenderers) if (!list.Contains(r)) list.Add(r);
                aircraft.exteriorRenderers = list.ToArray();
            }
            notes.Add($"stock skin hidden on {hidden} parts, design shape drawn ({tris:N0} triangles)");
        }

        // ------------------------------------------------------------------ aerodynamics

        private sealed class DesignLift
        {
            public double Area;
            public Vector3 Point;   // aerodynamic centre in prefab root space
            public Vector3 Up;
            public HashSet<string> Axes = new HashSet<string>();
            // A control's inputs (axis -> degrees at full input), its travel limit and servo speed (0 = not set).
            public Dictionary<string, double> Gains = new Dictionary<string, double>();
            public double Travel;
            public double Servo;
            /// <summary>Which wing panel ("w3") or strip ("w3s1") it is, to find its moving visual.</summary>
            public string Key = "";
        }

        private sealed class DonorLift
        {
            public AeroPart Part = null!;
            public Vector3 Point;   // root space
            public Vector3 Up;      // root space
            public HashSet<string> Axes = new HashSet<string>();
            public bool IsControl;
            public double Area;
            public Vector3 WeightedPoint;
            public Vector3 WeightedUp;
            /// <summary>The design's control pieces that ride on this stock part.</summary>
            public List<DesignLift> Matched = new List<DesignLift>();
        }

        /// <summary>
        /// Where a part's own rigidbody has its centre of mass, in root space: the volume-weighted centre of the part's
        /// enabled, solid colliders (not triggers, not the disabled gear collider, not those of child parts), as Unity works
        /// it out. Centres come from the colliders' own data through TransformPoint (Collider.bounds is empty on inactive
        /// objects), weighted by their volume at the colliders' scale; the part's pivot if it has none.
        /// </summary>
        internal static Vector3 PartCentre(Transform root, UnitPart part) => root.InverseTransformPoint(PartCentreWorld(part));

        private static Vector3 PartCentreWorld(UnitPart part)
        {
            var sum = Vector3.zero; float weight = 0;
            foreach (var c in part.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (c.GetComponentInParent<UnitPart>(true) != part) continue;
                var s = c.transform.lossyScale;
                var abs = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                Vector3 centre; float volume;
                switch (c)
                {
                    case BoxCollider box:
                        centre = box.transform.TransformPoint(box.center);
                        volume = Mathf.Abs(box.size.x * abs.x * box.size.y * abs.y * box.size.z * abs.z);
                        break;
                    case SphereCollider sphere:
                        centre = sphere.transform.TransformPoint(sphere.center);
                        float r = sphere.radius * Mathf.Max(abs.x, Mathf.Max(abs.y, abs.z));
                        volume = 4.18879f * r * r * r;
                        break;
                    case CapsuleCollider cap:
                        centre = cap.transform.TransformPoint(cap.center);
                        float along = cap.direction == 0 ? abs.x : cap.direction == 1 ? abs.y : abs.z;
                        float across = cap.direction == 0 ? Mathf.Max(abs.y, abs.z) : cap.direction == 1 ? Mathf.Max(abs.x, abs.z) : Mathf.Max(abs.x, abs.y);
                        float cr = cap.radius * across, ch = Mathf.Max(cap.height * along, 2 * cr);
                        volume = Mathf.PI * cr * cr * (ch - 2 * cr) + 4.18879f * cr * cr * cr;
                        break;
                    case MeshCollider mc when mc.sharedMesh != null:
                        var bounds = mc.sharedMesh.bounds;
                        centre = mc.transform.TransformPoint(bounds.center);
                        volume = Mathf.Abs(bounds.size.x * abs.x * bounds.size.y * abs.y * bounds.size.z * abs.z);
                        break;
                    default:
                        continue;
                }
                if (!(volume > 0) || float.IsInfinity(volume) || !Finite(centre)) continue;
                sum += centre * volume; weight += volume;
            }
            return weight > 0 ? sum / weight : part.transform.position;
        }

        private static void RetuneAero(GameObject prefab, AircraftDesign design, DesignStatsResult stats, AircraftDefinition donor,
                                       Func<double[], Vector3> toRoot, Vector3 cg, List<string> notes)
        {
            var root = prefab.transform;

            // Stock lifting parts and the controls that move them. The game applies a part's lift at its rigidbody's centre of
            // mass plus liftNormal.rotation * centerOfLift, so that is where the stock lift acts.
            var controls = prefab.GetComponentsInChildren<ControlSurface>(true);
            var flapParts = FlapParts(prefab);
            var donors = new List<DonorLift>();
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true))
            {
                if (p.wingArea <= 0) continue;
                var ln = p.liftNormal != null ? p.liftNormal : p.transform;
                var stockPoint = PartCentreWorld(p) + ln.rotation * p.centerOfLift;
                var d = new DonorLift
                {
                    Part = p,
                    Point = root.InverseTransformPoint(stockPoint),
                    Up = root.InverseTransformDirection(ln.up).normalized,
                };
                foreach (var cs in controls.Where(c => c.attachedSurface == p))
                {
                    d.IsControl = true;
                    if (Mathf.Abs(cs.pitchRange) > 0.01f) d.Axes.Add("pitch");
                    if (Mathf.Abs(cs.rollRange) > 0.01f) d.Axes.Add("roll");
                    if (Mathf.Abs(cs.yawRange) > 0.01f) d.Axes.Add("yaw");
                    if (cs.flap) d.Axes.Add("flaps");
                }
                // Stock flaps are lowered by a high-lift device, not a control surface: design area on them would turn down
                // with the flap and grow its deployed area with it (the AF-02's flaps went from 1.4 to 14 m2 and tore off).
                if (flapParts.Contains(p)) { d.IsControl = true; d.Axes.Add("flaps"); }
                donors.Add(d);
            }
            if (donors.Count == 0) { notes.Add("no stock lifting parts found; lift unchanged"); return; }

            // Design lifting elements: each panel and each control surface, at its quarter-chord point.
            var elements = new List<DesignLift>();
            for (int wi = 0; wi < design.Wings.Count; wi++)
            {
                var w = design.Wings[wi];
                var panelElement = Element(w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading, w.Up, null, toRoot);
                panelElement.Key = $"w{wi}";
                // An all-moving panel (stabilator, turning fin) is a control surface as a whole.
                if (w.Inputs != null && w.Inputs.Count > 0)
                {
                    foreach (var i in w.Inputs) { panelElement.Axes.Add(i.Axis); AddGain(panelElement, i.Axis, i.Gain); }
                    panelElement.Travel = System.Math.Max(System.Math.Abs(w.MinDeg), System.Math.Abs(w.MaxDeg));
                    panelElement.Servo = w.ServoDegPerSec;
                }
                elements.Add(panelElement);
                for (int si = 0; si < w.Surfaces.Count; si++)
                {
                    var s = w.Surfaces[si];
                    var e = Element(s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading, w.Up, s, toRoot);
                    e.Key = $"w{wi}s{si}";
                    elements.Add(e);
                }
            }
            elements.RemoveAll(e => !(e.Area >= 1e-4) || !Finite(e.Point) || !Finite(e.Up));
            if (elements.Count == 0) { notes.Add("the design has no lifting area; lift unchanged"); return; }

            // Normalise both planes to compare positions: x by half-span, z by length around their lift-weighted centres.
            float donorSemi = Mathf.Max(0.5f, donors.Max(d => Mathf.Abs(d.Point.x)));
            float designSemi = Mathf.Max(0.5f, elements.Max(e => Mathf.Abs(e.Point.x)));
            float donorLen = Mathf.Max(1f, donor.length), designLen = Mathf.Max(1f, (float)stats.LengthM);
            float donorZc = (float)(donors.Sum(d => d.Part.wingArea * d.Point.z) / donors.Sum(d => d.Part.wingArea));
            float designZc = (float)(elements.Sum(e => e.Area * e.Point.z) / elements.Sum(e => e.Area));

            int unmatched = 0;
            foreach (var e in elements)
            {
                bool vertical = Mathf.Abs(e.Up.x) > Mathf.Abs(e.Up.y);
                float ex = e.Point.x / designSemi, ez = (e.Point.z - designZc) / designLen;
                DonorLift? best = null; double bestScore = double.MaxValue;
                // First only parts that move the way this piece moves: area the design keeps fixed never rides on a stock
                // control surface, and a control only on a stock control of the same axis. (Main-wing area on the Revoker's
                // canards stalled them at full deflection and pitched the nose down when the AI pulled up.) Only if no such
                // part is left does a piece go to the nearest part of any kind.
                for (int pass = 0; pass < 2 && best == null; pass++)
                foreach (var d in donors)
                {
                    bool dVertical = Mathf.Abs(d.Up.x) > Mathf.Abs(d.Up.y);
                    if (dVertical != vertical) continue;
                    if (pass == 0 && d.IsControl && !e.Axes.Intersect(d.Axes).Any()) continue;
                    float dx = d.Point.x / donorSemi, dz = (d.Point.z - donorZc) / donorLen;
                    if (Mathf.Abs(ex) > 0.08f && Mathf.Abs(dx) > 0.08f && Mathf.Sign(ex) != Mathf.Sign(dx)) continue; // keep left on left
                    // A tail elevator never drives a stock canard, nor a canard a stock tail: on the other side of the centre of
                    // gravity the same deflection pitches the other way, and the lift point can't be moved that far.
                    if (e.Axes.Contains("pitch") && d.Axes.Contains("pitch"))
                    {
                        float es = e.Point.z - cg.z, ds = d.Point.z - cg.z;
                        if (Mathf.Abs(es) > 0.3f && Mathf.Abs(ds) > 0.3f && Mathf.Sign(es) != Mathf.Sign(ds)) continue;
                    }
                    double score = (ex - dx) * (ex - dx) + 4 * (ez - dz) * (ez - dz);
                    // Prefer a stock control that moves the same way; uncontrolled area prefers uncontrolled parts.
                    int shared = e.Axes.Intersect(d.Axes).Count();
                    if (e.Axes.Count > 0) score += shared > 0 ? -0.5 * shared : 1.0;
                    else if (d.IsControl) score += 0.6;
                    if (score < bestScore) { bestScore = score; best = d; }
                }
                if (best == null) { unmatched++; continue; }
                if (best.IsControl && e.Gains.Count > 0) best.Matched.Add(e);
                best.Area += e.Area;
                best.WeightedPoint += e.Point * (float)e.Area;
                best.WeightedUp += e.Up * (float)e.Area;
            }

            double before = donors.Sum(d => d.Part.wingArea), after = 0;
            int moved = 0, capped = 0;
            foreach (var d in donors)
            {
                var p = d.Part;
                if (d.Area <= 0) { p.wingArea = 0; continue; }
                p.wingArea = (float)d.Area;
                after += d.Area;
                var worldPoint = root.TransformPoint(d.WeightedPoint / (float)d.Area);
                var up = (d.WeightedUp / (float)d.Area).normalized;

                // 1. The lift direction first. Non-control parts get their own lift transform pointing where the design's panels
                //    point (dihedral, fins, canted tails); a stock liftNormal is never turned, since other things may hang under it.
                //    Control surfaces keep their stock hinge-mounted lift direction so deflections still work.
                if (!d.IsControl && up.sqrMagnitude > 0.5f)
                {
                    var worldUp = root.TransformDirection(up);
                    var ln = new GameObject("op_liftNormal").transform;
                    // Under the stock lift transform when there is one: flaps animate it, and the new direction follows.
                    ln.SetParent(p.liftNormal != null ? p.liftNormal : p.transform, false);
                    ln.position = worldPoint;
                    var fwd = Vector3.ProjectOnPlane(root.forward, worldUp);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(root.up, worldUp);
                    ln.rotation = Quaternion.LookRotation(fwd.normalized, worldUp);
                    p.liftNormal = ln;
                }

                // 2. Then the lift point, as the game reads it: an offset from the part's centre of mass (PartCentre, the same
                //    estimate the balance uses), in the lift transform's frame.
                var liftT = p.liftNormal != null ? p.liftNormal : p.transform;
                var offset = Quaternion.Inverse(liftT.rotation) * (worldPoint - PartCentreWorld(p));
                if (d.IsControl && offset.magnitude > MaxControlLiftOffset)
                {
                    // Stock control surfaces sit within 1.5 m of their lift point; further out, the surface's own lift would
                    // twist it off its hinge.
                    Plugin.Log.LogInfo($"  {design.Name}: lift offset on control surface {p.name} capped from {offset.magnitude:0.00} m to {MaxControlLiftOffset} m.");
                    offset = offset.normalized * MaxControlLiftOffset;
                    capped++;
                }
                if (Finite(offset)) p.centerOfLift = offset;
                moved++;
            }
            if (unmatched > 0)
                Warn($"{unmatched} pieces of the wings had no matching lifting part on the {_donorName}, so they give no lift in the game. Joining small pieces into bigger panels in the builder helps.");
            ApplyControlTravel(donors, controls, notes);
            LinkMovingSurfaces(root, donors, controls, notes);
            notes.Add($"lift retuned on {moved} of {donors.Count} stock parts ({before:0.#} -> {after:0.#} m2" + (unmatched > 0 ? $", {unmatched} design pieces unmatched" : "")
                      + (capped > 0 ? $", {capped} control-surface lift offsets capped at {MaxControlLiftOffset} m" : "") + ")");
        }

        private static DesignLift Element(double[] rl, double[] rt, double[] tt, double[] tl, double[] up, SurfaceDef? s, Func<double[], Vector3> toRoot)
        {
            double area = DesignStats.QuadArea(rl, rt, tt, tl);
            var c = (toRoot(rl) + toRoot(rt) + toRoot(tt) + toRoot(tl)) / 4;
            var lead = (toRoot(rl) + toRoot(tl)) / 2; var trail = (toRoot(rt) + toRoot(tt)) / 2;
            var point = s == null ? Vector3.Lerp(trail, lead, 0.75f) : c;   // quarter chord for panels
            var e = new DesignLift { Area = area, Point = point, Up = new Vector3((float)up[0], (float)up[1], (float)up[2]).normalized };
            if (s != null)
            {
                foreach (var i in s.Inputs) { e.Axes.Add(i.Axis); AddGain(e, i.Axis, i.Gain); }
                e.Travel = System.Math.Max(System.Math.Abs(s.MinDeg), System.Math.Abs(s.MaxDeg));
                e.Servo = s.ServoDegPerSec;
            }
            return e;
        }

        /// <summary>The design's moving surfaces drawn in the skin step (by key): their hinge pivot and hinge axis (root space).</summary>
        private static readonly Dictionary<string, (Transform pivot, Vector3 axis)> _movingVisuals = new Dictionary<string, (Transform, Vector3)>();

        /// <summary>
        /// Each moving surface the design draws turns with the stock control surface its lift rides on: it copies that
        /// surface's deflection (which the game sets from the controls, with the design's travel), so what you see is what
        /// the flight model does. A surface whose lift rides on a fixed stock part stays still.
        /// </summary>
        private static void LinkMovingSurfaces(Transform root, List<DonorLift> donors, ControlSurface[] controls, List<string> notes)
        {
            if (_movingVisuals.Count == 0) return;
            int linked = 0;
            foreach (var d in donors)
            {
                if (!d.IsControl) continue;
                var cs = controls.FirstOrDefault(c => c != null && c.attachedSurface == d.Part && c.visibleMesh != null);
                if (cs == null) continue;
                var stock = cs.visibleMesh.transform;
                var stockAxis = root.InverseTransformDirection(stock.parent != null ? stock.parent.TransformDirection(stock.localRotation * Vector3.right) : stock.right);
                foreach (var e in d.Matched)
                {
                    if (!_movingVisuals.TryGetValue(e.Key, out var mv) || mv.pivot == null) continue;
                    var f = mv.pivot.gameObject.AddComponent<SurfaceFollower>();
                    f.Stock = stock;
                    f.StockRest = stock.localRotation;
                    f.Axis = mv.axis.normalized;
                    f.Sign = Vector3.Dot(stockAxis, mv.axis) >= 0 ? 1f : -1f;
                    _movingVisuals.Remove(e.Key);
                    linked++;
                }
            }
            notes.Add($"moving surfaces: {linked} follow their stock control surfaces, {_movingVisuals.Count} stay still");
            _movingVisuals.Clear();
        }

        private static void AddGain(DesignLift e, string axis, double gain)
        {
            if (string.IsNullOrEmpty(axis) || double.IsNaN(gain) || double.IsInfinity(gain)) return;
            e.Gains[axis] = e.Gains.TryGetValue(axis, out var g) ? g + gain : gain;
        }

        // ------------------------------------------------------------------ control travel

        /// <summary>How far a design's travel may take a stock control surface from its own (the game's autopilot is tuned to it).</summary>
        private const float MinTravelScale = 0.4f, MaxTravelScale = 2f;

        /// <summary>
        /// The design's control travel and speed on the stock control surfaces its controls ride on. The game moves a surface
        /// by input x range on each axis it has (pitch, roll, yaw), clamped to that range, at its servo speed; each stock range
        /// takes the area-weighted travel of the design's controls on that axis (its gain, no more than its travel limit),
        /// keeping the stock sign (which way the surface turns), within 0.4 to 2 times the stock range. An axis the design
        /// doesn't drive there keeps the stock range; an axis the stock surface doesn't have can't be added. The game's flap
        /// surfaces take the design's flap travel.
        /// </summary>
        private static void ApplyControlTravel(List<DonorLift> donors, ControlSurface[] controls, List<string> notes)
        {
            int changed = 0, clamped = 0;
            var log = new List<string>();
            foreach (var d in donors)
            {
                if (!d.IsControl || d.Matched.Count == 0) continue;
                foreach (var cs in controls.Where(c => c != null && c.attachedSurface == d.Part))
                {
                    string Axis(string axis, ref float range)
                    {
                        var els = d.Matched.Where(e => e.Gains.ContainsKey(axis)).ToList();
                        if (els.Count == 0 || Mathf.Abs(range) < 0.01f) return "";
                        double area = els.Sum(e => e.Area);
                        if (!(area > 0)) return "";
                        double deg = els.Sum(e => e.Area * System.Math.Min(System.Math.Abs(e.Gains[axis]), e.Travel > 0 ? e.Travel : double.MaxValue)) / area;
                        float stock = Mathf.Abs(range);
                        float want = (float)deg;
                        float set = Mathf.Clamp(want, stock * MinTravelScale, stock * MaxTravelScale);
                        if (System.Math.Abs(set - want) > 0.05f) clamped++;
                        if (!(set > 0) || float.IsNaN(set)) return "";
                        string text = $"{axis} {stock:0.#}->{set:0.#}";
                        range = Mathf.Sign(range) * set;
                        return text;
                    }
                    var parts = new List<string>();
                    if (cs.flap)
                    {
                        float r = cs.pitchRange;
                        var t = Axis("flaps", ref r);
                        if (t.Length > 0) { cs.pitchRange = r; parts.Add(t); }
                    }
                    else
                    {
                        float pr = cs.pitchRange, rr = cs.rollRange, yr = cs.yawRange;
                        foreach (var t in new[] { Axis("pitch", ref pr), Axis("roll", ref rr), Axis("yaw", ref yr) }) if (t.Length > 0) parts.Add(t);
                        cs.pitchRange = pr; cs.rollRange = rr; cs.yawRange = yr;
                    }
                    var servos = d.Matched.Where(e => e.Servo > 0).ToList();
                    if (servos.Count > 0)
                    {
                        float stockServo = cs.servoSpeed;
                        float want = (float)(servos.Sum(e => e.Area * e.Servo) / servos.Sum(e => e.Area));
                        float set = stockServo > 0 ? Mathf.Clamp(want, stockServo * 0.5f, stockServo * 4f) : want;
                        if (set > 0 && !float.IsNaN(set)) { parts.Add($"speed {stockServo:0}->{set:0} deg/s"); cs.servoSpeed = set; }
                    }
                    if (parts.Count > 0) { changed++; log.Add($"{cs.attachedSurface.name} " + string.Join(", ", parts)); }
                }
            }
            notes.Add($"control travel: {changed} stock surfaces set from the design" + (clamped > 0 ? $" ({clamped} axes held within {MinTravelScale}-{MaxTravelScale}x stock)" : "") +
                      (log.Count > 0 ? " [" + string.Join("; ", log) + "]" : ""));
            if (changed > 0) Change($"Its control surfaces move as far and as fast as set in the builder ({changed} of the {_donorName}'s surfaces).");
        }

        // ------------------------------------------------------------------ pitch balance

        /// <summary>A stock lifting part as it was before OpenPlane changed it.</summary>
        private sealed class StockAero
        {
            public float Area;
            public Vector3 CenterOfLift;
            public Transform? LiftNormal;
            public float Behind;   // its lift point's distance behind the centre of gravity (m)
        }

        private sealed class StockAeroSet
        {
            public readonly Dictionary<AeroPart, StockAero> Parts = new Dictionary<AeroPart, StockAero>();
            public Pitch Pitch = new Pitch();
        }

        /// <summary>
        /// Pitch numbers as the game's flight model sees them. Every lifting part makes lift in proportion to its area at its
        /// own lift point, and the game has no downwash, so for each part facing up: its area times how far its lift point is
        /// behind the centre of gravity.
        /// </summary>
        private sealed class Pitch
        {
            public double Stiffness;          // sum over all parts; positive = the nose comes back down by itself (stable)
            public double Control;            // pitch controls: area x arm; negative when a control ended up on the wrong side
            public double ControlStiffness;   // the pitch controls' own share of Stiffness
            public double Area, ControlArea;
            /// <summary>How hard the controls can pitch the plane compared with how hard it resists: the AoA reach of the elevator.</summary>
            public double Ratio => Stiffness > 1e-6 ? Control / Stiffness : double.PositiveInfinity;
        }

        private static Transform LiftFrame(AeroPart p) => p.liftNormal != null ? p.liftNormal : p.transform;

        private static Vector3 LiftPoint(Transform root, AeroPart p) => root.InverseTransformPoint(PartCentreWorld(p) + LiftFrame(p).rotation * p.centerOfLift);

        private static bool FacesUp(Transform root, AeroPart p)
        {
            var up = root.InverseTransformDirection(LiftFrame(p).up);
            return Mathf.Abs(up.y) >= Mathf.Abs(up.x);
        }

        private static HashSet<AeroPart> PitchControls(GameObject prefab)
            => new HashSet<AeroPart>(prefab.GetComponentsInChildren<ControlSurface>(true)
                .Where(c => c != null && Mathf.Abs(c.pitchRange) > 0.01f).Select(c => c.attachedSurface as AeroPart).Where(p => p != null).Cast<AeroPart>());

        private static StockAeroSet SnapshotAero(GameObject prefab, Vector3 cg)
        {
            var root = prefab.transform;
            var set = new StockAeroSet();
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true))
                set.Parts[p] = new StockAero { Area = p.wingArea, CenterOfLift = p.centerOfLift, LiftNormal = p.liftNormal, Behind = cg.z - LiftPoint(root, p).z };
            set.Pitch = MeasurePitch(prefab, cg, set);
            return set;
        }

        private static Pitch MeasurePitch(GameObject prefab, Vector3 cg, StockAeroSet stock)
        {
            var root = prefab.transform;
            var controls = PitchControls(prefab);
            var r = new Pitch();
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true))
            {
                if (!(p.wingArea > 0) || !FacesUp(root, p)) continue;
                double behind = cg.z - LiftPoint(root, p).z;
                if (double.IsNaN(behind) || double.IsInfinity(behind)) continue;
                r.Area += p.wingArea;
                r.Stiffness += p.wingArea * behind;
                if (!controls.Contains(p)) continue;
                // A control pitches the plane the way its stock deflection expects only on the stock side of the centre of gravity.
                float stockBehind = stock.Parts.TryGetValue(p, out var s) ? s.Behind : (float)behind;
                bool sameSide = Math.Abs(behind) < 0.05 || Math.Sign(behind) == Math.Sign(stockBehind);
                r.Control += p.wingArea * Math.Abs(behind) * (sameSide ? 1 : -1);
                r.ControlStiffness += p.wingArea * behind;
                r.ControlArea += p.wingArea;
            }
            return r;
        }

        /// <summary>Reach to aim for when the stock plane itself measures neutral or unstable in pitch (canard deltas).</summary>
        private const double DefaultReachTarget = 1.5;

        private static string Reach(double r) => double.IsInfinity(r) || double.IsNaN(r) ? "n/a" : r.ToString("0.00");

        /// <summary>Every lifting part a control surface moves (any axis), whose lift point must stay near its hinge.</summary>
        private static HashSet<AeroPart> MovedParts(GameObject prefab)
        {
            var moved = new HashSet<AeroPart>(prefab.GetComponentsInChildren<ControlSurface>(true)
                .Select(c => c != null ? c.attachedSurface as AeroPart : null).Where(p => p != null).Cast<AeroPart>());
            moved.UnionWith(FlapParts(prefab));
            return moved;
        }

        /// <summary>Lifting parts a high-lift device (flap or slat) moves.</summary>
        private static HashSet<AeroPart> FlapParts(GameObject prefab)
            => new HashSet<AeroPart>(prefab.GetComponentsInChildren<HighLiftDevice>(true)
                .Select(h => h == null ? null : HarmonyLib.Traverse.Create(h).Field("aeroPart").GetValue<AeroPart>()).Where(p => p != null).Cast<AeroPart>());

        /// <summary>
        /// Joints sized for the design's loads. Each part's joint is tuned for the stock plane's lift and weight; a part that now
        /// carries several times its stock area (or holds parts that do, or sits on a heavier plane) would tear off in the first
        /// hard pull (the AF-02's wingtip and flap broke 12 s into flight). Joints only get stronger, never weaker.
        /// </summary>
        private static void StrengthenJoints(GameObject prefab, AircraftDefinition donor, StockAeroSet stock, FlightReport flight, List<string> notes)
        {
            float stockTotal = stock.Parts.Values.Sum(s => Mathf.Max(0, s.Area));
            float total = prefab.GetComponentsInChildren<AeroPart>(true).Sum(p => Mathf.Max(0, p.wingArea));
            float massRatio = donor.mass > 0 && Positive(flight.EmptyMassKg) ? (float)(flight.EmptyMassKg / donor.mass) : 1f;
            float global = Mathf.Max(1f, stockTotal > 0 ? total / stockTotal : 1f, massRatio);
            float Own(AeroPart p)
            {
                float stockArea = stock.Parts.TryGetValue(p, out var s) ? s.Area : 0f;
                return Mathf.Max(0, p.wingArea) / Mathf.Max(0.5f, stockArea);
            }
            // A joint carries its own part's load and everything outboard of it (its child parts).
            float Load(AeroPart p)
            {
                float f = Own(p);
                foreach (var child in p.GetComponentsInChildren<AeroPart>(true))
                    if (child != p) f = Mathf.Max(f, Own(child));
                return f;
            }
            int strengthened = 0; float most = 1;
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true))
            {
                if (p.joints == null || p.joints.Length == 0) continue;
                float k = Mathf.Clamp(Mathf.Max(global, Load(p)), 1f, 20f);
                if (float.IsNaN(k) || k <= 1.01f) continue;
                foreach (var j in p.joints)
                {
                    if (j == null) continue;
                    j.breakForce *= k;
                    j.breakTorque *= k;
                }
                strengthened++;
                most = Mathf.Max(most, k);
            }
            if (strengthened > 0) notes.Add($"joints strengthened on {strengthened} parts (up to x{most:0.0})");
        }

        /// <summary>
        /// Makes sure the copy can still raise its nose. A design balanced for a flight model with downwash (Flyout, real
        /// planes) has a tail that works much harder in Nuclear Option, so a very stable design ends up far stiffer in pitch than
        /// any stock plane, with an elevator that can't lift the nose off the runway. Steps:
        /// 0. if no pitch control works the right way any more, the stock pitch controls get their area back;
        /// 1. if the copy is still too stiff for its controls (under 70 % of the stock plane's reach), the fixed lift moves
        ///    forward (as if the centre of gravity sat a little further back), never below a 4 % stability margin;
        /// 2. if that isn't enough, the pitch controls grow (at most 2.5 times), only where growing them helps.
        /// Designs that are unstable in pitch are left to the game's autopilot.
        /// </summary>
        private static void BalancePitch(GameObject prefab, FlightReport flight, DesignStatsResult stats, Vector3 cg, StockAeroSet stock, List<string> notes)
        {
            var sp = stock.Pitch;
            if (!(sp.Control > 0) || !(sp.Area > 0)) return;   // the stock plane has no pitch control to compare with
            var root = prefab.transform;
            var controls = PitchControls(prefab);
            var copy = MeasurePitch(prefab, cg, stock);
            string start = $"pitch reach {Reach(copy.Ratio)} (stock {Reach(sp.Ratio)})";
            var did = new List<string>();
            string Done(string end) => start + (did.Count > 0 ? "; " + string.Join(", ", did) : "") + end;

            // 0. No pitch control left that works the right way (the design's elevator went to a part on the other side of the
            //    centre of gravity, or nowhere): the stock pitch controls get their stock area back, scaled with the plane, at
            //    their stock lift points.
            double areaScale = Math.Max(0.1, copy.Area / sp.Area);
            if (copy.Control <= 0.15 * sp.Control * areaScale)
            {
                foreach (var p in controls)
                {
                    if (!stock.Parts.TryGetValue(p, out var s) || !(s.Area > 0)) continue;
                    double behind = cg.z - LiftPoint(root, p).z;
                    bool wrongSide = p.wingArea > 0 && Math.Abs(behind) >= 0.05 && Math.Sign(behind) != Math.Sign(s.Behind);
                    if (p.wingArea > 0 && !wrongSide) continue;
                    p.wingArea = (float)(s.Area * areaScale);
                    p.liftNormal = s.LiftNormal;
                    p.centerOfLift = s.CenterOfLift;
                }
                copy = MeasurePitch(prefab, cg, stock);
                did.Add($"stock pitch controls given back ({copy.ControlArea:0.0} m2)");
                Change($"Your elevators sit on the other side of the centre of gravity from the {_donorName}'s pitch controls, so the {_donorName}'s own pitch controls steer it (sized to your plane).");
            }

            // Parts a control surface moves keep their lift points (a lift point far from its hinge twists the surface off);
            // the fixed lifting parts are the ones that move.
            double mac = Positive(flight.MeanChordM) ? flight.MeanChordM : Positive(stats.SpanM) ? stats.WingAreaM2 / stats.SpanM : 1;
            double maxShift = 0.15 * Math.Max(1, stats.LengthM);
            var moved = MovedParts(prefab);
            double fixedArea = 0;
            var fixedParts = new List<AeroPart>();
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true))
                if (p.wingArea > 0 && !moved.Contains(p) && FacesUp(root, p)) { fixedParts.Add(p); fixedArea += p.wingArea; }
            void ShiftFixed(double metresForward)
            {
                var worldShift = root.TransformDirection(Vector3.forward) * (float)metresForward;
                foreach (var p in fixedParts) p.centerOfLift += Quaternion.Inverse(LiftFrame(p).rotation) * worldShift;
            }

            // 1a. Strongly unstable as the game flies it (more than 5 % of a chord; usually a weight estimate that put the
            //     centre of gravity too far back): the fixed lift moves back until the plane is just stable.
            if (copy.Stiffness < -0.05 * mac * copy.Area && fixedArea > 0)
            {
                double back = Math.Min((0.02 * mac * copy.Area - copy.Stiffness) / fixedArea, maxShift);
                ShiftFixed(-back);
                copy = MeasurePitch(prefab, cg, stock);
                did.Add($"fixed lift moved {back:0.00} m back (it was unstable in the game)");
                Change($"It was unstable in the game's flight model, so OpenPlane moved its lift {back:0.00} m back. To fly as you designed it, move weight forward or the wings back in the builder.");
            }

            // At least 70 % of the stock plane's reach, and never under the minimum every plane that flew in testing had.
            double target = System.Math.Max(sp.Stiffness > 0 ? 0.7 * sp.Ratio : DefaultReachTarget, GameEstimates.MinPitchReach);
            if (copy.Stiffness <= 0) { notes.Add(Done(", slightly unstable in pitch: left to the autopilot")); return; }
            if (copy.Ratio >= target) { notes.Add(Done($" -> {Reach(copy.Ratio)}, fine")); return; }

            // 1. Too stiff for its controls: move the fixed lift forward, never below a 4 % stability margin.
            double floor = 0.04 * mac * copy.Area;
            double want = Math.Max(floor, copy.Control > 0 ? copy.Control / target : floor);
            if (want < copy.Stiffness && fixedArea > 0)
            {
                double shift = Math.Min((copy.Stiffness - want) / fixedArea, maxShift);
                ShiftFixed(shift);
                copy = MeasurePitch(prefab, cg, stock);
                did.Add($"fixed lift moved {shift:0.00} m forward");
                Change($"It was too stiff in pitch for its elevator in the game, so OpenPlane moved its lift {shift:0.00} m forward. A bigger elevator, or the wing further forward, makes this unnecessary.");
            }

            // 2. Bigger pitch controls. Reach = k*C / (Sf + k*Sc) only grows with k while the fixed parts are stable (Sf > 0);
            //    on planes whose fixed parts are unstable (all-moving tails carrying the stability) bigger controls make it worse.
            double sf = copy.Stiffness - copy.ControlStiffness;
            if (copy.Ratio < target && copy.Control > 0 && sf > 1e-6)
            {
                double den = copy.Control - target * copy.ControlStiffness;
                double k = den > 1e-6 ? target * sf / den : 2.5;
                k = Math.Min(2.5, Math.Max(1, double.IsNaN(k) ? 2.5 : k));
                if (k > 1.01)
                {
                    foreach (var p in controls) if (p.wingArea > 0) p.wingArea *= (float)k;
                    copy = MeasurePitch(prefab, cg, stock);
                    did.Add($"pitch controls x{k:0.0}");
                    Change($"OpenPlane made its pitch controls {k:0.0} times larger in the game so it can lift its nose. Enlarging the elevator in the builder makes this unnecessary.");
                }
            }
            if (copy.Ratio < 0.9 * target)
                Warn($"Its pitch control is still weak in the game (reach {Reach(copy.Ratio)}; {target:0.0} is needed), so it may not lift its nose on takeoff or pull out of a dive. Enlarge the elevator or move the wing forward in the builder.");
            notes.Add(Done($" -> {Reach(copy.Ratio)}" + (copy.Ratio < target ? " (still short)" : "")));
        }

        /// <summary>
        /// Flaps and slats (HighLiftDevice) add their stock extra area when they deploy at low speed. Scaled with the part's new
        /// area, so a part the design doesn't use (area 0) adds nothing at a stock lift point on takeoff and landing.
        /// </summary>
        private static void ScaleHighLift(GameObject prefab, StockAeroSet stock, List<string> notes)
        {
            int n = 0;
            foreach (var h in prefab.GetComponentsInChildren<HighLiftDevice>(true))
            {
                var t = HarmonyLib.Traverse.Create(h);
                var part = t.Field("aeroPart").GetValue<AeroPart>();
                if (part == null || !stock.Parts.TryGetValue(part, out var s) || !(s.Area > 0)) continue;
                var extra = t.Field("deployedArea");
                float stockExtra = extra.GetValue<float>();
                float scaled = stockExtra * Mathf.Max(0, part.wingArea) / s.Area;
                if (float.IsNaN(scaled) || float.IsInfinity(scaled)) continue;
                extra.SetValue(scaled);
                n++;
            }
            if (n > 0) notes.Add($"flaps and slats scaled on {n} parts");
        }

        private static void ScaleDrag(GameObject prefab, AircraftDefinition donor, DesignStatsResult stats, List<string> notes)
        {
            // Body drag follows the plane's side profile (length x height) compared with the stock plane.
            if (!(donor.length > 0) || !(donor.height > 0) || !Positive(stats.LengthM) || !Positive(stats.HeightM)) return;
            float ratio = Mathf.Clamp((float)(stats.LengthM * stats.HeightM / (donor.length * donor.height)), 0.25f, 4f);
            if (float.IsNaN(ratio)) return;
            foreach (var p in prefab.GetComponentsInChildren<AeroPart>(true)) p.dragArea *= ratio;
            notes.Add($"body drag x{ratio:0.00}");
        }

        // ------------------------------------------------------------------ engines, mass, handling

        private static void ScaleEngines(GameObject prefab, AircraftDesign design, List<string> notes)
        {
            var jets = prefab.GetComponentsInChildren<Turbojet>(true);
            double designJet = design.Engines.Where(e => e.Type == "turbofan" || e.Type == "turbojet").Sum(e => e.MaxThrustN);
            double stockJet = jets.Sum(j => (double)j.maxThrust);
            if (jets.Length > 0 && Positive(designJet) && Positive(stockJet))
            {
                float f = (float)(designJet / stockJet);
                foreach (var j in jets) j.maxThrust *= f;
                notes.Add($"jet thrust {stockJet / 1000:0} -> {designJet / 1000:0} kN");

                // Afterburners: the game adds each nozzle's afterburner thrust on top of the engine's (JetNozzle), at full
                // throttle. It becomes the design's afterburner thrust minus its dry thrust; a design without afterburners
                // gets none.
                double designAb = design.Engines.Where(e => e.Type == "turbofan" || e.Type == "turbojet")
                                        .Sum(e => System.Math.Max(0, e.AfterburnerThrustN - e.MaxThrustN));
                var burners = new List<object>();
                foreach (var nz in prefab.GetComponentsInChildren<JetNozzle>(true))
                    if (HarmonyLib.Traverse.Create(nz).Field("afterburners").GetValue() is Array arr)
                        foreach (var b in arr) if (b != null) burners.Add(b);
                double stockAb = burners.Sum(b => (double)HarmonyLib.Traverse.Create(b).Field("thrust").GetValue<float>());
                if (burners.Count > 0 && Positive(stockAb) && Positive(designAb))
                {
                    float g = (float)(designAb / stockAb);
                    foreach (var b in burners)
                    {
                        var t = HarmonyLib.Traverse.Create(b).Field("thrust");
                        t.SetValue(t.GetValue<float>() * g);
                    }
                    notes.Add($"afterburner thrust {stockAb / 1000:0} -> {designAb / 1000:0} kN on {burners.Count} nozzles");
                }
                else if (burners.Count > 0 && !Positive(designAb))
                {
                    // A design without afterburners keeps the stock ones for now: on their real thrust alone some (the converted
                    // He 162) could no longer reach takeoff speed on the game's runways, as they did in OpenPlane 0.1.
                    notes.Add($"afterburner: none in the design, the stock {stockAb / 1000:0} kN kept");
                }
            }

            var turbines = prefab.GetComponentsInChildren<TurbineEngine>(true);
            double designPower = design.Engines.Where(e => e.PowerKw.HasValue).Sum(e => e.PowerKw!.Value * 1000);
            double stockPower = turbines.Sum(t => (double)t.maxPower);
            if (turbines.Length > 0 && Positive(designPower) && Positive(stockPower))
            {
                float f = (float)(designPower / stockPower);
                foreach (var t in turbines) t.maxPower *= f;
                notes.Add($"engine power {stockPower / 1000:0} -> {designPower / 1000:0} kW");
            }
        }

        private static void SetMass(AircraftDefinition def, AircraftDefinition donor, GameObject prefab, FlightReport flight, List<string> notes)
        {
            if (!Positive(flight.EmptyMassKg) || !(donor.mass > 0)) return;
            float ratio = Mathf.Clamp((float)(flight.EmptyMassKg / donor.mass), 0.1f, 10f);
            if (float.IsNaN(ratio)) return;
            var parts = prefab.GetComponentsInChildren<UnitPart>(true);
            foreach (var part in parts) part.mass *= ratio;
            foreach (var rb in prefab.GetComponentsInChildren<Rigidbody>(true)) rb.mass *= ratio;
            // The definition's mass is what Aircraft.SetSimplePhysics gives the whole plane (remote and distant planes). Set it
            // explicitly: the stock value (the sum of the stock prefab's parts) times the same ratio as every part. Never
            // CacheMass(): it counts only active parts, and this copy sits under an inactive holder, so it would give 0.
            def.mass = donor.mass * ratio;

            // Landing gear carries its own mass (it is taken off the part when a wheel breaks) and springs tuned for the stock
            // weight: scale them too, so a lighter plane doesn't sit on springs several times too stiff, and a broken wheel
            // can't leave a part with negative mass.
            int legs = 0;
            foreach (var g in prefab.GetComponentsInChildren<LandingGear>(true))
            {
                g.mass *= ratio; g.springRate *= ratio; g.dampingRate *= ratio;
                legs++;
            }
            notes.Add($"mass {donor.mass:0} -> {def.mass:0} kg (x{ratio:0.00}" + (legs > 0 ? $", {legs} gear legs' mass and springs too" : "") + ")");
        }

        private static void ScaleFuel(GameObject prefab, FlightReport flight, List<string> notes)
        {
            var tanks = prefab.GetComponentsInChildren<FuelTank>(true);
            double total = tanks.Sum(t => (double)t.fuelCapacity);
            if (tanks.Length == 0 || !Positive(total) || !Positive(flight.FuelMassKg)) return;
            float f = (float)(flight.FuelMassKg / total);
            foreach (var t in tanks) t.fuelCapacity *= f;
            notes.Add($"fuel {total:0} -> {flight.FuelMassKg:0} kg");
        }

        // ------------------------------------------------------------------ landing gear

        /// <summary>A landing-gear leg's whole object: from the gear up to the one hanging straight off its part.</summary>
        private static Transform LegRoot(Transform root, Transform gear)
        {
            var leg = gear;
            while (leg.parent != null && leg.parent != root && leg.parent.GetComponent<UnitPart>() == null) leg = leg.parent;
            return leg;
        }

        /// <summary>
        /// Stands the stock landing-gear legs under the design's wheels: each leg (with its retraction arm's target) moves to
        /// the nearest design wheel on its side, at its stock height: a nose leg across and along, main legs across only (see
        /// below). Moving legs up or down put
        /// wheels where the stock spawn height and suspension don't expect them (wheels broke on the runway), so the height
        /// stays. A nose leg only moves to a wheel ahead of the centre of gravity and a main leg to one behind it, so a
        /// tricycle stock leg never becomes a tail wheel. (On the stock spots a shorter design's nose wheel hung in the air
        /// ahead of its nose.)
        /// </summary>
        private static void FitGear(GameObject prefab, AircraftDesign design, Func<double[], Vector3> toRoot, Vector3 cg, List<string> notes)
        {
            var root = prefab.transform;
            var legs = prefab.GetComponentsInChildren<LandingGear>(true).Where(g => g != null).ToList();
            if (legs.Count == 0 || design.Gear == null || design.Gear.Count == 0) return;
            var gearDefs = design.Gear.Where(g => g?.Position != null && Finite(toRoot(g.Position))).ToList();
            var wheels = gearDefs.Select(g => toRoot(g.Position)).ToList();
            // Steering and brakes as designed, on the legs matched below. A design with no steering (or no brakes) at all keeps
            // the stock plane's, so a converted plane whose file says nothing doesn't lose them.
            // Planes converted from Flyout keep the stock ones: Flyout's gear flags don't mean the same (the converted Su-57 steers
            // on its main wheels and not its nose, and lost its stock nose steering, so it couldn't taxi).
            bool converted = string.Equals(design.Origin?.Source, "flyout", StringComparison.OrdinalIgnoreCase);
            bool anySteers = !converted && gearDefs.Any(g => g.Steerable), anyBrakes = !converted && gearDefs.Any(g => g.Brakes);
            int steerSet = 0, brakeSet = 0;
            var armTargets = prefab.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.IndexOf("armtarget", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var taken = new bool[wheels.Count];
            int moved = 0;
            foreach (var g in legs.OrderBy(g => Mathf.Abs(root.InverseTransformPoint(g.transform.position).x)))
            {
                var cur = root.InverseTransformPoint(g.transform.position);
                bool centre = Mathf.Abs(cur.x) < 0.35f;
                int best = -1; float bestD = float.MaxValue;
                for (int k = 0; k < wheels.Count; k++)
                {
                    if (taken[k]) continue;
                    var w = wheels[k];
                    bool wCentre = Mathf.Abs(w.x) < 0.35f;
                    if (wCentre != centre || (!centre && Mathf.Sign(w.x) != Mathf.Sign(cur.x))) continue;
                    if (Mathf.Sign(w.z - cg.z) != Mathf.Sign(cur.z - cg.z)) continue;
                    float d = new Vector2(w.x - cur.x, w.z - cur.z).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = k; }
                }
                if (best < 0) continue;
                taken[best] = true;
                var gt = HarmonyLib.Traverse.Create(g);
                if (anySteers && gt.Field("steering").GetValue<bool>() != gearDefs[best].Steerable) { gt.Field("steering").SetValue(gearDefs[best].Steerable); steerSet++; }
                if (anyBrakes && gt.Field("braked").GetValue<bool>() != gearDefs[best].Brakes) { gt.Field("braked").SetValue(gearDefs[best].Brakes); brakeSet++; }
                // A nose leg moves to the design's nose wheel, but stays at least 1 m ahead of the loaded centre of gravity.
                // Main legs only move across, to the design's track: along the plane they keep their stock spot, so the
                // plane can't sit back on its tail and rotates on takeoff as the stock plane does (main legs moved forward
                // to the design's wheels tipped the Ta-183 onto its tail and left the Fighter and Trainer stuck).
                float z = wheels[best].z;
                if (cur.z > cg.z) z = Mathf.Max(z, cg.z + 1f);
                else z = cur.z;
                var delta = new Vector3(wheels[best].x - cur.x, 0, z - cur.z);
                if (delta.magnitude < 0.05f || delta.magnitude > 12f) continue;
                var worldDelta = root.TransformVector(delta);
                // The whole leg: from the gear up to the object hanging straight off its part (its mount or hinge).
                var leg = LegRoot(root, g.transform);
                // Its retraction arm points at a target beside the leg: the nearest one on its side moves with it.
                var arm = armTargets.Where(a => !a.IsChildOf(leg))
                    .Select(a => (a, p: root.InverseTransformPoint(a.position)))
                    .Where(x => (centre ? Mathf.Abs(x.p.x) < 0.35f : Mathf.Sign(x.p.x) == Mathf.Sign(cur.x)) && (x.p - cur).magnitude < 3f)
                    .OrderBy(x => (x.p - cur).sqrMagnitude).Select(x => x.a).FirstOrDefault();
                leg.position += worldDelta;
                if (arm != null) { arm.position += worldDelta; armTargets.Remove(arm); }
                moved++;
            }
            notes.Add($"landing gear: {moved} of {legs.Count} legs moved under the design's wheels; steering changed on {steerSet}, brakes on {brakeSet}" +
                      $" (steering: {string.Join(",", legs.Select(l => HarmonyLib.Traverse.Create(l).Field("steering").GetValue<bool>() ? "y" : "n"))}, brakes: {string.Join(",", legs.Select(l => HarmonyLib.Traverse.Create(l).Field("braked").GetValue<bool>() ? "y" : "n"))})");
            if (moved > 0) Change($"Its landing gear stands where you placed the wheels ({moved} of the {_donorName}'s legs moved there, at their stock length).");
        }

        // ------------------------------------------------------------------ weapons

        /// <summary>Weapon mass each OpenPlane plane may carry (kg); loadouts are trimmed to it when the plane spawns.</summary>
        internal static readonly Dictionary<AircraftDefinition, float> WeaponCapacity = new Dictionary<AircraftDefinition, float>();

        private static float MountMass(WeaponMount? m) => m == null ? 0f : Mathf.Max(m.mass, m.emptyMass);

        /// <summary>A station set of built-in guns (its options are ammunition, not stores).</summary>
        private static bool IsGunSet(HardpointSet s)
            => s.hardpoints != null && s.hardpoints.Any(h => h != null && ((h.BuiltInWeapons?.Length ?? 0) > 0 || (h.BuiltInTurrets?.Length ?? 0) > 0));

        private static float SetMass(HardpointSet s, WeaponMount? m) => MountMass(m) * Mathf.Max(1, s.hardpoints?.Count ?? 1);

        /// <summary>
        /// Takes off weapons a station no longer offers, then the heaviest stores until the load fits what the plane can carry.
        /// Used on the AI's preset loadouts when the plane is built, and on every loadout (player, mission, AI) when it spawns;
        /// with <paramref name="weightOnly"/> (the host's weapon check) only the weight is trimmed. Returns how many were left off.
        /// </summary>
        internal static int TrimLoadout(Loadout? loadout, HardpointSet[] sets, float capacityKg, bool weightOnly = false)
        {
            if (loadout?.weapons == null || sets == null) return 0;
            int removed = 0;
            for (int i = 0; !weightOnly && i < loadout.weapons.Count && i < sets.Length; i++)
            {
                var m = loadout.weapons[i];
                if (m != null && sets[i]?.weaponOptions != null && !sets[i].weaponOptions.Contains(m)) { loadout.weapons[i] = null!; removed++; }
            }
            while (true)
            {
                float total = 0, heaviestMass = 0; int heaviest = -1;
                for (int i = 0; i < loadout.weapons.Count && i < sets.Length; i++)
                {
                    var m = loadout.weapons[i];
                    if (m == null || sets[i] == null || IsGunSet(sets[i])) continue;
                    float mass = SetMass(sets[i], m);
                    total += mass;
                    if (mass > heaviestMass) { heaviestMass = mass; heaviest = i; }
                }
                if (total <= capacityKg + 0.5f || heaviest < 0) break;
                loadout.weapons[heaviest] = null!;
                removed++;
            }
            return removed;
        }

        private static IEnumerable<Renderer> PylonRenderers(Hardpoint hp)
        {
            if (hp.Pylon != null) yield return hp.Pylon;
            if (hp.Plug != null) yield return hp.Plug;
            var options = HarmonyLib.Traverse.Create(hp).Field("pylonOptions").GetValue() as Array;
            if (options == null) yield break;
            foreach (var o in options)
            {
                var r = o == null ? null : HarmonyLib.Traverse.Create(o).Field("renderer").GetValue<Renderer>();
                if (r != null) yield return r;
            }
        }

        /// <summary>
        /// Puts the stock plane's weapons where the design carries them, and only as much as it can carry:
        /// - each external station set moves onto free design hardpoints on the same side (centreline stations onto centreline
        ///   hardpoints); a set that doesn't find one for every station is closed (offers nothing, its pylons hidden), so
        ///   no store hangs in the air beside a smaller plane, and a design without hardpoints carries no external stores;
        /// - internal bays stay open only if the design has an internal hardpoint;
        /// - built-in guns move to the design's guns;
        /// - stores heavier than the plane's size allows are no longer offered, and the total load is limited to the stock
        ///   plane's heaviest preset scaled with the weight (stock stores on a much lighter plane made it nose-heavy, sag on
        ///   its gear and scrape the ground).
        /// </summary>
        private static void FitWeapons(AircraftDefinition def, AircraftDefinition donor, GameObject prefab, AircraftDesign design, FlightReport flight,
                                       Func<double[], Vector3> toRoot, List<string> notes)
        {
            var root = prefab.transform;
            var wm = prefab.GetComponentInChildren<WeaponManager>(true);
            if (wm?.hardpointSets == null || wm.hardpointSets.Length == 0) return;
            var sets = wm.hardpointSets;
            float ratio = donor.mass > 0 && Positive(flight.EmptyMassKg) ? Mathf.Clamp((float)(flight.EmptyMassKg / donor.mass), 0.1f, 10f) : 1f;

            // What the stock plane carries at most, before anything changes.
            var stockParams = donor.aircraftParameters;
            float stockMax = 0;
            if (stockParams?.StandardLoadouts != null)
                foreach (var sl in stockParams.StandardLoadouts)
                {
                    if (sl?.loadout?.weapons == null) continue;
                    float t = 0;
                    for (int i = 0; i < sl.loadout.weapons.Count && i < sets.Length; i++)
                        if (sets[i] != null && !IsGunSet(sets[i])) t += SetMass(sets[i], sl.loadout.weapons[i]);
                    stockMax = Mathf.Max(stockMax, t);
                }
            if (!(stockMax > 0))
                foreach (var set in sets)
                    if (set?.weaponOptions != null && !IsGunSet(set))
                        stockMax += set.weaponOptions.Select(m => SetMass(set, m)).DefaultIfEmpty(0).Max();

            var external = design.Hardpoints.Where(h => !h.Internal).Select(h => toRoot(h.Position)).ToList();
            bool anyInternal = design.Hardpoints.Any(h => h.Internal);
            var taken = new bool[external.Count];
            var parts = prefab.GetComponentsInChildren<UnitPart>(true).Where(p => p != null && p.mass > 0).ToList();
            int movedStations = 0, offered = 0, closed = 0, heavyDropped = 0;
            var setLog = new List<string>();

            void Close(HardpointSet set)
            {
                set.weaponOptions = new List<WeaponMount> { null! };
                foreach (var hp in set.hardpoints)
                {
                    if (hp == null) continue;
                    foreach (var r in PylonRenderers(hp))
                    {
                        if (r is SkinnedMeshRenderer smr) smr.sharedMesh = Empty;
                        else if (r.GetComponent<MeshFilter>() is MeshFilter mf) mf.sharedMesh = Empty;
                        r.enabled = false;
                    }
                }
                closed++;
            }

            // Inner station sets first, so they take the inner hardpoints.
            var order = Enumerable.Range(0, sets.Length).Where(i => sets[i]?.hardpoints != null && sets[i].hardpoints.Count > 0)
                .OrderBy(i => sets[i].hardpoints.Where(h => h?.transform != null).Select(h => Mathf.Abs(root.InverseTransformPoint(h.transform.position).x)).DefaultIfEmpty(0).Average())
                .ToList();
            var gunTargets = design.Guns.Select(g => toRoot(g.Position)).ToList();
            int gunStationsMoved = 0;
            foreach (int i in order)
            {
                var set = sets[i];
                if (IsGunSet(set)) continue;
                // A station in the body that only takes guns is the plane's cannon (the KR-67's is a loadout choice, "27mm
                // Autocannon"): it goes to the design's gun, not to a wing pylon (it took the Lanner's right inner pylon and
                // left its pylons paired wrongly), and is closed on a design without guns.
                var opts = set.weaponOptions?.Where(m => m != null).ToList() ?? new List<WeaponMount>();
                bool bodyGun = opts.Count > 0 && opts.All(m => m.info != null && m.info.gun)
                               && set.hardpoints.Where(h => h?.transform != null).All(h => Mathf.Abs(root.InverseTransformPoint(h.transform.position).x) < 1.5f);
                if (bodyGun)
                {
                    if (gunTargets.Count == 0) { setLog.Add($"#{i} gun station closed (the design has no gun)"); Close(set); continue; }
                    foreach (var hp in set.hardpoints)
                    {
                        if (hp?.transform == null) continue;
                        var cur = root.InverseTransformPoint(hp.transform.position);
                        var best = gunTargets.OrderBy(g => (Mathf.Abs(g.x) < 0.35f || Mathf.Abs(cur.x) < 0.35f || Mathf.Sign(g.x) == Mathf.Sign(cur.x) ? 0 : 1000) + (g - cur).sqrMagnitude).First();
                        var target = root.TransformPoint(best);
                        var delta = target - hp.transform.position;
                        foreach (var r in PylonRenderers(hp))
                            if (!r.transform.IsChildOf(hp.transform)) r.transform.position += delta;
                        hp.transform.position = target;
                        gunStationsMoved++;
                    }
                    offered++;
                    setLog.Add($"#{i} gun station -> the design's gun");
                    continue;
                }
                if (set.hardpoints.Any(h => h?.bayDoors != null && h.bayDoors.Length > 0))
                {
                    if (anyInternal) offered++; else Close(set);
                    setLog.Add($"#{i} bay x{set.hardpoints.Count} {(anyInternal ? "open" : "closed")}");
                    continue;
                }
                var picks = new List<int>();
                foreach (var hp in set.hardpoints)
                {
                    int best = -1;
                    if (hp?.transform != null)
                    {
                        var cur = root.InverseTransformPoint(hp.transform.position);
                        bool centre = Mathf.Abs(cur.x) < 0.35f;
                        float bestD = float.MaxValue;
                        for (int k = 0; k < external.Count; k++)
                        {
                            if (taken[k] || picks.Contains(k)) continue;
                            var t = external[k];
                            bool tCentre = Mathf.Abs(t.x) < 0.35f;
                            if (centre != tCentre || (!centre && Mathf.Sign(t.x) != Mathf.Sign(cur.x))) continue;
                            float d = (t - cur).sqrMagnitude;
                            if (d < bestD) { bestD = d; best = k; }
                        }
                    }
                    picks.Add(best);
                }
                string Where(HardpointSet s2) => string.Join(" ", s2.hardpoints.Where(h => h?.transform != null).Select(h => { var q = root.InverseTransformPoint(h.transform.position); return $"({q.x:0.0},{q.z:0.0})"; }));
                if (picks.Count == 0 || picks.Any(p => p < 0)) { setLog.Add($"#{i} x{set.hardpoints.Count} at {Where(set)} closed (no free design hardpoint on that side)"); Close(set); continue; }
                setLog.Add($"#{i} x{set.hardpoints.Count} at {Where(set)} -> " + string.Join(" ", picks.Select(k => $"({external[k].x:0.0},{external[k].z:0.0})")));
                for (int s = 0; s < set.hardpoints.Count; s++)
                {
                    var hp = set.hardpoints[s];
                    int k = picks[s];
                    taken[k] = true;
                    var target = root.TransformPoint(external[k]);
                    var delta = target - hp.transform.position;
                    foreach (var r in PylonRenderers(hp))
                        if (!r.transform.IsChildOf(hp.transform)) r.transform.position += delta;
                    hp.transform.position = target;
                    // The store's weight hangs on the part nearest its new station, and moves with it.
                    var near = parts.OrderBy(p => (PartCentre(root, p) - external[k]).sqrMagnitude).FirstOrDefault();
                    if (near != null && near != hp.part)
                    {
                        hp.part = near;
                        hp.transform.SetParent(near.transform, true);
                    }
                    movedStations++;
                }
                offered++;
            }

            // Built-in guns to the design's guns (only gun objects that aren't parts themselves).
            notes.Add("weapon stations: " + string.Join("; ", setLog) + $"; design hardpoints {string.Join(" ", external.Select(e => $"({e.x:0.0},{e.z:0.0})"))}");
            var gunPoints = design.Guns.Select(g => toRoot(g.Position)).ToList();
            int gunsMoved = 0;
            var gunRates = new List<string>();
            if (gunPoints.Count > 0)
                foreach (var set in sets)
                    if (set != null && IsGunSet(set))
                        foreach (var hp in set.hardpoints)
                            foreach (var w in hp?.BuiltInWeapons ?? new Weapon[0])
                            {
                                if (w == null) continue;
                                var cur = root.InverseTransformPoint(w.transform.position);
                                int bestIndex = Enumerable.Range(0, gunPoints.Count)
                                    .OrderBy(k => (Mathf.Abs(gunPoints[k].x) < 0.35f || Mathf.Abs(cur.x) < 0.35f || Mathf.Sign(gunPoints[k].x) == Mathf.Sign(cur.x) ? 0 : 1000) + (gunPoints[k] - cur).sqrMagnitude).First();
                                var best = gunPoints[bestIndex];
                                if (w.GetComponent<UnitPart>() == null && w.GetComponentInChildren<UnitPart>(true) == null)
                                {
                                    w.transform.position = root.TransformPoint(best);
                                    gunsMoved++;
                                }
                                else if (w is Gun gun && gun.muzzles != null && gun.muzzles.Length > 0)
                                {
                                    // A gun built into a part (the KR-67's sits in its cockpit section) can't move without the part:
                                    // its muzzles move instead, keeping their spacing, so the rounds leave from the design's gun.
                                    var centre = gun.muzzles.Where(m => m != null).Aggregate(Vector3.zero, (s, m) => s + m.position) / Mathf.Max(1, gun.muzzles.Count(m => m != null));
                                    var delta = root.TransformPoint(best) - centre;
                                    foreach (var m in gun.muzzles) if (m != null) m.position += delta;
                                    gunsMoved++;
                                }
                                // The design's rate of fire (rounds a minute), within a quarter and four times the stock gun's.
                                var gd = design.Guns[bestIndex];
                                if (w is Gun g2 && gd.RoundsPerMinute > 0)
                                {
                                    var fr = HarmonyLib.Traverse.Create(g2).Field("fireRate");
                                    float stockRate = fr.GetValue<float>();
                                    if (stockRate > 0)
                                    {
                                        float rate = Mathf.Clamp((float)gd.RoundsPerMinute, stockRate * 0.25f, stockRate * 4f);
                                        fr.SetValue(rate);
                                        gunRates.Add($"{stockRate:0}->{rate:0} rounds/min");
                                    }
                                }
                            }

            // Stores too heavy for the plane's size are no longer offered (a plane a third of the weight carries lighter ones).
            // Within 10 % of the stock plane's weight it carries everything the stock plane does.
            if (ratio < 0.9f)
            {
                float keep = Mathf.Sqrt(ratio);
                foreach (var set in sets)
                {
                    if (set?.weaponOptions == null || IsGunSet(set) || set.weaponOptions.Count <= 1) continue;
                    float heaviest = set.weaponOptions.Select(MountMass).DefaultIfEmpty(0).Max();
                    var kept = set.weaponOptions.Where(m => m == null || MountMass(m) <= heaviest * keep + 0.5f).ToList();
                    heavyDropped += set.weaponOptions.Count - kept.Count;
                    set.weaponOptions = kept;
                }
            }

            float capacity = stockMax * ratio;
            WeaponCapacity[def] = capacity;
            // Hardpoints a player can hang weapons on (open station sets, guns not counted), for the host's limit.
            if (Facts.TryGetValue(def, out var facts))
                facts.Hardpoints = sets.Where(s => s?.hardpoints != null && !IsGunSet(s) && s.weaponOptions != null && s.weaponOptions.Any(m => m != null))
                                       .Sum(s => s.hardpoints.Count(h => h != null));

            // The AI's preset loadouts, trimmed the same way (new objects: the stock plane's presets are never touched).
            var ps = def.aircraftParameters;
            int presetsTrimmed = 0;
            if (ps != null && ps != stockParams && ps.StandardLoadouts != null)
                ps.StandardLoadouts = ps.StandardLoadouts.Where(sl => sl != null).Select(sl =>
                {
                    var copy = new Loadout { weapons = new List<WeaponMount>(sl.loadout?.weapons ?? new List<WeaponMount>()) };
                    if (TrimLoadout(copy, sets, capacity) > 0) presetsTrimmed++;
                    return new StandardLoadout { disabled = sl.disabled, Name = sl.Name, FuelRatio = sl.FuelRatio, loadout = copy };
                }).ToArray();
            // The default loadouts too: loadouts[1] is what a plane spawned with an empty loadout gets, and what the loadout
            // screen starts from. Same length, so index 1 stays the default.
            if (ps != null && ps != stockParams && ps.loadouts != null)
                ps.loadouts = ps.loadouts.Select(lo =>
                {
                    if (lo == null) return lo!;
                    var copy = new Loadout { weapons = new List<WeaponMount>(lo.weapons ?? new List<WeaponMount>()) };
                    if (TrimLoadout(copy, sets, capacity) > 0) presetsTrimmed++;
                    return copy;
                }).ToList();

            notes.Add($"weapons: {movedStations} stations moved onto {external.Count} design hardpoints, {offered} station sets offered, {closed} closed, " +
                      $"{gunsMoved + gunStationsMoved} guns moved" + (gunRates.Count > 0 ? $" (rate of fire {string.Join(", ", gunRates)})" : "") + $", {heavyDropped} heavy options dropped, carries {capacity:0} kg (stock {stockMax:0} kg), {presetsTrimmed} AI presets trimmed");
            if (external.Count == 0 && !anyInternal)
                Change("It has no hardpoints, so it carries no bombs or missiles (only its guns). Add hardpoints in the builder to arm it.");
            else
                Change($"Its weapons hang on the hardpoints you placed: {offered} of the {_donorName}'s station sets moved there" +
                       (closed > 0 ? $"; {closed} that had no hardpoint for them aren't offered" : "") + ".");
            if (ratio < 0.95f)
                Change($"It carries up to {capacity:0} kg of weapons (the {_donorName} carries {stockMax:0} kg; scaled with its weight), and stores too heavy for it aren't offered.");
        }

        private static void TuneParameters(AircraftDefinition def, AircraftDefinition donor, AircraftDesign design, FlightReport flight, List<string> notes)
        {
            // Encyclopedia and loadout numbers (weights, stall speed) from the design. The AI's landing speed already scales with
            // sqrt(mass / aircraftInfo.maxWeight), so maxWeight scales with the empty weight to keep that ratio the stock one.
            var info = def.aircraftInfo;
            if (info != null && donor.aircraftInfo != null && Positive(flight.EmptyMassKg))
            {
                float scale = donor.aircraftInfo.emptyWeight > 0 ? (float)(flight.EmptyMassKg / donor.aircraftInfo.emptyWeight)
                            : donor.mass > 0 ? (float)(flight.EmptyMassKg / donor.mass) : 1f;
                if (scale > 0 && !float.IsInfinity(scale))
                {
                    info.emptyWeight = (float)flight.EmptyMassKg;
                    info.maxWeight = donor.aircraftInfo.maxWeight > 0 ? donor.aircraftInfo.maxWeight * scale : (float)flight.GrossMassKg * 1.5f;
                }
                if (Positive(flight.StallSpeedMs)) info.stallSpeed = (float)(flight.StallSpeedMs * 3.6);   // km/h, as the game stores it
            }

            var p = def.aircraftParameters;
            if (p == null) return;
            // AI speeds scale with the square root of wing loading compared with the stock plane. (With maxWeight scaled above,
            // the game's own sqrt(mass / maxWeight) factor only follows how loaded the plane is, as for the stock plane, so
            // the design's weight is counted once, here.)
            double stockArea = donor.unitPrefab.GetComponentsInChildren<AeroPart>(true).Sum(a => (double)Mathf.Max(0, a.wingArea));
            if (stockArea > 0 && Positive(flight.WingAreaM2) && donor.mass > 0 && Positive(flight.GrossMassKg))
            {
                double ratio = (flight.GrossMassKg / flight.WingAreaM2) / (donor.mass * 1.3 / stockArea);
                float k = Mathf.Clamp((float)System.Math.Sqrt(ratio), 0.6f, 1.6f);
                if (!float.IsNaN(k))
                {
                    p.takeoffSpeed *= k; p.approachSpeed *= k; p.landingSpeed *= k; p.shortLandingSpeed *= k; p.cornerSpeed *= k;
                    // The AI lays out its landing pattern and pull-outs from turningRadius; a plane with higher wing loading turns
                    // wider (radius grows with speed squared), so it gets more room. Never less than the stock plane's.
                    if (p.turningRadius > 0) p.turningRadius *= Mathf.Max(1f, k * k);
                    notes.Add($"AI speeds x{k:0.00}");
                }

                // Runway this plane needs, for choosing which hangars offer it: the design's own takeoff run (the same estimate
                // the app shows), never less than the stock plane's setting. Only the hangar patch uses it. The AI's own
                // takeoffDistance stays the stock value: the AI ejects when no runway on the map is that long.
                double run = GameEstimates.RunwayNeededM(flight.StallSpeedMs, flight.ThrustToWeight);
                float need = Mathf.Max(p.takeoffDistance, (float)run);
                if (need > 0 && !float.IsNaN(need) && !float.IsInfinity(need))
                {
                    RunwayNeeded[def] = need;
                    if (_report != null) _report.RunwayNeededM = System.Math.Round(need);
                    notes.Add($"needs about {need:0} m of runway (own run {run:0} m, stock setting {p.takeoffDistance:0} m)");
                    if (need > GameEstimates.LongestRunwayM)
                        Warn($"It needs about {need:0} m of runway, longer than any runway in the game, so no airbase offers it. More thrust, less weight or more wing area shortens its takeoff run.");
                }
            }
            if (design.Handling.GLimit > 3) p.aircraftGLimit = Mathf.Clamp((float)design.Handling.GLimit, 4f, 12f);
            TuneFlyByWire(def.unitPrefab, design, notes);
        }

        /// <summary>
        /// The design's g limit and angle-of-attack limit on the copy's fly-by-wire (ControlsFilter.FlyByWire: gLimitPositive,
        /// what full stick asks for; alphaLimiter, where it starts easing the pull). 0 keeps the stock plane's.
        /// </summary>
        private static void TuneFlyByWire(GameObject prefab, AircraftDesign design, List<string> notes)
        {
            double g = design.Handling.GLimit, aoa = design.Handling.AoaLimitDeg;
            if (!(g > 3) && !(aoa > 0)) return;
            var cf = prefab.GetComponentInChildren<ControlsFilter>(true);
            var fbw = cf == null ? null : HarmonyLib.Traverse.Create(cf).Field("flyByWire").GetValue();
            if (fbw == null) { notes.Add("fly-by-wire: none on the stock plane, limits not set"); return; }
            var t = HarmonyLib.Traverse.Create(fbw);
            var parts = new List<string>();
            var said = new List<string>();
            if (g > 3)
            {
                float before = t.Field("gLimitPositive").GetValue<float>(), set = Mathf.Clamp((float)g, 4f, 12f);
                t.Field("gLimitPositive").SetValue(set);
                parts.Add($"g limit {before:0.#}->{set:0.#}");
                said.Add($"{set:0.#} g");
            }
            if (aoa > 0)
            {
                float before = t.Field("alphaLimiter").GetValue<float>(), set = Mathf.Clamp((float)aoa, 10f, 60f);
                t.Field("alphaLimiter").SetValue(set);
                parts.Add($"angle-of-attack limit {before:0.#}->{set:0.#} deg");
                said.Add($"{set:0.#}° angle of attack");
            }
            notes.Add("fly-by-wire: " + string.Join(", ", parts));
            Change($"Its fly-by-wire keeps to the limits set in the builder: {string.Join(" and ", said)}.");
        }

        /// <summary>Numbers the host's multiplayer rules check (see <see cref="HostRules"/>).</summary>
        internal sealed class PlaneFacts
        {
            /// <summary>Design thrust over weight with full fuel and no weapons, as the app shows it.</summary>
            public float ThrustToWeight;
            /// <summary>Weapon hardpoints the plane offers in the game (open station sets; guns not counted).</summary>
            public int Hardpoints;
        }

        internal static readonly Dictionary<AircraftDefinition, PlaneFacts> Facts = new Dictionary<AircraftDefinition, PlaneFacts>();

        /// <summary>Estimated runway each OpenPlane plane needs (m), used only to choose the hangars that offer it.</summary>
        internal static readonly Dictionary<AircraftDefinition, float> RunwayNeeded = new Dictionary<AircraftDefinition, float>();

        /// <summary>The copy's engine output compared with the stock plane's: jet thrust if the donor has jets, else turbine power.</summary>
        private static float ThrustScale(AircraftDefinition def, AircraftDefinition donor)
        {
            float jets(GameObject g) => g.GetComponentsInChildren<Turbojet>(true).Sum(j => Mathf.Max(0, j.maxThrust));
            float turbines(GameObject g) => g.GetComponentsInChildren<TurbineEngine>(true).Sum(t => Mathf.Max(0, t.maxPower));
            float dj = jets(donor.unitPrefab), dt = turbines(donor.unitPrefab);
            if (dj > 0) return jets(def.unitPrefab) / dj;
            if (dt > 0) return turbines(def.unitPrefab) / dt;
            return 1f;
        }
    }
}
