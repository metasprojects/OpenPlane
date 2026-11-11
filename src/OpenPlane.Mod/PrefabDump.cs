using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>Developer aid: writes a stock aircraft prefab's structure (parts, aero values, renderers) to a text file.</summary>
    internal static class PrefabDump
    {
        private static readonly string[] Fields =
        {
            "wingArea", "dragArea", "centerOfLift", "airfoil", "mass", "liftNormal", "streamlining",
            "pitchRange", "rollRange", "yawRange", "brakeRange", "flap", "attachedSurface", "visibleMesh",
            "maxThrust", "maxPower", "power", "maxRPM", "thrust", "afterburner", "parentPart", "id",
        };

        public static void Dump(AircraftDefinition def, string path)
        {
            var sb = new StringBuilder();
            var root = def.unitPrefab.transform;
            sb.AppendLine($"# {def.jsonKey} ({def.unitName}) prefab {def.unitPrefab.name}; mass {def.mass}; length {def.length} width {def.width} height {def.height}");
            var ac = def.unitPrefab.GetComponent<Aircraft>();
            if (ac != null)
                sb.AppendLine($"cockpit part: {(ac.cockpit != null ? Path(root, ac.cockpit.transform) : "none")}; cockpitRenderers {ac.cockpitRenderers?.Length}; exteriorRenderers {ac.exteriorRenderers?.Length}");
            Walk(root, root, 0, sb);
            File.WriteAllText(path, sb.ToString());
        }

        private static void Walk(Transform root, Transform t, int depth, StringBuilder sb)
        {
            var pad = new string(' ', depth * 2);
            var lp = t.localPosition; var wp = root.InverseTransformPoint(t.position);
            sb.Append(pad).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [inactive]")
              .Append($"  local({lp.x:0.##},{lp.y:0.##},{lp.z:0.##}) root({wp.x:0.##},{wp.y:0.##},{wp.z:0.##})");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                var type = c.GetType();
                sb.Append(" | ").Append(type.Name);
                if (c is MeshFilter mf && mf.sharedMesh != null) sb.Append($"({mf.sharedMesh.name},{mf.sharedMesh.vertexCount}v)");
                if (c is Renderer r) sb.Append($"(mat {(r.sharedMaterial != null ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "none")}, b {r.bounds.size.x:0.#}x{r.bounds.size.y:0.#}x{r.bounds.size.z:0.#})");
                if (c is Rigidbody rb) sb.Append($"(mass {rb.mass})");
                if (type.Assembly.GetName().Name == "Assembly-CSharp")
                {
                    var vals = Fields.Select(f => (f, type.GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy) ?? FindField(type, f)))
                                     .Where(x => x.Item2 != null)
                                     .Select(x => x.f + "=" + Fmt(x.Item2!.GetValue(c)));
                    var s = string.Join(" ", vals);
                    if (s.Length > 0) sb.Append("{").Append(s).Append("}");
                }
            }
            sb.AppendLine();
            for (int i = 0; i < t.childCount; i++) Walk(root, t.GetChild(i), depth + 1, sb);
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            while (type != null)
            {
                var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                type = type.BaseType;
            }
            return null;
        }

        private static string Fmt(object? v)
        {
            switch (v)
            {
                case null: return "null";
                case Vector3 p: return $"({p.x:0.##},{p.y:0.##},{p.z:0.##})";
                case Component c: return c ? c.name : "null";
                case float f: return f.ToString("0.###");
                default: return v.ToString();
            }
        }

        private static string Path(Transform root, Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts);
        }
    }
}
