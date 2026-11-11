namespace OpenPlane.App;

/// <summary>Command-line options. Paths given here override Settings for this session only.</summary>
public sealed class AppOptions
{
    /// <summary>Run the web server without a window (for tests, or to use OpenPlane in a browser).</summary>
    public bool Serve { get; set; }
    /// <summary>Port to listen on (127.0.0.1 only). 0 picks a free port.</summary>
    public int Port { get; set; }
    public string? GameDir { get; set; }
    public string? PlanesDir { get; set; }
    public List<string> FlyoutDirs { get; } = new();
    /// <summary>Where settings, drafts and caches live. Defaults to the user's local app-data folder.</summary>
    public string? DataDir { get; set; }
    /// <summary>Don't start other programs (Steam, the file manager); log what would have happened instead.</summary>
    public bool NoExternal { get; set; }
    /// <summary>Folder holding the mod files to install (default: the "mod" folder next to the app).</summary>
    public string? ModDir { get; set; }
    /// <summary>Where "Save to Downloads" puts files (default: the user's Downloads folder).</summary>
    public string? DownloadsDir { get; set; }
    public bool DevTools { get; set; }
    public bool ShowHelp { get; set; }
    /// <summary>Files to open (import) at start: what Windows passes when a .oplane file is double-clicked.</summary>
    public List<string> OpenFiles { get; } = new();

    public const string Usage = """
        OpenPlane: convert Flyout planes, build your own and share them, for Nuclear Option.

          OpenPlane [options] [files to import...]

          --serve               Run without a window; open the printed address in a browser.
          --port <n>            Port for --serve (127.0.0.1 only). Default: a free port.
          --game-dir <path>     Nuclear Option's folder (overrides Settings for this session).
          --planes-dir <path>   Folder the OpenPlane mod loads planes from.
          --flyout-dir <path>   Folder to search for Flyout craft (repeat for more than one).
          --data-dir <path>     Where OpenPlane keeps its settings and drafts.
          --downloads-dir <path> Where "Save to Downloads" puts shared planes.
          --mod-dir <path>      Folder with the mod files to install (default: "mod" next to the app).
          --no-external         Don't open Steam or the file manager (for testing).
          --help                Show this help.
        """;

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{a} needs a value.");
                return args[++i];
            }
            switch (a)
            {
                case "--serve": o.Serve = true; break;
                case "--port":
                    if (!int.TryParse(Next(), out var p) || p < 0 || p > 65535) throw new ArgumentException("--port needs a number from 0 to 65535.");
                    o.Port = p; break;
                case "--game-dir": o.GameDir = Full(Next()); break;
                case "--planes-dir": o.PlanesDir = Full(Next()); break;
                case "--flyout-dir": o.FlyoutDirs.Add(Full(Next())); break;
                case "--data-dir": o.DataDir = Full(Next()); break;
                case "--mod-dir": o.ModDir = Full(Next()); break;
                case "--downloads-dir": o.DownloadsDir = Full(Next()); break;
                case "--no-external": o.NoExternal = true; break;
                case "--dev-tools": o.DevTools = true; break;
                case "--help": case "-h": case "/?": o.ShowHelp = true; break;
                default:
                    // A file to open: .oplane planes, plane packs and zipped Flyout craft (double-click, "Open with").
                    if (!a.StartsWith("--", StringComparison.Ordinal) && (a.EndsWith(".oplane", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    { o.OpenFiles.Add(Full(a)); break; }
                    throw new ArgumentException($"Unknown option \"{a}\". Use --help to see the options.");
            }
        }
        return o;
    }

    private static string Full(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
}
