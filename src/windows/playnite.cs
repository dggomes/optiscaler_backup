using System.Text.Json;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed class PlayniteLibrary
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedUtc { get; set; }
    public List<PlayniteGame> Games { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
public sealed class PlayniteGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InstallDirectory { get; set; }
    public bool Installed { get; set; }
    public string? Cover { get; set; }
    public List<string> Platforms { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Categories { get; set; } = [];
}
public sealed record PlayniteRow(PlayniteGame Game, string Status, string? CatalogId, bool Eligible);
public sealed record PlayniteComparison(int PlayniteTotal, int FilteredOut, int Included, int Installed, int Matched, int MissingInstalled, int Uninstalled, int ExcludedGames, int ScannedTotal, int ScannedFilteredOut, int ScannedOnly, List<PlayniteRow> Rows);

public static class Playnite
{
    public static string DefaultExport => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerBackup", "playnite", "library.json");
    public static PlayniteLibrary Read(string path)
    {
        SafePaths.NoLinks(path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Playnite export exceeds 32 MiB.");
        using var input = File.OpenRead(path);
        if (input.Length > 32 * 1024 * 1024) throw new IOException("Playnite export exceeds 32 MiB.");
        var length = checked((int)input.Length); var bytes = new byte[length]; input.ReadExactly(bytes);
        if (input.Length != length) throw new IOException("Playnite export changed while reading; retry.");
        var library = JsonSerializer.Deserialize<PlayniteLibrary>(bytes, Format.Json) ?? throw new IOException("Invalid Playnite export.");
        if (library.Version != 1) throw new IOException("Unsupported Playnite export version; export was not changed.");
        if (library.Games == null || library.Games.Count > 50000 || library.Games.Any(g => g == null || !Guid.TryParse(g.Id, out _) || string.IsNullOrWhiteSpace(g.Name)
            || g.Name.Length > 160 || g.Name.Any(char.IsControl) || g.Platforms == null || g.Tags == null || g.Categories == null
            || g.Platforms.Concat(g.Tags).Concat(g.Categories).Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200))) throw new IOException("Invalid or oversized Playnite game metadata.");
        if (library.Games.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != library.Games.Count) throw new IOException("Duplicate Playnite IDs in export.");
        return library;
    }
    private static string? DirectoryKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    public static PlayniteComparison Compare(PlayniteLibrary library, Store store, Settings filters)
    {
        var rows = new List<PlayniteRow>(); var matched = new HashSet<string>(); var filteredScanned = new HashSet<string>();
        var byPath = store.Data.Games.Where(g => DirectoryKey(g.Directory) != null).GroupBy(g => DirectoryKey(g.Directory)!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in library.Games)
        {
            var filtered = entry.Platforms.Intersect(filters.PlayniteExcludePlatforms, StringComparer.OrdinalIgnoreCase).Any()
                || entry.Tags.Intersect(filters.PlayniteExcludeTags, StringComparer.OrdinalIgnoreCase).Any()
                || entry.Categories.Intersect(filters.PlayniteExcludeCategories, StringComparer.OrdinalIgnoreCase).Any();
            var path = DirectoryKey(entry.InstallDirectory);
            var game = path == null ? null : byPath.GetValueOrDefault(path);
            var status = filtered ? "Filtered out" : game?.Excluded == true ? "Game excluded" : !entry.Installed ? "Not installed"
                : path == null ? "No usable install path" : game != null ? "Matched" : "Missing from scan";
            if (filtered && game != null && !game.Excluded) filteredScanned.Add(game.Id);
            if (!filtered && game != null && !game.Excluded) matched.Add(game.Id);
            rows.Add(new(entry, status, game?.Id, !filtered && game?.Excluded != true && entry.Installed && path != null));
        }
        filteredScanned.ExceptWith(matched);
        var included = rows.Where(r => r.Status != "Filtered out").ToList();
        return new(library.Games.Count, rows.Count - included.Count, included.Count, included.Count(r => r.Game.Installed), rows.Count(r => r.Status == "Matched"),
            rows.Count(r => r.Status is "Missing from scan" or "No usable install path"), rows.Count(r => r.Status == "Not installed"), rows.Count(r => r.Status == "Game excluded"),
            store.VisibleGames.Count(), filteredScanned.Count, store.VisibleGames.Count(g => !matched.Contains(g.Id) && !filteredScanned.Contains(g.Id)), rows);
    }
    public static List<string> Import(Store store, PlayniteLibrary library, string exportPath, Settings filters, IEnumerable<string> ids, Thumbnails thumbnails)
    {
        var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase); var warnings = new List<string>();
        var rows = Compare(library, store, filters).Rows;
        if (selected.Any(id => !rows.Any(r => r.Game.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))) throw new IOException("Unknown Playnite game ID.");
        var imported = new List<(PlayniteGame Entry, Game Game)>();
        var staged = store.Data.Games.ToDictionary(g => DirectoryKey(g.Directory) ?? g.Directory, g => Format.Clone(g), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => selected.Contains(r.Game.Id)))
        {
            if (!row.Eligible) { warnings.Add(row.Game.Name + ": " + row.Status); continue; }
            var path = DirectoryKey(row.Game.InstallDirectory)!;
            if (!Directory.Exists(path)) { warnings.Add(row.Game.Name + ": install folder unavailable."); continue; }
            if (new[] { store.Home, store.PayloadRoot }.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase) || SafePaths.Within(root, path) || SafePaths.Within(path, root)))
                throw new IOException("Playnite installation overlaps app/backup storage.");
            SafePaths.NoLinks(path);
            if (!staged.TryGetValue(path, out var game)) staged[path] = game = new Game { Name = Format.Label(row.Game.Name), Directory = path };
            if (!game.Sources.Contains("Playnite")) game.Sources.Add("Playnite");
            if (!game.PlayniteIds.Contains(row.Game.Id, StringComparer.OrdinalIgnoreCase)) game.PlayniteIds.Add(row.Game.Id);
            imported.Add((row.Game, game));
        }
        store.PutMany(imported.Select(item => item.Game).DistinctBy(game => game.Id));
        foreach (var item in imported)
        {
            var game = item.Game;
            if (item.Entry.Cover is not { Length: > 0 } cover) continue;
            try
            {
                var relative = SafePaths.Relative(cover); var folder = Path.GetDirectoryName(Path.GetFullPath(exportPath))!;
                var full = Path.GetFullPath(Path.Combine(folder, relative));
                if (!SafePaths.Within(folder, full)) throw new IOException("Playnite cover escaped export folder.");
                thumbnails.ImportPlayniteCover(game, full);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            { warnings.Add(game.Name + " cover: " + error.Message); }
        }
        return warnings;
    }
}
