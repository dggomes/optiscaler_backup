using System.ComponentModel;
using System.Diagnostics;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Windows;

public sealed record DetectedFile(string Path, string Role, string Evidence, bool ReviewRequired);

public static class Detection
{
    private static readonly HashSet<string> Libraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler.dll", "OptiScaler.asi", "OptiScaler.ini", "ReShade64.dll", "ReShade.ini", "ReShadePreset.ini", "amdxcffx64.dll", "nvngx.dll", "nvngx_dlss.dll", "nvngx_dlssg.dll", "nvngx_dlssd.dll",
        "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll", "fakenvapi.dll", "fakenvapi.ini", "nvapi64.dll",
        "dlssg_to_fsr3_amd_is_better.dll", "dlssg_to_fsr3.dll", "dlssg_to_fsr3.ini", "dlss-enabler-headless.dll",
        "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll", "amd_fidelityfx_loader_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_framegeneration_dx12_dll.dll",
        "sl.common.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll"
    };
    private static readonly HashSet<string> Proxies = new(StringComparer.OrdinalIgnoreCase)
        { "dxgi.dll", "d3d11.dll", "d3d12.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dinput8.dll" };
    private static readonly HashSet<string> Configs = new(StringComparer.OrdinalIgnoreCase)
        { "GameUserSettings.ini", "Engine.ini", "Scalability.ini", "DefaultGameUserSettings.ini", "DefaultEngine.ini", "DefaultScalability.ini", "user.cfg", "graphics.xml", "settings.json", "settings.ini", "config.ini" };

    public static List<string> Executables(string root, Action<string>? warning = null) => SafePaths.Walk(root, warning)
        .Where(p => Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(c =>
            new[] { "Engine", "_CommonRedist", "redist", "Installer", "EasyAntiCheat", "BattlEye" }.Contains(c, StringComparer.OrdinalIgnoreCase)))
        .Where(p => !new[] { "unins", "setup", "crash", "reporter" }.Any(s => Path.GetFileName(p).Contains(s, StringComparison.OrdinalIgnoreCase)))
        .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<DetectedFile> Files(Game game, Action<string>? warning = null)
    {
        var found = new List<DetectedFile>();
        foreach (var path in SafePaths.Walk(game.Directory, warning))
        {
            var name = Path.GetFileName(path);
            var relative = Path.GetRelativePath(game.Directory, path).Replace('\\', '/');
            if (Libraries.Contains(name)) found.Add(new(relative, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "graphics-library" : "mod-config", "Known filename", false));
            else if (Proxies.Contains(name))
            {
                string evidence;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    evidence = $"{info.ProductName} {info.FileDescription} {info.FileVersion}".Trim();
                }
                catch (Exception e) when (e is IOException or Win32Exception) { evidence = "Metadata unavailable: " + e.Message; }
                var opti = evidence.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase);
                found.Add(new(relative, "proxy-dll", evidence.Length == 0 ? "Ambiguous proxy filename" : evidence, !opti));
            }
            else if (Path.GetExtension(name).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                || (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && new[] { "sl.", "nvngx", "libxess", "amd_fidelityfx" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                found.Add(new(relative, "mod-companion", "ASI plugin or graphics-library family; review before capture", true));
            else if (Configs.Contains(name)) found.Add(new(relative, "game-config", "Review game settings file", true));
            else if (relative.Split('/').Any(c => c.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) || c.Equals("D3D12_Optiscaler", StringComparison.OrdinalIgnoreCase))
                && new[] { ".dll", ".asi", ".ini", ".json", ".toml", ".yaml", ".yml", ".xml" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                found.Add(new(relative, "mod-companion", "Inside OptiScaler folder", true));
        }
        return found;
    }

    public static List<string> ExternalCandidates(Game game, Action<string>? warning = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var names = game.Executables.Select(p => Path.GetFileNameWithoutExtension(p).Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase).Replace("-WinGDK-Shipping", "", StringComparison.OrdinalIgnoreCase))
            .Append(game.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        var roots = names.SelectMany(n => new[] { Path.Combine(local, n, "Saved", "Config"), Path.Combine(roaming, n), Path.Combine(docs, "My Games", n), Path.Combine(docs, n) });
        return roots.Where(Directory.Exists).SelectMany(r => SafePaths.Walk(r, warning, 4)).Where(p => Configs.Contains(Path.GetFileName(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void EnsureStopped(Game game)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (game.Executables.Any(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException($"Cannot inspect possible game process {process.Id}; close it before proceeding.", e);
                    continue;
                }
                if (executable != null && SafePaths.Within(game.Directory, executable)) throw new IOException($"Close game process {name} ({process.Id}) before changing files.");
            }
    }
}
