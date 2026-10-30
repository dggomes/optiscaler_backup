using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OptiScalerBackup.Desktop;

internal static class WindowChrome
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            var result = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
            if (result != 0) Trace.WriteLine($"Dark caption unavailable: HRESULT {result:x8}");
        };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
