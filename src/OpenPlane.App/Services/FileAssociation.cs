using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenPlane.App.Services;

/// <summary>
/// Windows only: lets double-clicking a .oplane file open it in OpenPlane. Everything is written for the current user
/// (HKEY_CURRENT_USER\Software\Classes), so no administrator rights are needed, and it is only done when the player turns
/// it on in Settings. Turning it off removes exactly what was added.
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "OpenPlane.Plane";
    public const string Extension = ".oplane";

    public sealed record State(bool Supported, bool On, bool OtherApp, string? Exe);

    /// <summary>The program double-clicked files should start: this OpenPlane.exe.</summary>
    public static string? CurrentExe()
    {
        var p = Environment.ProcessPath;
        return p != null && Path.GetFileName(p).Equals("OpenPlane.exe", StringComparison.OrdinalIgnoreCase) ? p : null;
    }

    public static State Status() => OperatingSystem.IsWindows() ? StatusIn(UserClasses(), CurrentExe()) : new State(false, false, false, null);

    public static void Register()
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Opening .oplane files by double-click is set up on Windows only.");
        var exe = CurrentExe() ?? throw new UserFacingException("Start OpenPlane from OpenPlane.exe to set this up.");
        using (var classes = UserClasses()) RegisterIn(classes, exe);
        Notify();
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var classes = UserClasses()) UnregisterIn(classes);
        Notify();
    }

    // ---------------------------------------------------------------- the registry work, on any Classes key (tests use a scratch key)

    public static State StatusIn(RegistryKey classes, string? exe)
    {
        if (!OperatingSystem.IsWindows()) return new State(false, false, false, exe);
        using var ext = classes.OpenSubKey(Extension);
        var progId = ext?.GetValue("") as string;
        bool ours = progId == ProgId;
        string? command = null;
        if (ours)
        {
            using var cmd = classes.OpenSubKey(ProgId + @"\shell\open\command");
            command = cmd?.GetValue("") as string;
        }
        bool on = ours && exe != null && command != null && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
        bool other = !string.IsNullOrEmpty(progId) && !ours;
        return new State(true, on, other, exe);
    }

    public static void RegisterIn(RegistryKey classes, string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var ext = classes.CreateSubKey(Extension))
        {
            ext.SetValue("", ProgId);
            ext.SetValue("Content Type", "application/x-openplane");
        }
        using var prog = classes.CreateSubKey(ProgId);
        prog.SetValue("", "OpenPlane plane");
        var icon = Path.Combine(Path.GetDirectoryName(exe) ?? "", "Assets", "openplane.ico");
        using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue("", File.Exists(icon) ? $"\"{icon}\"" : $"\"{exe}\",0");
        using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
    }

    public static void UnregisterIn(RegistryKey classes)
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var ext = classes.OpenSubKey(Extension, writable: true))
        {
            if (ext != null && ext.GetValue("") as string == ProgId)
            {
                ext.DeleteValue("", throwOnMissingValue: false);
                ext.DeleteValue("Content Type", throwOnMissingValue: false);
            }
        }
        using (var ext = classes.OpenSubKey(Extension))
        {
            // Remove the extension key only when nothing else lives in it.
            if (ext != null && ext.ValueCount == 0 && ext.SubKeyCount == 0) { ext.Dispose(); classes.DeleteSubKey(Extension, throwOnMissingSubKey: false); }
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
    }

    private static RegistryKey UserClasses()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);
    }

    /// <summary>Tells Explorer the associations changed, so icons and double-click update without signing out.</summary>
    private static void Notify()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* cosmetic */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
