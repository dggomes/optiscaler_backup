namespace OptiScalerBackup.Core;

public sealed partial class Store
{
    public void RenameSnapshot(Game game, string id, string name)
    {
        if (File.Exists(JournalPath) || File.Exists(RenameJournalPath)) throw new IOException("Finish pending recovery before renaming a snapshot.");
        name = Format.Label(name);
        var next = Format.Clone(game);
        var copies = AllSnapshots(next).Where(s => s.Id == id).ToList();
        if (copies.Count == 0) throw new InvalidOperationException("Snapshot ID not found.");
        var snapshot = copies[0];
        var oldFolder = snapshot.Folder ?? throw new IOException("Snapshot requires conversion before renaming.");
        var newFolder = SnapshotFolder(game, snapshot, name);
        if (oldFolder.Equals(newFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var copy in copies) copy.Name = name;
            Put(next); return;
        }
        var original = StoragePath(oldFolder);
        var destination = StoragePath(newFolder);
        if (!Directory.Exists(original) || Directory.Exists(destination)) throw new IOException("Snapshot source missing or destination folder already exists.");
        foreach (var state in snapshot.Files) Verify(state);
        foreach (var copy in copies)
        {
            copy.Name = name; copy.Folder = newFolder;
            copy.Files = copy.Files.Select(f => f.Payload == null ? f : f with { Payload = RewritePrefix(f.Payload, oldFolder, newFolder) }).ToList();
        }
        Atomic(RenameJournalPath, new RenameJournal { BeforeGame = Format.Clone(game), OldFolder = oldFolder, NewFolder = newFolder });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(original, destination); Put(next); File.Delete(RenameJournalPath);
        }
        catch (Exception error)
        {
            try { RecoverRename(); }
            catch (Exception recovery) { throw new IOException($"Rename failed: {error.Message}; recovery pending: {recovery.Message}", error); }
            throw new IOException($"Rename failed and was rolled back: {error.Message}", error);
        }
    }

    private static string RewritePrefix(string path, string oldFolder, string newFolder)
    {
        if (!path.StartsWith(oldFolder + "/", StringComparison.Ordinal)) throw new IOException("Snapshot payload lies outside its folder.");
        return newFolder + path[oldFolder.Length..];
    }

    private void RecoverRename()
    {
        if (!File.Exists(RenameJournalPath)) return;
        var journal = Read<RenameJournal>(RenameJournalPath);
        if (journal.Version != 1 || !journal.OldFolder.StartsWith("snapshots/", StringComparison.Ordinal) || !journal.NewFolder.StartsWith("snapshots/", StringComparison.Ordinal))
            throw new IOException("Unsupported or invalid rename recovery journal.");
        var oldPath = StoragePath(journal.OldFolder);
        var newPath = StoragePath(journal.NewFolder);
        if (Directory.Exists(newPath))
        {
            if (Directory.Exists(oldPath)) throw new IOException("Both rename folders exist; refusing to overwrite either.");
            Directory.Move(newPath, oldPath);
        }
        if (!Directory.Exists(oldPath)) throw new IOException("Snapshot folder is unavailable for rename recovery.");
        Put(journal.BeforeGame);
        File.Delete(RenameJournalPath);
    }
}
