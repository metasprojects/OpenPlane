using System.Runtime.InteropServices;
using OpenPlane.App.Services;
using Photino.NET;

namespace OpenPlane.App.Desktop;

/// <summary>The OpenPlane window: a Photino (system web view) window showing the local server's page.</summary>
public static class DesktopWindow
{
    private const string WebView2Download = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static int Run(AppOptions options)
    {
        var dataDir = options.DataDir ?? AppPaths.DefaultDataDir();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(dataDir, e.ExceptionObject as Exception);

        // The window is a WebView2 page on Windows: without the runtime it would close without a word.
        if (OperatingSystem.IsWindows() && !WebView2Installed())
        {
            if (Ask("OpenPlane needs the Microsoft Edge WebView2 Runtime to show its window, and it isn't installed on this computer.\n\n" +
                    "Open Microsoft's download page now? Install the \"Evergreen Bootstrapper\", then start OpenPlane again.\n\n" +
                    "(You can also run \"OpenPlane.exe --serve\" from a terminal and open the address it prints in your browser.)"))
                OpenUrl(WebView2Download);
            return 1;
        }

        // A .oplane double-clicked while OpenPlane is already open: hand it to that window instead of opening a second one.
        if (options.OpenFiles.Count > 0 && RunningInstance.TryForward(dataDir, options.OpenFiles)) return 0;

        var dialogs = new PhotinoDialogs();
        AppHost host;
        try { host = AppHost.StartAsync(options, dialogs).GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            WriteCrashLog(dataDir, e);
            ShowError("OpenPlane couldn't start its local server: " + e.Message);
            return 1;
        }
        RunningInstance.Announce(dataDir, host.Url);
        if (options.OpenFiles.Count > 0)
            _ = Task.Run(() => host.Services.Opened.Import(host.Services, options.OpenFiles, Api.Endpoints.ImportLimit));

        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", OperatingSystem.IsWindows() ? "openplane.ico" : "openplane.png");
            var window = new PhotinoWindow()
                .SetTitle("OpenPlane")
                .SetUseOsDefaultSize(false)
                .SetSize(1366, 860)
                .SetMinSize(1024, 640)
                .Center()
                .SetDevToolsEnabled(options.DevTools)
                .SetContextMenuEnabled(options.DevTools)
                .SetJavascriptClipboardAccessEnabled(true)
                .SetTemporaryFilesPath(Path.Combine(host.Services.DataDir, "WebView"));
            if (File.Exists(icon)) window.SetIconFile(icon);
            dialogs.Window = window;

            // Closing the window: first let the page put the builder's unsaved work into its draft. The page answers
            // "openplane:ready-to-close"; if it doesn't within a few seconds (no page, a script error), close anyway.
            bool allowClose = false, asked = false, closed = false;
            void CloseNow()
            {
                if (closed) return;
                allowClose = true;
                try { window.Invoke(() => window.Close()); } catch { /* already closed */ }
            }
            window.RegisterWebMessageReceivedHandler((_, message) => { if (message == "openplane:ready-to-close") CloseNow(); });
            window.RegisterWindowClosingHandler((_, _) =>
            {
                if (allowClose) return false;
                if (asked) return true;
                asked = true;
                try { window.SendWebMessage("openplane:closing"); }
                catch { return false; }
                _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => CloseNow());
                return true; // keep the window open until the page has answered
            });
            window.Load(new Uri(host.Url));
            try { window.WaitForClose(); }
            finally { closed = true; }
            return 0;
        }
        catch (Exception e)
        {
            WriteCrashLog(dataDir, e);
            ShowError($"OpenPlane's window stopped with an error: {e.Message}\n\nDetails are in \"{Path.Combine(dataDir, "crash.log")}\". " +
                      "If this keeps happening, reinstall the Microsoft Edge WebView2 Runtime, or run \"OpenPlane.exe --serve\" and use your browser.");
            return 1;
        }
        finally
        {
            dialogs.Window = null;
            RunningInstance.Withdraw(dataDir, host.Url);
            try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        }
    }

    /// <summary>True when the WebView2 Runtime is installed, machine-wide or for this user (Microsoft's documented check).</summary>
    private static bool WebView2Installed()
    {
        if (!OperatingSystem.IsWindows()) return true;
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, path) in new[]
                 {
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client),
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\" + client),
                     (Microsoft.Win32.Registry.CurrentUser, @"Software\" + client),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
            }
            catch { /* unreadable key: try the next */ }
        }
        // Not found in the registry; some setups (portable or fixed-version runtimes) don't register. Let Photino try.
        return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"));
    }

    private static void WriteCrashLog(string dataDir, Exception? e)
    {
        if (e == null) return;
        try
        {
            Directory.CreateDirectory(dataDir);
            File.AppendAllText(Path.Combine(dataDir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} OpenPlane {OpenPlaneServices.Version}\n{e}\n\n");
        }
        catch { /* nowhere to write */ }
    }

    private static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows()) { try { MessageBoxW(IntPtr.Zero, message, "OpenPlane", 0x10 /* MB_ICONERROR */); return; } catch { } }
        Console.Error.WriteLine(message);
    }

    private static bool Ask(string message)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine(message); return false; }
        try { return MessageBoxW(IntPtr.Zero, message, "OpenPlane", 0x4 /* MB_YESNO */ | 0x30 /* MB_ICONWARNING */) == 6 /* IDYES */; }
        catch { Console.Error.WriteLine(message); return false; }
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private sealed class PhotinoDialogs : INativeDialogs
    {
        public PhotinoWindow? Window { get; set; }
        public bool Available => Window != null;

        public async Task<string?> PickFolder(string title, string? startFolder)
        {
            var w = Window;
            if (w == null) return null;
            var start = startFolder != null && Directory.Exists(startFolder) ? startFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = await w.ShowOpenFolderAsync(title, start, false);
            return result is { Length: > 0 } && !string.IsNullOrWhiteSpace(result[0]) ? result[0] : null;
        }
    }
}
