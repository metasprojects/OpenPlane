using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>Offers each OpenPlane plane in every hangar that offers the stock plane it is built on.</summary>
    [HarmonyPatch(typeof(Airbase), nameof(Airbase.AddHangar))]
    internal static class AirbaseAddHangarPatch
    {
        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static void Prefix(Airbase __instance, Hangar hangar)
        {
            try
            {
                if (hangar == null || hangar.availableAircraft == null || Registry.Planes.Count == 0) return;
                // Blueprinter leaves this one hangar alone (it breaks the map); so does OpenPlane.
                if (__instance != null && __instance.name == "airstrip_city2" && hangar.attachedUnit != null
                    && hangar.attachedUnit.UniqueName == "<MAP_UNIT>++hangar_med_10") return;
                var list = hangar.availableAircraft;
                bool changed = false;
                var added = new List<string>();
                foreach (var plane in Registry.Planes)
                {
                    if (Array.IndexOf(list, plane.Definition) >= 0) continue;
                    if (Array.IndexOf(list, plane.Donor) < 0) continue;
                    if (!RunwayLongEnough(__instance, hangar, plane.Definition)) continue;
                    list = list.AddToArray(plane.Definition);
                    added.Add(plane.Definition.unitName);
                    changed = true;
                }
                if (!changed) return;
                hangar.availableAircraft = list;
                var where = hangar.name;
                if (Logged.Add(where))
                    Plugin.Log.LogInfo($"Hangar \"{where}\" now also offers: {string.Join(", ", added)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Hangar patch: " + e.Message);
            }
        }

        /// <summary>
        /// True when the runway the AI will take off from at this hangar is at least as long as the plane's takeoff run (always
        /// true for planes whose run isn't longer than their stock plane's, or when the airbase's runways aren't known). The AI
        /// picks the takeoff runway nearest to where the plane stands (Airbase.GetTakeoffRunway with no minimum length), so
        /// that is the one checked, not the longest.
        /// </summary>
        private static bool RunwayLongEnough(Airbase? airbase, Hangar hangar, AircraftDefinition def)
        {
            if (!PlaneBuilder.RunwayNeeded.TryGetValue(def, out float need) || need <= 0f) return true;
            if (airbase == null || airbase.runways == null || airbase.runways.Length == 0) return true;
            Airbase.Runway? pick = null;
            float best = float.MaxValue;
            foreach (var r in airbase.runways)
            {
                if (r == null || !r.Takeoff) continue;
                float d = hangar != null ? r.GetDistance(hangar.transform).Distance : 0f;
                if (pick == null || d < best) { best = d; pick = r; }
            }
            if (pick == null || pick.Length <= 0f || pick.Length >= need) return true;
            string where = airbase.name + (hangar != null ? "/" + hangar.name : "");
            if (Skipped.Add(where + "|" + def.jsonKey))
            {
                var report = PlaneReports.ByKey(def.jsonKey);
                if (report != null && report.NotOfferedAt.Add($"{OpenPlane.Core.Physics.GameEstimates.AirbaseName(airbase.name)} ({pick.Length:0} m runway)")) PlaneReports.Write();
            }
            if (Skipped.Contains(where + "|" + def.jsonKey) && LoggedSkip.Add(where + "|" + def.jsonKey))
                Plugin.Log.LogInfo($"\"{def.unitName}\" isn't offered at {where}: it needs about {need:0} m of runway and the nearest takeoff runway there is {pick.Length:0} m.");
            return false;
        }

        private static readonly HashSet<string> Skipped = new HashSet<string>();
        private static readonly HashSet<string> LoggedSkip = new HashSet<string>();
    }

    /// <summary>
    /// Adds OpenPlane's version and plane fingerprint to the game version, so only players with the same planes see each
    /// other's lobbies. Runs before Blueprinter's postfix (which goes last and shortens long strings).
    /// </summary>
    [HarmonyPatch(typeof(Application), nameof(Application.version), MethodType.Getter)]
    internal static class ApplicationVersionPatch
    {
        [HarmonyPriority(Priority.Normal)]
        private static void Postfix(ref string __result)
        {
            if (Registry.Planes.Count == 0) return;
            __result += $"_openplane-v{Plugin.Version}-{Registry.Signature}";
        }
    }

    /// <summary>
    /// Runs on the host when a mission loads (FactionHQ.OnMissionLoad is server-only): a mission that bans a stock plane also
    /// bans the OpenPlane planes built on it, and planes outside the host's own rules (<see cref="HostRules"/>) are banned too.
    /// The game syncs the list to every player, hides banned planes in the hangars and refuses to spawn them.
    /// </summary>
    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    internal static class FactionRestrictionsPatch
    {
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);

        private static void Postfix(FactionHQ __instance)
        {
            try
            {
                var current = __instance?.restrictedAircraft;
                if (current == null || Registry.Planes.Count == 0) return;
                // Built as a new list and set through the synced property, so players already in the game get it too.
                var list = new List<string>(current);
                foreach (var p in Registry.Planes)
                    if (p.Donor != null && list.Contains(p.Donor.jsonKey) && !list.Contains(p.Key))
                    {
                        list.Add(p.Key);
                        Plugin.Log.LogInfo($"{p.Definition.unitName} ({p.Key}) is not allowed in this mission, like {p.Donor.unitName} it is built on.");
                    }
                foreach (var (p, why) in HostRules.BlockedPlanes())
                {
                    if (list.Contains(p.Key)) continue;
                    list.Add(p.Key);
                    if (Logged.Add(p.Key + "|" + why)) Plugin.Log.LogInfo($"{p.Definition.unitName} ({p.Key}) can't be flown in games you host: {why}.");
                }
                if (list.Count != current.Count) __instance!.NetworkrestrictedAircraft = list;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Mission restrictions patch: " + e.Message); }
        }
    }

    /// <summary>
    /// The host's weapon check (WeaponChecker.VetLoadout, run for every spawn request) sees the stock plane's station sets,
    /// not what an OpenPlane plane can lift. The loadout is trimmed to the plane's stations and carrying weight first, the
    /// same way the plane is armed when it spawns, so the player isn't charged for weapons left off and no request is
    /// flagged as invalid (the game kicks players with too many invalid requests).
    /// </summary>
    [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout))]
    internal static class VetLoadoutPatch
    {
        private static void Prefix(AircraftDefinition definition, Loadout requestedLoadout)
        {
            try
            {
                if (definition == null || requestedLoadout == null) return;
                if (!PlaneBuilder.WeaponCapacity.TryGetValue(definition, out var capacity)) return;
                var wm = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager : null;
                if (wm?.hardpointSets == null) return;
                // Only the weight is trimmed: the loadout screen lets an honest player pick more than the plane can lift.
                // Weapons the plane has no station for can't be picked there, so those are left for the game's own check.
                int removed = PlaneBuilder.TrimLoadout(requestedLoadout, wm.hardpointSets, capacity, weightOnly: true);
                if (removed > 0)
                    Plugin.Log.LogInfo($"{definition.unitName}: {removed} requested weapons left off before the host's weapon check (over the {capacity:0} kg it can carry).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Weapon check patch: " + e.Message); }
        }
    }

    // ------------------------------------------------------------------ missions that name planes this game doesn't have

    /// <summary>
    /// Missions (and the mission editor) look unit keys up without checking they exist, so a mission saved with an
    /// OpenPlane plane throws on a machine without that plane. These patches leave such entries out (and log them once).
    /// </summary>
    internal static class UnknownUnits
    {
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> Hidden = new HashSet<int>();

        public static bool Known(string? key) => key != null && Encyclopedia.Lookup != null && Encyclopedia.Lookup.ContainsKey(key);

        public static void Report(string? key, string where)
        {
            if (Logged.Add(key ?? ""))
                Plugin.Log.LogWarning($"This mission lists \"{key}\", which isn't loaded in this game ({where}); it was left out." +
                                      ((key ?? "").StartsWith("op.", StringComparison.Ordinal) ? " It is an OpenPlane plane this computer doesn't have." : ""));
        }

        /// <summary>Hides a list row whose unit isn't loaded; shows it again when the row is reused for a known unit.</summary>
        public static bool Row(Component row, string? key, string where)
        {
            int id = row.GetInstanceID();
            if (Known(key))
            {
                if (Hidden.Remove(id)) row.gameObject.SetActive(true);
                return true;
            }
            Report(key, where);
            Hidden.Add(id);
            row.gameObject.SetActive(false);
            return false;
        }

        public static void Prune(SavedInventory? inventory, string where)
        {
            if (inventory?.StoredList == null) return;
            inventory.StoredList.RemoveAll(u =>
            {
                if (u != null && Known(u.UnitType)) return false;
                Report(u?.UnitType, where);
                return true;
            });
        }
    }

    [HarmonyPatch(typeof(InventoryItem), "SetInventoryItem")]
    internal static class InventoryItemPatch
    {
        private static bool Prefix(InventoryItem __instance, UnitCount factionSupply)
        {
            try { return UnknownUnits.Row(__instance, factionSupply?.UnitType, "faction supplies"); }
            catch (Exception e) { Plugin.Log.LogWarning("Inventory patch: " + e.Message); return true; }
        }
    }

    [HarmonyPatch(typeof(InventoryInspectorItem), "SetEntry")]
    internal static class InventoryInspectorItemPatch
    {
        private static bool Prefix(InventoryInspectorItem __instance, UnitCount supply)
        {
            try { return UnknownUnits.Row(__instance, supply?.UnitType, "unit inventory"); }
            catch (Exception e) { Plugin.Log.LogWarning("Inventory patch: " + e.Message); return true; }
        }
    }

    [HarmonyPatch(typeof(UnitStorage), nameof(UnitStorage.DeployUnits))]
    internal static class UnitStorageDeployPatch
    {
        private static void Prefix(UnitStorage __instance)
        {
            try { UnknownUnits.Prune(__instance.inventory, "stored units"); }
            catch (Exception e) { Plugin.Log.LogWarning("Unit storage patch: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(UnitStorage), nameof(UnitStorage.TryFillFromStorage))]
    internal static class UnitStorageFillPatch
    {
        private static void Prefix(UnitStorage fromStorage)
        {
            try { if (fromStorage != null) UnknownUnits.Prune(fromStorage.inventory, "stored units"); }
            catch (Exception e) { Plugin.Log.LogWarning("Unit storage patch: " + e.Message); }
        }
    }

    /// <summary>
    /// The AI aims guns by each weapon of its current station; a gun destroyed with its part (or left behind by a station that
    /// was closed) stays in that list and the game's aiming threw on it every frame, so the plane flew without steering.
    /// Dead entries are dropped first, on OpenPlane planes only.
    /// </summary>
    [HarmonyPatch(typeof(AutopilotPlane), nameof(AutopilotPlane.AutoAim))]
    internal static class AutoAimPatch
    {
        private static void Prefix(AutopilotPlane __instance)
        {
            try
            {
                var aircraft = __instance.aircraft;
                if (aircraft == null || !(aircraft.definition is AircraftDefinition def) || !PlaneBuilder.WeaponCapacity.ContainsKey(def)) return;
                var weapons = aircraft.weaponManager?.currentWeaponStation?.Weapons;
                if (weapons != null && weapons.Count > 0) weapons.RemoveAll(w => w == null);
            }
            catch { }
        }
    }

    /// <summary>
    /// Every loadout an OpenPlane plane spawns with (a player's, a mission's or the AI's) is trimmed to the stations the design
    /// has and the weight it can carry, before the game hangs the weapons on it.
    /// </summary>
    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
    internal static class SpawnWeaponsPatch
    {
        private static void Prefix(WeaponManager __instance)
        {
            try
            {
                var aircraft = __instance.aircraft;
                if (aircraft == null || !(aircraft.definition is AircraftDefinition def)) return;
                if (!PlaneBuilder.WeaponCapacity.TryGetValue(def, out var capacity)) return;
                int removed = PlaneBuilder.TrimLoadout(aircraft.loadout, __instance.hardpointSets, capacity);
                if (removed > 0)
                    Plugin.Log.LogInfo($"{def.unitName}: {removed} weapons left off (no station for them on this plane, or over the {capacity:0} kg it can carry).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Loadout trim: " + e.Message); }
        }
    }

    /// <summary>
    /// A taxiing AI pilot gives up and ejects when, at its 5-second check, the cockpit leans more than 3 degrees: the stock
    /// planes never lean that far unless they've tipped over. OpenPlane planes lean 4 to 9 degrees in an ordinary taxi turn
    /// (their own wheel track and weight), so on those the pilot gives up only when the plane leans over 6 degrees while
    /// it's stopped (a broken leg or a wing on the ground) or over 20 degrees at any speed.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotTaxiState), "EjectCheck")]
    internal static class TaxiTiltPatch
    {
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> AircraftOf = AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> Disembarking = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("disembarking");

        internal static bool GivesUp(float lean, float speed) => lean > 0.34f || (lean > 0.105f && speed < 1f);

        private static bool Prefix(AIPilotTaxiState __instance)
        {
            try
            {
                var aircraft = AircraftOf(__instance);
                if (aircraft == null || !Registry.IsOurs(aircraft.definition) || aircraft.cockpit == null) return true;
                var cockpit = aircraft.cockpit.xform != null ? aircraft.cockpit.xform : aircraft.cockpit.transform;
                float lean = Mathf.Abs(Vector3.Dot(cockpit.right, Vector3.up));
                if (GivesUp(lean, aircraft.speed)) Disembarking(__instance) = true;
                return false;
            }
            catch { return true; }
        }
    }
}
