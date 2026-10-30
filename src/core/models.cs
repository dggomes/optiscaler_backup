using System.Text.Json;

namespace OptiScalerBackup.Core;

public static class Format
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Id() => Guid.NewGuid().ToString("N");
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public static string Label(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 160
        && !name.Any(char.IsControl) ? name.Trim() : throw new InvalidOperationException("Name must be 1–160 printable characters.");
}

public sealed record Target(string Root, string Path)
{
    public string Key => Root + ":" + Path.Replace('\\', '/').ToLowerInvariant();
}
public sealed record FileState(Target Target, string? Hash, long Size);
public sealed class Snapshot
{
    public string Id { get; set; } = Format.Id();
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public bool Original { get; set; }
    public bool Recovery { get; set; }
    public List<FileState> Files { get; set; } = [];
}
public sealed class Package
{
    public string Id { get; set; } = Format.Id();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string ArchiveHash { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<FileState> Files { get; set; } = [];
    public List<PackageMapping> Mappings { get; set; } = [];
    public List<string> ExcludedEntries { get; set; } = [];
}
public sealed record PackageMapping(string Entry, Target Target, string Hash, long Size);
public sealed class Installation
{
    public string Id { get; set; } = Format.Id();
    public string PackageId { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RolledBack { get; set; }
    public Snapshot Before { get; set; } = new();
    public List<FileState> Installed { get; set; } = [];
}
public sealed class Game
{
    public string Id { get; set; } = Format.Id();
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<string> Executables { get; set; } = [];
    public Dictionary<string, string> ExternalRoots { get; set; } = [];
    public List<Target> Managed { get; set; } = [];
    public List<Snapshot> Snapshots { get; set; } = [];
    public List<Package> Packages { get; set; } = [];
    public List<Installation> Installations { get; set; } = [];
}
public sealed class Settings
{
    public string? BackupDirectory { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public long MaxArchiveBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxArchiveFiles { get; set; } = 10000;
}
public sealed class Catalog
{
    public int SchemaVersion { get; set; } = Format.Version;
    public Settings Settings { get; set; } = new();
    public List<Game> Games { get; set; } = [];
}
public sealed class Journal
{
    public int SchemaVersion { get; set; } = Format.Version;
    public string Id { get; set; } = Format.Id();
    public Game BeforeGame { get; set; } = new();
    public Snapshot BeforeFiles { get; set; } = new();
    public List<FileState> Desired { get; set; } = [];
}
public sealed record Change(string Action, Target Target, string? CurrentHash, string? DesiredHash);
public sealed record ImportEntry(string Entry, string Hash, long Size, string? SuggestedPath, List<string> Candidates);
public sealed record ImportPlan(string ArchiveHash, List<ImportEntry> Files);
