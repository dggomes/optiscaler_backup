using System.Text.Json;
using OptiScalerBackup.Core;

internal static class BackupComparisonTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    public static void InventoryDifferences()
    {
        using var f = new Fixture();
        f.Write("OptiScaler.ini", "before"); f.Write("baseline.bin", "original inventory only"); f.Write("removed.bin", "removed"); f.Write("unchanged.bin", "same");
        f.Game.Managed.Add(new("game", "OptiScaler.ini")); f.Store.Put(f.Game);
        var first = f.Engine.CreateSnapshot(f.Current, "Original", true, true, f.Engine.PrepareSnapshot(f.Current));
        f.Write("OptiScaler.ini", "after"); f.Write("baseline.bin", "changed"); f.Write("added.bin", "new"); File.Delete(f.Path("removed.bin"));
        var second = f.Engine.CreateSnapshot(f.Current, "Updated", false, false, f.Engine.PrepareSnapshot(f.Current));
        var manifest = JsonSerializer.Serialize(new[] { first, second }, Format.Json);
        var comparison = BackupComparer.Compare(f.Store, f.Current, first.Id, second.Id);
        Check(comparison.Added == 1 && comparison.Removed == 1 && comparison.Changed == 2 && comparison.Unchanged == 1 && comparison.Unknown == 0, "Inventory comparison counts are incorrect.");
        Check(comparison.Files.Single(file => file.Target.Path == "baseline.bin").BeforeCoverage == "Inventory only", "Uncopied original bytes were reported saved.");
        Check(JsonSerializer.Serialize(new[] { first, second }, Format.Json) == manifest, "Comparison mutated snapshots.");
        var reverse = BackupComparer.Compare(f.Store, f.Current, second.Id, first.Id);
        Check(reverse.Added == comparison.Removed && reverse.Removed == comparison.Added && reverse.Changed == comparison.Changed, "Comparison direction is wrong.");
        var offline = Format.Clone(f.Current); offline.Directory = Path.Combine(f.Root, "disconnected-drive");
        Check(BackupComparer.Compare(f.Store, offline, first.Id, second.Id).Changed == 2, "Comparison read current game files.");
        var index = Store.Read<InventoryIndex>(Path.Combine(f.Home, "game-inventories.json"));
        File.AppendAllText(f.Store.Blob(index.Snapshots[first.Id]), "corruption");
        try { BackupComparer.Compare(f.Store, f.Current, first.Id, second.Id); throw new Exception("Corrupt inventory accepted."); } catch (IOException) { }
    }
    public static void PartialCoverage()
    {
        using var f = new Fixture(); var a = new string('a', 64); var b = new string('b', 64);
        var first = new Snapshot { Name = "Legacy", Files = [new(new("game", "legacy.dll"), a, 1), new(new("profile", "settings.ini"), a, 1), new(new("game", "explicit.dll"), null, 0)] };
        var second = new Snapshot { Name = "Partial", Files = [new(new("game", "new.dll"), b, 1), new(new("profile", "settings.ini"), b, 1), new(new("game", "explicit.dll"), b, 1)] };
        f.Game.Snapshots.AddRange([first, second]); f.Store.Put(f.Game);
        var report = BackupComparer.Compare(f.Store, f.Current, first.Id, second.Id);
        Check(report.Unknown == 2 && report.Added == 1 && report.Changed == 1 && report.Removed == 0, "Missing legacy metadata was interpreted as absence.");
        var linked = new Snapshot { Name = "Link excluded" }; var newer = new Snapshot { Name = "New inventory" };
        f.Game.Snapshots.AddRange([linked, newer]); f.Store.Put(f.Game);
        f.Store.SaveInventory(linked, new GameInventory { ExcludedLinks = ["mods"] });
        f.Store.SaveInventory(newer, new GameInventory { Files = [new("mods/new.dll", a, 1, DateTime.UtcNow)] });
        var links = BackupComparer.Compare(f.Store, f.Current, linked.Id, newer.Id);
        Check(links.Unknown == 1 && links.Added == 0 && links.Files.Single().BeforeCoverage == "Excluded link subtree", "Excluded links were treated as absent files.");
    }
}
