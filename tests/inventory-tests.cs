using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class InventoryTests
{
    public static void RoundTrip()
    {
        using var f = new Fixture();
        f.Write("OptiScaler.ini", "clean"); f.Write("data/large.dat", "unchanged game data"); f.Write("old.txt", "original bytes not selected");
        f.Game.Managed.Add(new("game", "OptiScaler.ini")); f.Store.Put(f.Game);
        f.Engine.CreateSnapshot(f.Current, "Earlier backup", false, false);
        var original = Save(f, "Original", true);
        Check(original.Files.Count == 1, "Inventory copied unselected original files.");
        Check(f.Store.Inventory(original)!.Files.Count == 3, "Original inventory omitted files.");
        f.Write("custom/plugin.asi", "new plugin"); f.Write("old.txt", "modified");
        var version = Save(f, "Modded");
        Check(version.Files.Any(s => s.Target.Path == "custom/plugin.asi") && version.Files.Any(s => s.Target.Path == "old.txt"), "Changed or new files were missed.");
        f.Write("unrelated.txt", "keep");
        f.Engine.Restore(f.Current, original);
        Check(!File.Exists(f.Path("custom/plugin.asi")), "Original inventory did not remove a later tracked addition.");
        Check(f.Read("old.txt") == "modified", "Inventory invented unavailable original bytes.");
        Check(f.Read("unrelated.txt") == "keep", "Original restore removed unrelated files.");
        f.Engine.Restore(f.Current, version);
        Check(f.Read("custom/plugin.asi") == "new plugin", "New file could not be restored.");
        File.Delete(f.Path("old.txt")); var deleted = Save(f, "Deleted");
        Check(deleted.Files.Single(s => s.Target.Path == "old.txt").Hash == null, "Deletion was not tracked.");
        f.Engine.Restore(f.Current, version); f.Engine.Restore(f.Current, deleted);
        Check(!File.Exists(f.Path("old.txt")), "Deleted state did not restore exactly.");
        f.Engine.RenameSnapshot(f.Current, original.Id, "Clean Original");
        f.Store.RelocatePayloads(System.IO.Path.Combine(f.Root, "relocated"));
        Check(f.Store.Inventory(f.Current.Snapshots.Single(s => s.Id == original.Id))!.Files.Count == 3, "Rename or relocation lost inventory.");
    }
    public static void Safety()
    {
        using var f = new Fixture(); f.Write("OptiScaler.ini", "one");
        f.Game.Managed.Add(new("game", "OptiScaler.ini")); f.Store.Put(f.Game);
        var plan = f.Engine.PrepareSnapshot(f.Current); f.Write("OptiScaler.ini", "two");
        Reject(() => f.Engine.CreateSnapshot(f.Current, "Stale", false, false, plan));
        Check(f.Current.Snapshots.Count == 0, "Stale plan committed.");
        var original = Save(f, "Original", true);
        var indexPath = System.IO.Path.Combine(f.Home, "game-inventories.json");
        var index = Store.Read<InventoryIndex>(indexPath); var blob = f.Store.Blob(index.Snapshots[original.Id]);
        File.WriteAllText(blob, "corrupt");
        Reject(() => f.Engine.PrepareSnapshot(f.Current));
        index.Version = 99; Store.Atomic(indexPath, index);
        var future = File.ReadAllText(indexPath); Reject(() => f.Store.Inventory(original));
        Check(File.ReadAllText(indexPath) == future, "Future inventory format was rewritten.");
    }
    public static void PreparedDiscovery()
    {
        using var f = new Fixture(); f.Write("bin/game.exe", "exe");
        var discovery = new Discovery(_ => { });
        var results = discovery.Prepare([new("Fixture", f.Game.Directory, "Steam")]);
        f.Write("bin/later.exe", "created after scan");
        f.Game.Managed.Add(new("game", "settings.ini")); f.Store.Put(f.Game);
        f.Engine.CreateSnapshot(f.Current, "During scan", false, false);
        discovery.Merge(f.Store, results);
        Check(f.Current.Executables.SequenceEqual(new[] { "bin/game.exe" }), "Discovery merge rescanned files on the UI thread.");
        Check(f.Current.Snapshots.Count == 1, "Discovery merge overwrote concurrent backup metadata.");
        var offline = discovery.Prepare([new("Offline", System.IO.Path.Combine(f.Root, "disconnected"), "Custom")]);
        discovery.Merge(f.Store, offline);
        Check(f.Store.Data.Games.Count == 2, "Disconnected discovery entry was discarded.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { discovery.Prepare(results, cancellation.Token); throw new Exception("Cancelled scan continued."); }
        catch (OperationCanceledException) { }
    }
    public static void LinksAndCoverage()
    {
        using var f = new Fixture(); f.Write("OptiScaler.asi", "asi"); f.Write("sl.new.dll", "graphics"); f.Write("OptiScaler/plugin.yaml", "config");
        f.Write("ReShade64.dll", "chain"); f.Write("notes.log", "log");
        var detected = Detection.Files(f.Game).Select(d => d.Path).ToList();
        Check(new[] { "OptiScaler.asi", "sl.new.dll", "OptiScaler/plugin.yaml", "ReShade64.dll" }.All(detected.Contains), "Known upstream backup targets omitted.");
        Check(!detected.Contains("notes.log"), "Log selected as an original.");
        if (!OperatingSystem.IsWindows())
        {
            var outside = System.IO.Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(System.IO.Path.Combine(outside, "secret"), "outside");
            Directory.CreateSymbolicLink(f.Path("linked"), outside);
            var inventory = GameInventory.Scan(f.Game);
            Check(inventory.ExcludedLinks.Contains("linked") && !inventory.ProvesAbsent("linked/secret"), "Excluded link incorrectly proved absence.");
            Check(!inventory.Files.Any(i => i.Path.StartsWith("linked/")), "Inventory followed a link.");
        }
    }
    private static Snapshot Save(Fixture f, string name, bool original = false) => f.Engine.CreateSnapshot(f.Current, name, original, original, f.Engine.PrepareSnapshot(f.Current));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Unsafe inventory operation was accepted.");
    }
}
