using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;

namespace OpenPlane.App.Services;

/// <summary>One builder working copy, saved every few seconds so nothing is lost if the app closes.</summary>
public sealed class Draft
{
    /// <summary>
    /// Which plane this draft belongs to: the saved plane's id, or "new-..." for a plane that hasn't been saved yet
    /// (one per new-plane session). Each plane keeps its own draft, so opening another plane never overwrites it.
    /// </summary>
    public string Id { get; set; } = "";
    public PlaneSpec Spec { get; set; } = new PlaneSpec();
    /// <summary>The saved plane this draft edits, or null for a new plane.</summary>
    public string? PlaneId { get; set; }
    public string? MeshSet { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>What the draft list shows.</summary>
public sealed class DraftSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PlaneId { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>Unsaved builder work: one draft per plane (or per new plane), in OpenPlane's data folder.</summary>
public sealed class DraftStore
{
    // A saved plane's draft id is its file path in base64 (long or non-Latin names make long ids); files are named by a hash.
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,1024}$", RegexOptions.CultureInvariant);
    private readonly AppPaths _paths;
    private readonly object _lock = new();

    public DraftStore(AppPaths paths) { _paths = paths; }

    /// <summary>True for an id the page may use: letters, digits, '-' and '_' (plane ids and "new-..." ids are like that).</summary>
    public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

    private string FileFor(string id) => Path.Combine(_paths.DraftDir, "d-" + Key(id) + ".json");
    private string MeshesFor(string id) => Path.Combine(_paths.DraftDir, "d-" + Key(id) + ".meshes");

    private static string Key(string id) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(id)), 0, 10).ToLowerInvariant();

    private static string Check(string? id)
        => IsValidId(id) ? id! : throw new UserFacingException("That draft wasn't found.", 404);

    public void Save(Draft draft, List<MeshData>? meshes)
    {
        var id = Check(draft.Id);
        lock (_lock)
        {
            Directory.CreateDirectory(_paths.DraftDir);
            MigrateOldDraft();
            draft.SavedAt = DateTime.UtcNow;
            Write(FileFor(id), OplanePackage.ToJson(draft));
            if (meshes != null && meshes.Count > 0) Write(MeshesFor(id), OplanePackage.ToJson(meshes));
            else if (File.Exists(MeshesFor(id))) File.Delete(MeshesFor(id));
        }
    }

    public (Draft draft, List<MeshData> meshes)? Load(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock)
        {
            MigrateOldDraft();
            try
            {
                var file = FileFor(id);
                if (!File.Exists(file)) return null;
                var d = OplanePackage.FromJson<Draft>(File.ReadAllText(file));
                if (d.Id != id) return null;
                d.Spec = PlaneSpec.Upgrade(d.Spec ?? new PlaneSpec());
                var meshes = File.Exists(MeshesFor(id)) ? OplanePackage.FromJson<List<MeshData>>(File.ReadAllText(MeshesFor(id))) : new List<MeshData>();
                return (d, meshes);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Every unsaved draft, newest first.</summary>
    public List<DraftSummary> List()
    {
        lock (_lock)
        {
            MigrateOldDraft();
            var list = new List<DraftSummary>();
            if (!Directory.Exists(_paths.DraftDir)) return list;
            foreach (var file in Directory.EnumerateFiles(_paths.DraftDir, "d-*.json"))
            {
                try
                {
                    var d = OplanePackage.FromJson<Draft>(File.ReadAllText(file));
                    if (!IsValidId(d.Id) || !string.Equals(FileFor(d.Id), file, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new DraftSummary { Id = d.Id, Name = d.Spec?.Name ?? "Untitled plane", PlaneId = d.PlaneId, SavedAt = d.SavedAt });
                }
                catch (Exception) { /* a damaged draft is skipped */ }
            }
            return list.OrderByDescending(d => d.SavedAt).ToList();
        }
    }

    public void Clear(string id)
    {
        if (!IsValidId(id)) return;
        lock (_lock)
        {
            if (File.Exists(FileFor(id))) File.Delete(FileFor(id));
            if (File.Exists(MeshesFor(id))) File.Delete(MeshesFor(id));
        }
    }

    /// <summary>OpenPlane builds before 0.1 kept a single draft (Draft/draft.json); it becomes one draft in the new layout.</summary>
    private void MigrateOldDraft()
    {
        var old = Path.Combine(_paths.DraftDir, "draft.json");
        if (!File.Exists(old)) return;
        var oldMeshes = Path.Combine(_paths.DraftDir, "meshes.json");
        try
        {
            var d = OplanePackage.FromJson<Draft>(File.ReadAllText(old));
            d.Id = IsValidId(d.PlaneId) ? d.PlaneId! : "new-" + DateTime.UtcNow.Ticks.ToString("x");
            Write(FileFor(d.Id), OplanePackage.ToJson(d));
            if (File.Exists(oldMeshes)) File.Move(oldMeshes, MeshesFor(d.Id), overwrite: true);
            File.Delete(old);
        }
        catch (Exception)
        {
            try { File.Move(old, old + ".damaged", overwrite: true); } catch { }
        }
    }

    private static void Write(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
