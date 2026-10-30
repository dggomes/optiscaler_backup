using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class UpdateBundleTests
{
    private static string Url(string name) => "https://github.com/" + Releases.Repository + "/releases/download/v99.0.0/" + name;
    private static JsonElement Release(string tag, bool ui = true) => JsonSerializer.SerializeToElement(new
    {
        tag_name = tag,
        draft = false,
        prerelease = false,
        body = "Release notes",
        html_url = "https://github.com/" + Releases.Repository + "/releases/tag/" + tag,
        assets = new[] { Releases.Asset, Releases.Asset + ".sha256" }.Concat(ui ? new[] { Releases.UiAsset, Releases.UiAsset + ".sha256" } : [])
            .Select(name => new { name, browser_download_url = Url(name) })
    });
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Paired updater assertion failed."); }
    private static void Fails(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidOperationException("Expected paired updater validation failure.");
    }
    public static Task Selection()
    {
        var release = Releases.Parse(Release("v99.0.0"), Releases.Current, Releases.UiAsset)!;
        Check(release.AssetName == Releases.UiAsset && release.AssetUrl == Url(Releases.UiAsset));
        var cli = Releases.ForAsset(release, Releases.Asset); Check(cli.Tag == release.Tag && cli.AssetUrl == Url(Releases.Asset));
        var current = "v" + Releases.Current.ToString(3);
        Check(Releases.Parse(Release(current), Releases.Current, Releases.UiAsset) == null);
        Check(Releases.Parse(Release(current), Releases.Current, Releases.UiAsset, includeCurrent: true)?.Tag == current);
        Check(Releases.Parse(Release("v0.0.1"), Releases.Current, Releases.UiAsset, includeCurrent: true) == null);
        var onlyCli = Releases.Parse(Release("v99.0.0", ui: false), Releases.Current)!;
        Fails(() => Releases.ForAsset(onlyCli, Releases.UiAsset));
        return Task.CompletedTask;
    }
    public static async Task Downloads()
    {
        using var fixture = new Fixture();
        var release = Releases.Parse(Release("v99.0.0"), Releases.Current, Releases.UiAsset)!;
        using var client = new HttpClient(new FakeHttp(request =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var checksum = name.EndsWith(".sha256"); if (checksum) name = name[..^7];
            var content = Encoding.UTF8.GetBytes(name);
            return new(HttpStatusCode.OK) { Content = checksum ? new StringContent(Convert.ToHexStringLower(SHA256.HashData(content)) + "  " + name) : new ByteArrayContent(content) };
        }));
        var service = new Releases(client); var folder = Path.Combine(fixture.Home, "updates", "bundle");
        foreach (var name in new[] { Releases.Asset, Releases.UiAsset })
        {
            var verified = await service.DownloadVerified(Releases.ForAsset(release, name), folder);
            Check(Path.GetFileName(verified.Path) == name && verified.Hash == SafePaths.Hash(verified.Path));
        }
        Check(!File.Exists(UpdateHost.Pending(fixture.Home)));
    }
    public static Task MetadataAndGuards()
    {
        using var fixture = new Fixture(); var catalog = Path.Combine(fixture.Home, "catalog.json"); var before = SafePaths.Hash(catalog);
        var release = Releases.Parse(Release("v99.0.0"), Releases.Current)!;
        UpdateHost.RememberRelease(fixture.Home, release);
        Check(Releases.ForAsset(UpdateHost.LastRelease(fixture.Home)!, Releases.UiAsset).Tag == release.Tag && SafePaths.Hash(catalog) == before);
        var check = Path.Combine(fixture.Home, "updates", "last-check.json"); File.WriteAllText(check, "{\"Version\":999}");
        Fails(() => UpdateHost.RememberRelease(fixture.Home, release)); Check(File.ReadAllText(check) == "{\"Version\":999}");
        File.WriteAllText(fixture.Store.RenameJournalPath, "{}");
        Fails(() => UpdateHost.Stage(fixture.Store, fixture.Path("missing"), release));
        var job = new UpdateJob
        {
            SchemaVersion = UpdateHost.BundleJobVersion,
            Home = fixture.Home,
            Destination = Path.Combine(fixture.Root, "app", "optibackup.exe"),
            Download = Path.Combine(fixture.Home, "updates", "cli.exe"),
            Companions = [new() { Destination = Path.Combine(fixture.Root, "elsewhere", "optibackup-ui.exe"), Download = Path.Combine(fixture.Home, "updates", "ui.exe") }]
        };
        var pending = UpdateHost.Pending(fixture.Home); Store.Atomic(pending, job); var saved = File.ReadAllText(pending);
        Fails(() => UpdateHost.Health(pending)); Check(File.ReadAllText(pending) == saved && !File.Exists(pending + ".healthy"));
        return Task.CompletedTask;
    }
}
