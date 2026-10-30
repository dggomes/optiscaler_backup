using System.Security.Cryptography;

namespace OptiScalerBackup.Core;

public static class SafePaths
{
    public static string Relative(string path, int maxLength = 240)
    {
        path = path.Replace('\\', '/');
        var pieces = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Length > maxLength ||
            pieces.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || IsDevice(p)))
            throw new InvalidOperationException($"Unsafe Windows relative path: {path}");
        return string.Join('/', pieces);
    }

    public static string FolderName(string name, int max = 40)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var safe = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "Unnamed";
        if (IsDevice(safe)) safe = "_" + safe;
        if (safe.Length > max) safe = safe[..max];
        if (safe.Length > 0 && char.IsHighSurrogate(safe[^1])) safe = safe[..^1];
        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "_";
        if (IsDevice(safe)) safe = ("_" + safe)[..Math.Min(max, safe.Length + 1)];
        return safe;
    }

    private static bool IsDevice(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9';
    }

    public static bool Within(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void DistinctDestinations(IEnumerable<string> paths)
    {
        var list = paths.Select(Path.GetFullPath).ToList();
        var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count != list.Count) throw new IOException("Duplicate destination in operation.");
        foreach (var path in list)
            for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
                if (set.Contains(parent)) throw new IOException("A destination file is also a parent directory of another destination.");
    }

    public static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Linked/junction path is not allowed: {p}");
            }
            catch (FileNotFoundException) { /* An absent component is allowed. */ }
            catch (DirectoryNotFoundException) { /* Parent availability is checked separately. */ }
        }
    }

    public static bool FileExists(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access-denied and I/O errors must never be recorded as absence.
    }

    public static string Resolve(Game game, Target target)
    {
        var root = target.Root == "game" ? game.Directory :
            game.ExternalRoots.TryGetValue(target.Root, out var approved) ? approved :
            throw new InvalidOperationException($"Unapproved file root: {target.Root}");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Approved root is unavailable or inaccessible: {root}");
        var path = Path.GetFullPath(Path.Combine(root, Relative(target.Path).Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(root, path)) throw new IOException("Destination escapes approved root.");
        NoLinks(path);
        return path;
    }

    public static IEnumerable<string> Walk(string root, Action<string>? warning = null, int maxDepth = 12)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var item))
        {
            string[] entries;
            try { NoLinks(item.Path); entries = Directory.GetFileSystemEntries(item.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { warning?.Invoke($"Cannot scan {item.Path}: {e.Message}"); continue; }
            foreach (var entry in entries)
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(entry); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { warning?.Invoke($"Cannot inspect {entry}: {e.Message}"); continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Directory) == 0) yield return entry;
                else if (item.Depth < maxDepth && !new[] { ".git", "node_modules", "__pycache__" }.Contains(Path.GetFileName(entry)))
                    stack.Push((entry, item.Depth + 1));
            }
        }
    }

    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void Space(string destination, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination))!;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < checked(bytes + 16 * 1024 * 1024))
            throw new IOException($"Insufficient free space on {root}; need {bytes} bytes plus safety margin.");
    }
}
