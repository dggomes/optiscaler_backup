using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private async void Playnite_Click(object sender, RoutedEventArgs e) => await Guard("Playnite library", async () =>
    {
        var path = store.Data.Settings.PlayniteLibraryPath ?? Playnite.DefaultExport;
        if (!File.Exists(path) || (sender as Button)?.Tag as string == "browse")
        {
            var picker = new OpenFileDialog { Title = "Choose library.json exported by the Playnite bridge", Filter = "Playnite export|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            path = picker.FileName;
        }
        var library = await Work("Reading local Playnite export", () => Playnite.Read(path));
        var dialog = new PlayniteDialog(this, library, store, Format.Clone(store.Data.Settings));
        if (dialog.ShowDialog() != true) return;
        var warnings = await Work("Importing selected Playnite games and covers", () =>
        {
            var before = Format.Clone(store.Data.Settings);
            store.Data.Settings.PlayniteLibraryPath = path;
            store.Data.Settings.PlayniteExcludePlatforms = dialog.Filters.PlayniteExcludePlatforms;
            store.Data.Settings.PlayniteExcludeTags = dialog.Filters.PlayniteExcludeTags;
            store.Data.Settings.PlayniteExcludeCategories = dialog.Filters.PlayniteExcludeCategories;
            try { store.Save(); } catch { store.Data.Settings = before; throw; }
            return Playnite.Import(store, library, path, dialog.Filters, dialog.SelectedIds, thumbnails);
        });
        artwork.Clear(); Refresh(); await LoadArtwork(); StartThumbnails();
        StatusText.Text = "Playnite import complete · " + warnings.Count + " warnings";
        if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(5)));
    });
    private void PlayniteBridge_Click(object sender, RoutedEventArgs e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/dggomes/optiscaler_backup/releases/latest") { UseShellExecute = true });
}

internal sealed class PlayniteSelection
{
    public bool Include { get; set; }
    public required PlayniteRow Row { get; init; }
    public string Name => Row.Game.Name;
    public string Status => Row.Status;
    public string Platforms => string.Join("; ", Row.Game.Platforms);
    public string Tags => string.Join("; ", Row.Game.Tags);
    public string Categories => string.Join("; ", Row.Game.Categories);
}
internal sealed class PlayniteDialog : ReviewDialog
{
    private readonly TextBox platforms = new(), tags = new(), categories = new();
    private readonly DataGrid table = Table(true);
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private List<PlayniteSelection> rows = [];
    public Settings Filters { get; }
    public List<string> SelectedIds { get; private set; } = [];
    public PlayniteDialog(Window owner, PlayniteLibrary library, Store store, Settings filters)
        : base(owner, "Playnite library comparison", "Local SDK export · " + library.ExportedUtc.ToLocalTime().ToString("g") + ". Select individual games or all eligible installed games. Backups and excluded games are preserved.", "Save filters / import selected")
    {
        Width = 1100; Height = 720; Filters = filters;
        Error.Text = string.Join(" ", (library.Warnings ?? []).Take(3).Select(warning => warning.Length > 300 ? warning[..300] : warning));
        Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new());
        var header = new StackPanel();
        void Field(string label, TextBox input, List<string> values)
        { header.Children.Add(new TextBlock { Text = label + " (names separated by ;)", Margin = new Thickness(0, 4, 0, 3) }); input.Text = string.Join("; ", values); header.Children.Add(input); }
        Field("Exclude platforms", platforms, filters.PlayniteExcludePlatforms); Field("Exclude tags", tags, filters.PlayniteExcludeTags); Field("Exclude categories", categories, filters.PlayniteExcludeCategories);
        var actions = new WrapPanel(); var all = new Button { Content = "Select all eligible" }; var clear = new Button { Content = "Clear selection" };
        all.Click += (_, _) => { foreach (var row in rows) row.Include = row.Row.Eligible; table.Items.Refresh(); };
        clear.Click += (_, _) => { foreach (var row in rows) row.Include = false; table.Items.Refresh(); };
        actions.Children.Add(all); actions.Children.Add(clear); header.Children.Add(actions); Body.Children.Add(header);
        Grid.SetRow(summary, 1); Body.Children.Add(summary); Grid.SetRow(table, 2); Body.Children.Add(table);
        table.Columns.Add(IncludeColumn("Name")); table.Columns.Add(Column("Game", "Name", 230)); table.Columns.Add(Column("Comparison", "Status", 155));
        table.Columns.Add(Column("Platforms", "Platforms", 140)); table.Columns.Add(Column("Tags", "Tags", -1)); table.Columns.Add(Column("Categories", "Categories", -1));
        void RefreshComparison()
        {
            static List<string> Names(TextBox input) => input.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Filters.PlayniteExcludePlatforms = Names(platforms); Filters.PlayniteExcludeTags = Names(tags); Filters.PlayniteExcludeCategories = Names(categories);
            var report = Playnite.Compare(library, store, Filters);
            rows = report.Rows.Select(r => new PlayniteSelection { Row = r }).ToList(); table.ItemsSource = rows;
            summary.Text = $"Playnite: {report.PlayniteTotal} total · {report.FilteredOut} filtered out · {report.Included} included ({report.Installed} installed)\nScanned library: {report.ScannedTotal} total ({report.ScannedFilteredOut} filtered out) · {report.Matched} Playnite entries matched · {report.MissingInstalled} installed entries missing · {report.Uninstalled} not installed · {report.ExcludedGames} game exclusions · {report.ScannedOnly} scanned games without an included Playnite match";
        }
        platforms.TextChanged += (_, _) => RefreshComparison(); tags.TextChanged += (_, _) => RefreshComparison(); categories.TextChanged += (_, _) => RefreshComparison(); RefreshComparison();
    }
    internal override void CheckInteraction()
    {
        var previous = platforms.Text; platforms.Text = "PC";
        if (rows.Any(r => r.Row.Game.Platforms.Contains("PC") && r.Row.Status != "Filtered out")) throw new IOException("Playnite platform filter failed.");
        platforms.Text = previous;
        var eligible = rows.FirstOrDefault(r => r.Row.Eligible);
        if (eligible != null) { eligible.Include = true; Approve(); if (SelectedIds.Count != 1) throw new IOException("Individual Playnite selection failed."); eligible.Include = false; }
        table.Items.Refresh();
    }
    protected override void Approve()
    {
        Commit(table);
        if (rows.Any(r => r.Include && !r.Row.Eligible)) throw new IOException("Only eligible installed games can be imported. Clear filtered, uninstalled or excluded selections.");
        SelectedIds = rows.Where(r => r.Include).Select(r => r.Row.Game.Id).ToList();
    }
}
