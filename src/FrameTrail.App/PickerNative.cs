using System.Runtime.InteropServices;
using System.Text;

namespace FrameTrail.App;

/// <summary>
/// Win32 helpers shared by the display / window / region picker overlays.
/// All coordinates are physical screen pixels: the app is Per-Monitor-V2 DPI aware,
/// so GetCursorPos, WindowFromPoint and DWMWA_EXTENDED_FRAME_BOUNDS agree with each other.
/// </summary>
internal static class PickerNative
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExAppWindow = 0x00040000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint GaRoot = 2;
    internal const uint GwHwndNext = 2;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;
    internal static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] internal static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettingsW(string? deviceName, int modeNumber, ref DevMode mode);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    /// <summary>Adds WS_EX_TOOLWINDOW so a picker overlay never shows up in Alt+Tab.</summary>
    internal static void MakeToolWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        SetWindowLongPtr(window, GwlExStyle, new IntPtr((style | WsExToolWindow) & ~WsExAppWindow));
    }

    /// <summary>Moves a window to an exact physical-pixel rectangle without activating it.</summary>
    internal static void PlaceTopmost(IntPtr window, int x, int y, int width, int height, bool show = false) =>
        SetWindowPos(window, HwndTopmost, x, y, width, height, SwpNoActivate | (show ? SwpShowWindow : 0));

    /// <summary>Adds extended window style bits (e.g. WS_EX_NOACTIVATE).</summary>
    internal static void AddExStyle(IntPtr window, long bits)
    {
        if (window == IntPtr.Zero) return;
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        SetWindowLongPtr(window, GwlExStyle, new IntPtr(style | bits));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    /// <summary>Work area (physical pixels) and DPI scale of the monitor containing a physical point.</summary>
    internal static bool TryGetMonitorWorkArea(NativePoint point, out NativeRect work, out double scale)
    {
        work = default;
        scale = 1;
        var monitor = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return false;
        work = info.Work;
        try { if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96d; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        return true;
    }

    internal static bool WindowRectEquals(IntPtr window, int x, int y, int width, int height) =>
        GetWindowRect(window, out var r) && r.Left == x && r.Top == y && r.Right - r.Left == width && r.Bottom - r.Top == height;

    internal static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The visible frame (without the invisible resize border) in physical pixels.</summary>
    internal static bool TryGetFrameBounds(IntPtr window, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0) return true;
        return GetWindowRect(window, out rect);
    }

    internal static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    internal static string GetClassName(IntPtr window)
    {
        var text = new StringBuilder(256);
        return GetClassNameW(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    internal static uint GetProcessId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    /// <summary>Current refresh rate of a display device (e.g. \\.\DISPLAY1); 0 when unknown.</summary>
    internal static int GetRefreshRate(string deviceName)
    {
        try
        {
            var mode = new DevMode { dmDeviceName = string.Empty, dmFormName = string.Empty };
            mode.dmSize = (ushort)Marshal.SizeOf<DevMode>();
            // ENUM_CURRENT_SETTINGS = -1
            if (!EnumDisplaySettingsW(deviceName, -1, ref mode)) return 0;
            return mode.dmDisplayFrequency <= 1 ? 0 : (int)mode.dmDisplayFrequency;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether a top-level window may be offered as a recording target. Mirrors the Mac window
    /// picker: visible, not minimised, not cloaked (other virtual desktop / suspended UWP),
    /// not a tool window, titled, not shell chrome and not one of our own windows.
    /// </summary>
    internal static bool IsCapturableWindow(IntPtr window, uint ownProcessId, IntPtr allowedOwnWindow)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)) return false;
        if (GetProcessId(window) == ownProcessId && window != allowedOwnWindow) return false;
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        var appWindow = (style & WsExAppWindow) != 0;
        if (!appWindow && (style & (WsExToolWindow | WsExNoActivate)) != 0) return false;
        if (GetWindowTextLengthW(window) <= 0) return false;
        switch (GetClassName(window))
        {
            case "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd": return false;
        }
        return TryGetFrameBounds(window, out var rect) && rect.Right - rect.Left >= 8 && rect.Bottom - rect.Top >= 8;
    }

    /// <summary>
    /// The frontmost capturable top-level window under a physical-pixel point. Starts from
    /// WindowFromPoint + GA_ROOT and, when that is one of our overlays or an unsuitable window,
    /// walks down the z-order so the window "behind" the overlay is found instead.
    /// </summary>
    internal static IntPtr FindCapturableWindowAt(NativePoint point, uint ownProcessId, IntPtr allowedOwnWindow)
    {
        var candidate = GetAncestor(WindowFromPoint(point), GaRoot);
        if (candidate == IntPtr.Zero) candidate = GetTopWindow(IntPtr.Zero);
        for (var guard = 0; candidate != IntPtr.Zero && guard < 4096; guard++, candidate = GetWindow(candidate, GwHwndNext))
        {
            if (!IsCapturableWindow(candidate, ownProcessId, allowedOwnWindow)) continue;
            if (TryGetFrameBounds(candidate, out var rect) &&
                point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom)
                return candidate;
        }
        return IntPtr.Zero;
    }

    /// <summary>Application name for the selection card: file description, falling back to the process name.</summary>
    internal static string GetApplicationName(IntPtr window)
    {
        try
        {
            var pid = GetProcessId(window);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }
}
