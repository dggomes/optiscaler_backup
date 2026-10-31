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

public sealed record GameRow(string Id, string Name, string Detail, string Status, string Initials, System.Windows.Media.ImageSource? Artwork);
public sealed record SnapshotRow(string Id, string Name, string Kind, string Created, int FileCount, string Size, string Components);
public sealed record InstallationRow(string Id, string Name, string Created);
public sealed record ManagedRow(string Name, string Folder, string Path, string Location);

public partial class MainWindow : Window
{
    private enum Page { Library, Game, Settings }
    private const int PageSize = 72;
    private readonly Store store;
    private readonly Engine engine;
    private readonly bool smoke;
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> artwork = [];
    private readonly Dictionary<string, int> detectedCounts = [];
    private bool busy;
    private bool refreshing;
    private string? selectedId;
    private Page page;
    private int pageIndex;
    private string filter = "all";
    private int totalMatches;
    private int artworkGeneration;
    private Game Selected => selectedId != null ? store.Game(selectedId) : throw new InvalidOperationException("Select a game first.");

    public MainWindow(Store store, Engine engine, bool smoke)
    {
        this.store = store; this.engine = engine; this.smoke = smoke;
        thumbnails = new Thumbnails(thumbnailHttp, store.Home);
        InitializeComponent(); WindowChrome.Apply(this); Refresh();
        if (!smoke) Loaded += async (_, _) => { await LoadArtwork(); await ScanLibrary(); if (thumbnailCancellation == null) StartThumbnails(); await StartupUpdateCheck(); await RefreshFingerprints(true); };
        Closed += (_, _) => { scanningCancellation.Cancel(); thumbnailCancellation?.Cancel(); thumbnailHttp.Dispose(); };
    }

    private static string CountLabel(int count, string label) => $"{count} {label}{(count == 1 ? "" : "s")}";
    private static bool HasBackup(Game game) => game.Snapshots.Any(s => !s.Recovery);
    private static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => w.EnumerateRunes().First().ToString())).ToUpperInvariant();
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";

    private void Refresh()
    {
        refreshing = true;
        try
        {
            var query = SearchBox.Text.Trim();
            var games = store.Data.Games.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(g => filter == "all" || (filter == "protected") == HasBackup(g)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            totalMatches = games.Count;
            pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, (games.Count - 1) / PageSize));
            var rows = games.Skip(pageIndex * PageSize).Take(PageSize).Select(g => new GameRow(g.Id, g.Name,
                string.Join(" · ", g.Sources), !Directory.Exists(g.Directory) ? "Drive unavailable" : HasBackup(g) ? CountLabel(g.Snapshots.Count(s => !s.Recovery), "saved backup") : "No backup yet",
                Initials(g.Name), artwork.GetValueOrDefault(g.Id))).ToList();
            GameList.ItemsSource = rows; GameList.SelectedItem = rows.FirstOrDefault(g => g.Id == selectedId);
            LibraryCount.Text = $"{store.Data.Games.Count} games in library · {totalMatches} matching";
            PageCount.Text = $"{pageIndex + 1} / {Math.Max(1, (games.Count + PageSize - 1) / PageSize)}";
            Pagination.Visibility = games.Count > PageSize ? Visibility.Visible : Visibility.Collapsed;
            PreviousPage.IsEnabled = pageIndex > 0; NextPage.IsEnabled = (pageIndex + 1) * PageSize < games.Count;
            SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GameList.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = store.Data.Games.Count == 0 ? "Find your games" : "No games match";
            EmptyDescription.Text = store.Data.Games.Count == 0 ? "Scan your installed launchers, or add a game folder. Then choose a game to save a backup."
                : "Try another name or reset the filter to see your games.";
            EmptyPrimary.Content = store.Data.Games.Count == 0 ? "Scan games" : "Reset search";
            EmptySecondary.Visibility = store.Data.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HeaderChrome.Visibility = page == Page.Game ? Visibility.Collapsed : Visibility.Visible;
            LibraryPanel.Visibility = page == Page.Library ? Visibility.Visible : Visibility.Collapsed;
            GamePanel.Visibility = page == Page.Game && selectedId != null ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            LibraryNav.IsChecked = page != Page.Settings; SettingsNav.IsChecked = page == Page.Settings;
            TopActions.Visibility = page == Page.Library ? Visibility.Visible : Visibility.Collapsed;
            PageDescription.Text = page == Page.Library ? "Choose a game to manage its backups and packages." : page == Page.Settings ? "Storage and application preferences." : "Back up, change settings, and return to a working version.";
            AutomaticUpdatesToggle.IsChecked = store.Data.Settings.CheckUpdates;
            DownloadThumbnailsToggle.IsChecked = store.Data.Settings.DownloadThumbnails;
            RetryThumbnailsButton.IsEnabled = store.Data.Settings.DownloadThumbnails;
            BackupLocationText.Text = store.PayloadRoot; VersionText.Text = "OptiScaler Backup " + Releases.DisplayVersion;
            if (selectedId != null) RefreshDetails();
        }
        finally { refreshing = false; UpdateActions(); }
    }

    private void RefreshDetails()
    {
        var game = Selected;
        var snapshotId = (SnapshotList.SelectedItem as SnapshotRow)?.Id;
        var packageId = (PackageList.SelectedItem as Package)?.Id;
        var installationId = (InstallationList.SelectedItem as InstallationRow)?.Id;
        GameTitle.Text = game.Name; PathText.Text = game.Directory; SourceText.Text = string.Join(" · ", game.Sources);
        var online = Directory.Exists(game.Directory);
        AvailabilityText.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        AvailabilityText.Text = online ? "" : "Game folder unavailable. Reconnect its drive to back up, install or restore files.";
        var count = detectedCounts.GetValueOrDefault(game.Id);
        SummaryText.Text = $"{CountLabel(game.Managed.Count, "selected file")} · {CountLabel(game.Snapshots.Count(s => !s.Recovery), "backup")} · {CountLabel(game.Installations.Count(i => i.RolledBack == null), "active package")}" + (count > 0 ? $" · {count} new files found" : "");
        SetupGuide.Visibility = online && (game.Managed.Count == 0 || !game.Snapshots.Any(s => s.Original)) ? Visibility.Visible : Visibility.Collapsed;
        SetupTitle.Text = game.Managed.Count == 0 ? "Choose backup files" : "No Original backup";
        SetupDescription.Text = game.Managed.Count == 0 ? "Review detected files and approve your selection once." : "Capture clean files before making changes.";
        SetupButton.Content = game.Managed.Count == 0 ? "Choose files…" : "Save Original…";
        var snapshots = game.Snapshots.Where(s => !s.Recovery || ShowRecovery.IsChecked == true).OrderByDescending(s => s.Created)
            .Select(s => new SnapshotRow(s.Id, s.Name, s.Original ? "Original" : s.Recovery ? "Recovery" : "Backup", s.Created.LocalDateTime.ToString("g"), s.Files.Count(f => f.Hash != null), FormatSize(s.Files.Sum(f => f.Size)), ComponentSummary(s))).ToList();
        SnapshotList.ItemsSource = snapshots; SnapshotList.SelectedItem = snapshots.FirstOrDefault(s => s.Id == snapshotId);
        var packages = game.Packages.OrderByDescending(p => p.Created).ToList();
        PackageList.ItemsSource = packages; PackageList.SelectedItem = packages.FirstOrDefault(p => p.Id == packageId);
        var installations = game.Installations.Where(i => i.RolledBack == null).Reverse().Select(i => new InstallationRow(i.Id,
            PackageLabel(game, i), i.Created.LocalDateTime.ToString("g"))).ToList();
        InstallationList.ItemsSource = installations; InstallationList.SelectedItem = installations.FirstOrDefault(i => i.Id == installationId);
        RefreshManagedFiles();
        BackupsEmpty.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PackagesEmpty.Visibility = packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallationsEmpty.Visibility = installations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string PackageLabel(Game game, Installation installation)
    {
        var package = game.Packages.FirstOrDefault(p => p.Id == installation.PackageId);
        return package == null ? installation.Id[..8] : package.Name + " / " + package.Version;
    }
    private static string BlockerNames(Game game, IEnumerable<string> ids) => string.Join(", ", ids.Select(id => PackageLabel(game, game.Installations.Single(i => i.Id == id))));

    private void ManagedSearch_Changed(object sender, TextChangedEventArgs e)
    { if (IsInitialized && selectedId != null) RefreshManagedFiles(); }
    private void RefreshManagedFiles()
    {
        var game = Selected; var query = ManagedSearch.Text.Trim();
        ManagedSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = game.Managed.Select(target => new ManagedRow(Path.GetFileName(target.Path),
                target.Root == "game" ? Path.GetDirectoryName(target.Path) is { Length: > 0 } folder ? folder : "Game root" : Path.GetDirectoryName(Physical(game, target))!,
                Physical(game, target), target.Root == "game" ? "Game folder" : "External settings"))
            .Where(row => row.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ManagedList.ItemsSource = rows;
        ManagedCountText.Text = query.Length == 0 ? CountLabel(rows.Count, "file") : $"{rows.Count} / {game.Managed.Count} files";
        FilesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesEmpty.Text = game.Managed.Count == 0 ? "No files selected. Use Review / add files to choose what to back up." : "No selected files match this filter.";
    }

    private void UpdateActions()
    {
        if (!IsInitialized || SaveBackupButton == null) return;
        var online = selectedId != null && Directory.Exists(Selected.Directory) && !busy;
        var snapshot = SnapshotList.SelectedItem != null && !busy;
        SaveBackupButton.IsEnabled = online; ReviewFilesButton.IsEnabled = online; ImportPackageButton.IsEnabled = online; SetupButton.IsEnabled = online;
        RestoreButton.IsEnabled = online && snapshot; ShowFilesButton.IsEnabled = snapshot; RenameBackupButton.IsEnabled = snapshot;
        InstallButton.IsEnabled = online && PackageList.SelectedItem != null; RenamePackageButton.IsEnabled = !busy && PackageList.SelectedItem != null;
        RollbackButton.IsEnabled = online && InstallationList.SelectedItem != null;
        BackupInsightsButton.IsEnabled = snapshot; CurrentSettingsButton.IsEnabled = !busy && selectedId != null;
        UpdateUpdateActions();
    }

    private void ActionSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void RecoveryFilter_Changed(object sender, RoutedEventArgs e) { if (IsInitialized && !refreshing && selectedId != null) { RefreshDetails(); UpdateActions(); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && !refreshing) { pageIndex = 0; Refresh(); if (!smoke) _ = LoadArtwork(); } }
    private void Game_Selected(object sender, SelectionChangedEventArgs e) { }
    private async void Game_Activated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(GameList, element) is ListBoxItem) await OpenSelectedGame();
    }
    private async void Game_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await OpenSelectedGame(); }
    }
    private async Task OpenSelectedGame()
    {
        if (busy || GameList.SelectedItem is not GameRow row) return;
        selectedId = row.Id; page = Page.Game; GameTabs.SelectedIndex = 0; ManagedSearch.Clear(); Refresh();
        if (!smoke) await Guard("Find settings", async () =>
        {
            if (!Directory.Exists(Selected.Directory)) return;
            var game = Selected; var warnings = new List<string>();
            var found = await AnalyzeFiles(game, warnings.Add);
            detectedCounts[game.Id] = found.Count; if (selectedId == game.Id && page == Page.Game) RefreshDetails();
            if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(3)));
        });
    }
    private async Task LoadArtwork()
    {
        if (busy || page != Page.Library) return;
        var generation = ++artworkGeneration;
        var missing = GameList.Items.Cast<GameRow>().Where(r => !artwork.ContainsKey(r.Id)).Select(r => Format.Clone(store.Game(r.Id))).ToList();
        var loaded = await Task.Run(() => missing.Select(g => (g.Id, Image: GameArtwork.Load(g, CachedThumbnail(g)))).ToList());
        if (generation != artworkGeneration) return;
        if (artwork.Count > 120) artwork.Clear();
        foreach (var pair in loaded) artwork[pair.Id] = pair.Image;
        if (page == Page.Library && !busy) Refresh();
    }

    private static string Physical(Game game, Target target) => Path.GetFullPath(Path.Combine(target.Root == "game" ? game.Directory : game.ExternalRoots[target.Root], target.Path));
    private void BeginWork(string operation)
    {
        if (busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        busy = true; SetWorkspaceEnabled(false); BusyProgress.Visibility = Visibility.Visible; StatusText.Text = operation + "…";
    }
    private void EndWork()
    {
        busy = false; SetWorkspaceEnabled(true); BusyProgress.Visibility = Visibility.Collapsed; Refresh();
        if (pendingDiscovery != null) ApplyDiscovery();
        if (thumbnailsPending) StartThumbnails(thumbnailsPendingRetry);
        if (updateCheckPending) { updateCheckPending = false; _ = StartupUpdateCheck(); }
    }
    private async Task<T> Work<T>(string operation, Func<T> action)
    {
        BeginWork(operation);
        try { var value = await Task.Run(action); StatusText.Text = operation + " complete."; return value; }
        finally { EndWork(); }
    }
    private Task Work(string operation, Action action) => Work(operation, () => { action(); return true; });
    private async Task WorkAsync(string operation, Func<Task> action)
    {
        BeginWork(operation);
        try { await action(); StatusText.Text = operation + " complete."; }
        finally { EndWork(); }
    }
    private async Task Guard(string operation, Func<Task> action)
    {
        if (busy) return;
        try
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            if (File.Exists(store.JournalPath)) await Work("Recovering interrupted operation", engine.Recover);
            await action();
        }
        catch (OperationCanceledException) when (scanningCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = operation + " failed.";
            ShowError(error.Message);
            try { File.AppendAllText(Path.Combine(store.Home, "errors.log"), $"{DateTimeOffset.UtcNow:o} desktop game={selectedId} operation={operation}: {error.Message}\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { StatusText.Text += " Log unavailable: " + logError.Message; }

        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; StatusText.Text = "Wait for the active operation to finish before closing."; }
    }

    public async Task SmokeTest(string screenshot)
    {
        var count = store.Data.Games.Count;
        int gameFileCount() => Selected.Managed.Count;
        SearchBox.Text = "__no_matching_game__";
        if (GameList.Items.Count != 0 || EmptyState.Visibility != Visibility.Visible) throw new InvalidOperationException("Search empty state failed.");
        await Capture(screenshot + ".empty.png");
        SearchBox.Clear(); await LoadArtwork();
        foreach (var row in GameList.Items.Cast<GameRow>())
            if (CachedThumbnail(store.Game(row.Id)) != null && row.Artwork == null) throw new InvalidOperationException("Cached thumbnail did not render.");
        if (GameList.Items.Count != Math.Min(count, PageSize)) throw new InvalidOperationException("Clearing search failed.");
        await Capture(screenshot);
        if (count >= 7) CheckLibraryDensity();
        if (count == 0) return;
        if (count > PageSize)
        {
            pageIndex = 1; Refresh();
            if (((GameRow)GameList.Items[0]).Id == store.Data.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).First().Id) throw new InvalidOperationException("Pagination failed.");
            SearchBox.Text = store.Data.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Last().Name;
            if (GameList.Items.Count != 1 || pageIndex != 0) throw new InvalidOperationException("Search must cover every page.");
            SearchBox.Clear();
        }
        GameList.SelectedIndex = 0; await OpenSelectedGame();
        if (GamePanel.Visibility != Visibility.Visible || GameTitle.Text != Selected.Name) throw new InvalidOperationException("Game navigation failed.");
        if (RestoreButton.IsEnabled || RenameBackupButton.IsEnabled || RollbackButton.IsEnabled) throw new InvalidOperationException("Selection actions should start disabled.");
        if (SnapshotList.Items.Cast<SnapshotRow>().Any(s => s.Kind == "Recovery")) throw new InvalidOperationException("Recovery filter failed.");
        ShowRecovery.IsChecked = true;
        if (SnapshotList.Items.Count != Selected.Snapshots.Count) throw new InvalidOperationException("Recovery visibility failed.");
        ShowRecovery.IsChecked = false;
        if (SnapshotList.Items.Count > 0)
        {
            SnapshotList.SelectedIndex = 0; var id = ((SnapshotRow)SnapshotList.SelectedItem).Id;
            await Work("Checking selection retention", () => true);
            if ((SnapshotList.SelectedItem as SnapshotRow)?.Id != id || !RestoreButton.IsEnabled) throw new InvalidOperationException("Refresh lost selection.");
        }
        await Capture(screenshot + ".game.png");
        CheckTabs();
        GameTabs.SelectedIndex = 1; await Capture(screenshot + ".managed.png");
        if (gameFileCount() >= 54)
        {
            ManagedSearch.Text = "graphics-01";
            if (ManagedList.Items.Count != 1 || ((ManagedRow)ManagedList.Items[0]).Name != "graphics-01.ini") throw new InvalidOperationException("Managed-file filter did not match the filename.");
            ManagedSearch.Clear();
            if (ManagedList.Items.Count != gameFileCount()) throw new InvalidOperationException("Clearing managed-file filter lost rows.");
        }
        GameTabs.SelectedIndex = 2; await Capture(screenshot + ".packages.png");
        var width = Width; var height = Height; Width = MinWidth; Height = MinHeight;
        GameTabs.SelectedIndex = 1;
        await Capture(screenshot + ".files-compact.png");
        if (ManagedList.ActualHeight < 340 || HeaderChrome.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Compact game workspace does not give files enough height.");
        if (ManagedList.Items.Cast<ManagedRow>().Any(row => Path.IsPathRooted(row.Name) || row.Name.Contains('\\') || row.Name.Contains('/'))) throw new InvalidOperationException("File names are hidden inside full paths.");
        GameTabs.SelectedIndex = 2;
        await Capture(screenshot + ".compact.png");
        if (PackageList.ActualHeight < 92 || InstallationList.ActualHeight < 92) throw new InvalidOperationException("Compact package tables clip rows.");
        Width = width; Height = height;
        page = Page.Settings; Refresh(); await Capture(screenshot + ".settings.png");
        DownloadThumbnailsToggle.IsChecked = false; await SaveThumbnailPreference();
        if (Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.DownloadThumbnails || RetryThumbnailsButton.IsEnabled) throw new InvalidOperationException("Thumbnail opt-out did not persist.");
        DownloadThumbnailsToggle.IsChecked = true; await SaveThumbnailPreference();
        if (!Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.DownloadThumbnails || !RetryThumbnailsButton.IsEnabled) throw new InvalidOperationException("Thumbnail opt-in did not persist.");
        AutomaticUpdatesToggle.IsChecked = false; await SaveUpdatePreference();
        if (Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.CheckUpdates) throw new InvalidOperationException("Update opt-out did not persist.");
        AutomaticUpdatesToggle.IsChecked = true; await SaveUpdatePreference();
        if (!Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.CheckUpdates) throw new InvalidOperationException("Update opt-in did not persist.");
        ShowUpdate(new ReleaseInfo("v99.0.0", "Update review fixture", "", "", ""));
        if (UpdateNotice.Visibility != Visibility.Visible || !InstallUpdateButton.IsEnabled) throw new InvalidOperationException("Available update controls are missing.");
        SettingsPanel.ScrollToBottom(); await Capture(screenshot + ".settings-bottom.png");
        await RenderDialog(new ConfirmationDialog(this, "Update OptiScaler Backup", "Review the version and installed components before downloading.",
            "v99.0.0 · update review fixture\n\noptibackup-ui.exe\noptibackup.exe\n\nBoth executables are verified and previous copies retained.", "Download, update and restart"), screenshot + ".update.png");
        var finish = new TaskCompletionSource();
        var asyncWork = WorkAsync("Downloading application updates", () => finish.Task);
        if (!busy || InstallUpdateButton.IsEnabled) throw new InvalidOperationException("Async updater work did not keep the installation busy.");
        var closing = new CancelEventArgs(); Window_Closing(this, closing);
        if (!closing.Cancel) throw new InvalidOperationException("Updater allowed exit before staging completed.");
        finish.SetResult(); await asyncWork;
        if (busy) throw new InvalidOperationException("Updater did not release its busy guard.");
        ShowUpdate(null);
        var game = Selected;
        var scanStarted = new TaskCompletionSource(); var finishScan = new ManualResetEventSlim();
        var scan = ScanSources("Nonblocking scan test", _ => { scanStarted.SetResult(); finishScan.Wait(TimeSpan.FromSeconds(10)); return []; });
        await scanStarted.Task;
        if (busy || !Workspace.IsEnabled || !Navigation.IsEnabled) throw new InvalidOperationException("Library scanning blocks the app.");
        var originalDialog = new NameDialog(this, "Save Original", "Original", offerOriginal: true, preferOriginal: true);
        originalDialog.CheckInteraction(); var originalLabel = originalDialog.Value; var originalFlag = originalDialog.Original;
        try { await Work("Saving Original after an earlier backup", () => engine.CreateSnapshot(store.Game(game.Id), originalLabel, originalFlag, originalFlag, engine.PrepareSnapshot(store.Game(game.Id)))); }
        finally { finishScan.Set(); }
        await scan; finishScan.Dispose();
        if (store.Game(game.Id).Snapshots.Count(snapshot => snapshot.Original) != 1) throw new InvalidOperationException("Original creation after a regular backup failed.");
        page = Page.Game; Refresh(); GameTabs.SelectedIndex = 0;
        game = Selected;
        await RenderDialog(new FileReviewDialog(this, store, game, FindFiles(game)), screenshot + ".files.png");
        await RenderDialog(new NameDialog(this, "Save backup", "Original", offerOriginal: true, preferOriginal: true), screenshot + ".version.png");
        if (game.Snapshots.FirstOrDefault(s => !s.Recovery) is { } insightSnapshot)
        {
            var components = Fingerprints.Identify(store, insightSnapshot);
            var graphics = GameSettingsReader.Snapshot(store, insightSnapshot);
            if (!components.Identified.SelectMany(i => i.Matches).Any(m => m.Component == "FSR" && m.Version == "4.1.2.4555")
                || !components.Identified.SelectMany(i => i.Matches).Any(m => m.Component == "OptiPatcher" && m.Version == "0.42"))
                throw new InvalidOperationException("Real captured FSR/OptiPatcher fixtures were not recognized.");
            if (!graphics.Observations.Any(o => o.Setting == "FSR quality preference" && o.Value == "Native AA") || !graphics.Observations.Any(o => o.Value == "2560 px"))
                throw new InvalidOperationException("Captured Unreal graphics preferences were not extracted.");
            await RenderDialog(new BackupInsightsDialog(this, insightSnapshot.Name, components, graphics), screenshot + ".insights.png");
            if (!graphics.Observations.Any(o => o.Setting == "Game frame generation: DLSS" && o.Value == "On")
                || !graphics.Observations.Any(o => o.Setting == "OptiScaler FG output" && o.Value == "XeSS frame generation"))
                throw new InvalidOperationException("Captured game and OptiScaler frame-generation preferences were not separated.");
            var gameSettingsDialog = new BackupInsightsDialog(this, "Captured graphics settings", null, graphics);
            gameSettingsDialog.SelectSettingsScope("Game");
            await RenderDialog(gameSettingsDialog, screenshot + ".insights.png.settings.png");
            var optiSettingsDialog = new BackupInsightsDialog(this, "Captured OptiScaler settings", null, graphics);
            optiSettingsDialog.SelectSettingsScope("OptiScaler");
            await RenderDialog(optiSettingsDialog, screenshot + ".insights.png.optiscaler.png");
        }

        if (game.Snapshots.FirstOrDefault() is { } snapshot)
            await RenderDialog(new ChangeDialog(this, game, "Restore backup", "Review recorded file changes.", engine.Preview(game, snapshot.Files)), screenshot + ".changes.png");
        if (game.Packages.FirstOrDefault() is { } package)
        {
            var plan = new ImportPlan(package.ArchiveHash, package.Mappings.Select(m => new ImportEntry(m.Entry, m.Hash, m.Size, m.Target.Path, [m.Target.Path])).ToList());
            await RenderDialog(new PackageReviewDialog(this, plan), screenshot + ".package.png");
        }
        await RenderDialog(new ConfirmationDialog(this, "Move backup storage", "Copies are verified before switching locations.", store.PayloadRoot, "Copy and switch location"), screenshot + ".location.png");
        var selected = selectedId;
        page = Page.Library; Refresh(); SearchBox.Text = "__no_matching_game__";
        if (selectedId != selected) throw new InvalidOperationException("Library search discarded the current game.");
        SearchBox.Clear();
        var offline = store.Data.Games.FirstOrDefault(g => !Directory.Exists(g.Directory));
        if (offline != null)
        {
            selectedId = offline.Id; page = Page.Game; Refresh();
            if (SaveBackupButton.IsEnabled || ImportPackageButton.IsEnabled || ReviewFilesButton.IsEnabled) throw new InvalidOperationException("Offline mutation actions enabled.");
            await Capture(screenshot + ".offline.png");
        }
    }

    private void CheckLibraryDensity()
    {
        var cards = Enumerable.Range(0, Math.Min(12, GameList.Items.Count)).Select(i => (ListBoxItem)GameList.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
        var firstY = cards[0].TranslatePoint(new Point(), GameList).Y;
        var columns = cards.Count(card => Math.Abs(card.TranslatePoint(new Point(), GameList).Y - firstY) < 1);
        if (columns < 7) throw new InvalidOperationException("Compact library must fit at least seven portrait cards per row.");
        if (GameList.Items[0] is GameRow { Artwork: BitmapSource cover } && cover.PixelHeight <= cover.PixelWidth)
            throw new InvalidOperationException("Expected a real portrait cover, not a cropped landscape thumbnail.");
    }

    private void CheckTabs()
    {
        for (var i = 0; i < GameTabs.Items.Count; i++)
        {
            var tab = (TabItem)GameTabs.Items[i];
            var presenter = FindPresenter(tab);
            var label = new System.Windows.Media.FormattedText(tab.Header.ToString()!, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new System.Windows.Media.Typeface(tab.FontFamily, tab.FontStyle, tab.FontWeight, tab.FontStretch),
                tab.FontSize, (System.Windows.Media.Brush)FindResource("Ink"), 1);
            if (presenter == null || presenter.ActualWidth + 1 < label.Width) throw new InvalidOperationException("Tab label is clipped: " + tab.Header);
        }
        static ContentPresenter? FindPresenter(DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ContentPresenter presenter) return presenter;
                if (FindPresenter(child) is { } found) return found;
            }
            return null;
        }
    }

    private async Task Capture(string file)
    {
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout(); SaveImage(Shell, file);
    }

    private static async Task RenderDialog(Window dialog, string file)
    {
        dialog.Show();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        dialog.UpdateLayout();
        if (dialog is ReviewDialog review) review.CheckInteraction();
        if (dialog is NameDialog name) name.ValidateLayout();
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
