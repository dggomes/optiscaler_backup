using System.Collections.Immutable;

namespace OptiScalerBackup.Core;

public enum SupportState { Unknown, Supported, Unsupported, NotApplicable }
public enum SourcePurpose { ReleasedCompatibility, PreviewCompatibility, CompatibilityWiki, ReferenceOnly }
public enum MatchKind { Unmatched, LauncherId, ExactTitle, Ambiguous }
public sealed record SourceEvidence(string SourceId, string Url, string Revision, string ResponseSha256,
    DateTimeOffset FetchedUtc, int ParserSchema, string? ComponentVersion);
public sealed record CompatibilityObservation(SourceEvidence Evidence, SupportState Dlss, SupportState DlssFg, string Notes);
public sealed record CompatibilityGame(string Id, string Title, ImmutableDictionary<string, string> LauncherIds,
    ImmutableArray<CompatibilityObservation> Observations);
public sealed record CompatibilityDataPack(int FormatVersion, string Tag, string MinimumAppVersion,
    DateTimeOffset CreatedUtc, ImmutableArray<CompatibilityGame> Games);
public sealed record GameMatch(string? GameId, MatchKind Kind, ImmutableArray<string> Candidates);

// These contracts describe resolved inputs; they neither resolve downloads nor execute changes.
public sealed record GameAnalysis(string GameId, string? RuntimeExecutable, string? PreferredGpuId,
    ImmutableArray<string> ExecutableCandidates, ImmutableArray<SourceEvidence> Evidence, ImmutableArray<string> Warnings);
public sealed record ResolvedComponent(string Id, string Version, string Channel, string AssetUrl,
    string Sha256, SourceEvidence Evidence);
public sealed record FilePlanPrecondition(Target Target, string? ExpectedHash);
public sealed record FileOperationPlan(string Id, string GameId, string DataPackHash, GameAnalysis Analysis,
    ImmutableArray<ResolvedComponent> Components, ImmutableArray<FileState> DesiredFiles,
    ImmutableArray<FilePlanPrecondition> Preconditions, ImmutableArray<string> Warnings);
