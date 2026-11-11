using System.IO.Compression;
using OpenPlane.Core.Design;

namespace OpenPlane.App.Services;

/// <summary>
/// Files opened with OpenPlane from outside the page: double-clicking a .oplane file, "Open with", or dropping files on
/// OpenPlane.exe. They are imported straight away; the page collects what happened (GET /api/opened) and tells the player.
/// </summary>
public sealed class OpenedFiles
{
    private readonly object _lock = new();
    private readonly List<object> _pending = new();

    /// <summary>What happened to each opened file since the page last asked, oldest first; the list is then emptied.</summary>
    public List<object> Take()
    {
        lock (_lock)
        {
            var list = _pending.ToList();
            _pending.Clear();
            return list;
        }
    }

    private void Add(object result) { lock (_lock) _pending.Add(result); }

    /// <summary>Imports each file (planes, plane packs, zipped Flyout craft) and queues the outcome for the page.</summary>
    public void Import(OpenPlaneServices s, IEnumerable<string> paths, long maxBytes)
    {
        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw.Trim().Trim('"')); }
            catch { Add(new { file = raw, ok = false, message = $"\"{raw}\" isn't a file name OpenPlane understands." }); continue; }
            var name = Path.GetFileName(path);
            try
            {
                var ext = Path.GetExtension(path);
                if (!ext.Equals(OplanePackage.Extension, StringComparison.OrdinalIgnoreCase) && !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new UserFacingException($"OpenPlane opens .oplane planes, plane packs (.zip) and zipped Flyout craft. \"{name}\" is none of these.");
                if (!File.Exists(path)) throw new UserFacingException($"\"{name}\" wasn't found.");
                var info = new FileInfo(path);
                if (info.Length > maxBytes) throw new UserFacingException($"\"{name}\" is too big to be a plane, a plane pack or a Flyout craft.");
                using var ms = new MemoryStream();
                using (var fs = File.OpenRead(path)) fs.CopyTo(ms);
                ms.Position = 0;
                ZipArchive zip;
                try { zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true); }
                catch (InvalidDataException) { throw new UserFacingException($"\"{name}\" is damaged (it isn't a readable zip)."); }
                using (zip)
                {
                    if (zip.GetEntry(OplanePackage.DesignEntry) == null)
                    {
                        if (PlaneLibrary.IsPack(zip))
                        {
                            var results = s.Planes.ImportPack(zip, name, maxBytes);
                            Add(new { file = name, ok = true, kind = "pack", results = results.Select(r => new { plane = r.Plane, outcome = r.Outcome, replaced = r.Replaced?.Name, replaced_newer = r.ReplacedNewer, error = r.Error, file = r.File }) });
                            continue;
                        }
                        var craft = s.Flyout.ImportZip(zip, name);
                        Add(new { file = name, ok = true, kind = "craft", craft = craft.Select(c => new { id = c.Id, name = c.Name, helicopter = c.Helicopter }) });
                        continue;
                    }
                }
                var one = s.Planes.ImportPlane(ms, name, replaceOlder: false);
                Add(new { file = name, ok = true, kind = "plane", plane = one.Plane, outcome = one.Outcome });
            }
            catch (UserFacingException e) { Add(new { file = name, ok = false, message = e.Message }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            { Add(new { file = name, ok = false, message = $"\"{name}\" couldn't be opened: {e.Message}" }); }
        }
    }
}
