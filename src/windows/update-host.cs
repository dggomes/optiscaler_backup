using System.Diagnostics;
using System.Reflection.PortableExecutable;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed class UpdateFile
{
    public string Destination { get; set; } = "";
    public string Download { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public string PreviousHash { get; set; } = "";
}
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
    public List<UpdateFile> Companions { get; set; } = [];
}
public sealed record UpdateTarget(string Destination, string Asset);
public sealed class UpdateCheckResult
{
    public int Version { get; set; } = 1;
    public ReleaseInfo? Release { get; set; }
}

public static class UpdateHost
{
    public const int JobVersion = 1; // Existing CLI helpers/jobs remain compatible.
    public const int BundleJobVersion = 2;
    public static string Pending(string home) => Path.Combine(home, "updates", "pending.json");
    public static bool CanSelfUpdate => OperatingSystem.IsWindows() && Environment.Is64BitProcess && IsAppName(Environment.ProcessPath!);
    public static string AssetForPath(string path) => Path.GetFileName(path).Equals("optibackup-ui.exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals(Releases.UiAsset, StringComparison.OrdinalIgnoreCase) ? Releases.UiAsset : Releases.Asset;

    public static string? LastFailure(string home)
    {
        var archive = Path.Combine(home, "updates", "last-update.json"); SafePaths.NoLinks(archive);
        if (!File.Exists(archive)) return null;
        var job = Store.Read<UpdateJob>(archive);
        if (job.SchemaVersion is not (JobVersion or BundleJobVersion)) throw new IOException("Unsupported archived update format.");
        if (job.Status != "rolled-back") return null;
        var error = Pending(home) + ".error"; SafePaths.NoLinks(error);
        var detail = File.Exists(error) && new FileInfo(error).Length < 16384 ? File.ReadAllText(error) : "An interrupted application update was recovered.";
        return "The previous application update was rolled back. Previous executables were restored. " + detail;
    }

    public static ReleaseInfo? LastRelease(string home)
    {
        var path = Path.Combine(home, "updates", "last-check.json"); SafePaths.NoLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Update-check metadata exceeds its size limit.");
        var result = Store.Read<UpdateCheckResult>(path);
        if (result.Version != 1) throw new IOException("Unsupported update-check format; metadata was not rewritten.");
        if (result.Release != null && string.IsNullOrWhiteSpace(result.Release.Tag)) throw new IOException("Invalid cached release version.");
        return result.Release;
    }
    public static void RememberRelease(string home, ReleaseInfo? release)
    {
        LastRelease(home);
        Store.Atomic(Path.Combine(home, "updates", "last-check.json"), new UpdateCheckResult { Release = release });
    }

    public static List<UpdateTarget> Targets()
    {
        if (!CanSelfUpdate) throw new InvalidOperationException("Self-update requires a published Windows x64 CLI or desktop executable. Development builds can check releases only.");
        var current = Path.GetFullPath(Environment.ProcessPath!);
        var folder = Path.GetDirectoryName(current)!;
        return new[] { current }.Concat(Directory.EnumerateFiles(folder, "*.exe").Where(IsAppName))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new UpdateTarget(path, AssetForPath(path))).ToList();
    }
    public static bool NeedsUpdate(ReleaseInfo release)
    {
        if (string.IsNullOrWhiteSpace(release.Tag)) throw new IOException("Invalid release version.");
        if (Releases.NewerStable(release.Tag, Releases.Current)) return true;
        if (!Version.TryParse(release.Tag.TrimStart('v', 'V'), out var wanted) || !Releases.VersionEquals(wanted, Releases.Current) || !CanSelfUpdate) return false;
        return Targets().Any(target => !Version.TryParse(FileVersionInfo.GetVersionInfo(target.Destination).FileVersion, out var version)
            || Releases.CompareVersions(version, Releases.Current) < 0);
    }
    public static async Task Prepare(Store store, Releases releases, ReleaseInfo release, CancellationToken cancellation = default)
    {
        EnsureIdle(store); var targets = Targets(); EnsureStopped(targets.Select(t => t.Destination));
        if (!Version.TryParse(release.Tag.TrimStart('v', 'V'), out var desired)) throw new IOException("Invalid release version.");
        foreach (var target in targets)
            if (Version.TryParse(FileVersionInfo.GetVersionInfo(target.Destination).FileVersion, out var installed)
                && Releases.CompareVersions(installed, desired) > 0)
                throw new IOException("An installed companion is newer than this release; it will not be downgraded: " + target.Destination);
        var directory = Path.Combine(store.Home, "updates", Format.Id());
        var downloads = new Dictionary<string, VerifiedDownload>();
        foreach (var target in targets)
            if (!downloads.ContainsKey(target.Asset)) downloads[target.Asset] = await releases.DownloadVerified(Releases.ForAsset(release, target.Asset), directory, cancellation);
        var files = targets.Select(target => new UpdateFile
        {
            Destination = target.Destination,
            Download = downloads[target.Asset].Path,
            ExpectedHash = downloads[target.Asset].Hash,
            PreviousHash = SafePaths.Hash(target.Destination)
        }).ToList();
        StageFiles(store, files, release);
    }
    public static void Stage(Store store, string downloaded, ReleaseInfo release)
    {
        EnsureIdle(store);
        if (!CanSelfUpdate) throw new InvalidOperationException("Self-update requires a published Windows x64 application.");
        StageFiles(store, [new() { Destination = Environment.ProcessPath!, Download = downloaded, ExpectedHash = SafePaths.Hash(downloaded), PreviousHash = SafePaths.Hash(Environment.ProcessPath!) }], release);
    }
    private static void StageFiles(Store store, List<UpdateFile> files, ReleaseInfo release)
    {
        EnsureIdle(store); EnsureStopped(files.Select(file => file.Destination));
        if (File.Exists(Pending(store.Home))) throw new IOException("Pending updater recovery must finish first.");
        foreach (var file in files)
        {
            ValidateFile(store.Home, files[0].Destination, file);
            if (SafePaths.Hash(file.Download) != file.ExpectedHash) throw new IOException("Downloaded executable changed after checksum verification.");
            var access = file.Destination + ".update-access";
            using (new FileStream(access, FileMode.CreateNew, FileAccess.Write)) { }
            File.Delete(access);
        }
        var job = new UpdateJob
        {
            SchemaVersion = files.Count > 1 ? BundleJobVersion : JobVersion,
            Home = store.Home,
            Destination = files[0].Destination,
            Download = files[0].Download,
            ExpectedHash = files[0].ExpectedHash,
            PreviousHash = files[0].PreviousHash,
            Companions = files.Skip(1).ToList(),
            Version = release.Tag,
            ParentId = Environment.ProcessId
        };
        var helper = Path.Combine(store.Home, "updates", "helper-" + Format.Id() + ".exe");
        SafePaths.NoLinks(helper); File.Copy(Environment.ProcessPath!, helper);
        Store.Atomic(Pending(store.Home), job);
        try { Start(helper, "--update-helper", Pending(store.Home)); }
        catch { File.Delete(Pending(store.Home)); throw; }
    }
    public static bool ResumePending(string home)
    {
        var path = Pending(home);
        if (!File.Exists(path)) return false;
        var job = Load(path);
        if (job.Status is "complete" or "rolled-back") { File.Delete(path); return false; }
        if (!CanSelfUpdate) throw new IOException("Run a published Windows executable to finish application update recovery.");
        var helper = Path.Combine(home, "updates", "helper-" + Format.Id() + ".exe");
        SafePaths.NoLinks(helper); File.Copy(Environment.ProcessPath!, helper);
        job.ParentId = Environment.ProcessId; Store.Atomic(path, job);
        Start(helper, "--update-helper", path, "--rollback-update"); return true;
    }
    public static int Health(string path, string component = Releases.Asset, Action? startupProbe = null)
    {
        var job = Load(path);
        var file = Files(job).SingleOrDefault(file => file.Destination.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Health probe is not an updater target.");
        if (AssetForPath(file.Destination) != component || SafePaths.Hash(Environment.ProcessPath!) != file.ExpectedHash)
            throw new IOException("Updated executable/component hash does not match.");
        if (!Version.TryParse(job.Version.TrimStart('v', 'V'), out var expected) || !Releases.VersionEquals(expected, Releases.Current))
            throw new IOException("Updated executable version does not match its release tag.");
        var catalog = Path.Combine(job.Home, "catalog.json");
        if (File.Exists(catalog) && !Store.SupportedSchema(Store.Read<Catalog>(catalog).SchemaVersion))
            throw new IOException("Unsupported catalog format in updated application.");
        if (File.Exists(Path.Combine(job.Home, "pending.json")) || File.Exists(Path.Combine(job.Home, "rename-pending.json")))
            throw new IOException("Pending game recovery prevents update health confirmation.");
        startupProbe?.Invoke();
        Store.Atomic(HealthPath(path, job, file), new { Version = Releases.Current.ToString(), Component = component }); return 0;
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
                EnsureIdle(store); EnsureStopped(Files(job).Select(file => file.Destination));
                if (!rollback) Replace(path, job); else Rollback(job);
                job.Status = rollback ? "rolled-back" : "complete"; Store.Atomic(path, job);
            }
            Restart(path, job); return 0;
        }
        catch (Exception error)
        {
            try
            {
                using var recoverySession = new Store(job.Home);
                job = Load(path);
                if (job.Status == "complete") return 0;
                EnsureStopped(Files(job).Select(file => file.Destination));
                Rollback(job); job.Status = "rolled-back"; Store.Atomic(path, job);
            }
            catch (Exception recoveryError)
            { File.WriteAllText(path + ".error", $"{DateTimeOffset.UtcNow:o} Update: {error.Message}; rollback: {recoveryError.Message}"); return 1; }
            File.WriteAllText(path + ".error", $"{DateTimeOffset.UtcNow:o} Update rolled back: {error.Message}");
            Restart(path, job); return 1;
        }
    }
    private static void Replace(string path, UpdateJob job)
    {
        var files = Files(job);
        foreach (var file in files)
        {
            if (SafePaths.Hash(file.Download) != file.ExpectedHash || SafePaths.Hash(file.Destination) != file.PreviousHash)
                throw new IOException("An executable changed before update replacement.");
            using var input = File.OpenRead(file.Download);
            using var image = new PEReader(input);
            if (image.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus || image.PEHeaders.CoffHeader.Machine != Machine.Amd64
                || (image.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
                || (image.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                throw new IOException("Update is not a Windows x64 executable: " + Path.GetFileName(file.Destination));
        }
        SafePaths.Space(job.Destination, files.Sum(file => new FileInfo(file.Download).Length + new FileInfo(file.Destination).Length));
        foreach (var file in files) File.Copy(file.Destination, file.Destination + ".previous", true);
        job.Status = "swapping"; Store.Atomic(path, job);
        foreach (var file in files)
        {
            File.Copy(file.Download, file.Destination + ".incoming", true);
            MoveIncoming(file);
        }
        foreach (var file in files)
        {
            var marker = HealthPath(path, job, file); if (File.Exists(marker)) File.Delete(marker);
            using var health = Start(file.Destination, "--update-health", path);
            if (!health.WaitForExit(30000)) { health.Kill(true); health.WaitForExit(); throw new IOException("Updated application health check timed out."); }
            if (health.ExitCode != 0 || !File.Exists(marker)) throw new IOException("Updated application failed its health check: " + Path.GetFileName(file.Destination));
        }
    }
    private static void Rollback(UpdateJob job)
    {
        foreach (var file in Files(job))
        {
            if (File.Exists(file.Destination) && SafePaths.Hash(file.Destination) == file.PreviousHash) continue;
            var previous = file.Destination + ".previous";
            if (!File.Exists(previous) || SafePaths.Hash(previous) != file.PreviousHash) throw new IOException("Previous executable unavailable/corrupt; manual recovery required.");
            File.Copy(previous, file.Destination + ".incoming", true);
            MoveIncoming(file);
        }
    }
    private static void MoveIncoming(UpdateFile file)
    {
        for (var attempt = 0; ; attempt++)
        {
            SafePaths.NoLinks(file.Destination); SafePaths.NoLinks(file.Destination + ".incoming");
            try { File.Move(file.Destination + ".incoming", file.Destination, true); return; }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 20
                && (error is IOException or UnauthorizedAccessException) && (error.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Windows may briefly retain an image/antivirus handle after WaitForExit.
                // Keep the journal and original ACLs; persistent failures still surface.
                Trace.WriteLine($"Updater replacement retry {attempt + 1}: {Path.GetFileName(file.Destination)}: {error.Message}");
                Thread.Sleep(100);
            }
        }
    }
    private static void Restart(string path, UpdateJob job)
    {
        if (!job.Restart) return;
        // Archive terminal jobs before starting old binaries after a rollback;
        // older CLI clients cannot parse paired-job schema 2.
        Store.Atomic(Path.Combine(job.Home, "updates", "last-update.json"), job); File.Delete(path);
        var start = new ProcessStartInfo(job.Destination) { UseShellExecute = true };
        start.ArgumentList.Add("--home"); start.ArgumentList.Add(job.Home);
        Process.Start(start);
    }
    private static UpdateJob Load(string path)
    {
        SafePaths.NoLinks(path); var job = Store.Read<UpdateJob>(path);
        if (job.Companions == null) throw new IOException("Invalid updater component list.");
        if (job.SchemaVersion is not (JobVersion or BundleJobVersion) || !Path.GetFullPath(path).Equals(Pending(Path.GetFullPath(job.Home)), StringComparison.OrdinalIgnoreCase)
            || (job.SchemaVersion == JobVersion && job.Companions.Count > 0)) throw new IOException("Invalid updater job location/schema.");
        var files = Files(job); var destinations = files.Select(file => file.Destination).ToList(); SafePaths.DistinctDestinations(destinations);
        foreach (var file in files) ValidateFile(job.Home, job.Destination, file);
        return job;
    }
    private static void ValidateFile(string home, string primary, UpdateFile file)
    {
        foreach (var path in new[] { file.Destination, file.Download, file.Destination + ".previous", file.Destination + ".incoming" }) SafePaths.NoLinks(path);
        if (!IsAppName(file.Destination) || !Path.GetDirectoryName(Path.GetFullPath(file.Destination))!.Equals(Path.GetDirectoryName(Path.GetFullPath(primary)), StringComparison.OrdinalIgnoreCase)
            || !SafePaths.Within(Path.Combine(home, "updates"), file.Download)) throw new IOException("Invalid updater destinations.");
    }
    private static List<UpdateFile> Files(UpdateJob job) => new[] { new UpdateFile { Destination = job.Destination, Download = job.Download, ExpectedHash = job.ExpectedHash, PreviousHash = job.PreviousHash } }.Concat(job.Companions).ToList();
    private static string HealthPath(string path, UpdateJob job, UpdateFile file) => file.Destination.Equals(job.Destination, StringComparison.OrdinalIgnoreCase)
        ? path + ".healthy" : path + ".healthy-" + Path.GetFileName(file.Destination);
    private static void EnsureIdle(Store store)
    {
        if (File.Exists(store.JournalPath) || File.Exists(store.RenameJournalPath)) throw new IOException("Recover the pending game operation before updating.");
    }
    private static void EnsureStopped(IEnumerable<string> destinations)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = destinations.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in paths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        if (process.MainModule?.FileName is { } executable && paths.Contains(Path.GetFullPath(executable)))
                            throw new IOException("Close the other application before updating: " + executable);
                    }
                    catch (InvalidOperationException) { /* Process exited during inspection. */ }
                    catch (System.ComponentModel.Win32Exception error) { throw new IOException("Cannot verify whether an application is running: " + name, error); }
                }
    }
    private static Process Start(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Could not start updater process.");
    }
    private static bool IsAppName(string path) => new[] { "optibackup.exe", Releases.Asset, "optibackup-ui.exe", Releases.UiAsset }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
}
