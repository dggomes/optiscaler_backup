using System.Net;
using System.Text;
using System.Text.Json;
using OptiScalerBackup.Core;

internal static class BackupInsightTests
{
    public static void HistoricalRecognition()
    {
        using var f = new Fixture(); var catalog = ComponentFingerprints.Bundled();
        Check(catalog.Document.Records.Count >= 98, "Historical seed is incomplete.");
        foreach (var version in new[] { "4.1.0.0", "4.1.1.2740", "4.1.2.4555" })
            Check(catalog.Document.Records.Any(r => r.Component == "FSR" && r.Role == "upscaler" && r.Version == version), "Known FSR version omitted: " + version);
        var scaler = catalog.Document.Records.First(r => r.Component == "OptiScaler" && r.ReleaseTag == "v0.9.4");
        var patcher = catalog.Document.Records.First(r => r.Component == "OptiPatcher" && r.ReleaseTag == "v0.42");
        var fsr = catalog.Document.Records.First(r => r.Component == "FSR" && r.Version == "4.1.2.4555");
        var snapshot = new Snapshot { Name = "Historical backup", Files = [new(new("game", "renamed-proxy.dll"), scaler.Sha256, scaler.Size), new(new("game", "plugins/OptiPatcher.asi"), patcher.Sha256, patcher.Size), new(new("game", "amd_fidelityfx_upscaler_dx12.dll"), fsr.Sha256, fsr.Size)] };
        var before = JsonSerializer.Serialize(snapshot, Format.Json);
        var report = catalog.Identify(f.Store, snapshot);
        Check(report.Identified.Count == 3 && report.Unknown.Count == 0, "Captured fingerprints did not identify components.");
        Check(report.Identified.Single(r => r.Target.Path == "renamed-proxy.dll").Matches.Any(m => m.Component == "OptiScaler"), "Renamed proxy was guessed by filename.");
        Check(report.Summary.Contains("4.1.2.4555") && report.Summary.Contains("OptiPatcher"), "Version summary is missing.");
        Check(JsonSerializer.Serialize(snapshot, Format.Json) == before, "Recognition rewrote immutable snapshot metadata.");
        Check(catalog.Document.Records.Any(r => r.Component == "OptiPatcher" && r.ReleaseTag == "v0.37" && r.Version == "0.37a"), "Publisher asset patch suffix was lost.");
        Check(catalog.Document.Records.Any(r => r.Component == "OptiScaler" && r.ReleaseTag == "v0.9.2" && r.Version.StartsWith("0.9.2a")), "Archive version suffix was lost.");
        var unknown = catalog.Identify(f.Store, new Snapshot { Files = [new(new("game", "OptiPatcher.asi"), new string('0', 64), 123)] });
        Check(unknown.Unknown.Single().Component == "OptiPatcher" && unknown.Identified.Count == 0, "Unknown file received a guessed version.");
        var inventorySnapshot = new Snapshot();
        f.Store.SaveInventory(inventorySnapshot, new GameInventory { Files = [new("plugins/renamed.asi", patcher.Sha256, patcher.Size, DateTime.UtcNow)] });
        var inventoryReport = catalog.Identify(f.Store, inventorySnapshot);
        Check(!inventoryReport.Identified.Single().BytesBackedUp, "Inventory-only bytes were reported saved.");
        var shared = catalog.Document.Records.Where(r => r.Component == "FSR").GroupBy(r => r.Sha256).First(g => g.Select(r => r.ReleaseTag).Distinct().Count() > 1).First();
        var sharedReport = catalog.Identify(f.Store, new Snapshot { Files = [new(new("game", shared.FileName), shared.Sha256, shared.Size)] });
        Check(sharedReport.Identified.Single().Matches.Select(m => m.ReleaseTag).Distinct().Count() > 1, "Shared release hashes lost provenance/ambiguity.");
    }
    public static async Task CatalogRefresh()
    {
        using var f = new Fixture(); var catalog = ComponentFingerprints.Bundled();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog.Document, Format.Json);
        using var client = new HttpClient(new Handler(bytes));
        var next = await catalog.Refresh(client, f.Home);
        Check(next.Document.Records.Count == catalog.Document.Records.Count, "Valid refresh lost history.");
        var path = ComponentFingerprints.CachePath(f.Home); var before = File.ReadAllBytes(path);
        using var older = new HttpClient(new Handler(JsonSerializer.SerializeToUtf8Bytes(catalog.Document with { Records = catalog.Document.Records.Skip(1).ToList() }, Format.Json)));
        await RejectAsync(() => next.Refresh(older, f.Home));
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Rejected refresh changed last-good data.");
        using var malformed = new HttpClient(new Handler(Encoding.UTF8.GetBytes("{truncated")));
        await RejectAsync(() => next.Refresh(malformed, f.Home));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(catalog.Document with { Version = 99 }, Format.Json));
        var future = File.ReadAllBytes(path);
        Check(ComponentFingerprints.Load(f.Home).Document.Version == 1, "Unsupported cache blocked bundled recognition.");
        await RejectAsync(() => next.Refresh(client, f.Home));
        Check(File.ReadAllBytes(path).SequenceEqual(future), "Future cache format was rewritten.");
    }
    public static void UnrealParsing()
    {
        var text = "[/Script/Engine.GameUserSettings]\nResolutionSizeX=2560\nResolutionSizeY=1440\nFullscreenMode=1\nbUseVSync=False\nDLSSQuality=2\n[ScalabilityGroups]\nsg.ResolutionQuality=100\n[SystemSettings]\nr.FidelityFX.FSR3.Enabled=1\nr.FidelityFX.FSR3.QualityMode=0 ; explicit native AA\nr.NGX.DLSS.Quality=-2\nr.AntiAliasingMethod=4\n";
        var report = GameSettingsReader.Parse(text, "game:Saved/Config/Windows/GameUserSettings.ini");
        Check(report.Observations.Any(o => o.Setting == "Window mode" && o.Value == "Borderless fullscreen"), "Unreal fullscreen enum is wrong.");
        Check(report.Observations.Any(o => o.Setting == "FSR quality preference" && o.Value == "Native AA"), "FSR native AA preference was missed.");
        Check(report.Observations.Any(o => o.Key == "DLSSQuality" && o.Value == "2" && o.Confidence.Contains("mapping required")), "Custom numeric DLSS mode was guessed.");
        Check(report.Observations.Any(o => o.Key == "r.NGX.DLSS.Quality" && o.Value == "-2" && o.Confidence.Contains("no universal")), "Unverified plugin enum was guessed.");
        Check(report.Warnings.Any(w => w.Contains("cannot distinguish")), "100% scale was incorrectly taken as native AA proof.");
        foreach (var (number, label) in new[] { (0, "Native AA"), (1, "Quality"), (2, "Balanced"), (3, "Performance"), (4, "Ultra Performance") })
            Check(GameSettingsReader.Parse("[SystemSettings]\nr.FidelityFX.FSR.QualityMode=" + number, "Engine.ini").Observations.Single().Value == label, "Documented FSR enum is incorrect.");
        var defaults = GameSettingsReader.Parse("[SystemSettings]\nr.ScreenPercentage=50\nr.ScreenPercentage=100\n+r.ScreenPercentage=75\n", "game:DefaultEngine.ini");
        Check(defaults.Observations.Count == 2 && defaults.Warnings.Any(w => w.Contains("Multiple")), "Conflicting/array INI values were silently collapsed.");
        Check(defaults.Observations.All(o => o.Layer == "Project default"), "Default config was labelled a saved user choice.");
        Check(defaults.Observations.All(o => o.Line > 0 && o.Source.StartsWith("https://")), "Settings provenance missing.");
        var zeroScale = GameSettingsReader.Parse("[ScalabilityGroups]\nsg.ResolutionQuality=0.000000\n[SystemSettings]\nr.ScreenPercentage=0", "GameUserSettings.ini");
        Check(zeroScale.Observations.All(o => o.Value.Contains("default")), "UE default scale sentinel was displayed as a 0% render scale.");
        var customDisplay = GameSettingsReader.Parse("[Display]\nFullscreenMode=2", "GameUserSettings.ini");
        Check(customDisplay.Observations.Single().Value == "2" && customDisplay.Observations.Single().Confidence.Contains("mapping required"), "Generic display-section enum was mistaken for a documented Unreal base field.");
        var unrelated = GameSettingsReader.Parse("[Account]\nToken=secret\nDLSSQuality=Quality", "GameUserSettings.ini");
        Check(unrelated.Observations.Count == 0, "Unrelated account fields were exposed.");
    }
    public static void CapturedSettings()
    {
        using var f = new Fixture();
        f.Write("Saved/Config/Windows/GameUserSettings.ini", "[/Script/Engine.GameUserSettings]\nResolutionSizeX=3840\nResolutionSizeY=2160\nFullscreenMode=0");
        f.Write("Saved/Config/Windows/Engine.ini", "[SystemSettings]\nr.FidelityFX.FSR3.QualityMode=3");
        File.WriteAllText(f.Path("Saved/Config/Windows/GameUserSettings.ini"), File.ReadAllText(f.Path("Saved/Config/Windows/GameUserSettings.ini")), Encoding.Unicode);
        f.Game.Managed.AddRange([new("game", "Saved/Config/Windows/GameUserSettings.ini"), new("game", "Saved/Config/Windows/Engine.ini")]); f.Store.Put(f.Game);
        var snapshot = f.Engine.CreateSnapshot(f.Current, "Graphics", false, false);
        var stored = GameSettingsReader.Snapshot(f.Store, snapshot);
        Check(stored.Observations.Any(o => o.Value == "3840 px") && stored.Observations.Any(o => o.Value == "Performance"), "Captured UTF16/config settings were not parsed.");
        f.Write("Saved/Config/Windows/Engine.ini", "[SystemSettings]\nr.FidelityFX.FSR3.QualityMode=1");
        Check(GameSettingsReader.Current(f.Current).Observations.Any(o => o.Value == "Quality"), "Current settings did not reflect saved file changes.");
        Check(GameSettingsReader.Snapshot(f.Store, snapshot).Observations.Any(o => o.Value == "Performance"), "Backup settings were incorrectly read from current game files.");
        var state = snapshot.Files.First(); File.SetAttributes(f.Store.Payload(state), FileAttributes.Normal); File.AppendAllText(f.Store.Payload(state), "\ncorrupted");
        Check(GameSettingsReader.Snapshot(f.Store, snapshot).Warnings.Any(w => w.Contains("modified payload")), "Corrupted config payload was presented as verified saved settings.");
        var empty = GameSettingsReader.Snapshot(f.Store, new Snapshot());
        Check(empty.Observations.Count == 0, "Missing settings were invented from live files.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task<ComponentFingerprints>> action)
    {
        try { await action(); } catch (IOException) { return; }
        throw new Exception("Unsafe component catalog refresh was accepted.");
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.AbsoluteUri == ComponentFingerprints.Url, "Recognition fetched an unexpected source.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
