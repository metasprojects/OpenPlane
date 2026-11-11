using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenPlane.App.Services;

/// <summary>What's installed in the game folder.</summary>
public sealed class ModStatus
{
    public string? GameDir { get; set; }
    public bool GameFound { get; set; }
    public bool GameExeFound { get; set; }
    public bool GameRunning { get; set; }
    /// <summary>BepInEx is there and complete: its core, its loader (winhttp.dll) and the loader's settings (doorstop_config.ini).</summary>
    public bool BepinexInstalled { get; set; }
    /// <summary>BepInEx's core is there but loader files are missing, so the game won't load mods; installing repairs it.</summary>
    public bool BepinexIncomplete { get; set; }
    /// <summary>BepInEx is there but its loader is switched off in doorstop_config.ini.</summary>
    public bool LoaderDisabled { get; set; }
    /// <summary>The BepInEx files that are missing, relative to the game folder.</summary>
    public List<string> BepinexMissing { get; set; } = new();
    public string? BepinexVersion { get; set; }
    public bool BlueprinterInstalled { get; set; }
    /// <summary>True when Blueprinter is installed somewhere other than OpenPlane's own folder (by the player or NOMM).</summary>
    public bool BlueprinterInstalledByOthers { get; set; }
    public string? BlueprinterVersion { get; set; }
    /// <summary>The Blueprinter in the game is older than the one OpenPlane's mod is made for.</summary>
    public bool BlueprinterTooOld { get; set; }
    /// <summary>Blueprinter is turned off in NOMM (it sits in BepInEx/disabledPlugins).</summary>
    public bool BlueprinterDisabledInNomm { get; set; }
    public bool ModInstalled { get; set; }
    public string? ModVersion { get; set; }
    /// <summary>Where the mod is, relative to the game folder (BepInEx/plugins/OpenPlane when OpenPlane installed it).</summary>
    public string? ModFolder { get; set; }
    /// <summary>The installed mod isn't OpenPlane's own copy: NOMM installed it (in BepInEx/plugins/&lt;NOMNOM id&gt;) or the
    /// player put it in another folder. OpenPlane then neither updates nor removes it.</summary>
    public bool ModManagedElsewhere { get; set; }
    /// <summary>The installed mod differs from the one bundled with this app.</summary>
    public bool UpdateAvailable { get; set; }
    public bool BundleAvailable { get; set; }
    public string? BundleVersion { get; set; }
    public bool NommDetected { get; set; }
    public bool DisabledInNomm { get; set; }
    public bool IsLinux { get; set; }
    public string LaunchOption { get; set; } = ModInstaller.LinuxLaunchOption;
    /// <summary>Things the player should know that don't stop the mod (an old Blueprinter, for example).</summary>
    public List<string> Warnings { get; set; } = new();
    /// <summary>One sentence for the status strip.</summary>
    public string Summary { get; set; } = "";
    /// <summary>ready, needs-install, needs-update, blueprinter-old, no-game, no-bundle.</summary>
    public string State { get; set; } = "";
}

/// <summary>
/// Installs OpenPlane into Nuclear Option: BepInEx 5 (downloaded from its official GitHub release and checked against
/// its published SHA-256), Blueprinter (bundled) and OpenPlane's mod files, each into its own folder under
/// BepInEx/plugins. Removing deletes only OpenPlane's two plugin folders. When NOMM manages the game, OpenPlane
/// doesn't install BepInEx or a second Blueprinter, and never installs a second copy of a mod NOMM has turned off.
/// </summary>
public sealed class ModInstaller
{
    public const string LinuxLaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%";
    public const string BepInExVersion = "5.4.23.4";
    public const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.4/BepInEx_win_x64_5.4.23.4.zip";
    public const string BepInExSha256 = "f881201b79da03e513bf97cdf39607ffa7f9e0d31a519b1aeeca8eb60f8309e7";
    public const long BepInExSize = 638940;
    public const string PluginFolder = "OpenPlane";
    /// <summary>OpenPlane's id in NOMNOM. NOMM unpacks a mod into BepInEx/plugins/&lt;id&gt; (and turns it off by moving that
    /// folder to BepInEx/disabledPlugins/&lt;id&gt;), so a NOMM install of OpenPlane is in BepInEx/plugins/com.meta.openplane.</summary>
    public const string NommId = "com.meta.openplane";
    public const string BlueprinterFolder = "Blueprinter";
    /// <summary>The Blueprinter version whose API the OpenPlane mod uses.</summary>
    public static readonly Version MinBlueprinter = new(2, 1, 1);

    /// <summary>The mod's BepInEx config file (its plugin GUID), and the setting that names an extra planes folder.</summary>
    public const string ModConfigFile = "com.meta.openplane.cfg";
    public const string ConfigSection = "Paths";
    public const string ExtraFolderKey = "ExtraAircraftFolder";

    /// <summary>Files a working BepInEx 5 install has, relative to the game folder.</summary>
    public static readonly string[] BepInExFiles = { Path.Combine("BepInEx", "core", "BepInEx.dll"), "winhttp.dll", "doorstop_config.ini" };

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly SettingsStore? _settings;

    public ModInstaller(AppPaths paths, HttpClient http, SettingsStore? settings = null) { _paths = paths; _http = http; _settings = settings; }

    private string BundleOpenPlane => Path.Combine(_paths.ModBundleDir, PluginFolder);
    private string BundleBlueprinter => Path.Combine(_paths.ModBundleDir, BlueprinterFolder);

    public bool BundleAvailable => File.Exists(Path.Combine(BundleOpenPlane, "OpenPlane.Mod.dll")) && File.Exists(Path.Combine(BundleOpenPlane, "OpenPlane.Core.dll"));

    /// <summary>The BepInEx files missing from a game folder (empty when BepInEx is complete).</summary>
    public static List<string> MissingBepInEx(string gameDir)
        => BepInExFiles.Where(f => !File.Exists(Path.Combine(gameDir, f))).ToList();

    public ModStatus Status()
    {
        var game = _paths.Game();
        var s = new ModStatus { GameDir = game.Path, IsLinux = OperatingSystem.IsLinux(), BundleAvailable = BundleAvailable };
        s.BundleVersion = s.BundleAvailable ? FileVersion(Path.Combine(BundleOpenPlane, "OpenPlane.Mod.dll")) : null;
        s.GameFound = SteamLocator.IsNuclearOptionFolder(game.Path);
        if (!s.GameFound)
        {
            s.State = "no-game";
            s.Summary = game.Path == null ? "Nuclear Option wasn't found. Choose its folder in Settings." : $"Nuclear Option wasn't found in \"{game.Path}\". Choose its folder in Settings.";
            return s;
        }
        var dir = game.Path!;
        s.GameExeFound = File.Exists(Path.Combine(dir, "NuclearOption.exe"));
        s.GameRunning = SystemShell.IsProcessRunning("NuclearOption");
        var core = Path.Combine(dir, "BepInEx", "core", "BepInEx.dll");
        s.BepinexMissing = MissingBepInEx(dir);
        bool coreThere = File.Exists(core);
        s.BepinexInstalled = s.BepinexMissing.Count == 0;
        s.BepinexIncomplete = coreThere && !s.BepinexInstalled;
        s.BepinexVersion = coreThere ? FileVersion(core) : null;
        var plugins = Path.Combine(dir, "BepInEx", "plugins");
        var blueprinters = FindBlueprinters(dir);
        s.BlueprinterInstalled = blueprinters.Count > 0;
        s.BlueprinterInstalledByOthers = blueprinters.Any(b => !IsOurBlueprinter(dir, b));
        s.BlueprinterDisabledInNomm = !s.BlueprinterInstalled && FindDisabledBlueprinters(dir).Count > 0;
        var others = blueprinters.Where(b => !IsOurBlueprinter(dir, b)).ToList();
        var oldest = others.Select(BlueprinterVersionOf).Where(v => v != null).OrderBy(v => v).FirstOrDefault();
        var shown = oldest ?? (blueprinters.Count > 0 ? BlueprinterVersionOf(blueprinters[0]) : null);
        s.BlueprinterVersion = shown?.ToString(3);
        if (oldest != null && oldest < MinBlueprinter)
        {
            s.BlueprinterTooOld = true;
            s.Warnings.Add($"The Blueprinter in the game is version {oldest.ToString(3)}; OpenPlane's mod needs {MinBlueprinter.ToString(3)} or newer and won't load with it. Update Blueprinter (in NOMM, or from github.com/nikkorap/NOBlueprinter-Releases).");
        }
        var copies = FindModFolders(plugins);
        var modFolder = copies.FirstOrDefault(f => IsOurModFolder(plugins, f)) ?? copies.FirstOrDefault();
        s.ModInstalled = modFolder != null;
        s.ModVersion = modFolder != null ? FileVersion(Path.Combine(modFolder, "OpenPlane.Mod.dll")) : null;
        s.ModFolder = modFolder != null ? Path.GetRelativePath(dir, modFolder) : null;
        s.ModManagedElsewhere = modFolder != null && !IsOurModFolder(plugins, modFolder);
        s.NommDetected = NommManages(dir);
        s.DisabledInNomm = TurnedOffInNomm(dir);
        if (copies.Count > 1)
            s.Warnings.Add("The OpenPlane mod is installed twice (" + string.Join(" and ", copies.Select(c => Path.GetRelativePath(dir, c)))
                + "), so the game loads only one of them. Remove one: in NOMM, or with Remove in Settings for OpenPlane's own copy.");
        if (modFolder != null && s.BundleAvailable)
        {
            bool differs = !SameFiles(BundleOpenPlane, modFolder, "OpenPlane.Mod.dll", "OpenPlane.Core.dll");
            if (!s.ModManagedElsewhere) s.UpdateAvailable = differs;
            else if (differs && Version.TryParse(s.BundleVersion, out var bundled) && Version.TryParse(s.ModVersion, out var installed) && bundled > installed)
                s.Warnings.Add($"NOMM has OpenPlane mod {s.ModVersion}; this app comes with {s.BundleVersion}. Update it in NOMM so your planes load the same way for everyone.");
        }

        s.LoaderDisabled = s.BepinexInstalled && !DoorstopEnabled(dir);
        if (s.LoaderDisabled) { s.State = "needs-install"; s.Summary = "BepInEx is installed but switched off (enabled = false in doorstop_config.ini), so the game loads no mods. Set it to true, or turn mods back on in NOMM."; }
        else if (s.DisabledInNomm && !s.ModInstalled) { s.State = "needs-install"; s.Summary = "OpenPlane is turned off in NOMM. Turn it back on there."; }
        else if (s.BlueprinterDisabledInNomm) { s.State = "needs-install"; s.Summary = "Blueprinter is turned off in NOMM, so the game can't load OpenPlane planes. Turn it back on there."; }
        else if (s.BepinexIncomplete)
        {
            s.State = "needs-install";
            s.Summary = $"BepInEx is incomplete ({string.Join(", ", s.BepinexMissing.Select(Path.GetFileName))} missing), so the game won't load mods. Install OpenPlane again to repair it.";
        }
        else if (s.ModInstalled && s.BlueprinterInstalled && !s.BepinexInstalled)
        {
            s.State = "needs-install";
            s.Summary = "OpenPlane's files are in the game folder, but BepInEx (the mod loader) is missing, so the game won't load them yet.";
        }
        else if (!s.BepinexInstalled || !s.BlueprinterInstalled || !s.ModInstalled)
        {
            s.State = s.BundleAvailable ? "needs-install" : "no-bundle";
            s.Summary = !s.BundleAvailable ? "This copy of OpenPlane doesn't include the mod files, so it can't install the mod."
                : "The OpenPlane mod isn't installed in Nuclear Option yet.";
        }
        else if (s.UpdateAvailable) { s.State = "needs-update"; s.Summary = "A newer OpenPlane mod is ready to install."; }
        else if (s.BlueprinterTooOld) { s.State = "blueprinter-old"; s.Summary = s.Warnings.First(w => w.Contains("Blueprinter")); }
        else
        {
            s.State = "ready";
            s.Summary = !s.ModManagedElsewhere ? $"OpenPlane {s.ModVersion} is installed."
                : File.Exists(Path.Combine(modFolder!, "meta.json")) ? $"OpenPlane {s.ModVersion} is installed through NOMM (update or remove it there)."
                : $"OpenPlane {s.ModVersion} is installed in {s.ModFolder}.";
        }
        return s;
    }

    /// <summary>
    /// Every folder under <paramref name="root"/> (BepInEx/plugins or disabledPlugins) that holds OpenPlane.Mod.dll:
    /// OpenPlane's own folder, NOMM's (named after the NOMNOM id), or any other a player used. BepInEx loads plugins
    /// from every subfolder, so each one counts.
    /// </summary>
    public static List<string> FindModFolders(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            foreach (var dll in Directory.EnumerateFiles(root, "OpenPlane.Mod.dll", options))
                found.Add(Path.GetDirectoryName(dll)!);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>NOMM has turned the OpenPlane mod off: its folder (under OpenPlane's name or the NOMNOM id) or the mod's DLL
    /// sits in BepInEx/disabledPlugins.</summary>
    public static bool TurnedOffInNomm(string gameDir)
    {
        var off = Path.Combine(gameDir, "BepInEx", "disabledPlugins");
        return Directory.Exists(Path.Combine(off, PluginFolder)) || Directory.Exists(Path.Combine(off, NommId)) || FindModFolders(off).Count > 0;
    }

    /// <summary>True for BepInEx/plugins/OpenPlane unless NOMM put it there (its meta.json without OpenPlane's marker).</summary>
    private static bool IsOurModFolder(string plugins, string folder)
        => SamePath(folder, Path.Combine(plugins, PluginFolder))
           && !(File.Exists(Path.Combine(folder, "meta.json")) && !File.Exists(Path.Combine(folder, OwnerMarker)));

    /// <summary>File OpenPlane writes into the folders it installs (Blueprinter, OpenPlane), so it never removes one the player or NOMM put there.</summary>
    public const string OwnerMarker = ".openplane-installed";
    private const string OwnerText = "Installed by OpenPlane. OpenPlane only removes this folder while this file is here.\n";

    /// <summary>False when BepInEx's loader is switched off (enabled = false in doorstop_config.ini, as NOMM does to turn mods off).</summary>
    public static bool DoorstopEnabled(string gameDir)
    {
        var f = Path.Combine(gameDir, "doorstop_config.ini");
        try
        {
            if (!File.Exists(f)) return true;
            bool inGeneral = true;   // doorstop 3 has no sections; doorstop 4 puts "enabled" under [General]
            foreach (var raw in File.ReadLines(f))
            {
                var l = raw.Trim();
                if (l.StartsWith("[")) { inGeneral = l.Equals("[General]", StringComparison.OrdinalIgnoreCase) || l.Equals("[UnityDoorstop]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inGeneral || l.StartsWith("#") || l.StartsWith(";")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(l, @"^enabled\s*=\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>True when OpenPlane can treat this folder as its own: it isn't there yet, is empty, or carries OpenPlane's marker.</summary>
    private static bool OursOrFresh(string folder)
        => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any() || File.Exists(Path.Combine(folder, OwnerMarker));

    /// <summary>True when this Blueprinter DLL is in BepInEx/plugins/Blueprinter and OpenPlane installed that folder.</summary>
    public static bool IsOurBlueprinter(string gameDir, string dll)
    {
        var ours = Path.Combine(gameDir, "BepInEx", "plugins", BlueprinterFolder);
        return dll.StartsWith(ours + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(ours, OwnerMarker));
    }

    /// <summary>Blueprinter DLLs anywhere under BepInEx/plugins.</summary>
    public static List<string> FindBlueprinters(string gameDir) => FindDlls(Path.Combine(gameDir, "BepInEx", "plugins"), "Blueprinter");

    /// <summary>Blueprinter DLLs NOMM has turned off (under BepInEx/disabledPlugins).</summary>
    public static List<string> FindDisabledBlueprinters(string gameDir) => FindDlls(Path.Combine(gameDir, "BepInEx", "disabledPlugins"), "Blueprinter");

    private static List<string> FindDlls(string folder, string prefix)
    {
        if (!Directory.Exists(folder)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>A Blueprinter DLL's version: its file version, or the version in its name ("Blueprinter_2.1.1.dll").</summary>
    public static Version? BlueprinterVersionOf(string dll)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            foreach (var text in new[] { info.FileVersion, info.ProductVersion })
            {
                var v = ParseVersion(text);
                if (v != null && v > new Version(0, 0, 0)) return v;
            }
        }
        catch { }
        var m = Regex.Match(Path.GetFileNameWithoutExtension(dll), @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success ? ParseVersion(m.Value) : null;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"^\s*(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>
    /// NOMM (Nuclear Option Mod Manager) keeps turned-off mods in BepInEx/disabledPlugins and writes a meta.json with
    /// an "artifact" into each mod folder it installs.
    /// </summary>
    public static bool NommManages(string gameDir)
    {
        var bep = Path.Combine(gameDir, "BepInEx");
        if (Directory.Exists(Path.Combine(bep, "disabledPlugins"))) return true;
        var plugins = Path.Combine(bep, "plugins");
        if (!Directory.Exists(plugins)) return false;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(plugins))
            {
                var meta = Path.Combine(d, "meta.json");
                if (File.Exists(meta) && File.ReadAllText(meta).Contains("\"artifact\"", StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string? FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var text = v.ProductVersion ?? v.FileVersion;
            if (string.IsNullOrEmpty(text)) return null;
            int plus = text.IndexOf('+');
            return plus > 0 ? text.Substring(0, plus) : text;
        }
        catch { return null; }
    }

    private static bool SameFiles(string a, string b, params string[] names)
    {
        foreach (var n in names)
        {
            var fa = Path.Combine(a, n); var fb = Path.Combine(b, n);
            if (!File.Exists(fa) || !File.Exists(fb)) return false;
            if (!SHA256.HashData(File.ReadAllBytes(fa)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(fb)))) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- planes folder in the mod's config

    /// <summary>
    /// Tells the mod about a planes folder the player chose: writes it as ExtraAircraftFolder in
    /// &lt;game&gt;/BepInEx/config/com.meta.openplane.cfg (that one key only; every other key stays). With the default
    /// planes folder (which the mod always loads) it clears a value OpenPlane wrote earlier, and leaves anything else
    /// alone. Returns a sentence for the player when something happened or needs attention, else null.
    /// </summary>
    public string? SyncPlanesFolder()
    {
        try
        {
            var game = _paths.Game().Path;
            if (!SteamLocator.IsNuclearOptionFolder(game)) return null;
            var bep = Path.Combine(game!, "BepInEx");
            if (!Directory.Exists(bep)) return null; // installing the mod writes it
            var cfg = Path.Combine(bep, "config", ModConfigFile);
            var planes = _paths.Planes().Path;
            bool custom = planes != null && !SamePath(planes, _paths.DefaultPlanesDir());
            var current = ModConfig.Read(cfg, ConfigSection, ExtraFolderKey);
            var written = _settings?.Get().ModConfigFolder;
            if (!custom)
            {
                if (string.IsNullOrEmpty(current) || written == null || !SamePath(current!, written)) return null;
                ModConfig.Set(cfg, ConfigSection, ExtraFolderKey, "", ExtraFolderComment);
                _settings?.Update(s => s.ModConfigFolder = null);
                return "The game loads planes from its own planes folder again.";
            }
            var wanted = GamePath(planes!);
            string? warning = wanted.StartsWith(@"\\", StringComparison.Ordinal) || wanted.IndexOfAny(new[] { '\'', '"' }) >= 0
                ? " BepInEx may misread this folder's name (a network path or a quote mark) after the game's first start; a folder on a drive letter, without quote marks, is safer."
                : null;
            if (current != null && SamePath(current, wanted)) return warning?.Trim();
            ModConfig.Set(cfg, ConfigSection, ExtraFolderKey, wanted, ExtraFolderComment);
            _settings?.Update(s => s.ModConfigFolder = wanted);
            return $"The game will also load planes from \"{planes}\" (set in BepInEx/config/{ModConfigFile}). Restart Nuclear Option if it's running." + warning;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"OpenPlane couldn't tell the mod about the planes folder ({e.Message}). Until it can, the game loads planes only from its own planes folder.";
        }
    }

    private const string ExtraFolderComment = "Optional second folder to load .oplane files from (besides Nuclear Option's user folder).";

    // ---------------------------------------------------------------- multiplayer: what this computer allows when it hosts

    /// <summary>The mod's host settings ([Multiplayer] in its config file), as the Settings screen shows and edits them.</summary>
    public sealed class HostRulesView
    {
        public bool Allow { get; set; } = true;
        /// <summary>0 = no limit.</summary>
        public double MaxThrustToWeight { get; set; }
        /// <summary>0 = no limit.</summary>
        public int MaxHardpoints { get; set; }
        /// <summary>False when the game or BepInEx isn't there yet, so there is no config file to change.</summary>
        public bool Available { get; set; }
        public string? Note { get; set; }
    }

    public const string HostSection = "Multiplayer";

    private string? ModConfigPath()
    {
        var game = _paths.Game().Path;
        if (!SteamLocator.IsNuclearOptionFolder(game)) return null;
        var bep = Path.Combine(game!, "BepInEx");
        return Directory.Exists(bep) ? Path.Combine(bep, "config", ModConfigFile) : null;
    }

    public HostRulesView HostRules()
    {
        var cfg = ModConfigPath();
        if (cfg == null) return new HostRulesView { Available = false, Note = "Install the OpenPlane mod first; these settings live in its config file." };
        var v = new HostRulesView { Available = true };
        var allow = ModConfig.Read(cfg, HostSection, "AllowOpenPlanePlanes");
        if (allow != null) v.Allow = !allow.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        if (double.TryParse(ModConfig.Read(cfg, HostSection, "MaxThrustToWeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tw) && tw > 0 && tw < 100)
            v.MaxThrustToWeight = tw;
        if (int.TryParse(ModConfig.Read(cfg, HostSection, "MaxHardpoints"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hp) && hp > 0)
            v.MaxHardpoints = hp;
        return v;
    }

    public HostRulesView SetHostRules(HostRulesView wanted)
    {
        var cfg = ModConfigPath() ?? throw new UserFacingException("Install the OpenPlane mod first; these settings live in its config file.", 409);
        if (double.IsNaN(wanted.MaxThrustToWeight) || wanted.MaxThrustToWeight < 0 || wanted.MaxThrustToWeight > 10)
            throw new UserFacingException("The thrust to weight limit must be between 0 (no limit) and 10.");
        if (wanted.MaxHardpoints < 0 || wanted.MaxHardpoints > 64)
            throw new UserFacingException("The hardpoint limit must be between 0 (no limit) and 64.");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        ModConfig.Set(cfg, HostSection, "AllowOpenPlanePlanes", wanted.Allow ? "true" : "false",
            "When you host (single-player, a lobby or a dedicated server): let players fly OpenPlane planes. Off hides them in every hangar.", "Boolean", "true");
        ModConfig.Set(cfg, HostSection, "MaxThrustToWeight", Math.Round(wanted.MaxThrustToWeight, 2).ToString("0.##", inv),
            "When you host: OpenPlane planes with more thrust for their weight than this (full fuel, no weapons) can't be flown. 0 = no limit.", "Single", "0");
        ModConfig.Set(cfg, HostSection, "MaxHardpoints", wanted.MaxHardpoints.ToString(inv),
            "When you host: OpenPlane planes with more weapon hardpoints than this can't be flown. 0 = no limit.", "Int32", "0");
        var v = HostRules();
        v.Note = "Saved. Nuclear Option reads these when it starts: restart it if it's running.";
        return v;
    }

    /// <summary>A folder as the game sees it: the full path on Windows; under Proton, Wine's Z: drive maps to "/".</summary>
    public static string GamePath(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!OperatingSystem.IsWindows() && full.StartsWith("/", StringComparison.Ordinal)) return "Z:" + full.Replace('/', '\\');
        return full;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            var cmp = OperatingSystem.IsWindows() || a.Contains(':') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string Norm(string p) => (p.Contains(':') && !OperatingSystem.IsWindows() ? p : Path.GetFullPath(p)).TrimEnd('\\', '/');
            return string.Equals(Norm(a), Norm(b), cmp);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // ---------------------------------------------------------------- install

    public async Task Install(Job job, CancellationToken ct = default)
    {
        var step = job.Step("Checking the game folder");
        var game = _paths.Game().Path;
        if (!SteamLocator.IsNuclearOptionFolder(game))
            throw new UserFacingException(game == null
                ? "Nuclear Option wasn't found. Choose its folder in Settings (the folder that holds NuclearOption.exe), then try again."
                : $"\"{game}\" doesn't look like Nuclear Option's folder (it should hold NuclearOption.exe and NuclearOption_Data). Choose the right folder in Settings.");
        var dir = game!;
        if (!BundleAvailable)
            throw new UserFacingException("This copy of OpenPlane doesn't include the mod files. Download OpenPlane from its release page (the release includes them), or build the mod from source as the README explains.");
        if (SystemShell.IsProcessRunning("NuclearOption"))
            throw new UserFacingException("Nuclear Option is running. Close the game, then install again (Windows locks the mod files while it runs).");
        // A copy NOMM has turned off would come back next to ours when it's turned on again: never install a second one.
        if (TurnedOffInNomm(dir))
            throw new UserFacingException("OpenPlane is turned off in NOMM. Turn it back on in NOMM (or remove it there) instead of installing a second copy.");
        step.Detail = dir;
        bool nomm = NommManages(dir);

        // BepInEx
        step = job.Step("BepInEx 5 (the mod loader)");
        var core = Path.Combine(dir, "BepInEx", "core", "BepInEx.dll");
        var missing = MissingBepInEx(dir);
        if (missing.Count == 0) { step.State = "skipped"; step.Detail = "Already installed."; }
        else if (!File.Exists(core) && nomm) throw new UserFacingException("NOMM manages this game's mods, but BepInEx is missing. Install BepInEx with NOMM, then try again.");
        else if (!File.Exists(Path.Combine(dir, "NuclearOption.exe")))
        {
            step.State = "skipped";
            step.Detail = "NuclearOption.exe isn't in this folder, so BepInEx wasn't downloaded here. OpenPlane's files were still copied, but they only load once BepInEx is installed in the real game folder.";
        }
        else
        {
            var zip = await DownloadBepInEx(job, step, ct);
            if (File.Exists(core))
            {
                // Repair: only the missing loader files, so a newer BepInEx (from NOMM or the player) isn't replaced.
                step.Text = "Repairing BepInEx";
                ExtractInto(zip, dir, overwrite: false);
                step.Detail = "Put back " + string.Join(", ", missing.Select(Path.GetFileName)) + " from the official BepInEx " + BepInExVersion + " release.";
            }
            else
            {
                step.Text = "Installing BepInEx " + BepInExVersion;
                // Another loader's proxy files (BepInEx 6, another doorstop) are kept, not replaced.
                var clash = new[] { "winhttp.dll", "doorstop_config.ini" }.Where(f => File.Exists(Path.Combine(dir, f))).ToList();
                ExtractInto(zip, dir, overwrite: clash.Count == 0);
                step.Detail = "Downloaded from github.com/BepInEx and checked.";
                if (clash.Count > 0)
                    job.NextSteps.Add($"Another mod loader's {string.Join(" and ", clash)} were already in the game folder and were kept. If OpenPlane planes don't appear in the game, that loader isn't BepInEx 5: remove it, or install BepInEx 5 yourself.");
            }
        }

        // Blueprinter
        step = job.Step("Blueprinter (plane loader by nikkorap)");
        var plugins = Path.Combine(dir, "BepInEx", "plugins");
        var ours = Path.Combine(plugins, BlueprinterFolder);
        var others = FindBlueprinters(dir).Where(b => !IsOurBlueprinter(dir, b)).ToList();
        if (others.Count > 0)
        {
            step.State = "skipped";
            step.Detail = "Already installed (" + Path.GetFileName(others[0]) + ").";
            var oldest = others.Select(BlueprinterVersionOf).Where(v => v != null).OrderBy(v => v).FirstOrDefault();
            if (oldest != null && oldest < MinBlueprinter)
                job.NextSteps.Add($"The Blueprinter already in the game is version {oldest.ToString(3)}, older than the {MinBlueprinter.ToString(3)} OpenPlane needs, so OpenPlane planes won't load. Update Blueprinter (in NOMM, or from github.com/nikkorap/NOBlueprinter-Releases).");
        }
        else if (FindBlueprinters(dir).Count == 0 && FindDisabledBlueprinters(dir).Count > 0)
        {
            step.State = "skipped";
            step.Detail = "Blueprinter is turned off in NOMM, so OpenPlane didn't install a second copy.";
            job.NextSteps.Add("Turn Blueprinter back on in NOMM: OpenPlane planes need it.");
        }
        else if (!Directory.Exists(BundleBlueprinter) || !Directory.EnumerateFiles(BundleBlueprinter, "Blueprinter*.dll").Any())
            throw new UserFacingException("Blueprinter is missing from this copy of OpenPlane. Download OpenPlane again from its release page.");
        else
        {
            bool fresh = OursOrFresh(ours);
            CopyFolder(BundleBlueprinter, ours);
            if (fresh) File.WriteAllText(Path.Combine(ours, OwnerMarker), OwnerText);
            else step.Detail = "Copied into the existing Blueprinter folder, which OpenPlane will leave in place if you remove OpenPlane.";
        }

        // OpenPlane
        step = job.Step("OpenPlane mod");
        var modFolder = Path.Combine(plugins, PluginFolder);
        var elsewhere = FindModFolders(plugins).FirstOrDefault(f => !IsOurModFolder(plugins, f));
        if (elsewhere != null)
        {
            // NOMM's copy (BepInEx/plugins/com.meta.openplane) or one the player put somewhere else: a second copy next to
            // it would make the game load only one of them.
            step.State = "skipped";
            step.Detail = File.Exists(Path.Combine(elsewhere, "meta.json"))
                ? "NOMM installed the OpenPlane mod, so OpenPlane left that copy alone."
                : $"The OpenPlane mod is already in {Path.GetRelativePath(dir, elsewhere)}, so OpenPlane left that copy alone.";
            job.NextSteps.Add(File.Exists(Path.Combine(elsewhere, "meta.json"))
                ? "NOMM manages the OpenPlane mod in this game: update or remove it in NOMM."
                : $"To let OpenPlane install and update the mod, delete {Path.GetRelativePath(dir, elsewhere)} and install again.");
        }
        else
        {
            bool freshMod = OursOrFresh(modFolder) || OnlyBundleFiles(modFolder);
            CopyFolder(BundleOpenPlane, modFolder);
            if (freshMod) File.WriteAllText(Path.Combine(modFolder, OwnerMarker), OwnerText);
            step.Detail = Path.Combine("BepInEx", "plugins", PluginFolder);
        }

        step = job.Step("Planes folder");
        var planes = _paths.Planes().Path;
        if (planes != null) { Directory.CreateDirectory(planes); step.Detail = planes; }
        var note = SyncPlanesFolder();
        if (note != null) job.NextSteps.Add(note);

        if (OperatingSystem.IsLinux())
            job.NextSteps.Add("One-time step on Steam Deck and Linux: in Steam, open Nuclear Option's Properties and paste this into Launch Options: " + LinuxLaunchOption);
        if (nomm)
            job.NextSteps.Add("NOMM manages this game's mods. It will list Blueprinter and OpenPlane as mods it didn't install; that's fine.");
        job.NextSteps.Add("While Blueprinter is installed, Nuclear Option only shows multiplayer lobbies that use the same mods.");
        bool complete = MissingBepInEx(dir).Count == 0;
        job.Partial = !complete;
        job.Finish(complete ? "OpenPlane is installed. Start Nuclear Option to fly your planes." : "OpenPlane's files were copied, but BepInEx still needs to be installed in the real game folder.");
    }

    private async Task<string> DownloadBepInEx(Job job, JobStep step, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.DownloadCacheDir);
        var file = Path.Combine(_paths.DownloadCacheDir, $"BepInEx_win_x64_{BepInExVersion}.zip");
        if (File.Exists(file) && Verify(file)) { step.Detail = "Using the copy downloaded earlier."; return file; }

        step.Text = $"Downloading BepInEx {BepInExVersion} from GitHub ({BepInExSize / 1024} KB)";
        var part = file + ".part";
        try
        {
            using var response = await _http.GetAsync(BepInExUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UserFacingException($"GitHub answered \"{(int)response.StatusCode} {response.ReasonPhrase}\" when downloading BepInEx. Try again in a minute, or install BepInEx 5 (win x64) yourself from github.com/BepInEx/BepInEx/releases.");
            long total = response.Content.Headers.ContentLength ?? BepInExSize;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                long done = 0; int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (done > 16 * BepInExSize) throw new UserFacingException("The BepInEx download was much bigger than the official release, so OpenPlane stopped it. Try again later.");
                    job.Progress = Math.Min(1, (double)done / Math.Max(1, total));
                }
            }
        }
        catch (HttpRequestException e)
        {
            throw new UserFacingException($"BepInEx couldn't be downloaded ({e.Message}). Check your internet connection and try again, or install BepInEx 5 (win x64) yourself from github.com/BepInEx/BepInEx/releases.");
        }
        catch (TaskCanceledException)
        {
            throw new UserFacingException("Downloading BepInEx took too long. Check your internet connection and try again.");
        }
        if (!Verify(part))
        {
            File.Delete(part);
            throw new UserFacingException("The downloaded BepInEx file didn't match the official release (its checksum is different), so OpenPlane didn't use it. Try again later.");
        }
        File.Move(part, file, overwrite: true);
        return file;
    }

    private static bool Verify(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(BepInExSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unpacks a zip into a folder, refusing entries that would land outside it. Without <paramref name="overwrite"/>,
    /// files already there are kept (used to repair a BepInEx install).
    /// </summary>
    public static void ExtractInto(string zipPath, string folder, bool overwrite = true)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(target); continue; }
            if (!overwrite && File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>True when every file in the folder has the name of a file OpenPlane's own mod bundle ships (no subfolders).</summary>
    private bool OnlyBundleFiles(string folder)
    {
        if (!Directory.Exists(folder) || Directory.EnumerateDirectories(folder).Any()) return false;
        if (!Directory.Exists(BundleOpenPlane)) return false;
        var names = new HashSet<string>(Directory.EnumerateFiles(BundleOpenPlane).Select(p => Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase) { OwnerMarker };
        return Directory.EnumerateFiles(folder).All(p => names.Contains(Path.GetFileName(p)));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true); }
            catch (IOException e) { throw new UserFacingException($"Couldn't copy {Path.GetFileName(f)} into the game folder ({e.Message}). Close Nuclear Option and try again."); }
            catch (UnauthorizedAccessException) { throw new UserFacingException($"Windows didn't allow OpenPlane to write into \"{to}\". Check the folder's permissions, or run Steam's 'Verify integrity' and try again."); }
        }
    }

    // ---------------------------------------------------------------- remove

    public void Remove(Job job, bool removeBlueprinter)
    {
        var step = job.Step("Checking the game folder");
        var game = _paths.Game().Path;
        if (!SteamLocator.IsNuclearOptionFolder(game))
            throw new UserFacingException("Nuclear Option wasn't found, so there's nothing to remove. Choose its folder in Settings if it's somewhere else.");
        if (SystemShell.IsProcessRunning("NuclearOption"))
            throw new UserFacingException("Nuclear Option is running. Close the game, then try again.");
        var plugins = Path.Combine(game!, "BepInEx", "plugins");

        step = job.Step("Removing the OpenPlane mod");
        var ours = Path.Combine(plugins, PluginFolder);
        if (!Directory.Exists(ours)) { step.State = "skipped"; step.Detail = "It wasn't installed."; }
        else if (File.Exists(Path.Combine(ours, OwnerMarker))) Directory.Delete(ours, recursive: true);
        else if (OnlyBundleFiles(ours))
        {
            // Installed by an OpenPlane version from before the marker: only its own files are there.
            foreach (var file in Directory.EnumerateFiles(ours)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(ours).Any()) Directory.Delete(ours);
        }
        else { step.State = "skipped"; step.Detail = "This copy wasn't installed by OpenPlane (NOMM or you added it, with other files in its folder), so it was left alone. Remove it in NOMM."; }

        step = job.Step("Removing Blueprinter");
        var bp = Path.Combine(plugins, BlueprinterFolder);
        if (!removeBlueprinter) { step.State = "skipped"; step.Detail = "Kept, as you asked."; }
        else if (Directory.Exists(bp) && File.Exists(Path.Combine(bp, OwnerMarker))) Directory.Delete(bp, recursive: true);
        else if (Directory.Exists(bp)) { step.State = "skipped"; step.Detail = "This Blueprinter wasn't installed by OpenPlane (you or NOMM added it), so it was left alone."; }
        else { step.State = "skipped"; step.Detail = "OpenPlane's copy wasn't installed."; }

        var left = FindModFolders(plugins).Where(f => !SamePath(f, ours)).ToList();
        if (left.Count > 0)
            job.NextSteps.Add("Another copy of the OpenPlane mod is still in " + Path.GetRelativePath(game!, left[0])
                + (File.Exists(Path.Combine(left[0], "meta.json")) ? ": NOMM installed it, so remove it in NOMM." : ": OpenPlane didn't install it, so it was left alone."));
        job.NextSteps.Add("Your planes are still in the planes folder; OpenPlane didn't delete them.");
        job.NextSteps.Add("BepInEx stays installed (other mods may use it).");
        job.Finish("OpenPlane was removed from Nuclear Option.");
    }
}
