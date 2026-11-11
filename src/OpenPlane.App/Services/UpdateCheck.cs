using Newtonsoft.Json.Linq;

namespace OpenPlane.App.Services;

/// <summary>
/// Asks GitHub once per start whether a newer OpenPlane release exists (no data is sent beyond the request itself).
/// Off with the "Check for updates" setting or --no-external; any failure just means "no news".
/// </summary>
public sealed class UpdateCheck
{
    public const string Repo = "metasprojects/OpenPlane";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";

    private readonly HttpClient _http;
    private Task<Result>? _running;

    public UpdateCheck(HttpClient http) { _http = http; }

    public sealed record Result(bool Checked, string? Latest, bool Newer, string Url);

    public Task<Result> Get(bool allowed)
    {
        if (!allowed) return Task.FromResult(new Result(false, null, false, ReleasesUrl));
        return _running ??= Run();
    }

    private async Task<Result> Run()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return new Result(true, null, false, ReleasesUrl);
            var doc = JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var tag = (string?)doc["tag_name"];
            var url = (string?)doc["html_url"];
            if (url == null || !url.StartsWith(ReleasesUrl, StringComparison.Ordinal)) url = ReleasesUrl;
            return new Result(true, tag, IsNewer(tag, OpenPlaneServices.Version), url);
        }
        catch { return new Result(false, null, false, ReleasesUrl); }
    }

    /// <summary>True when the release tag (v1.2.3 or 1.2.3) is a higher version than the running one.</summary>
    public static bool IsNewer(string? tag, string current)
    {
        static Version? Parse(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().TrimStart('v', 'V');
            int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) v = v.Substring(0, cut);
            return Version.TryParse(v, out var parsed) ? parsed : null;
        }
        var a = Parse(tag); var b = Parse(current);
        return a != null && b != null && a > b;
    }
}
