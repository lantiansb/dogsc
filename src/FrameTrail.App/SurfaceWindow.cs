using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace FrameTrail.App;

/// <summary>Shared, non-layered window shell for short-lived FrameTrail surfaces.</summary>
internal static class SurfaceWindow
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static void Configure(Window window, Window? owner = null, bool closeOnEscape = true, bool resizable = false, bool centerOnOwner = true, double captionHeight = 38)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (owner is not null)
        {
            window.Owner = owner;
            if (centerOnOwner) window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.WindowStyle = captionHeight > 0 ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        window.AllowsTransparency = false;
        window.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        window.SetResourceReference(Control.FontFamilyProperty, "InterfaceFont");
        var windowStyle = new Style(typeof(Window), Application.Current?.TryFindResource(typeof(Window)) as Style);
        var contrast = HighContrastTrigger();
        contrast.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.WindowBrushKey)));
        contrast.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.WindowTextBrushKey)));
        windowStyle.Triggers.Add(contrast);
        window.Style = windowStyle;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        AttachNativeEdges(window);
        FitToWorkArea(window, limitResize: true);
        // Dialogs, settings and export windows opened while recording must not appear in the video.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ExcludeIfRecording(window);
        else window.SourceInitialized += (_, _) => ExcludeIfRecording(window);
        if (closeOnEscape)
            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || e.IsRepeat) return;
                window.Close();
                e.Handled = true;
            };
    }

    /// <summary>Set by MainWindow: true while a recording is starting, running or paused.</summary>
    public static Func<bool>? ShouldExcludeFromCapture { get; set; }

    private static void ExcludeIfRecording(Window window)
    {
        try { if (ShouldExcludeFromCapture?.Invoke() == true) NativeWindows.Exclude(window); }
        catch (Exception error) { App.Log(error); }
    }

    public static UIElement WrapContent(Window window, UIElement content, string title, bool showCloseButton = true, bool showTitleBar = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);

        var shell = new Border { SnapsToDevicePixels = true };
        var shellStyle = new Style(typeof(Border));
        shellStyle.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Chrome")));
        shellStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Hairline")));
        shellStyle.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        shellStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
        var highContrast = HighContrastTrigger();
        highContrast.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(SystemColors.WindowBrushKey)));
        highContrast.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(SystemColors.WindowTextBrushKey)));
        highContrast.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        highContrast.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(0)));
        shellStyle.Triggers.Add(highContrast);
        shell.Style = shellStyle;
        var grid = new Grid();
        if (showTitleBar)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new Grid { Margin = new Thickness(12, 0, 8, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(caption, 0);
            header.Children.Add(caption);
            if (showCloseButton)
            {
                var close = new Button
                {
                    Content = "×",
                    Width = 30,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "关闭",
                    Style = window.TryFindResource("PlainButton") as Style
                };
                System.Windows.Automation.AutomationProperties.SetName(close, "关闭");
                WindowChrome.SetIsHitTestVisibleInChrome(close, true);
                close.Click += (_, _) =>
                {
                    window.Close();
                };
                Grid.SetColumn(close, 1);
                header.Children.Add(close);
            }
            Grid.SetRow(header, 0);
            grid.Children.Add(header);
            Grid.SetRow(content, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        grid.Children.Add(content);
        shell.Child = grid;
        return shell;
    }

    private static DataTrigger HighContrastTrigger()
    {
        var preferences = Application.Current?.TryFindResource("TransientPreferences");
        return new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(TransientPreferences.HighContrast)) { Source = preferences },
            Value = true
        };
    }

    public static void AttachNativeEdges(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyDwmCorners(window);
        else window.SourceInitialized += (_, _) => ApplyDwmCorners(window);
    }

    public static void FitToWorkArea(Window window, bool limitResize = false)
    {
        void Fit()
        {
            var area = WorkArea(window);
            var width = Math.Max(1, area.Width - 32);
            var height = Math.Max(1, area.Height - 32);
            window.MinWidth = Math.Min(window.MinWidth, width);
            window.MinHeight = Math.Min(window.MinHeight, height);
            if (limitResize) { window.MaxWidth = width; window.MaxHeight = height; }
            if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width, width);
            if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height, height);
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Fit();
        else window.SourceInitialized += (_, _) => Fit();
        if (limitResize) window.DpiChanged += (_, _) => Fit();
    }

    internal static Rect WorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window.IsLoaded ? window : window.Owner ?? window).Handle;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var dpi = hwnd == IntPtr.Zero ? 96u : GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        return new Rect(info.Work.Left / scale, info.Work.Top / scale,
            (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static void ApplyDwmCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (PlatformNotSupportedException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
