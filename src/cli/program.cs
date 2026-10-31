using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerBackup");
        try
        {
            if (args.Length >= 2 && args[0] == "--update-helper") return UpdateHost.RunHelper(args[1], args.Contains("--rollback-update"));
            if (args.Length == 2 && args[0] == "--update-health") return UpdateHost.Health(args[1]);
            var parsed = new Arguments(args);
            home = Path.GetFullPath(parsed.Get("home") ?? home);
            if (parsed.Positionals.FirstOrDefault() is "help" || parsed.Has("help")) { Console.WriteLine(Application.Help); return 0; }
            if (parsed.Has("version")) { Console.WriteLine(Releases.DisplayVersion); return 0; }
            using var store = new Store(home);
            if (OperatingSystem.IsWindows() && UpdateHost.ResumePending(home))
            { Console.Error.WriteLine("Finishing interrupted application update recovery; this process will exit."); return 0; }
            var engine = new Engine(store, Detection.EnsureStopped);
            if (engine.Recover()) Console.Error.WriteLine("Recovered interrupted game operation to its previous state.");
            var app = new Application(store, engine);
            if (parsed.Positionals.Count == 0)
            {
                if (Console.IsInputRedirected) { Console.WriteLine(Application.Help); return 0; }
                if (await app.StartupUpdate()) return 0;
                await app.Menu();
            }
            else await app.Run(parsed);
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException or HttpRequestException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Error: {SessionDiagnostics.Message(e)}");
            try
            {
                SafePaths.NoLinks(home);
                Directory.CreateDirectory(home);
                File.AppendAllText(Path.Combine(home, "errors.log"), $"{DateTimeOffset.UtcNow:o} operation={args.FirstOrDefault() ?? "menu"} {e.GetType().Name}: {e.Message}{Environment.NewLine}");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Could not write error log: " + logError.Message); }
            return 1;
        }
    }
}

internal sealed class Arguments
{
    private static readonly HashSet<string> Flags = ["yes", "json", "dry-run", "original", "confirm-clean", "allow-overlap", "override-edits", "external", "help", "version"];
    private static readonly HashSet<string> Values = ["home", "map", "exe"];
    private readonly Dictionary<string, string?> options = [];
    public List<string> Positionals { get; } = [];
    public Arguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Positionals.Add(args[i]); continue; }
            var key = args[i][2..];
            if (!Flags.Contains(key) && !Values.Contains(key)) throw new ArgumentException("Unknown option: " + args[i]);
            if (options.ContainsKey(key)) throw new ArgumentException("Repeated option: " + key);
            if (Values.Contains(key))
            {
                if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing value for --" + key);
                options[key] = args[i];
            }
            else options[key] = null;
        }
    }
    public bool Has(string key) => options.ContainsKey(key);
    public string? Get(string key) => options.GetValueOrDefault(key);
    public string At(int index) => Positionals.Count > index ? Positionals[index] : throw new ArgumentException("Missing argument. Run optibackup help.");
    public void Count(int min, int max)
    {
        if (Positionals.Count < min || Positionals.Count > max) throw new ArgumentException("Unexpected argument count. Run optibackup help.");
    }
}

internal sealed partial class Application(Store store, Engine engine)
{
    private ComponentFingerprints? fingerprints;
    private ComponentFingerprints Fingerprints => fingerprints ??= ComponentFingerprints.Load(store.Home);
    private readonly Discovery discovery = new(message => Console.Error.WriteLine("Warning: " + message));
    private bool exitForUpdate;
    public const string Help = """
        OptiScaler Backup
          optibackup                              Interactive menu
          optibackup menu                         Explicit menu (also supports piped input)
          optibackup scan [folder ...]            Launchers plus selected folders
          optibackup games list
          optibackup games add <name> <folder> [--exe <relative-path>]
          optibackup games show <game-id>
          optibackup files list|detect <game-id>
          optibackup files add <game-id> <relative-path>
          optibackup files add <game-id> <absolute-file> --external
          optibackup files remove <game-id> <root-id:relative-path>
          optibackup components catalog|refresh
          optibackup components identify <game-id> <snapshot-id>
          optibackup settings <game-id> [snapshot-id]
          optibackup snapshots list <game-id>
          optibackup snapshots create <game-id> <name> [--original --confirm-clean] [--dry-run]
          optibackup snapshots rename <game-id> <snapshot-id> <name>
          optibackup restore <game-id> <snapshot-id> [--dry-run] [--yes]
          optibackup packages inspect <game-id> <zip>
          optibackup packages import <game-id> <zip> <name> <version> --map <mapping.json>
          optibackup packages list <game-id>
          optibackup packages show <game-id> <package-id>
          optibackup packages rename <game-id> <package-id> <name> [version-label]
          optibackup packages install <game-id> <package-id> [--allow-overlap] [--dry-run] [--yes]
          optibackup packages rollback <game-id> <installation-id> [--override-edits] [--dry-run] [--yes]
          optibackup update check|install
          optibackup config show
          optibackup config set <backup-directory|check-updates|max-archive-bytes|max-archive-files> <value>
        Global: --home <data-folder>, --json (read/preview output), --yes, --version, --help
        ZIP mapping JSON: { "archive/path.ini": "game/relative/path.ini", "skip.txt": null }
        --yes accepts displayed changes; overlap and edited-file overrides require their own flags.
        """;

    public async Task Run(Arguments args)
    {
        switch (args.At(0))
        {
            case "menu": args.Count(1, 1); await Menu(); break;
            case "scan":
                discovery.Merge(store, discovery.Scan(args.Positionals.Skip(1)));
                Output(GameRows(), args); break;
            case "games": Games(args); break;
            case "files": Files(args); break;
            case "snapshots": Snapshots(args); break;
            case "components": await Components(args); break;
            case "settings":
                args.Count(2, 3);
                var settingsGame = store.Game(args.At(1));
                Output(args.Positionals.Count == 3 ? GameSettingsReader.Snapshot(store, FindSnapshot(settingsGame, args.At(2))) : GameSettingsReader.Current(settingsGame), args); break;
            case "restore": Restore(args); break;
            case "packages": PackageCommands(args); break;
            case "config": Config(args); break;
            case "update":
                args.Count(2, 2);
                if (args.At(1) is not ("check" or "install")) throw new ArgumentException("Use update check or update install.");
                await Update(args.At(1) == "install", args); break;
            default: throw new ArgumentException("Unknown command. Run optibackup help.");
        }
    }

    private object GameRows() => store.Data.Games.Select(g => new { g.Id, g.Name, g.Directory, g.Sources, Available = Directory.Exists(g.Directory), g.Executables }).ToList();
    private static void Output(object value, Arguments args)
    {
        // JSON is also the plain command format; menus provide compact human navigation.
        Console.WriteLine(JsonSerializer.Serialize(value, Format.Json));
    }
    private static void Confirm(string action, Arguments args)
    {
        if (args.Has("yes")) return;
        if (Console.IsInputRedirected) throw new InvalidOperationException(action + " requires confirmation. Review --dry-run, then pass --yes.");
        Console.Write(action + "? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cancelled; no game files changed.");
    }

    private void Games(Arguments args)
    {
        switch (args.At(1))
        {
            case "list": args.Count(2, 2); Output(GameRows(), args); break;
            case "show": args.Count(3, 3); Output(store.Game(args.At(2)), args); break;
            case "add":
                args.Count(4, 4);
                var path = Path.GetFullPath(args.At(3));
                SafePaths.NoLinks(path);
                if (!Directory.Exists(path)) throw new IOException("Choose an existing game directory.");
                if (SafePaths.Within(path, store.Home) || SafePaths.Within(path, store.PayloadRoot) || SafePaths.Within(store.Home, path))
                    throw new IOException("Game and application storage directories must not overlap.");
                discovery.Merge(store, [new(args.At(2), path, "Manual")]);
                var game = store.Data.Games.Single(g => g.Directory.Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
                if (args.Get("exe") is { } exe)
                {
                    exe = SafePaths.Relative(exe);
                    if (!File.Exists(SafePaths.Resolve(game, new("game", exe))) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Selected game executable does not exist or is not an .exe.");
                    game.Executables = [exe]; store.Save();
                }
                Output(game, args); break;
            default: throw new ArgumentException("Unknown games action.");
        }
    }

    private void Files(Arguments args)
    {
        var game = store.Game(args.At(2));
        switch (args.At(1))
        {
            case "list": args.Count(3, 3); Output(game.Managed.Select(t => new { Id = t.Key, Target = t, File = SafePaths.Resolve(game, t) }), args); break;
            case "detect":
                args.Count(3, 3);
                Output(new { InGame = Detection.Files(game, Warn), ExternalSettings = Detection.ExternalCandidates(game, Warn), game.Executables }, args); break;
            case "add":
                args.Count(4, 4);
                var next = Format.Clone(game);
                Target target;
                if (args.Has("external"))
                {
                    var file = Path.GetFullPath(args.At(3));
                    SafePaths.NoLinks(file);
                    if (!File.Exists(file)) throw new IOException("External settings file does not exist.");
                    var root = Path.GetDirectoryName(file)!;
                    var id = next.ExternalRoots.FirstOrDefault(p => p.Value.Equals(root, StringComparison.OrdinalIgnoreCase)).Key ?? Format.Id();
                    next.ExternalRoots[id] = root;
                    target = new(id, SafePaths.Relative(Path.GetFileName(file)));
                    Console.WriteLine("Approve external settings file: " + file);
                    Confirm("Manage this external file (exclude saves/caches/logs)", args);
                }
                else target = new("game", SafePaths.Relative(args.At(3)));
                var dest = SafePaths.Resolve(next, target);
                if (Directory.Exists(dest) || SafePaths.Within(store.Home, dest) || SafePaths.Within(store.PayloadRoot, dest)) throw new IOException("Invalid managed file destination.");
                if (!next.Managed.Any(t => t.Key == target.Key)) next.Managed.Add(target);
                store.Put(next); Output(target, args); break;
            case "remove":
                args.Count(4, 4);
                var copy = Format.Clone(game);
                if (copy.Managed.RemoveAll(t => t.Key.Equals(args.At(3), StringComparison.OrdinalIgnoreCase)) == 0) throw new InvalidOperationException("Managed file ID not found.");
                store.Put(copy); break;
            default: throw new ArgumentException("Unknown files action.");
        }
    }

    private void Snapshots(Arguments args)
    {
        var game = store.Game(args.At(2));
        switch (args.At(1))
        {
            case "list": args.Count(3, 3); Output(game.Snapshots.Select(SnapshotOutput), args); break;
            case "create":
                args.Count(4, 4);
                if (args.Has("original")) Console.Error.WriteLine("Original records current files. Confirm clean only if you know they are unmodified.");
                var plan = engine.PrepareSnapshot(game);
                if (args.Has("original"))
                {
                    var targets = plan.Targets.Concat(Detection.Files(game, Warn).Select(f => new Target("game", f.Path))).DistinctBy(t => t.Key).ToList();
                    plan = plan with { Targets = targets };
                }
                if (args.Has("dry-run")) { Output(plan, args); break; }
                var automatic = plan.Targets.Where(t => !game.Managed.Any(m => m.Key == t.Key)).ToList();
                if (automatic.Count > 0)
                {
                    Console.Error.WriteLine("Additional inventory/detected files:\n" + string.Join("\n", automatic.Select(t => t.Root + ": " + t.Path)));
                    Confirm("Capture these additional files alongside selected originals", args);
                }
                Output(SnapshotOutput(engine.CreateSnapshot(game, args.At(3), args.Has("original"), args.Has("confirm-clean"), plan)), args); break;
            case "rename":
                args.Count(5, 5);
                engine.RenameSnapshot(game, args.At(3), args.At(4)); break;
            default: throw new ArgumentException("Unknown snapshots action.");
        }
    }

    private async Task Components(Arguments args)
    {
        var catalog = Fingerprints;
        switch (args.At(1))
        {
            case "catalog": args.Count(2, 2); Output(new { catalog.Sha256, Count = catalog.Document.Records.Count, catalog.Document.Sources }, args); break;
            case "identify":
                args.Count(4, 4); var game = store.Game(args.At(2));
                Output(catalog.Identify(store, FindSnapshot(game, args.At(3))), args); break;
            case "refresh":
                args.Count(2, 2);
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) })
                {
                    catalog = await catalog.Refresh(client, store.Home); fingerprints = catalog;
                    Output(new { catalog.Sha256, Count = catalog.Document.Records.Count }, args);
                }
                break;
            default: throw new ArgumentException("Use components catalog, refresh or identify.");
        }
    }

    private object SnapshotOutput(Snapshot snapshot) => new
    {
        snapshot.Id,
        snapshot.Name,
        snapshot.Created,
        snapshot.Original,
        snapshot.Recovery,
        snapshot.Folder,
        FolderPath = store.SnapshotPath(snapshot),
        snapshot.Files,
        Components = Fingerprints.Identify(store, snapshot)
    };

    private static Snapshot FindSnapshot(Game game, string id) => game.Snapshots.SingleOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Snapshot ID not found.");
    private static Package FindPackage(Game game, string id) => game.Packages.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Package ID not found.");
    private static Installation FindInstallation(Game game, string id) => game.Installations.SingleOrDefault(i => i.Id == id) ?? throw new InvalidOperationException("Installation ID not found.");
    private static void Warn(string message) => Console.Error.WriteLine("Warning: " + message);

    private void Restore(Arguments args)
    {
        args.Count(3, 3);
        var game = store.Game(args.At(1));
        var snapshot = FindSnapshot(game, args.At(2));
        var desired = engine.SnapshotFiles(game, snapshot);
        var changes = engine.Preview(game, desired);
        Output(new
        {
            Changes = changes,
            Blockers = engine.ActiveOverlaps(game, desired.Select(f => f.Target)),
            UnrecordedManagedFiles = game.Managed.Where(t => !snapshot.Files.Any(f => f.Target.Key == t.Key)),
            Warning = "Only recorded paths change; explicit recorded absence removes a file. Changed binaries may be from a newer game update."
        }, args);
        if (args.Has("dry-run")) return;
        Confirm("Restore exact managed state and create recovery snapshot", args);
        engine.Restore(game, snapshot);
    }

    private void PackageCommands(Arguments args)
    {
        var game = store.Game(args.At(2));
        var packages = new Packages(store);
        switch (args.At(1))
        {
            case "inspect": args.Count(4, 4); Output(packages.Inspect(game, args.At(3)), args); break;
            case "import":
                args.Count(6, 6);
                var plan = packages.Inspect(game, args.At(3));
                var mappingPath = args.Get("map") ?? throw new ArgumentException("Pass --map mapping.json; use packages inspect to review suggested paths.");
                var mappings = Store.Read<Dictionary<string, string?>>(mappingPath);
                Output(new { Plan = plan, ReviewedMappings = mappings }, args);
                Confirm("Import this reviewed package (no installation yet)", args);
                Output(packages.Import(game, plan, args.At(4), args.At(5), mappings), args); break;
            case "list": args.Count(3, 3); Output(new { game.Packages, game.Installations }, args); break;
            case "show":
                args.Count(4, 4);
                var package = FindPackage(game, args.At(3));
                Output(new { Package = package, Installations = game.Installations.Where(i => i.PackageId == package.Id) }, args); break;
            case "rename":
                args.Count(5, 6);
                var next = Format.Clone(game);
                var renamed = FindPackage(next, args.At(3));
                renamed.Name = Format.Label(args.At(4));
                if (args.Positionals.Count == 6) renamed.Version = Format.Label(args.At(5));
                store.Put(next); break;
            case "install":
                args.Count(4, 4);
                var selected = FindPackage(game, args.At(3));
                Output(new { Changes = engine.Preview(game, selected.Files), Overlaps = engine.ActiveOverlaps(game, selected.Files.Select(f => f.Target)) }, args);
                if (args.Has("dry-run")) return;
                Confirm("Back up affected originals and install package", args);
                Output(engine.Install(game, selected, args.Has("allow-overlap")), args); break;
            case "rollback":
                args.Count(4, 4);
                var installation = FindInstallation(game, args.At(3));
                Output(new { Changes = engine.RollbackChanges(game, installation), EditedFiles = engine.Modified(game, installation) }, args);
                if (args.Has("dry-run")) return;
                Confirm("Save recovery snapshot and roll back package", args);
                engine.Rollback(game, installation, args.Has("override-edits")); break;
            default: throw new ArgumentException("Unknown packages action.");
        }
    }

    private void Config(Arguments args)
    {
        if (args.At(1) == "show") { args.Count(2, 2); Output(new { store.Home, store.PayloadRoot, Settings = store.Data.Settings }, args); return; }
        args.Count(4, 4);
        if (args.At(1) != "set") throw new ArgumentException("Use config show or config set.");
        var value = args.At(3);
        switch (args.At(2))
        {
            case "download-thumbnails":
                if (!bool.TryParse(value, out var thumbnails)) throw new ArgumentException("download-thumbnails must be true or false.");
                store.Data.Settings.DownloadThumbnails = thumbnails; break;
            case "check-updates":
                if (!bool.TryParse(value, out var check)) throw new ArgumentException("check-updates must be true or false.");
                store.Data.Settings.CheckUpdates = check; break;
            case "max-archive-bytes":
                if (!long.TryParse(value, out var bytes) || bytes < 1 || bytes > 64L * 1024 * 1024 * 1024) throw new ArgumentException("Archive limit must be 1–68719476736 bytes.");
                store.Data.Settings.MaxArchiveBytes = bytes; break;
            case "max-archive-files":
                if (!int.TryParse(value, out var count) || count < 1 || count > 1000000) throw new ArgumentException("File limit must be 1–1000000.");
                store.Data.Settings.MaxArchiveFiles = count; break;
            case "backup-directory": MoveBackups(value, args); return;
            default: throw new ArgumentException("Unknown configuration setting.");
        }
        store.Save();
    }

    private void MoveBackups(string value, Arguments args)
    {
        Confirm($"Copy verified snapshot folders and package payloads to {Path.GetFullPath(value)}; retain old copies", args);
        store.RelocatePayloads(value);
    }

    public async Task<bool> StartupUpdate()
    {
        if (!store.Data.Settings.CheckUpdates || store.Data.Settings.LastUpdateCheck > DateTimeOffset.UtcNow.AddDays(-1)) return false;
        try { await Update(false, new Arguments([]), startup: true); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        { Warn("Update check unavailable: " + e.Message); }
        return exitForUpdate;
    }

    private async Task Update(bool install, Arguments args, bool startup = false)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var releases = new Releases(client);
        store.Data.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
        store.Save();
        using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var release = await releases.Check(checkTimeout.Token, includeCurrent: true);
        UpdateHost.RememberRelease(store.Home, release);
        if (release == null || !UpdateHost.NeedsUpdate(release)) { if (!startup) Output(new { Current = Releases.DisplayVersion, Update = "No newer stable release" }, args); return; }
        Output(release, args);
        if (startup)
        {
            Console.Write("Install this update now? [y/N] ");
            if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return;
        }
        else if (!install) return;
        else Confirm("Download and update this application and installed side-by-side CLI/UI companions", args);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Install updates on Windows using the published executable.");
        await UpdateHost.Prepare(store, releases, release);
        exitForUpdate = true;
        Console.WriteLine("Update staged. Closing this session so the updater can replace and restart the application.");
    }
}
