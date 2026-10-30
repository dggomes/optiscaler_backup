using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OptiScalerBackup.Core;
using OptiScalerBackup.Windows;

namespace OptiScalerBackup.Desktop;

internal static class GameArtwork
{
    public static BitmapSource? Load(Game game, string? downloaded = null)
    {
        try
        {
            if (downloaded != null && Bitmap(downloaded) is { } remote) return remote;
            var cover = CachedSteamCover(game);
            if (cover != null && Bitmap(cover) is { } local) return local;
            foreach (var relative in game.Executables.Take(5))
            {
                var file = SafePaths.Resolve(game, new("game", relative));
                if (!File.Exists(file)) continue;
                var handles = new IntPtr[1];
                try
                {
                    var count = ExtractIconEx(file, 0, handles, IntPtr.Zero, 1);
                    if (count is 0 or uint.MaxValue || handles[0] == IntPtr.Zero) continue;
                    var icon = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(96, 96));
                    icon.Freeze(); return icon;
                }
                finally { if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        { Trace.WriteLine($"Local artwork game={game.Id}: {error.Message}"); }
        return null;
    }

    private static BitmapSource? Bitmap(string file)
    {
        try
        {
            SafePaths.NoLinks(file);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 240; bitmap.UriSource = new Uri(file); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        { Trace.WriteLine("Artwork decode failed: " + error.Message); return null; }
    }

    private static string? CachedSteamCover(Game game)
    {
        var common = Directory.GetParent(game.Directory);
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true || common.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true) return null;
        var apps = common.Parent.FullName;
        SafePaths.NoLinks(apps);
        string? appId = null;
        foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
        {
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            SafePaths.NoLinks(file);
            var data = Vdf.Parse(File.ReadAllText(file));
            var directory = data.Values("installdir").FirstOrDefault();
            if (directory == null || !Path.GetFullPath(Path.Combine(common.FullName, directory)).Equals(game.Directory, StringComparison.OrdinalIgnoreCase)) continue;
            appId = data.Values("appid").FirstOrDefault(); break;
        }
        if (appId == null || !appId.All(char.IsAsciiDigit)) return null;
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        foreach (var root in new[] { steam, common.Parent.Parent?.FullName }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
        {
            var cache = Path.Combine(root!, "appcache", "librarycache");
            foreach (var name in new[] { appId + "_library_600x900.jpg", appId + "_header.jpg" })
            {
                var file = Path.Combine(cache, name);
                if (File.Exists(file)) return file;
            }
            var perGame = Path.Combine(cache, appId);
            if (Directory.Exists(perGame))
            {
                SafePaths.NoLinks(perGame);
                var found = Directory.EnumerateFiles(perGame, "*library_600x900*").FirstOrDefault();
                if (found != null) return found;
            }
        }
        return null;
    }
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[] large, IntPtr small, uint count);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
