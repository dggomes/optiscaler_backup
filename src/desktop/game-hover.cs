using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

public sealed class GameHoverDetails : INotifyPropertyChanged
{
    private string text = "Hover to inspect current game components and settings.";
    public string Text { get => text; internal set { text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    internal DateTimeOffset CheckedUtc { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private readonly Dictionary<string, GameHoverDetails> hoverDetails = [];
    private readonly SemaphoreSlim hoverGate = new(1);
    private CancellationTokenSource? hoverCancellation;
    private GameHoverDetails HoverFor(Game game)
    {
        if (!hoverDetails.TryGetValue(game.Id, out var details)) hoverDetails[game.Id] = details = new();
        return details;
    }
    private void ClearHoverDetails()
    {
        hoverCancellation?.Cancel();
        foreach (var details in hoverDetails.Values) details.CheckedUtc = default;
    }
    private async void GameHover_Opening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: GameRow row }) return;
        if (row.Hover.CheckedUtc > DateTimeOffset.UtcNow.AddSeconds(-30)) return;
        if (busy) { row.Hover.Text = row.Description + "\n\nWait for the current file operation to finish before inspecting components."; return; }
        await InspectHover(row);
    }
    private void GameHover_Closing(object sender, System.Windows.Controls.ToolTipEventArgs e) => hoverCancellation?.Cancel();
    private async Task InspectHover(GameRow row)
    {
        hoverCancellation?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(scanningCancellation.Token); hoverCancellation = source;
        row.Hover.Text = row.Description + "\n\nReading current components and approved settings…";
        var entered = false;
        try
        {
            var game = Format.Clone(store.Game(row.Id)); var catalog = Fingerprints;
            await hoverGate.WaitAsync(source.Token); entered = true;
            var report = await Task.Run(() => GameInspector.Read(game, catalog, source.Token), source.Token);
            if (source.IsCancellationRequested) return;
            row.Hover.Text = HoverText(row.Description, report); row.Hover.CheckedUtc = report.CheckedUtc;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { Trace.WriteLine("Game hover " + row.Id + ": " + error.Message); row.Hover.Text = row.Description + "\n\nCurrent inspection unavailable: " + error.Message; }
        finally { if (entered) hoverGate.Release(); if (ReferenceEquals(hoverCancellation, source)) hoverCancellation = null; }
    }
    private static string HoverText(string name, GameInspection report)
    {
        var text = new StringBuilder(name).Append("\nCurrent game files · checked ").Append(report.CheckedUtc.LocalDateTime.ToString("T"));
        if (!report.Available) return text.Append("\n\nGame folder unavailable. Installed versions were not inferred from backups.").ToString();
        text.Append("\n\nComponents on disk");
        foreach (var component in report.Components.Take(12))
            text.Append('\n').Append(component.Component).Append(' ').Append(component.Version).Append(" · ").Append(component.Evidence).Append("\n  ").Append(component.Path);
        if (report.Components.Count == 0) text.Append("\nNo supported component files found.");
        if (report.Components.Count > 12) text.Append("\n… ").Append(report.Components.Count - 12).Append(" additional component files.");
        foreach (var scope in new[] { "Game", "OptiScaler" })
        {
            text.Append("\n\n").Append(scope == "Game" ? "Game graphics preferences" : "OptiScaler preferences");
            var values = report.Settings.Observations.Where(value => value.Scope == scope).Select(value => value.Setting + ": " + value.Value).Distinct().ToList();
            foreach (var value in values.Take(8)) text.Append('\n').Append(value.Length > 240 ? value[..240] + "…" : value);
            if (values.Count == 0) text.Append("\nNo readable supported settings found.");
            if (values.Count > 8) text.Append("\n… ").Append(values.Count - 8).Append(" more values; see Files → Current settings.");
        }
        foreach (var warning in report.Warnings.Concat(report.Settings.Warnings).Take(3)) text.Append("\n\nNote: ").Append(warning.Length > 240 ? warning[..240] + "…" : warning);
        return text.Append("\n\nFile presence and saved preferences do not prove runtime activation. No system GPU drivers are inspected.").ToString();
    }
}
