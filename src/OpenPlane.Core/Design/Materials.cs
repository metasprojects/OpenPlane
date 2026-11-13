using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenPlane.Core.Design
{
    /// <summary>
    /// A material in the plane's palette (<see cref="AircraftDesign.Materials"/>). Meshes, wing panels and control surfaces
    /// point at palette entries by index.
    /// </summary>
    public sealed class MaterialDef
    {
        public const string Paint = "paint";
        public const string Glass = "glass";
        public const string Light = "light";

        /// <summary>A stable id for the builder (palette entries are referenced by id there, by index in aircraft.json).</summary>
        public string? Id { get; set; }
        public string Name { get; set; } = "Paint";
        /// <summary>"paint" (solid), "glass" (see-through, its alpha is how clear it is) or "light" (glows in its colour).</summary>
        public string Kind { get; set; } = Paint;
        /// <summary>[r, g, b, a] in 0..1.</summary>
        public double[] Color { get; set; } = new double[] { 0.6, 0.62, 0.64, 1 };
        public double Metallic { get; set; }
        public double Smoothness { get; set; } = 0.3;
        /// <summary>The original surface finish when it had a texture OpenPlane approximates (Flyout's "CarbonFiber", "Fabric").</summary>
        public string? Finish { get; set; }

        public MaterialDef Clone() => new MaterialDef
        {
            Id = Id, Name = Name, Kind = Kind, Color = (double[])Color.Clone(), Metallic = Metallic, Smoothness = Smoothness, Finish = Finish,
        };

        /// <summary>The default see-through canopy glass.</summary>
        public static MaterialDef DefaultGlass() => new MaterialDef { Id = "glass", Name = "Glass", Kind = Glass, Color = new double[] { 0.55, 0.65, 0.72, 0.25 }, Metallic = 0.2, Smoothness = 0.95 };
    }

    /// <summary>
    /// A cockpit instrument OpenPlane builds and animates in the game: a dial gauge, an artificial horizon, a compass,
    /// a screen, a head-up display frame, a mirror or a lever. Flyout's own instrument models are never copied; OpenPlane
    /// draws its own at the same place and size.
    /// </summary>
    public sealed class InstrumentDef
    {
        public static readonly string[] Kinds = { "gauge", "attitude", "compass", "screen", "hud", "mirror", "lever" };
        /// <summary>What a gauge shows: airspeed, altitude, radar-altitude, vertical-speed, rpm, throttle, fuel, g, aoa, heading, clock.</summary>
        public static readonly string[] Readings = { "airspeed", "altitude", "radar-altitude", "vertical-speed", "rpm", "throttle", "fuel", "g", "aoa", "heading", "clock" };
        /// <summary>What a screen shows: a flight page (attitude, speed, altitude, heading, g), an engine page or a navigation page.</summary>
        public static readonly string[] ScreenPages = { "flight", "engine", "nav" };
        /// <summary>What moves a lever: the throttle, the landing gear or the stick (pitch and roll).</summary>
        public static readonly string[] LeverMotions = { "throttle", "gear", "stick" };

        /// <summary>True when <paramref name="reads"/> means something for an instrument of this kind.</summary>
        public static bool ReadsFits(string? kind, string? reads)
            => reads != null && (kind == "screen" ? System.Array.IndexOf(ScreenPages, reads) >= 0
                               : kind == "lever" ? System.Array.IndexOf(LeverMotions, reads) >= 0
                               : System.Array.IndexOf(Readings, reads) >= 0);

        public string? Id { get; set; }
        public string Name { get; set; } = "Gauge";
        public string Kind { get; set; } = "gauge";
        /// <summary>For gauges: what the needle shows (see <see cref="Readings"/>); for screens, the page they show
        /// (<see cref="ScreenPages"/>); for levers, what moves them (<see cref="LeverMotions"/>).</summary>
        public string? Reads { get; set; }
        /// <summary>The face's centre (aircraft space); the face looks along the rotation's forward axis.</summary>
        public double[] Position { get; set; } = new double[3];
        public double[] Rotation { get; set; } = new double[] { 0, 0, 0, 1 };
        /// <summary>Face diameter (gauges) or width (screens, mirrors), metres.</summary>
        public double Size { get; set; } = 0.08;
        /// <summary>Dial: the value at <see cref="MinDeg"/> and at <see cref="MaxDeg"/> (needle angles clockwise from 12 o'clock).</summary>
        public double Min { get; set; }
        public double Max { get; set; } = 1;
        public double MinDeg { get; set; } = 30;
        public double MaxDeg { get; set; } = 330;
        public int? SourcePartId { get; set; }
        /// <summary>Builder only: also place a copy on the other side (x mirrored).</summary>
        public bool Mirror { get; set; }
    }

    /// <summary>A light on the plane: navigation (steady), strobe or beacon (blinking), landing (bright, forward) or a strip.</summary>
    public sealed class LightDef
    {
        public static readonly string[] Kinds = { "nav", "strobe", "beacon", "landing", "strip" };

        public string? Id { get; set; }
        public string Name { get; set; } = "Light";
        public string Kind { get; set; } = "nav";
        public double[] Position { get; set; } = new double[3];
        public double[] Rotation { get; set; } = new double[] { 0, 0, 0, 1 };
        /// <summary>[r, g, b, a] in 0..1. Navigation lights: red on the left, green on the right, white at the tail.</summary>
        public double[] Color { get; set; } = new double[] { 1, 1, 1, 1 };
        /// <summary>Size of the lamp (m); a strip's length.</summary>
        public double Size { get; set; } = 0.12;
        public int? SourcePartId { get; set; }
        /// <summary>Builder only: also place a copy on the other side (x mirrored; red and green swap).</summary>
        public bool Mirror { get; set; }
    }

    /// <summary>How a design's meshes, wings and surfaces resolve to palette materials.</summary>
    public static class DesignMaterials
    {
        /// <summary>
        /// The material a triangle uses: its slot through the mesh's <see cref="MeshData.Materials"/> into the palette. Older
        /// files have no table: slot 0 is the plane's paint, any other slot is glass.
        /// </summary>
        public static MaterialDef ForSlot(AircraftDesign d, MeshData m, int slot)
        {
            if (m.Materials != null && slot >= 0 && slot < m.Materials.Length) return At(d, m.Materials[slot]);
            return slot == 0 ? PaintOf(d) : GlassOf(d);
        }

        /// <summary>The palette entry at <paramref name="index"/>, or the plane's paint when it isn't one.</summary>
        public static MaterialDef At(AircraftDesign d, int? index)
            => index.HasValue && index.Value >= 0 && d.Materials != null && index.Value < d.Materials.Count && d.Materials[index.Value] != null
                ? d.Materials[index.Value] : PaintOf(d);

        /// <summary>The plane's base paint (<see cref="PaintDef"/>) as a material.</summary>
        public static MaterialDef PaintOf(AircraftDesign d)
        {
            var p = d.Paint ?? new PaintDef();
            return new MaterialDef { Id = "paint", Name = "Paint", Color = p.BaseColor ?? new double[] { 0.6, 0.62, 0.64, 1 }, Metallic = p.Metallic, Smoothness = p.Smoothness };
        }

        /// <summary>The palette's first glass, or the default glass.</summary>
        public static MaterialDef GlassOf(AircraftDesign d)
            => d.Materials?.FirstOrDefault(m => m != null && m.Kind == MaterialDef.Glass) ?? MaterialDef.DefaultGlass();

        /// <summary>The index of a material equal to <paramref name="m"/> in the palette, adding it when there is none.</summary>
        public static int IndexOf(List<MaterialDef> palette, MaterialDef m)
        {
            for (int i = 0; i < palette.Count; i++)
            {
                var p = palette[i];
                if (p.Kind == m.Kind && p.Finish == m.Finish && Near(p.Metallic, m.Metallic) && Near(p.Smoothness, m.Smoothness)
                    && p.Color.Length == m.Color.Length && p.Color.Zip(m.Color, (a, b) => Near(a, b)).All(x => x))
                    return i;
            }
            palette.Add(m);
            return palette.Count - 1;
        }

        private static bool Near(double a, double b) => System.Math.Abs(a - b) < 0.004;
    }
}
