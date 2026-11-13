using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace OpenPlane.Mod
{
    // Live cockpit displays and moving levers. A screen with "reads" shows a page drawn from the plane's own state:
    // - flight: an attitude ball with airspeed, altitude, heading, g and angle of attack;
    // - engine: engine speed, throttle and fuel as bars with numbers;
    // - nav: a heading needle with heading, height above the ground and climb rate.
    // A lever with "reads" moves: throttle (forward with the throttle), gear (down with the gear), stick (with pitch and roll).
    // Each instrument's frame: the face looks along local +z (towards the pilot), 12 o'clock is local +y, and seen from the
    // pilot local -x is to the right (see AddInstruments).
    internal static partial class DesignVisuals
    {
        private static TMP_FontAsset? _font;
        private static bool _fontLooked;

        /// <summary>A font the game already has (its own UI uses TextMesh Pro); null if none is loaded.</summary>
        internal static TMP_FontAsset? Font()
        {
            if (_fontLooked) return _font;
            _fontLooked = true;
            try
            {
                _font = TMP_Settings.defaultFontAsset;
                if (_font == null) _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f != null && f.name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0)
                                       ?? Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f != null);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Cockpit displays: no font for their numbers (" + e.Message + ")"); }
            return _font;
        }

        /// <summary>
        /// Plans a line of text on an instrument face at (x, y) in the face's own frame (x is to the pilot's left). The text
        /// itself is made when the plane spawns (<see cref="ScreenDriver"/>): TextMesh Pro can't be set up on the inactive
        /// stock-plane copy the mod builds (it threw and took the whole skin step down).
        /// </summary>
        private static void Text(ScreenDriver d, string name, float x, float y, float height, float width, string role, string initial = "")
        {
            int n = d.TextNames.Length;
            System.Array.Resize(ref d.TextNames, n + 1); d.TextNames[n] = name;
            System.Array.Resize(ref d.TextRoles, n + 1); d.TextRoles[n] = role;
            System.Array.Resize(ref d.TextInitial, n + 1); d.TextInitial[n] = initial;
            System.Array.Resize(ref d.TextX, n + 1); d.TextX[n] = x;
            System.Array.Resize(ref d.TextY, n + 1); d.TextY[n] = y;
            System.Array.Resize(ref d.TextH, n + 1); d.TextH[n] = height;
            System.Array.Resize(ref d.TextW, n + 1); d.TextW[n] = width;
        }

        /// <summary>Makes one planned line of text (on the live plane), readable by the pilot.</summary>
        internal static TextMeshPro? MakeText(Transform parent, string name, float x, float y, float height, float width, Color color, string initial)
        {
            var font = Font();
            if (font == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            go.transform.localPosition = new Vector3(x, y, 0.0025f);
            go.transform.localRotation = Quaternion.Euler(0, 180f, 0);   // TextMesh Pro reads from its -z side; the pilot looks along -z
            t.font = font;
            t.enableAutoSizing = false;
            t.fontSize = height * 10f;   // TextMesh Pro: a font size of 1 is about 0.1 m tall
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta = new Vector2(width, height * 1.4f);
            t.text = initial;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; }
            return t;
        }

        /// <summary>The page a screen shows (see the top of this file); nothing for a plain screen.</summary>
        internal static void AddScreenPage(Transform root, string reads, float size, MaterialFactory mats)
        {
            float w = size, h = size * 0.75f;
            var green = new Color(0.45f, 1f, 0.55f);
            var glow = mats.Glow(green, 2.2f);
            var dim = mats.Glow(new Color(0.15f, 0.45f, 0.2f), 1.2f);
            var driver = root.gameObject.AddComponent<ScreenDriver>();
            driver.Page = reads;
            float th = h * 0.11f;   // text height
            switch (reads)
            {
                case "flight":
                {
                    float r = h * 0.32f;
                    var sky = mats.Glow(new Color(0.12f, 0.32f, 0.68f), 0.7f);
                    var ground = mats.Glow(new Color(0.4f, 0.24f, 0.1f), 0.7f);
                    var ball = Part(root, "horizon", new Mesh { name = "openplane-horizon" }, sky, new Vector3(0, -h * 0.02f, 0.0015f), Quaternion.identity);
                    ball.GetComponent<MeshRenderer>().sharedMaterials = new[] { sky, ground };
                    ball.AddComponent<AttitudeFace>().Radius = r;
                    Part(root, "wings", Box(new Vector3(r * 0.9f, r * 0.07f, 0.001f)), glow, new Vector3(0, -h * 0.02f, 0.0028f), Quaternion.identity);
                    Text(driver, "speed", w * 0.38f, 0, th, w * 0.26f, "speed");
                    Text(driver, "altitude", -w * 0.38f, 0, th, w * 0.26f, "altitude");
                    Text(driver, "heading", 0, h * 0.40f, th, w * 0.6f, "heading");
                    Text(driver, "g and aoa", 0, -h * 0.41f, th * 0.9f, w * 0.9f, "bottom");
                    Text(driver, "speed label", w * 0.38f, h * 0.14f, th * 0.7f, w * 0.26f, "", "KT");
                    Text(driver, "altitude label", -w * 0.38f, h * 0.14f, th * 0.7f, w * 0.26f, "", "FT");
                    break;
                }
                case "engine":
                {
                    string[] labels = { "RPM", "THR", "FUEL" };
                    float barH = h * 0.56f, barW = w * 0.12f, y0 = -h * 0.3f;
                    driver.Bars = new Transform[3];
                    for (int i = 0; i < 3; i++)
                    {
                        float x = (1 - i) * w * 0.3f;   // left to right as the pilot sees it
                        Part(root, labels[i] + " track", Box(new Vector3(barW, barH, 0.0005f), new Vector3(0, barH / 2, 0)), dim, new Vector3(x, y0, 0.0012f), Quaternion.identity);
                        var pivot = new GameObject(labels[i] + " bar").transform;
                        pivot.SetParent(root, false);
                        pivot.localPosition = new Vector3(x, y0, 0.0018f);
                        Part(pivot, "fill", Box(new Vector3(barW * 0.8f, barH, 0.0005f), new Vector3(0, barH / 2, 0)), glow, Vector3.zero, Quaternion.identity);
                        driver.Bars[i] = pivot;
                        Text(driver, labels[i] + " label", x, y0 - th * 0.9f, th * 0.75f, w * 0.3f, "", labels[i]);
                        Text(driver, labels[i] + " value", x, y0 + barH + th * 0.8f, th * 0.8f, w * 0.3f, "bar" + i);
                    }
                    break;
                }
                case "nav":
                {
                    float r = h * 0.3f;
                    Part(root, "rose", Disc(r, 32, 0.0012f), dim, new Vector3(0, h * 0.05f, 0), Quaternion.identity);
                    var needle = Part(root, "needle", Box(new Vector3(r * 0.1f, r * 0.9f, 0.0005f), new Vector3(0, r * 0.45f, 0)), mats.Glow(new Color(1f, 0.35f, 0.25f), 2f),
                                      new Vector3(0, h * 0.05f, 0.002f), Quaternion.identity);
                    var drive = needle.AddComponent<InstrumentDriver>();
                    drive.Kind = "compass"; drive.Reads = "heading";
                    Text(driver, "north", 0, h * 0.05f + r * 1.15f, th * 0.7f, w * 0.2f, "", "N");
                    Text(driver, "heading", w * 0.36f, h * 0.30f, th, w * 0.26f, "heading");
                    Text(driver, "radar altitude", -w * 0.36f, h * 0.30f, th, w * 0.26f, "altitude");
                    Text(driver, "climb", 0, -h * 0.40f, th * 0.9f, w * 0.9f, "bottom");
                    break;
                }
            }
        }

        /// <summary>Makes a lever move with the plane's controls (throttle, gear or stick).</summary>
        internal static void AddLeverMotion(Transform root, string reads)
        {
            if (reads != "throttle" && reads != "gear" && reads != "stick") return;
            var d = root.gameObject.AddComponent<LeverDriver>();
            d.Reads = reads;
        }
    }

    /// <summary>Keeps a screen's page up to date (numbers ten times a second, bars every frame).</summary>
    internal sealed class ScreenDriver : MonoBehaviour
    {
        public string Page = "";
        public Transform[]? Bars;
        // The page's lines of text, planned when the plane was built and made when it spawns (see DesignVisuals.Text).
        public string[] TextNames = new string[0], TextRoles = new string[0], TextInitial = new string[0];
        public float[] TextX = new float[0], TextY = new float[0], TextH = new float[0], TextW = new float[0];
        private TextMeshPro? Speed, Altitude, Heading, Bottom;
        private TextMeshPro?[] BarTexts = new TextMeshPro?[3];
        private Aircraft? _aircraft;
        private float _next;

        private void Start()
        {
            _aircraft = GetComponentInParent<Aircraft>();
            try
            {
                var green = new Color(0.45f, 1f, 0.55f);
                for (int i = 0; i < TextNames.Length; i++)
                {
                    var t = DesignVisuals.MakeText(transform, TextNames[i], TextX[i], TextY[i], TextH[i], TextW[i], green, TextInitial[i]);
                    switch (TextRoles[i])
                    {
                        case "speed": Speed = t; break;
                        case "altitude": Altitude = t; break;
                        case "heading": Heading = t; break;
                        case "bottom": Bottom = t; break;
                        case "bar0": BarTexts[0] = t; break;
                        case "bar1": BarTexts[1] = t; break;
                        case "bar2": BarTexts[2] = t; break;
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogInfo("Cockpit display text skipped: " + e.Message); }
        }

        private void LateUpdate()
        {
            var a = _aircraft;
            if (a == null) return;
            try
            {
                if (Page == "engine" && Bars != null)
                {
                    float rpm = InstrumentDriver.Value(a, "rpm", out _, out _) / 110f;
                    float thr = InstrumentDriver.Value(a, "throttle", out _, out _);
                    float fuel = InstrumentDriver.Value(a, "fuel", out _, out _);
                    var v = new[] { rpm, thr, fuel };
                    for (int i = 0; i < Bars.Length && i < 3; i++)
                        if (Bars[i] != null) Bars[i].localScale = new Vector3(1, Mathf.Clamp(v[i], 0.001f, 1f), 1);
                }
                if (Time.time < _next) return;
                _next = Time.time + 0.1f;
                float kt = a.speed * 1.94384f, ft = a.transform.position.GlobalY() * 3.28084f;
                float hdg = InstrumentDriver.Value(a, "heading", out _, out _);
                switch (Page)
                {
                    case "flight":
                        Set(Speed, $"{kt:0}");
                        Set(Altitude, $"{ft:0}");
                        Set(Heading, $"{Mathf.Round(hdg) % 360:000}");
                        Set(Bottom, $"G {InstrumentDriver.Value(a, "g", out _, out _):0.0}   AOA {InstrumentDriver.Value(a, "aoa", out _, out _):0}");
                        break;
                    case "engine":
                        {
                            Set(BarTexts[0], $"{InstrumentDriver.Value(a, "rpm", out _, out _):0}%");
                            Set(BarTexts[1], $"{InstrumentDriver.Value(a, "throttle", out _, out _) * 100f:0}%");
                            Set(BarTexts[2], $"{InstrumentDriver.Value(a, "fuel", out _, out _) * 100f:0}%");
                        }
                        break;
                    case "nav":
                        Set(Heading, $"HDG\n{Mathf.Round(hdg) % 360:000}");
                        Set(Altitude, $"RALT\n{a.radarAlt * 3.28084f:0}");
                        Set(Bottom, $"VS {InstrumentDriver.Value(a, "vertical-speed", out _, out _) * 196.85f:0} FT/MIN");
                        break;
                }
            }
            catch { enabled = false; }
        }

        private static void Set(TextMeshPro? t, string s) { if (t != null && t.text != s) t.text = s; }
    }

    /// <summary>
    /// Moves a lever about its base with the controls. The lever sticks out along local +z; turning it about local x moves its
    /// knob towards local -y (6 o'clock), about local y towards local +x.
    /// </summary>
    internal sealed class LeverDriver : MonoBehaviour
    {
        public string Reads = "throttle";
        private Aircraft? _aircraft;
        private Quaternion _rest;
        private float _gear;

        private void Start() { _aircraft = GetComponentInParent<Aircraft>(); _rest = transform.localRotation; }

        private void LateUpdate()
        {
            var a = _aircraft;
            if (a == null) return;
            try
            {
                Quaternion turn;
                switch (Reads)
                {
                    case "throttle":
                        // Idle back towards 6 o'clock, full throttle forward towards 12 o'clock.
                        turn = Quaternion.Euler(Mathf.Lerp(18f, -24f, Mathf.Clamp01(a.controlInputs.throttle)), 0, 0);
                        break;
                    case "gear":
                        _gear = Mathf.MoveTowards(_gear, a.gearDeployed ? 1f : 0f, Time.deltaTime * 2f);
                        turn = Quaternion.Euler(Mathf.Lerp(-30f, 30f, _gear), 0, 0);   // up with the gear up, down with it down
                        break;
                    default:
                        turn = Quaternion.Euler(Mathf.Clamp(a.controlInputs.pitch, -1f, 1f) * 14f, -Mathf.Clamp(a.controlInputs.roll, -1f, 1f) * 14f, 0);
                        break;
                }
                transform.localRotation = _rest * turn;
            }
            catch { enabled = false; }
        }
    }
}
