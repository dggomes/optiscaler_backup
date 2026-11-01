using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OptiScalerBackup.Desktop;

internal static class WindowChrome
{
    public static void Apply(Window window, bool fit = true)
    {
        window.SetResourceReference(Window.IconProperty, "ApplicationIcon");
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = 1;
            var result = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            if (result != 0) Trace.WriteLine($"Dark caption unavailable: HRESULT {result:x8}");
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // Let WPF process the new DPI first. Work areas also change when Xbox mode,
                // taskbar placement, display resolution or a docked monitor changes.
                if (fit && message is 0x02E0 or 0x007E or 0x001A)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Fit(window, false)));
                return IntPtr.Zero;
            });
            if (fit) Fit(window, true);
        };
        if (fit) window.Loaded += (_, _) => Fit(window, true);
    }

    internal static Rect FitBounds(Rect bounds, Rect work, bool center)
    {
        var width = Math.Min(bounds.Width, work.Width);
        var height = Math.Min(bounds.Height, work.Height);
        return new Rect(center ? work.Left + (work.Width - width) / 2 : Math.Clamp(bounds.Left, work.Left, work.Right - width),
            center ? work.Top + (work.Height - height) / 2 : Math.Clamp(bounds.Top, work.Top, work.Bottom - height), width, height);
    }

    private static void Fit(Window window, bool initial)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.WindowState == WindowState.Minimized) return;
        var monitor = MonitorFromWindow(handle, 2);
        if (initial && window.Owner is { } owner) monitor = MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var current))
        { Trace.WriteLine("Cannot read monitor work area for " + window.Title); return; }
        var work = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var scale = Math.Max(96, GetDpiForWindow(handle)) / 96d;
        window.MinWidth = Math.Min(window.MinWidth, work.Width / scale);
        window.MinHeight = Math.Min(window.MinHeight, work.Height / scale);
        if (window.WindowState != WindowState.Normal) return; // Native maximization respects the work area.
        var requested = new Rect(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top);
        if (initial && window.Owner is { } parent && GetWindowRect(new WindowInteropHelper(parent).Handle, out var parentBounds))
            requested.Location = new Point(parentBounds.Left + (parentBounds.Right - parentBounds.Left - requested.Width) / 2,
                parentBounds.Top + (parentBounds.Bottom - parentBounds.Top - requested.Height) / 2);
        var bounds = FitBounds(requested, work, initial && window.Owner == null);
        if (!SetWindowPos(handle, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0014))
            Trace.WriteLine("Cannot fit window to monitor work area: " + Marshal.GetLastWin32Error());
    }

    internal static void CheckPlacement(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("Cannot verify native window placement.");
        if (bounds.Left < info.Work.Left || bounds.Top < info.Work.Top || bounds.Right > info.Work.Right || bounds.Bottom > info.Work.Bottom)
            throw new InvalidOperationException("Window extends outside the monitor work area: " + window.Title);
    }

    // Give tables a finite viewport, even when an entire workspace must scroll on
    // a tiny screen. An unconstrained outer ScrollViewer would expand every row.
    internal static ScrollViewer Viewport(FrameworkElement content, double minWidth, double minHeight)
    {
        content.Width = minWidth; content.Height = minHeight;
        var viewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.Both
        };
        void Resize()
        {
            content.Width = Math.Max(Math.Max(minWidth, content.MinWidth), viewer.ViewportWidth);
            content.Height = Math.Max(Math.Max(minHeight, content.MinHeight), viewer.ViewportHeight);
        }
        viewer.Loaded += (_, _) => Resize();
        viewer.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) Resize(); };
        return viewer;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
