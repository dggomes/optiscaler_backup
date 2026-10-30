using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource scanningCancellation = new();
    private bool scanningSources;
    private int analyzingFiles;
    private List<DiscoveredGame>? pendingDiscovery;
    private List<string> discoveryWarnings = [];

    private void ScanStatus() => ScanStatusText.Text = scanningSources ? "Scanning · app remains available" : pendingDiscovery != null ? "Scan ready · waiting for file operation" : analyzingFiles > 0 ? "Finding files · app remains available" : "";
    private async Task ScanSources(string operation, Func<Discovery, List<DiscoveredGame>> discover)
    {
        if (scanningSources || pendingDiscovery != null) { StatusText.Text = "A library scan is already running."; return; }
        scanningSources = true; ScanStatus(); var warnings = new List<string>();
        try
        {
            var results = await Task.Run(() =>
            {
                var source = new Discovery(warnings.Add);
                return source.Prepare(discover(source), scanningCancellation.Token);
            }, scanningCancellation.Token);
            scanningCancellation.Token.ThrowIfCancellationRequested();
            pendingDiscovery = results; discoveryWarnings = warnings;
            if (!busy) ApplyDiscovery();
        }
        catch (OperationCanceledException) when (scanningCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException)
        { Trace.WriteLine(operation + ": " + error.Message); ShowError(operation + ": " + error.Message); }
        finally { scanningSources = false; ScanStatus(); }
    }
    private void ApplyDiscovery()
    {
        if (busy || pendingDiscovery == null || scanningCancellation.IsCancellationRequested) return;
        var results = pendingDiscovery; pendingDiscovery = null;
        try
        {
            new Discovery(TraceWarning).Merge(store, results);
            Refresh(); StatusText.Text = $"Scan complete · {results.Count} installations found.";
            if (discoveryWarnings.Count > 0) ShowError(string.Join(" ", discoveryWarnings.Take(3)));
            _ = LoadArtwork(); StartThumbnails();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException)
        { Trace.WriteLine("Merge scan: " + error.Message); ShowError("Could not save scan results: " + error.Message); }
        ScanStatus();
    }
    private async Task<List<SelectionRow>> AnalyzeFiles(Game game, Action<string>? warning)
    {
        var snapshot = Format.Clone(game); analyzingFiles++; ScanStatus();
        try
        {
            var files = await Task.Run(() => FindFiles(snapshot, warning), scanningCancellation.Token);
            scanningCancellation.Token.ThrowIfCancellationRequested(); return files;
        }
        finally { analyzingFiles--; ScanStatus(); }
    }
}
