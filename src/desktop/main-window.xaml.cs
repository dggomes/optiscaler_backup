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
public sealed record SnapshotRow(string Id, string Name, string Kind, string Created, int FileCount, string Size);
public sealed record InstallationRow(string Id, string Name, string Created);
public sealed record ManagedRow(string Path, string Location);

public partial class MainWindow : Window
{
    private enum Page { Library, Game, Settings }
    private const int PageSize = 24;
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
        if (!smoke) Loaded += async (_, _) => { await LoadArtwork(); await ScanLibrary(); if (thumbnailCancellation == null) StartThumbnails(); };
        Closed += (_, _) => { thumbnailCancellation?.Cancel(); thumbnailHttp.Dispose(); };
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
            LibraryPanel.Visibility = page == Page.Library ? Visibility.Visible : Visibility.Collapsed;
            GamePanel.Visibility = page == Page.Game && selectedId != null ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            LibraryNav.IsChecked = page != Page.Settings; SettingsNav.IsChecked = page == Page.Settings;
            TopActions.Visibility = page == Page.Library ? Visibility.Visible : Visibility.Collapsed;
            PageDescription.Text = page == Page.Library ? "Choose a game to manage its backups and packages." : page == Page.Settings ? "Storage and application preferences." : "Back up, change settings, and return to a working version.";
            DownloadThumbnailsToggle.IsChecked = store.Data.Settings.DownloadThumbnails;
            RetryThumbnailsButton.IsEnabled = store.Data.Settings.DownloadThumbnails;
            BackupLocationText.Text = store.PayloadRoot; VersionText.Text = "OptiScaler Backup " + Releases.Current.ToString(3);
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
        AvailabilityText.Text = online ? "" : "Game folder unavailable. Reconnect its drive to back up, install or restore files.";
        var count = detectedCounts.GetValueOrDefault(game.Id);
        SummaryText.Text = $"{CountLabel(game.Managed.Count, "selected file")} · {CountLabel(game.Snapshots.Count(s => !s.Recovery), "backup")} · {CountLabel(game.Installations.Count(i => i.RolledBack == null), "active package")}" + (count > 0 ? $" · {count} new files found" : "");
        SetupGuide.Visibility = online && (game.Managed.Count == 0 || !game.Snapshots.Any(s => s.Original)) ? Visibility.Visible : Visibility.Collapsed;
        SetupTitle.Text = game.Managed.Count == 0 ? "Choose what to protect" : "Keep a clean starting point";
        SetupDescription.Text = game.Managed.Count == 0 ? "Review detected settings and libraries, add others if needed, then approve once." : "Save an Original backup before making changes. Confirm Original only when the current files are clean.";
        SetupButton.Content = game.Managed.Count == 0 ? "Choose files…" : "Save Original…";
        var snapshots = game.Snapshots.Where(s => !s.Recovery || ShowRecovery.IsChecked == true).OrderByDescending(s => s.Created)
            .Select(s => new SnapshotRow(s.Id, s.Name, s.Original ? "Original" : s.Recovery ? "Recovery" : "Backup", s.Created.LocalDateTime.ToString("g"), s.Files.Count(f => f.Hash != null), FormatSize(s.Files.Sum(f => f.Size)))).ToList();
        SnapshotList.ItemsSource = snapshots; SnapshotList.SelectedItem = snapshots.FirstOrDefault(s => s.Id == snapshotId);
        var packages = game.Packages.OrderByDescending(p => p.Created).ToList();
        PackageList.ItemsSource = packages; PackageList.SelectedItem = packages.FirstOrDefault(p => p.Id == packageId);
        var installations = game.Installations.Where(i => i.RolledBack == null).Reverse().Select(i => new InstallationRow(i.Id,
            PackageLabel(game, i), i.Created.LocalDateTime.ToString("g"))).ToList();
        InstallationList.ItemsSource = installations; InstallationList.SelectedItem = installations.FirstOrDefault(i => i.Id == installationId);
        ManagedList.ItemsSource = game.Managed.Select(t => new ManagedRow(Physical(game, t), t.Root == "game" ? "Game folder" : "External settings")).ToList();
        BackupsEmpty.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesEmpty.Visibility = game.Managed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PackagesEmpty.Visibility = packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallationsEmpty.Visibility = installations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string PackageLabel(Game game, Installation installation)
    {
        var package = game.Packages.FirstOrDefault(p => p.Id == installation.PackageId);
        return package == null ? installation.Id[..8] : package.Name + " / " + package.Version;
    }
    private static string BlockerNames(Game game, IEnumerable<string> ids) => string.Join(", ", ids.Select(id => PackageLabel(game, game.Installations.Single(i => i.Id == id))));

    private void GameLayout_Changed(object sender, SizeChangedEventArgs e)
    {
        if (GameTabs == null || GameScroll.ActualHeight <= 0 || GamePanel.Visibility != Visibility.Visible) return;
        var header = GamePanel.RowDefinitions.Take(3).Sum(row => row.ActualHeight);
        var height = Math.Max(400, GameScroll.ActualHeight - header);
        if (Math.Abs(GameTabs.Height - height) > 0.5) GameTabs.Height = height;
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
        selectedId = row.Id; page = Page.Game; GameTabs.SelectedIndex = 0; GameScroll.ScrollToTop(); Refresh();
        if (!smoke) await Guard("Find settings", async () =>
        {
            if (!Directory.Exists(Selected.Directory)) return;
            var game = Selected; var warnings = new List<string>();
            var found = await Work("Finding standard files", () => FindFiles(game, warnings.Add));
            detectedCounts[game.Id] = found.Count; RefreshDetails();
            if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(3)));
        });
    }
    private async Task LoadArtwork()
    {
        if (smoke || busy || page != Page.Library) return;
        var generation = ++artworkGeneration;
        var missing = GameList.Items.Cast<GameRow>().Where(r => !artwork.ContainsKey(r.Id)).Select(r => Format.Clone(store.Game(r.Id))).ToList();
        var loaded = await Task.Run(() => missing.Select(g => (g.Id, Image: GameArtwork.Load(g, CachedThumbnail(g)))).ToList());
        if (generation != artworkGeneration) return;
        if (artwork.Count > 120) artwork.Clear();
        foreach (var pair in loaded) artwork[pair.Id] = pair.Image;
        if (page == Page.Library && !busy) Refresh();
    }

    private static string Physical(Game game, Target target) => Path.GetFullPath(Path.Combine(target.Root == "game" ? game.Directory : game.ExternalRoots[target.Root], target.Path));
    private async Task<T> Work<T>(string operation, Func<T> action)
    {
        if (busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        busy = true; SetWorkspaceEnabled(false); BusyProgress.Visibility = Visibility.Visible; StatusText.Text = operation + "…";
        try { var value = await Task.Run(action); StatusText.Text = operation + " complete."; return value; }
        finally { busy = false; SetWorkspaceEnabled(true); BusyProgress.Visibility = Visibility.Collapsed; Refresh(); if (thumbnailsPending) StartThumbnails(thumbnailsPendingRetry); }
    }
    private Task Work(string operation, Action action) => Work(operation, () => { action(); return true; });
    private async Task Guard(string operation, Func<Task> action)
    {
        if (busy) return;
        try
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            if (File.Exists(store.JournalPath)) await Work("Recovering interrupted operation", engine.Recover);
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
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
        SearchBox.Text = "__no_matching_game__";
        if (GameList.Items.Count != 0 || EmptyState.Visibility != Visibility.Visible) throw new InvalidOperationException("Search empty state failed.");
        await Capture(screenshot + ".empty.png");
        SearchBox.Clear();
        if (GameList.Items.Count != Math.Min(count, PageSize)) throw new InvalidOperationException("Clearing search failed.");
        await Capture(screenshot);
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
        GameTabs.SelectedIndex = 2; await Capture(screenshot + ".packages.png");
        var width = Width; var height = Height; Width = MinWidth; Height = MinHeight;
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        GameScroll.ScrollToBottom();
        await Capture(screenshot + ".compact.png");
        if (PackageList.ActualHeight < 92 || InstallationList.ActualHeight < 92) throw new InvalidOperationException("Compact package tables clip rows.");
        Width = width; Height = height; GameScroll.ScrollToTop();
        page = Page.Settings; Refresh(); await Capture(screenshot + ".settings.png");
        DownloadThumbnailsToggle.IsChecked = false; await SaveThumbnailPreference();
        if (Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.DownloadThumbnails || RetryThumbnailsButton.IsEnabled) throw new InvalidOperationException("Thumbnail opt-out did not persist.");
        DownloadThumbnailsToggle.IsChecked = true; await SaveThumbnailPreference();
        if (!Store.Read<Catalog>(Path.Combine(store.Home, "catalog.json")).Settings.DownloadThumbnails || !RetryThumbnailsButton.IsEnabled) throw new InvalidOperationException("Thumbnail opt-in did not persist.");
        SettingsPanel.ScrollToBottom(); await Capture(screenshot + ".settings-bottom.png");
        page = Page.Game; Refresh(); GameTabs.SelectedIndex = 0;
        var game = Selected;
        await RenderDialog(new FileReviewDialog(this, store, game, FindFiles(game)), screenshot + ".files.png");
        await RenderDialog(new NameDialog(this, "Save backup", "Original", offerOriginal: true, preferOriginal: true), screenshot + ".version.png");
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
