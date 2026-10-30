using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptiScalerBackup.Core;

internal static class CompatibilityPackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter<SupportState>() } };
    private static SourceEvidence Evidence(string id = "optipatcher-release")
    {
        var source = UpstreamSources.Get(id);
        return new(id, source.RepositoryUrl + "/blob/" + source.ReviewedRevision + "/GameSupport.md", source.ReviewedRevision,
            new string('a', 64), Now, 1, "v0.42");
    }
    private static CompatibilityObservation Observation(SupportState dlss = SupportState.Supported, string source = "optipatcher-release") =>
        new(Evidence(source), dlss, SupportState.NotApplicable, "Review game version and source notes.");
    private static CompatibilityGame Game(string id, string title, string? steam = null) => new(id, title,
        steam == null ? ImmutableDictionary<string, string>.Empty : ImmutableDictionary<string, string>.Empty.Add("steam", steam), [Observation()]);
    private static CompatibilityDataPack Pack(params CompatibilityGame[] games) => new(1, "data-v2026.10.30.1", "0.3.1", Now, [.. games]);
    private static CompatibilityDataPack Read(CompatibilityDataPack pack) => Read(JsonSerializer.SerializeToUtf8Bytes(pack, Json));
    private static CompatibilityDataPack Read(byte[] bytes, string? hash = null) => CompatibilityPacks.Read(bytes,
        hash ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), new Version(0, 3, 1), Now);
    public static void Provenance()
    {
        var game = Game("achilles", "Achilles: Legends Untold");
        // Conflicting/released/main observations remain separate rather than becoming a Boolean.
        game = game with { Observations = [Observation(), Observation(SupportState.Unsupported, "optipatcher-main")] };
        var read = Read(Pack(game));
        Check(read.Games[0].Observations.Length == 2 && read.Games[0].Observations[0].DlssFg == SupportState.NotApplicable, "Support evidence was collapsed.");
        Check(UpstreamSources.Get(read.Games[0].Observations[1].Evidence.SourceId).Purpose == SourcePurpose.PreviewCompatibility, "Main evidence appeared released.");
        var unknown = Observation(SupportState.Unknown) with { DlssFg = SupportState.Unknown, Notes = "Unknown symbol and unparsed prose stay unknown." };
        Check(Read(Pack(game with { Observations = [unknown] })).Games[0].Observations[0] == unknown, "Unknown support/prose changed.");
        Reject(() => Read(Pack(game with { Observations = [Observation() with { Evidence = Evidence("optiscaler-client-reference") }] })));
        Reject(() => Read(Pack(game with { Observations = [Observation() with { Evidence = Evidence() with { Url = "https://github.com/other/project/blob/main/GameSupport.md" } }] })));
        Reject(() => Read(Pack(game with { Observations = [Observation() with { Evidence = Evidence() with { ResponseSha256 = "unverified" } }] })));
        Reject(() => Read(Pack(game with { Observations = [Observation() with { Evidence = Evidence() with { Revision = "main" } }] })));
        var wiki = new SourceEvidence("optiscaler-wiki", "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List", "", new string('b', 64), Now, 1, null);
        Read(Pack(game with { Observations = [unknown with { Evidence = wiki }] }));
    }
    public static void Guards()
    {
        var pack = Pack(Game("achilles", "Achilles: Legends Untold"));
        Reject(() => Read(pack with { FormatVersion = 99 }));
        Reject(() => Read(pack with { MinimumAppVersion = "99.0.0" }));
        Reject(() => Read(pack with { Games = [] }));
        Reject(() => Read(pack with { Games = [pack.Games[0], pack.Games[0]] }));
        Reject(() => Read(pack with { Tag = "v1.0.0" }));
        Reject(() => Read(pack with { CreatedUtc = Now.AddDays(1) }));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pack, Json);
        Reject(() => Read(bytes, new string('0', 64)));
        Reject(() => Read(new byte[CompatibilityPacks.MaxBytes + 1]));
        var text = Encoding.UTF8.GetString(bytes);
        Reject(() => Read(Encoding.UTF8.GetBytes(text[..^1])));
        Reject(() => Read(Encoding.UTF8.GetBytes(text.Replace("\"FormatVersion\":1", "\"FormatVersion\":1,\"FormatVersion\":1"))));
        Reject(() => Read(Encoding.UTF8.GetBytes(text.Replace("\"FormatVersion\":1", "\"FormatVersion\":1,\"Command\":\"setup.exe\""))));
        Reject(() => Read(Encoding.UTF8.GetBytes(text.Replace("\"Supported\"", "1"))));
        Check(!Releases.NewerStable(pack.Tag, new Version(0, 3, 1)) && CompatibilityPacks.IsDataTag(pack.Tag), "App/data release channels overlap.");
    }
    public static void Matching()
    {
        var pack = Read(Pack(Game("original", "A Game", "100"), Game("remake", "A Game Remake", "101"), Game("other", "A Game", "102")));
        Check(CompatibilityPacks.Match(pack, "A Game", new Dictionary<string, string>()).Kind == MatchKind.Ambiguous, "Ambiguous title auto-matched.");
        Check(CompatibilityPacks.Match(pack, " a  game remake ", new Dictionary<string, string>()).GameId == "remake", "Exact normalized title did not match.");
        Check(CompatibilityPacks.Match(pack, "A Game", new Dictionary<string, string> { ["steam"] = "101" }).GameId == "remake", "Launcher identity lost priority.");
        Check(CompatibilityPacks.Match(pack, "A Game Remake", new Dictionary<string, string> { ["steam"] = "999" }).Kind == MatchKind.Unmatched, "Conflicting launcher identity fell back to a title match.");
        var conflictingGame = Game("conflict", "A Game", "100") with { LauncherIds = ImmutableDictionary<string, string>.Empty.Add("steam", "100").Add("epic", "epic-a") };
        Check(CompatibilityPacks.Match(Read(Pack(conflictingGame)), "A Game", new Dictionary<string, string> { ["steam"] = "100", ["epic"] = "epic-b" }).Kind == MatchKind.Ambiguous, "Conflicting known IDs were silently accepted.");
        Check(CompatibilityPacks.Match(pack, "A Game Rem", new Dictionary<string, string>()).Kind == MatchKind.Unmatched, "Fuzzy title was automatically matched.");
        Check(CompatibilityPacks.Match(pack, "A Game Remake", new Dictionary<string, string> { ["steam"] = "100", ["epic"] = "unknown" }).GameId == "original", "Edition identity was inferred from title over known ID.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Invalid compatibility pack was accepted.");
    }
}
