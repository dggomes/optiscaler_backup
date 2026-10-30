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
                var choice = Pick("OptiScaler Backup", ["Scan installed launchers", "Scan a custom folder", "Game list", "Add a game manually", "Check application updates", "Configuration"]);
                switch (choice)
                {
                    case -1: return;
                    case 0: await Command("scan"); break;
                    case 1: await Command("scan", Ask("Folder to scan")); break;
                    case 2:
                        var games = store.Data.Games.ToList();
                        var game = Pick("Games", games.Select(g => $"{g.Name} {(Directory.Exists(g.Directory) ? "" : "[unavailable]")} — {g.Directory}").ToList());
                        if (game >= 0) await GameMenu(games[game].Id);
                        break;
                    case 3: await Command("games", "add", Ask("Game name"), Ask("Game installation folder")); break;
                    case 4:
                        await Command("update", "check");
                        if (Yes("Install available update")) await Command("update", "install"); break;
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
        while (!exitForUpdate)
        {
            try
            {
                var game = store.Game(id);
                var choice = Pick(game.Name, ["Detect and select managed files", "List managed files", "Add a managed file", "Stop managing a file", "Create named snapshot", "Restore snapshot", "Rename snapshot", "Packages", "Select game executable"]);
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
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            { MenuError(e); }
        }
    }

    private async Task SelectDetected(Game game)
    {
        foreach (var candidate in Detection.Files(game, Warn))
        {
            if (game.Managed.Any(t => t.Root == "game" && t.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine($"\n{candidate.Path} [{candidate.Role}] {candidate.Evidence}");
            if (Yes("Manage this file")) await Command("files", "add", game.Id, candidate.Path);
            game = store.Game(game.Id);
        }
        foreach (var candidate in Detection.ExternalCandidates(game, Warn))
        {
            if (game.Managed.Any(t => SafePaths.Resolve(game, t).Equals(candidate, StringComparison.OrdinalIgnoreCase))) continue;
            if (Yes("Manage external settings file " + candidate)) await Command("files", "add", game.Id, candidate, "--external", "--yes");
            game = store.Game(game.Id);
        }
        Console.WriteLine("Add currently absent mod filenames with 'Add a managed file' to record their absence in Original.");
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
