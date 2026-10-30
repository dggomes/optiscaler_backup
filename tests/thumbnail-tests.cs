using System.Net;
using System.Text;
using System.Text.Json;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class ThumbnailTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9];
    private static HttpResponseMessage Image() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) };
    private static HttpResponseMessage Search(params (int Id, string Name)[] entries) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { items = entries.Select(e => new { id = e.Id, name = e.Name, type = "app" }) })) };
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Thumbnail assertion failed."); }
    private static async Task Fails(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException) { return; }
        throw new InvalidOperationException("Expected thumbnail failure.");
    }
    public static async Task Cache()
    {
        using var fixture = new Fixture(); var requests = 0;
        using var http = new HttpClient(new FakeHttp(request => { requests++; return request.RequestUri!.Host == "store.steampowered.com" ? Search((123, "Fixture®™: Game")) : Image(); }));
        var service = new Thumbnails(http, fixture.Home, TimeSpan.Zero);
        var file = await service.Download(fixture.Game, false); Check(file != null && File.Exists(file) && requests == 2);
        Check(await service.Download(fixture.Game, false) == file && requests == 2);
        using var offline = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("offline")));
        var reopened = new Thumbnails(offline, fixture.Home, TimeSpan.Zero);
        Check(await reopened.Download(fixture.Game, false) == file);
        await Fails(async () => await reopened.Download(fixture.Game, true)); Check(reopened.Cached(fixture.Game) == file);
        File.WriteAllText(file!, "corrupt cache"); Check(service.Cached(fixture.Game) == null);
        Check(await service.Download(fixture.Game, false) == file && requests == 4);
        using var missing = new HttpClient(new FakeHttp(_ => Search()));
        Check(await new Thumbnails(missing, fixture.Home, TimeSpan.Zero).Download(fixture.Game, true) == file);
        Check(service.Cached(fixture.Game) == file);
    }
    public static async Task SteamIdentity()
    {
        using var fixture = new Fixture(); var apps = Path.Combine(fixture.Root, "steamapps");
        var folder = Path.Combine(apps, "common", "Installed Game"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(apps, "appmanifest_321.acf"), "\"AppState\" { \"appid\" \"321\" \"installdir\" \"Installed Game\" }");
        var game = new Game { Name = "A renamed Steam game", Directory = folder };
        Check(Thumbnails.SteamAppId(game) == 321); var requests = 0;
        using var http = new HttpClient(new FakeHttp(request => { requests++; Check(request.RequestUri!.AbsoluteUri == "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/321/library_600x900.jpg"); return Image(); }));
        Check(await new Thumbnails(http, fixture.Home, TimeSpan.Zero).Download(game, false) != null && requests == 1);
        game.Directory = Path.Combine(fixture.Root, "missing", "steamapps", "common", "Disconnected");
        Check(Thumbnails.SteamAppId(game) == null);
    }
    public static async Task Ambiguity()
    {
        using var fixture = new Fixture(); var requests = 0;
        using var http = new HttpClient(new FakeHttp(_ => { requests++; return Search((1, fixture.Game.Name), (2, fixture.Game.Name)); }));
        var service = new Thumbnails(http, fixture.Home, TimeSpan.Zero);
        Check(await service.Download(fixture.Game, false) == null);
        Check(await service.Download(fixture.Game, false) == null && requests == 1);
        Check(await service.Download(fixture.Game, true) == null && requests == 2);
        Check(!Directory.EnumerateFiles(Path.Combine(fixture.Home, "thumbnails"), "*.jpg").Any());
        using var partial = new HttpClient(new FakeHttp(_ => Search((3, fixture.Game.Name + " Demo"))));
        Check(await new Thumbnails(partial, fixture.Home, TimeSpan.Zero).Download(fixture.Game, true) == null);
    }
    public static async Task Failures()
    {
        using var fixture = new Fixture();
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var http = new HttpClient(new FakeHttp(_ => new(status)));
            await Fails(async () => await new Thumbnails(http, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false));
        }
        using var malformed = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent("not json") }));
        await Fails(async () => await new Thumbnails(malformed, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false));
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("not an image"), new byte[4 * 1024 * 1024 + 1] })
        {
            using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "store.steampowered.com" ? Search((1, fixture.Game.Name)) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await Fails(async () => await new Thumbnails(http, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false));
        }
        using var redirect = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, "http://untrusted.invalid/"), Content = new StringContent("{}") }));
        await Fails(async () => await new Thumbnails(redirect, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false));
        using var never = new HttpClient(new FakeHttp(_ => throw new InvalidOperationException("Canceled download sent a request")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Fails(async () => await new Thumbnails(never, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false, cancellation.Token));
        Check(!Directory.Exists(Path.Combine(fixture.Home, "thumbnails")));
    }
    public static async Task FutureCache()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new FakeHttp(request => request.RequestUri!.Host == "store.steampowered.com" ? Search((1, fixture.Game.Name)) : Image()));
        var service = new Thumbnails(http, fixture.Home, TimeSpan.Zero); await service.Download(fixture.Game, false);
        var metadata = Directory.GetFiles(Path.Combine(fixture.Home, "thumbnails"), "*.json").Single();
        File.WriteAllText(metadata, "{\"Version\":999}");
        await Fails(async () => await service.Download(fixture.Game, true));
        Check(File.ReadAllText(metadata) == "{\"Version\":999}");
    }
    public static async Task PortraitUpgrade()
    {
        using var fixture = new Fixture(); var portraits = 0; var headers = 0; var missingPortrait = false;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            if (request.RequestUri!.Host == "store.steampowered.com") return Search((123, fixture.Game.Name));
            if (request.RequestUri.AbsolutePath.EndsWith("library_600x900.jpg"))
            { portraits++; return missingPortrait ? new(HttpStatusCode.NotFound) : Image(); }
            headers++; return Image();
        }));
        var service = new Thumbnails(http, fixture.Home, TimeSpan.Zero);
        var file = await service.Download(fixture.Game, false);
        var metadata = Directory.GetFiles(Path.Combine(fixture.Home, "thumbnails"), "*.json").Single();
        var legacy = Store.Read<ThumbnailRecord>(metadata); legacy.PortraitChecked = false; Store.Atomic(metadata, legacy);
        using var offline = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("offline")));
        await Fails(async () => await new Thumbnails(offline, fixture.Home, TimeSpan.Zero).Download(fixture.Game, false));
        Check(service.Cached(fixture.Game) == file);
        Check(await service.Download(fixture.Game, false) == file && portraits == 2 && headers == 0);
        Check(Store.Read<ThumbnailRecord>(metadata).PortraitChecked);
        missingPortrait = true;
        Check(await service.Download(fixture.Game, true) == file && portraits == 3 && headers == 1);
        Check(await service.Download(fixture.Game, false) == file && portraits == 3 && headers == 1);
    }

    public static async Task Live()
    {
        var home = Path.Combine(Directory.GetCurrentDirectory(), ".context", "live-thumbnails");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        var game = new Game { Id = "live-cyberpunk", Name = "Cyberpunk 2077", Directory = Path.Combine(home, "game") };
        var service = new Thumbnails(http, home); var file = await service.Download(game, true);
        Check(file != null && new FileInfo(file).Length > 1000 && service.Cached(game) == file);
        Console.WriteLine("PASS live Steam storefront match, HTTPS cover download and verified persistent cache: " + file);
    }
}
