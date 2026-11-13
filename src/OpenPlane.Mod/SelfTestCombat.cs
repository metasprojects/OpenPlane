using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.SavedMission;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Developer self-test phases beyond the air start and takeoff (config Developer/SelfTestPhases):
    /// - landing: each plane starts in the air near one of our airbases with nothing to attack, the AI flies home and lands;
    ///   the touchdown and the stop are logged.
    /// - weapons: for every store the plane can carry (each one its own sortie, loaded on every station that offers it) and
    ///   its guns, the plane starts in the air with matching targets ahead (an unarmed enemy plane for air-to-air stores,
    ///   enemy vehicles for the rest, a radar vehicle for anti-radiation stores). The targets are reported to our side the
    ///   way a datalink would, so the AI attacks; every round or store fired and every target hit is logged.
    /// Single-player only, like the rest of the self-test.
    /// </summary>
    internal sealed partial class SelfTest
    {
        /// <summary>
        /// The planes a phase covers: those whose name contains the phase's own filter ("landing:Lanner"), else a SelfTestOnly
        /// entry, else every OpenPlane plane.
        /// </summary>
        private static List<RegisteredPlane> Selected(string? phase = null)
        {
            string filter = Plugin.SelfTestOnly.Value ?? "";
            if (phase != null)
                foreach (var p in PhaseEntries())
                    if (p.name == phase && p.filter.Length > 0) filter = p.filter;
            var only = filter.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            return Registry.Planes.Where(p => only.Count == 0 || only.Any(o => (p.Definition.unitName ?? "").IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        /// <summary>The phases asked for: "air,runway,landing:Lanner,weapons:Lanner" (a filter after ":", several joined by "|").</summary>
        private static IEnumerable<(string name, string filter)> PhaseEntries()
            => (Plugin.SelfTestPhases.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0)
                .Select(s => { int c = s.IndexOf(':'); return c < 0 ? (s.ToLowerInvariant(), "") : (s.Substring(0, c).Trim().ToLowerInvariant(), s.Substring(c + 1).Trim()); });

        private static bool Phase(string name) => PhaseEntries().Any(p => p.name == name);

        // ------------------------------------------------------------------ landing

        private IEnumerator LandingPhase(FactionHQ? hq, List<Airbase> ours)
        {
            var planes = Selected("landing");
            // Each stock donor lands too, once, as the yardstick for touchdowns and wheels.
            var defs = new List<AircraftDefinition>();
            foreach (var p in planes) { defs.Add(p.Definition); if (!defs.Contains(p.Donor)) defs.Add(p.Donor); }
            Line($"landing: {defs.Count} planes (with their stock planes) start 8 km out at 600 m with nothing to attack; the AI flies home and lands");
            int i = 0;
            int landed = 0;
            foreach (var def in defs)
            {
                var at = ours[i++ % ours.Count];
                var forward = Vector3.ProjectOnPlane(at.transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
                var pos = at.transform.position + Vector3.up * 600f - forward * 8000f;
                float speed = Mathf.Max(100f, def.aircraftParameters != null ? def.aircraftParameters.takeoffSpeed * 1.5f : 130f);
                var t = new Tracked { Label = "landing " + def.unitName, Def = def, FullTrace = true };
                try
                {
                    t.Plane = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, def.unitPrefab, new Loadout(), 0.3f, default, pos.ToGlobalPosition(),
                        Quaternion.LookRotation(forward, Vector3.up), forward * speed, null, hq, "OP land " + def.unitName, 1f, 0.5f);
                    t.StartAlt = pos.GlobalY();
                }
                catch (Exception e) { Line($"  {t.Label}: spawn failed: {e.Message}"); continue; }
                t.Start = Time.time;
                bool airborne = true, touched = false, shotFinal = false, shotDown = false;
                float touchSpeed = 0, touchSink = 0, touchAt = 0, stoppedFor = 0, prevAlt = t.Plane.transform.position.GlobalY(), prevTime = Time.time, taxiFrom = -1f;
                int brokenBefore = SelfTestHooks.WheelsBroken.Count;
                string result = "";
                while (Time.time - t.Start < 420f)
                {
                    yield return new WaitForSeconds(0.5f);
                    Sample(t);
                    // An AI pilot that has landed taxis to its parking spot and gets out: the plane is put away, which is the end of
                    // a good flight, not a loss.
                    if (t.Lost && touched && !airborne && t.LastSpeed < 3f) { result = "LANDED, taxied in and parked (the pilot got out)"; landed++; break; }
                    if (t.Lost) { result = "LOST " + t.Note; break; }
                    var a = t.Plane!;
                    float alt = a.transform.position.GlobalY(), now = Time.time;
                    float sink = (prevAlt - alt) / Mathf.Max(0.05f, now - prevTime);
                    prevAlt = alt; prevTime = now;
                    if (!shotFinal && a.radarAlt < 60f && a.radarAlt > 10f && a.gearDeployed)
                    {
                        shotFinal = true;
                        yield return StartCoroutine(Shot(a, def.unitName, "a final approach", View.Side));
                        if (_film != null)
                            _film.Add(new SelfTestFilm.Request { Plane = a, Label = def.unitName, Kind = SelfTestFilm.Kind.Flyby, Seconds = 9f, Priority = 2, Deadline = Time.time + 6f });
                    }
                    if (airborne && a.radarAlt < 1.5f && t.MaxAlt > 0)
                    {
                        airborne = false;
                        if (!touched) { touched = true; touchSpeed = a.speed; touchSink = sink; touchAt = Time.time - t.Start; }
                    }
                    else if (a.radarAlt > 4f) airborne = true;
                    if (touched && !airborne && a.speed < 3f) stoppedFor += 0.5f; else stoppedFor = 0;
                    if (touched && !shotDown && !airborne && a.speed < 25f) { shotDown = true; yield return StartCoroutine(Shot(a, def.unitName, "b rollout", View.Front)); }
                    // Landed: stopped on the runway, or slowed and taxiing in (watched for 45 s of taxiing, where wheels break).
                    if (touched && !airborne && taxiFrom < 0 && a.speed < 25f && t.LastState.IndexOf("taxi", StringComparison.OrdinalIgnoreCase) >= 0) taxiFrom = Time.time;
                    if (stoppedFor >= 4f || (taxiFrom >= 0 && Time.time - taxiFrom >= 45f))
                    {
                        result = stoppedFor >= 4f ? "LANDED and stopped" : "LANDED and taxiing in";
                        landed++;
                        break;
                    }
                }
                if (result.Length == 0) result = touched ? "touched down but never stopped" : "never landed (still flying)";
                var broken = SelfTestHooks.WheelsBroken.Skip(brokenBefore).ToList();
                Line($"  {t.Label,-34} at {at.name}: {result}" + (touched ? $", touchdown at {touchAt:0} s, {touchSpeed:0} m/s ({touchSpeed * 1.944f:0} kt), sinking {touchSink:0.0} m/s" : "") +
                     $", min alt {t.MinAlt:0} m" + (broken.Count > 0 ? $"; WHEELS BROKEN: {string.Join(", ", broken)}" : "; no wheel broken"));
                Trace(t);
                if (!t.Lost) RemovePlane(t.Plane);
                yield return new WaitForSeconds(1f);
            }
            Line($"SCORE landing {landed}/{defs.Count} landed (OpenPlane planes and their stock planes)");
        }

        // ------------------------------------------------------------------ weapons

        private sealed class Sortie
        {
            public string Name = "";
            public WeaponMount? Mount;        // null: guns only
            public Loadout Loadout = new Loadout();
            public string Targets = "air";    // air, ground, radar
            public int Stations;
        }

        private static string Kind(WeaponInfo? w)
        {
            if (w == null || w.gun) return "air";   // guns: tried against a plane, where the AI uses them
            var e = w.effectiveness;
            if (e.antiRadar > 0.3f && e.antiRadar >= e.antiSurface) return "radar";
            if (e.antiAir > e.antiSurface) return "air";
            return "ground";
        }

        /// <summary>One sortie per store any open station of the plane offers (loaded on every station offering it), plus guns only.</summary>
        private static List<Sortie> Sorties(AircraftDefinition def)
        {
            var list = new List<Sortie>();
            var wm = def.unitPrefab.GetComponentInChildren<WeaponManager>(true);
            var sets = wm?.hardpointSets ?? new HardpointSet[0];
            bool IsGun(HardpointSet s) => s.hardpoints != null && s.hardpoints.Any(h => h != null && ((h.BuiltInWeapons?.Length ?? 0) > 0 || (h.BuiltInTurrets?.Length ?? 0) > 0));
            var mounts = new List<WeaponMount>();
            foreach (var s in sets)
                if (s?.weaponOptions != null && !IsGun(s))
                    foreach (var m in s.weaponOptions)
                        if (m != null && m.info != null && !m.Cargo && !m.Troops && !mounts.Contains(m)) mounts.Add(m);
            // One sortie per weapon: of the mounts carrying the same weapon (x1, x2, x3 racks), the one that loads the most rounds.
            int Rounds(WeaponMount m) => Math.Max(1, m.ammo) * sets.Count(s => s?.weaponOptions != null && !IsGun(s) && s.weaponOptions.Contains(m)) * Math.Max(1, sets.Where(s => s?.weaponOptions != null && s.weaponOptions.Contains(m)).Select(s => s.hardpoints?.Count ?? 1).DefaultIfEmpty(1).Max());
            mounts = mounts.GroupBy(m => m.info).Select(g => g.OrderByDescending(Rounds).First()).ToList();
            // Built-in guns always carry their first ammunition (their station's options are ammunition, not stores).
            // A station that only takes guns (the KR-67's cannon is such a loadout choice) carries its gun in every sortie too.
            bool BodyGun(HardpointSet? s) => s?.weaponOptions != null && s.weaponOptions.Any(o => o != null) && s.weaponOptions.Where(o => o != null).All(o => o.info != null && o.info.gun);
            WeaponMount? GunAmmo(HardpointSet? s) => s?.weaponOptions != null && (IsGun(s) || BodyGun(s)) ? s.weaponOptions.FirstOrDefault(o => o != null) : null;
            foreach (var m in mounts)
            {
                var lo = new Loadout { weapons = new List<WeaponMount>() };
                int n = 0;
                for (int i = 0; i < sets.Length; i++)
                {
                    bool offers = sets[i]?.weaponOptions != null && !IsGun(sets[i]) && sets[i].weaponOptions.Contains(m);
                    lo.weapons.Add(offers ? m : GunAmmo(sets[i])!);
                    if (offers) n++;
                }
                list.Add(new Sortie { Name = m.mountName ?? m.info.weaponName, Mount = m, Loadout = lo, Targets = Kind(m.info), Stations = n });
            }
            // Strategic stores last: they wreck the test area.
            list = list.OrderBy(s => s.Mount?.info != null && (s.Mount.info.nuclear || s.Mount.info.strategic) ? 1 : 0).ToList();
            var guns = new Loadout { weapons = sets.Select(s => GunAmmo(s)!).ToList() };
            if (sets.Any(s => s != null && IsGun(s)))
                list.Add(new Sortie { Name = "guns only", Mount = null, Loadout = guns, Targets = "air", Stations = guns.weapons.Count(w => w != null) });
            return list;
        }

        /// <summary>
        /// Somewhere on dry land about <paramref name="distance"/> from <paramref name="from"/>, trying headings around
        /// <paramref name="forward"/>; its ground point, or null if all of them are over water.
        /// </summary>
        private static Vector3? LandAhead(Vector3 from, Vector3 forward, float distance)
        {
            // The asked-for distance first, then nearer and further; 24 headings each, nearest to the asked-for one first.
            foreach (float dist in new[] { distance, distance * 0.7f, distance * 1.3f, distance * 0.5f })
                for (int k = 0; k < 24; k++)
                {
                    float turn = (k % 2 == 0 ? 1 : -1) * 15f * ((k + 1) / 2);
                    var dir = Quaternion.AngleAxis(turn, Vector3.up) * forward;
                    var p = from + dir * dist;
                    float y = GroundY(p);
                    var g = new Vector3(p.x, y, p.z);
                    // Sea level is at a global height of 0: well above it, on a slope a vehicle can stand on (under about 20°), is land.
                    if (g.GlobalY() < 5f) continue;
                    bool flat = true;
                    foreach (var o in new[] { Vector3.right * 60f, Vector3.left * 60f, Vector3.forward * 60f, Vector3.back * 60f })
                        if (Mathf.Abs(GroundY(g + o) - y) > 22f || (g + o).GlobalY() < 5f) flat = false;
                    if (flat) return g;
                }
            return null;
        }

        private static float GroundY(Vector3 at)
        {
            var from = new Vector3(at.x, at.y + 6000f, at.z);
            var hits = Physics.RaycastAll(from, Vector3.down, 20000f);
            float best = float.NegativeInfinity;
            foreach (var h in hits)
                if (h.collider != null && h.collider.GetComponentInParent<Unit>() == null) best = Mathf.Max(best, h.point.y);
            return float.IsNegativeInfinity(best) ? at.y : best;
        }

        private static void Reveal(FactionHQ? hq, Unit? u)
        {
            if (hq == null || u == null || u.disabled) return;
            try { hq.CmdUpdateTrackingInfo(u.persistentID); }
            catch (Exception e) { Plugin.Log.LogInfo($"[SelfTest] could not report {u.unitName} to our side: {e.Message}"); }
        }

        private static VehicleDefinition? PickVehicle(bool radar)
        {
            var all = Encyclopedia.i?.vehicles?.Where(v => v != null && v.unitPrefab != null).ToList() ?? new List<VehicleDefinition>();
            if (radar)
                return all.Where(v => v.typeIdentity.radar > 0).OrderBy(v => v.roleIdentity.antiAir).FirstOrDefault();
            return all.Where(v => v.typeIdentity.radar <= 0 && v.roleIdentity.antiAir <= 0.05f && v.armorTier > 0).OrderByDescending(v => v.armorTier).FirstOrDefault()
                ?? all.FirstOrDefault(v => v.roleIdentity.antiAir <= 0.05f);
        }

        private IEnumerator WeaponsPhase(FactionHQ? hq, List<Airbase> ours)
        {
            var enemy = FactionRegistry.HQLookup.Values.FirstOrDefault(h => h != null && h != hq);
            if (enemy == null) { Line("weapons: no enemy side in this mission, skipped"); yield break; }
            var targetPlane = Encyclopedia.i?.aircraft?.FirstOrDefault(a => a != null && a.jsonKey == "COIN")
                           ?? Encyclopedia.i?.aircraft?.FirstOrDefault(a => a != null && !Registry.IsOurs(a));
            var armor = PickVehicle(false);
            var radarVehicle = PickVehicle(true);
            Line($"weapons: enemy side {enemy.name}; targets: plane {targetPlane?.unitName ?? "none"}, vehicle {armor?.unitName ?? "none"}, radar {radarVehicle?.unitName ?? "none"}");
            int fired = 0, sorties = 0, kills = 0;
            int baseIndex = 0;
            foreach (var p in Selected("weapons"))
            {
                var def = p.Definition;
                var list = Sorties(def);
                Line($"  {def.unitName}: {list.Count} sorties: " + string.Join("; ", list.Select(s => $"{s.Name} ({s.Targets}, {s.Stations} stations)")));
                foreach (var sortie in list)
                {
                    sorties++;
                    var at = ours[baseIndex++ % ours.Count];
                    var forward = Vector3.ProjectOnPlane(at.transform.forward, Vector3.up).normalized;
                    if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
                    var right = Vector3.Cross(Vector3.up, forward);
                    // Out to the side of the base, away from its traffic; the targets 14 km on (on dry land for ground targets),
                    // and the plane pointed at them.
                    // Air targets too stay over our own ground: 8 km out from the base, the plane 14 km short of them.
                    var targetsAt = at.transform.position + right * 8000f + Vector3.up * 2500f;
                    var start = targetsAt - right * 14000f;
                    forward = right; right = Vector3.Cross(Vector3.up, forward);
                    if (sortie.Targets == "air")
                    {
                        // Air targets as far from every airbase (and its air defences) as can be found within 60 km of ours:
                        // near either side's bases the defences shot down the target or the test plane before the fight.
                        var basePos = at.transform.position;
                        var bases = FactionRegistry.airbaseLookup.Values.Where(a => a != null).Select(a => a.transform.position).ToList();
                        Vector3 best = targetsAt; float bestScore = -1f;
                        for (int k = 0; k < 24; k++)
                            foreach (float r in new[] { 25000f, 35000f, 45000f, 55000f })
                            {
                                var dir = Quaternion.AngleAxis(15f * k, Vector3.up) * Vector3.forward;
                                var pt = basePos + dir * r;
                                float score = bases.Count > 0 ? bases.Min(b => Vector2.Distance(new Vector2(b.x, b.z), new Vector2(pt.x, pt.z))) : r;
                                if (score > bestScore) { bestScore = score; best = pt; }
                            }
                        targetsAt = new Vector3(best.x, Mathf.Max(GroundY(best), 0f) + 3000f, best.z);
                        var to = Vector3.ProjectOnPlane(targetsAt - basePos, Vector3.up);
                        if (to.sqrMagnitude > 1f) { forward = to.normalized; right = Vector3.Cross(Vector3.up, forward); }
                        start = targetsAt - forward * 14000f;
                    }
                    if (sortie.Targets != "air")
                    {
                        // Ground targets on dry land 7 km out from our own airbase, inside our territory (targets near the
                        // enemy's ground had their air defences shoot the test plane down before it could attack), and the
                        // plane starting 7 km on the other side of the base, flying over it towards them.
                        var land = LandAhead(at.transform.position, right, 7000f) ?? LandAhead(at.transform.position, -right, 7000f);
                        if (land == null) { Line($"    sortie \"{sortie.Name}\": no dry land found for ground targets, skipped"); continue; }
                        targetsAt = land.Value;
                        var to = Vector3.ProjectOnPlane(targetsAt - at.transform.position, Vector3.up);
                        if (to.sqrMagnitude > 1f) { forward = to.normalized; right = Vector3.Cross(Vector3.up, forward); }
                        start = targetsAt - forward * 14000f;
                        start.y = Mathf.Max(targetsAt.y, at.transform.position.y) + 2500f;
                    }
                    var spawned = new List<Unit>();
                    try
                    {
                        if (sortie.Targets == "air" && targetPlane != null)
                        {
                            var tp = targetsAt + Vector3.up * 0f;
                            spawned.Add(NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, targetPlane.unitPrefab, new Loadout(), 0.5f, default, tp.ToGlobalPosition(),
                                Quaternion.LookRotation(right, Vector3.up), right * 130f, null, enemy, "OP target plane", 0.6f, 0f));
                        }
                        else
                        {
                            var vd = sortie.Targets == "radar" && radarVehicle != null ? radarVehicle : armor;
                            if (vd != null)
                                for (int k = 0; k < 3; k++)
                                {
                                    var gp = targetsAt + right * ((k - 1) * 70f);
                                    gp.y = GroundY(gp) + 1.5f;
                                    spawned.Add(NetworkSceneSingleton<Spawner>.i.SpawnVehicle(vd.unitPrefab, gp.ToGlobalPosition(), Quaternion.LookRotation(-forward, Vector3.up),
                                        Vector3.zero, enemy, $"OP target {k}", 0.2f, true, null));
                                }
                        }
                    }
                    catch (Exception e) { Line($"    {sortie.Name}: target spawn failed: {e.Message}"); }
                    spawned = spawned.Where(u => u != null).ToList();

                    Aircraft? plane = null;
                    try
                    {
                        plane = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, def.unitPrefab, sortie.Loadout, 0.7f, default, start.ToGlobalPosition(),
                            Quaternion.LookRotation(forward, Vector3.up), forward * 220f, null, hq, "OP arms " + def.unitName, 1f, 1f);
                    }
                    catch (Exception e) { Line($"    {sortie.Name}: plane spawn failed: {e.Message}"); }
                    if (plane == null) { foreach (var u in spawned) RemoveUnit(u); continue; }
                    SelfTestHooks.Shielded.Add(plane);
                    int hitsBefore = SelfTestHooks.ShieldedHits;

                    yield return new WaitForSeconds(1f);
                    var ammo = new Dictionary<WeaponStation, int>();
                    foreach (var ws in plane.weaponStations ?? new List<WeaponStation>()) if (ws != null) ammo[ws] = ws.Ammo;
                    string Loaded() => string.Join(", ", ammo.Keys.Select(ws => $"{ws.WeaponInfo?.weaponName ?? "?"} x{ws.Ammo}"));
                    Line($"    sortie \"{sortie.Name}\" vs {sortie.Targets}: loaded {Loaded()}; {spawned.Count} targets 14 km ahead");
                    var events = new List<string>();
                    var downAt = new Dictionary<Unit, float>();
                    float t0 = Time.time, lastReveal = -99f, firstShot = -1f;
                    bool shotTaken = false, lost = false;
                    while (Time.time - t0 < 150f)
                    {
                        yield return new WaitForSeconds(0.5f);
                        if (plane == null || plane.disabled) { lost = true; events.Add($"{Time.time - t0:0}s plane LOST"); break; }
                        if (Time.time - lastReveal >= 1f) { lastReveal = Time.time; foreach (var u in spawned) Reveal(hq, u); }
                        foreach (var ws in ammo.Keys.ToList())
                        {
                            if (ws == null) continue;
                            int now = ws.Ammo;
                            if (now < ammo[ws])
                            {
                                int n = ammo[ws] - now;
                                events.Add($"{Time.time - t0:0}s fired {n} {ws.WeaponInfo?.weaponName ?? "?"}");
                                fired += n;
                                if (firstShot < 0) firstShot = Time.time - t0;
                                if (!shotTaken && !(ws.WeaponInfo?.gun ?? false))
                                {
                                    shotTaken = true;
                                    yield return StartCoroutine(Shot(plane, def.unitName, $"c fires {Safe(ws.WeaponInfo?.weaponName ?? "weapon")}", View.Chase));
                                    if (_film != null)
                                        _film.Add(new SelfTestFilm.Request { Plane = plane, Label = def.unitName + " " + (ws.WeaponInfo?.weaponName ?? ""), Kind = SelfTestFilm.Kind.Chase, Seconds = 8f, Priority = 2, Deadline = Time.time + 3f });
                                }
                            }
                            ammo[ws] = now;
                        }
                        foreach (var u in spawned)
                            if (u != null && u.disabled && !downAt.ContainsKey(u))
                            {
                                downAt[u] = Time.time - t0;
                                // Only a target lost after the plane fired counts as its kill.
                                if (firstShot >= 0) { events.Add($"{Time.time - t0:0}s target {u.unitName} destroyed"); kills++; }
                                else events.Add($"{Time.time - t0:0}s target {u.unitName} lost before anything was fired (not counted)");
                            }
                        bool allDown = spawned.Count > 0 && spawned.All(u => u == null || u.disabled);
                        bool empty = sortie.Mount != null && ammo.Keys.Where(ws => ws?.WeaponInfo != null && !ws.WeaponInfo.gun).All(ws => ws.Ammo == 0);
                        if (allDown || (empty && firstShot >= 0 && Time.time - t0 - firstShot > 25f)) break;
                    }
                    int hits = downAt.Count(kv => firstShot >= 0 && kv.Value >= firstShot);
                    string verdict = lost ? "PLANE LOST" : firstShot < 0 ? "NOTHING FIRED" : hits > 0 ? $"FIRED and HIT ({hits}/{spawned.Count} targets destroyed)" : "FIRED, no target destroyed";
                    Line($"      {verdict}; {(events.Count > 0 ? string.Join("; ", events) : "no events")}; left: {(plane != null ? Loaded() : "-")}");
                    if (SelfTestHooks.ShieldedHits > hitsBefore) Line($"      (enemy weapons hit the test plane {SelfTestHooks.ShieldedHits - hitsBefore} times; ignored for this test)");
                    if (plane != null) SelfTestHooks.Shielded.Remove(plane);
                    SelfTestHooks.Shielded.RemoveWhere(x => x == null);
                    RemovePlane(plane);
                    foreach (var u in spawned) RemoveUnit(u);
                    yield return new WaitForSeconds(3f);
                }
            }
            Line($"SCORE weapons: {sorties} sorties, {fired} rounds and stores fired, {kills} targets destroyed");
        }

        private static void RemoveUnit(Unit? u)
        {
            try { if (u != null) Destroy(u.gameObject); } catch { }
        }
    }
}
