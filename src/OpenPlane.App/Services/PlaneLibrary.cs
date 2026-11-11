using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Preview;

namespace OpenPlane.App.Services;

/// <summary>What the plane list shows about a saved plane.</summary>
public sealed class PlaneSummary
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>The plane's own stable id (aircraft.json "id"; the game names the plane by it), if it has one.</summary>
    public string? DesignId { get; set; }
    public DesignOrigin? Origin { get; set; }
    public string Donor { get; set; } = "";
    public double SpanM { get; set; }
    public double LengthM { get; set; }
    public int Engines { get; set; }
    public DateTime Modified { get; set; }
    public bool HasThumbnail { get; set; }
    public bool HasBuilderData { get; set; }
    /// <summary>Set when the file couldn't be read; the message says why.</summary>
    public string? Error { get; set; }
    /// <summary>Set after an import when something needs the player's attention.</summary>
    public string? Warning { get; set; }

    /// <summary>A copy (summaries are cached and shared) carrying a warning.</summary>
    public PlaneSummary WithWarning(string? warning) { var c = (PlaneSummary)MemberwiseClone(); c.Warning = warning; return c; }
}

/// <summary>A plane moved to "Deleted planes": the token that brings it back, and its name.</summary>
public sealed record DeletedPlane(string Token, string Name);

/// <summary>
/// The planes folder (the one the mod loads): list, open, save, duplicate, delete (to OpenPlane's "Deleted planes"
/// folder, so it can be undone), import and export.
/// </summary>
public sealed class PlaneLibrary
{
    private static readonly Regex TokenPattern = new(@"^\d{8}-\d{6}-\d{3}(-\d+)?_[^\\/:*?""<>|\x00-\x1f]{1,200}\.oplane$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, (DateTime stamp, long size, PlaneSummary summary)> _cache = new();
    private readonly object _writeLock = new();

    public PlaneLibrary(AppPaths paths) { _paths = paths; }

    public string Folder => _paths.Planes().Path ?? throw new UserFacingException("OpenPlane doesn't know where to save planes. Choose a planes folder in Settings.");

    // Ids are the file's path inside the planes folder, base64url-encoded.
    public static string IdFor(string relativePath)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/'))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string PathFor(string id)
    {
        string rel;
        try
        {
            var b64 = id.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            rel = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException) { throw new UserFacingException("That plane wasn't found.", 404); }
        var folder = Path.GetFullPath(Folder);
        var full = Path.GetFullPath(Path.Combine(folder, rel));
        if (!full.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !full.EndsWith(OplanePackage.Extension, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("That plane wasn't found.", 404);
        if (!File.Exists(full)) throw new UserFacingException("That plane isn't in the planes folder any more. It may have been moved or deleted.", 404);
        return full;
    }

    private string IdForPath(string full) => IdFor(Path.GetRelativePath(Path.GetFullPath(Folder), full));

    public List<PlaneSummary> List()
    {
        var folder = _paths.Planes().Path;
        if (folder == null || !Directory.Exists(folder)) return new List<PlaneSummary>();
        var result = new List<PlaneSummary>();
        foreach (var file in Directory.EnumerateFiles(folder, "*" + OplanePackage.Extension, SearchOption.AllDirectories))
        {
            try { result.Add(Summary(file)); }
            catch (Exception e) { result.Add(new PlaneSummary { Id = IdForPath(file), FileName = Path.GetFileName(file), Path = file, Name = Path.GetFileNameWithoutExtension(file), Error = "Couldn't read this file: " + e.Message }); }
        }
        // Two files with the same plane id: the game loads only one, so both say so.
        foreach (var same in result.Where(p => p.Error == null && p.DesignId != null).GroupBy(p => OplanePackage.SanitizeId(p.DesignId)).Where(g => g.Key != null && g.Count() > 1))
        {
            var names = same.Select(p => p.FileName).ToList();
            foreach (var p in same.ToList())
            {
                int i = result.IndexOf(p);
                result[i] = p.WithWarning(p.Warning ?? $"Same plane id as {string.Join(", ", names.Where(n => n != p.FileName))}: the game loads only one of them. Delete one, or duplicate it to keep both.");
            }
        }
        return result.OrderByDescending(p => p.Modified).ToList();
    }

    public PlaneSummary Summary(string file)
    {
        var info = new FileInfo(file);
        if (_cache.TryGetValue(file, out var c) && c.stamp == info.LastWriteTimeUtc && c.size == info.Length) return c.summary;
        PlaneSummary s;
        try
        {
            var opened = BuilderPackage.Read(file, includeReferences: false);
            var d = opened.Contents.Design;
            var stats = DesignStats.Compute(d, opened.Contents.Meshes);
            s = new PlaneSummary
            {
                Id = IdForPath(file), FileName = info.Name, Path = file, Name = d.Name, Author = d.Author, DesignId = d.Id, Origin = d.Origin, Donor = d.Donor,
                SpanM = Math.Round(stats.SpanM, 2), LengthM = Math.Round(stats.LengthM, 2), Engines = d.Engines.Count,
                Modified = info.LastWriteTimeUtc, HasThumbnail = opened.Contents.Thumbnail != null, HasBuilderData = opened.FromBuilderData,
            };
        }
        catch (InvalidDataException e)
        {
            s = new PlaneSummary { Id = IdForPath(file), FileName = info.Name, Path = file, Name = Path.GetFileNameWithoutExtension(file), Modified = info.LastWriteTimeUtc, Error = e.Message };
        }
        _cache[file] = (info.LastWriteTimeUtc, info.Length, s);
        return s;
    }


    public BuilderPackage.Opened Open(string id) => BuilderPackage.Read(PathFor(id));

    private readonly ConcurrentDictionary<string, (DateTime When, byte[] Png)> _thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The plane's picture, drawn from its design (so every plane uses the current look, whatever wrote the file), kept
    /// until the file changes. Falls back to the picture stored in the file if drawing fails.
    /// </summary>
    public byte[]? Thumbnail(string id)
    {
        var path = PathFor(id);
        var when = File.GetLastWriteTimeUtc(path);
        if (_thumbs.TryGetValue(path, out var cached) && cached.When == when) return cached.Png;
        var contents = OplanePackage.Read(path);
        byte[]? png;
        try { png = ThreeView.RenderPng(contents.Design, contents.Meshes, 300); }
        catch { png = contents.Thumbnail; }
        if (png != null) _thumbs[path] = (when, png);
        return png;
    }

    /// <summary>Plane ids (aircraft.json "id") already used in the planes folder, except by <paramref name="exceptFile"/>.</summary>
    private HashSet<string> UsedIds(string? exceptFile = null)
        => new(List().Where(p => p.DesignId != null && !string.Equals(p.Path, exceptFile, StringComparison.OrdinalIgnoreCase))
                     .Select(p => OplanePackage.SanitizeId(p.DesignId)!).Where(i => i != null), StringComparer.Ordinal);

    /// <summary>
    /// Saves a plane from the builder. With an id it replaces that file, renaming it when the plane's name changed and the
    /// new name is free (all or nothing: if the old file can't be renamed, nothing changes and the save fails). Without
    /// one it makes a new file with a new plane id. The conversion report is kept.
    /// </summary>
    public PlaneSummary Save(string? id, PlaneSpec spec, List<MeshData> sourceMeshes, IDictionary<string, byte[]>? referenceImages = null)
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            ImportReport? report = null;
            string? existing = null;
            if (id != null)
            {
                existing = PathFor(id);
                AircraftDesign? old = null;
                try { var c = OplanePackage.Read(existing); report = c.Report; old = c.Design; } catch { }
                // The plane keeps its identity across saves.
                if (string.IsNullOrWhiteSpace(spec.Id)) spec.Id = old?.Id ?? OplanePackage.NewId();
            }
            else
            {
                // A new plane (first save or "Save as"): its own id, unless the one it carries is still unused.
                var sid = OplanePackage.SanitizeId(spec.Id);
                if (sid == null || UsedIds().Contains(sid)) spec.Id = OplanePackage.NewId();
            }

            var plane = DesignGenerator.Generate(spec, sourceMeshes);
            var thumb = ThreeView.RenderPng(plane.Design, plane.Meshes, 300);
            if (existing == null)
            {
                var target = UniqueFile(Folder, plane.Design.Name);
                BuilderPackage.Write(target, spec, plane, report, thumb, referenceImages);
                _cache.TryRemove(target, out _);
                return Summary(target);
            }

            var wanted = Path.Combine(Path.GetDirectoryName(existing)!, FlyoutLibrary.Sanitize(plane.Design.Name) + OplanePackage.Extension);
            bool rename = !string.Equals(Path.GetFullPath(wanted), existing, StringComparison.OrdinalIgnoreCase) && !File.Exists(wanted);
            if (!rename)
            {
                BuilderPackage.Write(existing, spec, plane, report, thumb, referenceImages);
                _cache.TryRemove(existing, out _);
                return Summary(existing);
            }
            // Rename first (fails as a whole if the old file is locked), then write the new content in its place. If the
            // write fails, the old file gets its old name back. Either way there is never a second copy.
            try { File.Move(existing, wanted); }
            catch (IOException e) { throw new UserFacingException($"\"{Path.GetFileName(existing)}\" couldn't be renamed ({e.Message}). If Nuclear Option or another program has it open, close it and save again."); }
            try { BuilderPackage.Write(wanted, spec, plane, report, thumb, referenceImages); }
            catch
            {
                try { File.Move(wanted, existing); } catch { }
                throw;
            }
            _cache.TryRemove(existing, out _);
            _cache.TryRemove(wanted, out _);
            return Summary(wanted);
        }
    }

    /// <summary>
    /// Writes a converted plane (no builder data; it opens through SpecFromDesign) under a new, unique file name.
    /// With <paramref name="replacePlaneId"/> (the plane converted from the same craft before) that plane first moves to
    /// "Deleted planes", so replacing can be undone, and the new one keeps its plane id. No other file is ever overwritten.
    /// </summary>
    public (PlaneSummary plane, DeletedPlane? replaced) SaveConverted(AircraftDesign design, List<MeshData> meshes, ImportReport report, string? replacePlaneId)
    {
        var thumb = ThreeView.RenderPng(design, meshes, 300);
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            DeletedPlane? replaced = null;
            string? oldPath = null, oldDeleted = null;
            if (replacePlaneId != null)
            {
                try { oldPath = PathFor(replacePlaneId); } catch (UserFacingException) { oldPath = null; }
                if (oldPath != null)
                {
                    try { design.Id = OplanePackage.Read(oldPath).Design.Id; } catch { }
                    replaced = MoveToDeleted(oldPath, out oldDeleted);
                }
            }
            var sid = OplanePackage.SanitizeId(design.Id);
            if (sid == null || UsedIds().Contains(sid)) design.Id = OplanePackage.NewId();
            var file = UniqueFile(Folder, design.Name);
            try { OplanePackage.Write(file, design, meshes, report, thumb); }
            catch
            {
                if (oldPath != null && oldDeleted != null) try { File.Move(oldDeleted, oldPath); } catch { }
                throw;
            }
            _cache.TryRemove(file, out _);
            return (Summary(file), replaced);
        }
    }

    public PlaneSummary Duplicate(string id)
    {
        var opened = Open(id);
        var spec = BuilderJson.Clone(opened.Spec);
        spec.Name = CopyName(spec.Name);
        spec.DesignHash = null;
        spec.Id = OplanePackage.NewId(); // a copy is a different plane in the game
        var plane = DesignGenerator.Generate(spec, opened.SourceMeshes);
        lock (_writeLock)
        {
            var target = UniqueFile(Folder, spec.Name);
            BuilderPackage.Write(target, spec, plane, opened.Contents.Report, ThreeView.RenderPng(plane.Design, plane.Meshes, 300), opened.ReferenceImages);
            return Summary(target);
        }
    }

    private string CopyName(string name)
    {
        var existing = new HashSet<string>(List().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var basis = name.EndsWith(" copy", StringComparison.OrdinalIgnoreCase) ? name : name + " copy";
        if (!existing.Contains(basis)) return basis;
        for (int i = 2; ; i++) if (!existing.Contains($"{basis} {i}")) return $"{basis} {i}";
    }

    /// <summary>Moves the file to OpenPlane's "Deleted planes" folder; returns a token to undo it.</summary>
    public (string token, string name) Delete(string id)
    {
        lock (_writeLock)
        {
            var d = MoveToDeleted(PathFor(id), out _);
            return (d.Token, d.Name);
        }
    }

    private DeletedPlane MoveToDeleted(string file, out string deletedPath)
    {
        string name;
        try { name = OplanePackage.Read(file).Design.Name; } catch { name = Path.GetFileNameWithoutExtension(file); }
        Directory.CreateDirectory(_paths.DeletedPlanesDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var fileName = Path.GetFileName(file);
        var token = stamp + "_" + fileName;
        for (int i = 2; File.Exists(Path.Combine(_paths.DeletedPlanesDir, token)); i++) token = $"{stamp}-{i}_{fileName}";
        deletedPath = Path.Combine(_paths.DeletedPlanesDir, token);
        File.Move(file, deletedPath);
        _cache.TryRemove(file, out _);
        return new DeletedPlane(token, name);
    }

    /// <summary>The file in "Deleted planes" a restore token names; anything else (paths, drive letters, other folders) is refused.</summary>
    public string DeletedFileFor(string token)
    {
        if (string.IsNullOrEmpty(token) || !TokenPattern.IsMatch(token) || token.Contains("..")) throw new UserFacingException("That deleted plane wasn't found.", 404);
        var dir = Path.GetFullPath(_paths.DeletedPlanesDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, token));
        if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(full) + Path.DirectorySeparatorChar, dir, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("That deleted plane wasn't found.", 404);
        return full;
    }

    public PlaneSummary Restore(string token)
    {
        var src = DeletedFileFor(token);
        if (!File.Exists(src)) throw new UserFacingException("That deleted plane wasn't found. It may have been restored already.", 404);
        int us = token.IndexOf('_');
        var original = token.Substring(us + 1);
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            string? sid = null;
            try { sid = OplanePackage.SanitizeId(OplanePackage.Read(src).Design.Id); } catch { }
            var twin = sid == null ? null : List().FirstOrDefault(p => p.DesignId != null && OplanePackage.SanitizeId(p.DesignId) == sid);
            var target = UniqueFile(Folder, Path.GetFileNameWithoutExtension(original));
            File.Move(src, target);
            _cache.TryRemove(target, out _);
            return Summary(target).WithWarning(twin == null ? null :
                $"\"{twin.Name}\" ({twin.FileName}) is the same plane (it has the same plane id), so the game loads only one of the two. Delete the one you don't want, or duplicate one to keep both.");
        }
    }

    /// <summary>
    /// Adds a .oplane file someone shared. It's checked first; a damaged file is refused. The file keeps its plane id; when
    /// another plane in the folder already has that id, the summary carries a warning (the game would load only one).
    /// </summary>
    public PlaneSummary Import(Stream file, string fileName) => ImportPlane(file, fileName, replaceOlder: false).Plane!;

    /// <summary>
    /// How one imported plane went: added, already there (same plane, same contents), replacing another version (with
    /// <see cref="ReplacedNewer"/> when the one replaced had been saved later than the incoming one), or, in a plane pack,
    /// failed (<see cref="Error"/> says why; <see cref="Plane"/> is null).
    /// </summary>
    public sealed record ImportOutcome(PlaneSummary? Plane, string Outcome, DeletedPlane? Replaced = null, bool ReplacedNewer = false, string? Error = null, string? File = null)
    {
        public const string Added = "added", Same = "same", ReplacedOlder = "replaced", Failed = "error";
    }

    /// <summary>When a plane file was last saved: the newest time on its zip entries (null when it can't be read).</summary>
    private static DateTime? SavedTime(Stream zipStream)
    {
        try
        {
            zipStream.Position = 0;
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            return zip.Entries.Count == 0 ? null : zip.Entries.Max(e => e.LastWriteTime.UtcDateTime);
        }
        catch { return null; }
        finally { zipStream.Position = 0; }
    }

    private static DateTime? SavedTime(string path)
    {
        try { using var fs = System.IO.File.OpenRead(path); return SavedTime(fs); } catch { return null; }
    }

    /// <summary>
    /// Adds a plane file. A plane already in the folder with the same id and the same contents is left as it is (nothing is
    /// copied). With <paramref name="replaceOlder"/> (plane packs from a host), a plane with the same id but other contents
    /// moves to "Deleted planes" and the new one takes its place, so both players load the same version.
    /// </summary>
    public ImportOutcome ImportPlane(Stream file, string fileName, bool replaceOlder)
    {
        BuilderPackage.Opened opened;
        file.Position = 0;
        try { opened = BuilderPackage.Read(file); }
        catch (InvalidDataException e) { throw new UserFacingException($"\"{fileName}\" isn't a plane OpenPlane can read: {e.Message}"); }
        catch (Newtonsoft.Json.JsonException)
        { throw new UserFacingException($"\"{fileName}\" is damaged or isn't an OpenPlane plane file."); }
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            var design = opened.Contents.Design;
            string? warning = null;
            DeletedPlane? replaced = null;
            bool replacedNewer = false;
            var sid = OplanePackage.SanitizeId(design.Id);
            if (sid != null)
            {
                var twin = List().FirstOrDefault(p => p.Error == null && p.DesignId != null && OplanePackage.SanitizeId(p.DesignId) == sid);
                if (twin != null)
                {
                    string? twinHash = null;
                    try { twinHash = OplanePackage.Read(twin.Path, OplanePackage.ReadOptions.DesignOnly).ContentHash; } catch { /* unreadable: treat as different */ }
                    if (twinHash != null && twinHash == opened.Contents.ContentHash)
                        return new ImportOutcome(twin.WithWarning($"You already have this plane (\"{twin.Name}\", {twin.FileName}); nothing changed."), ImportOutcome.Same);
                    if (replaceOlder)
                    {
                        // The pack's version wins (players who fly together need the same planes), but say so when the copy
                        // replaced had been saved later: it stays in Deleted planes.
                        var mine = SavedTime(twin.Path);
                        var theirs = SavedTime(file);
                        replacedNewer = mine != null && theirs != null && mine.Value > theirs.Value.AddMinutes(1);
                        replaced = MoveToDeleted(twin.Path, out _);
                    }
                    else
                        warning = $"\"{twin.Name}\" ({twin.FileName}) is the same plane (it has the same plane id), so the game loads only one of the two. " +
                                  "If this is a newer version, delete the older one; if you want both, duplicate one of them.";
                }
            }
            var target = UniqueFile(Folder, string.IsNullOrWhiteSpace(design.Name) ? Path.GetFileNameWithoutExtension(fileName) : design.Name);
            var temp = target + ".importing";
            try
            {
                file.Position = 0;
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) file.CopyTo(fs);
                File.Move(temp, target);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            return new ImportOutcome(Summary(target).WithWarning(warning), replaced != null ? ImportOutcome.ReplacedOlder : ImportOutcome.Added, replaced, replacedNewer);
        }
    }

    /// <summary>
    /// Adds the three starter planes (trainer, fighter, bomber with fixed ids, see Templates.Starters). A starter that is
    /// already in the folder (by its id) is left as it is.
    /// </summary>
    public List<ImportOutcome> AddStarters()
    {
        var results = new List<ImportOutcome>();
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            for (int i = 0; i < Templates.Starters.Count; i++)
            {
                var id = Templates.Starters[i].Id;
                var have = List().FirstOrDefault(p => p.Error == null && p.DesignId == id);
                if (have != null) { results.Add(new ImportOutcome(have, ImportOutcome.Same)); continue; }
                var spec = Templates.Starter(i);
                var plane = DesignGenerator.Generate(spec, new List<MeshData>());
                var target = UniqueFile(Folder, spec.Name);
                BuilderPackage.Write(target, spec, plane, null, ThreeView.RenderPng(plane.Design, plane.Meshes, 300));
                _cache.TryRemove(target, out _);
                results.Add(new ImportOutcome(Summary(target), ImportOutcome.Added));
            }
        }
        return results;
    }

    /// <summary>File name ending of a plane pack: a zip of .oplane files.</summary>
    public const string PackExtension = ".zip";

    /// <summary>True when a zip holds .oplane files (a plane pack) rather than being a plane itself or a Flyout craft.</summary>
    public static bool IsPack(ZipArchive zip) =>
        zip.GetEntry(OplanePackage.DesignEntry) == null &&
        zip.Entries.Any(e => e.FullName.EndsWith(OplanePackage.Extension, StringComparison.OrdinalIgnoreCase) && e.Length > 0);

    /// <summary>
    /// Saves several planes as one plane pack (a .zip of their .oplane files, reference pictures left out), for sending a
    /// set of planes at once, such as everything a host flies with. Returns the pack's path.
    /// </summary>
    public string ExportPack(IReadOnlyList<string> ids, string folder, string packName)
    {
        if (ids.Count == 0) throw new UserFacingException("Choose the planes to put in the pack.");
        if (string.IsNullOrWhiteSpace(folder)) throw new UserFacingException("Choose a folder to save the pack in.");
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) { throw new UserFacingException($"OpenPlane can't use the folder \"{folder}\": {e.Message}"); }
        var baseName = FlyoutLibrary.Sanitize(string.IsNullOrWhiteSpace(packName) ? "OpenPlane planes" : packName.Trim());
        var target = Path.Combine(folder, baseName + PackExtension);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"{baseName} {i}{PackExtension}");
        var temp = target + ".saving";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids.Distinct(StringComparer.Ordinal))
                {
                    var src = PathFor(id);
                    var name = FlyoutLibrary.Sanitize(Path.GetFileNameWithoutExtension(src));
                    var entryName = name + OplanePackage.Extension;
                    for (int i = 2; !names.Add(entryName); i++) entryName = $"{name} {i}{OplanePackage.Extension}";
                    // Planes are zips already: stored, not compressed again.
                    var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                    entry.LastWriteTime = File.GetLastWriteTime(src);
                    using var es = entry.Open();
                    var bytes = BuilderPackage.WithoutReferences(File.ReadAllBytes(src));
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            File.Move(temp, target);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        return target;
    }

    /// <summary>
    /// Imports every plane in a plane pack. Other versions of the same planes are replaced (see <see cref="ImportPlane"/>).
    /// A plane that can't be imported (damaged, too big, or from a newer OpenPlane) is reported and the rest still come in;
    /// only when none of them can be imported is that an error.
    /// </summary>
    public List<ImportOutcome> ImportPack(ZipArchive zip, string packName, long maxPlaneBytes)
    {
        var results = new List<ImportOutcome>();
        var entries = zip.Entries.Where(e => e.FullName.EndsWith(OplanePackage.Extension, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                                 .OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        if (entries.Count == 0) throw new UserFacingException($"\"{packName}\" has no planes in it.");
        foreach (var e in entries)
        {
            try
            {
                if (e.Length > maxPlaneBytes) throw new UserFacingException($"\"{e.Name}\" is too big to be a plane file.");
                using var ms = new MemoryStream();
                using (var es = e.Open()) es.CopyTo(ms);
                results.Add(ImportPlane(ms, Path.GetFileName(e.FullName), replaceOlder: true));
            }
            catch (UserFacingException ex) { results.Add(new ImportOutcome(null, ImportOutcome.Failed, Error: ex.Message, File: e.Name)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { results.Add(new ImportOutcome(null, ImportOutcome.Failed, Error: $"\"{e.Name}\" couldn't be read: {ex.Message}", File: e.Name)); }
        }
        if (results.All(r => r.Outcome == ImportOutcome.Failed))
            throw new UserFacingException($"No plane in \"{packName}\" could be imported. {results[0].Error}");
        return results;
    }

    /// <summary>
    /// Copies a plane's file into a folder (Downloads or one the player picked); returns the new path. Reference pictures are
    /// left out unless asked for (they are often someone else's drawings).
    /// </summary>
    public string Export(string id, string folder, bool includeReferences = false)
    {
        var src = PathFor(id);
        if (string.IsNullOrWhiteSpace(folder)) throw new UserFacingException("Choose a folder to save the plane in.");
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) { throw new UserFacingException($"OpenPlane can't use the folder \"{folder}\": {e.Message}"); }
        var target = UniqueFile(folder, Path.GetFileNameWithoutExtension(src));
        if (includeReferences) File.Copy(src, target);
        else File.WriteAllBytes(target, BuilderPackage.WithoutReferences(File.ReadAllBytes(src)));
        return target;
    }

    public static string UniqueFile(string folder, string name)
    {
        var baseName = FlyoutLibrary.Sanitize(name);
        var path = Path.Combine(folder, baseName + OplanePackage.Extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName} {i}{OplanePackage.Extension}");
        return path;
    }
}
