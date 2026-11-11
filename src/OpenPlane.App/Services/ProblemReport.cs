using System.Text;
using Newtonsoft.Json.Linq;

namespace OpenPlane.App.Services;

/// <summary>
/// A plain-text report a player can paste into a bug report: versions, what was found, what the mod did with each
/// plane the last time the game started, and the OpenPlane lines of the game's mod log. The player's own folder names
/// are replaced with %USERPROFILE% and similar, so it is safe to post.
/// </summary>
public static class ProblemReport
{
    public static string Build(OpenPlaneServices s)
    {
        var sb = new StringBuilder();
        var mod = s.Installer.Status();
        var game = s.Paths.Game();
        sb.AppendLine($"OpenPlane {OpenPlaneServices.Version} problem report, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Game folder: {game.Path ?? "not found"} ({game.Source}){(mod.GameRunning ? ", game running" : "")}");
        sb.AppendLine($"Mod: {mod.State}. {mod.Summary}");
        sb.AppendLine($"  BepInEx {(mod.BepinexInstalled ? mod.BepinexVersion ?? "installed" : mod.BepinexIncomplete ? "incomplete (missing " + string.Join(", ", mod.BepinexMissing) + ")" : "not installed")}" +
                      $"{(mod.LoaderDisabled ? ", loader switched off" : "")}; Blueprinter {(mod.BlueprinterInstalled ? mod.BlueprinterVersion ?? "installed" : "not installed")}" +
                      $"{(mod.BlueprinterTooOld ? " (too old)" : "")}; OpenPlane mod {(mod.ModInstalled ? mod.ModVersion ?? "installed" : "not installed")}" +
                      $"{(mod.UpdateAvailable ? " (update ready)" : "")}{(mod.NommDetected ? "; NOMM manages the game" : "")}");
        foreach (var w in mod.Warnings) sb.AppendLine("  Warning: " + w);

        var planes = s.Planes.List();
        sb.AppendLine($"Planes folder: {s.Paths.Planes().Path} ({planes.Count} file{(planes.Count == 1 ? "" : "s")}{(planes.Any(p => p.Error != null) ? $", {planes.Count(p => p.Error != null)} unreadable" : "")})");
        foreach (var p in planes.Where(p => p.Error != null)) sb.AppendLine($"  {p.FileName}: {p.Error}");

        var bep = game.Path == null ? null : Path.Combine(game.Path, "BepInEx");
        AppendGameReport(sb, bep);
        AppendLog(sb, bep);
        return Anonymise(sb.ToString());
    }

    private static void AppendGameReport(StringBuilder sb, string? bep)
    {
        var file = bep == null ? null : Path.Combine(bep, "OpenPlane-planes.json");
        if (file == null || !File.Exists(file)) { sb.AppendLine("Game report: none yet (the game hasn't started with the OpenPlane mod)."); return; }
        try
        {
            var doc = JObject.Parse(File.ReadAllText(file));
            sb.AppendLine($"Game report ({File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}): mod {doc["openplane_version"]}, game {doc["game_version"]}, plane set {doc["signature"]}");
            foreach (var p in doc["planes"] as JArray ?? new JArray())
            {
                var line = $"  {p["name"]} ({p["file"]}): " + (p["skipped"] is JValue skip && skip.Type == JTokenType.String ? "NOT LOADED: " + skip : $"on {p["built_on"]}");
                if (p["host_blocked"] is JValue hb && hb.Type == JTokenType.String) line += "; host rules: " + hb;
                sb.AppendLine(line);
                foreach (var w in p["warnings"] as JArray ?? new JArray()) sb.AppendLine("    warning: " + w);
            }
        }
        catch (Exception e) { sb.AppendLine("Game report unreadable: " + e.Message); }
        var tf = Path.Combine(bep!, "OpenPlane-testfly-result.txt");
        if (File.Exists(tf)) { try { sb.AppendLine("Last test flight: " + File.ReadAllText(tf).Trim()); } catch { } }
    }

    private static void AppendLog(StringBuilder sb, string? bep)
    {
        var log = bep == null ? null : Path.Combine(bep, "LogOutput.log");
        if (log == null || !File.Exists(log)) { sb.AppendLine("Mod log: none."); return; }
        try
        {
            // The log is open while the game runs: read it shared.
            string[] lines;
            using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n');
            var keep = lines.Select(l => l.TrimEnd('\r'))
                .Where(l => l.Contains("OpenPlane", StringComparison.OrdinalIgnoreCase) || l.Contains("Blueprinter", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("Exception", StringComparison.Ordinal) || l.StartsWith("[Error", StringComparison.Ordinal))
                .Select(l => l.Length > 400 ? l.Substring(0, 400) + "…" : l).ToList();
            sb.AppendLine($"Mod log ({File.GetLastWriteTime(log):yyyy-MM-dd HH:mm}), OpenPlane, Blueprinter and error lines (last 60 of {keep.Count}):");
            foreach (var l in keep.Skip(Math.Max(0, keep.Count - 60))) sb.AppendLine("  " + l);
        }
        catch (Exception e) { sb.AppendLine("Mod log unreadable: " + e.Message); }
    }

    /// <summary>Replaces the player's own folders (user profile, app data) with their %NAME% forms.</summary>
    public static string Anonymise(string text)
    {
        var pairs = new List<(string path, string name)>();
        void Add(Environment.SpecialFolder f, string name)
        {
            try { var p = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(p) && p.Length > 3) pairs.Add((p, name)); } catch { }
        }
        Add(Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%");
        Add(Environment.SpecialFolder.ApplicationData, "%APPDATA%");
        Add(Environment.SpecialFolder.UserProfile, "%USERPROFILE%");
        var user = Environment.UserName;
        // Whole folder names only ("...\AppData\LocalLow" is not "%LOCALAPPDATA%Low"): the match must not run on into a
        // longer name (a letter, digit, "_" or "-" after it); a separator, space, quote or punctuation ends it. The user
        // name goes wherever it stands as a word of its own.
        foreach (var (path, name) in pairs.OrderByDescending(p => p.path.Length))
            foreach (var form in new[] { path, path.Replace('\\', '/') })
                text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(form) + @"(?![\w-])", name.Replace("$", "$$"),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!string.IsNullOrEmpty(user) && user.Length >= 3)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(user) + @"(?![\w-])", "<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }
}
