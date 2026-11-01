using System.Diagnostics;
using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class GameInspectionTests
{
    public static void LiveFiles()
    {
        using var fixture = new Fixture();
        fixture.Write("dxgi.dll", "component recognition test bytes");
        fixture.Write("OptiScaler.ini", "[Upscalers]\nDx12Upscaler=dlss\n[FrameGen]\nEnabled=true\nFGOutput=xefg");
        fixture.Write("Saved/Config/Windows/GameUserSettings.ini", "[/Script/Engine.GameUserSettings]\nResolutionSizeX=2560\nResolutionSizeY=1440");
        fixture.Game.Snapshots.Add(new Snapshot { Name = "Old unrelated backup" });
        var before = JsonSerializer.Serialize(fixture.Game, Format.Json);
        var source = ComponentFingerprints.Bundled().Document;
        var seed = source.Records.First(record => record.Component == "OptiScaler");
        var record = seed with { Sha256 = SafePaths.Hash(fixture.Path("dxgi.dll")), Size = new FileInfo(fixture.Path("dxgi.dll")).Length };
        var catalog = ComponentFingerprints.Read(JsonSerializer.SerializeToUtf8Bytes(source with { Records = [record] }, Format.Json));
        var report = GameInspector.Read(fixture.Game, catalog);
        Check(report.Available && report.Components.Single().Evidence == "SHA-256 catalog match" && report.Components.Single().Version == record.Version, "Live proxy hash was not identified.");
        Check(report.Settings.Observations.Any(value => value.Value == "2560 px") && report.Settings.Observations.Any(value => value.Setting == "OptiScaler FG output" && value.Value.Contains("XeSS")), "Unmanaged game-local settings were not inspected.");
        Check(before == JsonSerializer.Serialize(fixture.Game, Format.Json), "Inspection changed managed files or backup metadata.");
        fixture.Write("dxgi.dll", "changed component");
        fixture.Write("OptiScaler.ini", "[Upscalers]\nDx12Upscaler=fsr31");
        var changed = GameInspector.Read(fixture.Game, catalog);
        Check(changed.Components.Single().Evidence != "SHA-256 catalog match" && changed.Components.Single().Version == "Unknown version", "Edited bytes retained an old installed version.");
        Check(changed.Settings.Observations.Single(value => value.Scope == "OptiScaler").Value.Contains("FSR"), "Changed settings were replaced with backup state.");
        var missing = Format.Clone(fixture.Game); missing.Directory = fixture.Path("offline");
        Check(!GameInspector.Read(missing, catalog).Available, "Offline inspection used an older backup as installed state.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { GameInspector.Read(fixture.Game, catalog, cancellation.Token); throw new Exception("Cancelled inspection ran."); }
        catch (OperationCanceledException) { }
        fixture.Write("libxess_fg.dll", "unversioned test bytes");
        var unknown = GameInspector.Read(fixture.Game, catalog).Components.Single(file => file.Component == "XeSS FG (XeFG)");
        Check(unknown.Version == "Unknown version", "Filename implied a fabricated XeFG version.");
        File.Delete(fixture.Path("libxess_fg.dll"));
        File.CreateSymbolicLink(fixture.Path("libxess_fg.dll"), fixture.Path("dxgi.dll"));
        Check(GameInspector.Read(fixture.Game, catalog).Components.All(file => file.Component != "XeSS FG (XeFG)"), "Linked component was inspected.");
        using (var oversized = File.Create(fixture.Path("amd_fidelityfx_upscaler_dx12.dll"))) oversized.SetLength(GameInspector.MaxBinaryBytes + 1);
        var bounded = GameInspector.Read(fixture.Game, catalog);
        Check(bounded.Components.All(file => !file.Path.EndsWith("amd_fidelityfx_upscaler_dx12.dll")) && bounded.Warnings.Any(warning => warning.Contains("size limit")), "Oversized component was read without a limit.");
    }
    public static void Metadata()
    {
        using var fixture = new Fixture();
        var binary = typeof(Detection).Assembly.Location;
        File.Copy(binary, fixture.Path("libxess_fg.dll"));
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(fixture.Path("libxess_fg.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
            var denied = GameInspector.Read(fixture.Game, ComponentFingerprints.Bundled());
            Check(denied.Components.Count == 0 && denied.Warnings.Count > 0, "Locked components did not produce an inspection warning.");
        }
        var expected = FileVersionInfo.GetVersionInfo(binary).FileVersion;
        var report = GameInspector.Read(fixture.Game, ComponentFingerprints.Bundled());
        var component = report.Components.Single();
        Check(component.Component == "XeSS FG (XeFG)" && component.Version == expected && component.Evidence.Contains("metadata only"), "Version resources were confused with hash-verified identity.");
        var external = fixture.Path("approved"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "Engine.ini"), "[SystemSettings]\nr.XeFG.Enabled=true");
        fixture.Game.ExternalRoots["settings"] = external; fixture.Game.Managed.Add(new("settings", "Engine.ini"));
        var settings = GameInspector.Read(fixture.Game, ComponentFingerprints.Bundled()).Settings;
        Check(settings.Observations.Any(value => value.File == "settings:Engine.ini" && value.Value == "On"), "Approved external settings were not included.");
        fixture.Game.Managed.Add(new("unapproved", "Engine.ini"));
        Check(GameInspector.Read(fixture.Game, ComponentFingerprints.Bundled()).Settings.Warnings.Any(value => value.Contains("Unapproved")), "Unapproved settings root was read.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
