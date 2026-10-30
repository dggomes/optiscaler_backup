using OptiScalerBackup.Core;

namespace OptiScalerBackup.Cli;

internal static class GamePicker
{
    public static Game? Select(TextReader input, TextWriter output, IEnumerable<Game> games)
    {
        var all = games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var matches = all;
        while (true)
        {
            output.WriteLine("\nGames — type a few letters to search, a number to select, '*' to show all, or 0 to go back.");
            for (var i = 0; i < matches.Count; i++)
                output.WriteLine($"  {i + 1}. {matches[i].Name} {(Directory.Exists(matches[i].Directory) ? "" : "[unavailable]")} — {matches[i].Directory}");
            output.Write("> ");
            var text = input.ReadLine()?.Trim();
            if (text == null || text == "0") return null;
            if (text == "*") { matches = all; continue; }
            if (int.TryParse(text, out var choice) && choice >= 1 && choice <= matches.Count) return matches[choice - 1];
            if (string.IsNullOrWhiteSpace(text))
            {
                if (matches.Count == 1) return matches[0];
                continue;
            }
            matches = all.Where(g => g.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) output.WriteLine($"No games match '{text}'. Try another name or '*' to show all.");
        }
    }
}
