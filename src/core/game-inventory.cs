using System.Text.Json;

namespace OptiScalerBackup.Core;

public sealed record InventoryFile(string Path, string Hash, long Size, DateTime LastWriteUtc);
public sealed class GameInventory
{
    public int Version { get; set; } = 1;
    public List<InventoryFile> Files { get; set; } = [];
    public List<string> Folders { get; set; } = [];
    public List<string> ExcludedLinks { get; set; } = [];

    public static GameInventory Scan(Game game)
    {
        var result = ReadTree(game, true);
        var check = ReadTree(game, false);
        if (!result.Files.OrderBy(f => f.Path).Select(f => (f.Path, f.Size, f.LastWriteUtc)).SequenceEqual(check.Files.OrderBy(f => f.Path).Select(f => (f.Path, f.Size, f.LastWriteUtc)))
            || !result.Folders.Order().SequenceEqual(check.Folders.Order()) || !result.ExcludedLinks.Order().SequenceEqual(check.ExcludedLinks.Order()))
            throw new IOException("Game folder changed during inventory; retry with the game closed.");
        return result;
    }
    private static GameInventory ReadTree(Game game, bool hashFiles)
    {
        SafePaths.NoLinks(game.Directory);
        if (!Directory.Exists(game.Directory)) throw new IOException("Game folder is unavailable.");
        var inventory = new GameInventory();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((game.Directory, 0));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var directory))
        {
            if (directory.Depth > 256) throw new IOException("Game inventory exceeds the directory-depth limit.");
            SafePaths.NoLinks(directory.Path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                var relative = Path.GetRelativePath(game.Directory, entry).Replace('\\', '/');
                SafePaths.Relative(relative, 4096);
                if (!paths.Add(relative)) throw new IOException("Case-insensitive collision in game inventory: " + relative);
                if (paths.Count > 1_000_000) throw new IOException("Game inventory exceeds one million entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { inventory.ExcludedLinks.Add(relative); continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                { inventory.Folders.Add(relative); pending.Push((entry, directory.Depth + 1)); continue; }
                SafePaths.NoLinks(entry);
                var before = new FileInfo(entry); var size = before.Length; var modified = before.LastWriteTimeUtc;
                var hash = hashFiles ? SafePaths.Hash(entry) : ""; before.Refresh();
                if (before.Length != size || before.LastWriteTimeUtc != modified) throw new IOException("File changed during inventory: " + entry);
                inventory.Files.Add(new(relative, hash, size, modified));
            }
        }
        return inventory;
    }

    public bool ProvesAbsent(string path) => !Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        && !Folders.Any(f => f.Equals(path, StringComparison.OrdinalIgnoreCase))
        && !ExcludedLinks.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
}

public sealed class InventoryIndex
{
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Snapshots { get; set; } = [];
}
public sealed record SnapshotPlan(GameInventory Inventory, List<Target> Targets, string GameState);

public sealed partial class Store
{
    private string InventoryIndexPath => Path.Combine(Home, "game-inventories.json");
    private InventoryIndex ReadInventoryIndex()
    {
        var index = File.Exists(InventoryIndexPath) ? Read<InventoryIndex>(InventoryIndexPath) : new();
        if (index.Version != 1) throw new IOException("Unsupported game inventory index; no data changed.");
        return index;
    }
    public GameInventory? Inventory(Snapshot snapshot)
    {
        var index = ReadInventoryIndex();
        if (!index.Snapshots.TryGetValue(snapshot.Id, out var hash)) return null;
        Verify(hash);
        if (new FileInfo(Blob(hash)).Length > 512L * 1024 * 1024) throw new IOException("Game inventory exceeds the metadata-size limit.");
        var inventory = Read<GameInventory>(Blob(hash));
        if (inventory.Version != 1) throw new IOException("Unsupported game inventory format; no data changed.");
        var paths = inventory.Files.Select(f => f.Path).Concat(inventory.Folders).Concat(inventory.ExcludedLinks).ToList();
        foreach (var path in paths) SafePaths.Relative(path, 4096);
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count || inventory.Files.Any(f => f.Size < 0 || f.Hash.Length != 64 || f.Hash.Any(c => !char.IsAsciiHexDigit(c))))
            throw new IOException("Invalid game inventory metadata.");
        return inventory;
    }
    public void SaveInventory(Snapshot snapshot, GameInventory inventory)
    {
        var index = ReadInventoryIndex();
        if (index.Snapshots.ContainsKey(snapshot.Id)) throw new IOException("Snapshot inventory is immutable.");
        var temporary = Path.Combine(Home, "inventory-" + Format.Id() + ".tmp");
        try
        {
            Atomic(temporary, inventory);
            index.Snapshots.Add(snapshot.Id, Capture(temporary));
            Atomic(InventoryIndexPath, index);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
