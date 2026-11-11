using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NuclearOption.SavedMission;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Developer self-test (config Testing/SelfTest). Once a single-player mission is running, spawns every OpenPlane
    /// plane and its stock donor in the air under AI control, then each OpenPlane plane on a runway for an AI takeoff,
    /// and writes what happened (altitude, speed, crashes) to BepInEx/OpenPlane-selftest.txt.
    /// Start the game with: -autoHost -mission "Free Flight - Heartland" -socket Offline -state SinglePlayer
    /// </summary>
    internal sealed class SelfTest : MonoBehaviour
    {
        private bool _started, _toldNotSinglePlayer;
        private readonly StringBuilder _report = new StringBuilder();

        private void Update()
        {
            if (_started || !Plugin.SelfTest.Value || !Plugin.Ready) return;
            // It spawns planes for everyone: never in multiplayer or the editor.
            if (GameManager.gameState != GameState.SinglePlayer)
            {
                if (!_toldNotSinglePlayer && GameManager.gameState == GameState.Multiplayer)
                {
                    _toldNotSinglePlayer = true;
                    Plugin.Log.LogInfo("[SelfTest] SelfTest is on, but it only runs in single-player missions.");
                }
                return;
            }
            if (!MissionManager.IsRunning || FactionRegistry.HQLookup.Count == 0 || FactionRegistry.airbaseLookup.Count == 0) return;
            if (NetworkSceneSingleton<Spawner>.i == null) return;
            _started = true;
            StartCoroutine(Run());
        }

        private void Line(string s)
        {
            _report.AppendLine(s);
            Plugin.Log.LogInfo("[SelfTest] " + s);
        }

        private sealed class Tracked
        {
            public string Label = "";
            public AircraftDefinition Def = null!;
            public Aircraft? Plane;
            public float StartAlt, MinAlt = float.MaxValue, MaxAlt = float.MinValue, MinSpeed = float.MaxValue, MaxSpeed;
            public float LastAlt, LastSpeed, LastRadarAlt = float.MaxValue;
            public float Start;
            public bool Lost;
            public string Note = "";
            public bool FullTrace;
            public float LastTraceAt = -99f;
            public string LastState = "";
            public readonly StringBuilder Trace = new StringBuilder();
            public readonly StringBuilder States = new StringBuilder();
        }

        private SelfTestFilm? _film;

        private IEnumerator Run()
        {
            yield return new WaitForSeconds(8f);
            if (Plugin.SelfTestVideo.Value)
            {
                _film = new SelfTestFilm();
                foreach (var loop in _film.Loops()) StartCoroutine(loop);
            }
            Line($"OpenPlane self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}, mission \"{MissionManager.CurrentMission?.Name}\", {Registry.Planes.Count} OpenPlane planes");

            var hq = FactionRegistry.HQLookup.Values.FirstOrDefault(h => h != null);
            var airbases = FactionRegistry.airbaseLookup.Values.Where(a => a != null).ToList();
            var home = airbases.FirstOrDefault(a => a.CurrentHQ == hq) ?? airbases.First();
            Line($"faction {hq?.name}, airbase {home.name}");

            // ---- air start: OpenPlane planes and their stock donors side by side
            var defs = new List<(AircraftDefinition def, string label)>();
            foreach (var p in Registry.Planes)
            {
                defs.Add((p.Definition, "OpenPlane " + p.Definition.unitName));
                if (!defs.Any(d => d.def == p.Donor)) defs.Add((p.Donor, "stock " + p.Donor.unitName));
            }
            // Spread over our side's airbases: planes with nothing to attack fly home to land after about 20 s, and all of them
            // landing at one airstrip ended in mid-air collisions that said nothing about the planes.
            var ours = airbases.Where(a => a.CurrentHQ == hq).ToList();
            if (ours.Count == 0) ours = airbases;
            var perBase = new Dictionary<Airbase, int>();
            var tracked = new List<Tracked>();
            for (int i = 0; i < defs.Count; i++)
            {
                var (def, label) = defs[i];
                var at = ours[i % ours.Count];
                int k = perBase.TryGetValue(at, out var n0) ? n0 : 0;
                perBase[at] = k + 1;
                var forward = Vector3.ProjectOnPlane(at.transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
                var right = Vector3.Cross(Vector3.up, forward);
                var rot = Quaternion.LookRotation(forward, Vector3.up);
                var pos = at.transform.position + Vector3.up * 1500f + right * ((k - 1.5f) * 900f) - forward * 3000f;
                float speed = Mathf.Max(120f, def.aircraftParameters != null ? def.aircraftParameters.takeoffSpeed * 1.7f : 150f);
                var t = new Tracked { Label = label, Def = def };
                try
                {
                    t.Plane = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, def.unitPrefab, new Loadout(), 0.5f, default, pos.ToGlobalPosition(),
                        rot, forward * speed, null, hq, "OP test " + def.unitName, 1f, 0.5f);
                    t.StartAlt = pos.GlobalY();
                }
                catch (Exception e) { t.Note = "spawn failed: " + e.Message; t.Lost = true; }
                tracked.Add(t);
            }
            Line($"air start: {tracked.Count(t => t.Plane != null)} of {tracked.Count} spawned at 1500 m near {ours.Count} airbases");

            var alive = tracked.Where(t => !t.Lost).ToList();
            yield return StartCoroutine(Watch(alive, 4f, 5f));
            // Where the game really applies each plane's lift, and what it carries, before anything else happens to it.
            foreach (var t in tracked) PitchAudit(t);
            foreach (var t in tracked) yield return StartCoroutine(Shot(t.Plane, t.Label, "1 air start", View.Chase));
            // From each OpenPlane plane's own cockpit: its interior, instruments and canopy.
            foreach (var t in tracked.Where(t => t.Label.StartsWith("OpenPlane "))) yield return StartCoroutine(CockpitShot(t.Plane, t.Label));
            yield return StartCoroutine(Watch(alive, 50f, 5f, keepStart: true));
            foreach (var t in tracked) yield return StartCoroutine(Shot(t.Plane, t.Label, "2 flying", View.Side));
            yield return StartCoroutine(Watch(alive, 36f, 5f, keepStart: true));
            foreach (var t in tracked)
            {
                Line($"  {t.Label,-34} {AirResult(t)}  alt {t.StartAlt:0} -> {t.LastAlt:0} m (min {t.MinAlt:0}, max {t.MaxAlt:0}), speed {t.MinSpeed:0}-{t.MaxSpeed:0} m/s {t.Note}");
                Trace(t);
            }

            // The air-start planes go, so none comes back to land on a runway the takeoff test uses.
            foreach (var t in tracked) RemovePlane(t.Plane);
            yield return new WaitForSeconds(2f);

            // ---- runway start: every OpenPlane plane at once, spread over the airbases that offer it (fewest test planes first)
            //      so takeoff queues stay short. Each is watched for up to 180 s from when it appears, then removed. A plane no
            //      hangar takes right now is tried again every 15 s.
            var usedCount = new Dictionary<Airbase, int>();
            var runs = new List<Takeoff>();
            var waiting = Registry.Planes.ToList();
            var known = new HashSet<Aircraft>(UnitRegistry.allAircraft.Where(a => a != null));
            float phase = Time.time, lastTry = -999f;
            const float window = 300f, phaseLimit = 720f;
            while (Time.time - phase < phaseLimit)
            {
                if (waiting.Count > 0 && Time.time - lastTry >= 15f)
                {
                    lastTry = Time.time;
                    foreach (var p in waiting.ToList())
                    {
                        Airbase? used = null;
                        // Our side's airbases first (a plane spawned at another side's base flies for that side), least used first.
                        var order = airbases.OrderBy(a => a.CurrentHQ == hq ? 0 : 1).ThenBy(a => usedCount.TryGetValue(a, out var n) ? n : 0).ToList();
                        foreach (var ab in order)
                        {
                            try
                            {
                                if (runs.Count(r => r.Base == ab && !r.Done) >= 2) continue;
                                if (!ab.CanSpawnAircraft(p.Definition)) continue;
                                var r = ab.TrySpawnAircraft(null, p.Definition, default, new Loadout(), 0.5f);
                                if (r.Allowed) { used = ab; break; }
                            }
                            catch (Exception e) { Plugin.Log.LogInfo($"[SelfTest] spawn error for {p.Definition.unitName} at {ab.name}: {e.Message}"); }
                        }
                        if (used == null) continue;
                        usedCount[used] = (usedCount.TryGetValue(used, out var c) ? c : 0) + 1;
                        runs.Add(new Takeoff { Entry = p, Base = used, Queued = Time.time,
                                               T = new Tracked { Label = "takeoff " + p.Definition.unitName, Def = p.Definition, FullTrace = true } });
                        waiting.Remove(p);
                    }
                }
                foreach (var r in runs.Where(r => !r.Bound && !r.Done))
                {
                    var a = UnitRegistry.allAircraft.FirstOrDefault(x => x != null && !known.Contains(x) && x.definition == r.Entry.Definition);
                    if (a != null)
                    {
                        known.Add(a); r.Bound = true;
                        r.T.Plane = a; r.T.StartAlt = a.transform.position.GlobalY(); r.T.Start = Time.time;
                    }
                    else if (Time.time - r.Queued > 90f) { r.Done = true; r.Result = "queued but never appeared in 90 s"; }
                }
                foreach (var r in runs.Where(r => r.Bound && !r.Done))
                {
                    Sample(r.T);
                    float since = Time.time - r.T.Start;
                    // Taxiing and waiting in the takeoff queue aren't the plane's fault: its 90 s start with the takeoff run.
                    if (r.RollAt < 0 && r.T.LastState == "taking off")
                    {
                        r.RollAt = since;
                        if (_film != null && r.T.Plane != null)
                            _film.Add(new SelfTestFilm.Request { Plane = r.T.Plane, Label = r.Entry.Definition.unitName, Kind = SelfTestFilm.Kind.Takeoff, Seconds = 13f,
                                                                 Along = r.T.Plane.transform.forward, Priority = 2, Deadline = Time.time + 10f });
                    }
                    if (r.AirborneAt < 0 && r.T.MaxAlt > r.T.StartAlt + 60) r.AirborneAt = since;
                    var label = r.Entry.Definition.unitName;
                    if (!r.ShotGround && since >= 3f && !r.T.Lost && r.RollAt < 0)
                    {
                        yield return StartCoroutine(Shot(r.T.Plane, label, "3 on the ground", View.Front));
                        r.ShotGround = _lastShotTaken;
                    }
                    if (!r.ShotRoll && r.RollAt >= 0 && r.T.LastSpeed > 25f && r.T.LastRadarAlt < 3f && !r.T.Lost) { r.ShotRoll = true; yield return StartCoroutine(Shot(r.T.Plane, label, "4 takeoff run", View.ChaseLow)); }
                    if (!r.ShotClimb && r.AirborneAt >= 0 && since - r.AirborneAt >= 4f && !r.T.Lost)
                    {
                        r.ShotClimb = true;
                        yield return StartCoroutine(Shot(r.T.Plane, label, "5 climbing out", View.Side));
                        if (_film != null && r.T.Plane != null)
                            _film.Add(new SelfTestFilm.Request { Plane = r.T.Plane, Label = label, Kind = SelfTestFilm.Kind.Climb, Seconds = 8f, Priority = 1, Deadline = Time.time + 12f });
                    }
                    if (!r.ShotLost && r.T.Lost && r.T.Plane != null) { r.ShotLost = true; yield return StartCoroutine(Shot(r.T.Plane, label, "6 lost", View.Chase)); }
                    bool climbedAway = r.AirborneAt >= 0 && since - r.AirborneAt > 25f && (_film == null || since - r.AirborneAt > 45f || _film.Idle);
                    bool timedOut = r.RollAt >= 0 ? since - r.RollAt >= 90f : since >= window;
                    if (r.T.Lost || timedOut || climbedAway)
                    {
                        r.Done = true;
                        if (!r.T.Lost) RemovePlane(r.T.Plane);
                    }
                }
                if (waiting.Count == 0 && runs.All(r => r.Done)) break;
                yield return new WaitForSeconds(2f);
            }
            int airborne = 0;
            foreach (var r in runs)
            {
                var t = r.T;
                string result = r.Result ?? (r.AirborneAt >= 0 ? $"AIRBORNE after {r.AirborneAt:0} s" + (r.RollAt >= 0 ? $" ({r.AirborneAt - r.RollAt:0} s after starting its run)" : "") + (t.Lost ? " but LOST later" : "")
                                           : t.Lost ? "LOST"
                                           : r.RollAt < 0 ? $"never started its takeoff run (AI: {t.LastState})"
                                           : "still on the ground 90 s after starting its run");
                if (r.AirborneAt >= 0 && !t.Lost) airborne++;
                Line($"  {t.Label,-34} at {r.Base.name}: {result}, alt {t.StartAlt:0} -> {t.LastAlt:0} m (max {t.MaxAlt:0}), top speed {t.MaxSpeed:0} m/s {t.Note}");
                Trace(t);
            }
            foreach (var p in waiting) Line($"  takeoff {p.Definition.unitName}: no airbase would spawn it");
            int planes = Registry.Planes.Count;
            // Flying = not lost and still clear of the ground at the end (a plane gliding in counts as down).
            int flying = tracked.Count(t => !t.Lost && t.LastRadarAlt > 20f && t.Label.StartsWith("OpenPlane "));
            int landing = tracked.Count(t => !t.Lost && !(t.LastRadarAlt > 20f) && t.Label.StartsWith("OpenPlane "));
            Line($"SCORE air start {flying}/{planes} flying" + (landing > 0 ? $" and {landing} landing or landed" : "") + $", runway takeoff {airborne}/{planes} airborne");

            if (_film != null) yield return StartCoroutine(Showcase(hq, ours));

            Line($"screenshots: {_shots} saved in BepInEx/OpenPlane-shots" + (_blackShots > 0 ? $" ({_blackShots} came out black: the test camera may not render with this game's pipeline)" : ""));
            if (_cam != null) { Destroy(_cam.gameObject); _cam = null; }
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            if (_film != null) { _film.Stop = true; while (!_film.Idle) yield return null; Line(_film.Finish()); }
            Line("self-test done");
            try { File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-selftest.txt"), _report.ToString()); } catch { }
            if (Plugin.SelfTestQuit.Value) { yield return new WaitForSeconds(2f); Application.Quit(); }
        }

        /// <summary>
        /// Video only: each OpenPlane plane on its own in clear air near one of our airbases, filmed from behind, from the side
        /// as it passes, from its cockpit and in a slow turn around it, then removed.
        /// </summary>
        private IEnumerator Showcase(FactionHQ? hq, List<Airbase> ours)
        {
            var film = _film!;
            Line("showcase: filming each plane on its own");
            int i = 0;
            foreach (var p in Registry.Planes.ToList())
            {
                var def = p.Definition;
                var at = ours[i++ % ours.Count];
                var forward = Vector3.ProjectOnPlane(at.transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
                var pos = at.transform.position + Vector3.up * 900f - forward * 2500f;
                float speed = Mathf.Max(110f, def.aircraftParameters != null ? def.aircraftParameters.takeoffSpeed * 1.8f : 150f);
                Aircraft? a = null;
                try
                {
                    a = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, def.unitPrefab, new Loadout(), 1f, default, pos.ToGlobalPosition(),
                        Quaternion.LookRotation(forward, Vector3.up), forward * speed, null, hq, "OP film " + def.unitName, 1f, 0.5f);
                }
                catch (Exception e) { Line($"  showcase {def.unitName}: spawn failed: {e.Message}"); continue; }
                yield return new WaitForSeconds(3f);
                var name = def.unitName;
                foreach (var (kind, secs) in new[] { (SelfTestFilm.Kind.Chase, 7f), (SelfTestFilm.Kind.Flyby, 6f), (SelfTestFilm.Kind.Cockpit, 6f), (SelfTestFilm.Kind.Orbit, 7f) })
                {
                    if (a == null || a.disabled) break;
                    film.Add(new SelfTestFilm.Request { Plane = a, Label = name, Kind = kind, Seconds = secs });
                    yield return null;
                    while (!film.Idle) yield return null;
                }
                bool ok = a != null && !a.disabled;
                Line($"  showcase {name}: {(ok ? "filmed" : "lost while filming")}");
                if (ok) yield return StartCoroutine(Shot(a, "OpenPlane " + name, "9 showcase", View.Side));
                RemovePlane(a);
                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Logs where the game applies each lifting part's lift on a plane in flight (its body's centre of mass plus the part's lift
        /// offset, as AeroPart does in full physics), measured from the whole plane's real centre of mass, and the pitch numbers
        /// OpenPlane's balance works with: stiffness (area x distance behind the centre of mass) and the pitch controls' share.
        /// Also the weapons it carries and whether it is in full or simple physics.
        /// </summary>
        private void PitchAudit(Tracked t)
        {
            var a = t.Plane;
            if (a == null || t.Lost) return;
            try
            {
                var parts = (a.partLookup ?? new List<UnitPart>()).Where(p => p != null).ToList();
                var bodies = parts.Select(p => p.rb).Where(rb => rb != null).Distinct().ToList();
                if (a.rb != null && !bodies.Contains(a.rb)) bodies.Add(a.rb);
                float mass = bodies.Sum(rb => rb.mass);
                if (!(mass > 0)) return;
                var com = bodies.Aggregate(Vector3.zero, (acc, rb) => acc + rb.worldCenterOfMass * rb.mass) / mass;
                var fwd = a.transform.forward; var up = a.transform.up;
                // Each control surface sits on its part's own object (parts leave the plane's hierarchy in full physics).
                var pitchParts = new HashSet<AeroPart>(parts.OfType<AeroPart>().Where(p => p.GetComponents<ControlSurface>().Any(c => c != null && Mathf.Abs(c.pitchRange) > 0.01f)));
                double stiff = 0, ctrl = 0, area = 0;
                var lines = new List<string>();
                foreach (var p in parts.OfType<AeroPart>())
                {
                    if (!(p.wingArea > 0) || p.rb == null) continue;
                    var ln = p.liftNormal != null ? p.liftNormal : p.transform;
                    if (Vector3.Dot(ln.up, up) < 0.5f) continue;   // fins and other parts that don't lift upwards
                    var point = p.rb.worldCenterOfMass + ln.rotation * p.centerOfLift;
                    float behind = Vector3.Dot(com - point, fwd);
                    stiff += p.wingArea * behind; area += p.wingArea;
                    bool control = pitchParts.Contains(p);
                    if (control) ctrl += p.wingArea * behind;
                    lines.Add($"{p.name}{(control ? "*" : "")} {p.wingArea:0.0}m2@{behind:0.00}");
                }
                float load = 0;
                if (a.weaponManager?.hardpointSets != null)
                    foreach (var set in a.weaponManager.hardpointSets)
                        if (set?.weaponMount != null) load += Mathf.Max(set.weaponMount.mass, set.weaponMount.emptyMass) * Mathf.Max(1, set.hardpoints?.Count ?? 1);
                double reach = stiff > 1e-6 ? Math.Abs(ctrl) / stiff : double.PositiveInfinity;
                var localCom = a.transform.InverseTransformPoint(com);
                Line($"  pitch {t.Label,-32} {(a.simplePhysics ? "SIMPLE" : "full")} physics, {mass:0} kg with {load:0} kg of weapons, centre of gravity z {localCom.z:0.00}, lifting area {area:0.0} m2, " +
                     $"stiffness {stiff:0.0}, pitch controls {ctrl:0.0} (reach {(double.IsInfinity(reach) ? "n/a" : reach.ToString("0.00"))}); parts (area@m behind CG, * = pitch control): {string.Join(" ", lines)}");
            }
            catch (Exception e) { Line($"  pitch {t.Label}: audit failed: {e.Message}"); }
        }

        /// <summary>How an air-start plane ended: flying, down low because the AI is landing it, or lost.</summary>
        private static string AirResult(Tracked t)
        {
            if (t.Lost) return "LOST";
            if (t.LastRadarAlt > 20f) return "flying";
            var s = t.LastState.ToLowerInvariant();
            if (s.Contains("final") || s.Contains("approach") || s.Contains("land") || s.Contains("taxi") || s.Contains("pattern") || t.LastSpeed < 5f)
                return $"landing (AI: {t.LastState})";
            return $"LOW (under 20 m, AI: {t.LastState})";
        }

        private sealed class Takeoff
        {
            public RegisteredPlane Entry = null!;
            public Airbase Base = null!;
            public Tracked T = null!;
            public float Queued, AirborneAt = -1, RollAt = -1;
            public bool Bound, Done, ShotGround, ShotRoll, ShotClimb, ShotLost;
            public string? Result;
        }

        // ------------------------------------------------------------------ screenshots

        private enum View { Chase, Side, Front, ChaseLow }
        private Camera? _cam;
        private RenderTexture? _rt;
        private Texture2D? _tex;
        private int _shots, _blackShots;
        private const int ShotWidth = 1920, ShotHeight = 1080;
        private static string ShotDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-shots");

        /// <summary>
        /// A picture of one plane from a camera of the test's own (the game's view is untouched): it renders into a texture,
        /// which is saved as a JPG in BepInEx/OpenPlane-shots.
        /// </summary>
        /// <summary>Set when the last <see cref="Shot"/> saved a picture (a view blocked by buildings or terrain saves none).</summary>
        private bool _lastShotTaken;

        private IEnumerator Shot(Aircraft? plane, string label, string condition, View view)
        {
            _lastShotTaken = false;
            if (plane == null) yield break;
            yield return new WaitForEndOfFrame();
            try
            {
                if (plane == null) yield break;
                if (_cam == null) SetUpCamera();
                var (center, size) = Frame(plane);
                var up = Vector3.up;
                var fwd = Vector3.ProjectOnPlane(plane.transform.forward, up);
                fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                var right = Vector3.Cross(up, fwd);
                Vector3 Dir(View v) => v switch
                {
                    View.Side => right * 1.0f - fwd * 0.15f + up * 0.12f,
                    View.Front => fwd * 0.8f + right * 0.45f + up * 0.18f,
                    View.ChaseLow => -fwd * 0.9f + right * 0.3f + up * 0.08f,
                    _ => -fwd * 0.85f + right * 0.35f + up * 0.3f,
                };
                float dist = size * 1.15f + 3f;
                // The asked-for view first, then the others, then the same from the left: the first with a clear line of sight.
                var tries = new List<Vector3> { Dir(view) };
                foreach (View v in Enum.GetValues(typeof(View))) if (v != view) tries.Add(Dir(v));
                foreach (var d in tries.ToList()) tries.Add(Vector3.Reflect(d, right));
                Vector3? pos = null;
                foreach (var d in tries)
                {
                    var p = center + d.normalized * dist;
                    if (!Blocked(plane, center, p)) { pos = p; break; }
                }
                if (pos == null) yield break;   // inside a hangar, behind a hill: try again later
                _cam!.transform.position = pos.Value;
                _cam.transform.LookAt(center, up);
                _cam.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = _rt;
                _tex!.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                _tex.Apply();
                RenderTexture.active = previous;
                if (LooksBlack(_tex)) _blackShots++;
                var name = $"{Safe(label)} - {condition}.jpg";
                File.WriteAllBytes(Path.Combine(ShotDir, name), _tex.EncodeToJPG(88));
                _shots++;
                _lastShotTaken = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SelfTest] screenshot of {label} ({condition}) failed: {e.Message}"); }
        }

        /// <summary>A picture from the plane's cockpit eye (its cockpit view point), looking forward with a wide lens.</summary>
        private IEnumerator CockpitShot(Aircraft? plane, string label)
        {
            if (plane == null) yield break;
            yield return new WaitForEndOfFrame();
            try
            {
                if (plane == null) yield break;
                if (_cam == null) SetUpCamera();
                var eye = plane.cockpitViewPoint != null ? plane.cockpitViewPoint : (plane.cockpit != null ? plane.cockpit.transform : plane.transform);
                float fov = _cam!.fieldOfView, near = _cam.nearClipPlane;
                _cam.fieldOfView = 75f; _cam.nearClipPlane = 0.02f;
                void Take(string name)
                {
                    _cam.Render();
                    var previous = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _tex!.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                    _tex.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(Path.Combine(ShotDir, $"{Safe(label)} - {name}.jpg"), _tex.EncodeToJPG(88));
                    _shots++;
                }
                _cam.transform.position = eye.position;
                _cam.transform.rotation = Quaternion.LookRotation(plane.transform.forward, plane.transform.up) * Quaternion.Euler(8f, 0, 0);
                Take("7 cockpit view");
                // A close look at the design's own instruments from the eye: are they the right way up, and do they read?
                var instruments = plane.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("OpenPlane instrument ", StringComparison.Ordinal)).Select(t => t.position).ToList();
                if (instruments.Count > 0)
                {
                    var centre = instruments.Aggregate(Vector3.zero, (s, p) => s + p) / instruments.Count;
                    var look = centre - eye.position;
                    if (look.sqrMagnitude > 1e-4f)
                    {
                        float spread = instruments.Max(p => Vector3.Distance(p, centre)) + 0.08f;
                        _cam.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(spread / look.magnitude) * Mathf.Rad2Deg * 1.15f, 20f, 75f);
                        _cam.transform.rotation = Quaternion.LookRotation(look, plane.transform.up);
                        Take("8 instruments");
                    }
                }
                _cam.fieldOfView = fov; _cam.nearClipPlane = near;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SelfTest] cockpit picture of {label} failed: {e.Message}"); }
        }

        /// <summary>
        /// The test camera, set up like the game's own (culling, clear flags and, through reflection, the render pipeline's
        /// per-camera data such as which renderer it uses), so scenery draws as the player sees it.
        /// </summary>
        private void SetUpCamera()
        {
            Directory.CreateDirectory(ShotDir);
            _cam = MakeCamera("OpenPlane test camera");
            _rt = new RenderTexture(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            _cam.targetTexture = _rt;
            _tex = new Texture2D(ShotWidth, ShotHeight, TextureFormat.RGB24, false);
        }

        /// <summary>A disabled camera set up like the game's own, rendered by hand into a texture.</summary>
        internal static Camera MakeCamera(string name)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            var game = Camera.main ?? Camera.allCameras.Where(c => c != null && c != cam && c.enabled && c.targetTexture == null).OrderByDescending(c => c.depth).FirstOrDefault();
            if (game != null)
            {
                cam.CopyFrom(game);
                try
                {
                    // UniversalAdditionalCameraData (URP) holds the renderer, depth/opaque texture and post-processing choices.
                    var data = game.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "UniversalAdditionalCameraData");
                    if (data != null)
                    {
                        var ours = go.GetComponent(data.GetType()) ?? go.AddComponent(data.GetType());
                        foreach (var field in data.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        {
                            var t = field.FieldType;
                            if (typeof(Camera).IsAssignableFrom(t) || (t.IsGenericType && t.GetGenericArguments().Any(a => typeof(Camera).IsAssignableFrom(a)))) continue;
                            if (typeof(Component).IsAssignableFrom(t)) continue;
                            try { field.SetValue(ours, field.GetValue(data)); } catch { }
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogInfo("[SelfTest] test camera: couldn't copy the render settings: " + e.Message); }
            }
            cam.enabled = false;
            cam.fieldOfView = 35f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 40000f);
            return cam;
        }

        /// <summary>True when something other than the plane itself is between the plane and the camera position.</summary>
        internal static bool Blocked(Aircraft plane, Vector3 from, Vector3 to)
        {
            var d = to - from;
            foreach (var hit in Physics.RaycastAll(from, d.normalized, d.magnitude, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && hit.collider.GetComponentInParent<Aircraft>() != plane) return true;
            // The camera itself under the ground or inside something.
            return Physics.CheckSphere(to, 0.4f, ~0, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Where to point the camera: the design's own renderers (or every visible renderer of a stock plane).</summary>
        internal static (Vector3 center, float size) Frame(Aircraft plane)
        {
            Bounds? b = null;
            var renderers = plane.GetComponentsInChildren<Renderer>(false);
            bool design = renderers.Any(x => x != null && x.name == "OpenPlane Body");
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                bool ours = r.name == "OpenPlane Body" || r.name == "OpenPlane Wings";
                if (!ours && design) continue;
                if (r.bounds.size.sqrMagnitude < 1e-6f || r.bounds.size.magnitude > 200f) continue;
                if (b == null) b = r.bounds; else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
            }
            if (b == null) return (plane.transform.position, 15f);
            return (b.Value.center, Mathf.Clamp(b.Value.size.magnitude, 6f, 80f));
        }

        private static bool LooksBlack(Texture2D t)
        {
            float sum = 0; int n = 0;
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 8; y++) { var c = t.GetPixel(x * t.width / 8 + 4, y * t.height / 8 + 4); sum += c.r + c.g + c.b; n++; }
            return sum / (3 * n) < 0.02f;
        }

        internal static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = s.Select(c => bad.Contains(c) || c == '\'' ? '_' : c).ToArray();
            return new string(chars).Trim();
        }

        /// <summary>Removes a test plane the way the game removes planes on the host (it destroys the object).</summary>
        private static void RemovePlane(Aircraft? a)
        {
            try
            {
                if (a == null) return;
                if (!a.disabled && a.NetworkHQ != null) a.ReturnToInventory();   // the game's own path: supply back, then removed
                else Destroy(a.gameObject);
            }
            catch { try { if (a != null) Destroy(a.gameObject); } catch { } }
        }

        /// <summary>Samples every 0.25 s for the first 10 s (where light planes were lost), then every <paramref name="later"/> s.</summary>
        private static IEnumerator Watch(List<Tracked> tracked, float seconds, float later, bool keepStart = false)
        {
            float begin = Time.time;
            if (!keepStart) foreach (var t in tracked) t.Start = begin;
            float first = tracked.Count > 0 ? tracked[0].Start : begin;
            while (Time.time - begin < seconds && tracked.Any(t => !t.Lost))
            {
                yield return new WaitForSeconds(Time.time - first < 10f ? 0.25f : later);
                foreach (var t in tracked.Where(t => !t.Lost)) Sample(t);
            }
        }

        /// <summary>Records altitude and speed first, then marks the plane lost if it is gone or disabled.</summary>
        private static void Sample(Tracked t)
        {
            float since = Time.time - t.Start;
            if (t.Plane == null)
            {
                t.Lost = true;
                if (t.Note.Length == 0) t.Note = $"(destroyed by t={since:0.00} s; last seen at {t.LastAlt:0} m, {t.LastSpeed:0} m/s)";
                return;
            }
            float alt = t.Plane.transform.position.GlobalY();
            float spd = t.Plane.rb != null ? t.Plane.rb.velocity.magnitude : 0;
            t.LastAlt = alt; t.LastSpeed = spd;
            try { t.LastRadarAlt = t.Plane.radarAlt; } catch { }
            t.MinAlt = Mathf.Min(t.MinAlt, alt); t.MaxAlt = Mathf.Max(t.MaxAlt, alt);
            t.MinSpeed = Mathf.Min(t.MinSpeed, spd); t.MaxSpeed = Mathf.Max(t.MaxSpeed, spd);
            if (since <= 10.01f || (t.FullTrace && since - t.LastTraceAt >= 6f))
            {
                string extra = "";
                try
                {
                    var v = t.Plane.rb != null ? t.Plane.rb.velocity : Vector3.zero;
                    if (v.sqrMagnitude > 25f)
                    {
                        var local = t.Plane.transform.InverseTransformDirection(v);
                        float aoa = Mathf.Atan2(-local.y, local.z) * Mathf.Rad2Deg;
                        extra = $"/a{aoa:0}/p{t.Plane.controlInputs.pitch:0.0}";
                    }
                }
                catch { }
                t.Trace.Append($" {since:0.##}:{alt:0}/{spd:0}{extra}");
                t.LastTraceAt = since;
            }
            try
            {
                var pilot = t.Plane.pilots != null && t.Plane.pilots.Length > 0 ? t.Plane.pilots[0] : null;
                var state = pilot != null ? pilot.GetCurrentState() : "no pilot";
                if (state != t.LastState) { t.States.Append($" {since:0}s {state};"); t.LastState = state; }
            }
            catch { }
            if (t.Plane.disabled)
            {
                t.Lost = true;
                if (t.Note.Length == 0) t.Note = $"(disabled at t={since:0.00} s at {alt:0} m, {spd:0} m/s)";
            }
        }

        private void Trace(Tracked t)
        {
            if (t.Trace.Length > 0) Line($"    {t.Label} {(t.FullTrace ? "trace" : "first 10 s")} (t:alt m/speed m/s/angle of attack/pitch input):{t.Trace}");
            if (t.States.Length > 0) Line($"    {t.Label} AI:{t.States}");
        }
    }
}
