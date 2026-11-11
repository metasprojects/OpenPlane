using OpenPlane.App.Services;
using Xunit;

namespace OpenPlane.App.Tests;

public class VdfTests
{
    [Fact]
    public void ReadsNewLibraryFoldersFormat()
    {
        const string text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"420159095"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            		"apps" { "2168680" "1234" }
            	}
            }
            """;
        var paths = SteamLocator.LibraryPaths(Vdf.Parse(text));
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, paths);
    }

    [Fact]
    public void ReadsOldLibraryFoldersFormatAndLinuxPaths()
    {
        const string text = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "1700000000"
                "ContentStatsID"       "-123"
                "1"  "/home/deck/.local/share/Steam"
                "2"  "/run/media/mmcblk0p1"
            }
            """;
        Assert.Equal(new[] { "/home/deck/.local/share/Steam", "/run/media/mmcblk0p1" }, SteamLocator.LibraryPaths(Vdf.Parse(text)));
    }

    [Fact]
    public void ToleratesCommentsConditionsEscapesAndMissingBraces()
    {
        const string text = """
            // written by hand
            "AppState"
            {
                "appid"   "2168680"   [$WIN32]
                "name"    "Nuclear \"Option\""
                "installdir" Nuclear_Option
                "path"  "D:\Games\Steam"
                "UserConfig"
                {
                    "language" "english"
            """;
        var doc = Vdf.Parse(text);
        var state = doc.Child("appstate")!;
        Assert.Equal("2168680", state.Text("appid"));
        Assert.Equal("Nuclear \"Option\"", state.Text("name"));
        Assert.Equal("Nuclear_Option", state.Text("installdir"));
        Assert.Equal(@"D:\Games\Steam", state.Text("path"));
        Assert.Equal("english", state.Child("UserConfig")!.Text("language"));
    }

    [Fact]
    public void GarbageNeverThrows()
    {
        foreach (var s in new[] { "", "}}}{{{", "\"unterminated", "{ \"a\" }", "key", "\"a\" \"b\" \"c\"" })
            Vdf.Parse(s);
        Assert.Empty(SteamLocator.LibraryPaths(Vdf.Parse("}{")));
    }
}

public class PathDetectionTests : IDisposable
{
    private readonly string _dir = TestPaths.NewScratch("steam");
    public void Dispose() => TestPaths.TryDelete(_dir);

    /// <summary>A fake Steam: the install folder lists a second library that holds Nuclear Option.</summary>
    private (string steam, string library, string game) FakeSteam()
    {
        var steam = Path.Combine(_dir, "Steam");
        var library = Path.Combine(_dir, "Library Two");
        var game = Path.Combine(library, "steamapps", "common", "Nuclear Option");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(game, "NuclearOption_Data", "Managed"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n \"0\" {{ \"path\" \"{steam.Replace("\\", "\\\\")}\" }}\n \"1\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }}\n}}\n");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_2168680.acf"), "\"AppState\"\n{\n \"appid\" \"2168680\"\n \"installdir\" \"Nuclear Option\"\n}\n");
        return (steam, library, game);
    }

    [Fact]
    public void FindsTheGameInASecondLibrary()
    {
        var (steam, library, game) = FakeSteam();
        var locator = new SteamLocator(() => new[] { steam, Path.Combine(_dir, "does-not-exist") });
        Assert.Equal(new[] { steam }, locator.SteamRoots());
        Assert.Contains(Path.GetFullPath(library), locator.Libraries());
        var app = locator.FindApp(SteamLocator.NuclearOptionAppId);
        Assert.NotNull(app);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(app!.InstallPath));
        Assert.True(SteamLocator.IsNuclearOptionFolder(app.InstallPath));
        Assert.Null(locator.FindApp(SteamLocator.FlyoutAppId));
    }

    [Fact]
    public void CommandLineBeatsSettingsBeatsDetection()
    {
        var (steam, _, game) = FakeSteam();
        var data = Path.Combine(_dir, "data");
        var settings = new SettingsStore(data);
        var locator = new SteamLocator(() => new[] { steam });

        var detected = new AppPaths(new AppOptions(), settings, locator, data);
        Assert.Equal("detected", detected.Game().Source);
        Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(detected.Game().Path!));

        var chosen = Path.Combine(_dir, "My NO");
        Directory.CreateDirectory(chosen);
        settings.Update(s => s.GameDir = chosen);
        var fromSettings = new AppPaths(new AppOptions(), settings, locator, data);
        Assert.Equal("settings", fromSettings.Game().Source);
        Assert.Equal(chosen, fromSettings.Game().Path);

        var opts = AppOptions.Parse(new[] { "--game-dir", game, "--planes-dir", Path.Combine(_dir, "planes"), "--flyout-dir", _dir });
        var fromFlags = new AppPaths(opts, settings, locator, data);
        Assert.Equal("command line", fromFlags.Game().Source);
        Assert.Equal("command line", fromFlags.Planes().Source);
        Assert.Contains(fromFlags.FlyoutFolders(), f => f.Source == "command line" && f.Path == Path.GetFullPath(_dir));
        Assert.Contains(fromFlags.FlyoutFolders(), f => f.Source == "imported");

        // Settings survive a restart.
        Assert.Equal(chosen, new SettingsStore(data).Get().GameDir);
    }

    [Fact]
    public void DefaultPlanesFolderIsNuclearOptionsUserFolder()
    {
        var data = Path.Combine(_dir, "data");
        var paths = new AppPaths(new AppOptions(), new SettingsStore(data), new SteamLocator(() => Array.Empty<string>()), data);
        var p = paths.DefaultPlanesDir();
        Assert.EndsWith(Path.Combine("Shockfront", "NuclearOption", "OpenPlane", "Aircraft"), p);
        if (OperatingSystem.IsWindows()) Assert.Contains(Path.Combine("AppData", "LocalLow"), p);
        else Assert.Contains(Path.Combine("compatdata", "2168680", "pfx", "drive_c", "users", "steamuser"), p);
    }

    [Fact]
    public void DamagedSettingsFallBackToDefaults()
    {
        var data = Path.Combine(_dir, "data2");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{ this is not json");
        var s = new SettingsStore(data).Get();
        Assert.Equal("dark", s.Theme);
        Assert.True(File.Exists(Path.Combine(data, "settings.json.damaged")));
    }

    [Fact]
    public void OptionsParseAndRejectNonsense()
    {
        var o = AppOptions.Parse(new[] { "--serve", "--port", "5173", "--no-external" });
        Assert.True(o.Serve); Assert.Equal(5173, o.Port); Assert.True(o.NoExternal);
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(new[] { "--port", "lots" }));
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(new[] { "--frobnicate" }));
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(new[] { "--game-dir" }));
    }

    [Fact]
    public void NommIsDetectedFromItsMarkers()
    {
        var game = Path.Combine(_dir, "NO");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins", "SomeMod"));
        Assert.False(ModInstaller.NommManages(game));
        File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "SomeMod", "meta.json"), "{\"id\":\"SomeMod\",\"artifact\":{\"version\":\"1.0\"}}");
        Assert.True(ModInstaller.NommManages(game));
        var game2 = Path.Combine(_dir, "NO2");
        Directory.CreateDirectory(Path.Combine(game2, "BepInEx", "disabledPlugins"));
        Assert.True(ModInstaller.NommManages(game2));
    }

    [Fact]
    public void ZipExtractionNeverWritesOutsideTheGameFolder()
    {
        var zipPath = Path.Combine(_dir, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("BepInEx/core/BepInEx.dll").Open())) w.Write("ok");
            using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("bad");
        }
        var target = Path.Combine(_dir, "game");
        Directory.CreateDirectory(target);
        ModInstaller.ExtractInto(zipPath, target);
        Assert.True(File.Exists(Path.Combine(target, "BepInEx", "core", "BepInEx.dll")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }
}
