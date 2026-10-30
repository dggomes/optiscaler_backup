using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private ReleaseInfo? availableUpdate;
    private bool checkingUpdates;
    private bool updateCheckPending;

    private void UpdateUpdateActions()
    {
        if (CheckUpdatesButton == null) return;
        CheckUpdatesButton.IsEnabled = !busy && !checkingUpdates;
        InstallUpdateButton.IsEnabled = BannerInstallUpdateButton.IsEnabled = !busy && !checkingUpdates && availableUpdate != null && UpdateHost.CanSelfUpdate;
        if (!UpdateHost.CanSelfUpdate) InstallUpdateButton.ToolTip = "Self-update requires a published Windows executable. Development builds can check releases only.";
    }
    private void ShowUpdate(ReleaseInfo? release)
    {
        availableUpdate = release != null && UpdateHost.NeedsUpdate(release) ? release : null;
        UpdateNotice.Visibility = availableUpdate != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoticeText.Text = availableUpdate == null ? "" : $"{availableUpdate.Tag} is available. Review the release notes before installing.";
        UpdateStatusText.Text = availableUpdate == null ? "No newer stable release is available for this installation." : $"{availableUpdate.Tag} is available. Installed CLI/UI companions can update together.";
        UpdateUpdateActions();
    }
    private async Task StartupUpdateCheck()
    {
        if (smoke) return;
        if (busy) { updateCheckPending = true; return; }
        try
        {
            if (UpdateHost.LastFailure(store.Home) is { } failure) ShowError(failure);
            var cached = UpdateHost.LastRelease(store.Home);
            if (cached != null) ShowUpdate(cached);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { Trace.WriteLine("Cached update check: " + error.Message); }
        if (!store.Data.Settings.CheckUpdates || store.Data.Settings.LastUpdateCheck > DateTimeOffset.UtcNow.AddDays(-1)) return;
        await CheckUpdates(startup: true);
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Guard("Check updates", () => CheckUpdates(startup: false));
    private async Task CheckUpdates(bool startup)
    {
        if (busy || checkingUpdates) return;
        checkingUpdates = true; UpdateUpdateActions(); UpdateStatusText.Text = "Checking GitHub Releases…";
        try
        {
            store.Data.Settings.LastUpdateCheck = DateTimeOffset.UtcNow; store.Save();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var release = await new Releases(client, Releases.UiAsset).Check(includeCurrent: true);
            UpdateHost.RememberRelease(store.Home, release); ShowUpdate(release);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            UpdateStatusText.Text = "Update check unavailable. Retry when online. " + error.Message;
            if (!startup) ShowError(UpdateStatusText.Text);
            Trace.WriteLine(UpdateStatusText.Text);
        }
        finally { checkingUpdates = false; UpdateUpdateActions(); }
    }
    private async void UpdatePreference_Click(object sender, RoutedEventArgs e) => await SaveUpdatePreference();
    private Task SaveUpdatePreference() => Guard("Save update preference", async () =>
    {
        store.Data.Settings.CheckUpdates = AutomaticUpdatesToggle.IsChecked == true;
        await Work("Saving update preference", store.Save);
    });
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await Guard("Install application update", async () =>
    {
        var release = availableUpdate ?? throw new InvalidOperationException("Check for an available update first.");
        var targets = UpdateHost.Targets();
        var dialog = new ConfirmationDialog(this, "Update OptiScaler Backup", "Downloads are verified before closing this session. Installed companions update together; all previous executables are restored if any health check fails.",
            "Release " + release.Tag + "\n\nExecutables to update\n" + string.Join("\n", targets.Select(target => target.Destination)) + "\n\nRelease notes\n" + release.Notes, "Download, update and restart");
        if (dialog.ShowDialog() != true) return;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await WorkAsync("Downloading and verifying application updates", () => UpdateHost.Prepare(store, new Releases(client, Releases.UiAsset), release));
        System.Windows.Application.Current.Shutdown();
    });
}
