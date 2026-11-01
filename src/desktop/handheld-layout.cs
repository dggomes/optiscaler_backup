using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private void AdaptLayout()
    {
        var narrow = ActualWidth < 1100;
        Grid.SetRow(TopActions, narrow ? 1 : 0); Grid.SetColumn(TopActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(TopActions, narrow ? 2 : 1);
        TopActions.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
        Grid.SetRow(LibrarySearch, narrow ? 1 : 0); Grid.SetColumn(LibrarySearch, narrow ? 0 : 1);
        Grid.SetColumnSpan(LibrarySearch, narrow ? 2 : 1);
        LibrarySearch.Margin = new Thickness(0, narrow ? 8 : 1, 0, 0);
        Grid.SetColumnSpan(LibraryFilters.Children[0], narrow ? 2 : 1);
        Grid.SetRow(SettingsSecondary, narrow ? 1 : 0); Grid.SetColumn(SettingsSecondary, narrow ? 0 : 2);
        SettingsSecondary.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
        SettingsColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
        SettingsColumns.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private async void CurrentComponents_Click(object sender, RoutedEventArgs e) => await Guard("Inspect current components", async () =>
    {
        if (busy || selectedId == null) return;
        var row = new GameRow(Selected.Id, Selected.Name, string.Join(" · ", Selected.Sources), "", "", null, false, false, 0, HoverFor(Selected));
        await InspectHover(row);
        new ConfirmationDialog(this, "Current components and settings", "Current game files and approved settings. Saved backups are inspected separately.", row.Hover.Text, "Close").ShowDialog();
    });

    private async Task CheckHandheldLayout(string screenshot)
    {
        foreach (var work in new[] { new Rect(0, 0, 1280, 752), new Rect(-1920, 40, 1920, 1040), new Rect(40, 0, 800, 480) })
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
            {
                var bounds = WindowChrome.FitBounds(new Rect(-200, -100, 1360 * scale, 860 * scale), work, true);
                if (!work.Contains(bounds)) throw new InvalidOperationException("Window placement escapes the monitor work area.");
                bounds = WindowChrome.FitBounds(new Rect(3000, 2000, 880 * scale, 600 * scale), work, false);
                if (!work.Contains(bounds)) throw new InvalidOperationException("Display-change recovery escapes the work area.");
            }
        }
        Width = 853; Height = 500; GameTabs.SelectedIndex = 1;
        await Capture(screenshot + ".handheld-files.png");
        if (ManagedList.ActualHeight < 44 || ReviewFilesButton.ActualHeight < 44 || CurrentSettingsButton.ActualHeight < 44)
            throw new InvalidOperationException("Handheld file actions or table are inaccessible.");
        page = Page.Library; Refresh(); await Capture(screenshot + ".handheld-library.png");
        if (Grid.GetRow(TopActions) != 1 || Grid.GetRow(LibrarySearch) != 1 || ScanButton.ActualHeight < 44 || FilterAll.ActualHeight < 44)
            throw new InvalidOperationException("Handheld library actions did not adapt.");
        page = Page.Settings; Refresh(); await Capture(screenshot + ".handheld-settings.png");
        if (Grid.GetRow(SettingsSecondary) != 1) throw new InvalidOperationException("Handheld settings did not stack.");
        var dialog = new NameDialog(this, "Save backup", "Handheld", offerOriginal: true);
        dialog.Width = 720; dialog.Height = 420;
        await RenderDialog(dialog, screenshot + ".handheld-original.png");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
