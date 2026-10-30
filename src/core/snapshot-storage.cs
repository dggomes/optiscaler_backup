namespace OptiScalerBackup.Core;

public sealed partial class Store
{
    private static string ShortId(string id) => SafePaths.FolderName(id[..Math.Min(8, id.Length)]);
    public string GameSnapshotFolder(Game game) => $"snapshots/{SafePaths.FolderName(game.Name)} ({ShortId(game.Id)})";
    private string SnapshotFolder(Game game, Snapshot snapshot, string? name = null) =>
        $"{GameSnapshotFolder(game)}/{snapshot.Created.LocalDateTime:yyyy-MM-dd HH-mm-ss} {SafePaths.FolderName(name ?? snapshot.Name)} ({ShortId(snapshot.Id)})";

    public string StoragePath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(PayloadRoot, SafePaths.Relative(relative, 4096)));
        if (!SafePaths.Within(PayloadRoot, path)) throw new IOException("Payload path escapes backup storage.");
        SafePaths.NoLinks(path);
        return path;
    }
    public string SnapshotPath(Snapshot snapshot) => StoragePath(snapshot.Folder ?? throw new IOException("Snapshot has no readable folder."));
    public string Payload(FileState state) => state.Hash == null ? throw new IOException("Absent file has no payload.") :
        state.Payload == null ? Blob(state.Hash) : StoragePath(state.Payload);
    public void Verify(FileState state)
    {
        if (state.Hash == null) return;
        var path = Payload(state);
        if (!SafePaths.FileExists(path) || new FileInfo(path).Length != state.Size || SafePaths.Hash(path) != state.Hash)
            throw new IOException($"Missing or modified payload {path}; operation cancelled.");
    }

    private static string InsideSnapshot(Game game, Target target)
    {
        var group = target.Root == "game" ? "game" : game.ExternalRoots.TryGetValue(target.Root, out var root)
            ? $"{SafePaths.FolderName(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)))} ({ShortId(target.Root)})"
            : throw new IOException("Snapshot contains an unapproved external root.");
        return group + "/" + SafePaths.Relative(target.Path);
    }

    public void CaptureSnapshot(Game game, Snapshot snapshot, IEnumerable<(Target Target, string Source)> inputs)
    {
        var sources = inputs.ToDictionary(s => s.Target.Key, s => s.Source);
        var folder = snapshot.Folder ?? SnapshotFolder(game, snapshot);
        if (!folder.StartsWith("snapshots/", StringComparison.Ordinal)) throw new IOException("Invalid snapshot folder.");
        var destination = StoragePath(folder);
        var initial = snapshot.Files.ToList();
        var mapped = initial.Select(f => f.Hash == null && !sources.ContainsKey(f.Target.Key) ? f with { Payload = null } :
            f with { Payload = folder + "/" + InsideSnapshot(game, f.Target) }).ToList();
        SafePaths.DistinctDestinations(mapped.Where(f => f.Payload != null).Select(f => StoragePath(f.Payload!)));
        if (Directory.Exists(destination) && sources.Keys.All(k => initial.Single(f => f.Target.Key == k).Hash != null))
        {
            ReuseSnapshot(snapshot, folder, mapped);
            return;
        }
        SafePaths.Space(PayloadRoot, sources.Values.Sum(p => new FileInfo(p).Length));
        var stage = StoragePath("staging/snapshot-" + Format.Id());
        Directory.CreateDirectory(stage);
        try
        {
            var captured = new List<FileState>();
            foreach (var state in mapped)
            {
                if (!sources.TryGetValue(state.Target.Key, out var source)) { captured.Add(state with { Hash = null, Size = 0, Payload = null }); continue; }
                SafePaths.NoLinks(source);
                var path = Path.Combine(stage, InsideSnapshot(game, state.Target));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                CopyFlushed(source, path);
                var hash = SafePaths.Hash(path);
                if (state.Hash != null && (hash != state.Hash || new FileInfo(path).Length != state.Size))
                    throw new IOException($"Modified source payload during snapshot conversion: {source}");
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                captured.Add(state with { Hash = hash, Size = new FileInfo(path).Length });
            }
            if (Directory.Exists(destination)) ReuseSnapshot(snapshot, folder, captured);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                SafePaths.NoLinks(destination);
                Directory.Move(stage, destination);
                snapshot.Folder = folder; snapshot.Files = captured;
            }
        }
        finally { if (Directory.Exists(stage)) DeleteOwnedTree(stage); }
    }

    private void ReuseSnapshot(Snapshot snapshot, string folder, List<FileState> states)
    {
        var actual = SafePaths.Walk(StoragePath(folder), message => throw new IOException(message), 4096).ToList();
        var expected = states.Where(f => f.Hash != null).ToList();
        if (actual.Count != expected.Count) throw new IOException("Existing snapshot folder has unexpected files; refusing to replace it.");
        foreach (var state in expected)
        {
            Verify(state);
            File.SetAttributes(Payload(state), File.GetAttributes(Payload(state)) | FileAttributes.ReadOnly);
        }
        snapshot.Folder = folder; snapshot.Files = states;
    }

    private static void CopyFlushed(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }

    private static void DeleteOwnedTree(string folder)
    {
        SafePaths.NoLinks(folder);
        foreach (var file in SafePaths.Walk(folder, message => throw new IOException(message), 4096))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(folder, true);
    }

    public void RemoveOrphanSnapshot(Snapshot snapshot)
    {
        if (snapshot.Folder == null || Data.Games.Any(g => AllSnapshots(g).Any(s => s.Id == snapshot.Id))) return;
        if (!snapshot.Folder.StartsWith("snapshots/", StringComparison.Ordinal)) throw new IOException("Invalid orphan snapshot folder.");
        var path = SnapshotPath(snapshot);
        if (Directory.Exists(path)) DeleteOwnedTree(path);
    }

    private static IEnumerable<Snapshot> AllSnapshots(Game game) => game.Snapshots.Concat(game.Installations.Select(i => i.Before));
}
