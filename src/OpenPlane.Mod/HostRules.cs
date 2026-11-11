using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;

namespace OpenPlane.Mod
{
    /// <summary>
    /// What a host allows in the games it runs (single-player, a hosted lobby or a dedicated server): OpenPlane planes on or
    /// off, and limits on thrust to weight and on weapon hardpoints. Planes outside the rules are added to each faction's
    /// restricted aircraft when the mission loads, so the game itself hides them in the hangars and refuses to spawn them,
    /// for the host and for every player who joins (the list is synced). Every machine still loads the same planes, so lobby
    /// matching and network numbering don't change. The cost of a plane is the stock plane's, paid from the player's budget
    /// like any other, so missions keep controlling cost the usual way.
    /// </summary>
    internal static class HostRules
    {
        internal static ConfigEntry<bool> Allow = null!;
        internal static ConfigEntry<float> MaxThrustToWeight = null!;
        internal static ConfigEntry<int> MaxHardpoints = null!;

        public const string Section = "Multiplayer";

        public static void Bind(ConfigFile config)
        {
            Allow = config.Bind(Section, "AllowOpenPlanePlanes", true,
                "When you host (single-player, a lobby or a dedicated server): let players fly OpenPlane planes. Off hides them in every hangar.");
            MaxThrustToWeight = config.Bind(Section, "MaxThrustToWeight", 0f,
                "When you host: OpenPlane planes with more thrust for their weight than this (full fuel, no weapons) can't be flown. 0 = no limit.");
            MaxHardpoints = config.Bind(Section, "MaxHardpoints", 0,
                "When you host: OpenPlane planes with more weapon hardpoints than this can't be flown. 0 = no limit.");
        }

        /// <summary>Why the host's rules keep this plane out, or null when it may be flown.</summary>
        public static string? Blocked(RegisteredPlane plane)
        {
            if (Allow == null) return null;
            if (!Allow.Value) return "OpenPlane planes are switched off in this host's settings";
            if (!PlaneBuilder.Facts.TryGetValue(plane.Definition, out var facts)) return null;
            float maxTw = MaxThrustToWeight.Value;
            if (maxTw > 0 && facts.ThrustToWeight > maxTw + 0.005f)
                return $"its thrust to weight {facts.ThrustToWeight.ToString("0.00", CultureInfo.InvariantCulture)} is over this host's limit of {maxTw.ToString("0.00", CultureInfo.InvariantCulture)}";
            int maxHp = MaxHardpoints.Value;
            if (maxHp > 0 && facts.Hardpoints > maxHp)
                return $"its {facts.Hardpoints} weapon hardpoints are over this host's limit of {maxHp}";
            return null;
        }

        /// <summary>Keys of the planes the host's rules keep out (with the reason), for one mission load.</summary>
        public static List<(RegisteredPlane plane, string reason)> BlockedPlanes()
        {
            var list = new List<(RegisteredPlane, string)>();
            foreach (var p in Registry.Planes)
            {
                string? why;
                try { why = Blocked(p); }
                catch (Exception e) { Plugin.Log.LogWarning("Host rules: " + e.Message); why = null; }
                if (why != null) list.Add((p, why));
            }
            return list;
        }
    }
}
