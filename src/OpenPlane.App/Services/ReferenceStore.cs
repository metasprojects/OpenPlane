using OpenPlane.Core.Builder;

namespace OpenPlane.App.Services;

/// <summary>
/// Blueprint pictures for the builder's reference images, kept by content (SHA-1 + extension) under the app's data
/// folder, so the page refers to them by a short name and drafts keep working across restarts. Saved planes carry their
/// own copies inside the .oplane (references/&lt;name&gt;).
/// </summary>
public sealed class ReferenceStore
{
    public const long MaxBytes = 25L * 1024 * 1024;
    private readonly string _dir;

    public ReferenceStore(string dir) { _dir = dir; }

    /// <summary>Stores a picture (PNG, JPEG or WebP) and returns its name. Throws a player-facing error for anything else.</summary>
    public string Add(byte[] bytes)
    {
        if (bytes.Length == 0) throw new UserFacingException("That picture is empty.");
        if (bytes.Length > MaxBytes) throw new UserFacingException($"That picture is {bytes.Length / 1024 / 1024} MB; reference pictures can be up to 25 MB.");
        var ext = Extension(bytes) ?? throw new UserFacingException("Reference pictures can be PNG, JPEG or WebP files.");
        var name = BuilderJson.Sha1(bytes) + "." + ext;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        return name;
    }

    /// <summary>Stores pictures read from a plane file (names are checked against their content).</summary>
    public void AddAll(IReadOnlyDictionary<string, byte[]> images)
    {
        foreach (var (name, bytes) in images)
        {
            try { if (Add(bytes) != name) continue; } catch (UserFacingException) { }
        }
    }

    /// <summary>The picture's bytes, or null when the name is malformed or unknown.</summary>
    public byte[]? Get(string? name)
    {
        if (!BuilderPackage.IsImageName(name)) return null;
        var path = Path.Combine(_dir, name!);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>The pictures a plane's references use that this store has, by name.</summary>
    public Dictionary<string, byte[]> For(PlaneSpec spec)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var r in spec.References ?? new List<ReferenceImageSpec>())
        {
            if (r == null || result.ContainsKey(r.Image)) continue;
            var bytes = Get(r.Image);
            if (bytes != null) result[r.Image] = bytes;
        }
        return result;
    }

    public static string ContentType(string name) => name.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
        : name.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/jpeg";

    /// <summary>"png", "jpg" or "webp" from the file's first bytes; null for anything else.</summary>
    public static string? Extension(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
        return null;
    }

    /// <summary>Pixel size of a PNG or JPEG (null when it can't be read; the page measures the picture itself then).</summary>
    public static (int width, int height)? Size(byte[] b)
    {
        try
        {
            var ext = Extension(b);
            if (ext == "png" && b.Length >= 24)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (ext == "jpg")
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    int len = (b[i + 2] << 8) | b[i + 3];
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                        return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + len;
                }
            }
        }
        catch (IndexOutOfRangeException) { }
        return null;
    }
}
