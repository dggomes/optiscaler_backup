using System.Diagnostics;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class SessionLockTests
{
    public static void StaleMarkers()
    {
        Check(Releases.NewerStable("v0.3.0.1", new Version(0, 3, 0)), "Hotfix revision was not offered as newer.");
        Check(!Releases.VersionEquals(new Version(0, 3, 0, 1), new Version(0, 3, 0, 0)), "Health checks ignored hotfix revision.");
        Check(Releases.CompareVersions(new Version(0, 3, 0, 1), new Version(0, 3, 0)) > 0, "Companion comparison ignored revision.");
        using var f = new Fixture(); var home = Path.Combine(f.Root, "stale-home"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "session.lock"); File.WriteAllText(path, "left over after an interrupted session");
        using (var store = new Store(home))
        {
            Check(File.Exists(path), "Live lock marker disappeared.");
            Reject(() => { using var second = new Store(home); });
        }
        Check(!File.Exists(path), "Closed session left its lock marker.");
        File.WriteAllText(path, "copied stale marker"); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        using (var store = new Store(home)) { Check((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0, "Read-only stale marker was not recovered."); }
        Check(!File.Exists(path), "Recovered read-only lock was not cleaned up.");
        File.WriteAllText(Path.Combine(home, "catalog.json"), "{\"SchemaVersion\":99}");
        try { using var unsupported = new Store(home); throw new Exception("Future schema opened."); }
        catch (InvalidOperationException) { }
        Check(!File.Exists(path), "Failed startup retained its lock.");
    }
    public static void LegacyAndLinks()
    {
        using var f = new Fixture(); var home = Path.Combine(f.Root, "legacy-home"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "session.lock");
        using (var legacy = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            legacy.WriteByte(42); legacy.Flush(true);
            Reject(() => { using var second = new Store(home); });
            Check(legacy.Length == 1, "Active legacy marker was truncated.");
            if (OperatingSystem.IsWindows()) Check(SessionDiagnostics.Owners(path).Any(o => o.ProcessId == Environment.ProcessId), "Windows could not identify the active legacy lock owner.");
        }
        using (var recovered = new Store(home)) { }
        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(f.Root, "valuable.txt"); File.WriteAllText(outside, "preserve");
            File.CreateSymbolicLink(path, outside);
            Reject(() => { using var linked = new Store(home); });
            Check(File.ReadAllText(outside) == "preserve", "Linked lock target was changed.");
        }
    }
    public static void CrashRecovery()
    {
        using var f = new Fixture(); var home = Path.Combine(f.Root, "child-home");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SessionLockTests).Assembly.Location);
        start.ArgumentList.Add("--hold-session"); start.ArgumentList.Add(home);
        using var child = Process.Start(start) ?? throw new Exception("Session child did not start.");
        try
        {
            Check(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY", "Session child could not acquire its lock.");
            Reject(() => { using var second = new Store(home); });
            Check(!child.HasExited, "Opening another session terminated a live owner.");
            child.Kill(entireProcessTree: true); Check(child.WaitForExit(5000), "Session child did not exit.");
            using (var reopened = new Store(home)) { reopened.Save(); }
            Check(!File.Exists(Path.Combine(home, "session.lock")), "Restart retained a stale crash marker.");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Active or unsafe session lock was bypassed.");
    }
}
