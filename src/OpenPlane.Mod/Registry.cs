using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mirage;
using OpenPlane.Core.Design;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OpenPlane.Mod
{
    /// <summary>One OpenPlane plane added to the game.</summary>
    internal sealed class RegisteredPlane
    {
        public RegisteredPlane(string key, AircraftDefinition definition, AircraftDefinition donor, AircraftDesign? design, string contentHash, int prefabHash)
        { Key = key; Definition = definition; Donor = donor; Design = design; ContentHash = contentHash; PrefabHash = prefabHash; }
        public string Key { get; }
        public AircraftDefinition Definition { get; }
        public AircraftDefinition Donor { get; }
        public AircraftDesign? Design { get; }
        /// <summary>Hash of the plane's unzipped aircraft.json and meshes.json (see OplanePackage.Contents.ContentHash).</summary>
        public string ContentHash { get; }
        public int PrefabHash { get; }
    }

    /// <summary>Adds OpenPlane planes to the Encyclopedia, Mirage's prefab list and hangars, after Blueprinter.</summary>
    internal static class Registry
    {
        public static readonly List<RegisteredPlane> Planes = new List<RegisteredPlane>();

        /// <summary>Short fingerprint of every loaded plane, so players with different planes don't share a lobby.</summary>
        public static string Signature { get; private set; } = "none";

        private static GameObject? _holder;

        /// <summary>True for an aircraft definition OpenPlane made (never used as a donor).</summary>
        public static bool IsOurs(AircraftDefinition? def) => def != null && Planes.Any(p => p.Definition == def);

        public static RegisteredPlane? Find(AircraftDefinition? def) => def == null ? null : Planes.FirstOrDefault(p => p.Definition == def);

        /// <summary>
        /// An inactive, never-unloaded parent for our prefab copies: children of an inactive object stay inactive,
        /// so the copies never run Awake; the game's Instantiate(prefab) at spawn makes active instances as usual.
        /// </summary>
        private static Transform Holder
        {
            get
            {
                if (_holder == null)
                {
                    _holder = new GameObject("OpenPlane Prefabs");
                    _holder.SetActive(false);
                    _holder.hideFlags = HideFlags.HideAndDontSave;
                    Object.DontDestroyOnLoad(_holder);
                }
                return _holder.transform;
            }
        }

        private sealed class Pending
        {
            public string Key = "";
            public AircraftDesign? Design;
            public List<MeshData>? Meshes;
            public string Source = "";
            public string ContentHash = "";
        }

        public static void RegisterAll()
        {
            var enc = Encyclopedia.i;
            if (enc == null) { Plugin.Log.LogError("The game's Encyclopedia isn't loaded; nothing registered."); return; }

            foreach (var key in Plugin.DumpPrefabs.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()))
            {
                try
                {
                    if (Encyclopedia.Lookup.TryGetValue(key, out var u) && u is AircraftDefinition ad)
                    {
                        var file = Path.Combine(BepInEx.Paths.BepInExRootPath, $"OpenPlane-dump-{key}.txt");
                        PrefabDump.Dump(ad, file);
                        Plugin.Log.LogInfo($"Wrote {file}");
                    }
                    else Plugin.Log.LogWarning($"DumpPrefabs: no stock plane {key}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"DumpPrefabs {key}: {e}"); }
            }

            PlaneReports.Reset();
            var pending = new List<Pending>();
            if (Plugin.CloneTest.Value)
                pending.Add(new Pending { Key = "op.test.clone", Source = "CloneTest setting", ContentHash = "clone-test" });

            // Read every file once. Files are taken in file-name order (ordinal, the same on every machine), so when two files
            // carry the same plane id the same one wins everywhere.
            var byKey = new Dictionary<string, Pending>(StringComparer.Ordinal);
            foreach (var file in PlaneFiles())
            {
                try
                {
                    var c = OplanePackage.Read(file, OplanePackage.ReadOptions.DesignOnly);
                    var key = OplanePackage.GameKey(c.Design, c.ContentHash);
                    if (byKey.TryGetValue(key, out var first))
                    {
                        PlaneReports.For(Path.GetFileName(file), key, c.Design.Name).Skipped =
                            $"It has the same plane id as {Path.GetFileName(first.Source)}, so the game loaded only that one. Duplicate one of them in OpenPlane to give it its own id.";
                        Plugin.Log.LogWarning($"{Path.GetFileName(file)} has the same plane id as {Path.GetFileName(first.Source)} ({key}), so it was skipped. " +
                                              "Give one of them a new id in the OpenPlane app (Duplicate or Save as makes a copy with its own id).");
                        continue;
                    }
                    var p = new Pending { Key = key, Design = c.Design, Meshes = c.Meshes, Source = file, ContentHash = c.ContentHash };
                    byKey[key] = p;
                    pending.Add(p);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Skipped {Path.GetFileName(file)}: {e.Message}");
                    PlaneReports.For(Path.GetFileName(file), "", Path.GetFileNameWithoutExtension(file)).Skipped = "The game couldn't read this file: " + e.Message;
                }
            }

            // Same registration order on every machine: by key.
            pending = pending.GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.First())
                             .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

            var taken = new HashSet<int>(Resources.FindObjectsOfTypeAll<NetworkIdentity>().Where(n => n != null && n.PrefabHash != 0).Select(n => n.PrefabHash));

            foreach (var p in pending)
            {
                AircraftDefinition? def = null, donor = null;
                int hash = 0;
                bool added = false;
                var report = PlaneReports.For(Path.GetFileName(p.Source), p.Key, p.Design?.Name ?? "OpenPlane Test");
                try
                {
                    var donorKey = p.Design?.Donor ?? Plugin.CloneTestDonor.Value;
                    donor = FindDonor(donorKey);
                    if (donor == null)
                    {
                        Plugin.Log.LogWarning($"{p.Key}: no stock plane to build on; skipped.");
                        report.Skipped = $"The stock plane it is built on (\"{donorKey}\") isn't in this version of the game. Open it in OpenPlane and save it again to pick another.";
                        continue;
                    }

                    string name = p.Design?.Name ?? "OpenPlane Test";
                    def = CloneDefinition(donor, p.Key, name, taken, out hash);
                    if (p.Design != null) PlaneBuilder.Apply(def, donor, p.Design, p.Meshes ?? new List<MeshData>(), report);
                    AddToEncyclopedia(enc, def);
                    added = true;
                    Planes.Add(new RegisteredPlane(p.Key, def, donor, p.Design, p.ContentHash, hash));
                    Plugin.Log.LogInfo($"Added \"{def.unitName}\" as {p.Key} (built on {donor.jsonKey}, mass {def.mass.ToString("0", CultureInfo.InvariantCulture)} kg, " +
                                       $"network index {((INetworkDefinition)def).LookupIndex}, prefab hash {hash}) from {Path.GetFileName(p.Source)}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"{p.Key}: could not be added: {e}");
                    report.Skipped = "The game couldn't build this plane: " + e.Message;
                    if (def != null && !added) Discard(def, donor, hash, taken);
                }
            }

            Signature = MakeSignature(Planes);
            foreach (var p in Planes)
            {
                var r = PlaneReports.ByKey(p.Key);
                if (r == null) continue;
                if (PlaneBuilder.Facts.TryGetValue(p.Definition, out var facts)) { r.ThrustToWeight = System.Math.Round(facts.ThrustToWeight, 2); r.Hardpoints = facts.Hardpoints; }
                try { r.HostBlocked = HostRules.Blocked(p); } catch { /* the report is only a help */ }
            }
            PlaneReports.Write();
            Plugin.Log.LogInfo($"OpenPlane ready: {Planes.Count} plane(s), signature {Signature}.");
        }

        /// <summary>Frees what a failed registration made: the prefab copy, the definition copies and its prefab hash.</summary>
        private static void Discard(AircraftDefinition def, AircraftDefinition? donor, int hash, HashSet<int> taken)
        {
            try
            {
                // Only our copies: never the donor's own prefab or parameters.
                if (def.unitPrefab != null && (donor == null || def.unitPrefab != donor.unitPrefab)) Object.Destroy(def.unitPrefab);
                if (def.aircraftParameters != null && (donor == null || def.aircraftParameters != donor.aircraftParameters)) Object.Destroy(def.aircraftParameters);
                Object.Destroy(def);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Cleaning up a failed plane: " + e.Message); }
            if (hash != 0) taken.Remove(hash);
        }

        /// <summary>Every .oplane file in the planes folders, sorted by file name. A folder that can't be read is skipped and logged.</summary>
        private static List<string> PlaneFiles()
        {
            var folders = new List<string> { Plugin.AircraftFolder };
            var extra = Plugin.ExtraAircraftFolder.Value;
            if (!string.IsNullOrWhiteSpace(extra)) folders.Add(extra.Trim().Trim('"'));
            var files = new List<string>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                try
                {
                    var full = Path.GetFullPath(f);
                    if (!seenFolders.Add(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) continue;
                    if (!Directory.Exists(full))
                    {
                        if (string.Equals(f, Plugin.AircraftFolder, StringComparison.Ordinal)) Directory.CreateDirectory(full);
                        else Plugin.Log.LogWarning($"The extra planes folder \"{full}\" doesn't exist; no planes were loaded from it.");
                        continue;
                    }
                    files.AddRange(Directory.GetFiles(full, "*" + OplanePackage.Extension, SearchOption.AllDirectories));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't read the planes folder \"{f}\": {e.Message}");
                }
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                        .ThenBy(p => p, StringComparer.Ordinal)
                        .ToList();
        }

        private static AircraftDefinition? FindDonor(string key)
        {
            bool Usable(AircraftDefinition? a) => a != null && !IsOurs(a) && !(a.jsonKey ?? "").StartsWith("op.", StringComparison.Ordinal);
            if (Encyclopedia.Lookup != null && Encyclopedia.Lookup.TryGetValue(key, out var u) && u is AircraftDefinition a && Usable(a)) return a;
            var list = Encyclopedia.i.aircraft;
            // Fall back to a sensible stock plane: Multirole1, else the first one that isn't an OpenPlane plane.
            return list.FirstOrDefault(x => Usable(x) && x.jsonKey == "Multirole1") ?? list.FirstOrDefault(x => Usable(x));
        }

        private static AircraftDefinition CloneDefinition(AircraftDefinition donor, string key, string name, HashSet<int> takenHashes, out int hash)
        {
            hash = 0;
            var def = Object.Instantiate(donor);
            def.name = key;
            def.jsonKey = key;
            // A converted replica can share a stock plane's name (e.g. "KR-67 Ifrit"), or two OpenPlane planes can share one;
            // keep names and Workshop tags unique.
            name = UniqueName(name);
            def.unitName = name;
            def.code = ShortCode(name);  // the label under the icon in the hangar's aircraft picker
            def.WorkshopTag = name;  // lets players upload liveries for this plane with the game's own uploader
            def.dontAutomaticallyAddToEncyclopedia = false;
            // Explicit copies of the per-plane data we change, so the donor's are never touched.
            if (donor.aircraftParameters != null)
            {
                def.aircraftParameters = Object.Instantiate(donor.aircraftParameters);
                def.aircraftParameters.aircraftName = name;
            }
            if (donor.aircraftInfo != null)
                def.aircraftInfo = new AircraftInfo
                {
                    emptyWeight = donor.aircraftInfo.emptyWeight, maxSpeed = donor.aircraftInfo.maxSpeed, stallSpeed = donor.aircraftInfo.stallSpeed,
                    maneuverability = donor.aircraftInfo.maneuverability, maxWeight = donor.aircraftInfo.maxWeight,
                };

            var prefab = Object.Instantiate(donor.unitPrefab, Holder);
            prefab.name = key;
            def.unitPrefab = prefab;
            var unit = prefab.GetComponent<Unit>();
            if (unit != null) unit.definition = def;

            var identity = prefab.GetComponent<NetworkIdentity>();
            if (identity == null)
            {
                Discard(def, donor, 0, takenHashes);
                throw new InvalidOperationException($"{donor.jsonKey}'s prefab has no NetworkIdentity");
            }
            hash = FreeHash(key, takenHashes);
            identity.PrefabHash = hash;
            // The copy keeps the donor's mass (Instantiate copied it). Never call CacheMass() on a copy: it sums only active
            // parts, and the copy sits under an inactive holder, so it would set the mass to 0.
            return def;
        }

        private static string UniqueName(string name)
        {
            bool Taken(string n) => Encyclopedia.i.aircraft.Any(a => a != null && (string.Equals(a.unitName, n, StringComparison.OrdinalIgnoreCase)
                                                                                    || string.Equals(a.WorkshopTag, n, StringComparison.OrdinalIgnoreCase)));
            if (!Taken(name)) return name;
            var candidate = name + " (OpenPlane)";
            for (int i = 2; Taken(candidate); i++) candidate = $"{name} (OpenPlane {i.ToString(CultureInfo.InvariantCulture)})";
            return candidate;
        }

        /// <summary>A short hangar label from the plane's name, like the game's own ("CI-22"): up to 7 letters, digits or dashes.</summary>
        internal static string ShortCode(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 7) break;
            }
            var code = sb.ToString().Trim('-');
            return code.Length > 0 ? code : "OP";
        }

        /// <summary>A hash from the key, moved to the next free value if taken. Same planes give the same hashes everywhere.</summary>
        private static int FreeHash(string key, HashSet<int> taken)
        {
            uint h = 2166136261;
            foreach (var c in Encoding.UTF8.GetBytes("openplane:" + key)) { h ^= c; h *= 16777619; }
            int hash = (int)((h & 0x3FFFFFFFu) | 0x40000000u); // always in 0x40000000..0x7FFFFFFF: never 0, never negative
            while (taken.Contains(hash) || hash == 0) hash = hash == int.MaxValue ? 0x40000000 : hash + 1;
            taken.Add(hash);
            return hash;
        }

        private static void AddToEncyclopedia(Encyclopedia enc, AircraftDefinition def)
        {
            if (Encyclopedia.Lookup.ContainsKey(def.jsonKey))
                throw new InvalidOperationException($"key {def.jsonKey} already exists");
            enc.aircraft.Add(def);
            Encyclopedia.Lookup.Add(def.jsonKey, def);
            ((INetworkDefinition)def).LookupIndex = enc.IndexLookup.Count;
            enc.IndexLookup.Add(def);
        }

        /// <summary>
        /// Lobby fingerprint: OpenPlane's version, and every plane's key, content hash and prefab hash, sorted (ordinal).
        /// Different plane contents, or a different prefab hash, never share a lobby.
        /// </summary>
        private static string MakeSignature(IEnumerable<RegisteredPlane> planes)
        {
            var items = planes.Select(p => p.Key + ":" + p.ContentHash + ":" + p.PrefabHash.ToString(CultureInfo.InvariantCulture))
                              .OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (items.Count == 0) return "none";
            var joined = "openplane-" + Plugin.Version + "|" + string.Join("|", items);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(joined)), 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }
}
