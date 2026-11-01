using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private void AdaptLayout()
    {
        var narrow = ActualWidth < 800;
        var stackSettings = ActualWidth < 1100;
        Grid.SetRow(TopActions, narrow ? 1 : 0); Grid.SetColumn(TopActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(TopActions, narrow ? 2 : 1);
        TopActions.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
        Grid.SetRow(LibrarySearch, narrow ? 1 : 0); Grid.SetColumn(LibrarySearch, narrow ? 0 : 1);
        Grid.SetColumnSpan(LibrarySearch, narrow ? 2 : 1);
        LibrarySearch.Margin = new Thickness(0, narrow ? 8 : 1, 0, 0);
        Grid.SetColumnSpan(LibraryFilters.Children[0], narrow ? 2 : 1);
        Grid.SetRow(SettingsSecondary, stackSettings ? 1 : 0); Grid.SetColumn(SettingsSecondary, stackSettings ? 0 : 2);
        SettingsSecondary.Margin = new Thickness(0, stackSettings ? 12 : 0, 0, 0);
        SettingsColumns.ColumnDefinitions[1].Width = new GridLength(stackSettings ? 0 : 16);
        SettingsColumns.ColumnDefinitions[2].Width = stackSettings ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private bool inspectingCurrent;
    private async void CurrentComponents_Click(object sender, RoutedEventArgs e) => await Guard("Inspect current components", async () =>
    {
        if (busy || inspectingCurrent || selectedId == null) return;
        inspectingCurrent = true;
        try
        {
            var row = new GameRow(Selected.Id, Selected.Name, string.Join(" · ", Selected.Sources), "", "", null, false, false, 0, HoverFor(Selected));
            if (!await InspectHover(row) || busy || page != Page.Game || selectedId != row.Id) return;
            new ConfirmationDialog(this, "Current components and settings", "Current game files and approved settings. Saved backups are inspected separately.", row.Hover.Text, "Close").ShowDialog();
        }
        finally { inspectingCurrent = false; }
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
        if (Grid.GetRow(TopActions) != 0 || Grid.GetRow(LibrarySearch) != 0 || ScanButton.ActualHeight < 44 || FilterAll.ActualHeight < 44)
            throw new InvalidOperationException("Handheld library actions did not adapt.");
        Width = 720;
        await Capture(screenshot + ".handheld-narrow.png");
        if (Grid.GetRow(TopActions) != 1 || Grid.GetRow(LibrarySearch) != 1)
            throw new InvalidOperationException("Narrow library actions did not reflow.");
        page = Page.Settings; Refresh(); await Capture(screenshot + ".handheld-settings.png");
        if (Grid.GetRow(SettingsSecondary) != 1) throw new InvalidOperationException("Handheld settings did not stack.");
        var dialog = new NameDialog(this, "Save backup", "Handheld", offerOriginal: true);
        dialog.Width = 720; dialog.Height = 420;
        await RenderDialog(dialog, screenshot + ".handheld-original.png");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
