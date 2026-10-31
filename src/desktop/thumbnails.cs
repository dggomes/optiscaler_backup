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
    private readonly HttpClient thumbnailHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Thumbnails thumbnails;
    private CancellationTokenSource? thumbnailCancellation;
    private int thumbnailGeneration;
    private bool thumbnailsPending;
    private bool thumbnailsPendingRetry;

    private string? CachedThumbnail(Game game)
    {
        try { return thumbnails.Cached(game); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        { Trace.WriteLine($"Thumbnail cache game={game.Id}: {error.Message}"); return null; }
    }

    private void StartThumbnails(bool retry = false)
    {
        if (busy) { thumbnailsPending = true; thumbnailsPendingRetry |= retry; return; }
        thumbnailsPending = false; thumbnailsPendingRetry = false;
        thumbnailCancellation?.Cancel();
        var generation = ++thumbnailGeneration;
        if (smoke || !store.Data.Settings.DownloadThumbnails)
        { ArtworkStatus.Text = store.Data.Settings.DownloadThumbnails ? "" : "Online thumbnails off"; return; }
        var games = store.VisibleGames.Select(Format.Clone).ToList();
        if (games.Count == 0) { ArtworkStatus.Text = ""; return; }
        var source = thumbnailCancellation = new();
        ArtworkStatus.Text = $"Checking thumbnails · 0 / {games.Count}";
        _ = DownloadThumbnails(games, retry, source, generation);
    }

    private async Task DownloadThumbnails(List<Game> games, bool retry, CancellationTokenSource source, int generation)
    {
        var completed = 0; var available = 0; var unavailable = 0;
        try
        {
            await Parallel.ForEachAsync(games, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = source.Token }, async (game, cancellation) =>
            {
                string? path = null;
                try { path = await thumbnails.Download(game, retry, cancellation); }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or HttpRequestException or TaskCanceledException)
                { Trace.WriteLine($"Thumbnail game={game.Id}: {error.Message}"); Interlocked.Increment(ref unavailable); path = CachedThumbnail(game); }
                var count = Interlocked.Increment(ref completed); if (path != null) Interlocked.Increment(ref available);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation != thumbnailGeneration || cancellation.IsCancellationRequested) return;
                    ArtworkStatus.Text = $"Checking thumbnails · {count} / {games.Count}";
                    if (path != null)
                    {
                        artwork.Remove(game.Id);
                        if (page == Page.Library && GameList.Items.Cast<GameRow>().Any(row => row.Id == game.Id)) _ = LoadArtwork();
                    }
                });
            });
            if (generation == thumbnailGeneration)
                ArtworkStatus.Text = $"Thumbnails checked · {available} cached · {games.Count - available} without API artwork" + (unavailable > 0 ? $" · {unavailable} unavailable" : "");
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Trace.WriteLine("Thumbnail downloads paused: " + error.Message);
            if (generation == thumbnailGeneration)
                ArtworkStatus.Text = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                    ? "Artwork service rate limit reached · downloads paused. Retry later from Settings."
                    : "Thumbnail downloads paused · offline or service unavailable. Retry from Settings.";
        }
        finally { if (ReferenceEquals(thumbnailCancellation, source)) thumbnailCancellation = null; source.Dispose(); }
    }

    private async void ThumbnailSetting_Click(object sender, RoutedEventArgs e) => await SaveThumbnailPreference();
    private Task SaveThumbnailPreference() => Guard("Save thumbnail preference", async () =>
    {
        store.Data.Settings.DownloadThumbnails = DownloadThumbnailsToggle.IsChecked == true;
        await Work("Saving thumbnail preference", store.Save); StartThumbnails();
    });
    private void RetryThumbnails_Click(object sender, RoutedEventArgs e) => StartThumbnails(retry: true);
}
