using System.Text;

namespace OpenPlane.App.Services;

/// <summary>A node in a Valve KeyValues (VDF/ACF) file: an ordered list of keys whose values are text or nested nodes.</summary>
public sealed class VdfObject
{
    public List<KeyValuePair<string, object>> Items { get; } = new();

    /// <summary>The first value with this key (case-insensitive), or null.</summary>
    public object? this[string key] => Items.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public string? Text(string key) => this[key] as string;
    public VdfObject? Child(string key) => this[key] as VdfObject;
}

/// <summary>
/// A forgiving reader for Steam's KeyValues text files (libraryfolders.vdf, appmanifest_*.acf). It accepts quoted and
/// bare tokens, // comments, [$PLATFORM] conditions (ignored), escapes, and missing closing braces at the end.
/// It never throws on bad input; it returns what it could read.
/// </summary>
public static class Vdf
{
    public static VdfObject Parse(string text)
    {
        var tokens = Tokenize(text).ToList();
        int i = 0;
        return ReadObject(tokens, ref i, topLevel: true);
    }

    public static VdfObject? TryParseFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private enum Kind { Text, Open, Close }

    private static VdfObject ReadObject(List<(Kind kind, string text)> tokens, ref int i, bool topLevel)
    {
        var obj = new VdfObject();
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.kind == Kind.Close) { i++; if (!topLevel) return obj; continue; }
            if (t.kind == Kind.Open) { i++; obj.Items.Add(new("", ReadObject(tokens, ref i, false))); continue; }
            string key = t.text;
            i++;
            if (i >= tokens.Count) break;
            var v = tokens[i];
            if (v.kind == Kind.Open) { i++; obj.Items.Add(new(key, ReadObject(tokens, ref i, false))); }
            else if (v.kind == Kind.Text) { i++; obj.Items.Add(new(key, v.text)); }
            // A key followed by '}' has no value; leave the '}' for the loop.
        }
        return obj;
    }

    private static IEnumerable<(Kind, string)> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '{') { i++; yield return (Kind.Open, "{"); continue; }
            if (c == '}') { i++; yield return (Kind.Close, "}"); continue; }
            if (c == '[')
            {
                // Platform condition such as [$WIN32]: skip it.
                while (i < s.Length && s[i] != ']' && s[i] != '\n') i++;
                if (i < s.Length && s[i] == ']') i++;
                continue;
            }
            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char e = s[i + 1];
                        // Unknown escapes keep their backslash, so a hand-written "D:\Games" path survives.
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == '\\' || e == '"') sb.Append(e);
                        else sb.Append('\\').Append(e);
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++; // closing quote (or end of text)
                yield return (Kind.Text, sb.ToString());
                continue;
            }
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') sb.Append(s[i++]);
            yield return (Kind.Text, sb.ToString());
        }
    }
}
