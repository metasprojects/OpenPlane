using System.Collections.Generic;
using System.Globalization;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Flyout
{
    /// <summary>Parses the value formats Flyout writes: numbers with invariant culture, comma vectors, RGBA(...).</summary>
    public static class FlyoutValues
    {
        /// <summary>A finite number (NaN and infinities are refused, so they never reach a design).</summary>
        public static bool TryDouble(string? s, out double value)
            => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>All numbers in a comma-separated value. Non-numbers are skipped.</summary>
        public static double[] Numbers(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new double[0];
            var text = s!.Trim();
            if (text.StartsWith("RGBA(", System.StringComparison.OrdinalIgnoreCase) && text.EndsWith(")"))
                text = text.Substring(5, text.Length - 6);
            var parts = text.Split(',');
            var list = new List<double>(parts.Length);
            foreach (var p in parts)
                if (TryDouble(p, out var d)) list.Add(d);
            return list.ToArray();
        }

        public static bool TryVec3(string? s, out Vec3 v)
        {
            var n = Numbers(s);
            if (n.Length >= 3) { v = new Vec3(n[0], n[1], n[2]); return true; }
            v = default; return false;
        }

        public static bool TryQuat(string? s, out Quat q)
        {
            var n = Numbers(s);
            if (n.Length >= 4) { q = new Quat(n[0], n[1], n[2], n[3]).Normalized; return true; }
            q = Quat.Identity; return false;
        }
    }
}
