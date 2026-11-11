using System.Runtime.Versioning;

namespace OpenPlane.App.Services;

/// <summary>Where a Steam game is installed: the library that holds it and its folder.</summary>
public sealed record SteamApp(string LibraryPath, string InstallPath);

/// <summary>
/// Finds Steam, its library folders (libraryfolders.vdf) and installed games (appmanifest_*.acf), on Windows and
/// on Linux / Steam Deck. Every path can be overridden; this only supplies the defaults.
/// </summary>
public sealed class SteamLocator
{
    public const string NuclearOptionAppId = "2168680";
    public const string FlyoutAppId = "777390";

    private readonly Func<IEnumerable<string>> _roots;

    /// <param name="roots">Steam folders to look in; null uses the registry and the usual install locations.</param>
    public SteamLocator(Func<IEnumerable<string>>? roots = null) { _roots = roots ?? DefaultRoots; }

    /// <summary>Steam install folders that exist on this machine, best first, without duplicates.</summary>
    public List<string> SteamRoots()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _roots())
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string full;
            try { full = Path.GetFullPath(r); } catch { continue; }
            if (!Directory.Exists(full)) continue;
            string real = full;
            try { real = new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full; } catch { }
            if (seen.Add(real.TrimEnd(Path.DirectorySeparatorChar))) result.Add(full);
        }
        return result;
    }

    /// <summary>Every library folder of every Steam install (the install folder itself included).</summary>
    public List<string> Libraries()
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string p)
        {
            try
            {
                var full = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(full) && seen.Add(full)) libs.Add(full);
            }
            catch { }
        }
        foreach (var root in SteamRoots())
        {
            Add(root);
            foreach (var p in LibraryPathsFromVdf(Path.Combine(root, "steamapps", "libraryfolders.vdf"))) Add(p);
            foreach (var p in LibraryPathsFromVdf(Path.Combine(root, "config", "libraryfolders.vdf"))) Add(p);
        }
        return libs;
    }

    /// <summary>Library paths listed in a libraryfolders.vdf (new format with "path" keys, or the old numbered list).</summary>
    public static List<string> LibraryPathsFromVdf(string vdfPath)
    {
        var doc = Vdf.TryParseFile(vdfPath);
        return doc == null ? new List<string>() : LibraryPaths(doc);
    }

    public static List<string> LibraryPaths(VdfObject doc)
    {
        var list = new List<string>();
        var folders = doc.Child("libraryfolders") ?? doc.Child("LibraryFolders") ?? doc;
        foreach (var kv in folders.Items)
        {
            if (kv.Value is VdfObject o && o.Text("path") is { Length: > 0 } p) list.Add(p);
            else if (kv.Value is string s && int.TryParse(kv.Key, out _) && s.Length > 0) list.Add(s);
        }
        return list;
    }

    /// <summary>Finds an installed game by its Steam app id, or null.</summary>
    public SteamApp? FindApp(string appId)
    {
        foreach (var lib in Libraries())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var doc = Vdf.TryParseFile(manifest);
            var state = doc?.Child("AppState") ?? doc;
            var installDir = state?.Text("installdir");
            if (string.IsNullOrWhiteSpace(installDir)) installDir = appId == NuclearOptionAppId ? "Nuclear Option" : null;
            if (installDir == null) continue;
            var path = Path.Combine(lib, "steamapps", "common", installDir);
            if (Directory.Exists(path)) return new SteamApp(lib, path);
        }
        return null;
    }

    /// <summary>Folders that look like a Steam library containing a compatdata (Proton) prefix for this app.</summary>
    public List<string> ProtonPrefixes(string appId)
        => Libraries().Select(l => Path.Combine(l, "steamapps", "compatdata", appId, "pfx")).Where(Directory.Exists).ToList();

    /// <summary>The user's Windows profile folder inside a Proton prefix.</summary>
    public static string ProtonUserFolder(string library, string appId)
        => Path.Combine(library, "steamapps", "compatdata", appId, "pfx", "drive_c", "users", "steamuser");

    private static IEnumerable<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var r in RegistryRoots()) yield return r;
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryRoots()
    {
        var list = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && p.Length > 0) list.Add(p.Replace('/', '\\'));
        }
        catch { }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string p && p.Length > 0) list.Add(p);
        }
        catch { }
        return list;
    }

    /// <summary>True when the folder looks like a Nuclear Option install (the game's data folder or its exe is there).</summary>
    public static bool IsNuclearOptionFolder(string? dir)
        => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
           && (Directory.Exists(Path.Combine(dir, "NuclearOption_Data")) || File.Exists(Path.Combine(dir, "NuclearOption.exe")));
}
