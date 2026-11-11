using System.Runtime.InteropServices;
using OpenPlane.App.Desktop;

namespace OpenPlane.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppOptions options;
        try { options = AppOptions.Parse(args); }
        catch (ArgumentException e)
        {
            AttachParentConsole();
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (options.ShowHelp)
        {
            AttachParentConsole();
            Console.WriteLine(AppOptions.Usage);
            return 0;
        }
        return options.Serve ? Serve(options) : DesktopWindow.Run(options);
    }

    /// <summary>--serve: the web server only, until Ctrl+C.</summary>
    private static int Serve(AppOptions options)
    {
        AttachParentConsole();
        AppHost host;
        try { host = AppHost.StartAsync(options, logToConsole: true).GetAwaiter().GetResult(); }
        catch (IOException e)
        {
            Console.Error.WriteLine($"OpenPlane couldn't start its local server: {e.Message}");
            if (options.Port != 0) Console.Error.WriteLine($"Port {options.Port} may be in use. Try another --port, or leave it out to pick a free one.");
            return 1;
        }
        if (options.OpenFiles.Count > 0) host.Services.Opened.Import(host.Services, options.OpenFiles, Api.Endpoints.ImportLimit);
        Console.WriteLine($"OpenPlane {OpenPlaneServices.Version} is running at {host.Url}");
        Console.WriteLine("Open that address in a browser. Press Ctrl+C to stop.");
        host.App.WaitForShutdownAsync().GetAwaiter().GetResult();
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>The app is a windowed program on Windows; show console output when it was started from a terminal.</summary>
    private static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Keep output that is already going somewhere (a pipe or a file); only attach when there is none.
            var h = GetStdHandle(-11);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) return;
            if (AttachConsole(-1))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);
}
