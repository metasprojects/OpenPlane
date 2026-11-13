using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Draws a design as it was made: its meshes and wings grouped by palette material (paint, glass, glowing lights), its
    /// interior pieces, its instruments (which work) and its lights. Materials are copies of the game's own URP materials,
    /// so they use shader variants the game ships.
    /// </summary>
    internal static partial class DesignVisuals
    {
        /// <summary>The design's meshes split by role, each with one submesh per material.</summary>
        internal sealed class Group
        {
            public Mesh? Mesh;
            /// <summary>For each submesh: the palette index (-1 the plane's paint, -2 old-style glass).</summary>
            public readonly List<int> Keys = new List<int>();
        }

        internal const int PaintKey = -1, LegacyGlassKey = -2;

        /// <summary>Faces meeting at more than this angle keep a hard edge; gentler ones are shaded smooth, as in Flyout.</summary>
        private const float CreaseDeg = 50f;

        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly Dictionary<int, List<int>> T = new Dictionary<int, List<int>>();
            public void Add(int key, Vector3 a, Vector3 b, Vector3 c)
            {
                if (!T.TryGetValue(key, out var list)) T[key] = list = new List<int>();
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c);
                list.Add(i); list.Add(i + 1); list.Add(i + 2);
            }

            /// <summary>
            /// Smooth normals with hard creases: each corner averages the faces around its point that turn less than
            /// <see cref="CreaseDeg"/> from its own face (a lofted fuselage shades round, a wing's edge stays sharp).
            /// </summary>
            private List<Vector3> Normals()
            {
                int corners = V.Count;
                var face = new Vector3[corners / 3];
                var unit = new Vector3[corners / 3];
                for (int t = 0; t < face.Length; t++)
                {
                    // Unity's front face winds clockwise; Cross(b - a, c - a) points out of it (as RecalculateNormals does).
                    face[t] = Vector3.Cross(V[t * 3 + 1] - V[t * 3], V[t * 3 + 2] - V[t * 3]);
                    unit[t] = face[t].sqrMagnitude > 1e-20f ? face[t].normalized : Vector3.zero;
                }
                var at = new Dictionary<(long, long, long), List<int>>();
                (long, long, long) Key(Vector3 p) => ((long)Math.Round(p.x * 2000), (long)Math.Round(p.y * 2000), (long)Math.Round(p.z * 2000));
                for (int i = 0; i < corners; i++)
                {
                    var k = Key(V[i]);
                    if (!at.TryGetValue(k, out var list)) at[k] = list = new List<int>();
                    list.Add(i);
                }
                float cos = Mathf.Cos(CreaseDeg * Mathf.Deg2Rad);
                var normals = new List<Vector3>(corners);
                for (int i = 0; i < corners; i++)
                {
                    var own = unit[i / 3];
                    var sum = Vector3.zero;
                    foreach (int j in at[Key(V[i])])
                        if (Vector3.Dot(unit[j / 3], own) >= cos) sum += face[j / 3];
                    normals.Add(sum.sqrMagnitude > 1e-20f ? sum.normalized : (own == Vector3.zero ? Vector3.up : own));
                }
                return normals;
            }

            public Group Build(string name, Bounds uvBounds)
            {
                var g = new Group();
                if (V.Count == 0) return g;
                var mesh = new Mesh { name = name };
                if (V.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(V);
                mesh.SetNormals(Normals());
                var size = uvBounds.size;
                var uv = new Vector2[V.Count];
                for (int i = 0; i < uv.Length; i++)
                    uv[i] = new Vector2(size.x > 0 ? (V[i].x - uvBounds.min.x) / size.x : 0, size.z > 0 ? (V[i].z - uvBounds.min.z) / size.z : 0);
                mesh.uv = uv;
                mesh.subMeshCount = T.Count;
                int s = 0;
                foreach (var kv in T.OrderBy(k => k.Key))
                {
                    mesh.SetTriangles(kv.Value, s++);
                    g.Keys.Add(kv.Key);
                }
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                g.Mesh = mesh;
                return g;
            }
        }

        /// <summary>
        /// A control surface (or an all-moving panel) drawn on its own so it can turn: its mesh in design space and its hinge
        /// line (design space). Key: "w{wing}" for a whole panel, "w{wing}s{surface}" for a strip.
        /// </summary>
        internal sealed class MovingPiece
        {
            public string Key = "";
            public Group Group = new Group();
            public Vector3 HingeA, HingeB;
        }

        /// <summary>The control surfaces and all-moving panels of the last <see cref="Build"/>, drawn apart from the wings.</summary>
        internal static List<MovingPiece> LastMoving = new List<MovingPiece>();

        /// <summary>Exterior body, interior pieces and wings, each grouped by material. Moving surfaces go to <see cref="LastMoving"/>.</summary>
        public static (Group body, Group interior, Group wings) Build(AircraftDesign design, List<MeshData> meshes)
        {
            var body = new Builder(); var interior = new Builder(); var wings = new Builder();
            var moving = new List<(string key, Builder b, Vector3 a, Vector3 c)>();
            foreach (var m in meshes)
            {
                var target = m.Role == MeshData.Interior ? interior : body;
                var v = m.Vertices; var t = m.Triangles;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int slot = m.TriangleMaterials != null && i / 3 < m.TriangleMaterials.Length ? m.TriangleMaterials[i / 3] : 0;
                    int key = m.Materials != null && slot >= 0 && slot < m.Materials.Length ? m.Materials[slot] : slot == 0 ? PaintKey : LegacyGlassKey;
                    Vector3 P(int k) => new Vector3(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
                    target.Add(key, P(t[i]), P(t[i + 1]), P(t[i + 2]));
                }
            }
            Vector3 V3(double[] p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
            for (int wi = 0; wi < design.Wings.Count; wi++)
            {
                var w = design.Wings[wi];
                int wk = w.Material ?? PaintKey;
                bool wholePanel = w.Inputs != null && w.Inputs.Count > 0;
                // An all-moving panel turns about its quarter-chord line, its strips with it.
                var panelB = wings;
                if (wholePanel)
                {
                    panelB = new Builder();
                    moving.Add(($"w{wi}", panelB, Vector3.Lerp(V3(w.RootLeading), V3(w.RootTrailing), 0.25f), Vector3.Lerp(V3(w.TipLeading), V3(w.TipTrailing), 0.25f)));
                }
                AddSlab(panelB, wk, w.RootLeading, w.RootTrailing, w.TipTrailing, w.TipLeading, w.Up);
                for (int si = 0; si < w.Surfaces.Count; si++)
                {
                    var s = w.Surfaces[si];
                    var target = panelB;
                    if (!wholePanel && s.Role != "fixed" && s.Inputs != null && s.Inputs.Count > 0)
                    {
                        // A hinged strip turns about its edge against the wing: its leading edge, or a slat's trailing edge.
                        target = new Builder();
                        bool lead = s.Edge == "leading";
                        moving.Add(($"w{wi}s{si}", target, V3(lead ? s.RootTrailing : s.RootLeading), V3(lead ? s.TipTrailing : s.TipLeading)));
                    }
                    AddSlab(target, s.Material ?? wk, s.RootLeading, s.RootTrailing, s.TipTrailing, s.TipLeading, w.Up);
                }
            }
            var all = new Bounds(); bool any = false;
            foreach (var b in new[] { body, interior, wings }.Concat(moving.Select(m => m.b)))
                foreach (var p in b.V) { if (!any) { all = new Bounds(p, Vector3.zero); any = true; } else all.Encapsulate(p); }
            LastMoving = moving.Select(m => new MovingPiece { Key = m.key, Group = m.b.Build($"{design.Name} surface {m.key} (OpenPlane)", all), HingeA = m.a, HingeB = m.c })
                               .Where(m => m.Group.Mesh != null).ToList();
            return (body.Build(design.Name + " (OpenPlane)", all), interior.Build(design.Name + " interior (OpenPlane)", all), wings.Build(design.Name + " wings (OpenPlane)", all));
        }

        /// <summary>Whether a palette key is drawn as the plane's paint (as opposed to glass or a light).</summary>
        public static bool IsPaint(AircraftDesign d, int key)
            => key != LegacyGlassKey && (key == PaintKey || DesignMaterials.At(d, key).Kind == MaterialDef.Paint);

        /// <summary>A mesh with only some of another's submeshes (sharing its points).</summary>
        public static Mesh Subset(Mesh source, IList<int> submeshes, string name)
        {
            var m = new Mesh { name = name, indexFormat = source.indexFormat };
            m.vertices = source.vertices;
            m.normals = source.normals;
            m.uv = source.uv;
            m.tangents = source.tangents;
            m.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++) m.SetTriangles(source.GetTriangles(submeshes[i]), i);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>The same surface seen from the other side (for the inside of the hull from the cockpit).</summary>
        public static Mesh Inside(Mesh source, string name)
        {
            var m = new Mesh { name = name, indexFormat = source.indexFormat };
            m.vertices = source.vertices;
            m.normals = source.normals.Select(n => -n).ToArray();
            m.uv = source.uv;
            var tris = new List<int>();
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var t = source.GetTriangles(s);
                for (int i = 0; i + 2 < t.Length; i += 3) { tris.Add(t[i]); tris.Add(t[i + 2]); tris.Add(t[i + 1]); }
            }
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A thin closed slab for a wing panel or surface, wound outwards.</summary>
        private static void AddSlab(Builder b, int key, double[] rl, double[] rt, double[] tt, double[] tl, double[] up)
        {
            Vector3 P(double[] p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
            var n = new Vector3((float)up[0], (float)up[1], (float)up[2]).normalized;
            var a = P(rl); var bb = P(rt); var c = P(tt); var d = P(tl);
            float chord = ((a - bb).magnitude + (d - c).magnitude) / 2;
            var h = n * Mathf.Clamp(chord * 0.04f, 0.015f, 0.25f);
            var corners = new[] { a + h, bb + h, c + h, d + h, a - h, bb - h, c - h, d - h };
            var centre = (a + bb + c + d) / 4;
            var faces = new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 }, new[] { 0, 3, 7, 4 }, new[] { 1, 5, 6, 2 }, new[] { 0, 4, 5, 1 }, new[] { 3, 2, 6, 7 } };
            foreach (var f in faces)
            {
                var q = f.Select(i => corners[i]).ToArray();
                var normal = Vector3.Cross(q[1] - q[0], q[2] - q[0]) + Vector3.Cross(q[2] - q[0], q[3] - q[0]);
                if (normal.sqrMagnitude < 1e-12f) continue;
                var mid = (q[0] + q[1] + q[2] + q[3]) / 4;
                bool outward = Vector3.Dot(normal, mid - centre) >= 0;
                if (outward) { b.Add(key, q[0], q[1], q[2]); b.Add(key, q[0], q[2], q[3]); }
                else { b.Add(key, q[0], q[2], q[1]); b.Add(key, q[0], q[3], q[2]); }
            }
        }

        // ---------------------------------------------------------------- materials

        /// <summary>Makes Unity materials for palette entries from the game's own materials found in the stock prefab.</summary>
        internal sealed class MaterialFactory
        {
            private readonly Material? _lit, _glass, _skin;
            private readonly Dictionary<string, Material> _made = new Dictionary<string, Material>();

            public MaterialFactory(GameObject prefab, Material? skin)
            {
                _skin = skin;
                var mats = prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials ?? new Material[0]).Where(m => m != null && m.shader != null).Distinct().ToList();
                _lit = mats.FirstOrDefault(m => m.shader.name == "Universal Render Pipeline/Lit" && m.renderQueue < 2500)
                       ?? mats.FirstOrDefault(m => m.shader.name.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0 && m.renderQueue < 2500);
                _glass = mats.FirstOrDefault(m => m.renderQueue >= 2900 && (m.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 || m.name.IndexOf("canopy", StringComparison.OrdinalIgnoreCase) >= 0))
                         ?? mats.FirstOrDefault(m => m.renderQueue >= 2900 && m.shader.name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0);
                if (_lit == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader != null) _lit = new Material(shader);
                }
            }

            public bool CanPaint => _lit != null;

            /// <summary>The material for a palette key. <paramref name="stockLivery"/> keeps the stock skin for paint.</summary>
            public Material For(AircraftDesign d, int key, bool stockLivery, bool twoSided)
            {
                var def = key == LegacyGlassKey ? DesignMaterials.GlassOf(d) : DesignMaterials.At(d, key);
                if (def.Kind == MaterialDef.Paint && (stockLivery || _lit == null) && _skin != null) return _skin;
                string id = $"{d.Id}|{key}|{twoSided}|{stockLivery}";
                if (_made.TryGetValue(id, out var made)) return made;
                Material m;
                var c = new Color((float)def.Color[0], (float)def.Color[1], (float)def.Color[2], def.Color.Length > 3 ? (float)def.Color[3] : 1f);
                if (def.Kind == MaterialDef.Glass)
                    m = Tint(c, def.Name, (float)def.Smoothness);
                else
                {
                    m = Plain($"OpenPlane {def.Name}", new Color(c.r, c.g, c.b, 1));
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", (float)def.Metallic);
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", (float)def.Smoothness);
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", (float)def.Smoothness);
                    if (def.Kind == MaterialDef.Light)
                    {
                        m.EnableKeyword("_EMISSION");
                        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b) * 4f);
                        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                    }
                    if (twoSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                }
                _made[id] = m;
                return m;
            }

            /// <summary>A copy of the game's lit material in a plain colour, without the stock textures.</summary>
            private Material Plain(string name, Color c)
            {
                var m = new Material(_lit ?? _skin!) { name = name };
                foreach (var tex in new[] { "_BaseMap", "_MainTex" }) if (m.HasProperty(tex)) m.SetTexture(tex, Texture2D.whiteTexture);
                foreach (var tex in new[] { "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap", "_DetailAlbedoMap", "_DetailNormalMap" }) if (m.HasProperty(tex)) m.SetTexture(tex, null);
                foreach (var kw in new[] { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP", "_DETAIL_MULX2" }) m.DisableKeyword(kw);
                SetColor(m, c);
                return m;
            }

            /// <summary>See-through tinted glass (canopies, HUD combiners); alpha is the opacity.</summary>
            public Material Tint(Color c, string name = "glass", float smoothness = 0.95f)
            {
                string id = $"tint|{c}|{smoothness}";
                if (_made.TryGetValue(id, out var made)) return made;
                Material m;
                if (_glass != null)
                {
                    m = new Material(_glass) { name = $"OpenPlane glass {name}" };
                    SetColor(m, c);
                }
                else
                {
                    m = Plain($"OpenPlane glass {name}", c);
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.1f);
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
                    MakeTransparent(m);
                }
                _made[id] = m;
                return m;
            }

            /// <summary>A faint see-through colour (a spinning propeller's blur): the plain lit material, made transparent.</summary>
            public Material Haze(Color c)
            {
                string id = $"haze|{c}";
                if (_made.TryGetValue(id, out var made)) return made;
                var m = Plain("OpenPlane haze", c);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                MakeTransparent(m);
                if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                m.SetOverrideTag("RenderType", "Transparent");
                m.DisableKeyword("_ALPHATEST_ON");
                _made[id] = m;
                return m;
            }

            /// <summary>A glowing material in a colour (lights, instrument needles and faces).</summary>
            public Material Glow(Color c, float strength = 3f)
            {
                string id = $"glow|{c}|{strength}";
                if (_made.TryGetValue(id, out var made)) return made;
                var m = Plain("OpenPlane light", c);
                m.EnableKeyword("_EMISSION");
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * strength);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                _made[id] = m;
                return m;
            }

            /// <summary>A plain solid colour (instrument bodies, the inside of the hull).</summary>
            public Material Solid(Color c, float metallic = 0.2f, float smooth = 0.4f)
            {
                string id = $"solid|{c}|{metallic}|{smooth}";
                if (_made.TryGetValue(id, out var made)) return made;
                var m = Plain("OpenPlane solid", c);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
                _made[id] = m;
                return m;
            }

            private static void SetColor(Material m, Color c)
            {
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            }

            private static void MakeTransparent(Material m)
            {
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
        }

        // ---------------------------------------------------------------- instruments and lights
        //
        // Every instrument is built in its own frame: the face looks along local +z (towards the pilot) and its 12 o'clock
        // is local +y. Seen from the pilot, local -x is to the right, so turning a needle clockwise is a positive turn about z.

        /// <summary>A flat disc facing local +z (wound so it is seen from +z).</summary>
        private static Mesh Disc(float radius, int seg, float z)
        {
            var v = new List<Vector3> { new Vector3(0, 0, z) };
            var t = new List<int>();
            for (int i = 0; i <= seg; i++) { float a = 2 * Mathf.PI * i / seg; v.Add(new Vector3(radius * Mathf.Cos(a), radius * Mathf.Sin(a), z)); }
            for (int i = 1; i <= seg; i++) { t.Add(0); t.Add(i); t.Add(i + 1); }
            var m = new Mesh { name = "openplane-disc" };
            m.SetVertices(v); m.SetTriangles(t, 0);
            m.SetNormals(Enumerable.Repeat(Vector3.forward, v.Count).ToList());
            m.RecalculateBounds();
            return m;
        }

        private static Mesh Box(Vector3 size, Vector3 shift = default)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(go);
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = Vector3.Scale(v[i], size) + shift;
            mesh.vertices = v; mesh.RecalculateBounds();
            return mesh;
        }

        private static GameObject Part(Transform parent, string name, Mesh mesh, Material mat, Vector3 localPos, Quaternion localRot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Tick marks around a dial, clockwise from <paramref name="fromDeg"/> to <paramref name="toDeg"/>.</summary>
        private static void Ticks(Transform root, float r, float fromDeg, float toDeg, int count, int majorEvery, Material mat)
        {
            for (int k = 0; k < count; k++)
            {
                float deg = count == 1 ? fromDeg : Mathf.Lerp(fromDeg, toDeg, k / (float)(count - 1));
                bool major = k % majorEvery == 0;
                float len = r * (major ? 0.2f : 0.11f);
                var rot = Quaternion.Euler(0, 0, deg);
                Part(root, "tick", Box(new Vector3(r * (major ? 0.06f : 0.035f), len, 0.0006f)), mat, rot * new Vector3(0, r * 0.92f - len / 2, -0.0012f), rot);
            }
        }

        /// <summary>
        /// Builds the design's instruments under <paramref name="parent"/> (which sits at the design's origin). A face looks
        /// along its rotation's up axis and its 12 o'clock is the rotation's forward axis (a gauge lying face up with its
        /// top towards the nose, tipped back towards the pilot, reads the right way up).
        /// </summary>
        public static int AddInstruments(Transform parent, AircraftDesign d, MaterialFactory mats)
        {
            int n = 0;
            var bezel = mats.Solid(new Color(0.08f, 0.08f, 0.09f), 0.6f, 0.5f);
            var face = mats.Solid(new Color(0.02f, 0.02f, 0.025f), 0f, 0.2f);
            var marks = mats.Glow(new Color(0.95f, 0.95f, 0.85f), 1.5f);
            var north = mats.Glow(new Color(1f, 0.25f, 0.2f), 1.5f);
            var sky = mats.Glow(new Color(0.18f, 0.42f, 0.78f), 0.6f);
            var ground = mats.Glow(new Color(0.42f, 0.27f, 0.13f), 0.6f);
            var screen = mats.Glow(new Color(0.05f, 0.2f, 0.08f), 0.8f);
            var hud = mats.Tint(new Color(0.35f, 1f, 0.5f, 0.12f), "HUD");
            var hudFrame = mats.Glow(new Color(0.2f, 1f, 0.35f), 2f);
            var mirror = mats.Solid(new Color(0.85f, 0.88f, 0.9f), 1f, 0.97f);
            // Local +z (the face normal) to the rotation's +y, local +y (12 o'clock) to the rotation's +z.
            var frame = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            foreach (var ins in d.Instruments ?? new List<InstrumentDef>())
            {
                var root = new GameObject("OpenPlane instrument " + ins.Name).transform;
                root.SetParent(parent, false);
                root.localPosition = new Vector3((float)ins.Position[0], (float)ins.Position[1], (float)ins.Position[2]);
                var q = new Quaternion((float)ins.Rotation[0], (float)ins.Rotation[1], (float)ins.Rotation[2], (float)ins.Rotation[3]);
                root.localRotation = q * frame;
                float r = (float)ins.Size / 2;
                float size = (float)ins.Size;
                switch (ins.Kind)
                {
                    case "gauge":
                    case "compass":
                    {
                        bool compass = ins.Kind == "compass";
                        Part(root, "bezel", Disc(r * 1.12f, 32, -0.004f), bezel, Vector3.zero, Quaternion.identity);
                        Part(root, "face", Disc(r, 32, -0.002f), face, Vector3.zero, Quaternion.identity);
                        if (compass) Ticks(root, r, 0, 315, 8, 2, marks);
                        else Ticks(root, r, (float)ins.MinDeg, (float)ins.MaxDeg, 11, 5, marks);
                        // The needle points to 12 o'clock from its pivot; a compass needle is red and points north.
                        var needle = Part(root, "needle", Box(new Vector3(r * 0.08f, r * 0.85f, 0.002f), new Vector3(0, r * 0.4f, 0.0005f)), compass ? north : marks, Vector3.zero, Quaternion.identity);
                        Part(root, "hub", Disc(r * 0.09f, 12, 0.002f), bezel, Vector3.zero, Quaternion.identity);
                        var drive = needle.AddComponent<InstrumentDriver>();
                        drive.Kind = ins.Kind; drive.Reads = ins.Reads ?? (compass ? "heading" : "airspeed");
                        drive.Min = (float)ins.Min; drive.Max = (float)ins.Max; drive.MinDeg = (float)ins.MinDeg; drive.MaxDeg = (float)ins.MaxDeg;
                        break;
                    }
                    case "attitude":
                    {
                        Part(root, "bezel", Disc(r * 1.12f, 32, -0.003f), bezel, Vector3.zero, Quaternion.identity);
                        var ball = Part(root, "horizon", new Mesh { name = "openplane-horizon" }, sky, Vector3.zero, Quaternion.identity);
                        ball.GetComponent<MeshRenderer>().sharedMaterials = new[] { sky, ground };
                        var att = ball.AddComponent<AttitudeFace>();
                        att.Radius = r;
                        Part(root, "wings", Box(new Vector3(r * 0.9f, r * 0.06f, 0.002f)), marks, new Vector3(0, 0, 0.002f), Quaternion.identity);
                        Part(root, "dot", Box(new Vector3(r * 0.08f, r * 0.08f, 0.002f)), marks, new Vector3(0, 0, 0.002f), Quaternion.identity);
                        break;
                    }
                    case "screen":
                        Part(root, "frame", Box(new Vector3(size * 1.1f, size * 0.85f, 0.01f)), bezel, new Vector3(0, 0, -0.006f), Quaternion.identity);
                        // A screen with a page is dark behind it, so the page reads; a plain screen glows green.
                        Part(root, "display", Box(new Vector3(size, size * 0.75f, 0.002f)), string.IsNullOrEmpty(ins.Reads) ? screen : face, Vector3.zero, Quaternion.identity);
                        if (!string.IsNullOrEmpty(ins.Reads))
                            try { AddScreenPage(root, ins.Reads!, size, mats); }
                            catch (Exception e) { Plugin.Log.LogWarning($"Cockpit display \"{ins.Name}\" left plain: {e.Message}"); }
                        break;
                    case "hud":
                        // A see-through combiner glass with a thin glowing frame: the game's own HUD symbols still show.
                        Part(root, "combiner", Box(new Vector3(size, size * 0.8f, 0.002f)), hud, Vector3.zero, Quaternion.identity);
                        Part(root, "frame", Box(new Vector3(size, 0.004f, 0.003f)), hudFrame, new Vector3(0, -size * 0.4f, 0), Quaternion.identity);
                        break;
                    case "mirror":
                        Part(root, "mirror", Box(new Vector3(size, size * 0.4f, 0.006f)), mirror, Vector3.zero, Quaternion.identity);
                        break;
                    case "lever":
                        Part(root, "stem", Box(new Vector3(0.01f, 0.01f, size)), bezel, new Vector3(0, 0, size / 2), Quaternion.identity);
                        Part(root, "knob", Box(new Vector3(0.025f, 0.025f, 0.025f)), marks, new Vector3(0, 0, size), Quaternion.identity);
                        if (!string.IsNullOrEmpty(ins.Reads))
                            try { AddLeverMotion(root, ins.Reads!); } catch (Exception e) { Plugin.Log.LogWarning($"Lever \"{ins.Name}\" left still: {e.Message}"); }
                        break;
                }
                n++;
            }
            return n;
        }

        /// <summary>Builds the design's lights: glowing lamps (and strips); strobes and beacons blink.</summary>
        public static int AddLights(Transform parent, AircraftDesign d, MaterialFactory mats)
        {
            int n = 0;
            var sphere = SphereMesh();
            foreach (var l in d.Lights ?? new List<LightDef>())
            {
                var c = new Color((float)l.Color[0], (float)l.Color[1], (float)l.Color[2], 1);
                var pos = new Vector3((float)l.Position[0], (float)l.Position[1], (float)l.Position[2]);
                var rot = new Quaternion((float)l.Rotation[0], (float)l.Rotation[1], (float)l.Rotation[2], (float)l.Rotation[3]);
                var mesh = l.Kind == "strip" ? Box(new Vector3((float)l.Size, 0.015f, 0.04f)) : sphere;
                var go = Part(parent, "OpenPlane light " + l.Name, mesh, mats.Glow(c, l.Kind == "landing" ? 6f : 4f), pos, rot);
                if (l.Kind != "strip") go.transform.localScale = Vector3.one * (float)l.Size;
                if (l.Kind == "strobe" || l.Kind == "beacon")
                {
                    var blink = go.AddComponent<LightBlinker>();
                    blink.Period = l.Kind == "strobe" ? 1.2f : 1.0f;
                    blink.OnFraction = l.Kind == "strobe" ? 0.08f : 0.4f;
                }
                n++;
            }
            return n;
        }

        /// <summary>True when an engine drives a propeller (rotors are left to the stock plane).</summary>
        public static bool IsProp(EngineDef e)
            => e != null && e.Type != "rotor" && (e.Type == "piston-prop" || e.Type == "turboprop" || e.Type == "electric-prop" || (e.PropDiameterM ?? 0) > 0);

        /// <summary>
        /// A propeller for each of the design's propeller engines, as the builder shows it: a spinner, three blades and a
        /// see-through disc that stands in for the blades once they turn fast (PropSpinner turns them with the engine).
        /// </summary>
        public static int AddPropellers(Transform parent, AircraftDesign d, MaterialFactory mats)
        {
            int n = 0;
            var bladeMat = mats.Solid(new Color(0.12f, 0.12f, 0.13f), 0.2f, 0.35f);
            var hubMat = mats.Solid(new Color(0.55f, 0.56f, 0.58f), 0.6f, 0.6f);
            var blurMat = mats.Haze(new Color(0.5f, 0.51f, 0.53f, 0.24f));
            foreach (var e in d.Engines ?? new List<EngineDef>())
            {
                if (!IsProp(e) || e.Position == null || e.Position.Length < 3) continue;
                float dia = Mathf.Clamp((float)(e.PropDiameterM ?? 2.0), 0.3f, 8f);
                var dir = e.ThrustDirection != null && e.ThrustDirection.Length >= 3
                    ? new Vector3((float)e.ThrustDirection[0], (float)e.ThrustDirection[1], (float)e.ThrustDirection[2]) : Vector3.forward;
                if (dir.sqrMagnitude < 1e-6f || float.IsNaN(dir.x)) dir = Vector3.forward;
                dir.Normalize();
                var hub = new GameObject("OpenPlane propeller");
                hub.transform.SetParent(parent, false);
                hub.transform.localPosition = new Vector3((float)e.Position[0], (float)e.Position[1], (float)e.Position[2]);
                hub.transform.localRotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up);
                Part(hub.transform, "spinner", Cone(dia * 0.09f, dia * 0.22f, 24), hubMat, new Vector3(0, 0, -dia * 0.02f), Quaternion.identity);
                var blades = new GameObject("blades").transform;
                blades.SetParent(hub.transform, false);
                for (int k = 0; k < 3; k++)
                    Part(blades, "blade", Box(new Vector3(dia * 0.075f, dia * 0.44f, Mathf.Max(0.02f, dia * 0.012f)), new Vector3(0, dia * 0.26f, 0)), bladeMat,
                         Vector3.zero, Quaternion.Euler(0, 0, 120f * k) * Quaternion.Euler(0, 18f, 0));
                var blur = new GameObject("blur").transform;
                blur.SetParent(hub.transform, false);
                var disc = Disc(dia / 2, 48, 0);
                Part(blur, "front", disc, blurMat, Vector3.zero, Quaternion.identity);
                Part(blur, "back", disc, blurMat, Vector3.zero, Quaternion.Euler(0, 180f, 0));
                hub.AddComponent<PropSpinner>();
                n++;
            }
            return n;
        }

        /// <summary>
        /// The jet engines of a plane built in OpenPlane, as the builder shows them: a metal casing along the thrust line with a
        /// dark intake face and a nozzle (orange-brown with an afterburner). Converted planes carry their own nacelles, so
        /// they get none.
        /// </summary>
        public static int AddJets(Transform parent, AircraftDesign d, MaterialFactory mats)
        {
            if (string.Equals(d.Origin?.Source, "flyout", StringComparison.OrdinalIgnoreCase)) return 0;
            int n = 0;
            var caseMat = mats.Solid(new Color(0.3f, 0.31f, 0.33f), 0.6f, 0.45f);
            var faceMat = mats.Solid(new Color(0.07f, 0.07f, 0.075f), 0.1f, 0.2f);
            foreach (var e in d.Engines ?? new List<EngineDef>())
            {
                if (IsProp(e) || e.Type == "rotor" || e.Position == null || e.Position.Length < 3) continue;
                float dia = e.DiameterM > 0 ? Mathf.Clamp((float)e.DiameterM, 0.2f, 4f) : 0.9f;
                float len = Mathf.Max(1.2f, dia * 3f);
                var dir = e.ThrustDirection != null && e.ThrustDirection.Length >= 3
                    ? new Vector3((float)e.ThrustDirection[0], (float)e.ThrustDirection[1], (float)e.ThrustDirection[2]) : Vector3.forward;
                if (dir.sqrMagnitude < 1e-6f || float.IsNaN(dir.x)) dir = Vector3.forward;
                dir.Normalize();
                var nozzleMat = e.AfterburnerThrustN > 0 ? mats.Solid(new Color(0.75f, 0.35f, 0.16f), 0.3f, 0.4f) : mats.Solid(new Color(0.16f, 0.17f, 0.19f), 0.4f, 0.35f);
                var eng = new GameObject("OpenPlane jet engine");
                eng.transform.SetParent(parent, false);
                eng.transform.localPosition = new Vector3((float)e.Position[0], (float)e.Position[1], (float)e.Position[2]);
                eng.transform.localRotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up);
                Part(eng.transform, "casing", Tube(dia / 2, dia * 0.42f, len, 32), caseMat, Vector3.zero, Quaternion.identity);
                Part(eng.transform, "intake", Disc(dia / 2 * 0.98f, 32, 0), faceMat, new Vector3(0, 0, len / 2), Quaternion.identity);
                Part(eng.transform, "nozzle", Disc(dia * 0.42f * 0.98f, 32, 0), nozzleMat, new Vector3(0, 0, -len / 2 - 0.005f), Quaternion.Euler(0, 180f, 0));
                n++;
            }
            return n;
        }

        /// <summary>An open tube along local z: radius <paramref name="front"/> at +length/2, <paramref name="back"/> at -length/2, seen from outside.</summary>
        private static Mesh Tube(float front, float back, float length, int seg)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = 2 * Mathf.PI * i / seg;
                v.Add(new Vector3(front * Mathf.Cos(a), front * Mathf.Sin(a), length / 2));
                v.Add(new Vector3(back * Mathf.Cos(a), back * Mathf.Sin(a), -length / 2));
            }
            for (int i = 0; i < seg; i++)
            {
                int f0 = 2 * i, b0 = f0 + 1, f1 = f0 + 2, b1 = f0 + 3;
                t.Add(f0); t.Add(b0); t.Add(f1);
                t.Add(f1); t.Add(b0); t.Add(b1);
            }
            var m = new Mesh { name = "openplane-engine" };
            m.SetVertices(v); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>A cone along local +z: its base (radius <paramref name="r"/>) at z = 0, its tip at z = <paramref name="length"/>.</summary>
        private static Mesh Cone(float r, float length, int seg)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i < seg; i++)
            {
                float a0 = 2 * Mathf.PI * i / seg, a1 = 2 * Mathf.PI * (i + 1) / seg;
                var p0 = new Vector3(r * Mathf.Cos(a0), r * Mathf.Sin(a0), 0);
                var p1 = new Vector3(r * Mathf.Cos(a1), r * Mathf.Sin(a1), 0);
                int b = v.Count;
                v.Add(p0); v.Add(new Vector3(0, 0, length)); v.Add(p1);   // side, seen from outside
                t.Add(b); t.Add(b + 2); t.Add(b + 1);   // p0, p1, tip
                b = v.Count;
                v.Add(p0); v.Add(Vector3.zero); v.Add(p1);                 // base, seen from behind
                t.Add(b); t.Add(b + 1); t.Add(b + 2);
            }
            var m = new Mesh { name = "openplane-spinner" };
            m.SetVertices(v); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        private static Mesh SphereMesh()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go);
            return mesh;
        }
    }

    /// <summary>Turns an instrument's needle from the plane it sits in (clockwise from 12 o'clock, seen from the face).</summary>
    internal sealed class InstrumentDriver : MonoBehaviour
    {
        public string Kind = "gauge";
        public string Reads = "airspeed";
        public float Min, Max = 1, MinDeg = 30, MaxDeg = 330;
        private Aircraft? _aircraft;

        private void Start() { _aircraft = GetComponentInParent<Aircraft>(); }

        private void LateUpdate()
        {
            var a = _aircraft;
            if (a == null) return;
            try
            {
                float value = Value(a, Reads, out float naturalMin, out float naturalMax);
                float deg;
                if (Kind == "compass") deg = 360f - value;   // the needle points north
                else
                {
                    float lo = Min, hi = Max;
                    // A dial left at 0 to 1 by its maker shows the reading's natural range.
                    if (Mathf.Abs(hi - lo) <= 1.0001f && Reads != "throttle" && Reads != "fuel") { lo = naturalMin; hi = naturalMax; }
                    float f = hi > lo ? Mathf.Clamp01((value - lo) / (hi - lo)) : 0;
                    deg = Mathf.Lerp(MinDeg, MaxDeg, f);
                }
                transform.localRotation = Quaternion.Euler(0, 0, deg);
            }
            catch { enabled = false; }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Aircraft, Turbojet[]> _jetTable = new System.Runtime.CompilerServices.ConditionalWeakTable<Aircraft, Turbojet[]>();

        internal static float Value(Aircraft a, string reads, out float lo, out float hi)
        {
            lo = 0; hi = 1;
            switch (reads)
            {
                case "airspeed": lo = 0; hi = 1000; return a.speed * 3.6f;
                case "altitude": lo = 0; hi = 10000; return a.transform.position.GlobalY();
                case "radar-altitude": lo = 0; hi = 1000; return a.radarAlt;
                case "vertical-speed": lo = -50; hi = 50; return a.rb != null ? a.rb.velocity.y : 0;
                case "rpm":
                {
                    lo = 0; hi = 110;
                    float best = 0;
                    if (a.engines != null) foreach (var e in a.engines) if (e != null) best = Mathf.Max(best, e.GetRPMRatio());
                    // Jet engines (Turbojet) never join aircraft.engines (only propellers, fans and rotors do): read them directly.
                    var jets = _jetTable.GetValue(a, x => x.GetComponentsInChildren<Turbojet>(true));
                    foreach (var j in jets) if (j != null) best = Mathf.Max(best, j.GetRPMRatio());
                    return best * 100;
                }
                case "throttle": return a.controlInputs.throttle;
                case "fuel": return a.GetFuelLevel();
                case "g": lo = -3; hi = 9; return a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null ? a.pilots[0].GetAccel().magnitude / 9.81f : 1f;
                case "aoa":
                {
                    lo = -10; hi = 30;
                    var v = a.rb != null ? a.transform.InverseTransformDirection(a.rb.velocity) : Vector3.forward;
                    return v.sqrMagnitude > 1 ? Mathf.Atan2(-v.y, v.z) * Mathf.Rad2Deg : 0;
                }
                case "heading":
                {
                    lo = 0; hi = 360;
                    var f = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up);
                    return f.sqrMagnitude > 1e-6f ? (Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + 360) % 360 : 0;
                }
                case "clock": lo = 0; hi = 12; return (float)(DateTime.Now.TimeOfDay.TotalHours % 12);
            }
            return 0;
        }
    }

    /// <summary>
    /// An artificial horizon: a round face split into sky and ground along the horizon line, which tilts against the
    /// plane's bank and drops as the nose rises (redrawn when the attitude changes).
    /// </summary>
    internal sealed class AttitudeFace : MonoBehaviour
    {
        public float Radius = 0.04f;
        private const int Segments = 36;
        private Aircraft? _aircraft;
        private Mesh? _mesh;
        private float _lastBank = float.NaN, _lastDrop = float.NaN;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<int> _sky = new List<int>(), _ground = new List<int>();

        private void Start()
        {
            _aircraft = GetComponentInParent<Aircraft>();
            _mesh = GetComponent<MeshFilter>().mesh;
            Draw(0, 0);
        }

        private void LateUpdate()
        {
            var a = _aircraft;
            if (a == null || _mesh == null) return;
            var t = a.transform;
            // Bank: positive with the right wing down. Pitch: positive nose up.
            float bank = Mathf.Atan2(-Vector3.Dot(t.right, Vector3.up), Vector3.Dot(t.up, Vector3.up));
            float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(t.forward, Vector3.up), -1, 1)) * Mathf.Rad2Deg;
            float drop = Mathf.Clamp(pitch / 45f, -1, 1) * Radius * 0.9f;
            if (Mathf.Abs(bank - _lastBank) < 0.003f && Mathf.Abs(drop - _lastDrop) < Radius * 0.004f) return;
            Draw(bank, drop);
        }

        /// <summary>Sky is where dot(p, n) &gt; -drop; n points to the sky, turned with the bank (seen from the pilot,
        /// banking right turns the horizon anticlockwise).</summary>
        private void Draw(float bank, float drop)
        {
            _lastBank = bank; _lastDrop = drop;
            var n = new Vector2(Mathf.Sin(bank), Mathf.Cos(bank));
            var circle = new List<Vector2>(Segments);
            for (int i = 0; i < Segments; i++) { float ang = 2 * Mathf.PI * i / Segments; circle.Add(new Vector2(Radius * Mathf.Cos(ang), Radius * Mathf.Sin(ang))); }
            _v.Clear(); _sky.Clear(); _ground.Clear();
            Fill(Clip(circle, n, drop), _sky);
            Fill(Clip(circle, -n, -drop), _ground);
            var m = _mesh!;
            m.Clear();
            m.SetVertices(_v);
            m.SetNormals(Enumerable.Repeat(Vector3.forward, _v.Count).ToList());
            m.subMeshCount = 2;
            m.SetTriangles(_sky, 0);
            m.SetTriangles(_ground, 1);
            m.RecalculateBounds();
        }

        /// <summary>The part of a convex polygon where dot(p, n) + d &gt;= 0 (Sutherland-Hodgman, one edge).</summary>
        private static List<Vector2> Clip(List<Vector2> poly, Vector2 n, float d)
        {
            var o = new List<Vector2>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                float fa = Vector2.Dot(a, n) + d, fb = Vector2.Dot(b, n) + d;
                if (fa >= 0) o.Add(a);
                if ((fa >= 0) != (fb >= 0)) o.Add(a + (b - a) * (fa / (fa - fb)));
            }
            return o;
        }

        /// <summary>A fan over a convex polygon wound anticlockwise in x-y, so it faces +z.</summary>
        private void Fill(List<Vector2> poly, List<int> tris)
        {
            if (poly.Count < 3) return;
            int start = _v.Count;
            foreach (var p in poly) _v.Add(new Vector3(p.x, p.y, 0));
            for (int i = 1; i + 1 < poly.Count; i++) { tris.Add(start); tris.Add(start + i); tris.Add(start + i + 1); }
        }
    }

    /// <summary>Blinks a lamp (strobes and beacons).</summary>
    internal sealed class LightBlinker : MonoBehaviour
    {
        public float Period = 1.2f, OnFraction = 0.1f;
        private Renderer? _r;
        private float _phase;
        private void Start() { _r = GetComponent<Renderer>(); _phase = UnityEngine.Random.value * Period; }
        private void Update()
        {
            if (_r == null) return;
            _r.enabled = ((Time.time + _phase) % Period) < Period * OnFraction;
        }
    }

    /// <summary>
    /// Turns a design propeller with the plane's engines: the blades turn while the engine is slow, and above that the
    /// see-through disc shows instead (as the stock propellers do).
    /// </summary>
    internal sealed class PropSpinner : MonoBehaviour
    {
        private Aircraft? _aircraft;
        private Transform? _blades;
        private Renderer[] _bladeRenderers = new Renderer[0], _blurRenderers = new Renderer[0];
        private MeshFilter? _disc;
        private List<Mesh>? _stockBlur;
        private float _rpm, _radius;
        private bool _blurred = true, _ready;

        private void Start()
        {
            _aircraft = GetComponentInParent<Aircraft>();
            _blades = transform.Find("blades");
            _bladeRenderers = _blades != null ? _blades.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            var blur = transform.Find("blur");
            _blurRenderers = blur != null ? blur.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            _disc = blur != null ? blur.Find("front")?.GetComponent<MeshFilter>() : null;
            _radius = _disc != null && _disc.sharedMesh != null ? _disc.sharedMesh.bounds.extents.x : 1f;
            Show(blurred: false);
        }

        /// <summary>
        /// When the stock plane has a propeller, its own blur discs (the game's material and meshes, which turn from faint
        /// to solid with the engine's speed) are used for this one, scaled to its diameter.
        /// </summary>
        private void Init()
        {
            _ready = true;
            var a = _aircraft;
            if (a == null || _disc == null || a.partLookup == null) return;
            var places = new List<Transform> { a.transform };
            foreach (var part in a.partLookup) if (part != null) places.Add(part.transform);
            var stock = places.SelectMany(t => t.GetComponentsInChildren<ConstantSpeedProp>(true)).FirstOrDefault();
            if (stock == null) return;
            var t0 = HarmonyLib.Traverse.Create(stock);
            var meshes = t0.Field("propDiscMeshes").GetValue<List<Mesh>>();
            var stockDisc = t0.Field("propDisc").GetValue<MeshFilter>();
            var mat = stockDisc != null && stockDisc.GetComponent<Renderer>() is Renderer sr ? sr.sharedMaterial : null;
            if (meshes == null || meshes.Count == 0 || mat == null || meshes.Any(m => m == null)) return;
            float stockRadius = meshes.Max(m => Mathf.Max(m.bounds.extents.x, m.bounds.extents.y));
            if (!(stockRadius > 0.05f)) return;
            _stockBlur = meshes;
            var r = _disc.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
            _disc.transform.localScale = Vector3.one * (_radius / stockRadius);
            // One disc, as the game draws it (the back copy was for the plain see-through colour).
            foreach (var other in _blurRenderers) if (other != null && other != r) { other.forceRenderingOff = true; }
            _blurRenderers = r != null ? new[] { r } : new Renderer[0];
            _blurred = true;
            Show(blurred: false);
        }

        private void Show(bool blurred)
        {
            if (blurred == _blurred) return;
            _blurred = blurred;
            foreach (var r in _bladeRenderers) if (r != null) r.forceRenderingOff = blurred;
            // The game's blur discs stay on (the slowest one is nearly clear); a plain disc shows only when blurred.
            foreach (var r in _blurRenderers) if (r != null) r.forceRenderingOff = _stockBlur == null && !blurred;
        }

        private void LateUpdate()
        {
            if (_aircraft == null || _blades == null) return;
            if (!_ready && _aircraft.partLookup != null && _aircraft.partLookup.Count > 0)
            {
                try { Init(); } catch (Exception e) { Plugin.Log.LogWarning("OpenPlane propeller: " + e.Message); }
                StockPieces.Once(_aircraft, "propeller", $"its propeller turns with the engine ({_bladeRenderers.Length} blades, " +
                                 (_stockBlur != null ? $"the stock plane's {_stockBlur.Count} blur discs" : "a plain blur disc") + ")");
            }
            float ratio = 0f;
            try
            {
                if (_aircraft.engines != null)
                    foreach (var e in _aircraft.engines) if (e != null) ratio = Mathf.Max(ratio, e.GetRPMRatio());
            }
            catch { }
            ratio = Mathf.Clamp01(ratio);
            // About 2,200 rpm at full power; it spins up and down over a second or two.
            _rpm = Mathf.MoveTowards(_rpm, ratio * 2200f, 1500f * Time.deltaTime);
            if (_stockBlur != null && _disc != null)
            {
                // The game's own rule: the blur disc for the speed, the blades only below the first blur.
                int index = Mathf.Clamp(Mathf.FloorToInt(_stockBlur.Count * (_rpm / 2200f * 0.55f * Time.timeScale + 0.25f)), 0, _stockBlur.Count - 1);
                if (_disc.sharedMesh != _stockBlur[index]) _disc.sharedMesh = _stockBlur[index];
                Show(blurred: index > 0);
            }
            else Show(blurred: _rpm > 450f);
            if (!_blurred) _blades.Rotate(0f, 0f, -_rpm * 6f * Time.deltaTime, Space.Self);
        }
    }

    /// <summary>
    /// Stock pieces the game switches on and off itself, hidden on an OpenPlane plane: the stock propeller (the design draws
    /// its own; its blades stay in place for the game's physics) and the landing gear while it is stowed (in the stock
    /// plane's wells it would poke through the design's wings). It uses forceRenderingOff, which the game never sets.
    /// </summary>
    internal sealed class StockPieces : MonoBehaviour
    {
        public bool HideProps = true;
        private Aircraft? _aircraft;
        private readonly List<Renderer> _gear = new List<Renderer>();
        private readonly List<Renderer> _props = new List<Renderer>();
        private bool _gearHidden;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        /// <summary>One line in the log per plane type and topic (every spawn of a plane would repeat it).</summary>
        internal static void Once(Aircraft? a, string topic, string what)
        {
            try
            {
                var name = a != null && a.definition != null ? a.definition.unitName : "?";
                if (Logged.Add(name + "|" + topic)) Plugin.Log.LogInfo($"{name}: {what}.");
            }
            catch { }
        }

        private bool _ready;

        private void Start()
        {
            _aircraft = GetComponentInParent<Aircraft>();
            if (_aircraft == null) Plugin.Log.LogWarning("OpenPlane stock pieces: no plane above " + name);
        }

        /// <summary>
        /// Finds the pieces once the plane is set up. A flying plane's parts are separate objects (the game takes each part
        /// off the plane's root so it moves on its own joint), so they are found through the plane's list of parts.
        /// </summary>
        private bool Init()
        {
            var a = _aircraft!;
            if (a.partLookup == null || a.partLookup.Count == 0) return false;
            var places = new List<Transform> { a.transform };
            foreach (var part in a.partLookup) if (part != null && !places.Contains(part.transform)) places.Add(part.transform);
            T[] All<T>() where T : Component => places.SelectMany(t => t.GetComponentsInChildren<T>(true)).Distinct().ToArray();
            if (HideProps)
            {
                foreach (var p in All<ConstantSpeedProp>())
                {
                    Collect(p.transform);
                    // The blades, hub and blur disc by the propeller's own references, wherever they sit.
                    var t = HarmonyLib.Traverse.Create(p);
                    if (t.Field("blades").GetValue() is System.Collections.IEnumerable blades)
                        foreach (var b in blades) if (b != null && HarmonyLib.Traverse.Create(b).Field("renderer").GetValue() is Renderer br) Add(br);
                    if (t.Field("hubVisible").GetValue() is GameObject hub) Collect(hub.transform);
                    if (t.Field("propDisc").GetValue() is MeshFilter disc && disc.GetComponent<Renderer>() is Renderer dr) Add(dr);
                }
                foreach (var p in All<PropFan>()) Collect(p.transform);
            }
            // Weapon stations (and what hangs on them) never count as gear, even under a leg's branch.
            var stations = new List<Transform>();
            var wm = a.weaponManager;
            if (wm?.hardpointSets != null)
                foreach (var set in wm.hardpointSets)
                    if (set?.hardpoints != null)
                        foreach (var hp in set.hardpoints)
                            if (hp?.transform != null) stations.Add(hp.transform);
            foreach (var g in All<LandingGear>())
            {
                var leg = g.transform;
                while (leg.parent != null && leg.parent != a.transform && leg.parent.GetComponent<UnitPart>() == null) leg = leg.parent;
                foreach (var r in leg.GetComponentsInChildren<Renderer>(true))
                    if ((r is MeshRenderer || r is SkinnedMeshRenderer) && !r.transform.IsChildOf(transform) && !_gear.Contains(r)
                        && !stations.Any(st => r.transform == st || r.transform.IsChildOf(st)))
                        _gear.Add(r);
            }
            Once(a, "pieces", $"{_props.Count} stock propeller pieces hidden, {_gear.Count} landing-gear pieces hide when it is stowed");
            return true;
        }

        private void Collect(Transform t)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) Add(r);
        }

        private void Add(Renderer r)
        {
            if (r != null && !r.transform.IsChildOf(transform) && !_props.Contains(r)) _props.Add(r);
        }

        private void LateUpdate()
        {
            if (_aircraft == null) return;
            if (!_ready) { try { _ready = Init(); } catch (Exception e) { _ready = true; Plugin.Log.LogWarning("OpenPlane stock pieces: " + e.Message); } }
            // The game never sets forceRenderingOff, but keep it set (cheap) in case anything resets it.
            for (int i = 0; i < _props.Count; i++) if (_props[i] != null && !_props[i].forceRenderingOff) _props[i].forceRenderingOff = true;
            if (_gear.Count == 0) return;
            bool stowed = _aircraft.gearState == LandingGear.GearState.LockedRetracted;
            if (stowed == _gearHidden) return;
            _gearHidden = stowed;
            foreach (var r in _gear) if (r != null) r.forceRenderingOff = stowed;
            if (stowed) Once(_aircraft, "gear", $"its stowed landing gear is hidden ({_gear.Count} pieces)");
        }
    }
}
