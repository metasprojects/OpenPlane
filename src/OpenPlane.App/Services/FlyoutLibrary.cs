using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Preview;

namespace OpenPlane.App.Services;

/// <summary>A Flyout craft found on disk.</summary>
public sealed class CraftInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    /// <summary>Flyout save version from meta.txt (for example 0.2504).</summary>
    public string? Version { get; init; }
    public DateTime Modified { get; init; }
    public string Source { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public bool Helicopter { get; init; }
    public bool UsesModParts { get; init; }
}

/// <summary>Finds Flyout craft folders (any folder holding data.txt), draws their thumbnails and imports zipped craft.</summary>
public sealed class FlyoutLibrary
{
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, CraftInfo> _byId = new();
    private readonly ConcurrentDictionary<string, (DateTime stamp, bool heli, bool mods)> _scanCache = new();
    private readonly SemaphoreSlim _thumbGate = new(2);
    private const int MaxDepth = 5;

    public FlyoutLibrary(AppPaths paths) { _paths = paths; }

    public static string IdFor(string folder)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 8).ToLowerInvariant();
    }

    /// <summary>Every craft in every Flyout folder, newest first.</summary>
    public List<CraftInfo> Scan()
    {
        var list = new List<CraftInfo>();
        var seen = new HashSet<string>();
        foreach (var src in _paths.FlyoutFolders().Where(f => f.Exists))
            foreach (var folder in CraftFolders(src.Path))
            {
                var info = Describe(folder, src);
                if (info == null || !seen.Add(info.Id)) continue;
                _byId[info.Id] = info;
                list.Add(info);
            }
        return list.OrderByDescending(c => c.Modified).ToList();
    }

    public CraftInfo? Find(string id)
    {
        if (_byId.TryGetValue(id, out var c) && Directory.Exists(c.Folder)) return c;
        Scan();
        return _byId.TryGetValue(id, out c) ? c : null;
    }

    private static IEnumerable<string> CraftFolders(string root)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (File.Exists(Path.Combine(dir, "data.txt"))) { yield return dir; continue; }
            if (depth >= MaxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var s in subs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(s);
                if (name.Equals("Textures", StringComparison.OrdinalIgnoreCase) || name.Equals("Source", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push((s, depth + 1));
            }
        }
    }

    private CraftInfo? Describe(string folder, FlyoutFolder src)
    {
        try
        {
            var data = Path.Combine(folder, "data.txt");
            var stamp = File.GetLastWriteTimeUtc(data);
            string? name = null, version = null;
            var meta = Path.Combine(folder, "meta.txt");
            if (File.Exists(meta))
                foreach (var line in File.ReadLines(meta))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim(); var v = line.Substring(eq + 1).Trim();
                    if (k == "name" && v.Length > 0) name = v;
                    else if (k == "version" && v.Length > 0) version = v;
                }
            if (!_scanCache.TryGetValue(folder, out var scan) || scan.stamp != stamp)
            {
                var text = File.ReadAllText(data);
                scan = (stamp, text.Contains("resource_path=Parts/MainRotor", StringComparison.Ordinal),
                        text.Contains("resource_path=*", StringComparison.Ordinal));
                _scanCache[folder] = scan;
            }
            return new CraftInfo
            {
                Id = IdFor(folder), Name = name ?? Path.GetFileName(folder), Folder = folder, Version = version,
                Modified = stamp, Source = src.Source, SourceFolder = src.Path, Helicopter = scan.heli, UsesModParts = scan.mods,
            };
        }
        catch (Exception) { return null; }
    }

    /// <summary>A three-view PNG of the craft as OpenPlane converts it (cached on disk by data.txt's date).</summary>
    public async Task<byte[]> Thumbnail(CraftInfo craft)
    {
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(craft.Folder, "data.txt")).Ticks;
        // The app version is in the name: a new OpenPlane converts differently, so its pictures are redrawn.
        var version = OpenPlane.Core.BuildInfo.Version.Replace('.', '_');
        var dir = Path.Combine(_paths.CacheDir, "craft-thumbs");
        var cacheFile = Path.Combine(dir, $"{craft.Id}-{stamp}-v{version}.png");
        if (File.Exists(cacheFile)) return await File.ReadAllBytesAsync(cacheFile);
        await _thumbGate.WaitAsync();
        try
        {
            if (File.Exists(cacheFile)) return await File.ReadAllBytesAsync(cacheFile);
            var png = await Task.Run(() =>
            {
                var result = FlyoutConverter.Convert(FlyoutCraft.Load(craft.Folder));
                return ThreeView.RenderPng(result.Design, result.Meshes, 220);
            });
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(cacheFile, png);
            // Older pictures of this craft (earlier saves or OpenPlane versions) are no longer used.
            foreach (var old in Directory.EnumerateFiles(dir, craft.Id + "-*.png"))
                if (!string.Equals(old, cacheFile, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (IOException) { }
            return png;
        }
        finally { _thumbGate.Release(); }
    }

    /// <summary>
    /// Unpacks a zipped craft folder (as shared on Discord) into OpenPlane's "Imported craft" folder.
    /// Returns the craft found (a zip may hold several).
    /// </summary>
    public List<CraftInfo> ImportZip(ZipArchive zip, string fileName)
    {
        var dataEntries = zip.Entries.Where(e => Path.GetFileName(e.FullName).Equals("data.txt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dataEntries.Count == 0) throw new UserFacingException("This zip doesn't contain a Flyout craft (no data.txt inside).");
        const long Limit = 500L * 1024 * 1024;
        long total = zip.Entries.Sum(e => e.Length);
        if (total > Limit) throw new UserFacingException("This zip is too big to be a Flyout craft (over 500 MB unpacked).");
        long written = 0;   // counted as it unpacks: a zip can claim smaller sizes than it holds

        var root = _paths.ImportedCraftDir;
        Directory.CreateDirectory(root);
        var imported = new List<string>();
        string? current = null;
        try
        {
        foreach (var data in dataEntries)
        {
            var prefix = data.FullName.Substring(0, data.FullName.Length - Path.GetFileName(data.FullName).Length);
            var folderName = prefix.TrimEnd('/', '\\');
            folderName = folderName.Length > 0 ? folderName.Split('/', '\\').Last() : Path.GetFileNameWithoutExtension(fileName);
            var dest = UniqueDir(Path.Combine(root, Sanitize(folderName)));
            current = dest;
            Directory.CreateDirectory(dest);
            var destFull = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(e.Name)))
            {
                var rel = e.FullName.Substring(prefix.Length);
                var target = Path.GetFullPath(Path.Combine(dest, rel));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the folder
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = e.Open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += n;
                        if (written > Limit) throw new UserFacingException("This zip is too big to be a Flyout craft (over 500 MB unpacked).");
                        output.Write(buffer, 0, n);
                    }
                }
            }
            imported.Add(dest);
            current = null;
        }
        }
        catch
        {
            // A zip that fails half way leaves nothing behind.
            foreach (var d in imported.Append(current)) if (d != null) try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        var src = new FlyoutFolder(root, "imported", true);
        var result = new List<CraftInfo>();
        foreach (var f in imported)
        {
            var info = Describe(f, src);
            if (info != null) { _byId[info.Id] = info; result.Add(info); }
        }
        return result;
    }

    private static string UniqueDir(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 2; ; i++)
            if (!Directory.Exists(path + " " + i)) return path + " " + i;
    }

    /// <summary>A safe file or folder name for a plane or craft name (see <see cref="OpenPlane.Core.FileNames.Sanitize"/>).</summary>
    public static string Sanitize(string name) => OpenPlane.Core.FileNames.Sanitize(name);
}
