namespace OptiScalerBackup.Core;

public sealed record ComparedFile(Target Target, string Change, string? BeforeHash, string? AfterHash, long? BeforeSize, long? AfterSize, string BeforeCoverage, string AfterCoverage);
public sealed record BackupComparison(string BeforeId, string BeforeName, string AfterId, string AfterName, bool BeforeInventory, bool AfterInventory, List<ComparedFile> Files)
{
    public int Added => Files.Count(f => f.Change == "Added");
    public int Removed => Files.Count(f => f.Change == "Removed");
    public int Changed => Files.Count(f => f.Change == "Changed");
    public int Unchanged => Files.Count(f => f.Change == "Unchanged");
    public int Unknown => Files.Count(f => f.Change == "Unknown coverage");
}

public static class BackupComparer
{
    private sealed record State(string? Hash, long? Size, string Coverage, bool Known);
    private sealed class View
    {
        public Dictionary<string, (Target Target, State State)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> links = new(StringComparer.OrdinalIgnoreCase);
        public bool HasInventory { get; }
        public View(Store store, Snapshot snapshot)
        {
            var inventory = store.Inventory(snapshot); HasInventory = inventory != null;
            if (inventory != null)
            {
                foreach (var file in inventory.Files)
                {
                    var target = new Target("game", file.Path);
                    Files.Add(target.Key, (target, new(file.Hash, file.Size, "Inventory only", true)));
                }
                folders.UnionWith(inventory.Folders); links.UnionWith(inventory.ExcludedLinks);
            }
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Files)
            {
                SafePaths.Relative(file.Target.Path, 4096);
                if (!tracked.Add(file.Target.Key) || file.Size < 0 || file.Hash != null && (file.Hash.Length != 64 || file.Hash.Any(c => !char.IsAsciiHexDigit(c))))
                    throw new IOException("Invalid or duplicate tracked comparison metadata.");
                Files[file.Target.Key] = (file.Target, new(file.Hash, file.Size, file.Hash == null ? "Recorded absent" : "Saved bytes", true));
            }
        }
        public State At(Target target)
        {
            if (Files.TryGetValue(target.Key, out var file)) return file.State;
            if (!HasInventory || !target.Root.Equals("game", StringComparison.OrdinalIgnoreCase)) return new(null, null, "Not recorded", false);
            var path = target.Path.Replace('\\', '/');
            if (folders.Contains(path)) return new(null, null, "Directory recorded", false);
            for (var prefix = path; prefix.Length > 0;)
            {
                if (links.Contains(prefix)) return new(null, null, "Excluded link subtree", false);
                var slash = prefix.LastIndexOf('/'); if (slash < 0) break; prefix = prefix[..slash];
            }
            return new(null, 0, "Inventory absence", true);
        }
    }
    public static BackupComparison Compare(Store store, Game game, string beforeId, string afterId)
    {
        var before = game.Snapshots.SingleOrDefault(s => s.Id == beforeId) ?? throw new InvalidOperationException("First backup does not belong to this game.");
        var after = game.Snapshots.SingleOrDefault(s => s.Id == afterId) ?? throw new InvalidOperationException("Second backup does not belong to this game.");
        var left = new View(store, before); var right = beforeId == afterId ? left : new View(store, after);
        var targets = left.Files.Values.Select(f => f.Target).Concat(right.Files.Values.Select(f => f.Target)).DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var files = new List<ComparedFile>();
        foreach (var target in targets)
        {
            var a = left.At(target); var b = right.At(target);
            var change = !a.Known || !b.Known ? "Unknown coverage" : a.Hash == null && b.Hash != null ? "Added" : a.Hash != null && b.Hash == null ? "Removed"
                : string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase) && a.Size == b.Size ? "Unchanged" : "Changed";
            files.Add(new(target, change, a.Hash, b.Hash, a.Size, b.Size, a.Coverage, b.Coverage));
        }
        return new(before.Id, before.Name, after.Id, after.Name, left.HasInventory, right.HasInventory,
            files.OrderBy(f => f.Target.Root, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Target.Path, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
