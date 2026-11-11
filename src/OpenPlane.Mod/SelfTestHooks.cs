using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Developer diagnostics for the self-test: logs when and why an OpenPlane plane ejects, loses a part, breaks a wheel,
    /// hurts its pilot with g or takes damage, with its radar altitude, the angle between its nose and its flight path and
    /// how much of it has come off. Installed only while the SelfTest setting is on (no attributes, so PatchAll skips it);
    /// each hook is applied on its own.
    /// </summary>
    internal static class SelfTestHooks
    {
        private static readonly Dictionary<string, float> LastLogged = new Dictionary<string, float>();

        public static void Install(Harmony harmony)
        {
            Hook(harmony, typeof(Aircraft), "StartEjectionSequence", Type.EmptyTypes, nameof(OnEject));
            Hook(harmony, typeof(Unit), "DetachPart", new[] { typeof(byte), typeof(Vector3), typeof(Vector3) }, nameof(OnDetach));
            Hook(harmony, typeof(LandingGear), "BreakWheel", Type.EmptyTypes, nameof(OnBreakWheel));
            Hook(harmony, typeof(Pilot), "TakeGForceDamage", new[] { typeof(float) }, nameof(OnGForce));
            Hook(harmony, typeof(UnitPart), "TakeDamage", null, nameof(OnPartDamage));
            // Why a taxiing AI pilot stops and ejects: the 5-second tilt check, or any damage to the plane's main part.
            Hook(harmony, typeof(AIPilotTaxiState), "EjectCheck", Type.EmptyTypes, nameof(OnTaxiTiltCheck));
            Hook(harmony, typeof(AIPilotTaxiState), "Taxi_OnTakeDamage", null, nameof(OnTaxiDamage));
            Hook(harmony, typeof(UnitPart), "ApplyDamage", new[] { typeof(float), typeof(float), typeof(float), typeof(float) }, nameof(OnApplyDamage));
        }

        private static Aircraft? TaxiAircraft(AIPilotTaxiState state)
        {
            try { return Ours(Traverse.Create(state).Field("aircraft").GetValue<Aircraft>()); } catch { return null; }
        }

        private static void OnTaxiTiltCheck(AIPilotTaxiState __instance)
        {
            try
            {
                if (!(TaxiAircraft(__instance) is Aircraft a) || a.cockpit == null) return;
                var c = a.cockpit.xform != null ? a.cockpit.xform : a.cockpit.transform;
                float cockpitRoll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(c.right, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
                float bodyRoll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(a.transform.right, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
                float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(a.transform.forward, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
                float lean = Mathf.Abs(Vector3.Dot(c.right, Vector3.up));
                bool gameWould = lean > 0.05f, givesUp = TaxiTiltPatch.GivesUp(lean, a.speed);
                if (gameWould || Due(a.GetInstanceID() + ":tilt", 10f))
                    Log(a, $"taxi tilt check {(givesUp ? "GIVES UP (the pilot will stop and eject)" : gameWould ? "leaning, allowed for OpenPlane planes" : "ok")}: cockpit rolled {cockpitRoll:0.0} deg, airframe rolled {bodyRoll:0.0} deg, pitched {pitch:0.0} deg");
            }
            catch { }
        }

        private static void OnTaxiDamage(AIPilotTaxiState __instance)
        {
            try { if (TaxiAircraft(__instance) is Aircraft a) Log(a, "damaged while taxiing (the pilot will stop and eject)"); } catch { }
        }

        private static void OnApplyDamage(UnitPart __instance, float netPierceDamage, float netBlastDamage, float netFireDamage, float netImpactDamage)
        {
            try
            {
                if (netPierceDamage + netBlastDamage + netFireDamage + netImpactDamage <= 0f) return;
                if (!(Ours(__instance.parentUnit) is Aircraft a) || !Due(__instance.GetInstanceID() + ":apply", 0.5f)) return;
                Log(a, $"part {__instance.name} takes damage (pierce {netPierceDamage:0.##}, blast {netBlastDamage:0.##}, fire {netFireDamage:0.##}, impact {netImpactDamage:0.##}, hit points {__instance.hitPoints:0.#})");
            }
            catch { }
        }

        private static void Hook(Harmony harmony, Type type, string method, Type[]? args, string prefix)
        {
            try
            {
                var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (target == null) { Plugin.Log.LogWarning($"[SelfTest] {type.Name}.{method} wasn't found; that event isn't logged."); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SelfTestHooks), prefix));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[SelfTest] couldn't hook {type.Name}.{method}: {e.Message}"); }
        }

        private static Aircraft? Ours(Unit? unit)
            => unit is Aircraft a && a.definition != null && Registry.Planes.Any(p => p.Definition == a.definition) ? a : null;

        /// <summary>At most one line per key every <paramref name="every"/> seconds (g and damage fire every frame).</summary>
        private static bool Due(string key, float every)
        {
            float now = Time.time;
            if (LastLogged.TryGetValue(key, out var last) && now - last < every) return false;
            LastLogged[key] = now;
            return true;
        }

        private static void Log(Aircraft a, string what)
        {
            try
            {
                var rb = a.rb;
                var v = rb != null ? rb.velocity : Vector3.zero;
                var nose = a.cockpit != null ? a.cockpit.transform.forward : a.transform.forward;
                string angle = v.sqrMagnitude > 1f ? $"{Vector3.Angle(nose, v):0} deg" : "n/a (slow)";
                int parts = a.partLookup?.Count ?? 0;
                int detached = a.partLookup?.Count(p => p != null && p.IsDetached()) ?? 0;
                Plugin.Log.LogInfo($"[SelfTest] t={Time.time:0.00} {a.definition.unitName}: {what}. radar alt {a.radarAlt:0.0} m, speed {v.magnitude:0} m/s, " +
                                   $"nose vs flight path {angle}, parts detached {detached}/{parts}");
            }
            catch (Exception e) { Plugin.Log.LogInfo($"[SelfTest] {what} (details failed: {e.Message})"); }
        }

        private static void OnEject(Aircraft __instance)
        {
            try { if (Ours(__instance) is Aircraft a && !a.ejected) Log(a, "pilot ejects"); } catch { }
        }

        private static void OnDetach(Unit __instance, byte partID)
        {
            try
            {
                if (!(Ours(__instance) is Aircraft a)) return;
                var part = a.partLookup?.FirstOrDefault(p => p != null && p.id == partID);
                Log(a, $"part {(part != null ? part.name : "#" + partID)} breaks off");
            }
            catch { }
        }

        private static void OnBreakWheel(LandingGear __instance)
        {
            try { if (Ours(__instance.aircraft) is Aircraft a) Log(a, $"wheel {__instance.name} breaks"); } catch { }
        }

        private static void OnGForce(Pilot __instance, float sqrGForces)
        {
            try
            {
                if (Ours(__instance.aircraft) is Aircraft a && Due(a.GetInstanceID() + ":g", 1f))
                    Log(a, $"pilot hurt by {Mathf.Sqrt(Mathf.Max(0, sqrGForces)):0.0} g");
            }
            catch { }
        }

        private static void OnPartDamage(UnitPart __instance, float pierceDamage, float blastDamage, float fireDamage, float impactDamage)
        {
            try
            {
                if (!(Ours(__instance.parentUnit) is Aircraft a) || !Due(__instance.GetInstanceID() + ":dmg", 0.5f)) return;
                Log(a, $"part {__instance.name} damaged (pierce {pierceDamage:0.#}, blast {blastDamage:0.#}, fire {fireDamage:0.#}, impact {impactDamage:0.#}, hit points {__instance.hitPoints:0})");
            }
            catch { }
        }
    }
}
