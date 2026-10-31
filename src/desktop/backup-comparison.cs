using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private async void CompareBackups_Click(object sender, RoutedEventArgs e) => await Guard("Compare backups", async () =>
    {
        var dialog = new BackupComparisonDialog(this, store, Format.Clone(Selected), (SnapshotList.SelectedItem as SnapshotRow)?.Id);
        BeginWork("Comparing recorded backup metadata");
        try { await dialog.Prepare(); } finally { EndWork(); }
        dialog.ShowDialog();
    });
}
internal sealed record BackupChoice(string Id, string Label);
internal sealed class BackupComparisonDialog : ReviewDialog
{
    private readonly Store store;
    private readonly Game game;
    private readonly ComboBox before = new(), after = new(), filter = new();
    private readonly TextBox search = new() { MinWidth = 220 };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly DataGrid table = Table();
    private readonly TextBlock empty = new() { Text = "No files match these filters. Try All or clear the path search.", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly Button swap = new() { Content = "Swap" };
    private BackupComparison? report;
    private bool comparing;
    public BackupComparisonDialog(Window owner, Store store, Game game, string? selected)
        : base(owner, "Compare backups", "From → To, using recorded hashes and verified inventories. No live game files are read or modified. Saved-byte labels describe capture coverage; restore separately verifies payload bytes.", "Close")
    {
        this.store = store; this.game = game; Width = 1200; Height = 720;
        Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new());
        var header = new StackPanel(); var versions = new Grid();
        versions.ColumnDefinitions.Add(new()); versions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); versions.ColumnDefinitions.Add(new());
        var choices = game.Snapshots.OrderBy(s => s.Created).Select(s => new BackupChoice(s.Id, s.Name + " · " + s.Created.LocalDateTime.ToString("g") + " · " + s.Id[..Math.Min(8, s.Id.Length)])).ToList();
        foreach (var box in new[] { before, after }) { box.ItemsSource = choices; box.DisplayMemberPath = "Label"; box.MinWidth = 200; box.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("SelectedItem.Label") { Source = box }); }
        after.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices.LastOrDefault();
        var index = after.SelectedItem is BackupChoice current ? choices.IndexOf(current) : choices.Count - 1;
        before.SelectedItem = choices.Take(Math.Max(0, index)).LastOrDefault() ?? choices.FirstOrDefault(c => c.Id != (after.SelectedItem as BackupChoice)?.Id);
        var left = new StackPanel(); System.Windows.Automation.AutomationProperties.SetName(before, "From backup"); left.Children.Add(new TextBlock { Text = "From backup", Margin = new Thickness(0, 0, 0, 5) }); left.Children.Add(before); versions.Children.Add(left);
        swap.Margin = new Thickness(10, 22, 10, 0); Grid.SetColumn(swap, 1); versions.Children.Add(swap);
        var right = new StackPanel(); System.Windows.Automation.AutomationProperties.SetName(after, "To backup"); right.Children.Add(new TextBlock { Text = "To backup", Margin = new Thickness(0, 0, 0, 5) }); right.Children.Add(after); Grid.SetColumn(right, 2); versions.Children.Add(right); header.Children.Add(versions);
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        filter.ItemsSource = new[] { "Changes", "All", "Added", "Removed", "Changed", "Unknown coverage", "Unchanged" }; filter.SelectedIndex = 0; filter.MinWidth = 150; System.Windows.Automation.AutomationProperties.SetName(filter, "Filter changes"); System.Windows.Automation.AutomationProperties.SetName(search, "Search file paths");
        var hashes = new CheckBox { Content = "Show hashes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(hashes, Dock.Right); tools.Children.Add(hashes);
        DockPanel.SetDock(filter, Dock.Left); tools.Children.Add(filter); tools.Children.Add(new TextBlock { Text = "Search path", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) }); tools.Children.Add(search); header.Children.Add(tools);
        Body.Children.Add(header); Grid.SetRow(summary, 1); Body.Children.Add(summary); var results = new Grid(); results.Children.Add(table); results.Children.Add(empty); Grid.SetRow(results, 2); Body.Children.Add(results);
        table.Columns.Add(Column("Change", "Change", 110)); table.Columns.Add(Column("File", "File", -1));
        table.Columns.Add(Column("From: size / coverage", "Before", 165)); table.Columns.Add(Column("To: size / coverage", "After", 165));
        var beforeHash = Column("From SHA-256", "BeforeHash", 180); var afterHash = Column("To SHA-256", "AfterHash", 180);
        beforeHash.Visibility = afterHash.Visibility = Visibility.Collapsed;
        table.Columns.Add(beforeHash); table.Columns.Add(afterHash);
        hashes.Checked += (_, _) => beforeHash.Visibility = afterHash.Visibility = Visibility.Visible;
        hashes.Unchecked += (_, _) => beforeHash.Visibility = afterHash.Visibility = Visibility.Collapsed;
        table.Columns[1].MinWidth = 240;
        before.SelectionChanged += async (_, _) => { if (!comparing) await Prepare(); };
        after.SelectionChanged += async (_, _) => { if (!comparing) await Prepare(); };
        swap.Click += async (_, _) =>
        {
            comparing = true; var old = before.SelectedItem; before.SelectedItem = after.SelectedItem; after.SelectedItem = old; comparing = false; await Prepare();
        };
        filter.SelectionChanged += (_, _) => ShowRows(); search.TextChanged += (_, _) => ShowRows();
    }
    internal void SelectVersions(string beforeId, string afterId)
    {
        comparing = true;
        before.SelectedItem = before.Items.Cast<BackupChoice>().Single(choice => choice.Id == beforeId);
        after.SelectedItem = after.Items.Cast<BackupChoice>().Single(choice => choice.Id == afterId);
        comparing = false;
    }
    internal async Task Prepare()
    {
        if (comparing || before.SelectedItem is not BackupChoice first || after.SelectedItem is not BackupChoice second) return;
        comparing = true; before.IsEnabled = after.IsEnabled = swap.IsEnabled = false; Error.Text = ""; summary.Text = "Comparing recorded file states…"; empty.Visibility = Visibility.Collapsed; table.ItemsSource = null; report = null;
        try
        {
            report = await Task.Run(() => BackupComparer.Compare(store, game, first.Id, second.Id));
            summary.Text = $"{report.Added} added · {report.Removed} removed · {report.Changed} changed · {report.Unchanged} unchanged · {report.Unknown} unknown coverage\n" +
                "Unknown coverage is not deletion. Inventories establish absence only outside excluded link paths; missing legacy/external metadata stays unknown.";
            ShowRows();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { report = null; summary.Text = "Comparison unavailable"; Error.Text = error.Message; }
        finally { comparing = false; before.IsEnabled = after.IsEnabled = swap.IsEnabled = true; }
    }
    private void ShowRows()
    {
        if (report == null) return;
        var selected = filter.SelectedItem as string ?? "Changes"; var query = search.Text.Trim().Replace('\\', '/');
        table.ItemsSource = report.Files.Where(file => selected == "All" || selected == "Changes" && file.Change != "Unchanged" || file.Change == selected)
            .Where(file => file.Target.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(file => new
            {
                file.Change,
                File = file.Target.Root + ":" + file.Target.Path,
                Before = file.BeforeHash == null ? file.BeforeCoverage : $"{file.BeforeSize:N0} B · {file.BeforeCoverage}",
                After = file.AfterHash == null ? file.AfterCoverage : $"{file.AfterSize:N0} B · {file.AfterCoverage}",
                file.BeforeHash,
                file.AfterHash
            }).ToList();
        empty.Visibility = table.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    protected override void Approve() { }
    internal override void CheckInteraction()
    {
        if (report == null) throw new IOException("Backup comparison failed: " + Error.Text);
        filter.ApplyTemplate();
        var comboPeer = new System.Windows.Automation.Peers.ComboBoxAutomationPeer(filter);
        var dropdown = (System.Windows.Automation.Provider.IExpandCollapseProvider)comboPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
        dropdown.Expand(); filter.UpdateLayout();
        ((filter.Template.FindName("PART_Popup", filter) as System.Windows.Controls.Primitives.Popup)?.Child as FrameworkElement)?.UpdateLayout();
        var changed = (ComboBoxItem?)filter.ItemContainerGenerator.ContainerFromItem("Changed") ?? throw new IOException("Change filter dropdown did not generate selectable items.");
        var itemPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(changed) ?? throw new IOException("Change filter item has no accessibility peer.");
        ((System.Windows.Automation.Provider.ISelectionItemProvider)itemPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
        dropdown.Collapse();
        if (filter.SelectedItem as string != "Changed" || table.Items.Count != report.Changed) throw new IOException("Changed-file comparison filter failed.");
        search.Text = "no-such-path-" + Guid.NewGuid(); if (table.Items.Count != 0 || empty.Visibility != Visibility.Visible) throw new IOException("Comparison path search failed.");
        search.Clear(); filter.SelectedItem = "Changes";
    }
}
