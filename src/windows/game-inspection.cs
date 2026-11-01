using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed record CurrentComponent(string Path, string Component, string Version, string Evidence);
public sealed record GameInspection(DateTimeOffset CheckedUtc, bool Available, List<CurrentComponent> Components, GameSettingsReport Settings, List<string> Warnings);

public static class GameInspector
{
    public const long MaxBinaryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    public static GameInspection Read(Game game, ComponentFingerprints catalog, CancellationToken cancellation = default)
    {
        var warnings = new List<string>(); var components = new List<CurrentComponent>();
        cancellation.ThrowIfCancellationRequested();
        if (!Directory.Exists(game.Directory))
            return new(DateTimeOffset.UtcNow, false, [], new([], []), ["Game folder unavailable; installed versions could not be checked."]);
        var detected = Detection.Files(game, warnings.Add, cancellation, maxFiles: 50_000);
        var targets = game.Managed.Concat(detected.Select(file => new Target("game", file.Path))).DistinctBy(target => target.Key).ToList();
        var binaries = targets.Where(target => Path.GetExtension(target.Path).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(target.Path).Equals(".asi", StringComparison.OrdinalIgnoreCase)).ToList();
        if (binaries.Count > 128) warnings.Add("Only the first 128 component candidates were inspected.");
        long total = 0;
        foreach (var target in binaries.Take(128))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var path = SafePaths.Resolve(game, target);
                if (!SafePaths.FileExists(path)) continue;
                var size = new FileInfo(path).Length;
                if (size > MaxBinaryBytes || size > MaxTotalBytes - total) { warnings.Add(target.Path + ": component hashing size limit reached."); continue; }
                total += size;
                components.AddRange(Inspect(path, target.Root + ":" + target.Path, catalog, cancellation));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
            { warnings.Add(target.Path + ": " + error.Message); }
        }
        cancellation.ThrowIfCancellationRequested();
        var approved = Format.Clone(game); approved.Managed = targets.Where(target => GameSettingsReader.IsSettings(target.Path)).Take(64).ToList();
        if (targets.Count(target => GameSettingsReader.IsSettings(target.Path)) > 64) warnings.Add("Only the first 64 supported settings files were read.");
        var settings = GameSettingsReader.Current(approved, cancellation);
        return new(DateTimeOffset.UtcNow, true, components, settings, warnings.Distinct().Take(20).ToList());
    }

    private static List<CurrentComponent> Inspect(string path, string relative, ComponentFingerprints catalog, CancellationToken cancellation)
    {
        SafePaths.NoLinks(path); var before = new FileInfo(path); var modified = before.LastWriteTimeUtc; var length = before.Length;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != length || input.Length > MaxBinaryBytes) throw new IOException("Component changed or exceeds its read bound; retry.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; int count; long read = 0;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested(); read += count;
            if (read > length) throw new IOException("Component changed while reading; retry.");
            hash.AppendData(buffer, 0, count);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset()); var matches = catalog.Match(digest, length);
        var info = matches.Count == 0 ? FileVersionInfo.GetVersionInfo(path) : null;
        SafePaths.NoLinks(path); var after = new FileInfo(path);
        if (read != length || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("Component changed while reading; retry.");
        if (matches.Count > 0) return matches.Select(match => new CurrentComponent(relative, match.Component + (match.Role.Length > 0 ? " · " + match.Role : ""), match.Version, "SHA-256 catalog match")).Distinct().ToList();
        var component = Family(path, info?.ProductName ?? "");
        var version = Clean(string.IsNullOrWhiteSpace(info?.FileVersion) ? info?.ProductVersion ?? "" : info.FileVersion);
        return [new(relative, component, version.Length == 0 ? "Unknown version" : version, version.Length == 0 ? "No catalog match or DLL version metadata" : "DLL metadata only; not hash-verified")];
    }
    private static string Clean(string text) => new string(text.Where(character => !char.IsControl(character)).Take(100).ToArray()).Trim();
    private static string Family(string path, string product)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if ((product.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) || product.StartsWith("OptiScaler ", StringComparison.OrdinalIgnoreCase)) || name.StartsWith("optiscaler")) return "OptiScaler";
        if (name.Contains("optipatcher")) return "OptiPatcher";
        if (name.StartsWith("libxess_fg")) return "XeSS FG (XeFG)";
        if (name.StartsWith("libxess")) return "XeSS";
        if (name.StartsWith("libxell")) return "XeLL";
        if (name.Contains("dlssg_to_fsr3")) return "DLSS-to-FSR FG";
        if (name.StartsWith("nvngx_dlssg") || name == "sl.dlss_g.dll") return "DLSS FG";
        if (name.StartsWith("nvngx_dlssd")) return "DLSS Ray Reconstruction";
        if (name.StartsWith("nvngx_dlss") || name == "sl.dlss.dll") return "DLSS";
        if (name.StartsWith("amd_fidelityfx") || name.StartsWith("amdxcffx")) return name.Contains("framegeneration") ? "FSR FG" : "FSR";
        return Clean(Path.GetFileName(path));
    }
}
