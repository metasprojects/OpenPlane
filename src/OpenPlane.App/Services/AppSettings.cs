using OpenPlane.Core.Design;

namespace OpenPlane.App.Services;

/// <summary>What the player chose in Settings. Saved as settings.json in OpenPlane's data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Nuclear Option's folder; null finds it through Steam.</summary>
    public string? GameDir { get; set; }
    /// <summary>Where planes are saved (the folder the mod loads); null uses Nuclear Option's user folder.</summary>
    public string? PlanesDir { get; set; }
    /// <summary>Extra folders to search for Flyout craft, added by the player.</summary>
    public List<string> FlyoutDirs { get; set; } = new();
    /// <summary>Default Flyout folders the player chose to hide.</summary>
    public List<string> HiddenFlyoutDirs { get; set; } = new();
    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>The player's own name, used for "I did" when asked who built a plane.</summary>
    public string? YourName { get; set; }
    /// <summary>The ExtraAircraftFolder value OpenPlane last wrote into the mod's BepInEx config, so it only ever clears its own value.</summary>
    public string? ModConfigFolder { get; set; }
    /// <summary>Where Test fly puts the plane: "air" (flying over an airbase) or "hangar" (the game's own spawn at an airbase).</summary>
    public string TestFlyStart { get; set; } = "air";
    /// <summary>Ask GitHub once per start whether a newer OpenPlane is out.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The player closed the "Getting started" checklist on My planes.</summary>
    public bool GettingStartedHidden { get; set; }
    /// <summary>The player has shared a plane or a pack at least once (a step of the checklist).</summary>
    public bool SharedOnce { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> (atomically; a damaged file falls back to defaults).</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "settings.json");
        _current = Load();
    }

    public string FilePath => _path;

    /// <summary>A copy of the current settings.</summary>
    public AppSettings Get()
    {
        lock (_lock) return OplanePackage.FromJson<AppSettings>(OplanePackage.ToJson(_current));
    }

    public void Save(AppSettings s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, OplanePackage.ToJson(s));
            File.Move(tmp, _path, overwrite: true);
            _current = OplanePackage.FromJson<AppSettings>(OplanePackage.ToJson(s));
        }
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            var s = Get();
            change(s);
            Save(s);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path)) return OplanePackage.FromJson<AppSettings>(File.ReadAllText(_path));
        }
        catch (Exception)
        {
            // A damaged settings file shouldn't stop the app; keep a copy for the curious and start fresh.
            try { File.Copy(_path, _path + ".damaged", overwrite: true); } catch { }
        }
        return new AppSettings();
    }
}
