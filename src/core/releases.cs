using System.Net;
using System.Text.Json;

namespace OptiScalerBackup.Core;

public sealed record ReleaseAsset(string Url, string ChecksumUrl);
public sealed record VerifiedDownload(string Path, string Hash);
public sealed record ReleaseInfo(string Tag, string Notes, string Page, string AssetUrl, string ChecksumUrl)
{
    public string AssetName { get; init; } = Releases.Asset;
    public Dictionary<string, ReleaseAsset> Assets { get; init; } = [];
}

public sealed class Releases(HttpClient client, string assetName = Releases.Asset)
{
    public const string Repository = "dggomes/optiscaler_backup";
    public const string Asset = "optibackup-win-x64.exe";
    public const string UiAsset = "optibackup-ui-win-x64.exe";
    public static Version Current => typeof(Releases).Assembly.GetName().Version!;

    public static bool NewerStable(string tag, Version current) => !string.IsNullOrWhiteSpace(tag) && !tag.Contains('-') &&
        Version.TryParse(tag.TrimStart('v', 'V'), out var version) && Normalize(version) > Normalize(current);
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public async Task<ReleaseInfo?> Check(CancellationToken cancellation = default, bool includeCurrent = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("OptiScalerBackup/" + Current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return Parse(doc.RootElement, Current, assetName, includeCurrent);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new IOException("Unexpected GitHub release metadata; update check was stopped.", error); }
    }

    public static ReleaseInfo? Parse(JsonElement root, Version current, string assetName = Asset, bool includeCurrent = false)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? throw new IOException("Release tag is missing.");
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || (!NewerStable(tag, current) && !(includeCurrent && !tag.Contains('-') && Version.TryParse(tag.TrimStart('v', 'V'), out var equal) && Normalize(equal) == Normalize(current)))) return null;
        if (assetName is not (Asset or UiAsset)) throw new IOException("Unsupported application asset.");
        var urls = root.GetProperty("assets").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("browser_download_url").GetString()!);
        var assets = new Dictionary<string, ReleaseAsset>();
        foreach (var name in new[] { Asset, UiAsset })
        {
            if (!urls.TryGetValue(name, out var asset) || !urls.TryGetValue(name + ".sha256", out var checksum)) continue;
            ValidateUrl(asset); ValidateUrl(checksum); assets[name] = new(asset, checksum);
        }
        if (!assets.TryGetValue(assetName, out var selected)) throw new IOException("Release is missing the requested Windows executable or checksum.");
        return new(tag, root.GetProperty("body").GetString() ?? "", root.GetProperty("html_url").GetString()!, selected.Url, selected.ChecksumUrl) { AssetName = assetName, Assets = assets };
    }

    public static ReleaseInfo ForAsset(ReleaseInfo release, string asset)
    {
        if (asset == release.AssetName) return release;
        if (!(release.Assets ?? []).TryGetValue(asset, out var selected) || selected == null) throw new IOException("Release is missing companion executable/checksum: " + asset);
        return release with { AssetName = asset, AssetUrl = selected.Url, ChecksumUrl = selected.ChecksumUrl };
    }

    public async Task<string> Download(ReleaseInfo release, string directory, CancellationToken cancellation = default)
        => (await DownloadVerified(release, directory, cancellation)).Path;

    public async Task<VerifiedDownload> DownloadVerified(ReleaseInfo release, string directory, CancellationToken cancellation = default)
    {
        if (release.AssetName is not (Asset or UiAsset)) throw new IOException("Unsupported application asset.");
        ValidateUrl(release.AssetUrl); ValidateUrl(release.ChecksumUrl);
        SafePaths.NoLinks(directory);
        Directory.CreateDirectory(directory);
        var checksumText = await client.GetStringAsync(release.ChecksumUrl, cancellation);
        var hash = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (hash?.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid release checksum.");
        var file = Path.Combine(directory, release.AssetName);
        SafePaths.Space(file, 512L * 1024 * 1024);
        try
        {
            using var response = await client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512L * 1024 * 1024) throw new IOException("Update exceeds 512 MiB limit.");
            using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long size = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    size += count;
                    if (size > 512L * 1024 * 1024) throw new IOException("Update exceeds 512 MiB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                output.Flush(true);
            }
            if (SafePaths.Hash(file) != hash) throw new IOException("Update checksum mismatch; executable was not replaced.");
            return new(file, hash);
        }
        catch { if (File.Exists(file)) File.Delete(file); throw; }
    }

    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal))
            throw new IOException("Update asset must belong to the configured GitHub repository over HTTPS.");
    }
}
