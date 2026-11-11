using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// The app's Test fly button: the app writes BepInEx/OpenPlane-testfly.json and starts the game straight into a
    /// single-player free flight. Once that mission runs, this puts the player on a side that flies the plane and hands
    /// them the plane in the air (or in a hangar, when "runway" is asked for and an airbase there offers it). The request
    /// is used once and only within 15 minutes of being written; anything that doesn't work out leaves the player in the
    /// game's normal menus with a note in the log.
    /// </summary>
    internal sealed class TestFly : MonoBehaviour
    {
        internal sealed class Request
        {
            [JsonProperty("design_id")] public string? DesignId { get; set; }
            [JsonProperty("name")] public string? Name { get; set; }
            /// <summary>"air" (default) or "runway".</summary>
            [JsonProperty("start")] public string? Start { get; set; }
            [JsonProperty("requested_at")] public DateTime RequestedAt { get; set; }
        }

        public static string RequestPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-testfly.json");
        public static string ResultPath => Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-testfly-result.txt");

        private bool _done;
        private float _checkAt;

        private void Update()
        {
            if (_done || !Plugin.Ready || Time.unscaledTime < _checkAt) return;
            _checkAt = Time.unscaledTime + 1f;
            if (GameManager.gameState != GameState.SinglePlayer) return;
            if (!MissionManager.IsRunning || FactionRegistry.HQLookup.Count == 0 || FactionRegistry.airbaseLookup.Count == 0) return;
            if (NetworkSceneSingleton<Spawner>.i == null || !GameManager.GetLocalPlayer<Player>(out _)) return;
            if (Plugin.SelfTest.Value) { _done = true; return; }   // the self-test spawns its own planes
            if (!File.Exists(RequestPath)) { _done = true; return; }
            _done = true;
            Request? req = null;
            try { req = JsonConvert.DeserializeObject<Request>(File.ReadAllText(RequestPath)); }
            catch (Exception e) { Result("The test-flight request couldn't be read: " + e.Message); }
            finally { try { File.Delete(RequestPath); } catch { /* used once either way */ } }
            if (req == null) return;
            var age = DateTime.UtcNow - req.RequestedAt.ToUniversalTime();
            if (age > TimeSpan.FromMinutes(15) || age < TimeSpan.FromMinutes(-5))
            {
                Result($"The test-flight request for \"{req.Name}\" was too old ({age.TotalMinutes:0} minutes), so the game started normally.");
                return;
            }
            StartCoroutine(Fly(req));
        }

        private IEnumerator Fly(Request req)
        {
            // Let the mission finish setting up its sides and airbases.
            yield return new WaitForSeconds(3f);
            var plane = Registry.Planes.FirstOrDefault(p => p.Design != null && !string.IsNullOrEmpty(req.DesignId) && p.Design.Id == req.DesignId);
            if (plane == null)
            {
                Result($"\"{req.Name}\" isn't loaded in this game (save it in OpenPlane, then restart Nuclear Option).");
                yield break;
            }
            var def = plane.Definition;
            if (!GameManager.GetLocalPlayer<Player>(out var player)) { Result("No local player to fly."); yield break; }
            if (player.Aircraft != null)
            {
                Result($"This mission already put you in a plane, so the test flight of \"{def.unitName}\" didn't start.");
                yield break;
            }

            var airbases = FactionRegistry.airbaseLookup.Values.Where(a => a != null && a.CurrentHQ != null && !a.AttachedAirbase).ToList();
            // The player's side when the mission already gave them one (the game doesn't allow changing it); otherwise a side
            // whose airbases offer the plane (most such airbases first), or any side.
            var offering = airbases.Where(a => SafeCanSpawn(a, def)).ToList();
            var hq = player.HQ != null ? player.HQ
                   : offering.GroupBy(a => a.CurrentHQ).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                     ?? FactionRegistry.HQLookup.Values.FirstOrDefault(h => h != null && !h.preventJoin)
                     ?? FactionRegistry.HQLookup.Values.FirstOrDefault(h => h != null);
            if (hq == null) { Result("This mission has no side to fly for."); yield break; }
            if (player.HQ != hq)
            {
                player.SetFaction(hq, skipPreventJoin: true);
                float joinBy = Time.unscaledTime + 5f;
                while (player.HQ != hq && Time.unscaledTime < joinBy) yield return null;
            }
            if (player.HQ != hq) { Result($"Couldn't join {hq.name} to fly \"{def.unitName}\"."); yield break; }
            // What the game's own "join side" button does next: the map follows the side and the join menu closes.
            CloseJoinMenu(hq);

            var mine = airbases.Where(a => a.CurrentHQ == hq).ToList();
            bool runway = string.Equals(req.Start, "runway", StringComparison.OrdinalIgnoreCase);
            if (runway)
            {
                var ab = mine.Where(a => SafeCanSpawn(a, def)).OrderByDescending(LongestRunway).FirstOrDefault();
                if (ab != null)
                {
                    // The game spawns a player only in an airframe they own (normally bought on the aircraft screen): a test
                    // flight in single player is given one.
                    try { if (!player.OwnsAirframe(def, includeReserved: true)) player.CreditAirframe(def, 1, reserved: false); }
                    catch (Exception e) { Plugin.Log.LogWarning("[TestFly] airframe: " + e.Message); }
                    bool? ok = null;
                    RequestAtAirbase(ab, def, r => ok = r);
                    float until = Time.unscaledTime + 15f;
                    while (ok == null && Time.unscaledTime < until) yield return null;
                    if (ok == true)
                    {
                        Result($"Test flight: \"{def.unitName}\" in a hangar at {ab.name}.");
                        StartCoroutine(AfterSpawn(player));
                        yield break;
                    }
                    Plugin.Log.LogInfo($"[TestFly] {ab.name} didn't give out \"{def.unitName}\"; starting in the air instead.");
                }
                else Plugin.Log.LogInfo($"[TestFly] No airbase of {hq.name} offers \"{def.unitName}\"; starting in the air instead.");
            }

            // In the air: 1200 m above one of the side's airbases (the first that offers the plane), flying along its runway.
            var home = mine.FirstOrDefault(a => SafeCanSpawn(a, def)) ?? mine.FirstOrDefault() ?? airbases.FirstOrDefault();
            if (home == null) { Result("This mission has no airbase to start above."); yield break; }
            var forward = RunwayDirection(home);
            var pos = home.transform.position + Vector3.up * 1200f - forward * 1500f;
            float speed = Mathf.Max(110f, def.aircraftParameters != null ? def.aircraftParameters.takeoffSpeed * 1.8f : 140f);
            try
            {
                var spawner = NetworkSceneSingleton<Spawner>.i;
                var aircraft = spawner.SpawnAircraft(player, def.unitPrefab, DefaultLoadout(def), 0.6f, default, pos.ToGlobalPosition(),
                    Quaternion.LookRotation(forward, Vector3.up), forward * speed, null, hq, "Test flight", 1f, 0.5f);
                spawner.RpcOnGivenAircraftControl(player.Owner);
                Result(aircraft != null
                    ? $"Test flight: \"{def.unitName}\" in the air over {home.name} for {hq.name}."
                    : $"The game didn't spawn \"{def.unitName}\".");
                if (aircraft != null) StartCoroutine(AfterSpawn(player));
            }
            catch (Exception e) { Result($"Starting the test flight failed: {e.Message}"); }
        }

        /// <summary>The join menu the game opens for a player with no side, closed the way its own join button does.</summary>
        private static void CloseJoinMenu(FactionHQ hq)
        {
            try
            {
                if (SceneSingleton<DynamicMap>.i != null) SceneSingleton<DynamicMap>.i.SetFaction(hq);
                var menu = SceneSingleton<GameplayUI>.i != null
                    ? HarmonyLib.Traverse.Create(SceneSingleton<GameplayUI>.i).Field("leaderboardMenu").GetValue<NuclearOption.UI.LeaderboardMenu>()
                    : null;
                if (menu != null && menu.gameObject.activeSelf) menu.Close();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[TestFly] join menu: " + e.Message); }
        }

        /// <summary>Once the plane is the player's: the map (opened for picking an airbase) goes back to its corner.</summary>
        private IEnumerator AfterSpawn(Player player)
        {
            float until = Time.unscaledTime + 20f;
            while (player != null && player.Aircraft == null && Time.unscaledTime < until) yield return null;
            yield return new WaitForSeconds(0.5f);
            try { if (DynamicMap.mapMaximized && SceneSingleton<DynamicMap>.i != null) SceneSingleton<DynamicMap>.i.Minimize(); }
            catch (Exception e) { Plugin.Log.LogWarning("[TestFly] map: " + e.Message); }
            if (Plugin.TestFlyShot.Value) StartCoroutine(Shot());
        }

        /// <summary>Developer check (config Developer/TestFlyShot): a screenshot 15 s into the flight, and who controls the plane.</summary>
        private IEnumerator Shot()
        {
            yield return new WaitForSeconds(15f);
            try
            {
                var file = Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-testfly.png");
                ScreenCapture.CaptureScreenshot(file);
                string who = GameManager.GetLocalPlayer<Player>(out var p) && p.Aircraft != null
                    ? $"the local player flies \"{p.Aircraft.definition?.unitName}\" at {p.Aircraft.transform.position.y:0} m (local), {p.Aircraft.speed:0} m/s"
                    : "the local player has no aircraft";
                Result("Test flight check: " + who + ".");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[TestFly] screenshot: " + e.Message); }
        }

        /// <summary>The game's own spawn request, the one its aircraft menu uses (hangar, loadout and cost rules apply).</summary>
        private static async void RequestAtAirbase(Airbase ab, AircraftDefinition def, Action<bool> done)
        {
            try { done(await NetworkSceneSingleton<Spawner>.i.RequestSpawnAtAirbase(ab, def, default, DefaultLoadout(def), 0.6f)); }
            catch (Exception e) { Plugin.Log.LogWarning("[TestFly] " + e.Message); done(false); }
        }

        /// <summary>The plane's default loadout (index 1 is what the loadout screen starts from), already trimmed to fit it.</summary>
        private static Loadout DefaultLoadout(AircraftDefinition def)
        {
            var list = def.aircraftParameters?.loadouts;
            var lo = list != null && list.Count > 1 ? list[1] : null;
            return lo?.weapons != null ? new Loadout { weapons = new List<WeaponMount>(lo.weapons) } : new Loadout();
        }

        private static bool SafeCanSpawn(Airbase a, AircraftDefinition def)
        {
            try { return a.CanSpawnAircraft(def); } catch { return false; }
        }

        private static float LongestRunway(Airbase a) => a.runways == null ? 0f : a.runways.Where(r => r != null).Select(r => r.Length).DefaultIfEmpty(0f).Max();

        private static Vector3 RunwayDirection(Airbase a)
        {
            var r = a.runways?.Where(x => x != null).OrderByDescending(x => x.Length).FirstOrDefault();
            Vector3 dir = Vector3.zero;
            if (r?.Start != null && r.End != null) dir = r.End.position - r.Start.position;
            if (dir.sqrMagnitude < 1f) dir = a.transform.forward;
            dir = Vector3.ProjectOnPlane(dir, Vector3.up);
            return dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
        }

        private static void Result(string line)
        {
            Plugin.Log.LogInfo("[TestFly] " + line);
            try { File.WriteAllText(ResultPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine); } catch { }
        }
    }
}
