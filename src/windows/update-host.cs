using System.Diagnostics;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed class UpdateJob
{
    public int SchemaVersion { get; set; } = UpdateHost.JobVersion;
    public string Home { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Download { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string Version { get; set; } = "";
    public string Status { get; set; } = "staged";
    public int ParentId { get; set; }
    public bool Restart { get; set; } = true;
}

public static class UpdateHost
{
    public const int JobVersion = 1;
    public static string Pending(string home) => Path.Combine(home, "updates", "pending.json");

    public static void Stage(Store store, string downloaded, ReleaseInfo release)
    {
        if (File.Exists(store.JournalPath)) throw new IOException("Recover the pending game operation before updating.");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            !IsAppName(Environment.ProcessPath!))
            throw new InvalidOperationException("Self-update requires the published Windows x64 optibackup.exe. Development builds can check releases only.");
        var destination = Environment.ProcessPath!;
        SafePaths.NoLinks(destination);
        using (new FileStream(destination + ".update-access", FileMode.CreateNew, FileAccess.Write)) { }
        File.Delete(destination + ".update-access");
        var path = Pending(store.Home);
        if (File.Exists(path)) throw new IOException("Pending updater recovery must finish first.");
        var helper = Path.Combine(store.Home, "updates", "helper-" + Format.Id() + ".exe");
        File.Copy(destination, helper);
        var job = new UpdateJob
        {
            Home = store.Home,
            Destination = destination,
            Download = downloaded,
            ExpectedHash = SafePaths.Hash(downloaded),
            PreviousHash = SafePaths.Hash(destination),
            Version = release.Tag,
            ParentId = Environment.ProcessId
        };
        Store.Atomic(path, job);
        try { Start(helper, "--update-helper", path); }
        catch { File.Delete(path); throw; }
    }

    public static bool ResumePending(string home)
    {
        var path = Pending(home);
        if (!File.Exists(path)) return false;
        var job = Load(path);
        if (job.Status is "complete" or "rolled-back") { File.Delete(path); return false; }
        if (!IsAppName(Environment.ProcessPath!)) throw new IOException("Run the published Windows executable to finish application update recovery.");
        var helper = Path.Combine(home, "updates", "helper-" + Format.Id() + ".exe");
        File.Copy(Environment.ProcessPath!, helper);
        job.ParentId = Environment.ProcessId;
        Store.Atomic(path, job);
        Start(helper, "--update-helper", path, "--rollback-update");
        return true;
    }

    public static int Health(string path)
    {
        var job = Load(path);
        if (SafePaths.Hash(Environment.ProcessPath!) != job.ExpectedHash) throw new IOException("Updated executable hash does not match.");
        if (!Version.TryParse(job.Version.TrimStart('v', 'V'), out var expected) || !Releases.VersionEquals(expected, Releases.Current))
            throw new IOException("Updated executable version does not match its release tag.");
        // The helper holds session.lock through the probe. Read metadata without
        // opening a second session; this process must never mutate user data.
        var catalog = Path.Combine(job.Home, "catalog.json");
        if (File.Exists(catalog) && !Store.SupportedSchema(Store.Read<Catalog>(catalog).SchemaVersion))
            throw new IOException("Unsupported catalog format in updated application.");
        if (File.Exists(Path.Combine(job.Home, "pending.json"))) throw new IOException("Pending game recovery prevents update health confirmation.");
        Store.Atomic(path + ".healthy", new { Version = Releases.Current.ToString() });
        return 0;
    }

    public static int RunHelper(string path, bool rollback)
    {
        var job = Load(path);
        try
        {
            try
            {
                using var parent = Process.GetProcessById(job.ParentId);
                if (!parent.WaitForExit(60000)) throw new IOException("Updater parent did not exit.");
            }
            catch (ArgumentException) { /* Parent already exited. */ }
            using (var store = new Store(job.Home))
            {
                if (File.Exists(store.JournalPath)) throw new IOException("Game operation is pending; updater stopped.");
                if (!rollback) Replace(path, job);
                else Rollback(job);
                job.Status = rollback ? "rolled-back" : "complete";
                Store.Atomic(path, job);
            }
            if (job.Restart) Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true });
            return 0;
        }
        catch (Exception e)
        {
            try
            {
                // Never replace executables outside the same idle-session guard,
                // including the failure path after the original guard unwinds.
                using var recoverySession = new Store(job.Home);
                job = Load(path);
                if (job.Status == "complete") return 0;
                Rollback(job); job.Status = "rolled-back"; Store.Atomic(path, job);
            }
            catch (Exception r) { File.WriteAllText(path + ".error", $"{DateTimeOffset.UtcNow:o} Update: {e.Message}; rollback: {r.Message}"); return 1; }
            File.WriteAllText(path + ".error", $"{DateTimeOffset.UtcNow:o} Update rolled back: {e.Message}");
            return 1;
        }
    }

    private static void Replace(string path, UpdateJob job)
    {
        if (SafePaths.Hash(job.Download) != job.ExpectedHash || SafePaths.Hash(job.Destination) != job.PreviousHash)
            throw new IOException("Executable changed before update replacement.");
        var previous = job.Destination + ".previous";
        File.Copy(job.Destination, previous, true);
        job.Status = "swapping";
        Store.Atomic(path, job);
        File.Copy(job.Download, job.Destination + ".incoming", true);
        File.Move(job.Destination + ".incoming", job.Destination, true);
        using var health = Start(job.Destination, "--update-health", path);
        if (!health.WaitForExit(20000))
        {
            health.Kill(true);
            health.WaitForExit();
            throw new IOException("Updated application health check timed out.");
        }
        if (health.ExitCode != 0 || !File.Exists(path + ".healthy")) throw new IOException("Updated application failed its health check.");
    }

    private static void Rollback(UpdateJob job)
    {
        if (File.Exists(job.Destination) && SafePaths.Hash(job.Destination) == job.PreviousHash) return;
        var previous = job.Destination + ".previous";
        if (!File.Exists(previous))
        {
            if (File.Exists(job.Destination) && SafePaths.Hash(job.Destination) == job.PreviousHash) return;
            throw new IOException("Previous executable unavailable; manual recovery required.");
        }
        if (SafePaths.Hash(previous) != job.PreviousHash) throw new IOException("Previous executable integrity check failed.");
        File.Copy(previous, job.Destination + ".incoming", true);
        File.Move(job.Destination + ".incoming", job.Destination, true);
    }

    private static UpdateJob Load(string path)
    {
        SafePaths.NoLinks(path);
        var job = Store.Read<UpdateJob>(path);
        if (job.SchemaVersion != JobVersion || !Path.GetFullPath(path).Equals(Pending(Path.GetFullPath(job.Home)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid updater job location/schema.");
        SafePaths.NoLinks(job.Destination); SafePaths.NoLinks(job.Download);
        SafePaths.NoLinks(job.Destination + ".previous"); SafePaths.NoLinks(job.Destination + ".incoming");
        if (!IsAppName(job.Destination) ||
            !SafePaths.Within(Path.Combine(job.Home, "updates"), job.Download)) throw new IOException("Invalid updater destinations.");
        return job;
    }

    private static Process Start(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Could not start updater process.");
    }

    private static bool IsAppName(string path) => new[] { "optibackup.exe", Releases.Asset }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
}
