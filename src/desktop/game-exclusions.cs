using System.Windows;
using System.Windows.Controls;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
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
