using System.Collections.Generic;
using OpenPlane.Core.Design;

namespace OpenPlane.Core.Builder
{
    // The builder's own description of a plane: the numbers a player edits (wing span, chords, fuselage rings, ...).
    // DesignGenerator turns it into an AircraftDesign plus meshes. It is saved as builder.json inside the .oplane file
    // so the plane reopens with the same editable parts; the game only reads aircraft.json and meshes.json.
    // Units and axes are the same as the design: metres, degrees, Unity axes (x right, y up, z forward).

    /// <summary>An editable plane. Saved as builder.json inside a .oplane file.</summary>
    public sealed class PlaneSpec
    {
        public const string CurrentFormat = "openplane-builder/2";
        public const string FormatV1 = "openplane-builder/1";

        public string Format { get; set; } = CurrentFormat;
        /// <summary>The plane's stable id (aircraft.json "id"), kept across saves; new for copies. Null until first saved.</summary>
        public string? Id { get; set; }
        public string Name { get; set; } = "Untitled plane";
        public string Author { get; set; } = "";
        public string? Description { get; set; }
        public DesignOrigin? Origin { get; set; }

        /// <summary>"auto" (picked from the plane's engines and size) or a stock Nuclear Option aircraft key.</summary>
        public string Donor { get; set; } = DonorPicker.Auto;

        public List<FuselageSpec> Fuselages { get; set; } = new List<FuselageSpec>();
        public List<WingSpec> Wings { get; set; } = new List<WingSpec>();
        public List<EngineSpec> Engines { get; set; } = new List<EngineSpec>();
        public List<GearSpec> Gear { get; set; } = new List<GearSpec>();
        public List<HardpointSpec> Hardpoints { get; set; } = new List<HardpointSpec>();
        public List<GunSpec> Guns { get; set; } = new List<GunSpec>();
        /// <summary>Free-form body parts (canopies, intakes, pods, fairings). Saved as fuselage meshes, so they count as body.</summary>
        public List<ShapeSpec> Shapes { get; set; } = new List<ShapeSpec>();
        /// <summary>Blueprint pictures shown behind the plane in the builder. Builder-only; the game never sees them.</summary>
        public List<ReferenceImageSpec> References { get; set; } = new List<ReferenceImageSpec>();
        /// <summary>What the builder's view hides or locks. Viewport-only; never changes the design.</summary>
        public EditorState Editor { get; set; } = new EditorState();
        public CockpitDef? Cockpit { get; set; }
        public SensorSet Sensors { get; set; } = new SensorSet();
        public HandlingDef Handling { get; set; } = new HandlingDef();
        public MassDef Mass { get; set; } = new MassDef();
        public PaintDef Paint { get; set; } = new PaintDef();
        /// <summary>The plane's palette: paints, glass and lights. Parts pick one by its id.</summary>
        public List<MaterialDef> Materials { get; set; } = new List<MaterialDef>();
        public List<InstrumentDef> Instruments { get; set; } = new List<InstrumentDef>();
        public List<LightDef> Lights { get; set; } = new List<LightDef>();
        public bool ShouldSerializeMaterials() => Materials != null && Materials.Count > 0;
        public bool ShouldSerializeInstruments() => Instruments != null && Instruments.Count > 0;
        public bool ShouldSerializeLights() => Lights != null && Lights.Count > 0;

        /// <summary>SHA-1 of the aircraft.json this spec produced when saved; used to spot files changed by other tools.</summary>
        public string? DesignHash { get; set; }

        /// <summary>True for builder data this version can open (format 1 or 2).</summary>
        public static bool IsKnownFormat(string? format) => format == CurrentFormat || format == FormatV1;

        /// <summary>
        /// Brings a spec read from an older builder.json (or a draft) up to the current format: v1 becomes v2 (it has no
        /// shapes, references or editor state; every other v2 field defaults to v1's behaviour) and missing lists are filled.
        /// </summary>
        public static PlaneSpec Upgrade(PlaneSpec s)
        {
            if (s.Format == FormatV1 || string.IsNullOrEmpty(s.Format)) s.Format = CurrentFormat;
            s.Fuselages ??= new List<FuselageSpec>();
            s.Wings ??= new List<WingSpec>();
            s.Engines ??= new List<EngineSpec>();
            s.Gear ??= new List<GearSpec>();
            s.Hardpoints ??= new List<HardpointSpec>();
            s.Guns ??= new List<GunSpec>();
            s.Shapes ??= new List<ShapeSpec>();
            s.References ??= new List<ReferenceImageSpec>();
            s.Editor ??= new EditorState();
            s.Materials ??= new List<MaterialDef>();
            s.Instruments ??= new List<InstrumentDef>();
            s.Lights ??= new List<LightDef>();
            s.Editor.Hidden ??= new List<string>();
            s.Editor.Locked ??= new List<string>();
            foreach (var f in s.Fuselages) { f.RotationDeg ??= new double[3]; f.Rings ??= new List<FuselageRing>(); }
            foreach (var w in s.Wings) w.Surfaces ??= new List<SurfaceSpec>();
            foreach (var sh in s.Shapes)
            {
                sh.Position ??= new double[3]; sh.RotationDeg ??= new double[3]; sh.Scale ??= new double[] { 1, 1, 1 };
                sh.Vertices ??= new List<double[]>(); sh.Faces ??= new List<int[]>();
            }
            return s;
        }
    }

    /// <summary>A fuselage: either a loft through rings the player edits, or a fixed mesh (from a converted plane).</summary>
    public sealed class FuselageSpec
    {
        public const string LoftKind = "loft";
        public const string MeshKind = "mesh";

        public string Id { get; set; } = "";
        public string Name { get; set; } = "Fuselage";
        /// <summary>"loft" (rings) or "mesh" (a fixed shape).</summary>
        public string Kind { get; set; } = LoftKind;
        /// <summary>Offset of the whole body (aircraft space). Ring positions are relative to it.</summary>
        public double[] Position { get; set; } = new double[3];
        /// <summary>Also build a copy on the other side (x mirrored). Ignored when the body sits on the centre line.</summary>
        public bool Mirror { get; set; }

        /// <summary>Loft rings, nose first. At least two are needed.</summary>
        public List<FuselageRing> Rings { get; set; } = new List<FuselageRing>();
        /// <summary>Points around each ring (8 to 64).</summary>
        public int Sides { get; set; } = 24;

        /// <summary>
        /// Turn of the whole body: Euler degrees in Unity's order (about z, then x, then y). A loft turns about
        /// <see cref="Position"/>, a fixed mesh about the centre of its bounds.
        /// </summary>
        public double[] RotationDeg { get; set; } = new double[3];
        /// <summary>Loft only: smoothing along the body (0 to 3). Each level doubles the rings along a Catmull-Rom curve.</summary>
        public int Smooth { get; set; }

        /// <summary>Mesh kind: index into the plane's source meshes.</summary>
        public int MeshIndex { get; set; }
        /// <summary>Mesh kind: uniform scale about the mesh's centre.</summary>
        public double Scale { get; set; } = 1;
        /// <summary>Mesh kind: frontal area of the unscaled mesh (m^2), kept from the conversion.</summary>
        public double SourceFrontalArea { get; set; }
        public double SkinThickness { get; set; }
        /// <summary>Palette material id; null keeps the plane's paint (or, for a converted body, its own materials).</summary>
        public string? MaterialId { get; set; }
        /// <summary>A cockpit interior piece: seen from the cockpit, never counted as body for drag or weight.</summary>
        public bool Interior { get; set; }
        public bool ShouldSerializeInterior() => Interior;
        public int? SourcePartId { get; set; }
    }

    /// <summary>One cross-section of a lofted fuselage.</summary>
    public sealed class FuselageRing
    {
        /// <summary>Station along the nose axis (m, forward is positive).</summary>
        public double Z { get; set; }
        /// <summary>Vertical offset of the ring's centre (m).</summary>
        public double Y { get; set; }
        public double Width { get; set; } = 1;
        public double Height { get; set; } = 1;
        /// <summary>1 is round (an ellipse), 0 is a rounded box.</summary>
        public double Roundness { get; set; } = 1;
        /// <summary>
        /// A custom cross-section: one [dx, dy, dz] offset per point from the ring's centre (0, Y, Z) in the fuselage's own
        /// space, in the order of <see cref="FuselageLoft.RingPoints"/> (point 0 at the top, then towards +x). Null keeps the
        /// superellipse made from Width, Height and Roundness.
        /// </summary>
        public List<double[]>? Points { get; set; }
    }

    /// <summary>A wing, tailplane or fin. Either made from a few numbers ("wing") or kept as exact panels ("panels").</summary>
    public sealed class WingSpec
    {
        public const string WingKind = "wing";
        public const string PanelsKind = "panels";

        public string Id { get; set; } = "";
        public string Name { get; set; } = "Wing";
        public string Kind { get; set; } = WingKind;
        /// <summary>Build a matching left twin (x mirrored).</summary>
        public bool Mirror { get; set; } = true;

        /// <summary>Wing kind: the root's leading-edge point (aircraft space).</summary>
        public double[] Root { get; set; } = new double[3];
        /// <summary>Wing kind: length from root to tip of one panel (m).</summary>
        public double Span { get; set; } = 4;
        public double RootChord { get; set; } = 2;
        public double TipChord { get; set; } = 1;
        /// <summary>Sweep of the leading edge (degrees, positive sweeps the tip back).</summary>
        public double SweepDeg { get; set; }
        /// <summary>Upward tilt of the panel (degrees). For a fin it's the cant: how far it leans outwards from vertical.</summary>
        public double DihedralDeg { get; set; }
        /// <summary>Nose-up angle of the panel's chord (degrees).</summary>
        public double IncidenceDeg { get; set; }
        /// <summary>True for a fin (the panel stands up instead of reaching sideways).</summary>
        public bool Vertical { get; set; }
        public double ThicknessRatio { get; set; } = 0.12;
        /// <summary>Twist of the tip against the root (degrees; positive turns the tip nose-up, negative is washout).</summary>
        public double TwistDeg { get; set; }
        public List<SurfaceSpec> Surfaces { get; set; } = new List<SurfaceSpec>();
        /// <summary>Palette material id; null is the plane's paint.</summary>
        public string? MaterialId { get; set; }

        /// <summary>Panels kind: exact panels in aircraft space (the right-hand side when mirrored).</summary>
        public List<WingPanel> Panels { get; set; } = new List<WingPanel>();
        /// <summary>Panels kind: move (m).</summary>
        public double[] Offset { get; set; } = new double[3];
        /// <summary>Panels kind: uniform scale about the panels' centre.</summary>
        public double Scale { get; set; } = 1;
    }

    /// <summary>A control surface strip along a wing's trailing (or leading) edge.</summary>
    public sealed class SurfaceSpec
    {
        /// <summary>aileron, flap, elevator, rudder, elevon, flaperon, ruddervator, airbrake, slat or none.</summary>
        public string Type { get; set; } = "aileron";
        /// <summary>"trailing" or "leading" (slats are always on the leading edge).</summary>
        public string Edge { get; set; } = "trailing";
        /// <summary>Travel override (degrees); null uses the type's default.</summary>
        public double? MinDeg { get; set; }
        public double? MaxDeg { get; set; }
        public double? ServoDegPerSec { get; set; }
        /// <summary>Input override: gains as they apply to a panel on the +x side (its mirrored twin flips roll and yaw).
        /// Null uses the type's default inputs.</summary>
        public List<InputLink>? Inputs { get; set; }
        /// <summary>Where it starts and ends along the span (0 root, 100 tip).</summary>
        /// <summary>Palette material id; null uses the wing's.</summary>
        public string? MaterialId { get; set; }
        public double FromPct { get; set; } = 50;
        public double ToPct { get; set; } = 95;
        /// <summary>How much of the wing's chord it takes (percent).</summary>
        public double ChordPct { get; set; } = 25;
    }

    public sealed class EngineSpec
    {
        public const string Jet = "jet";
        public const string Prop = "prop";

        public string Id { get; set; } = "";
        public string Name { get; set; } = "Engine";
        /// <summary>"jet" or "prop".</summary>
        public string Type { get; set; } = Jet;
        /// <summary>The design's engine type for converted engines (turbofan, turbojet, ...); null picks one from Type.</summary>
        public string? SourceType { get; set; }
        /// <summary>turbojet, turbofan, ramjet, piston or electric; null keeps <see cref="Type"/> and SourceType.
        /// Ramjets are saved as turbojets (the game has no ramjet).</summary>
        public string? Variant { get; set; }
        public const string SizingThrust = "thrust";
        public const string SizingDesigned = "designed";
        /// <summary>"thrust": typed in. "designed": jets work it out from the diameter and turbine temperature.</summary>
        public string Sizing { get; set; } = SizingThrust;
        /// <summary>Designed jets: turbine inlet temperature (K).</summary>
        public double TurbineTempK { get; set; } = 1400;
        /// <summary>Designed jets: has an afterburner (55 % more thrust).</summary>
        public bool Afterburner { get; set; }
        public string? Title { get; set; }
        public double[] Position { get; set; } = new double[3];
        public bool Mirror { get; set; }
        /// <summary>Jet: dry thrust (kN).</summary>
        public double ThrustKn { get; set; } = 40;
        /// <summary>Jet: thrust with afterburner (kN); 0 means no afterburner.</summary>
        public double AfterburnerKn { get; set; }
        /// <summary>Jet: engine diameter (m).</summary>
        public double DiameterM { get; set; } = 0.9;
        /// <summary>Prop: engine power (kW).</summary>
        public double PowerKw { get; set; } = 600;
        /// <summary>Prop: propeller diameter (m).</summary>
        public double PropDiameterM { get; set; } = 2.4;
        /// <summary>True when the thrust numbers are estimates (converted planes) rather than typed by the player.</summary>
        public bool Estimated { get; set; }
        public int? SourcePartId { get; set; }
    }

    public sealed class GearSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Landing gear";
        /// <summary>nose, main, tail or other.</summary>
        public string Kind { get; set; } = "main";
        /// <summary>Where the strut is mounted (aircraft space).</summary>
        public double[] Position { get; set; } = new double[3];
        public bool Mirror { get; set; } = true;
        /// <summary>Mount to wheel axle (m).</summary>
        public double StrutLength { get; set; } = 1;
        public double WheelRadius { get; set; } = 0.3;
        public double Travel { get; set; } = 0.2;
        public bool Steerable { get; set; }
        public bool Brakes { get; set; } = true;
        public bool Retractable { get; set; } = true;
        public int? SourcePartId { get; set; }
    }

    public sealed class HardpointSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Hardpoint";
        public double[] Position { get; set; } = new double[3];
        public double[] Rotation { get; set; } = new double[] { 0, 0, 0, 1 };
        public bool Mirror { get; set; } = true;
        /// <summary>missile-ir, missile-radar, missile-ground, bomb, rockets (also tank, gun-pod from conversions).</summary>
        public List<string> Allows { get; set; } = new List<string>();
        public bool Internal { get; set; }
        public string OriginalPayload { get; set; } = "empty";
        public int? SourcePartId { get; set; }
    }

    public sealed class GunSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Gun";
        public double[] Position { get; set; } = new double[3];
        public double[] Direction { get; set; } = new double[] { 0, 0, 1 };
        public bool Mirror { get; set; }
        public double CaliberMm { get; set; } = 20;
        public double RoundsPerMinute { get; set; } = 6000;
        public double MuzzleVelocity { get; set; } = 1050;
        public int Barrels { get; set; } = 1;
        public int? SourcePartId { get; set; }
    }

    /// <summary>A free-form body part: a polygon mesh in its own space, moved, turned and scaled into the plane.</summary>
    public sealed class ShapeSpec
    {
        public const string Skin = "skin";
        public const string Glass = "glass";
        public const int MaxSubdivision = 3;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "Shape";
        public double[] Position { get; set; } = new double[3];
        /// <summary>Euler degrees in Unity's order (about z, then x, then y).</summary>
        public double[] RotationDeg { get; set; } = new double[3];
        public double[] Scale { get; set; } = new double[] { 1, 1, 1 };
        /// <summary>Also build a copy on the other side (x mirrored), unless the shape is its own mirror image.</summary>
        public bool Mirror { get; set; }
        /// <summary>"skin" or "glass" (glass is drawn as skin in the game for now).</summary>
        public string Material { get; set; } = Skin;
        /// <summary>Palette material id; when set it wins over <see cref="Material"/>.</summary>
        public string? MaterialId { get; set; }
        /// <summary>A cockpit interior piece (seat, panel, stick): seen from the cockpit, never counted as body for drag or weight.</summary>
        public bool Interior { get; set; }
        public bool ShouldSerializeInterior() => Interior;
        /// <summary>Catmull-Clark smoothing levels (0 to 3), applied to the saved plane too.</summary>
        public int Subdivision { get; set; }
        /// <summary>Corners in the shape's own space.</summary>
        public List<double[]> Vertices { get; set; } = new List<double[]>();
        /// <summary>Polygons (three or more corner indices), wound so cross(b - a, c - a) at the first corner points out of the shape.</summary>
        public List<int[]> Faces { get; set; } = new List<int[]>();
        /// <summary>Where it came from: box, cylinder, sphere, cone, capsule, obj or mesh.</summary>
        public string? Source { get; set; }
        public int? SourcePartId { get; set; }
    }

    /// <summary>A blueprint picture placed behind one of the flat views (builder-only, never in the game).</summary>
    public sealed class ReferenceImageSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Reference";
        /// <summary>The picture's file inside the .oplane: references/&lt;sha1&gt;.png, .jpg or .webp.</summary>
        public string Image { get; set; } = "";
        /// <summary>"top", "side" or "front".</summary>
        public string View { get; set; } = "top";
        /// <summary>Centre in the view's plane (m): top (x, z), side (z, y), front (x, y).</summary>
        public double[] Center { get; set; } = new double[2];
        /// <summary>Width of the picture in metres; its height follows the picture's shape.</summary>
        public double WidthM { get; set; } = 10;
        public double RotationDeg { get; set; }
        public double Opacity { get; set; } = 0.5;
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }
    }

    /// <summary>The builder view's own state: parts hidden or locked, as "kind:id" keys. Never changes the design.</summary>
    public sealed class EditorState
    {
        public List<string> Hidden { get; set; } = new List<string>();
        public List<string> Locked { get; set; } = new List<string>();
    }

    /// <summary>Something the builder wants the player to know about a part (not a flight check).</summary>
    public sealed class BuilderNote
    {
        /// <summary>"note" or "warning".</summary>
        public string Level { get; set; } = "note";
        public string Message { get; set; } = "";
        /// <summary>The part the note is about, when there is one.</summary>
        public string? ItemId { get; set; }
    }
}
