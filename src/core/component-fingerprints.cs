using System.Reflection;
using System.Text.Json;

namespace OptiScalerBackup.Core;

public sealed record ComponentFingerprint(string Component, string Role, string Version, string FileName, string Sha256, long Size,
    string Source, string SourceRevision, string ReleaseTag, string AssetId, string AssetUrl, string ArchiveSha256, string ArchiveEntry, string Channel);
public sealed record FingerprintSource(string Revision, string Url, string Sha256, string Signature, string PublicKey);
public sealed record FingerprintCheck(int Version, DateTimeOffset CheckedUtc);
public sealed record FingerprintDocument(int Version, List<ComponentFingerprint> Records, List<FingerprintSource> Sources);
public sealed record IdentifiedComponent(Target Target, string Hash, bool BytesBackedUp, List<ComponentFingerprint> Matches);
public sealed record UnknownComponent(Target Target, string Component, string Hash, bool BytesBackedUp);
public sealed record BackupComponents(string CatalogSha256, string Coverage, List<IdentifiedComponent> Identified, List<UnknownComponent> Unknown, List<string> Warnings)
{
    public string Summary => Identified.Count == 0 ? Unknown.Count == 0 ? "No known component recorded" : "Component files · unknown versions"
        : string.Join(" · ", Identified.SelectMany(file => file.Matches.Select(match => new
        {
            Name = match.Component + (match.Component == "FSR" ? " " + match.Role + " file" : "") + " " + match.Version,
            file.BytesBackedUp
        })).GroupBy(item => item.Name).Select(group => group.Key + (group.Any(item => item.BytesBackedUp) ? "" : " [inventory only]")));

}

public sealed class ComponentFingerprints
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public const string Url = "https://raw.githubusercontent.com/dggomes/optiscaler_backup/main/src/core/component-fingerprints.json";
    private readonly Dictionary<string, List<ComponentFingerprint>> hashes;
    public FingerprintDocument Document { get; }
    public string Sha256 { get; }
    private ComponentFingerprints(FingerprintDocument document, string sha256)
    { Document = document; Sha256 = sha256; hashes = document.Records.GroupBy(r => r.Sha256.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList()); }
    public static ComponentFingerprints Bundled()
    {
        using var input = typeof(ComponentFingerprints).Assembly.GetManifestResourceStream("OptiScalerBackup.Core.component-fingerprints.json")
            ?? throw new IOException("Bundled component catalog is missing.");
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return Read(bytes.ToArray());
    }
    public static ComponentFingerprints Read(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new IOException("Component catalog exceeds its size bound.");
        FingerprintDocument document;
        try
        {
            document = JsonSerializer.Deserialize<FingerprintDocument>(bytes, new JsonSerializerOptions
            { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 })
                ?? throw new IOException("Empty component catalog.");
            if (document.Version != 1) throw new IOException("Unsupported component catalog format; data was not rewritten.");
            if (document.Records == null || document.Sources == null || document.Records.Count is 0 or > 100_000 || document.Sources.Count > 10_000)
                throw new IOException("Invalid component catalog counts.");
            foreach (var record in document.Records)
            {
                if (record.Component is not ("OptiScaler" or "OptiPatcher" or "FSR") || !CompatibilityPacks.IsHash(record.Sha256)
                    || record.Size is <= 0 or > 268435456 || record.Source is not ("optiscaler/OptiScaler" or "optiscaler/OptiPatcher" or "DLSSync signed catalog"))
                    throw new IOException("Invalid component fingerprint.");
                foreach (var label in new[] { record.Role, record.Version, record.FileName, record.SourceRevision, record.ReleaseTag, record.AssetId, record.Channel }) Format.Label(label);
                if (SafePaths.Relative(record.FileName) != Path.GetFileName(record.FileName)) throw new IOException("Expected a component filename.");
                if (!Uri.TryCreate(record.AssetUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("github.com" or "raw.githubusercontent.com" or "dlss-swapper-downloads.beeradmoore.com") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
                    throw new IOException("Invalid component provenance URL.");
                if (record.ArchiveSha256.Length > 0 && !CompatibilityPacks.IsHash(record.ArchiveSha256)) throw new IOException("Invalid archive digest.");
            }
            foreach (var source in document.Sources)
                if (!CompatibilityPacks.IsHash(source.Sha256) || source.Revision.Length != 40 || source.Revision.Any(c => !char.IsAsciiHexDigit(c))
                    || source.Url != "https://raw.githubusercontent.com/xt0n1-t3ch/DLSSync-Manifest/" + source.Revision + "/manifest-v3.json"
                    || source.Signature.Length != 128 || source.Signature.Any(c => !char.IsAsciiHexDigit(c)) || source.PublicKey != "e9dd0828f9ee5ecb72e0a811723a79c6e5373ca1c20bd5b255d68a2b3928fcd3")
                    throw new IOException("Invalid upstream catalog source evidence.");
            var sourceIds = document.Sources.Select(s => s.Revision).ToHashSet();
            if (document.Records.Any(r => r.Source == "DLSSync signed catalog" && !sourceIds.Contains(r.SourceRevision)))
                throw new IOException("Missing component catalog source evidence.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw new IOException("Invalid component catalog; recognition kept its last usable catalog.", error); }
        return new(document, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
    public BackupComponents Identify(Store store, Snapshot snapshot)
    {
        var warnings = new List<string>(); var coverage = "Recorded backup files only";
        var identified = new List<IdentifiedComponent>(); var unknown = new List<UnknownComponent>();
        var recorded = new HashSet<string>();
        void Analyze(Target target, string hash, long size, bool bytes)
        {
            if (hashes.TryGetValue(hash.ToLowerInvariant(), out var matches))
            {
                var exact = matches.Where(m => m.Size == size).ToList();
                if (exact.Count > 0) { identified.Add(new(target, hash, bytes, exact)); return; }
            }
            var name = Path.GetFileName(target.Path);
            if (!new[] { ".dll", ".asi" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return;
            var component = name.Contains("optipatcher", StringComparison.OrdinalIgnoreCase) ? "OptiPatcher"
                : name.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("amdxcffx", StringComparison.OrdinalIgnoreCase) ? "FSR"
                : name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("OptiScaler.asi", StringComparison.OrdinalIgnoreCase) ? "OptiScaler" : null;
            if (component != null) unknown.Add(new(target, component, hash, bytes));
        }
        foreach (var file in snapshot.Files)
        {
            recorded.Add(file.Target.Key);
            if (file.Hash != null) Analyze(file.Target, file.Hash, file.Size, true);
        }
        try
        {
            if (store.Inventory(snapshot) is { } inventory)
            {
                coverage = inventory.ExcludedLinks.Count == 0 ? "Recorded files and game inventory" : "Recorded files and game inventory (linked paths excluded)";
                foreach (var file in inventory.Files)
                {
                    var target = new Target("game", file.Path);
                    if (!recorded.Contains(target.Key)) Analyze(target, file.Hash, file.Size, false);
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        { warnings.Add("Inventory was unavailable: " + error.Message); }
        return new(Sha256, coverage, identified, unknown, warnings);
    }
    public static string CachePath(string home) => Path.Combine(home, "components", "fingerprints.json");
    public static ComponentFingerprints Load(string home)
    {
        try
        {
            var file = CachePath(home); SafePaths.NoLinks(file);
            if (File.Exists(file)) return Read(BoundedFile(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine("Component catalog cache: " + error.Message); }
        return Bundled();
    }
    private static byte[] BoundedFile(string file)
    {
        if (new FileInfo(file).Length > MaxBytes) throw new IOException("Oversized component catalog cache.");
        return File.ReadAllBytes(file);
    }
    public async Task<ComponentFingerprints> Refresh(HttpClient client, string home, CancellationToken cancellation = default)
    {
        var file = CachePath(home); SafePaths.NoLinks(file);
        if (File.Exists(file)) Read(BoundedFile(file)); // Unknown formats are preserved, never replaced.
        var checkedFile = Path.Combine(home, "components", "checked.json"); SafePaths.NoLinks(checkedFile);
        if (File.Exists(checkedFile) && (new FileInfo(checkedFile).Length > 16384 || Store.Read<FingerprintCheck>(checkedFile).Version != 1))
            throw new IOException("Unsupported recognition check metadata; no data changed.");
        Store.Atomic(checkedFile, new FingerprintCheck(1, DateTimeOffset.UtcNow));
        var bytes = await Download(client, cancellation); var next = Read(bytes);
        var entries = next.Document.Records.ToHashSet();
        if (Document.Records.Any(r => !entries.Contains(r))) throw new IOException("Catalog refresh would discard historical fingerprints; previous catalog retained.");
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Format.Id() + ".tmp";
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { target.Write(bytes); target.Flush(true); }
            SafePaths.NoLinks(file); File.Move(temporary, file, true);

        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return next;
    }
    private static async Task<byte[]> Download(HttpClient client, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("Oversized component catalog response.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > MaxBytes) throw new IOException("Oversized component catalog response.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new IOException("Recognition metadata refresh timed out; cached catalog retained.", error); }
    }

}
