using OpenPlane.App.Services;
using Xunit;

namespace OpenPlane.App.Tests;

public class FileNameAndConfigTests
{
    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("nul.backup", "nul_.backup")]
    [InlineData("com1", "com1_")]
    [InlineData("LPT9", "LPT9_")]
    [InlineData("Plane. . ", "Plane")]
    [InlineData("  ", "Plane")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("Console", "Console")]
    [InlineData("Spitfire Mk.IX", "Spitfire Mk.IX")]
    public void FileNamesAreSafeOnWindows(string name, string expected) => Assert.Equal(expected, FlyoutLibrary.Sanitize(name));

    [Theory]
    [InlineData(@"G:\My Planes\Open'Plane")]
    [InlineData(@"\\server\share\planes")]
    [InlineData(@"Z:\home\deck\My Planes")]
    [InlineData(@"D:\a""b")]
    public void ConfigValuesReadBackAsBepInExReadsThem(string path) => Assert.Equal(path, ModConfig.Decode(ModConfig.Encode(path)));

    [Fact]
    public void ConfigKeysAreAddedWithoutTouchingTheRest()
    {
        var dir = TestPaths.NewScratch("cfg");
        try
        {
            var file = Path.Combine(dir, "a.cfg");
            ModConfig.Set(file, "Paths", "ExtraAircraftFolder", @"C:\Planes");
            Assert.Equal(@"C:\Planes", ModConfig.Read(file, "Paths", "ExtraAircraftFolder"));
            File.WriteAllText(file, "[Other]\nKey = 1\n");
            ModConfig.Set(file, "Paths", "ExtraAircraftFolder", @"C:\Planes 2");
            ModConfig.Set(file, "Paths", "ExtraAircraftFolder", @"C:\Planes 3");
            var text = File.ReadAllText(file);
            Assert.StartsWith("[Other]\nKey = 1\n", text);
            Assert.DoesNotContain("\r\n", text);
            Assert.Equal(@"C:\Planes 3", ModConfig.Read(file, "Paths", "ExtraAircraftFolder"));
            Assert.Equal("1", ModConfig.Read(file, "Other", "Key"));
        }
        finally { TestPaths.TryDelete(dir); }
    }

    [Theory]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.0.9", "0.1.0", false)]
    [InlineData("v1.0.0-beta", "0.9.0", true)]
    [InlineData("nightly", "0.1.0", false)]
    [InlineData(null, "0.1.0", false)]
    public void UpdateCheckComparesVersions(string? tag, string current, bool newer) => Assert.Equal(newer, UpdateCheck.IsNewer(tag, current));

    [Fact]
    public void FilesOnTheCommandLineAreOpened()
    {
        var o = AppOptions.Parse(new[] { "--no-external", @"C:\Downloads\Kite.oplane", "Host planes.zip" });
        Assert.Equal(2, o.OpenFiles.Count);
        Assert.EndsWith("Kite.oplane", o.OpenFiles[0]);
        Assert.True(Path.IsPathRooted(o.OpenFiles[1]));
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(new[] { "notes.txt" }));
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(new[] { "--planes.oplane" }));
    }

    /// <summary>The double-click setup, on a throwaway key under HKCU\Software (never the real Classes key).</summary>
    [Fact]
    public void DoubleClickSetupAddsAndRemovesOnlyItsOwnKeys()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = @"Software\OpenPlaneTests-" + Guid.NewGuid().ToString("N");
        try
        {
            using var classes = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\Classes", writable: true);
            const string exe = @"C:\Games\OpenPlane\OpenPlane.exe";
            Assert.False(FileAssociation.StatusIn(classes, exe).On);
            FileAssociation.RegisterIn(classes, exe);
            var on = FileAssociation.StatusIn(classes, exe);
            Assert.True(on.On);
            Assert.False(on.OtherApp);
            using (var cmd = classes.OpenSubKey(FileAssociation.ProgId + @"\shell\open\command"))
                Assert.Equal($"\"{exe}\" \"%1\"", cmd!.GetValue(""));
            Assert.False(FileAssociation.StatusIn(classes, @"D:\Other\OpenPlane.exe").On);   // another copy of the app isn't "on"

            FileAssociation.UnregisterIn(classes);
            Assert.False(FileAssociation.StatusIn(classes, exe).On);
            Assert.Null(classes.OpenSubKey(FileAssociation.ProgId));
            Assert.Null(classes.OpenSubKey(FileAssociation.Extension));

            // Another program's association is reported and left alone by "off".
            using (var ext = classes.CreateSubKey(FileAssociation.Extension)) ext.SetValue("", "SomeEditor.File");
            Assert.True(FileAssociation.StatusIn(classes, exe).OtherApp);
            FileAssociation.UnregisterIn(classes);
            using (var ext = classes.OpenSubKey(FileAssociation.Extension)) Assert.Equal("SomeEditor.File", ext!.GetValue(""));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }
}
