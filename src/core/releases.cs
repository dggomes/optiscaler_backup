using System.Net;
using System.Text.Json;

namespace OptiScalerBackup.Core;

public sealed record ReleaseInfo(string Tag, string Notes, string Page, string AssetUrl, string ChecksumUrl);

public sealed class Releases(HttpClient client)
{
    public const string Repository = "dggomes/optiscaler_backup";
    public const string Asset = "optibackup-win-x64.exe";
    public static Version Current => typeof(Releases).Assembly.GetName().Version!;

    public static string DisplayVersion => Current.Revision > 0 ? Current.ToString(4) : Current.ToString(3);
    public static bool VersionEquals(Version left, Version right) => CompareVersions(left, right) == 0;
    public static int CompareVersions(Version left, Version right) => Normalize(left).CompareTo(Normalize(right));

    public static bool NewerStable(string tag, Version current) => !tag.Contains('-') &&
        Version.TryParse(tag.TrimStart('v', 'V'), out var version) && Normalize(version) > Normalize(current);
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public async Task<ReleaseInfo?> Check(CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("OptiScalerBackup/" + Current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return Parse(doc.RootElement, Current);
    }

    public static ReleaseInfo? Parse(JsonElement root, Version current)
    {
        var tag = root.GetProperty("tag_name").GetString()!;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || !NewerStable(tag, current)) return null;
        string? asset = null, checksum = null;
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            if (name == Asset) asset = item.GetProperty("browser_download_url").GetString();
            if (name == Asset + ".sha256") checksum = item.GetProperty("browser_download_url").GetString();
        }
        if (asset == null || checksum == null) throw new IOException("Release is missing Windows executable or checksum.");
        ValidateUrl(asset); ValidateUrl(checksum);
        return new(tag, root.GetProperty("body").GetString() ?? "", root.GetProperty("html_url").GetString()!, asset, checksum);
    }

    public async Task<string> Download(ReleaseInfo release, string directory, CancellationToken cancellation = default)
    {
        ValidateUrl(release.AssetUrl); ValidateUrl(release.ChecksumUrl);
        SafePaths.NoLinks(directory);
        Directory.CreateDirectory(directory);
        var checksumText = await client.GetStringAsync(release.ChecksumUrl, cancellation);
        var hash = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (hash?.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid release checksum.");
        var file = Path.Combine(directory, Asset);
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
            return file;
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
