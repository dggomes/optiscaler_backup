using System.Collections.Immutable;

namespace OptiScalerBackup.Core;

public sealed record UpstreamSource(string Id, string RepositoryUrl, string DocumentPath, string ReviewedRevision,
    SourcePurpose Purpose, string LicenseReview);

public static class UpstreamSources
{
    public static ImmutableArray<UpstreamSource> All { get; } =
    [
        new("optipatcher-release", "https://github.com/optiscaler/OptiPatcher", "GameSupport.md", "5ec46fc7a1ac1aad0ac0aa8dcf71065ce43950fc",
            SourcePurpose.ReleasedCompatibility, "Reference recorded; redistribution review required before bundling data"),
        new("optipatcher-main", "https://github.com/optiscaler/OptiPatcher", "GameSupport.md", "c39c29efa41e2fa4e6c4e9447fc02eff468a4f8c",
            SourcePurpose.PreviewCompatibility, "Reference recorded; unreleased observations stay separate"),
        new("optiscaler-wiki", "https://github.com/optiscaler/OptiScaler", "wiki/Compatibility-List", "",
            SourcePurpose.CompatibilityWiki, "Mutable wiki; response hash required; redistribution review pending"),
        new("optiscaler-client-reference", "https://github.com/Optiscaler-Client/Optiscaler-Client", "Services", "f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f",
            SourcePurpose.ReferenceOnly, "GPLv3 license file reviewed; no code/data vendored"),
        new("opticlick-reference", "https://github.com/Alili100/OptiClick", "OptiClick.Wpf/Install", "5d73cf8de9b83529e34db4fc9f6b52345ad359da",
            SourcePurpose.ReferenceOnly, "GPLv3 license file reviewed; no code/data vendored"),
        new("opticlick-historical-reference", "https://github.com/red-Geck0/OptiClick", "assets/data", "a9f20bbdaead22177fae7dcab5443f7ff1c5db31",
            SourcePurpose.ReferenceOnly, "Historical data; specific reuse/license review pending; not vendored")
    ];

    public static UpstreamSource Get(string id) => All.SingleOrDefault(s => s.Id == id)
        ?? throw new IOException("Unknown upstream source: " + id);

    public static void Validate(SourceEvidence evidence, DateTimeOffset now)
    {
        var source = Get(evidence.SourceId);
        if (source.Purpose == SourcePurpose.ReferenceOnly) throw new IOException("Reference-only sources cannot supply compatibility packs.");
        if (!Uri.TryCreate(evidence.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort || !evidence.Url.StartsWith(source.RepositoryUrl + "/", StringComparison.Ordinal))
            throw new IOException("Evidence URL does not belong to its declared upstream source.");
        if (!CompatibilityPacks.IsHash(evidence.ResponseSha256) || evidence.ParserSchema < 1 || evidence.ParserSchema > 1000
            || evidence.FetchedUtc < DateTimeOffset.UnixEpoch || evidence.FetchedUtc > now.AddMinutes(5))
            throw new IOException("Invalid evidence hash, parser version or fetch time.");
        if (source.Purpose == SourcePurpose.CompatibilityWiki)
        {
            if (!uri.AbsolutePath.StartsWith("/optiscaler/OptiScaler/wiki/", StringComparison.Ordinal)) throw new IOException("Expected official wiki evidence.");
        }
        else if (evidence.Revision.Length != 40 || evidence.Revision.Any(c => !char.IsAsciiHexDigit(c))
            || uri.AbsolutePath != "/optiscaler/OptiPatcher/blob/" + evidence.Revision + "/GameSupport.md"
            || string.IsNullOrWhiteSpace(evidence.ComponentVersion) || evidence.ComponentVersion.Length > 80)
            throw new IOException("OptiPatcher evidence requires an exact commit, document and component version.");
    }
}
