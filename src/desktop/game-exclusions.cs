using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private void GameContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        var row = e.CursorLeft >= 0 && Mouse.DirectlyOver is DependencyObject element
            && ItemsControl.ContainerFromElement(GameList, element) is ListBoxItem item ? item.Content as GameRow : GameList.SelectedItem as GameRow;
        e.Handled = true;
        if (row == null || busy) return;
        GameList.SelectedItem = row;
        var menu = new ContextMenu { PlacementTarget = GameList };
        var remove = new MenuItem { Header = "Remove and exclude…", Tag = row.Id };
        remove.Click += ExcludeGame_Click; menu.Items.Add(remove); menu.IsOpen = true;
    }
    private async void ExcludeGame_Click(object sender, RoutedEventArgs e) => await Guard("Exclude game", async () =>
    {
        var id = (sender as MenuItem)?.Tag as string ?? selectedId ?? throw new InvalidOperationException("Select a game.");
        var game = store.Game(id);
        var dialog = new ConfirmationDialog(this, "Remove and exclude " + game.Name,
            "This installation stays hidden on future scans. Restore it from Settings → Excluded games.",
            "Game files, backups, Original, packages and installation history are retained.\n\n" + game.Directory, "Remove and exclude");
        if (dialog.ShowDialog() != true) return;
        await Work("Saving exclusion", () => store.SetExcluded(id, true));
        artwork.Remove(id); selectedId = null; page = Page.Library; Refresh(); StartThumbnails();
    });
    private async void UnexcludeGame_Click(object sender, RoutedEventArgs e) => await Guard("Restore excluded game", async () =>
    {
        var id = (sender as Button)?.Tag as string ?? throw new InvalidOperationException("Select an excluded game.");
        await Work("Restoring game to library", () => store.SetExcluded(id, false));
        Refresh(); StartThumbnails();
    });
}
