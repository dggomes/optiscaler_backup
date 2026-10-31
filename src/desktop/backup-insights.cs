using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private ComponentFingerprints? fingerprints;
    private readonly Dictionary<string, BackupComponents> componentReports = [];
    private readonly HashSet<string> identifyingSnapshots = [];
    private bool refreshingFingerprints;
    private readonly SemaphoreSlim componentAnalysisGate = new(1);
    private ComponentFingerprints Fingerprints => fingerprints ??= ComponentFingerprints.Load(store.Home);
    private string ComponentSummary(Snapshot snapshot)
    {
        if (componentReports.TryGetValue(snapshot.Id, out var report)) return report.Summary;
        if (identifyingSnapshots.Add(snapshot.Id)) _ = IdentifyBackup(Format.Clone(snapshot));
        return "Identifying recorded components…";
    }
    private async Task IdentifyBackup(Snapshot snapshot)
    {
        var entered = false; var update = false;
        try
        {
            await componentAnalysisGate.WaitAsync(scanningCancellation.Token); entered = true;
            var catalog = Fingerprints;
            var report = await Task.Run(() => catalog.Identify(store, snapshot), scanningCancellation.Token);
            if (scanningCancellation.IsCancellationRequested) return;
            if (catalog.Sha256 == Fingerprints.Sha256) componentReports[snapshot.Id] = report;
            update = true;
        }
        catch (OperationCanceledException) when (scanningCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace.WriteLine("Identify backup " + snapshot.Id + ": " + error.Message); }
        finally
        {
            identifyingSnapshots.Remove(snapshot.Id); if (entered) componentAnalysisGate.Release();
            if (update && !scanningCancellation.IsCancellationRequested && page == Page.Game && !busy && Selected.Snapshots.Any(s => s.Id == snapshot.Id)) RefreshDetails();
        }
    }
    private async void BackupInsights_Click(object sender, RoutedEventArgs e) => await Guard("Backup components and settings", async () =>
    {
        var game = Selected;
        var row = SnapshotList.SelectedItem as SnapshotRow ?? throw new InvalidOperationException("Select a backup.");
        var snapshot = Format.Clone(game.Snapshots.Single(s => s.Id == row.Id)); var catalog = Fingerprints;
        var result = await Work("Reading recorded components and settings", () => (Components: catalog.Identify(store, snapshot), Settings: GameSettingsReader.Snapshot(store, snapshot)));
        new BackupInsightsDialog(this, snapshot.Name, result.Components, result.Settings).ShowDialog();
    });
    private async void CurrentSettings_Click(object sender, RoutedEventArgs e) => await Guard("Read game settings", async () =>
    {
        var game = Format.Clone(Selected);
        var settings = await Work("Reading approved settings", () => GameSettingsReader.Current(game));
        new BackupInsightsDialog(this, "Current approved settings", null, settings).ShowDialog();
    });
    private async void RefreshFingerprints_Click(object sender, RoutedEventArgs e) => await Guard("Refresh recognition catalog", () => RefreshFingerprints(false));
    private async Task RefreshFingerprints(bool startup)
    {
        if (refreshingFingerprints || smoke) return;
        if (startup)
        {
            if (!store.Data.Settings.CheckUpdates) return;
            var checkedFile = Path.Combine(store.Home, "components", "checked.json");
            try
            {
                SafePaths.NoLinks(checkedFile);
                if (File.Exists(checkedFile))
                {
                    if (new FileInfo(checkedFile).Length > 16384) return;
                    var check = Store.Read<FingerprintCheck>(checkedFile);
                    if (check.Version != 1 || check.CheckedUtc > DateTimeOffset.UtcNow.AddDays(-1)) return;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { Trace.WriteLine("Recognition check cache: " + error.Message); }
        }
        refreshingFingerprints = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            fingerprints = await Fingerprints.Refresh(client, store.Home, scanningCancellation.Token);
            componentReports.Clear();
            StatusText.Text = $"Recognition catalog · {fingerprints.Document.Records.Count} historical fingerprints";
            if (page == Page.Game && !busy) RefreshDetails();
        }
        catch (OperationCanceledException) when (scanningCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
        { Trace.WriteLine("Recognition catalog refresh: " + error.Message); if (!startup) ShowError(error.Message); }
        finally { refreshingFingerprints = false; }
    }
}

internal sealed class BackupInsightsDialog : ReviewDialog
{
    protected override void Approve() { }
    public BackupInsightsDialog(Window owner, string name, BackupComponents? components, GameSettingsReport settings)
        : base(owner, name, "Recorded file fingerprints and saved preferences. File presence is not proof of runtime enablement. Uncaptured settings cannot be reconstructed from inventory hashes.", "Close")
    {
        Width = 1050; Height = 650;
        var tabs = new TabControl(); Body.Children.Add(tabs);
        if (components != null)
        {
            var rows = components.Identified.SelectMany(file => file.Matches.Select(match => new
            {
                File = file.Target.Root + ":" + file.Target.Path,
                Component = match.Component + " " + match.Role,
                Version = match.Component == "FSR" ? "File " + match.Version : match.Version,
                Scope = file.BytesBackedUp ? "Saved bytes" : "Inventory only",
                Source = match.Source + " · " + match.ReleaseTag + " · " + match.AssetUrl,
                Hash = file.Hash
            })).Concat(components.Unknown.Select(file => new
            {
                File = file.Target.Root + ":" + file.Target.Path,
                Component = file.Component,
                Version = "Unknown / custom build",
                Scope = file.BytesBackedUp ? "Saved bytes" : "Inventory only",
                Source = "No SHA-256 catalog match",
                Hash = file.Hash
            })).ToList();
            var table = Table(); table.ItemsSource = rows;
            table.Columns.Add(Column("File", "File", 220)); table.Columns.Add(Column("Component", "Component", 150));
            table.Columns.Add(Column("Version/build", "Version", 170)); table.Columns.Add(Column("Coverage", "Scope", 110)); table.Columns.Add(Column("Source / release", "Source", -1));
            var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
            panel.Children.Add(new TextBlock { Text = components.Coverage + ". Matches use captured hashes; restoration separately verifies payload integrity. " + string.Join(" ", components.Warnings), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            Grid.SetRow(table, 1); panel.Children.Add(table); tabs.Items.Add(new TabItem { Header = $"Components ({rows.Count})", Content = panel });
        }
        tabs.Items.Add(SettingsTab("Graphics settings", settings, "Game"));
        tabs.Items.Add(SettingsTab("OptiScaler settings", settings, "OptiScaler"));
    }
    internal void SelectSettingsScope(string scope)
    {
        if (Body.Children.OfType<TabControl>().SingleOrDefault() is { } tabs)
            tabs.SelectedIndex = tabs.Items.Count - (scope == "OptiScaler" ? 1 : 2);
    }
    private static TabItem SettingsTab(string title, GameSettingsReport settings, string scope)
    {
        var observations = settings.Observations.Where(o => o.Scope == scope).ToList();
        var settingsTable = Table(); settingsTable.ItemsSource = observations.Select(o => new
        {
            Setting = o.Setting.StartsWith(scope + " ", StringComparison.Ordinal) ? o.Setting[(scope.Length + 1)..] : o.Setting,
            SavedValue = o.Value == o.RawValue ? o.Value : o.Value + " [raw: " + o.RawValue + "]",
            Location = o.File + ":" + o.Line + " · " + o.Layer,
            Key = "[" + o.Section + "] " + o.Key,
            Interpretation = o.Confidence + " · " + o.Source
        }).ToList();
        settingsTable.Columns.Add(Column("Setting", "Setting", 165)); settingsTable.Columns.Add(Column("Saved value", "SavedValue", 190));
        settingsTable.Columns.Add(Column("File / line / layer", "Location", 200)); settingsTable.Columns.Add(Column("Section + key", "Key", 160)); settingsTable.Columns.Add(Column("Interpretation / source", "Interpretation", -1));
        var settingsPanel = new Grid(); settingsPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); settingsPanel.RowDefinitions.Add(new());
        settingsPanel.Children.Add(new TextBlock { Text = (observations.Count == 0 ? "No captured/approved " + title.ToLowerInvariant() + " were found." : new GameSettingsReport(observations, []).Summary.Replace(scope + " ", "", StringComparison.Ordinal) + ".") + " Saved preferences; runtime selection is not measured. " + string.Join(" ", settings.Warnings), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Grid.SetRow(settingsTable, 1); settingsPanel.Children.Add(settingsTable);
        return new TabItem { Header = $"{title} ({observations.Count})", Content = settingsPanel };
    }
}
