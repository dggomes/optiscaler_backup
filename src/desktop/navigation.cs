using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private void SetWorkspaceEnabled(bool enabled)
    {
        Workspace.IsEnabled = enabled; TopActions.IsEnabled = enabled; Navigation.IsEnabled = enabled;
        UpdateActions();
    }
    private void ShowError(string message) { ErrorText.Text = message; ErrorBanner.Visibility = Visibility.Visible; }
    private void DismissError_Click(object sender, RoutedEventArgs e) => ErrorBanner.Visibility = Visibility.Collapsed;
    private async void Library_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        page = Page.Library; Refresh(); await LoadArtwork();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) { if (!busy) { page = Page.Settings; Refresh(); } }
    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        filter = (sender as RadioButton)?.Tag as string ?? "all"; pageIndex = 0; Refresh(); await LoadArtwork();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private async void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (store.Data.Games.Count == 0) { Scan_Click(sender, e); return; }
        filter = "all"; FilterAll.IsChecked = true; pageIndex = 0; SearchBox.Clear(); Refresh(); await LoadArtwork();
    }
    private async void PreviousPage_Click(object sender, RoutedEventArgs e) { if (pageIndex > 0) { pageIndex--; Refresh(); await LoadArtwork(); } }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { if ((pageIndex + 1) * PageSize < totalMatches) { pageIndex++; Refresh(); await LoadArtwork(); } }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (busy) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { Library_Click(sender, e); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && page != Page.Library) { Library_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { Scan_Click(sender, e); e.Handled = true; }
    }
    private async void ScanOptions_Click(object sender, RoutedEventArgs e) => await Guard("Scan folder", async () =>
    {
        var folder = new OpenFolderDialog { Title = "Scan a game-library folder" };
        if (folder.ShowDialog(this) != true) return;
        var warnings = new List<string>();
        await Work("Scanning folder", () => { var discovery = new OptiScalerBackup.Windows.Discovery(warnings.Add); discovery.Merge(store, discovery.Custom(folder.FolderName)); });
        if (warnings.Count > 0) ShowError(string.Join(" ", warnings.Take(3)));
        await LoadArtwork();
    });
    private async void ChangeLocation_Click(object sender, RoutedEventArgs e) => await Guard("Change backup location", async () =>
    {
        var picker = new OpenFolderDialog { Title = "Choose the new backup location" };
        if (picker.ShowDialog(this) != true) return;
        var dialog = new ConfirmationDialog(this, "Move backup storage", "All saved files are copied and verified before switching locations. Original copies are retained.",
            "Current location\n" + store.PayloadRoot + "\n\nNew location\n" + picker.FolderName, "Copy and switch location");
        if (dialog.ShowDialog() == true) await Work("Copying and verifying backups", () => store.RelocatePayloads(picker.FolderName));
    });
    private async void CopyBackupPath_Click(object sender, RoutedEventArgs e) => await Guard("Copy backup path", () =>
    { Clipboard.SetText(store.PayloadRoot); StatusText.Text = "Backup path copied."; return Task.CompletedTask; });
    private async void OpenData_Click(object sender, RoutedEventArgs e) => await Guard("Open app data", () =>
    { Process.Start(new ProcessStartInfo(store.Home) { UseShellExecute = true }); return Task.CompletedTask; });
}
