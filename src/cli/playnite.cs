using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Cli;

internal sealed partial class Application
{
    private void PlayniteCommands(Arguments args)
    {
        var action = args.At(1);
        args.Count(action == "compare" ? 2 : action == "configure" ? 3 : 4, action == "import" ? 4 : 3);
        var path = Path.GetFullPath(args.Positionals.Count >= 3 ? args.At(2) : store.Data.Settings.PlayniteLibraryPath ?? Playnite.DefaultExport);
        var filters = Format.Clone(store.Data.Settings);
        static List<string> Values(string text) => text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (args.Get("exclude-platform") is { } platforms) filters.PlayniteExcludePlatforms = Values(platforms);
        if (args.Get("exclude-tag") is { } tags) filters.PlayniteExcludeTags = Values(tags);
        if (args.Get("exclude-category") is { } categories) filters.PlayniteExcludeCategories = Values(categories);
        var library = Playnite.Read(path); var comparison = Playnite.Compare(library, store, filters);
        if (action == "compare") { Output(new { library.ExportedUtc, library.Warnings, Comparison = comparison }, args); return; }
        if (action == "configure")
        {
            Confirm("Save Playnite export location and filters", args); filters.PlayniteLibraryPath = path;
            var previous = store.Data.Settings; store.Data.Settings = filters;
            try { store.Save(); } catch { store.Data.Settings = previous; throw; }
            Output(comparison, args); return;
        }
        if (action != "import") throw new ArgumentException("Use playnite compare, configure or import.");
        var ids = args.At(3) == "all" ? comparison.Rows.Where(r => r.Eligible).Select(r => r.Game.Id).ToList() : [args.At(3)];
        if (args.Has("dry-run")) { Output(comparison.Rows.Where(r => ids.Contains(r.Game.Id)).ToList(), args); return; }
        Confirm("Import selected installed Playnite games and covers (game files stay unchanged)", args);
        using var http = new HttpClient();
        var warnings = Playnite.Import(store, library, path, filters, ids, new Thumbnails(http, store.Home));
        Output(new { Warnings = warnings, Comparison = Playnite.Compare(library, store, filters) }, args);
    }
}
