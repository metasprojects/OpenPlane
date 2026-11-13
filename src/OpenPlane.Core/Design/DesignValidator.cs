using System.Collections.Generic;
using System.IO;

namespace OpenPlane.Core.Design
{
    /// <summary>
    /// Checks a design and its meshes before they are used or written: every number finite, points with three numbers and
    /// rotations with four, meshes whose triangles point at real corners, and sizes within <see cref="OplanePackage.Limits"/>.
    /// Missing lists are filled in (empty). Problems throw <see cref="InvalidDataException"/> with a message for the player.
    /// </summary>
    public static class DesignValidator
    {
        public const int MaxWings = 4096, MaxSurfacesPerWing = 64, MaxFuselages = 4096, MaxMeshes = 4096;
        public const int MaxEngines = 256, MaxGear = 256, MaxHardpoints = 512, MaxGuns = 256;
        public const int MaxMaterials = 1024, MaxInstruments = 512, MaxLights = 512;
        /// <summary>No point of a plane is further than this from its origin (metres): a bigger number is a damaged file.</summary>
        public const double MaxCoordinate = 1000;
        private static readonly HashSet<string> Roles = new HashSet<string> { "aileron", "elevator", "rudder", "flap", "elevon", "ruddervator", "flaperon", "airbrake", "slat", "fixed", "other", "canard", "spoiler" };

        public static void Check(AircraftDesign d, IList<MeshData> meshes)
        {
            if (d == null) throw Bad("aircraft.json is empty");
            d.Wings ??= new List<WingPanel>();
            d.Fuselages ??= new List<FuselageBody>();
            d.Engines ??= new List<EngineDef>();
            d.Gear ??= new List<GearDef>();
            d.Hardpoints ??= new List<HardpointDef>();
            d.Guns ??= new List<GunDef>();
            d.Sensors ??= new SensorSet();
            d.Handling ??= new HandlingDef();
            d.Mass ??= new MassDef();
            d.Paint ??= new PaintDef();
            d.Materials ??= new List<MaterialDef>();
            d.Instruments ??= new List<InstrumentDef>();
            d.Lights ??= new List<LightDef>();
            d.Name ??= "Untitled";
            d.Author ??= "";
            d.Donor ??= "";

            Count("wings", d.Wings.Count, MaxWings);
            Count("fuselages", d.Fuselages.Count, MaxFuselages);
            Count("engines", d.Engines.Count, MaxEngines);
            Count("gear", d.Gear.Count, MaxGear);
            Count("hardpoints", d.Hardpoints.Count, MaxHardpoints);
            Count("guns", d.Guns.Count, MaxGuns);
            Count("meshes", meshes.Count, MaxMeshes);
            Count("materials", d.Materials.Count, MaxMaterials);
            Count("instruments", d.Instruments.Count, MaxInstruments);
            Count("lights", d.Lights.Count, MaxLights);
            for (int i = 0; i < d.Materials.Count; i++)
            {
                var m = d.Materials[i] ?? throw Bad($"materials[{i}] is empty");
                var at = $"materials[{i}]";
                if (m.Color == null || (m.Color.Length != 3 && m.Color.Length != 4)) throw Bad(at + ".color needs 3 or 4 numbers");
                Nums(m.Color, at + ".color");
                Num(m.Metallic, at + ".metallic"); Num(m.Smoothness, at + ".smoothness");
                m.Name ??= "Paint";
                if (m.Kind != MaterialDef.Glass && m.Kind != MaterialDef.Light) m.Kind = MaterialDef.Paint;
                m.Metallic = Clamp01(m.Metallic); m.Smoothness = Clamp01(m.Smoothness);
                for (int c = 0; c < m.Color.Length; c++) m.Color[c] = Clamp01(m.Color[c]);
            }
            for (int i = 0; i < d.Instruments.Count; i++)
            {
                var n = d.Instruments[i] ?? throw Bad($"instruments[{i}] is empty");
                var at = $"instruments[{i}]";
                Point(n.Position, at + ".position"); Vector(n.Rotation, 4, at + ".rotation");
                Num(n.Size, at + ".size"); Num(n.Min, at + ".min"); Num(n.Max, at + ".max"); Num(n.MinDeg, at + ".min_deg"); Num(n.MaxDeg, at + ".max_deg");
                if (System.Array.IndexOf(InstrumentDef.Kinds, n.Kind) < 0) n.Kind = "gauge";
                if (n.Reads != null && !InstrumentDef.ReadsFits(n.Kind, n.Reads)) n.Reads = null;
                n.Size = System.Math.Min(System.Math.Max(n.Size, 0.01), 2);
                n.Name ??= "Instrument";
            }
            for (int i = 0; i < d.Lights.Count; i++)
            {
                var l = d.Lights[i] ?? throw Bad($"lights[{i}] is empty");
                var at = $"lights[{i}]";
                Point(l.Position, at + ".position"); Vector(l.Rotation, 4, at + ".rotation");
                if (l.Color == null || (l.Color.Length != 3 && l.Color.Length != 4)) throw Bad(at + ".color needs 3 or 4 numbers");
                Nums(l.Color, at + ".color"); Num(l.Size, at + ".size");
                if (System.Array.IndexOf(LightDef.Kinds, l.Kind) < 0) l.Kind = "nav";
                l.Size = System.Math.Min(System.Math.Max(l.Size, 0.01), 5);
                l.Name ??= "Light";
            }

            for (int i = 0; i < d.Wings.Count; i++)
            {
                var w = d.Wings[i] ?? throw Bad($"wings[{i}] is empty");
                var at = $"wings[{i}]";
                Point(w.RootLeading, at + ".root_leading"); Point(w.RootTrailing, at + ".root_trailing");
                Point(w.TipLeading, at + ".tip_leading"); Point(w.TipTrailing, at + ".tip_trailing");
                Point(w.Up, at + ".up");
                if (System.Math.Abs(w.Up[0]) + System.Math.Abs(w.Up[1]) + System.Math.Abs(w.Up[2]) < 1e-9) w.Up = new double[] { 0, 1, 0 };   // no direction: lift straight up
                Num(w.RootTwistDeg, at + ".root_twist_deg"); Num(w.TipTwistDeg, at + ".tip_twist_deg");
                Num(w.ThicknessRatio, at + ".thickness_ratio");
                Nums(w.ThicknessRaw, at + ".thickness_raw");
                w.Surfaces ??= new List<SurfaceDef>();
                Count(at + ".surfaces", w.Surfaces.Count, MaxSurfacesPerWing);
                w.Inputs ??= new List<InputLink>();
                if (w.Inputs.Count > 32) throw Bad($"{at}.inputs has too many entries");
                for (int j = 0; j < w.Inputs.Count; j++)
                {
                    var link = w.Inputs[j] ?? throw Bad($"{at}.inputs[{j}] is empty");
                    link.Axis ??= "";
                    Num(link.Gain, $"{at}.inputs[{j}].gain");
                }
                Num(w.MinDeg, at + ".min_deg"); Num(w.MaxDeg, at + ".max_deg");
                if (w.MinDeg > w.MaxDeg) { var lo = w.MaxDeg; w.MaxDeg = w.MinDeg; w.MinDeg = lo; }
                for (int k = 0; k < w.Surfaces.Count; k++)
                {
                    var s = w.Surfaces[k] ?? throw Bad($"{at}.surfaces[{k}] is empty");
                    var sat = $"{at}.surfaces[{k}]";
                    s.Role = s.Role != null && Roles.Contains(s.Role) ? s.Role : s.Role == null ? "fixed" : "other";
                    s.Edge = s.Edge == "leading" ? "leading" : "trailing";
                    Point(s.RootLeading, sat + ".root_leading"); Point(s.RootTrailing, sat + ".root_trailing");
                    Point(s.TipLeading, sat + ".tip_leading"); Point(s.TipTrailing, sat + ".tip_trailing");
                    Num(s.MinDeg, sat + ".min_deg"); Num(s.MaxDeg, sat + ".max_deg"); Num(s.ServoDegPerSec, sat + ".servo_deg_per_sec");
                    if (s.MinDeg > s.MaxDeg) { var lo = s.MaxDeg; s.MaxDeg = s.MinDeg; s.MinDeg = lo; }
                    s.Inputs ??= new List<InputLink>();
                    if (s.Inputs.Count > 32) throw Bad($"{sat}.inputs has too many entries");
                    for (int j = 0; j < s.Inputs.Count; j++)
                    {
                        var link = s.Inputs[j] ?? throw Bad($"{sat}.inputs[{j}] is empty");
                        link.Axis ??= "";
                        Num(link.Gain, $"{sat}.inputs[{j}].gain");
                    }
                }
            }

            for (int i = 0; i < d.Fuselages.Count; i++)
            {
                var f = d.Fuselages[i] ?? throw Bad($"fuselages[{i}] is empty");
                var at = $"fuselages[{i}]";
                if (f.MeshIndex < 0 || f.MeshIndex >= meshes.Count) throw Bad($"{at}.mesh_index points at a mesh that isn't in meshes.json");
                Point(f.BoundsMin, at + ".bounds_min"); Point(f.BoundsMax, at + ".bounds_max");
                Num(f.FrontalArea, at + ".frontal_area"); Num(f.WettedArea, at + ".wetted_area"); Num(f.SkinThickness, at + ".skin_thickness");
            }

            for (int i = 0; i < d.Engines.Count; i++)
            {
                var e = d.Engines[i] ?? throw Bad($"engines[{i}] is empty");
                var at = $"engines[{i}]";
                e.Type ??= "unknown";
                Point(e.Position, at + ".position"); Point(e.ThrustDirection, at + ".thrust_direction");
                Num(e.DiameterM, at + ".diameter_m"); Num(e.MaxThrustN, at + ".max_thrust_n"); Num(e.AfterburnerThrustN, at + ".afterburner_thrust_n");
                if (e.PowerKw.HasValue) Num(e.PowerKw.Value, at + ".power_kw");
                if (e.PropDiameterM.HasValue) Num(e.PropDiameterM.Value, at + ".prop_diameter_m");
            }

            for (int i = 0; i < d.Gear.Count; i++)
            {
                var g = d.Gear[i] ?? throw Bad($"gear[{i}] is empty");
                var at = $"gear[{i}]";
                Point(g.Position, at + ".position");
                Num(g.StrutLength, at + ".strut_length"); Num(g.Travel, at + ".travel"); Num(g.WheelRadius, at + ".wheel_radius");
            }

            for (int i = 0; i < d.Hardpoints.Count; i++)
            {
                var h = d.Hardpoints[i] ?? throw Bad($"hardpoints[{i}] is empty");
                var at = $"hardpoints[{i}]";
                h.OriginalPayload ??= "empty";
                h.Allows ??= new List<string>();
                Point(h.Position, at + ".position");
                Vector(h.Rotation, 4, at + ".rotation");
            }

            for (int i = 0; i < d.Guns.Count; i++)
            {
                var g = d.Guns[i] ?? throw Bad($"guns[{i}] is empty");
                var at = $"guns[{i}]";
                Point(g.Position, at + ".position"); Point(g.Direction, at + ".direction");
                Num(g.CaliberMm, at + ".caliber_mm"); Num(g.RoundsPerMinute, at + ".rounds_per_minute"); Num(g.MuzzleVelocity, at + ".muzzle_velocity");
            }

            if (d.Cockpit != null) { Point(d.Cockpit.Position, "cockpit.position"); if (d.Cockpit.Eye != null) Point(d.Cockpit.Eye, "cockpit.eye"); }
            Num(d.Handling.GLimit, "handling.g_limit"); Num(d.Handling.AoaLimitDeg, "handling.aoa_limit_deg");
            Nums(d.Handling.FlyoutGain, "handling.flyout_gain"); Nums(d.Handling.FlyoutRateDamp, "handling.flyout_rate_damp");
            Num(d.Mass.EmptyKg, "mass.empty_kg"); Num(d.Mass.FuelKg, "mass.fuel_kg");
            if (d.Mass.CenterOfMass != null) Point(d.Mass.CenterOfMass, "mass.center_of_mass");
            if (d.Paint.BaseColor == null || (d.Paint.BaseColor.Length != 3 && d.Paint.BaseColor.Length != 4)) throw Bad("paint.base_color needs 3 or 4 numbers");
            Nums(d.Paint.BaseColor, "paint.base_color");
            Num(d.Paint.Metallic, "paint.metallic"); Num(d.Paint.Smoothness, "paint.smoothness");

            long vertices = 0, triangles = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                var m = meshes[i] ?? throw Bad($"meshes[{i}] is empty");
                var at = $"meshes[{i}]";
                m.Name ??= "";
                m.Vertices ??= new float[0];
                m.Triangles ??= new int[0];
                if (m.Vertices.Length % 3 != 0) throw Bad($"{at}.vertices doesn't hold whole points (its length isn't a multiple of 3)");
                if (m.Triangles.Length % 3 != 0) throw Bad($"{at}.triangles doesn't hold whole triangles (its length isn't a multiple of 3)");
                int count = m.Vertices.Length / 3;
                vertices += count; triangles += m.Triangles.Length / 3;
                if (vertices > OplanePackage.Limits.MaxTotalVertices || triangles > OplanePackage.Limits.MaxTotalTriangles)
                    throw new InvalidDataException($"This plane is too detailed for OpenPlane (it reads up to {OplanePackage.Limits.MaxTotalTriangles:N0} triangles and {OplanePackage.Limits.MaxTotalVertices:N0} corners in all).");
                foreach (var v in m.Vertices)
                {
                    if (float.IsNaN(v) || float.IsInfinity(v)) throw Bad($"{at}.vertices has a number that isn't finite");
                    if (System.Math.Abs(v) > MaxCoordinate) throw Bad($"{at}.vertices has a point more than {MaxCoordinate:0} m from the plane's centre");
                }
                foreach (var t in m.Triangles)
                    if (t < 0 || t >= count) throw Bad($"{at}.triangles points at a corner that doesn't exist");
                if (m.TriangleMaterials == null || m.TriangleMaterials.Length != m.Triangles.Length / 3)
                    m.TriangleMaterials = new int[m.Triangles.Length / 3];
                if (m.Materials != null && m.Materials.Length > MaxMaterials) throw Bad($"{at}.materials has too many entries");
                if (m.Role != null && m.Role != MeshData.Interior) m.Role = null;
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static InvalidDataException Bad(string what) => new InvalidDataException($"This plane file is damaged: {what}.");

        private static void Count(string what, int count, int max)
        {
            if (count > max) throw new InvalidDataException($"This plane has too many {what} ({count:N0}; OpenPlane reads up to {max:N0}).");
        }

        private static void Num(double v, string what)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) throw Bad($"{what} isn't a finite number");
        }

        private static void Nums(double[]? v, string what)
        {
            if (v == null) return;
            if (v.Length > 1024) throw Bad($"{what} has too many numbers");
            foreach (var x in v) Num(x, what);
        }

        private static void Point(double[]? v, string what)
        {
            Vector(v, 3, what);
            foreach (var x in v!) if (System.Math.Abs(x) > MaxCoordinate) throw Bad($"{what} is more than {MaxCoordinate:0} m from the plane's centre");
        }

        private static void Vector(double[]? v, int length, string what)
        {
            if (v == null || v.Length != length) throw Bad($"{what} needs exactly {length} numbers");
            foreach (var x in v) Num(x, what);
        }
    }
}
