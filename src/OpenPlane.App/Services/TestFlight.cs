using Newtonsoft.Json;

namespace OpenPlane.App.Services;

/// <summary>
/// Test fly: a one-time request file for the mod (BepInEx/OpenPlane-testfly.json: which plane, in the air or from a
/// hangar), then Nuclear Option started straight into a single-player free flight on Heartland, where the mod hands the
/// player that plane. Without Steam's program at hand the game starts normally, and the mod still uses the request in
/// the first single-player mission within 15 minutes.
/// </summary>
public static class TestFlight
{
    public const string Mission = "Free Flight - Heartland";
    public const string RequestFile = "OpenPlane-testfly.json";

    /// <summary>The game's own command-line options for starting straight into a mission (the self-test uses the same).</summary>
    public static readonly string[] FreeFlightArgs = { "-autoHost", "-mission", Mission, "-socket", "Offline", "-state", "SinglePlayer" };

    /// <summary>Writes the request. Null when it's ready; otherwise the reason the game will start normally.</summary>
    public static string? Prepare(OpenPlaneServices s, PlaneSummary plane, string start)
    {
        if (string.IsNullOrEmpty(plane.DesignId)) return "This plane has no plane id yet; save it again to fly it straight away.";
        var game = s.Paths.Game().Path;
        if (!SteamLocator.IsNuclearOptionFolder(game)) return "Nuclear Option wasn't found, so OpenPlane can't set up the test flight.";
        var bep = Path.Combine(game!, "BepInEx");
        if (!Directory.Exists(bep)) return "The OpenPlane mod isn't installed, so the game starts at its menu.";
        var request = new
        {
            design_id = plane.DesignId,
            name = plane.Name,
            start = start == "hangar" ? "runway" : "air",
            requested_at = DateTime.UtcNow,
        };
        var path = Path.Combine(bep, RequestFile);
        var tmp = path + ".openplane-tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(request, Formatting.Indented));
        File.Move(tmp, path, overwrite: true);
        return null;
    }

    /// <summary>Starts the game: into the free flight when asked and Steam's program is found, else at its menu.</summary>
    public static string Launch(OpenPlaneServices s, bool freeFlight)
    {
        if (freeFlight && OperatingSystem.IsWindows())
        {
            var steamExe = s.Steam.SteamRoots().Select(r => Path.Combine(r, "steam.exe")).FirstOrDefault(File.Exists);
            if (steamExe != null)
            {
                s.Shell.Start(steamExe, new[] { "-applaunch", SteamLocator.NuclearOptionAppId }.Concat(FreeFlightArgs).ToArray());
                return "free-flight";
            }
        }
        s.Shell.OpenUrl("steam://rungameid/" + SteamLocator.NuclearOptionAppId);
        return "menu";
    }
}
