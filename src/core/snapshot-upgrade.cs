namespace OptiScalerBackup.Core;

public sealed partial class Store
{
    private void Upgrade()
    {
        if (Data.SchemaVersion != 1) return;
        var original = Path.Combine(Home, "catalog.json");
        var backup = Path.Combine(Home, "catalog.v1.json");
        SafePaths.NoLinks(backup);
        if (!File.Exists(backup)) File.Copy(original, backup, false);
        var upgraded = Format.Clone(Data);
        // Completed folders from an interrupted conversion need no second copy.
        var remaining = upgraded.Games.Sum(g => AllSnapshots(g).DistinctBy(s => s.Id)
            .Where(s => !Directory.Exists(StoragePath(s.Folder ?? SnapshotFolder(g, s))))
            .Sum(s => s.Files.Where(f => f.Hash != null).Sum(f => f.Size)));
        SafePaths.Space(PayloadRoot, remaining);
        foreach (var game in upgraded.Games) ConvertLegacySnapshots(game);
        upgraded.SchemaVersion = Format.Version;
        Atomic(original, upgraded);
        Data = upgraded;
    }

    public void ConvertLegacySnapshots(Game game)
    {
        foreach (var group in AllSnapshots(game).GroupBy(s => s.Id))
        {
            var copies = group.ToList();
            var snapshot = copies.First();
            var fingerprint = string.Join('|', snapshot.Files.OrderBy(f => f.Target.Key).Select(f => $"{f.Target.Key}:{f.Hash}:{f.Size}"));
            if (copies.Any(s => string.Join('|', s.Files.OrderBy(f => f.Target.Key).Select(f => $"{f.Target.Key}:{f.Hash}:{f.Size}")) != fingerprint))
                throw new IOException($"Conflicting copies of snapshot {snapshot.Id}; catalog was not changed.");
            if (snapshot.Folder == null || snapshot.Files.Any(f => f.Hash != null && f.Payload == null))
            {
                var sources = snapshot.Files.Where(f => f.Hash != null).Select(f =>
                {
                    Verify(f);
                    return (f.Target, Payload(f));
                }).ToList();
                CaptureSnapshot(game, snapshot, sources);
            }
            foreach (var copy in copies)
            {
                copy.Folder = snapshot.Folder;
                copy.Files = snapshot.Files.ToList();
                copy.Name = snapshot.Name;
            }
        }
    }
}
