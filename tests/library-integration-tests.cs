using System.Net;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

internal static class LibraryIntegrationTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Library integration assertion failed."); }
    public static void Exclusions()
    {
        using var f = new Fixture(); f.Write("OptiScaler.ini", "original"); f.Game.Managed.Add(new("game", "OptiScaler.ini")); f.Store.Put(f.Game);
        var snapshot = f.Engine.CreateSnapshot(f.Current, "Keep history", false, false);
        f.Store.SetExcluded(f.Game.Id, true);
        Check(!f.Store.VisibleGames.Any() && f.Current.Excluded && f.Current.Snapshots.Single().Id == snapshot.Id);
        var discovery = new Discovery(_ => { }); discovery.Merge(f.Store, [new("Changed title", f.Game.Directory + Path.DirectorySeparatorChar, "Steam", ["Changed.exe"])]);
        Check(f.Store.Data.Games.Count == 1 && f.Current.Excluded && f.Current.Name == f.Game.Name);
        Check(Store.Read<Catalog>(Path.Combine(f.Home, "catalog.json")).Games.Single().Excluded);
        f.Store.SetExcluded(f.Game.Id, false); Check(f.Store.VisibleGames.Single().Id == f.Game.Id);
        Check(File.ReadAllText(f.Path("OptiScaler.ini")) == "original" && f.Current.Snapshots.Single().Id == snapshot.Id);
    }
    public static void PlayniteImport()
    {
        using var f = new Fixture(); var exported = Path.Combine(f.Root, "playnite"); Directory.CreateDirectory(Path.Combine(exported, "covers"));
        var cover = Path.Combine(exported, "covers", "game.jpg"); File.WriteAllBytes(cover, [0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9]);
        var entry = new PlayniteGame { Id = Guid.NewGuid().ToString(), Name = f.Game.Name, InstallDirectory = f.Game.Directory, Installed = true, Cover = "covers/game.jpg", Platforms = ["PC"], Tags = ["Keep"], Categories = ["Library"] };
        var library = new PlayniteLibrary { ExportedUtc = DateTimeOffset.UtcNow, Games = [entry, new() { Id = Guid.NewGuid().ToString(), Name = "Not installed" }, new() { Id = Guid.NewGuid().ToString(), Name = "Console", Platforms = ["Switch"] }] };
        var path = Path.Combine(exported, "library.json"); Store.Atomic(path, library); library = Playnite.Read(path);
        var filters = new Settings { PlayniteExcludePlatforms = ["switch"] }; var compare = Playnite.Compare(library, f.Store, filters);
        Check(compare.PlayniteTotal == 3 && compare.FilteredOut == 1 && compare.Matched == 1 && compare.Uninstalled == 1 && compare.ScannedOnly == 0);
        filters.PlayniteExcludeTags = ["keep"]; Check(Playnite.Compare(library, f.Store, filters).FilteredOut == 2); filters.PlayniteExcludeTags.Clear();
        filters.PlayniteExcludeCategories = ["library"]; Check(Playnite.Compare(library, f.Store, filters).FilteredOut == 2); filters.PlayniteExcludeCategories.Clear();
        using var offline = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("No network during Playnite import")));
        var thumbnails = new Thumbnails(offline, f.Home, TimeSpan.Zero);
        Check(Playnite.Import(f.Store, library, path, filters, [entry.Id], thumbnails).Count == 0);
        Check(f.Store.Data.Games.Count == 1 && f.Current.PlayniteIds.Contains(entry.Id) && f.Current.Sources.Contains("Playnite") && thumbnails.Cached(f.Current) != null);
        Check(thumbnails.Download(f.Current, true).GetAwaiter().GetResult() != null);
        f.Store.SetExcluded(f.Game.Id, true);
        Check(Playnite.Compare(library, f.Store, filters).ExcludedGames == 1);
        Check(Playnite.Import(f.Store, library, path, filters, [entry.Id], thumbnails).Count == 1 && f.Current.Excluded);
        f.Store.SetExcluded(f.Game.Id, false);
        library.Games[0].Cover = "../outside.jpg";
        Check(Playnite.Import(f.Store, library, path, filters, [entry.Id], thumbnails).Any(w => w.Contains("cover")));
        Check(thumbnails.Cached(f.Current) != null);
        library.Version = 99; Store.Atomic(path, library);
        try { Playnite.Read(path); throw new Exception("Future export accepted."); } catch (IOException) { }
        Check(Store.Read<PlayniteLibrary>(path).Version == 99);
    }
}
