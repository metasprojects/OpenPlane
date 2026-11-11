using System.Collections.Generic;

namespace OpenPlane.Core.Design
{
    // The .oplane format, version 1. Units: metres, degrees, newtons, kilograms, seconds.
    // Axes follow Unity (x right, y up, z forward). Points are [x, y, z] arrays in aircraft space,
    // rotations are [x, y, z, w] quaternions. Every list may be empty; null means "not known".

    /// <summary>The whole plane. Saved as aircraft.json inside a .oplane file.</summary>
    public sealed class AircraftDesign
    {
        public const string CurrentFormat = "openplane/1";

        public string Format { get; set; } = CurrentFormat;

        /// <summary>
        /// A stable id for this plane (optional): made when the plane is created, kept when it is edited, shared or
        /// imported, and new for copies. The game mod names the plane "op.&lt;id&gt;" (see <see cref="OplanePackage.GameKey"/>).
        /// </summary>
        public string? Id { get; set; }

        public string Name { get; set; } = "Untitled";
        public string Author { get; set; } = "";
        public string? Description { get; set; }

        /// <summary>Where the plane came from (a Flyout craft, or built in OpenPlane).</summary>
        public DesignOrigin? Origin { get; set; }

        /// <summary>
        /// The stock Nuclear Option aircraft (its jsonKey) whose cockpit, instruments, systems and weapon mounts are reused.
        /// The mod falls back to a sensible stock plane if this key does not exist.
        /// </summary>
        public string Donor { get; set; } = "Fighter1";

        public List<WingPanel> Wings { get; set; } = new List<WingPanel>();
        public List<FuselageBody> Fuselages { get; set; } = new List<FuselageBody>();
        public List<EngineDef> Engines { get; set; } = new List<EngineDef>();
        public List<GearDef> Gear { get; set; } = new List<GearDef>();
        public List<HardpointDef> Hardpoints { get; set; } = new List<HardpointDef>();
        public List<GunDef> Guns { get; set; } = new List<GunDef>();
        public CockpitDef? Cockpit { get; set; }
        public SensorSet Sensors { get; set; } = new SensorSet();
        public HandlingDef Handling { get; set; } = new HandlingDef();
        public MassDef Mass { get; set; } = new MassDef();
        public PaintDef Paint { get; set; } = new PaintDef();

        /// <summary>The plane's materials (paints, glass, lights). Meshes, panels and surfaces point at them by index.</summary>
        public List<MaterialDef> Materials { get; set; } = new List<MaterialDef>();
        /// <summary>Cockpit instruments OpenPlane draws and animates in the game.</summary>
        public List<InstrumentDef> Instruments { get; set; } = new List<InstrumentDef>();
        public List<LightDef> Lights { get; set; } = new List<LightDef>();

        // Left out of the file while empty, so planes without them stay exactly as before.
        public bool ShouldSerializeMaterials() => Materials != null && Materials.Count > 0;
        public bool ShouldSerializeInstruments() => Instruments != null && Instruments.Count > 0;
        public bool ShouldSerializeLights() => Lights != null && Lights.Count > 0;

        /// <summary>Numbers worked out from the shape (filled in by <see cref="DesignStats"/>); not authoritative input.</summary>
        public DesignStatsResult? Stats { get; set; }
    }

    public sealed class DesignOrigin
    {
        /// <summary>"flyout" or "openplane".</summary>
        public string Source { get; set; } = "openplane";
        public string? CraftName { get; set; }
        /// <summary>The person who built the original. Credit them when sharing.</summary>
        public string? Builder { get; set; }
        public string? SourceVersion { get; set; }
        public string? ConvertedWith { get; set; }
    }

    /// <summary>One flat wing panel (also used for tails and fins). Corners are in aircraft space.</summary>
    public sealed class WingPanel
    {
        public string Name { get; set; } = "";
        public int? SourcePartId { get; set; }
        public double[] RootLeading { get; set; } = new double[3];
        public double[] RootTrailing { get; set; } = new double[3];
        public double[] TipLeading { get; set; } = new double[3];
        public double[] TipTrailing { get; set; } = new double[3];
        /// <summary>The panel's lift direction (unit vector, aircraft space).</summary>
        public double[] Up { get; set; } = new double[] { 0, 1, 0 };
        public double RootTwistDeg { get; set; }
        public double TipTwistDeg { get; set; }
        /// <summary>Flyout's four raw thickness numbers (continuous between joined panels; unit not yet confirmed).</summary>
        public double[]? ThicknessRaw { get; set; }
        /// <summary>Thickness as a fraction of chord used for drag and stall estimates.</summary>
        public double ThicknessRatio { get; set; } = 0.10;
        public bool FuelTank { get; set; }
        /// <summary>Palette material (index into <see cref="AircraftDesign.Materials"/>); null is the plane's paint.</summary>
        public int? Material { get; set; }
        public List<SurfaceDef> Surfaces { get; set; } = new List<SurfaceDef>();
        /// <summary>
        /// Controls that turn the whole panel (an all-moving tail, a stabilator, a turning fin). Empty for a fixed panel. In
        /// Flyout these are panels on a hinge joint or surface motor driven by the controls.
        /// </summary>
        public List<InputLink> Inputs { get; set; } = new List<InputLink>();
        /// <summary>Travel of an all-moving panel (degrees), when it has inputs.</summary>
        public double MinDeg { get; set; }
        public double MaxDeg { get; set; }

        // A fixed panel (the usual case) writes none of the three, so files stay as they were before panels could move.
        public bool ShouldSerializeInputs() => Inputs != null && Inputs.Count > 0;
        public bool ShouldSerializeMinDeg() => ShouldSerializeInputs();
        public bool ShouldSerializeMaxDeg() => ShouldSerializeInputs();
    }

    /// <summary>A control surface, flap or slat attached to a wing panel's edge.</summary>
    public sealed class SurfaceDef
    {
        public int? SourcePartId { get; set; }
        /// <summary>aileron, elevator, rudder, flap, elevon, ruddervator, flaperon, airbrake, slat, fixed, other.</summary>
        public string Role { get; set; } = "fixed";
        /// <summary>"trailing" or "leading".</summary>
        public string Edge { get; set; } = "trailing";
        public double[] RootLeading { get; set; } = new double[3];
        public double[] RootTrailing { get; set; } = new double[3];
        public double[] TipLeading { get; set; } = new double[3];
        public double[] TipTrailing { get; set; } = new double[3];
        public double MinDeg { get; set; }
        public double MaxDeg { get; set; }
        public double ServoDegPerSec { get; set; } = 60;
        public List<InputLink> Inputs { get; set; } = new List<InputLink>();
        /// <summary>Palette material; null means the panel's.</summary>
        public int? Material { get; set; }
    }

    /// <summary>How much a control input moves something: gain is degrees at full input (sign gives direction).</summary>
    public sealed class InputLink
    {
        /// <summary>pitch, roll, yaw, flaps, brake, throttle, gear, or "axisN" for inputs not mapped yet.</summary>
        public string Axis { get; set; } = "";
        public double Gain { get; set; }
    }

    /// <summary>A fuselage or body piece. Its mesh is stored in meshes.json (index <see cref="MeshIndex"/>).</summary>
    public sealed class FuselageBody
    {
        public string Name { get; set; } = "";
        public int? SourcePartId { get; set; }
        public int MeshIndex { get; set; }
        public double[] BoundsMin { get; set; } = new double[3];
        public double[] BoundsMax { get; set; } = new double[3];
        /// <summary>Largest cross-section area seen front-on (m^2), used for drag.</summary>
        public double FrontalArea { get; set; }
        public double WettedArea { get; set; }
        /// <summary>Flyout's skin thickness (m), used for mass estimates.</summary>
        public double SkinThickness { get; set; }
    }

    public sealed class EngineDef
    {
        public int? SourcePartId { get; set; }
        /// <summary>turbofan, turbojet, piston-prop, turboprop, electric-prop, rotor, unknown.</summary>
        public string Type { get; set; } = "unknown";
        public string? Title { get; set; }
        public double[] Position { get; set; } = new double[3];
        /// <summary>Direction the engine pushes the plane (unit vector).</summary>
        public double[] ThrustDirection { get; set; } = new double[] { 0, 0, 1 };
        public double DiameterM { get; set; }
        /// <summary>Sea-level static thrust without afterburner (N). Estimated from the Flyout engine data.</summary>
        public double MaxThrustN { get; set; }
        /// <summary>With afterburner (N); 0 when there is none.</summary>
        public double AfterburnerThrustN { get; set; }
        public double? PowerKw { get; set; }
        public double? PropDiameterM { get; set; }
        /// <summary>True when the thrust numbers are estimates (always, for converted planes).</summary>
        public bool Estimated { get; set; } = true;
    }

    public sealed class GearDef
    {
        public int? SourcePartId { get; set; }
        /// <summary>Where the strut is mounted (aircraft space).</summary>
        public double[] Position { get; set; } = new double[3];
        public double StrutLength { get; set; }
        public double Travel { get; set; }
        public double WheelRadius { get; set; }
        public bool Steerable { get; set; }
        public bool Brakes { get; set; }
        public bool Retractable { get; set; } = true;
    }

    public sealed class HardpointDef
    {
        public int? SourcePartId { get; set; }
        public double[] Position { get; set; } = new double[3];
        public double[] Rotation { get; set; } = new double[] { 0, 0, 0, 1 };
        /// <summary>What hung there in the original: missile, rockets, bomb, tank, gun-pod, empty, other.</summary>
        public string OriginalPayload { get; set; } = "empty";
        /// <summary>Weapon kinds this station may carry in Nuclear Option (chosen from the original payload).</summary>
        public List<string> Allows { get; set; } = new List<string>();
        public bool Internal { get; set; }
    }

    public sealed class GunDef
    {
        public int? SourcePartId { get; set; }
        public double[] Position { get; set; } = new double[3];
        public double[] Direction { get; set; } = new double[] { 0, 0, 1 };
        public double CaliberMm { get; set; }
        public double RoundsPerMinute { get; set; }
        public double MuzzleVelocity { get; set; }
        public int Barrels { get; set; } = 1;
    }

    public sealed class CockpitDef
    {
        public double[] Position { get; set; } = new double[3];
        /// <summary>The pilot's eye (aircraft space); null means 0.7 m above <see cref="Position"/>.</summary>
        public double[]? Eye { get; set; }
        /// <summary>
        /// Fly from this cockpit in the game (the view moves to <see cref="Eye"/> and the plane's own interior shows). False keeps
        /// the stock plane's cockpit view. Null means yes.
        /// </summary>
        public bool? OwnView { get; set; }

        /// <summary>The eye position to use.</summary>
        public double[] EyeOrDefault() => Eye != null && Eye.Length == 3 ? Eye : new[] { Position[0], Position[1] + 0.7, Position[2] };
    }

    public sealed class SensorSet
    {
        public bool Radar { get; set; }
        public bool Irst { get; set; }
        public bool TargetingPod { get; set; }
        public bool RadarWarning { get; set; }
        public bool Countermeasures { get; set; }
        public bool Jammer { get; set; }
    }

    /// <summary>Fly-by-wire limits. Zero means "use the donor's value".</summary>
    public sealed class HandlingDef
    {
        public double GLimit { get; set; }
        public double AoaLimitDeg { get; set; }
        public double[]? FlyoutGain { get; set; }
        public double[]? FlyoutRateDamp { get; set; }
    }

    /// <summary>Mass. Flyout does not store totals, so converted planes leave these at 0 and the mod estimates them.</summary>
    public sealed class MassDef
    {
        public double EmptyKg { get; set; }
        public double FuelKg { get; set; }
        /// <summary>Centre of mass override (aircraft space); null means work it out.</summary>
        public double[]? CenterOfMass { get; set; }
    }

    public sealed class PaintDef
    {
        /// <summary>Base colour as [r, g, b, a] in 0..1.</summary>
        public double[] BaseColor { get; set; } = new double[] { 0.6, 0.62, 0.64, 1 };
        public double Metallic { get; set; }
        public double Smoothness { get; set; } = 0.3;
        /// <summary>Use the stock plane's livery (and Workshop liveries) instead of the plane's own colours.</summary>
        public bool StockLivery { get; set; }
        public bool ShouldSerializeStockLivery() => StockLivery;
    }

    public sealed class DesignStatsResult
    {
        public double SpanM { get; set; }
        public double LengthM { get; set; }
        public double HeightM { get; set; }
        /// <summary>Area of roughly horizontal lifting panels, including their control surfaces (m^2).</summary>
        public double WingAreaM2 { get; set; }
        /// <summary>Area of roughly vertical panels (fins), including rudders (m^2).</summary>
        public double FinAreaM2 { get; set; }
        public double TotalStaticThrustN { get; set; }
        public double[] BoundsMin { get; set; } = new double[3];
        public double[] BoundsMax { get; set; } = new double[3];
    }

    /// <summary>A triangle mesh in aircraft space. Saved in meshes.json.</summary>
    public sealed class MeshData
    {
        public string Name { get; set; } = "";
        /// <summary>x, y, z for each vertex.</summary>
        public float[] Vertices { get; set; } = new float[0];
        /// <summary>Three vertex indices per triangle, wound so normals point outwards.</summary>
        public int[] Triangles { get; set; } = new int[0];
        /// <summary>A material slot per triangle: an index into <see cref="Materials"/>. Without that table, 0 is the paint and any other slot glass.</summary>
        public int[] TriangleMaterials { get; set; } = new int[0];
        /// <summary>Palette material (index into <see cref="AircraftDesign.Materials"/>) for each slot; null for older files.</summary>
        public int[]? Materials { get; set; }
        /// <summary>"interior" for cockpit interior pieces (seen from inside the cockpit), null for the outside of the plane.</summary>
        public string? Role { get; set; }
        public const string Interior = "interior";
    }
}
