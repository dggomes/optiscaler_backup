using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Cli;

internal sealed partial class Application
{
    private static string Ask(string prompt)
    {
        Console.Write(prompt + ": ");
        return Console.ReadLine()?.Trim() ?? throw new InvalidOperationException("Input closed.");
    }
    private static int Pick(string title, IReadOnlyList<string> choices)
    {
        Console.WriteLine("\n" + title);
        for (var i = 0; i < choices.Count; i++) Console.WriteLine($"  {i + 1}. {choices[i]}");
        Console.Write("  0. Back/exit\n> ");
        var text = Console.ReadLine();
        if (text == null || text == "0") return -1;
        if (!int.TryParse(text, out var index) || index < 1 || index > choices.Count) throw new InvalidOperationException("Choose a listed number.");
        return index - 1;
    }
    private Task Command(params string[] args) => Run(new Arguments(args));
    private static bool Yes(string prompt) => Ask(prompt + " [y/N]").Equals("y", StringComparison.OrdinalIgnoreCase);
    private void MenuError(Exception error)
    {
        Console.Error.WriteLine("Error: " + error.Message);
        try { File.AppendAllText(Path.Combine(store.Home, "errors.log"), $"{DateTimeOffset.UtcNow:o} menu {error.GetType().Name}: {error.Message}\n"); }
        catch (IOException logError) { Warn("Cannot write error log: " + logError.Message); }
    }

    public async Task Menu()
    {
        while (!exitForUpdate)
        {
            try
            {
                var choice = Pick("OptiScaler Backup", ["Scan installed launchers", "Scan a custom folder", "Game list", "Add a game manually", "Check application updates", "Configuration", "Excluded games"]);
                switch (choice)
                {
                    case -1: return;
                    case 0: await Command("scan"); break;
                    case 1: await Command("scan", Ask("Folder to scan")); break;
                    case 2:
                        var game = GamePicker.Select(Console.In, Console.Out, store.VisibleGames.ToList());
                        if (game != null) await GameMenu(game.Id);
                        break;
                    case 3: await Command("games", "add", Ask("Game name"), Ask("Game installation folder")); break;
                    case 4:
                        await Command("update", "check");
                        if (Yes("Install available update")) await Command("update", "install"); break;
                    case 6:
                        var hidden = GamePicker.Select(Console.In, Console.Out, store.Data.Games.Where(g => g.Excluded).ToList());
                        if (hidden != null && Yes("Restore to library")) await Command("games", "unexclude", hidden.Id, "--yes");
                        break;
                    case 5:
                        await Command("config", "show");
                        var setting = Pick("Configure", ["Backup directory", "Enable/disable update checks", "ZIP expanded-size limit", "ZIP entry-count limit"]);
                        if (setting >= 0) await Command("config", "set", new[] { "backup-directory", "check-updates", "max-archive-bytes", "max-archive-files" }[setting], Ask(setting == 1 ? "true or false" : "Value"));
                        break;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or HttpRequestException or TaskCanceledException)
            { MenuError(e); if (Console.IsInputRedirected) return; }
        }
    }

    private async Task GameMenu(string id)
    {
        await SelectDetected(store.Game(id), automatic: true);
        while (!exitForUpdate)
        {
            try
            {
                var game = store.Game(id);
                var choice = Pick(game.Name, ["Detect and select managed files", "List managed files", "Add a managed file", "Stop managing a file", "Create named snapshot", "Restore snapshot", "Rename snapshot", "Packages", "Select game executable", "Inspect backup components and graphics settings", "Read current graphics settings", "Refresh recognition catalog", "Remove and exclude game"]);
                switch (choice)
                {
                    case -1: return;
                    case 0: await SelectDetected(game); break;
                    case 1: await Command("files", "list", id); break;
                    case 2:
                        var external = Yes("File is outside the game directory");
                        var path = Ask(external ? "Absolute settings file path (no saves)" : "Game-relative file path (may be absent)");
                        await Run(new Arguments(new[] { "files", "add", id, path }.Concat(external ? new[] { "--external" } : []).ToArray())); break;
                    case 3:
                        var managed = Pick("Stop managing (snapshots remain)", game.Managed.Select(t => t.Key).ToList());
                        if (managed >= 0) await Command("files", "remove", id, game.Managed[managed].Key); break;
                    case 4:
                        var name = Ask("Snapshot name");
                        var original = !game.Snapshots.Any(s => s.Original) && Yes("Establish this snapshot as Original");
                        if (original && !Yes("I confirm the current files are clean/unmodified")) break;
                        await Run(new Arguments(new[] { "snapshots", "create", id, name }.Concat(original ? new[] { "--original", "--confirm-clean" } : []).ToArray())); break;
                    case 5:
                        var restore = Pick("Restore snapshot", game.Snapshots.Select(s => $"{s.Name} — {s.Created:g} {(s.Original ? "[Original]" : "")}").ToList());
                        if (restore >= 0) await Command("restore", id, game.Snapshots[restore].Id); break;
                    case 6:
                        var rename = Pick("Rename snapshot", game.Snapshots.Select(s => s.Name).ToList());
                        if (rename >= 0) await Command("snapshots", "rename", id, game.Snapshots[rename].Id, Ask("New name")); break;
                    case 7: await PackageMenu(id); break;
                    case 8:
                        var exes = Detection.Executables(game.Directory, Warn);
                        var selected = Pick("Actual game executable (choose nested Win64-Shipping where applicable)", exes);
                        if (selected >= 0) { game.Executables = [exes[selected]]; store.Save(); }
                        break;
                    case 9:
                        var insight = Pick("Backup components and settings", game.Snapshots.Select(s => s.Name).ToList());
                        if (insight >= 0) { await Command("components", "identify", id, game.Snapshots[insight].Id); await Command("settings", id, game.Snapshots[insight].Id); }
                        break;
                    case 10: await Command("settings", id); break;
                    case 11: await Command("components", "refresh"); break;
                    case 12:
                        if (Yes("Remove and exclude game (backups and game files are retained)")) { await Command("games", "exclude", id, "--yes"); return; }
                        break;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            { MenuError(e); }
        }
    }

    private Task SelectDetected(Game game, bool automatic = false)
    {
        if (!Directory.Exists(game.Directory))
        {
            Console.WriteLine("Game directory is unavailable; reconnect it to detect managed files.");
            return Task.CompletedTask;
        }
        var detected = Detection.Files(game, Warn);
        var requests = detected.Where(c => !game.Managed.Any(t => t.Root == "game" && t.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new FileRequest(c.Path)).ToList();
        var external = Detection.ExternalCandidates(game, Warn).Where(path => !game.Managed.Any(t =>
        {
            var root = t.Root == "game" ? game.Directory : game.ExternalRoots[t.Root];
            return Path.GetFullPath(Path.Combine(root, t.Path)).Equals(path, StringComparison.OrdinalIgnoreCase);
        })).ToList();
        requests.AddRange(external.Select(path => new FileRequest(path, true)));
        if (automatic && requests.Count == 0 && game.Managed.Count > 0) return Task.CompletedTask;
        Console.WriteLine($"\nDetected {requests.Count} new standard managed files for {game.Name}:");
        foreach (var candidate in detected.Where(c => requests.Any(r => !r.External && r.Path == c.Path)))
            Console.WriteLine($"  {candidate.Path} [{candidate.Role}] {candidate.Evidence}{(candidate.ReviewRequired ? " — review this candidate" : "")}");
        foreach (var file in external) Console.WriteLine("  " + file + " [external settings]");
        if (Yes("Add any other files to this selection"))
        {
            while (true)
            {
                var path = Ask("Additional file (game-relative or absolute; blank finishes)");
                if (path.Length == 0) break;
                requests.Add(new(path, Path.IsPathFullyQualified(path)));
            }
        }
        if (requests.Count == 0) { Console.WriteLine("No new managed files selected."); return Task.CompletedTask; }
        var proposal = ManagedFiles.Prepare(game, requests.Distinct(), store.Home, store.PayloadRoot);
        var added = proposal.Managed.Where(t => !game.Managed.Any(existing => existing.Key == t.Key)).ToList();
        if (added.Count == 0) { Console.WriteLine("All selected files are already managed."); return Task.CompletedTask; }
        Console.WriteLine($"\nApprove {added.Count} managed files (including any external paths below):");
        foreach (var target in added) Console.WriteLine("  " + SafePaths.Resolve(proposal, target));
        if (Yes("Manage this entire selection")) { store.Put(proposal); Console.WriteLine($"Added {added.Count} managed files."); }
        else Console.WriteLine("Selection cancelled; managed files unchanged.");
        return Task.CompletedTask;
    }

    private async Task PackageMenu(string id)
    {
        while (true)
        {
            var game = store.Game(id);
            var choice = Pick("Packages — " + game.Name, ["Import ZIP and optionally install", "View packages/history", "Install package/version", "Roll back installation", "Rename package"]);
            if (choice < 0) return;
            switch (choice)
            {
                case 0: await ImportInteractive(game); break;
                case 1: await Command("packages", "list", id); break;
                case 2:
                    var selected = Pick("Install", game.Packages.Select(p => $"{p.Name} — {p.Version}").ToList());
                    if (selected < 0) break;
                    await InstallInteractive(game, game.Packages[selected]); break;
                case 3:
                    var active = game.Installations.Where(i => i.RolledBack == null).Reverse().ToList();
                    var rollback = Pick("Roll back", active.Select(i => $"{FindPackage(game, i.PackageId).Name} — {i.Created:g} — {i.Id}").ToList());
                    if (rollback < 0) break;
                    var installation = active[rollback];
                    Output(new { Changes = engine.RollbackChanges(game, installation), EditedFiles = engine.Modified(game, installation) }, new Arguments([]));
                    var edited = engine.Modified(game, installation).Count > 0;
                    if (edited && !Yes("Files changed externally. Override AFTER saving a recovery snapshot")) break;
                    await Run(new Arguments(new[] { "packages", "rollback", id, installation.Id }.Concat(edited ? new[] { "--override-edits" } : []).ToArray())); break;
                case 4:
                    var rename = Pick("Rename package", game.Packages.Select(p => p.Name + " — " + p.Version).ToList());
                    if (rename >= 0) await Command("packages", "rename", id, game.Packages[rename].Id, Ask("New name")); break;
            }
        }
    }

    private async Task ImportInteractive(Game game)
    {
        var packages = new Packages(store);
        var plan = packages.Inspect(game, Ask("ZIP file path"));
        var mappings = new Dictionary<string, string?>();
        foreach (var entry in plan.Files)
        {
            Console.WriteLine($"\nZIP: {entry.Entry} ({entry.Size} bytes)");
            if (entry.Candidates.Count > 0) Console.WriteLine("Existing matches: " + string.Join(", ", entry.Candidates));
            var answer = Ask($"Game-relative destination [{entry.SuggestedPath ?? "unresolved"}]; '-' excludes");
            var dest = answer == "" ? entry.SuggestedPath : answer == "-" ? null : answer;
            if (answer == "" && dest == null) throw new InvalidOperationException("Ambiguous entry needs a destination or explicit exclusion. Import cancelled.");
            mappings[entry.Entry] = dest;
        }
        var name = Ask("Package name");
        var version = Ask("Version label");
        Output(mappings, new Arguments([]));
        if (!Yes("Save this reviewed package")) return;
        var package = packages.Import(game, plan, name, version, mappings);
        if (Yes("Preview and install now")) await InstallInteractive(store.Game(game.Id), package);
    }

    private async Task InstallInteractive(Game game, Package package)
    {
        var overlaps = engine.ActiveOverlaps(game, package.Files.Select(f => f.Target));
        var allow = false;
        if (overlaps.Count > 0)
        {
            Console.WriteLine("Overlaps installations: " + string.Join(", ", overlaps));
            allow = Yes("Allow this new layer (rollback must follow reverse overlap order)");
            if (!allow) return;
        }
        await Run(new Arguments(new[] { "packages", "install", game.Id, package.Id }.Concat(allow ? new[] { "--allow-overlap" } : []).ToArray()));
    }
}
