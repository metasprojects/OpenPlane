namespace OpenPlane.App.Tests;

/// <summary>Where the repository, private samples and scratch folders are for tests.</summary>
public static class TestPaths
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenPlane.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("OpenPlane.sln not found above the test folder.");
    });

    public static string RepoRoot => Root.Value;

    /// <summary>The private Flyout samples (never committed); null when they aren't on this machine.</summary>
    public static string? CraftRoot
    {
        get
        {
            var p = Path.Combine(RepoRoot, "samples-private", "flyout", "craft");
            return Directory.Exists(p) ? p : null;
        }
    }

    public static string? CraftFolder(string name)
        => CraftRoot == null ? null
           : Directory.GetDirectories(CraftRoot, "*", SearchOption.AllDirectories)
               .FirstOrDefault(d => Path.GetFileName(d) == name && File.Exists(Path.Combine(d, "data.txt")));

    /// <summary>A fresh folder under out/test-runs (gitignored scratch space).</summary>
    public static string NewScratch(string label)
    {
        var p = Path.Combine(RepoRoot, "out", "test-runs", $"{label}-{Guid.NewGuid():N}".Substring(0, label.Length + 13));
        Directory.CreateDirectory(p);
        return p;
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
