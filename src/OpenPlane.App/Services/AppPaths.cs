using System.Runtime.InteropServices;

namespace OpenPlane.App.Services;

/// <summary>A path the app uses, and where it came from: "command line", "settings", "detected" or "default".</summary>
public sealed record ResolvedPath(string? Path, string Source, bool Exists);

/// <summary>A folder searched for Flyout craft.</summary>
public sealed record FlyoutFolder(string Path, string Source, bool Exists);

/// <summary>
/// Works out every folder OpenPlane uses: command-line flags first, then Settings, then what Steam says, then the
/// usual default. Steam lookups are cached for a few seconds.
/// </summary>
public sealed class AppPaths
{
    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly SteamLocator _steam;
    private readonly object _lock = new();
    private (DateTime at, SteamApp? game, List<string> libraries)? _cache;

    public AppPaths(AppOptions options, SettingsStore settings, SteamLocator steam, string dataDir)
    {
        _options = options; _settings = settings; _steam = steam; DataDir = dataDir;
    }

    public string DataDir { get; }
    public string ImportedCraftDir => Path.Combine(DataDir, "Imported craft");
    public string DeletedPlanesDir => Path.Combine(DataDir, "Deleted planes");
    public string CacheDir => Path.Combine(DataDir, "Cache");
    public string DraftDir => Path.Combine(DataDir, "Draft");
    public string DownloadCacheDir => Path.Combine(DataDir, "Downloads");
    public string ModBundleDir => _options.ModDir ?? Path.Combine(AppContext.BaseDirectory, "mod");
    public bool IsLinux => OperatingSystem.IsLinux();

    public static string DefaultDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "OpenPlane");
    }

    public void Refresh() { lock (_lock) _cache = null; }

    private (SteamApp? game, List<string> libraries) Steam()
    {
        lock (_lock)
        {
            if (_cache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(10)) return (c.game, c.libraries);
            SteamApp? game = null; var libs = new List<string>();
            try { libs = _steam.Libraries(); game = _steam.FindApp(SteamLocator.NuclearOptionAppId); } catch { }
            _cache = (DateTime.UtcNow, game, libs);
            return (game, libs);
        }
    }

    public ResolvedPath Game()
    {
        if (_options.GameDir != null) return new(_options.GameDir, "command line", Directory.Exists(_options.GameDir));
        var s = _settings.Get();
        if (!string.IsNullOrWhiteSpace(s.GameDir)) return new(s.GameDir, "settings", Directory.Exists(s.GameDir));
        var found = Steam().game;
        if (found != null) return new(found.InstallPath, "detected", true);
        return new(null, "not found", false);
    }

    /// <summary>The Steam library that holds the game (for Proton paths), from its folder.</summary>
    private string? GameLibrary()
    {
        var g = Game().Path;
        if (g == null) return null;
        // <library>/steamapps/common/<game>
        var common = Directory.GetParent(g);
        var steamapps = common?.Parent;
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true && steamapps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
            return steamapps.Parent?.FullName;
        return Steam().game?.LibraryPath;
    }

    public ResolvedPath Planes()
    {
        if (_options.PlanesDir != null) return new(_options.PlanesDir, "command line", Directory.Exists(_options.PlanesDir));
        var s = _settings.Get();
        if (!string.IsNullOrWhiteSpace(s.PlanesDir)) return new(s.PlanesDir, "settings", Directory.Exists(s.PlanesDir));
        var p = DefaultPlanesDir();
        return new(p, IsLinux ? "detected" : "default", Directory.Exists(p));
    }

    /// <summary>Nuclear Option's user folder + OpenPlane/Aircraft (inside the Proton prefix on Linux).</summary>
    public string DefaultPlanesDir()
    {
        const string rel = "AppData/LocalLow/Shockfront/NuclearOption/OpenPlane/Aircraft";
        if (!IsLinux)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Shockfront", "NuclearOption", "OpenPlane", "Aircraft");
        var lib = GameLibrary() ?? Steam().libraries.FirstOrDefault()
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam");
        return Path.Combine(SteamLocator.ProtonUserFolder(lib, SteamLocator.NuclearOptionAppId), rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Folders searched for Flyout craft: flags (if any) replace the defaults; Settings adds to them.</summary>
    public List<FlyoutFolder> FlyoutFolders()
    {
        var list = new List<FlyoutFolder>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string path, string source)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (seen.Add(full.TrimEnd(Path.DirectorySeparatorChar))) list.Add(new(full, source, Directory.Exists(full)));
        }
        if (_options.FlyoutDirs.Count > 0)
            foreach (var d in _options.FlyoutDirs) Add(d, "command line");
        else
        {
            var hidden = new HashSet<string>(_settings.Get().HiddenFlyoutDirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in DefaultFlyoutDirs().Where(d => !hidden.Contains(d))) Add(d, "detected");
        }
        foreach (var d in _settings.Get().FlyoutDirs) Add(d, "settings");
        Add(ImportedCraftDir, "imported");
        return list;
    }

    /// <summary>Flyout's own craft folder: LocalLow on Windows, the Proton prefix of app 777390 on Linux.</summary>
    public List<string> DefaultFlyoutDirs()
    {
        const string rel = "AppData/LocalLow/Stonext Games/Flyout/Craft";
        var list = new List<string>();
        if (!IsLinux)
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Stonext Games", "Flyout", "Craft"));
        else
            foreach (var lib in Steam().libraries)
            {
                var p = Path.Combine(SteamLocator.ProtonUserFolder(lib, SteamLocator.FlyoutAppId), rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(p)) list.Add(p);
            }
        return list;
    }

    public string DownloadsDir()
    {
        if (_options.DownloadsDir != null) return _options.DownloadsDir;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    var p = Marshal.PtrToStringUni(ptr);
                    Marshal.FreeCoTaskMem(ptr);
                    if (!string.IsNullOrEmpty(p)) return p;
                }
            }
            catch { }
        }
        else
        {
            // XDG_DOWNLOAD_DIR="$HOME/Downloads" in ~/.config/user-dirs.dirs
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var file = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "user-dirs.dirs");
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                        if (line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
                        {
                            var v = line.Substring("XDG_DOWNLOAD_DIR=".Length).Trim().Trim('"').Replace("$HOME", home);
                            if (v.Length > 0) return v;
                        }
            }
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);
}
