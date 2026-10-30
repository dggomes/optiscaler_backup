using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public sealed record GameRow(string Id, string Name, string Detail);
public sealed record SnapshotRow(string Id, string Name, string Kind, string Created);
public sealed record InstallationRow(string Id, string Name, string Created);
public sealed record ManagedRow(string Path, string Location);

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly Engine engine;
    private readonly bool smoke;
    private bool busy;
    private bool refreshing;
    private string? selectedId;
    private Game Selected => selectedId != null ? store.Game(selectedId) : throw new InvalidOperationException("Select a game first.");

    public MainWindow(Store store, Engine engine, bool smoke)
    {
        this.store = store; this.engine = engine; this.smoke = smoke;
        InitializeComponent(); Refresh();
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            var query = SearchBox.Text.Trim();
            var rows = store.Data.Games.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Name)
                .Select(g => new GameRow(g.Id, g.Name, Directory.Exists(g.Directory) ? string.Join(" · ", g.Sources) : "Drive unavailable")).ToList();
            GameList.ItemsSource = rows; GameList.SelectedItem = rows.FirstOrDefault(g => g.Id == selectedId);
            LibraryCount.Text = $"{store.Data.Games.Count} games · {rows.Count} shown";
            EmptyTitle.Text = store.Data.Games.Count == 0 ? "Start with your library." : rows.Count == 0 ? "No matching games." : "Choose a game.";
            EmptyDescription.Text = store.Data.Games.Count == 0 ? "Scan your installed launchers, or add a game folder. Select a game to review files, save versions and install packages."
                : rows.Count == 0 ? "Try a different name or clear the search to show your library." : "Select a game from the library to review files and return to a working version.";
            if (selectedId != null && rows.All(g => g.Id != selectedId)) selectedId = null;
            RefreshDetails();
        }
        finally { refreshing = false; }
    }

    private void RefreshDetails()
    {
        EmptyState.Visibility = selectedId == null ? Visibility.Visible : Visibility.Collapsed;
        GamePanel.Visibility = selectedId == null ? Visibility.Collapsed : Visibility.Visible;
        if (selectedId == null) return;
        var game = Selected;
        GameTitle.Text = game.Name; PathText.Text = game.Directory; SourceText.Text = string.Join(" / ", game.Sources).ToUpperInvariant();
        AvailabilityText.Text = Directory.Exists(game.Directory) ? "" : "This game folder is unavailable. Reconnect its drive before making changes.";
        SummaryText.Text = $"{game.Managed.Count} managed files · {game.Snapshots.Count(s => !s.Recovery)} saved versions · {game.Installations.Count(i => i.RolledBack == null)} active package layers";
        SnapshotList.ItemsSource = game.Snapshots.OrderByDescending(s => s.Created).Select(s => new SnapshotRow(s.Id, s.Name, s.Original ? "Original" : s.Recovery ? "Recovery" : "Version", s.Created.LocalDateTime.ToString("g"))).ToList();
        PackageList.ItemsSource = game.Packages.OrderByDescending(p => p.Created).ToList();
        InstallationList.ItemsSource = game.Installations.Where(i => i.RolledBack == null).Reverse().Select(i => new InstallationRow(i.Id,
            game.Packages.FirstOrDefault(p => p.Id == i.PackageId)?.Name ?? i.PackageId, i.Created.LocalDateTime.ToString("g"))).ToList();
        ManagedList.ItemsSource = game.Managed.Select(t => new ManagedRow(Physical(game, t), t.Root == "game" ? "Game folder" : "External settings")).ToList();
    }

    private static string Physical(Game game, Target target) => Path.GetFullPath(Path.Combine(target.Root == "game" ? game.Directory : game.ExternalRoots[target.Root], target.Path));
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && store != null) Refresh(); }
    private async void Game_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || busy) return;
        selectedId = (GameList.SelectedItem as GameRow)?.Id; RefreshDetails();
        if (selectedId == null || smoke) return;
        await Guard("Detect managed files", async () =>
        {
            var game = Selected;
            var warnings = new List<string>();
            var rows = await Work("Finding standard files", () => FindFiles(game, warnings.Add));
            SummaryText.Text += $" · {rows.Count} new files to review";
            if (warnings.Count > 0) StatusText.Text = string.Join(" ", warnings.Take(3));
        });
    }

    private async Task<T> Work<T>(string operation, Func<T> action)
    {
        if (busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        busy = true; Shell.IsEnabled = false; BusyProgress.Visibility = Visibility.Visible; StatusText.Text = operation + "…";
        try { var value = await Task.Run(action); StatusText.Text = operation + " complete."; return value; }
        finally { busy = false; Shell.IsEnabled = true; BusyProgress.Visibility = Visibility.Collapsed; Refresh(); }
    }
    private Task Work(string operation, Action action) => Work(operation, () => { action(); return true; });
    private async Task Guard(string operation, Func<Task> action)
    {
        if (busy) return;
        try
        {
            if (File.Exists(store.JournalPath)) await Work("Recovering interrupted operation", engine.Recover);
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = operation + ": " + error.Message;
            try { File.AppendAllText(Path.Combine(store.Home, "errors.log"), $"{DateTimeOffset.UtcNow:o} desktop game={selectedId} operation={operation}: {error.Message}\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { StatusText.Text += " Log unavailable: " + logError.Message; }
            if (!smoke) MessageBox.Show(this, error.Message, operation, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Guard("Scan library", async () =>
    {
        var warnings = new List<string>();
        await Work("Scanning installed launchers", () => { var discovery = new Discovery(warnings.Add); discovery.Merge(store, discovery.Scan([])); });
        if (warnings.Count > 0) StatusText.Text = "Scan finished. " + string.Join(" ", warnings.Take(3));
    });
    private async void AddGame_Click(object sender, RoutedEventArgs e) => await Guard("Add game", async () =>
    {
        var folder = new OpenFolderDialog { Title = "Choose a game folder or game library" };
        if (folder.ShowDialog(this) != true) return;
        if (SafePaths.Within(folder.FolderName, store.Home) || SafePaths.Within(folder.FolderName, store.PayloadRoot)) throw new IOException("Keep game folders separate from application storage.");
        var warnings = new List<string>();
        var found = await Work("Scanning folder", () => new Discovery(warnings.Add).Custom(folder.FolderName));
        if (found.Count == 0)
        {
            var name = new NameDialog(this, "Add game", Path.GetFileName(folder.FolderName));
            if (name.ShowDialog() != true) return;
            found.Add(new(name.Value, folder.FolderName, "Manual"));
        }
        await Work("Adding games", () => new Discovery(warnings.Add).Merge(store, found));
    });

    private async void Detect_Click(object sender, RoutedEventArgs e) => await Guard("Review managed files", async () =>
    {
        var game = Selected;
        var warnings = new List<string>();
        var rows = await Work("Finding standard files", () => FindFiles(game, warnings.Add));
        if (warnings.Count > 0) StatusText.Text = string.Join(" ", warnings.Take(3));
        var dialog = new FileReviewDialog(this, store, game, rows);
        if (dialog.ShowDialog() == true && dialog.Proposal is { } proposal) await Work("Saving managed selection", () => store.Put(proposal));
    });
    private static List<SelectionRow> FindFiles(Game game, Action<string>? warning = null)
    {
        if (!Directory.Exists(game.Directory)) throw new IOException("Game directory is unavailable.");
        var existing = game.Managed.Select(t => Physical(game, t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = Detection.Files(game, warning).Where(f => !existing.Contains(Path.GetFullPath(Path.Combine(game.Directory, f.Path)))).Select(f => new SelectionRow
        { Path = f.Path, Role = f.Role, Evidence = f.ReviewRequired ? "Review: " + f.Evidence : f.Evidence }).ToList();
        result.AddRange(Detection.ExternalCandidates(game, warning).Where(f => !existing.Contains(f)).Select(f => new SelectionRow { Path = f, External = true, Role = "External settings", Evidence = "Approve this full path" }));
        return result;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await Guard("Save version", async () =>
    {
        var game = Selected;
        var dialog = new NameDialog(this, "Save version", offerOriginal: !game.Snapshots.Any(s => s.Original));
        if (dialog.ShowDialog() == true) await Work("Capturing version", () => engine.CreateSnapshot(game, dialog.Value, dialog.Original, dialog.Original));
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Guard("Restore version", async () =>
    {
        var game = Selected;
        var row = SnapshotList.SelectedItem as SnapshotRow ?? throw new InvalidOperationException("Select a saved version.");
        var snapshot = game.Snapshots.Single(s => s.Id == row.Id);
        var preview = await Work("Preparing restore", () =>
        {
            var desired = engine.SnapshotFiles(game, snapshot);
            var blockers = engine.ActiveOverlaps(game, desired.Select(f => f.Target));
            if (blockers.Count > 0) throw new InvalidOperationException("Roll back overlapping package layers first: " + string.Join(", ", blockers));
            return engine.Preview(game, desired);
        });
        var dialog = new ChangeDialog(this, game, "Restore version", $"Restore {snapshot.Name}. Only recorded files change; explicit absence removes a file. Inspect older binaries after game updates.", preview);
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
        var dialog = new ChangeDialog(this, game, "Install package", $"Install {package.Name} / {package.Version}.", preview,
            overlap.Count > 0 ? "Allow overlap with earlier layers; roll back in reverse order." : null);
        if (dialog.ShowDialog() == true) await Work("Installing package", () => engine.Install(game, package, dialog.Option));
    });
    private async void Rollback_Click(object sender, RoutedEventArgs e) => await Guard("Roll back package", async () =>
    {
        var game = Selected; var row = InstallationList.SelectedItem as InstallationRow ?? throw new InvalidOperationException("Select an active installation.");
        var installation = game.Installations.Single(i => i.Id == row.Id);
        var preview = await Work("Preparing rollback", () => engine.RollbackChanges(game, installation));
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
        if (selectedId != null)
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
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; StatusText.Text = "Wait for the active operation to finish before closing."; }
    }

    public async Task SmokeTest(string screenshot)
    {
        var count = store.Data.Games.Count;
        SearchBox.Text = "__no_matching_game__";
        if (GameList.Items.Count != 0) throw new InvalidOperationException("Search binding failed.");
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout(); SaveImage(Shell, screenshot + ".empty.png");
        SearchBox.Clear();
        if (GameList.Items.Count != count) throw new InvalidOperationException("Clearing search failed.");
        if (count > 0)
        {
            GameList.SelectedIndex = 0;
            if (GamePanel.Visibility != Visibility.Visible || GameTitle.Text != ((GameRow)GameList.SelectedItem).Name)
                throw new InvalidOperationException("Game selection binding failed.");
        }
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        if (count > 0 && SnapshotList.Items.Count > 0 && SnapshotList.Columns[0].ActualWidth <= 0) throw new InvalidOperationException("Version columns did not finish layout.");
        SaveImage(Shell, screenshot);
        if (count == 0) return;
        var game = Selected;
        var files = new FileReviewDialog(this, store, game, FindFiles(game));
        await RenderDialog(files, screenshot + ".files.png");
        await RenderDialog(new NameDialog(this, "Save version", offerOriginal: true), screenshot + ".version.png");
        if (game.Snapshots.FirstOrDefault() is { } snapshot)
            await RenderDialog(new ChangeDialog(this, game, "Restore version", "Review recorded file changes.", engine.Preview(game, snapshot.Files)), screenshot + ".changes.png");
        if (game.Packages.FirstOrDefault() is { } package)
        {
            var plan = new ImportPlan(package.ArchiveHash, package.Mappings.Select(m => new ImportEntry(m.Entry, m.Hash, m.Size, m.Target.Path, [m.Target.Path])).ToList());
            await RenderDialog(new PackageReviewDialog(this, plan), screenshot + ".package.png");
        }
    }

    private static async Task RenderDialog(Window dialog, string file)
    {
        dialog.Show();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        dialog.UpdateLayout();
        SaveImage((FrameworkElement)dialog.Content, file);
        dialog.Close();
    }

    private static void SaveImage(FrameworkElement visual, string screenshot)
    {
        SafePaths.NoLinks(screenshot);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(screenshot); encoder.Save(stream);
    }
}
