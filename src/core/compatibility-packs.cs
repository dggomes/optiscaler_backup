using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiScalerBackup.Core;

public static class CompatibilityPacks
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<SupportState>(allowIntegerValues: false) }
    };
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    public static bool IsDataTag(string tag)
    {
        var parts = tag.Split('-');
        return parts.Length == 2 && parts[0] == "data" && parts[1].StartsWith('v')
            && parts[1][1..].Split('.') is { Length: 4 } version && version.All(p => p.Length > 0 && p.Length <= 5 && p.All(char.IsAsciiDigit));
    }
    public static CompatibilityDataPack Read(ReadOnlySpan<byte> bytes, string reviewedSha256, Version appVersion, DateTimeOffset now)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes || !IsHash(reviewedSha256)
            || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(reviewedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Compatibility pack size or reviewed digest is invalid.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var pack = JsonSerializer.Deserialize<CompatibilityDataPack>(bytes, Json) ?? throw new IOException("Empty compatibility pack.");
            Validate(pack, appVersion, now); return pack;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new IOException("Invalid compatibility pack; no data activated.", error); }
    }
    private static void Validate(CompatibilityDataPack pack, Version appVersion, DateTimeOffset now)
    {
        if (pack.FormatVersion != 1 || !IsDataTag(pack.Tag) || !Version.TryParse(pack.MinimumAppVersion, out var minimum)
            || minimum > appVersion || pack.CreatedUtc < DateTimeOffset.UnixEpoch || pack.CreatedUtc > now.AddMinutes(5)
            || pack.Games.IsDefaultOrEmpty || pack.Games.Length > 20_000)
            throw new IOException("Unsupported, empty or incompatible compatibility pack.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in pack.Games)
        {
            if (!ids.Add(Format.Label(game.Id))) throw new IOException("Duplicate compatibility game ID.");
            Format.Label(game.Title);
            if (game.LauncherIds == null || game.LauncherIds.Count > 16 || game.Observations.IsDefaultOrEmpty || game.Observations.Length > 16)
                throw new IOException("Invalid game identity or evidence count.");
            foreach (var pair in game.LauncherIds) { Format.Label(pair.Key); Format.Label(pair.Value); }
            foreach (var observation in game.Observations)
            {
                UpstreamSources.Validate(observation.Evidence, now);
                if (observation.Evidence.FetchedUtc > pack.CreatedUtc.AddMinutes(5)) throw new IOException("Evidence postdates its data pack.");
                if (!Enum.IsDefined(observation.Dlss) || !Enum.IsDefined(observation.DlssFg) || observation.Notes == null || observation.Notes.Length > 16_384)
                    throw new IOException("Invalid support observation.");
            }
        }
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate property in compatibility pack.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
    public static GameMatch Match(CompatibilityDataPack pack, string title, IReadOnlyDictionary<string, string> launcherIds)
    {
        bool KnownMatch(CompatibilityGame game) => game.LauncherIds.Any(id => launcherIds.Any(selected =>
            selected.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase) && selected.Value.Equals(id.Value, StringComparison.OrdinalIgnoreCase)));
        bool KnownConflict(CompatibilityGame game) => game.LauncherIds.Any(id => launcherIds.Any(selected =>
            selected.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase) && !selected.Value.Equals(id.Value, StringComparison.OrdinalIgnoreCase)));
        var byId = pack.Games.Where(KnownMatch).ToList();
        if (byId.Any(KnownConflict)) return new(null, MatchKind.Ambiguous, byId.Select(g => g.Id).ToImmutableArray());
        if (byId.Count > 0) return Result(byId, MatchKind.LauncherId);
        var normalized = Normalize(title);
        return Result(pack.Games.Where(g => !KnownConflict(g) && Normalize(g.Title) == normalized).ToList(), MatchKind.ExactTitle);
    }
    private static string Normalize(string title) => string.Join(' ', title.Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static GameMatch Result(List<CompatibilityGame> matches, MatchKind kind) => matches.Count == 1
        ? new(matches[0].Id, kind, [matches[0].Id])
        : new(null, matches.Count == 0 ? MatchKind.Unmatched : MatchKind.Ambiguous, matches.Select(g => g.Id).ToImmutableArray());
}
