using System.IO.Compression;

namespace OptiScalerBackup.Core;

public sealed class Packages(Store store)
{
    public ImportPlan Inspect(Game game, string archive)
    {
        if (!Directory.Exists(game.Directory)) throw new IOException("Game directory is unavailable.");
        var archiveHash = SafePaths.Hash(archive);
        using var zip = ZipFile.OpenRead(archive);
        var entries = Validate(zip);
        var paths = SafePaths.Walk(game.Directory).Select(p => Path.GetRelativePath(game.Directory, p).Replace('\\', '/')).ToList();
        var wrapper = entries.Count > 0 && entries.All(e => e.FullName.Replace('\\', '/').Contains('/'))
            ? entries[0].FullName.Replace('\\', '/').Split('/')[0] + "/" : null;
        if (wrapper != null && !entries.All(e => e.FullName.Replace('\\', '/').StartsWith(wrapper, StringComparison.Ordinal))) wrapper = null;
        var result = new List<ImportEntry>();
        var staging = Path.Combine(store.PayloadRoot, "staging", Format.Id());
        SafePaths.NoLinks(staging);
        Directory.CreateDirectory(staging);
        try
        {
            long total = 0;
            foreach (var entry in entries)
            {
                var entryName = SafePaths.Relative(entry.FullName);
                var relative = wrapper == null ? entryName : entryName[wrapper.Length..];
                var exact = paths.Where(p => p.Equals(entryName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 0) exact = paths.Where(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase)).ToList();
                var candidates = exact.Count > 0 ? exact : paths.Where(p => Path.GetFileName(p).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase)).ToList();
                var suggested = candidates.Count == 1 ? candidates[0] : candidates.Count == 0 ? relative : null;
                var temp = Path.Combine(staging, Format.Id());
                using (var input = entry.Open())
                using (var output = new FileStream(temp, FileMode.CreateNew))
                {
                    var buffer = new byte[81920];
                    int n;
                    long length = 0;
                    while ((n = input.Read(buffer)) > 0)
                    {
                        length += n; total += n;
                        if (length > entry.Length || total > store.Data.Settings.MaxArchiveBytes) throw new IOException("Archive extraction limit exceeded.");
                        output.Write(buffer, 0, n);
                    }
                    if (length != entry.Length) throw new IOException("Archive entry length mismatch.");
                }
                result.Add(new(entryName, store.Capture(temp), entry.Length, suggested, candidates));
            }
        }
        finally { Directory.Delete(staging, true); }
        if (SafePaths.Hash(archive) != archiveHash) throw new IOException("Archive changed during import. Retry.");
        return new(archiveHash, result);
    }

    public Package Import(Game game, ImportPlan plan, string name, string version, IReadOnlyDictionary<string, string?> mappings)
    {
        var package = new Package { Name = Format.Label(name), Version = Format.Label(version), ArchiveHash = plan.ArchiveHash };
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Files)
        {
            if (!mappings.TryGetValue(entry.Entry, out var dest)) throw new InvalidOperationException($"Entry needs a reviewed mapping or exclusion: {entry.Entry}");
            if (dest == null) { package.ExcludedEntries.Add(entry.Entry); continue; }
            var relative = SafePaths.Relative(dest);
            if (!destinations.Add(relative)) throw new IOException($"Destination collision: {relative}");
            var target = new Target("game", relative);
            SafePaths.Resolve(game, target);
            store.Verify(entry.Hash);
            package.Files.Add(new(target, entry.Hash, entry.Size));
            package.Mappings.Add(new(entry.Entry, target, entry.Hash, entry.Size));
        }
        if (package.Files.Count == 0) throw new InvalidOperationException("Package has no included files.");
        SafePaths.DistinctDestinations(package.Files.Select(f => SafePaths.Resolve(game, f.Target)));
        var next = Format.Clone(game);
        next.Packages.Add(package);
        store.Put(next);
        return package;
    }

    private List<ZipArchiveEntry> Validate(ZipArchive zip)
    {
        var files = new List<ZipArchiveEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        if (zip.Entries.Count > store.Data.Settings.MaxArchiveFiles) throw new IOException("Archive file-count limit exceeded.");
        foreach (var e in zip.Entries)
        {
            var unixKind = (e.ExternalAttributes >> 16) & 0xF000;
            if (unixKind != 0 && unixKind != 0x8000 && unixKind != 0x4000 ||
                (e.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Archive contains link or special entry: {e.FullName}");
            var directory = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            var name = SafePaths.Relative(directory ? e.FullName[..^1] : e.FullName);
            if (!names.Add(name)) throw new IOException($"Case-insensitive archive collision: {name}");
            if (directory) continue;
            if (e.Length < 0 || e.Length > store.Data.Settings.MaxArchiveBytes - size) throw new IOException("Archive expanded-size limit exceeded.");
            size += e.Length;
            files.Add(e);
        }
        if (files.Count == 0) throw new IOException("ZIP contains no files.");
        SafePaths.Space(store.PayloadRoot, checked(size * 2));
        return files;
    }
}
