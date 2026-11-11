using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenPlane.App.Desktop;

/// <summary>
/// One OpenPlane window per data folder: the open window notes its local address in &lt;data&gt;/running.json, and an
/// OpenPlane started with files (a .oplane double-clicked while the app is open) passes them to that window and quits.
/// </summary>
public static class RunningInstance
{
    private const string FileName = "running.json";

    private sealed record Info(string Url, int Pid);

    public static void Announce(string dataDir, string url)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new Info(url, Environment.ProcessId)));
        }
        catch { /* only a convenience */ }
    }

    public static void Withdraw(string dataDir, string url)
    {
        try
        {
            var file = Path.Combine(dataDir, FileName);
            var info = Read(file);
            if (info != null && info.Url == url && info.Pid == Environment.ProcessId) File.Delete(file);
        }
        catch { /* only a convenience */ }
    }

    /// <summary>True when an open OpenPlane took the files.</summary>
    public static bool TryForward(string dataDir, IReadOnlyList<string> files)
    {
        try
        {
            var info = Read(Path.Combine(dataDir, FileName));
            if (info == null || info.Pid == Environment.ProcessId) return false;
            try { using var p = Process.GetProcessById(info.Pid); if (p.HasExited) return false; }
            catch (ArgumentException) { return false; } // not running any more
            var uri = new Uri(info.Url);
            if (!uri.IsLoopback) return false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "api/open-files"))
            {
                Content = JsonContent.Create(new { paths = files }),
            };
            msg.Headers.Add("X-OpenPlane", "1");
            using var response = http.Send(msg);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static Info? Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Info>(File.ReadAllText(file)) : null; }
        catch { return null; }
    }
}
