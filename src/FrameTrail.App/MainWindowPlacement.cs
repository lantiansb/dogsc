using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

[assembly: InternalsVisibleTo("FrameTrail.App.VisualProbe")]

namespace FrameTrail.App;

/// <summary>Places only the main editor window in native monitor coordinates.</summary>
internal static class MainWindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int MonitorDpiAwarenessContextPerMonitorAwareV2 = -4;
    private const double PaddingDip = 16;

    internal readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    /// <summary>
    /// Set by the real desktop startup only, so probes and diagnostic launches never read or overwrite the user's
    /// remembered window geometry.
    /// </summary>
    internal static bool PersistenceEnabled { get; set; }

    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var initialMinWidth = window.MinWidth;
        var initialMinHeight = window.MinHeight;
        var placed = false;
        var restored = PersistenceEnabled && TryRestore(window);

        window.Loaded += (_, _) =>
        {
            if (placed) return;
            placed = true;
            if (window.WindowState == WindowState.Normal)
            {
                // A remembered position is only clamped into the work area; otherwise the first-run layout centres the window.
                if (restored) ClampCurrent(window, initialMinWidth, initialMinHeight);
                else PlaceInitial(window, initialMinWidth, initialMinHeight);
            }
        };
        if (PersistenceEnabled) TrackGeometry(window);

        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (window.IsLoaded && window.WindowState == WindowState.Normal)
                ClampCurrent(window, initialMinWidth, initialMinHeight);
        }));
    }

    // ---- E23: remember position, size and maximized state (Mac EditorWindowController frame autosave) ----

    private static bool TryRestore(Window window)
    {
        try
        {
            var preferences = MainWindow.ReadUserPreferences();
            if (preferences.WindowLeft is not { } left || preferences.WindowTop is not { } top || preferences.WindowWidth is not { } width || preferences.WindowHeight is not { } height) return false;
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)) return false;
            width = Math.Max(width, window.MinWidth);
            height = Math.Max(height, window.MinHeight);
            if (!IsOnVisibleScreen(left, top, width, height)) return false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left; window.Top = top; window.Width = width; window.Height = height;
            if (preferences.WindowMaximized) window.WindowState = WindowState.Maximized;
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    // The title strip must overlap some connected monitor enough to be grabbed; otherwise the saved spot is stale.
    private static bool IsOnVisibleScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var strip = Rect.Intersect(screen, new Rect(left, top, width, Math.Min(height, 48)));
        return !strip.IsEmpty && strip.Width >= 120 && strip.Height >= 30;
    }

    private static void TrackGeometry(MainWindow window)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => { timer.Stop(); SaveGeometry(window); };
        void Schedule(object? sender, EventArgs e)
        {
            if (!window.IsLoaded) return;
            timer.Stop(); timer.Start();
        }
        window.LocationChanged += Schedule;
        window.SizeChanged += (s, e) => Schedule(s, e);
        window.StateChanged += Schedule;
        window.Closing += (_, _) => SaveGeometry(window);
    }

    private static void SaveGeometry(MainWindow window)
    {
        if (!window.IsLoaded || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
        try
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Width) || bounds.Width < 200 || bounds.Height < 200) return;
            var preferences = MainWindow.ReadUserPreferences();
            preferences.WindowLeft = bounds.Left; preferences.WindowTop = bounds.Top;
            preferences.WindowWidth = bounds.Width; preferences.WindowHeight = bounds.Height;
            preferences.WindowMaximized = window.WindowState == WindowState.Maximized;
            MainWindow.SaveUserPreferences(preferences);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    internal static RectPx CenterAndFit(RectPx window, RectPx workArea, int padding)
    {
        var availableWidth = Math.Max(1, workArea.Width - 2 * Math.Max(0, padding));
        var availableHeight = Math.Max(1, workArea.Height - 2 * Math.Max(0, padding));
        var width = Math.Min(Math.Max(1, window.Width), availableWidth);
        var height = Math.Min(Math.Max(1, window.Height), availableHeight);
        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = workArea.Top + (workArea.Height - height) / 2;
        return new RectPx(left, top, left + width, top + height);
    }

    internal static RectPx ClampAndFit(RectPx window, RectPx workArea, int padding)
    {
        var availableWidth = Math.Max(1, workArea.Width - 2 * Math.Max(0, padding));
        var availableHeight = Math.Max(1, workArea.Height - 2 * Math.Max(0, padding));
        var width = Math.Min(Math.Max(1, window.Width), availableWidth);
        var height = Math.Min(Math.Max(1, window.Height), availableHeight);
        var minLeft = workArea.Left + Math.Max(0, padding);
        var minTop = workArea.Top + Math.Max(0, padding);
        var maxLeft = workArea.Right - Math.Max(0, padding) - width;
        var maxTop = workArea.Bottom - Math.Max(0, padding) - height;
        var left = Math.Clamp(window.Left, minLeft, Math.Max(minLeft, maxLeft));
        var top = Math.Clamp(window.Top, minTop, Math.Max(minTop, maxTop));
        return new RectPx(left, top, left + width, top + height);
    }

    private static void PlaceInitial(Window window, double initialMinWidth, double initialMinHeight)
    {
        if (!TryGetGeometry(window, out var hwnd, out var current, out var workArea, out var dpi)) return;
        var padding = DipToPixels(PaddingDip, dpi);
        var target = CenterAndFit(current, workArea, padding);
        UpdateMinimumSize(window, initialMinWidth, initialMinHeight, workArea, padding, dpi);
        Apply(hwnd, target);
    }

    private static void ClampCurrent(Window window, double initialMinWidth, double initialMinHeight)
    {
        if (!TryGetGeometry(window, out var hwnd, out var current, out var workArea, out var dpi)) return;
        var padding = DipToPixels(PaddingDip, dpi);
        var target = ClampAndFit(current, workArea, padding);
        UpdateMinimumSize(window, initialMinWidth, initialMinHeight, workArea, padding, dpi);
        if (target != current) Apply(hwnd, target);
    }

    private static bool TryGetGeometry(Window window, out IntPtr hwnd, out RectPx current, out RectPx workArea, out uint dpi)
    {
        hwnd = new WindowInteropHelper(window).Handle;
        current = default;
        workArea = default;
        dpi = 96;
        if (hwnd == IntPtr.Zero) return false;

        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(MonitorDpiAwarenessContextPerMonitorAwareV2));
        if (previousContext == IntPtr.Zero) return false;
        try
        {
            if (!GetWindowRect(hwnd, out var windowRect)) return false;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            current = FromNative(windowRect);
            workArea = FromNative(info.Work);
            return current.Width > 0 && current.Height > 0 && workArea.Width > 0 && workArea.Height > 0;
        }
        finally { _ = SetThreadDpiAwarenessContext(previousContext); }
    }

    private static void UpdateMinimumSize(Window window, double initialMinWidth, double initialMinHeight, RectPx workArea, int padding, uint dpi)
    {
        var scale = Math.Max(1, dpi) / 96d;
        var availableWidthDip = Math.Max(1, workArea.Width - 2 * padding) / scale;
        var availableHeightDip = Math.Max(1, workArea.Height - 2 * padding) / scale;
        window.MinWidth = Math.Min(initialMinWidth, availableWidthDip);
        window.MinHeight = Math.Min(initialMinHeight, availableHeightDip);
    }

    private static void Apply(IntPtr hwnd, RectPx rect)
    {
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(MonitorDpiAwarenessContextPerMonitorAwareV2));
        if (previousContext == IntPtr.Zero)
        {
            App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法进入主窗口定位所需的 DPI 上下文。"));
            return;
        }
        try
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate))
                App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法将主窗口放入显示器工作区。"));
        }
        finally { _ = SetThreadDpiAwarenessContext(previousContext); }
    }

    private static int DipToPixels(double dip, uint dpi) => (int)Math.Round(dip * Math.Max(1, dpi) / 96d);
    private static RectPx FromNative(NativeRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
