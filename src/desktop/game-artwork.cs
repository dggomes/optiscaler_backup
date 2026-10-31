using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OptiScalerBackup.Core;

namespace OptiScalerBackup.Desktop;

internal static class GameArtwork
{
    public static BitmapSource? Load(Game game, string? downloaded = null) => downloaded == null ? null : Bitmap(downloaded);

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

}
