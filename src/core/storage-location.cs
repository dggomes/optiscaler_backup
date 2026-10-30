namespace OptiScalerBackup.Core;

public sealed partial class Store
{
    public void RelocatePayloads(string value)
    {
        if (File.Exists(JournalPath) || File.Exists(RenameJournalPath)) throw new IOException("Finish recovery before relocating backups.");
        var destination = Path.GetFullPath(value);
        SafePaths.NoLinks(destination);
        if (destination.Equals(PayloadRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (SafePaths.Within(PayloadRoot, destination) || SafePaths.Within(destination, PayloadRoot) ||
            Data.Games.SelectMany(g => g.ExternalRoots.Values.Append(g.Directory)).Any(root =>
                destination.Equals(root, StringComparison.OrdinalIgnoreCase) || SafePaths.Within(root, destination) || SafePaths.Within(destination, root)))
            throw new IOException("Choose backup storage separate from game/settings folders and the current backup folder.");
        var copies = new Dictionary<string, (string Source, string Hash, long Size)>(StringComparer.OrdinalIgnoreCase);
        var blobs = Path.Combine(PayloadRoot, "blobs");
        foreach (var file in SafePaths.Walk(blobs, message => throw new IOException(message), 4096))
        {
            var hash = Path.GetFileName(file); Verify(hash);
            copies["blobs/" + hash[..2] + "/" + hash] = (file, hash, new FileInfo(file).Length);
        }
        var snapshots = Data.Games.SelectMany(AllSnapshots).DistinctBy(s => s.Id).ToList();
        var referenced = Data.Games.SelectMany(g => g.Packages.SelectMany(p => p.Files)).Concat(snapshots.SelectMany(s => s.Files));
        foreach (var state in referenced.Where(f => f.Hash != null && f.Payload == null))
        {
            Verify(state);
            copies["blobs/" + state.Hash![..2] + "/" + state.Hash] = (Payload(state), state.Hash, state.Size);
        }
        foreach (var state in snapshots.SelectMany(s => s.Files).Where(f => f.Hash != null && f.Payload != null))
        {
            Verify(state);
            if (copies.TryGetValue(state.Payload!, out var previous) && previous.Hash != state.Hash) throw new IOException("Conflicting snapshot payload metadata.");
            copies[state.Payload!] = (Payload(state), state.Hash!, state.Size);
        }
        SafePaths.Space(destination, copies.Values.Sum(c => c.Size));
        foreach (var entry in copies)
        {
            var target = Path.Combine(destination, SafePaths.Relative(entry.Key, 4096));
            SafePaths.NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) CopyFlushed(entry.Value.Source, target);
            if (SafePaths.Hash(target) != entry.Value.Hash) throw new IOException($"Relocated payload failed verification: {target}; original storage retained.");
            File.SetAttributes(target, File.GetAttributes(entry.Value.Source));
        }
        foreach (var snapshot in snapshots.Where(s => s.Folder != null))
        {
            var folder = Path.Combine(destination, SafePaths.Relative(snapshot.Folder!, 4096));
            SafePaths.NoLinks(folder); Directory.CreateDirectory(folder);
        }
        var old = Data.Settings.BackupDirectory;
        Data.Settings.BackupDirectory = destination;
        try { Save(); }
        catch { Data.Settings.BackupDirectory = old; throw; }
    }
}
