using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Desktop;

internal abstract class ReviewDialog : Window
{
    protected Grid Body { get; } = new();
    protected readonly TextBlock Error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    protected Button ApproveButton { get; private set; } = null!;
    protected ReviewDialog(Window owner, string title, string description, string approve)
    {
        Owner = owner; Title = title; Width = 880; Height = 600; MinWidth = 700; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = owner.FontFamily; FontSize = 13;
        Background = (Brush)owner.FindResource("Canvas"); Foreground = (Brush)owner.FindResource("Ink");
        Error.Foreground = (Brush)owner.FindResource("ErrorInk"); WindowChrome.Apply(this);
        Resources.MergedDictionaries.Add(owner.Resources);
        var root = new Grid();
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 22, FontFamily = new FontFamily("Bahnschrift") });
        header.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)owner.FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(header); Grid.SetRow(Body, 1); root.Children.Add(Body);
        Grid.SetRow(Error, 2); root.Children.Add(Error);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = ApproveButton = new Button { Content = approve, Style = (Style)owner.FindResource("Primary") };
        accept.Click += (_, _) =>
        {
            try { Approve(); DialogResult = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { Error.Text = e.Message; }
        };
        footer.Children.Add(cancel); footer.Children.Add(accept); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = new Border { Padding = new Thickness(18), Background = Background, Child = root };
    }
    protected abstract void Approve();
    internal virtual void CheckInteraction() { }
    protected static DataGrid Table(bool editable = false) => new() { IsReadOnly = !editable, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, AutoGenerateColumns = false };
    protected static DataGridTextColumn Column(string title, string field, double width, bool readOnly = true, string? tooltip = null)
    {
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(ToolTipProperty, new Binding(tooltip ?? field)));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        text.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        text.Setters.Add(new Setter(MarginProperty, new Thickness(7, 0, 7, 0)));
        return new()
        {
            Header = title,
            Binding = new Binding(field) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            IsReadOnly = readOnly,
            ElementStyle = text
        };
    }
    protected static DataGridTemplateColumn IncludeColumn(string field = "Path")
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("Include") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(field));
        return new DataGridTemplateColumn { Header = "Use", Width = 46, CellTemplate = new DataTemplate { VisualTree = box } };
    }
    protected static void Commit(DataGrid table) { table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true); }
}

internal sealed class NameDialog : ReviewDialog
{
    private readonly TextBox name;
    private readonly CheckBox original = new() { Content = "Establish as Original", Margin = new Thickness(0, 14, 0, 8) };
    private readonly CheckBox clean = new() { Content = "I confirm these files are clean / unmodified", Margin = new Thickness(0, 0, 0, 10) };
    public string Value { get; private set; } = "";
    public bool Original { get; private set; }
    public NameDialog(Window owner, string title, string value = "", bool offerOriginal = false, bool preferOriginal = false) : base(owner, title, "Use a name you will recognize when returning to this version.", "Save")
    {
        Width = 680; MinWidth = 620; Height = offerOriginal ? 360 : 280; MinHeight = offerOriginal ? 340 : 260;
        var panel = new StackPanel(); name = new() { Text = value, MaxLength = 160 };
        panel.Children.Add(new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 7) }); panel.Children.Add(name);
        if (offerOriginal) { panel.Children.Add(original); panel.Children.Add(clean); }
        Body.Children.Add(panel);
        original.IsChecked = preferOriginal && offerOriginal; clean.IsEnabled = original.IsChecked == true;
        void Validate()
        {
            clean.IsEnabled = original.IsChecked == true;
            ApproveButton.IsEnabled = !string.IsNullOrWhiteSpace(name.Text) && (original.IsChecked != true || clean.IsChecked == true);
        }
        name.TextChanged += (_, _) => Validate(); original.Checked += (_, _) => Validate(); original.Unchecked += (_, _) => Validate();
        clean.Checked += (_, _) => Validate(); clean.Unchecked += (_, _) => Validate(); Validate();
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
    protected override void Approve()
    {
        var selectedOriginal = original.IsChecked == true;
        if (selectedOriginal && clean.IsChecked != true) throw new InvalidOperationException("Confirm that the current files are clean before establishing Original.");
        Value = Format.Label(name.Text); Original = selectedOriginal;
    }

    internal override void CheckInteraction()
    {
        if (original.Parent == null) return;
        name.Text = "Original"; original.IsChecked = true; clean.IsChecked = false;
        if (ApproveButton.IsEnabled) throw new InvalidOperationException("Original must require clean confirmation.");
        clean.IsChecked = true;
        if (!ApproveButton.IsEnabled) throw new InvalidOperationException("Clean Original cannot be saved.");
        name.Clear();
        if (ApproveButton.IsEnabled) throw new InvalidOperationException("Blank backup name accepted.");
        name.Text = "Original"; clean.IsChecked = true; Approve();
        if (!Task.Run(() => Original && Value == "Original").GetAwaiter().GetResult()) throw new InvalidOperationException("Approved Original values cannot be read by the background worker.");
        clean.IsChecked = false;
    }
    public void ValidateLayout()
    {
        if (clean.Parent != null && clean.TranslatePoint(new Point(0, clean.ActualHeight), Body).Y > Body.ActualHeight)
            throw new InvalidOperationException("Original confirmation is clipped.");
    }
}

internal sealed class SelectionRow
{
    public bool Include { get; set; } = true;
    public string Path { get; set; } = "";
    public string Role { get; set; } = "";
    public string Evidence { get; set; } = "";
    public bool External { get; set; }
    public Target? Existing { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

internal sealed class FileReviewDialog : ReviewDialog
{
    private readonly ObservableCollection<SelectionRow> rows;
    private readonly DataGrid table = Table(true);
    private readonly Store store;
    private readonly Game game;
    public Game? Proposal { get; private set; }
    public FileReviewDialog(Window owner, Store store, Game game, IEnumerable<SelectionRow> candidates)
        : base(owner, "Review managed files", "Selected originals are copied. Later inventory backups also capture changes and previously tracked paths. Unchecking changes this selection; saved backups remain intact. Review proxies and external paths, then approve once.", "Manage selected files")
    {
        this.store = store; this.game = game;
        rows = new(game.Managed.Select(t => new SelectionRow { Path = t.Root == "game" ? t.Path : SafePaths.Resolve(game, t), Existing = t, Role = "Already managed", Evidence = "Included in future backups" }).Concat(candidates));
        Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new());
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var add = new Button { Content = "Add files…" }; add.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Title = "Select additional settings or libraries", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) foreach (var file in picker.FileNames) rows.Add(new() { Path = file, External = true, Role = "Additional file", Evidence = "Selected explicitly" });
        };
        var relative = new TextBox { Width = 290, ToolTip = "Game-relative path; may be absent to record Original absence" };
        var addPath = new Button { Content = "Add relative path" }; addPath.Click += (_, _) =>
        {
            try { rows.Add(new() { Path = SafePaths.Relative(relative.Text.Trim()), Role = "Additional path", Evidence = "May be absent" }); relative.Clear(); }
            catch (InvalidOperationException e) { Error.Text = e.Message; }
        };
        tools.Children.Add(add); tools.Children.Add(relative); tools.Children.Add(addPath); Body.Children.Add(tools);
        table.ItemsSource = rows;
        table.Columns.Add(IncludeColumn());
        table.Columns.Add(Column("File name", "Name", 190, tooltip: "Path")); table.Columns.Add(Column("Folder", "Folder", -1, tooltip: "Path")); table.Columns.Add(Column("Type", "Role", 125)); table.Columns.Add(Column("Evidence", "Evidence", 200));
        Grid.SetRow(table, 1); Body.Children.Add(table);
    }
    internal override void CheckInteraction()
    {
        if (rows.Count < 2) return;
        table.UpdateLayout();
        var first = (DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0);
        CheckBox? FindCheck(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); if (child is CheckBox box) return box; if (FindCheck(child) is { } nested) return nested; }
            return null;
        }
        var checkbox = FindCheck(first) ?? throw new InvalidOperationException("File selection checkbox unavailable.");
        var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(checkbox);
        var toggle = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
        toggle.Toggle();
        if (rows[0].Include) throw new InvalidOperationException("Single-click file selection failed.");
        Approve();
        if (Proposal!.Managed.Any(t => t == rows[0].Existing)) throw new InvalidOperationException("Untracking did not update proposed files.");
        if (Proposal.Snapshots.Count != game.Snapshots.Count) throw new InvalidOperationException("Untracking changed saved snapshots.");
        toggle.Toggle();
    }
    protected override void Approve()
    {
        Commit(table);
        var included = rows.Where(r => r.Include).ToList();
        var selected = included.Where(r => r.Existing == null).Select(r => new FileRequest(r.Path, r.External)).ToList();
        if (included.Count == 0) throw new InvalidOperationException("Select at least one file or cancel.");
        var next = Format.Clone(game); next.Managed = included.Where(r => r.Existing != null).Select(r => r.Existing!).ToList();
        Proposal = ManagedFiles.Prepare(next, selected, store.Home, store.PayloadRoot);
    }
}

internal sealed class ChangeDialog : ReviewDialog
{
    private readonly CheckBox option = new() { Margin = new Thickness(0, 15, 0, 0) };
    private readonly bool required;
    public bool Option { get; private set; }
    public ChangeDialog(Window owner, Game game, string title, string detail, List<Change> changes, string? requiredOption = null)
        : base(owner, title, detail + " A recovery snapshot is saved before changes.", "Confirm " + title.ToLowerInvariant())
    {
        required = requiredOption != null;
        Body.RowDefinitions.Add(new()); Body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var table = Table();
        table.Columns.Add(Column("Change", "Action", 90)); table.Columns.Add(Column("File name", "Name", 190, tooltip: "Path")); table.Columns.Add(Column("Folder", "Folder", -1, tooltip: "Path"));
        table.ItemsSource = changes.Select(c => { var path = SafePaths.Resolve(game, c.Target); return new { c.Action, Name = Path.GetFileName(path), Folder = Path.GetDirectoryName(path), Path = path }; }); Body.Children.Add(table);
        if (required) { option.Content = requiredOption; Grid.SetRow(option, 1); Body.Children.Add(option); }
    }
    protected override void Approve()
    {
        var selected = option.IsChecked == true;
        if (required && !selected) throw new InvalidOperationException("Explicitly approve the file conflict above, or cancel.");
        Option = selected;
    }
    internal override void CheckInteraction()
    {
        option.IsChecked = required; Approve();
        if (Task.Run(() => Option).GetAwaiter().GetResult() != required) throw new InvalidOperationException("Approved conflict options cannot be read by a worker.");
    }
}

internal sealed class MappingRow
{
    public bool Include { get; set; } = true;
    public string Entry { get; init; } = "";
    public string Destination { get; set; } = "";
    public string Candidates { get; init; } = "";
}

internal sealed class PackageReviewDialog : ReviewDialog
{
    private readonly DataGrid table = Table(true);
    private readonly List<MappingRow> rows;
    private readonly TextBox name = new();
    private readonly TextBox version = new();
    public string PackageName { get; private set; } = "";
    public string VersionLabel { get; private set; } = "";
    public Dictionary<string, string?> Mappings { get; private set; } = [];
    public PackageReviewDialog(Window owner, ImportPlan plan) : base(owner, "Review ZIP destinations", "Each destination is relative to this game. Resolve ambiguous entries or uncheck them. Import saves the package; installation is a separate preview.", "Import package")
    {
        rows = plan.Files.Select(e => new MappingRow { Entry = e.Entry, Destination = e.SuggestedPath ?? "", Candidates = string.Join(", ", e.Candidates) }).ToList();
        Body.RowDefinitions.Add(new() { Height = GridLength.Auto }); Body.RowDefinitions.Add(new());
        var labels = new Grid { Margin = new Thickness(0, 0, 0, 10) }; labels.ColumnDefinitions.Add(new()); labels.ColumnDefinitions.Add(new() { Width = new GridLength(190) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 15, 0) }; left.Children.Add(new TextBlock { Text = "Package name" }); left.Children.Add(name);
        var right = new StackPanel(); right.Children.Add(new TextBlock { Text = "Version label" }); right.Children.Add(version); Grid.SetColumn(right, 1);
        labels.Children.Add(left); labels.Children.Add(right); Body.Children.Add(labels);
        table.ItemsSource = rows; table.Columns.Add(IncludeColumn("Entry"));
        table.Columns.Add(Column("Archive entry", "Entry", 210)); table.Columns.Add(Column("Destination — edit here", "Destination", -1, false)); table.Columns.Add(Column("Existing matches", "Candidates", 180));
        Grid.SetRow(table, 1); Body.Children.Add(table);
    }
    protected override void Approve()
    {
        Commit(table); PackageName = Format.Label(name.Text); VersionLabel = Format.Label(version.Text);
        Mappings = rows.ToDictionary(r => r.Entry, r => r.Include ? SafePaths.Relative(r.Destination.Trim()) : null);
        if (Mappings.Values.All(v => v == null)) throw new InvalidOperationException("Include at least one entry.");
        SafePaths.DistinctDestinations(Mappings.Values.Where(v => v != null).Select(v => Path.Combine(Path.GetTempPath(), v!)));
    }
}

internal sealed class ConfirmationDialog : ReviewDialog
{
    public ConfirmationDialog(Window owner, string title, string description, string detail, string approve) : base(owner, title, description, approve)
    {
        Height = 470;
        Body.Children.Add(new ScrollViewer { Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, LineHeight = 20 } });
    }
    protected override void Approve() { }
}
