using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed record DiscoveredGame(string Name, string Directory, string Source, List<string>? Executables = null);

public sealed class Discovery(Action<string> warning)
{
    public List<DiscoveredGame> Scan(IEnumerable<string> customFolders)
    {
        var found = new List<DiscoveredGame>();
        if (OperatingSystem.IsWindows())
        {
            Attempt("Steam", () => { if (OperatingSystem.IsWindows()) found.AddRange(Steam(SteamRoot())); });
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            Attempt("Epic", () => found.AddRange(Epic(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"))));
            Attempt("Registry launchers", () => { if (OperatingSystem.IsWindows()) found.AddRange(RegistryGames()); });
            Attempt("Xbox", () => found.AddRange(Xbox(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName))));
            Attempt("Amazon", () =>
            {
                if (OperatingSystem.IsWindows()) found.AddRange(AmazonSqlite.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite")));
            });
        }
        else warning("Launcher registry discovery requires Windows; custom-folder scans remain available.");
        foreach (var folder in customFolders) Attempt(folder, () => found.AddRange(Custom(folder)));
        return found;
    }

    public List<DiscoveredGame> Prepare(IEnumerable<DiscoveredGame> found, CancellationToken cancellation = default)
    {
        var result = new List<DiscoveredGame>();
        var executables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in found)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Directory));
            if (!Directory.Exists(path)) { result.Add(new(Format.Label(item.Name), path, item.Source)); continue; }
            if (!executables.TryGetValue(path, out var files))
                executables[path] = files = Directory.Exists(path) ? Detection.Executables(path, warning) : [];
            result.Add(new(Format.Label(item.Name), path, item.Source, files));
        }
        return result;
    }

    public void Merge(Store store, IEnumerable<DiscoveredGame> found)
    {
        foreach (var item in found)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Directory));
            var game = store.Data.Games.FirstOrDefault(g => g.Directory.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (game?.Excluded == true) continue;
            if (game == null)
            {
                game = new Game { Name = Format.Label(item.Name), Directory = path };
                store.Data.Games.Add(game);
            }
            if (!game.Sources.Contains(item.Source)) game.Sources.Add(item.Source);
            if (item.Executables != null) game.Executables = item.Executables.ToList();
            else if (Directory.Exists(path)) game.Executables = Detection.Executables(path, warning);
        }
        store.Save();
    }

    public List<DiscoveredGame> Steam(string root)
    {
        var result = new List<DiscoveredGame>();
        if (!Directory.Exists(root)) return result;
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            var vdf = Vdf.Parse(File.ReadAllText(libraryFile));
            foreach (var path in vdf.Values("path")) libraries.Add(path);
            // Older Steam versions use numeric keys with direct library paths.
            foreach (var node in vdf.Children.SelectMany(c => c.Children))
                if (int.TryParse(node.Key, out _) && node.Value != null) libraries.Add(node.Value);
        }
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                Attempt(manifest, () =>
                {
                    var data = Vdf.Parse(File.ReadAllText(manifest));
                    var dir = data.Values("installdir").FirstOrDefault();
                    var name = data.Values("name").FirstOrDefault();
                    if (dir != null && name != null)
                        result.Add(new(name, Path.Combine(apps, "common", SafePaths.Relative(dir)), "Steam"));
                });
        }
        return result;
    }

    public List<DiscoveredGame> Epic(string manifests)
    {
        var result = new List<DiscoveredGame>();
        if (!Directory.Exists(manifests)) return result;
        foreach (var path in Directory.EnumerateFiles(manifests, "*.item"))
            Attempt(path, () =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return;
                var dir = root.GetProperty("InstallLocation").GetString();
                var name = root.GetProperty("DisplayName").GetString();
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name)) result.Add(new(name, dir, "Epic"));
            });
        return result;
    }

    public List<DiscoveredGame> Xbox(IEnumerable<string> driveRoots)
    {
        var result = new List<DiscoveredGame>();
        foreach (var root in driveRoots)
        {
            var library = Path.Combine(root, "XboxGames");
            if (!Directory.Exists(library)) continue;
            foreach (var folder in Directory.EnumerateDirectories(library))
                Attempt(folder, () =>
                {
                    var content = Path.Combine(folder, "Content");
                    var config = Path.Combine(content, "MicrosoftGame.config");
                    var name = Path.GetFileName(folder);
                    if (File.Exists(config))
                    {
                        var xml = XDocument.Load(config);
                        var title = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value;
                        if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("ms-resource:")) name = title;
                    }
                    result.Add(new(name, Directory.Exists(content) ? content : folder, "Xbox"));
                });
        }
        return result;
    }

    public List<DiscoveredGame> Custom(string folder)
    {
        folder = Path.GetFullPath(folder);
        SafePaths.NoLinks(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var executables = Detection.Executables(folder, warning);
        if (executables.Count == 0) return [];
        if (executables.Any(p => !p.Contains('/')) || Directory.Exists(Path.Combine(folder, "Binaries")) ||
            Directory.Exists(Path.Combine(folder, "Engine")) || Directory.Exists(Path.Combine(folder, "bin")))
            return [new(Path.GetFileName(folder), folder, "Custom")];
        return executables.Select(p => p.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(child => new DiscoveredGame(child, Path.Combine(folder, child), "Custom")).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static string SteamRoot() => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

    [SupportedOSPlatform("windows")]
    private List<DiscoveredGame> RegistryGames()
    {
        var result = new List<DiscoveredGame>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (var source in new[] { "GOG", "Ubisoft", "EA", "Uninstall" })
                {
                    var path = source switch
                    {
                        "GOG" => @"SOFTWARE\GOG.com\Games",
                        "Ubisoft" => @"SOFTWARE\Ubisoft\Launcher\Installs",
                        "EA" => @"SOFTWARE\EA Games",
                        _ => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
                    };
                    using var parent = root.OpenSubKey(path);
                    if (parent == null) continue;
                    foreach (var keyName in parent.GetSubKeyNames())
                        Attempt($"{source}/{keyName}", () =>
                        {
                            using var key = parent.OpenSubKey(keyName);
                            if (key == null) return;
                            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var value in new[] { "path", "InstallDir", "InstallLocation", "Install Dir", "gameName", "DisplayName", "Publisher" })
                                fields[value] = key.GetValue(value) as string;
                            if (RegistryEntry(source, keyName, fields) is { } entry) result.Add(entry);
                        });
                }
                using var gaming = root.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
                if (gaming == null) continue;
                foreach (var sub in gaming.GetSubKeyNames())
                    Attempt("Xbox/" + sub, () =>
                    {
                        using var key = gaming.OpenSubKey(sub);
                        var dir = key?.GetValue("Root") as string;
                        if (!string.IsNullOrEmpty(dir)) result.Add(new(Path.GetFileName(dir.TrimEnd('\\')), dir, "Xbox"));
                    });
            }
        return result;
    }

    public static DiscoveredGame? RegistryEntry(string source, string keyName, IReadOnlyDictionary<string, string?> fields)
    {
        var dir = new[] { "path", "InstallDir", "InstallLocation", "Install Dir" }
            .Select(fields.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var name = new[] { "gameName", "DisplayName" }
            .Select(fields.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? keyName;
        var launcher = source;
        if (source == "Uninstall")
        {
            var publisher = fields.GetValueOrDefault("Publisher") ?? "";
            launcher = publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) || keyName.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)
                ? "Battle.net" : publisher.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ? "Ubisoft" :
                publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? "EA" : "";
            if (name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || name.Contains("Ubisoft Connect", StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (string.IsNullOrWhiteSpace(dir) || launcher == "") return null;
        if (name == keyName && source == "Ubisoft") name = dir.TrimEnd('\\', '/').Replace('\\', '/').Split('/')[^1];
        return new(name, dir, launcher);
    }

    public static List<DiscoveredGame> AmazonDatabase(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Amazon SQLite adapter requires Windows.");
        return AmazonSqlite.Read(path);
    }

    private void Attempt(string source, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or System.Xml.XmlException or System.Security.SecurityException or DllNotFoundException)
        { warning($"Discovery {source}: {e.Message}"); }
    }
}

public sealed class Vdf(string key = "", string? value = null)
{
    public string Key { get; } = key;
    public string? Value { get; } = value;
    public List<Vdf> Children { get; } = [];
    public IEnumerable<string> Values(string key) => Children.SelectMany(c =>
        (c.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && c.Value != null ? new[] { c.Value } : []).Concat(c.Values(key)));

    public static Vdf Parse(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]|//[^\\r\\n]*")
            .Cast<Match>().Where(m => !m.Value.StartsWith("//")).Select(m => m.Value).ToList();
        var index = 0;
        string Decode(string token) => token[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
        Vdf Read(string key)
        {
            var node = new Vdf(key);
            while (index < tokens.Count && tokens[index] != "}")
            {
                var name = tokens[index++];
                if (!name.StartsWith('"') || index >= tokens.Count) throw new IOException("Invalid VDF key.");
                var value = tokens[index++];
                if (value == "{") node.Children.Add(Read(Decode(name)));
                else if (value.StartsWith('"')) node.Children.Add(new(Decode(name), Decode(value)));
                else throw new IOException("Invalid VDF value.");
            }
            if (key != "" && (index >= tokens.Count || tokens[index++] != "}")) throw new IOException("Unclosed VDF object.");
            return node;
        }
        return Read("");
    }
}
