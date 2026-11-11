using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OpenPlane.Mod
{
    /// <summary>
    /// What happened to one plane file when the game loaded it, in words a player can act on. The OpenPlane app reads these
    /// from BepInEx/OpenPlane-planes.json and shows them next to the plane, so players see what OpenPlane changed in the game
    /// and what to fix in the builder.
    /// </summary>
    internal sealed class PlaneReport
    {
        public string File { get; set; } = "";
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The stock plane it is built on (its name in the game).</summary>
        public string BuiltOn { get; set; } = "";
        /// <summary>Why the game didn't load it, when it didn't.</summary>
        public string? Skipped { get; set; }
        /// <summary>What OpenPlane changed or chose so it flies in the game.</summary>
        public List<string> Changes { get; set; } = new List<string>();
        /// <summary>What may not work well, with what to do about it.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public double RunwayNeededM { get; set; }
        /// <summary>Thrust over weight (full fuel, no weapons) and weapon hardpoints, the numbers a host's limits check.</summary>
        public double ThrustToWeight { get; set; }
        public int Hardpoints { get; set; }
        /// <summary>Why this computer's host settings keep it out of the games it hosts, or null.</summary>
        public string? HostBlocked { get; set; }
        /// <summary>Airbases (and hangars) that don't offer it because their runway is too short.</summary>
        public SortedSet<string> NotOfferedAt { get; set; } = new SortedSet<string>(StringComparer.Ordinal);
    }

    internal static class PlaneReports
    {
        public static readonly List<PlaneReport> All = new List<PlaneReport>();
        private static DateTime _loadedAt = DateTime.UtcNow;

        public static string FilePath => Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-planes.json");

        public static PlaneReport For(string file, string key, string name)
        {
            var r = new PlaneReport { File = file, Key = key, Name = name };
            All.Add(r);
            return r;
        }

        public static PlaneReport? ByKey(string key) => All.FirstOrDefault(r => r.Key == key);

        public static void Reset() { All.Clear(); _loadedAt = DateTime.UtcNow; }

        /// <summary>Writes the file the app reads. Never throws: a report is only a help.</summary>
        public static void Write()
        {
            try
            {
                var doc = new
                {
                    format = "openplane-game-report/1",
                    openplane_version = Plugin.Version,
                    loaded_at = _loadedAt.ToString("o"),
                    // Lobby fingerprint: players only see each other's lobbies when theirs match (same planes, same version).
                    signature = Registry.Signature,
                    game_version = SafeGameVersion(),
                    host_rules = HostRules.Allow == null ? null : new
                    {
                        allow = HostRules.Allow.Value,
                        max_thrust_to_weight = HostRules.MaxThrustToWeight.Value,
                        max_hardpoints = HostRules.MaxHardpoints.Value,
                    },
                    planes = All,
                };
                var json = JsonConvert.SerializeObject(doc, Formatting.Indented,
                    new JsonSerializerSettings { ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() } });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't write the plane report for the app: " + e.Message); }
        }

        /// <summary>The game's own version (without the mod suffixes Blueprinter and OpenPlane add to Application.version).</summary>
        private static string SafeGameVersion()
        {
            try
            {
                var v = UnityEngine.Application.version ?? "";
                int cut = v.IndexOf('_');
                return cut > 0 ? v.Substring(0, cut) : v;
            }
            catch { return ""; }
        }
    }
}
