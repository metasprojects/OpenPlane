using System.Collections.Generic;
using System.Globalization;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Flyout
{
    /// <summary>
    /// One block of a Flyout craft file: a name line, then "{", key=value lines and nested blocks, then "}".
    /// Keys can repeat (for example several <c>response=</c> lines), so values are kept in order as a list.
    /// </summary>
    public sealed class FlyoutBlock
    {
        public FlyoutBlock(string name) { Name = name; }

        /// <summary>The block's name line, for example "Part", "*ProcWing2" or "Connection point". Empty for anonymous blocks.</summary>
        public string Name { get; }

        /// <summary>Values in file order. A bare word inside a block (no "=") is stored with a null value.</summary>
        public List<KeyValuePair<string, string?>> Values { get; } = new List<KeyValuePair<string, string?>>();

        public List<FlyoutBlock> Children { get; } = new List<FlyoutBlock>();

        /// <summary>The first value for <paramref name="key"/>, or null.</summary>
        public string? Get(string key)
        {
            foreach (var kv in Values)
                if (kv.Key == key) return kv.Value;
            return null;
        }

        /// <summary>Every value for <paramref name="key"/>, in file order.</summary>
        public IEnumerable<string> GetAll(string key)
        {
            foreach (var kv in Values)
                if (kv.Key == key && kv.Value != null) yield return kv.Value;
        }

        public bool Has(string key) => Get(key) != null;

        /// <summary>The first child block with this name, or null.</summary>
        public FlyoutBlock? Child(string name)
        {
            foreach (var c in Children)
                if (c.Name == name) return c;
            return null;
        }

        public IEnumerable<FlyoutBlock> ChildrenNamed(string name)
        {
            foreach (var c in Children)
                if (c.Name == name) yield return c;
        }

        public double GetDouble(string key, double fallback = 0)
            => FlyoutValues.TryDouble(Get(key), out var d) ? d : fallback;

        public int GetInt(string key, int fallback = 0)
            => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        public bool GetBool(string key, bool fallback = false)
        {
            var v = Get(key);
            if (v == null) return fallback;
            if (string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public Vec3 GetVec3(string key, Vec3 fallback = default)
            => FlyoutValues.TryVec3(Get(key), out var v) ? v : fallback;

        public double[] GetNumbers(string key)
            => FlyoutValues.Numbers(Get(key));

        public override string ToString() => $"{Name} ({Values.Count} values, {Children.Count} blocks)";
    }
}
