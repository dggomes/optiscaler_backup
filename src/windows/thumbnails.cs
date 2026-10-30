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
}

// Steam's public storefront endpoint needs no key. It is undocumented, so failures
// must remain optional: a game never depends on this metadata to be usable.
public sealed class Thumbnails(HttpClient client, string home, TimeSpan? requestInterval = null)
{
    private const int ImageLimit = 4 * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetFullPath(home), "thumbnails");
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
        timeout.CancelAfter(TimeSpan.FromSeconds(40)); cancellation = timeout.Token;
        var record = Read(game);
        var cached = Cached(game);
        var lifetime = cached != null ? TimeSpan.FromDays(30) : TimeSpan.FromDays(7);
        if (!retry && record != null && (cached != null || record.Hash == null) && record.Checked > DateTimeOffset.UtcNow - lifetime) return cached;
        var appId = SteamAppId(game) ?? await Search(game.Name, cancellation);
        if (appId == null) { Save(game, new() { AppId = record?.AppId, Hash = cached != null ? record?.Hash : null, Checked = DateTimeOffset.UtcNow }); return cached; }
        using var response = await Get(new Uri($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"), cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        { Save(game, new() { AppId = appId, Hash = cached != null ? record?.Hash : null, Checked = DateTimeOffset.UtcNow }); return cached; }
        response.EnsureSuccessStatusCode();
        var data = await Bytes(response, ImageLimit, cancellation);
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8 || data[2] != 0xff || data[^2] != 0xff || data[^1] != 0xd9)
            throw new InvalidDataException("Thumbnail service returned an invalid JPEG.");
        SafePaths.NoLinks(root); Directory.CreateDirectory(root);
        var file = Path.Combine(root, Key(game) + ".jpg"); SafePaths.NoLinks(file);
        var temporary = file + "." + Format.Id() + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellation);
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, file, true);
            Save(game, new() { AppId = appId, Hash = Convert.ToHexStringLower(SHA256.HashData(data)), Checked = DateTimeOffset.UtcNow });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return file;
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
    private void Save(Game game, ThumbnailRecord record) => Store.Atomic(Path.Combine(root, Key(game) + ".json"), record);

    private async Task<int?> Search(string name, CancellationToken cancellation)
    {
        if (Normalize(name).Length == 0) return null;
        using var response = await Get(new Uri("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(name)), cancellation);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await Bytes(response, 1024 * 1024, cancellation), new() { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Thumbnail search returned an unexpected response.");
        var matches = items.EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "app"
                && item.TryGetProperty("name", out var title) && Normalize(title.GetString() ?? "") == Normalize(name)
                && item.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && number > 0)
            .Select(item => item.GetProperty("id").GetInt32()).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
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
