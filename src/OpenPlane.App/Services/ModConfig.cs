using System.Text;
using System.Text.RegularExpressions;

namespace OpenPlane.App.Services;

/// <summary>
/// Reads and changes one setting in a BepInEx config file (BepInEx/config/&lt;guid&gt;.cfg) without touching the rest:
/// other keys, comments, sections and line endings stay exactly as they were.
/// </summary>
public static class ModConfig
{
    // BepInEx 5 reads a string value as-is when it looks like an unescaped Windows path, otherwise it unescapes it
    // (TomlTypeConverter). Checked against BepInEx 5.4.20's own converter.
    private static readonly Regex RawWindowsPath = new(@"^""?\w:\\(?!\\)(?!.+\\\\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// A string as BepInEx reads it back unchanged: backslashes doubled, quotes escaped. (BepInEx's own writer leaves
    /// backslashes single, which loses the first one of a \\server path; this form reads back right in every case.)
    /// </summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\'': sb.Append(@"\'"); break;
                case '"': sb.Append("\\\""); break;
                case '\t': sb.Append(@"\t"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>What BepInEx makes of a stored string value.</summary>
    public static string Decode(string stored)
    {
        if (RawWindowsPath.IsMatch(stored)) return stored;
        var sb = new StringBuilder(stored.Length);
        for (int i = 0; i < stored.Length; i++)
        {
            char c = stored[i];
            if (c != '\\' || i == stored.Length - 1) { sb.Append(c); continue; }
            char n = stored[++i];
            switch (n)
            {
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The decoded value of <paramref name="key"/> in <paramref name="section"/>, or null when the file or key is missing.</summary>
    public static string? Read(string file, string section, string key)
    {
        if (!File.Exists(file)) return null;
        string? current = null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) { current = line.Substring(1, line.Length - 2).Trim(); continue; }
            if (current != section || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && line.Substring(0, eq).Trim() == key) return Decode(line.Substring(eq + 1).Trim());
        }
        return null;
    }

    /// <summary>
    /// Sets <paramref name="key"/> in <paramref name="section"/> to <paramref name="value"/>, adding the key (with
    /// <paramref name="comment"/>) or the section when missing, and creating the file when there is none.
    /// </summary>
    public static void Set(string file, string section, string key, string value, string? comment = null, string type = "String", string defaultValue = "")
    {
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : text.Length > 0 ? "\n" : Environment.NewLine;
        var lines = text.Length == 0 ? new List<string>() : text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var setting = $"{key} = {Encode(value)}";
        var block = new List<string>();
        if (comment != null) block.Add("## " + comment);
        block.Add("# Setting type: " + type);
        block.Add("# Default value: " + defaultValue);
        block.Add(setting);

        int sectionStart = -1, sectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            bool header = t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal);
            if (!header) continue;
            if (sectionStart >= 0) { sectionEnd = i; break; }
            if (t.Substring(1, t.Length - 2).Trim() == section) sectionStart = i;
        }
        if (sectionStart < 0)
        {
            if (lines.Count == 0)
            {
                lines.Add("## Settings for the OpenPlane mod; BepInEx adds the rest when the game starts.");
                lines.Add("");
            }
            else if (lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add("");
            lines.AddRange(block);
            lines.Add("");
        }
        else
        {
            int found = -1;
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("#", StringComparison.Ordinal)) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim() == key) { found = i; break; }
            }
            if (found >= 0) lines[found] = setting;
            else
            {
                int at = sectionEnd;
                while (at - 1 > sectionStart && lines[at - 1].Trim().Length == 0) at--;
                var insert = new List<string> { "" };
                insert.AddRange(block);
                lines.InsertRange(at, insert);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".openplane-tmp";
        File.WriteAllText(tmp, string.Join(nl, lines) + nl, new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }
}
