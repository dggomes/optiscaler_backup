using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed class ThumbnailRecord
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Checked { get; set; }
    public int? AppId { get; set; }
    public string? Hash { get; set; }
    public bool PortraitChecked { get; set; }
    public string Source { get; set; } = "Steam";
    public int LookupRevision { get; set; }
}

// Steam's public storefront endpoint needs no key. It is undocumented, so failures
// must remain optional: a game never depends on this metadata to be usable.
public sealed class Thumbnails(HttpClient client, string home, TimeSpan? requestInterval = null)
{
    private const int ImageLimit = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetFullPath(home), "thumbnails");
    private readonly object cacheGate = new();
    private readonly SemaphoreSlim requests = new(1);
    private DateTimeOffset nextRequest;
    private readonly TimeSpan interval = requestInterval ?? TimeSpan.FromMilliseconds(750);
    private static string Key(Game game) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game.Id + "\n" + game.Name + "\n" + game.Directory)));

    public string? Cached(Game game)
    {
        var record = Read(game);
        var file = Path.Combine(root, Key(game) + ".jpg");
        SafePaths.NoLinks(file);
        if (record?.Hash == null || !File.Exists(file) || new FileInfo(file).Length > ImageLimit) return null;
        using var input = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(input)) == record.Hash ? file : null;
    }

    public async Task<string?> Download(Game game, bool retry, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); cancellation = timeout.Token;
        var record = Read(game);
        var cached = Cached(game);
        if (record?.Source == "Playnite" && cached != null) return cached;
        var lifetime = cached != null ? TimeSpan.FromDays(30) : TimeSpan.FromDays(1);
        if (!retry && record?.LookupRevision == 1 && (cached != null || record.Hash == null) && (record.PortraitChecked || record.Hash == null) && record.Checked > DateTimeOffset.UtcNow - lifetime) return cached;
        var appId = SteamAppId(game) ?? await Search(game.Name, cancellation);
        if (appId == null) { Save(game, new() { LookupRevision = 1, AppId = record?.AppId, Hash = cached != null ? record?.Hash : null, PortraitChecked = record?.PortraitChecked ?? false, Checked = DateTimeOffset.UtcNow }); return cached; }
        var data = await Image(appId.Value, cancellation);
        if (data == null)
        { Save(game, new() { LookupRevision = 1, AppId = appId, Hash = cached != null ? record?.Hash : null, PortraitChecked = true, Checked = DateTimeOffset.UtcNow }); return cached; }
        lock (cacheGate)
        {
            if (Read(game)?.Source == "Playnite" && Cached(game) is { } imported) return imported;
            SafePaths.NoLinks(root); Directory.CreateDirectory(root);
            var file = Path.Combine(root, Key(game) + ".jpg"); SafePaths.NoLinks(file);
            var temporary = file + "." + Format.Id() + ".tmp";
            try
            {
                cancellation.ThrowIfCancellationRequested(); File.WriteAllBytes(temporary, data);
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary, file, true);
                Save(game, new() { LookupRevision = 1, AppId = appId, Hash = Convert.ToHexStringLower(SHA256.HashData(data)), PortraitChecked = true, Checked = DateTimeOffset.UtcNow });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return file;
        }
    }

    public void ImportPlayniteCover(Game game, string file)
    {
        lock (cacheGate) ImportCover(game, file);
    }
    private void ImportCover(Game game, string file)
    {
        SafePaths.NoLinks(file);
        if (new FileInfo(file).Length > ImageLimit) throw new InvalidDataException("Playnite cover exceeds 4 MiB.");
        var bytes = File.ReadAllBytes(file);
        var jpeg = bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff && bytes[^2] == 0xff && bytes[^1] == 0xd9;
        var png = bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!jpeg && !png) throw new InvalidDataException("Playnite cover must be a JPEG or PNG.");
        Directory.CreateDirectory(root); var destination = Path.Combine(root, Key(game) + ".jpg"); SafePaths.NoLinks(destination);
        // Read first so an unsupported future cache is never silently replaced.
        _ = Read(game); var temporary = destination + "." + Format.Id() + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes); File.Move(temporary, destination, true);
            Save(game, new() { Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), Source = "Playnite", LookupRevision = 1, PortraitChecked = true, Checked = DateTimeOffset.UtcNow });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<byte[]?> Image(int appId, CancellationToken cancellation)
    {
        var rootUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/";
        if (await TryImage(new Uri(rootUrl + "library_600x900.jpg"), cancellation) is { } legacy) return legacy;
        var assets = await Assets(appId, cancellation);
        foreach (var asset in assets.Append("header.jpg").Distinct())
            if (await TryImage(new Uri(rootUrl + asset), cancellation) is { } image) return image;
        return null;
    }
    private async Task<List<string>> Assets(int appId, CancellationToken cancellation)
    {
        var query = JsonSerializer.Serialize(new { ids = new[] { new { appid = appId } }, context = new { language = "english", country_code = "US" }, data_request = new { include_assets = true } });
        using var response = await Get(new Uri("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(query)), cancellation);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await Bytes(response, 1024 * 1024, cancellation), new() { MaxDepth = 32 });
        var result = new List<string>();
        if (!doc.RootElement.TryGetProperty("response", out var body) || !body.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Steam artwork API returned an unexpected response.");
        foreach (var item in items.EnumerateArray().Take(10))
        {
            if (!item.TryGetProperty("appid", out var id) || !id.TryGetInt32(out var value) || value != appId || !item.TryGetProperty("assets", out var assets)) continue;
            foreach (var key in new[] { "library_capsule", "library_capsule_2x", "header" })
                if (assets.TryGetProperty(key, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { } path)
                {
                    // Only relative JPG paths in this app's fixed Steam CDN directory are accepted.
                    if (path.Length > 256 || path.Contains("..", StringComparison.Ordinal) || path.StartsWith('/') || !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '-' or '.'))) continue;
                    result.Add(path);
                }
        }
        return result;
    }
    private async Task<byte[]?> TryImage(Uri url, CancellationToken cancellation)
    {
        using var response = await Get(url, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); var data = await Bytes(response, ImageLimit, cancellation);
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8 || data[2] != 0xff || data[^2] != 0xff || data[^1] != 0xd9)
            throw new InvalidDataException("Thumbnail service returned an invalid JPEG.");
        return data;
    }

    private ThumbnailRecord? Read(Game game)
    {
        var file = Path.Combine(root, Key(game) + ".json"); SafePaths.NoLinks(file);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException("Thumbnail metadata exceeds its size limit.");
        var record = Store.Read<ThumbnailRecord>(file);
        if (record.Version != 1) throw new InvalidDataException("Unsupported thumbnail-cache format; cache was not rewritten.");
        return record;
    }
    private void Save(Game game, ThumbnailRecord record)
    {
        lock (cacheGate)
        {
            if (record.Source != "Playnite" && Read(game)?.Source == "Playnite" && Cached(game) != null) return;
            Store.Atomic(Path.Combine(root, Key(game) + ".json"), record);
        }
    }

    private async Task<int?> Search(string name, CancellationToken cancellation)
    {
        if (Normalize(name).Length == 0) return null;
        foreach (var term in new[] { name, SearchTitle(name) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var response = await Get(new Uri("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(term)), cancellation);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await Bytes(response, 1024 * 1024, cancellation), new() { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Thumbnail search returned an unexpected response.");
            var candidates = items.EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "app"
                && item.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String
                && item.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number > 0).ToList();
            var exact = candidates.Where(item => Normalize(item.GetProperty("name").GetString()!) == Normalize(name)).ToList();
            var matches = (exact.Count > 0 ? exact : candidates.Where(item => Normalize(SearchTitle(item.GetProperty("name").GetString()!)) == Normalize(SearchTitle(name))))
                .Select(item => item.GetProperty("id").GetInt32()).Distinct().ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null;
        }
        return null;
    }
    private static string SearchTitle(string name) => System.Text.RegularExpressions.Regex.Replace(name,
        @"[\s:–—-]*(?:\(?Windows(?: PC)?(?: Edition)?\)?|\(?PC\)?|Game of the Year(?: Edition)?|GOTY(?: Edition)?|Definitive Edition|Ultimate Edition|Deluxe Edition|Standard Edition|Complete Edition|Enhanced Edition)\s*$", "",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
    private static string Normalize(string name) => string.Concat(name.Replace("&", "and", StringComparison.Ordinal).Replace("™", "", StringComparison.Ordinal).Normalize(NormalizationForm.FormKD)
        .EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(r => Rune.ToLowerInvariant(r).ToString()));

    public static int? SteamAppId(Game game)
    {
        var common = Directory.GetParent(game.Directory);
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true || common.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true) return null;
        var apps = common.Parent.FullName; if (!Directory.Exists(apps)) return null; SafePaths.NoLinks(apps);
        foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
        {
            SafePaths.NoLinks(file);
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            var data = Vdf.Parse(File.ReadAllText(file));
            var directory = data.Values("installdir").FirstOrDefault();
            if (directory == null || !Path.GetFullPath(Path.Combine(common.FullName, directory)).Equals(game.Directory, StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(data.Values("appid").FirstOrDefault(), out var appId) && appId > 0 ? appId : null;
        }
        return null;
    }

    private async Task<HttpResponseMessage> Get(Uri url, CancellationToken cancellation)
    {
        await requests.WaitAsync(cancellation);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("OptiScalerBackup/" + Releases.Current.ToString(3));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.RequestMessage?.RequestUri is { } destination && (destination.Scheme != "https" || destination.Host != url.Host))
            { response.Dispose(); throw new HttpRequestException("Thumbnail redirect left the expected HTTPS host."); }
            return response;
        }
        finally { nextRequest = DateTimeOffset.UtcNow + interval; requests.Release(); }
    }
    private static async Task<byte[]> Bytes(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Thumbnail response exceeds its size limit.");
        using var output = new MemoryStream();
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Thumbnail response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
