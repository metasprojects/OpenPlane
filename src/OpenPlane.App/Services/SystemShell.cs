using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenPlane.App.Services;

/// <summary>
/// Starts other programs: Steam (to play), the file manager (show a file), the browser (links). With --no-external it
/// only records what it would have done, so tests can check it without opening anything.
/// </summary>
public sealed class SystemShell
{
    private readonly bool _dryRun;
    public SystemShell(bool dryRun) { _dryRun = dryRun; }

    /// <summary>What was asked for most recently (newest last), for tests and the log.</summary>
    public ConcurrentQueue<string> History { get; } = new();

    public bool DryRun => _dryRun;

    private void Record(string what)
    {
        History.Enqueue(what);
        while (History.Count > 50) History.TryDequeue(out _);
    }

    /// <summary>Opens a URL (https:// or steam://) with the system's handler.</summary>
    public void OpenUrl(string url)
    {
        Record("open " + url);
        if (_dryRun) return;
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
        else Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
    }

    /// <summary>Starts a program with arguments (each passed as one argument, quoted as needed).</summary>
    public void Start(string program, string[] args)
    {
        Record("start " + program + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
        if (_dryRun) return;
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Opens the file manager with the file selected (Windows), or its folder (elsewhere).</summary>
    public void Reveal(string path)
    {
        Record("reveal " + path);
        if (_dryRun) return;
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        else
        {
            var folder = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
        }
    }

    public static bool IsProcessRunning(string name)
    {
        try
        {
            var procs = Process.GetProcessesByName(name);
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
        catch { return false; }
    }
}
