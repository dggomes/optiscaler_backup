using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanLibrary();
    private async Task ScanLibrary() => await Guard("Scan library", async () =>
    {
        var warnings = new List<string>();
        await Work("Scanning installed launchers", () => { var discovery = new Discovery(warnings.Add); discovery.Merge(store, discovery.Scan([])); });
        if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(3)));
        await LoadArtwork(); StartThumbnails();
    });
    private async void AddGame_Click(object sender, RoutedEventArgs e) => await Guard("Add game", async () =>
    {
        var folder = new OpenFolderDialog { Title = "Choose the game's installation folder" };
        if (folder.ShowDialog(this) != true) return;
        var directory = System.IO.Path.GetFullPath(folder.FolderName);
        if (new[] { store.Home, store.PayloadRoot }.Any(root => directory.Equals(root, StringComparison.OrdinalIgnoreCase) || SafePaths.Within(root, directory) || SafePaths.Within(directory, root)))
            throw new IOException("Keep game folders separate from application and backup storage.");
        var name = new NameDialog(this, "Add game", System.IO.Path.GetFileName(directory));
        if (name.ShowDialog() != true) return;
        await Work("Adding game", () => new Discovery(TraceWarning).Merge(store, [new(name.Value, directory, "Manual")]));
        await LoadArtwork(); StartThumbnails();
    });

    private void TraceWarning(string message) => System.Diagnostics.Trace.WriteLine(message);
    private async void Detect_Click(object sender, RoutedEventArgs e) => await Guard("Choose backup files", async () => { await ReviewSelection(Selected); });
    private async Task<bool> ReviewSelection(Game game)
    {
        var warnings = new List<string>();
        var rows = await Work("Finding standard files", () => FindFiles(game, warnings.Add));
        if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(3)));
        var dialog = new FileReviewDialog(this, store, game, rows);
        if (dialog.ShowDialog() != true || dialog.Proposal is not { } proposal) return false;
        await Work("Saving selected files", () => store.Put(proposal));
        detectedCounts.Remove(game.Id); RefreshDetails(); return true;
    }

    private static List<SelectionRow> FindFiles(Game game, Action<string>? warning = null)
    {
        if (!Directory.Exists(game.Directory)) throw new IOException("Game directory is unavailable.");
        var existing = game.Managed.Select(t => Physical(game, t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = Detection.Files(game, warning).Where(f => !existing.Contains(Path.GetFullPath(Path.Combine(game.Directory, f.Path)))).Select(f => new SelectionRow
        { Path = f.Path, Role = f.Role, Evidence = f.ReviewRequired ? "Review: " + f.Evidence : f.Evidence }).ToList();
        result.AddRange(Detection.ExternalCandidates(game, warning).Where(f => !existing.Contains(f)).Select(f => new SelectionRow { Path = f, External = true, Role = "External settings", Evidence = "Approve this full path" }));
        return result;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await Guard("Save backup", () => CaptureBackup(false));
    private async void Setup_Click(object sender, RoutedEventArgs e) => await Guard("Set up backup", async () =>
    { if (Selected.Managed.Count == 0) await ReviewSelection(Selected); else await CaptureBackup(true); });
    private async Task CaptureBackup(bool original)
    {
        if (Selected.Managed.Count == 0 && !await ReviewSelection(Selected)) return;
        var game = Selected;
        var dialog = new NameDialog(this, "Save backup", original ? "Original" : "", offerOriginal: !game.Snapshots.Any(s => s.Original), preferOriginal: original);
        if (dialog.ShowDialog() == true) await Work("Saving backup", () => engine.CreateSnapshot(game, dialog.Value, dialog.Original, dialog.Original));
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Guard("Restore version", async () =>
    {
        var game = Selected;
        var row = SnapshotList.SelectedItem as SnapshotRow ?? throw new InvalidOperationException("Select a saved backup.");
        var snapshot = game.Snapshots.Single(s => s.Id == row.Id);
        var preview = await Work("Preparing restore", () =>
        {
            var desired = engine.SnapshotFiles(game, snapshot);
            var blockers = engine.ActiveOverlaps(game, desired.Select(f => f.Target));
            if (blockers.Count > 0) throw new InvalidOperationException("Roll back these packages first: " + BlockerNames(game, blockers));
            return engine.Preview(game, desired);
        });
        var dialog = new ChangeDialog(this, game, "Restore backup", $"Restore {snapshot.Name}. Only recorded files change; explicit absence removes a file. Inspect older binaries after game updates.", preview);
        if (dialog.ShowDialog() == true) await Work("Restoring version", () => engine.Restore(game, snapshot));
    });
    private async void RenameSnapshot_Click(object sender, RoutedEventArgs e) => await Guard("Rename version", async () =>
    {
        var game = Selected; var row = SnapshotList.SelectedItem as SnapshotRow ?? throw new InvalidOperationException("Select a version.");
        var dialog = new NameDialog(this, "Rename version", row.Name);
        if (dialog.ShowDialog() == true) await Work("Renaming version", () => engine.RenameSnapshot(game, row.Id, dialog.Value));
    });

    private async void Import_Click(object sender, RoutedEventArgs e) => await Guard("Import package", async () =>
    {
        var game = Selected;
        var picker = new OpenFileDialog { Title = "Select a ZIP package", Filter = "ZIP archives|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var packages = new Packages(store);
        var plan = await Work("Inspecting ZIP", () => packages.Inspect(game, picker.FileName));
        var dialog = new PackageReviewDialog(this, plan);
        if (dialog.ShowDialog() == true) await Work("Importing reviewed package", () => packages.Import(game, plan, dialog.PackageName, dialog.VersionLabel, dialog.Mappings));
    });
    private async void Install_Click(object sender, RoutedEventArgs e) => await Guard("Install package", async () =>
    {
        var game = Selected; var package = PackageList.SelectedItem as Package ?? throw new InvalidOperationException("Select a package.");
        var preview = await Work("Preparing installation", () => engine.Preview(game, package.Files));
        var overlap = engine.ActiveOverlaps(game, package.Files.Select(f => f.Target));
        var dialog = new ChangeDialog(this, game, "Install package", $"Install {package.Name} / {package.Version}." + (overlap.Count > 0 ? " Replaces files used by: " + BlockerNames(game, overlap) : ""), preview,
            overlap.Count > 0 ? "Allow overlap with earlier layers; roll back in reverse order." : null);
        if (dialog.ShowDialog() == true) await Work("Installing package", () => engine.Install(game, package, dialog.Option));
    });
    private async void Rollback_Click(object sender, RoutedEventArgs e) => await Guard("Roll back package", async () =>
    {
        var game = Selected; var row = InstallationList.SelectedItem as InstallationRow ?? throw new InvalidOperationException("Select an active installation.");
        var installation = game.Installations.Single(i => i.Id == row.Id);
        var preview = await Work("Preparing rollback", () =>
        {
            var blockers = engine.RollbackBlockers(game, installation);
            if (blockers.Count > 0) throw new InvalidOperationException("First roll back these newer packages: " + BlockerNames(game, blockers));
            return engine.RollbackChanges(game, installation);
        });
        var edits = await Work("Checking installed files", () => engine.Modified(game, installation));
        var dialog = new ChangeDialog(this, game, "Roll back package", $"Restore the state before {row.Name}. Newly added files will be removed.", preview,
            edits.Count > 0 ? "Override externally edited files after saving them to a recovery snapshot." : null);
        if (dialog.ShowDialog() == true) await Work("Rolling back package", () => engine.Rollback(game, installation, dialog.Option));
    });
    private async void RenamePackage_Click(object sender, RoutedEventArgs e) => await Guard("Rename package", async () =>
    {
        var game = Selected; var package = PackageList.SelectedItem as Package ?? throw new InvalidOperationException("Select a package.");
        var dialog = new NameDialog(this, "Rename package", package.Name);
        if (dialog.ShowDialog() == true) await Work("Renaming package", () => { var next = Format.Clone(game); next.Packages.Single(p => p.Id == package.Id).Name = dialog.Value; store.Put(next); });
    });

    private async void OpenBackups_Click(object sender, RoutedEventArgs e) => await Guard("Open backups", () =>
    {
        var folder = store.PayloadRoot;
        if (page == Page.Game && selectedId != null)
        {
            var gameFolder = store.StoragePath(store.GameSnapshotFolder(Selected));
            if (Directory.Exists(gameFolder)) folder = gameFolder;
        }
        SafePaths.NoLinks(folder); Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); return Task.CompletedTask;
    });
    private async void ShowFiles_Click(object sender, RoutedEventArgs e) => await Guard("Show snapshot files", () =>
    {
        var row = SnapshotList.SelectedItem as SnapshotRow ?? throw new InvalidOperationException("Select a version.");
        var snapshot = Selected.Snapshots.Single(s => s.Id == row.Id);
        var folder = store.SnapshotPath(snapshot);
        if (!Directory.Exists(folder)) throw new IOException("Snapshot folder is unavailable.");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); return Task.CompletedTask;
    });
    private async void Releases_Click(object sender, RoutedEventArgs e) => await Guard("Open releases", () =>
    {
        Process.Start(new ProcessStartInfo("https://github.com/" + Releases.Repository + "/releases") { UseShellExecute = true }); return Task.CompletedTask;
    });
}
