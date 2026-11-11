using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// OpenPlane's in-game helper. It waits for Blueprinter to finish loading, then adds every OpenPlane plane to the
    /// game's aircraft list, its multiplayer prefab registry and its hangars.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    // OpenPlane uses Blueprinter's 2.1.1 API (Api.StateChanged / Api.CurrentState); BepInEx refuses older copies.
    [BepInDependency(BlueprinterGuid, BlueprinterMinVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.meta.openplane";
        public const string Name = "OpenPlane";
        /// <summary>From Directory.Build.props (via OpenPlane.Core.BuildInfo); also part of the multiplayer version suffix.</summary>
        public const string Version = OpenPlane.Core.BuildInfo.Version;
        public const string BlueprinterGuid = "com.nikkorap.blueprinter";
        public const string BlueprinterMinVersion = "2.1.1";

        internal static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        internal static ConfigEntry<bool> CloneTest = null!;
        internal static ConfigEntry<string> CloneTestDonor = null!;
        internal static ConfigEntry<string> ExtraAircraftFolder = null!;
        internal static ConfigEntry<string> DumpPrefabs = null!;
        internal static ConfigEntry<bool> SelfTest = null!;
        internal static ConfigEntry<bool> SelfTestQuit = null!;
        internal static ConfigEntry<bool> SelfTestVideo = null!;
        internal static ConfigEntry<string> SelfTestVideoFolder = null!;
        internal static ConfigEntry<bool> TestFlyShot = null!;

        /// <summary>True once OpenPlane's planes are registered (or registration failed and was skipped).</summary>
        public static bool Ready { get; private set; }

        /// <summary>Folder the app saves planes to: Nuclear Option's user folder + OpenPlane/Aircraft.</summary>
        public static string AircraftFolder => Path.Combine(Application.persistentDataPath, "OpenPlane", "Aircraft");

        private bool _registered;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            CloneTest = Config.Bind("Testing", "CloneTest", false,
                "Adds 'OpenPlane Test' (a straight copy of a stock plane under a new name) to check that OpenPlane can register planes.");
            CloneTestDonor = Config.Bind("Testing", "CloneTestDonor", "Multirole1", "Stock plane (jsonKey) copied by CloneTest.");
            ExtraAircraftFolder = Config.Bind("Paths", "ExtraAircraftFolder", "",
                "Optional second folder to load .oplane files from (besides Nuclear Option's user folder).");

            DumpPrefabs = Config.Bind("Developer", "DumpPrefabs", "",
                "Comma-separated stock plane keys whose structure is written to BepInEx/OpenPlane-dump-<key>.txt at startup.");
            SelfTest = Config.Bind("Developer", "SelfTest", false,
                "Once a single-player mission runs, spawn every OpenPlane plane (air start and runway takeoff) and log how it flies to BepInEx/OpenPlane-selftest.txt.");
            SelfTestQuit = Config.Bind("Developer", "SelfTestQuit", false, "Quit the game when the self-test finishes.");
            SelfTestVideo = Config.Bind("Developer", "SelfTestVideo", false,
                "While the self-test runs, film the planes (takeoff runs, then each plane on its own) as 1920x1080 JPG frames, one folder per clip.");
            SelfTestVideoFolder = Config.Bind("Developer", "SelfTestVideoFolder", "",
                "Folder for the self-test's video frames (empty: BepInEx/OpenPlane-video).");
            TestFlyShot = Config.Bind("Developer", "TestFlyShot", false,
                "After a test flight starts, save a screenshot to BepInEx/OpenPlane-testfly.png and log who flies the plane.");
            HostRules.Bind(Config);
            var harmony = PatchAll();
            if (SelfTest.Value) SelfTestHooks.Install(harmony);   // developer diagnostics only
            gameObject.AddComponent<SelfTest>();
            gameObject.AddComponent<TestFly>();
            Log.LogInfo($"OpenPlane {Version} loaded. Planes folder: {AircraftFolder}");

            Blueprinter.Api.StateChanged += OnBlueprinterState;
            OnBlueprinterState(Blueprinter.Api.CurrentState);
        }

        /// <summary>Applies each patch class on its own, so one that no longer fits the game doesn't stop the others.</summary>
        private static Harmony PatchAll()
        {
            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    var patched = harmony.CreateClassProcessor(type).Patch();
                    if (patched != null && patched.Count > 0) ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"Patch {type.Name} could not be applied (this game version may have changed): {e}");
                }
            }
            if (failed > 0) Log.LogWarning($"{ok} OpenPlane patches applied, {failed} failed.");
            return harmony;
        }

        private void OnBlueprinterState(Blueprinter.Api.State state)
        {
            if (_registered) return;
            if (state == Blueprinter.Api.State.Ready) Register();
            else if (state == Blueprinter.Api.State.Failed)
            {
                Log.LogError("Blueprinter failed to load, so OpenPlane planes were not added this session.");
                _registered = true;
                Ready = true;
            }
        }

        private void Register()
        {
            _registered = true;
            try
            {
                Registry.RegisterAll();
            }
            catch (Exception e)
            {
                Log.LogError("OpenPlane could not add its planes: " + e);
            }
            finally
            {
                Ready = true;
            }
        }
    }
}
