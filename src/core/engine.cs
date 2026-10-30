namespace OptiScalerBackup.Core;

public sealed class Engine(Store store, Action<Game>? ensureStopped = null)
{
    public Action<int>? AfterWrite { get; set; } // Fault injection for recovery tests.

    public Snapshot Capture(Game game, IEnumerable<Target> targets, string name, bool recovery = false)
    {
        RequireAvailable(game);
        var label = recovery && name.Length > 160 ? name[..160] : name;
        if (label.Length > 0 && char.IsHighSurrogate(label[^1])) label = label[..^1];
        var snapshot = new Snapshot { Name = Format.Label(label), Recovery = recovery };
        var selected = targets.DistinctBy(t => t.Key).ToList();
        SafePaths.DistinctDestinations(selected.Select(t => SafePaths.Resolve(game, t)));
        foreach (var target in selected)
        {
            var path = SafePaths.Resolve(game, target);
            if (Directory.Exists(path)) throw new IOException($"Expected file, found directory: {path}");
            var hash = SafePaths.FileExists(path) ? store.Capture(path) : null;
            snapshot.Files.Add(new(target, hash, hash == null ? 0 : new FileInfo(store.Blob(hash)).Length));
        }
        return snapshot;
    }

    public Snapshot CreateSnapshot(Game game, string name, bool original, bool confirmedClean)
    {
        Ready(game);
        if (game.Managed.Count == 0) throw new InvalidOperationException("Select managed files before creating a snapshot.");
        if (original && (!confirmedClean || game.Snapshots.Any(s => s.Original)))
            throw new InvalidOperationException("Original requires --confirm-clean and can only be established once per game.");
        var snapshot = Capture(game, game.Managed, name);
        snapshot.Original = original;
        var next = Format.Clone(game);
        next.Snapshots.Add(snapshot);
        store.Put(next);
        return snapshot;
    }

    public List<FileState> SnapshotFiles(Game game, Snapshot snapshot)
    {
        if (!game.Snapshots.Any(s => s.Id == snapshot.Id)) throw new InvalidOperationException("Snapshot does not belong to this game.");
        // Missing metadata is unknown, not evidence of historical absence.
        // Only an explicit null hash authorizes deleting a tracked file.
        return snapshot.Files.ToList();
    }

    public List<Change> Preview(Game game, IEnumerable<FileState> desired)
    {
        RequireAvailable(game);
        Validate(game, desired);
        return desired.Select(f =>
        {
            var path = SafePaths.Resolve(game, f.Target);
            var current = SafePaths.FileExists(path) ? SafePaths.Hash(path) : null;
            return new Change(current == f.Hash ? "unchanged" : f.Hash == null ? "remove" : current == null ? "add" : "replace",
                f.Target, current, f.Hash);
        }).ToList();
    }

    public void Restore(Game game, Snapshot snapshot)
    {
        var desired = SnapshotFiles(game, snapshot);
        var blockers = ActiveOverlaps(game, desired.Select(f => f.Target));
        if (blockers.Count > 0) throw new InvalidOperationException("Roll back overlapping package installations first: " + string.Join(", ", blockers));
        var next = Format.Clone(game);
        Execute(game, next, desired, "Before restoring " + snapshot.Name);
    }

    public Installation Install(Game game, Package package, bool allowOverlap)
    {
        var overlaps = ActiveOverlaps(game, package.Files.Select(f => f.Target));
        if (overlaps.Count > 0 && !allowOverlap)
            throw new InvalidOperationException("Overlapping installations: " + string.Join(", ", overlaps) + ". Review and use --allow-overlap.");
        var next = Format.Clone(game);
        var installation = new Installation { PackageId = package.Id, Installed = Format.Clone(package.Files) };
        next.Installations.Add(installation);
        Execute(game, next, package.Files, "Before installing " + package.Name, before => installation.Before = before);
        return installation;
    }

    public List<string> RollbackBlockers(Game game, Installation installation)
    {
        var index = game.Installations.FindIndex(i => i.Id == installation.Id);
        if (index < 0) throw new InvalidOperationException("Installation is not part of this game.");
        var keys = installation.Installed.Select(f => SafePaths.Resolve(game, f.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return game.Installations.Skip(index + 1).Where(i => i.RolledBack == null && i.Installed.Any(f => keys.Contains(SafePaths.Resolve(game, f.Target))))
            .Select(i => i.Id).ToList();
    }

    public List<Change> RollbackChanges(Game game, Installation installation)
    {
        installation = CurrentInstallation(game, installation.Id);
        if (installation.RolledBack != null) throw new InvalidOperationException("Installation was already rolled back.");
        var blockers = RollbackBlockers(game, installation);
        if (blockers.Count > 0) throw new InvalidOperationException("Roll back newer overlapping installations first: " + string.Join(", ", blockers));
        return Preview(game, installation.Before.Files);
    }

    public List<Target> Modified(Game game, Installation installation) => CurrentInstallation(game, installation.Id).Installed.Where(f =>
    {
        var path = SafePaths.Resolve(game, f.Target);
        return !SafePaths.FileExists(path) || SafePaths.Hash(path) != f.Hash;
    }).Select(f => f.Target).ToList();

    public void Rollback(Game game, Installation installation, bool overrideEdits)
    {
        installation = CurrentInstallation(game, installation.Id);
        RollbackChanges(game, installation);
        if (Modified(game, installation).Count > 0 && !overrideEdits)
            throw new InvalidOperationException("Package files changed after installation. Review before using --override-edits; a recovery snapshot will be saved.");
        var next = Format.Clone(game);
        next.Installations.Single(i => i.Id == installation.Id).RolledBack = DateTimeOffset.UtcNow;
        Execute(game, next, installation.Before.Files, "Before package rollback " + installation.Id);
    }

    private static Installation CurrentInstallation(Game game, string id) => game.Installations.SingleOrDefault(i => i.Id == id)
        ?? throw new InvalidOperationException("Installation is not part of this game.");

    public List<string> ActiveOverlaps(Game game, IEnumerable<Target> targets)
    {
        var keys = targets.Select(t => SafePaths.Resolve(game, t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return game.Installations.Where(i => i.RolledBack == null && i.Installed.Any(f => keys.Contains(SafePaths.Resolve(game, f.Target)))).Select(i => i.Id).ToList();
    }

    public bool Recover()
    {
        if (!File.Exists(store.JournalPath)) return false;
        var journal = Store.Read<Journal>(store.JournalPath);
        if (journal.SchemaVersion != Format.Version) throw new IOException("Unsupported recovery journal. Do not modify the game until recovery is resolved.");
        RequireAvailable(journal.BeforeGame);
        ensureStopped?.Invoke(journal.BeforeGame);
        Validate(journal.BeforeGame, journal.BeforeFiles.Files);
        if (journal.Id.Length != 32 || journal.Id.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid transaction ID.");
        foreach (var state in journal.BeforeFiles.Files)
        {
            var temp = SafePaths.Resolve(journal.BeforeGame, state.Target) + ".optibackup-" + journal.Id + ".tmp";
            SafePaths.NoLinks(temp);
            if (File.Exists(temp)) File.Delete(temp);
        }
        WriteFiles(journal.BeforeGame, journal.BeforeFiles.Files, false, journal.Id);
        store.Put(journal.BeforeGame);
        File.Delete(store.JournalPath);
        return true;
    }

    private void Execute(Game game, Game next, List<FileState> desired, string name, Action<Snapshot>? captured = null)
    {
        Ready(game);
        Validate(game, desired);
        CheckAccess(game, desired);
        foreach (var volume in desired.GroupBy(f => Path.GetPathRoot(SafePaths.Resolve(game, f.Target))))
            SafePaths.Space(volume.Key!, volume.Sum(f => f.Size));
        var before = Capture(game, desired.Select(f => f.Target), name, true);
        captured?.Invoke(before);
        next.Snapshots.Add(before);
        var journal = new Journal { BeforeGame = Format.Clone(game), BeforeFiles = before, Desired = desired };
        // Journal is durable before the first destination change; recovery always rolls back.
        Store.Atomic(store.JournalPath, journal);
        try
        {
            var drift = Preview(game, before.Files).Any(c => c.Action != "unchanged");
            if (drift) throw new IOException("Game files changed while preparing the operation. Retry with the game closed.");
            WriteFiles(game, desired, true, journal.Id);
            store.Put(next);
            File.Delete(store.JournalPath);
        }
        catch (Exception error)
        {
            try { Recover(); }
            catch (Exception recovery) { throw new IOException($"Operation failed: {error.Message}. Recovery pending: {recovery.Message}", error); }
            throw new IOException($"Operation failed and was rolled back: {error.Message}", error);
        }
    }

    private void WriteFiles(Game game, IEnumerable<FileState> desired, bool inject, string transactionId)
    {
        var count = 0;
        foreach (var state in desired)
        {
            var path = SafePaths.Resolve(game, state.Target);
            if (state.Hash == null) { if (SafePaths.FileExists(path)) File.Delete(path); }
            else
            {
                store.Verify(state.Hash);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SafePaths.NoLinks(path);
                var temp = path + ".optibackup-" + transactionId + ".tmp";
                SafePaths.NoLinks(temp);
                try
                {
                    using (var input = File.OpenRead(store.Blob(state.Hash)))
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    if (SafePaths.Hash(temp) != state.Hash) throw new IOException("Staged file integrity check failed.");
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            if (inject) AfterWrite?.Invoke(++count);
        }
    }

    private void Validate(Game game, IEnumerable<FileState> files)
    {
        SafePaths.DistinctDestinations(files.Select(f => SafePaths.Resolve(game, f.Target)));
        var keys = new HashSet<string>();
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in files)
        {
            var path = SafePaths.Resolve(game, state.Target);
            if (!keys.Add(state.Target.Key) || !physical.Add(path)) throw new IOException("Duplicate destination in operation.");
            if (SafePaths.Within(store.Home, path) || SafePaths.Within(store.PayloadRoot, path))
                throw new IOException("Game/settings destinations must not overlap application storage.");
            if (state.Hash != null)
            {
                store.Verify(state.Hash);
                if (new FileInfo(store.Blob(state.Hash)).Length != state.Size) throw new IOException("Payload length mismatch.");
            }
        }
    }

    private static void CheckAccess(Game game, IEnumerable<FileState> files)
    {
        foreach (var f in files)
        {
            var path = SafePaths.Resolve(game, f.Target);
            for (var parent = Path.GetDirectoryName(path); parent != null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new IOException($"Destination parent is a file: {parent}");
            if (Directory.Exists(path)) throw new IOException($"Destination is a directory: {path}");
            if (!SafePaths.FileExists(path)) continue;
            using var check = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private void Ready(Game game)
    {
        if (File.Exists(store.JournalPath)) throw new IOException("A pending operation must be recovered first.");
        RequireAvailable(game);
        ensureStopped?.Invoke(game);
    }
    private static void RequireAvailable(Game game)
    {
        SafePaths.NoLinks(game.Directory);
        if (!Directory.Exists(game.Directory)) throw new DirectoryNotFoundException($"Game unavailable: {game.Directory}");
    }
}
