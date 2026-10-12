using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FrameTrail.Capture;

namespace FrameTrail.App;

/// <summary>Shared colours for the full-screen picker overlays (always a dark glass look, like Mac CaptureSelection*).</summary>
internal static class PickerStyle
{
    internal static readonly Color Accent = Color.FromRgb(0x46, 0xA9, 0x86);
    internal static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);
    internal static readonly Brush Glass = Frozen(Color.FromArgb(0xEB, 0x1C, 0x1C, 0x1E));
    internal static readonly Brush GlassBorder = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush White = Frozen(Colors.White);
    internal static readonly Brush WhiteMuted = Frozen(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));

    internal static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Mac honours "reduce motion"; Windows equivalent is the client-area animation setting.</summary>
    internal static bool Animate => SystemParameters.ClientAreaAnimation;
}

/// <summary>
/// A lightweight pill button for the picker overlays. It is a plain Border (no template) so it works in
/// non-activating transparent windows and always keeps the dark glass look regardless of the app theme.
/// </summary>
internal sealed class PickerChip : Border
{
    internal enum Look { Plain, Primary }

    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, FontSize = 13, FontWeight = FontWeights.SemiBold };
    private Look look;
    private bool hover, pressed, selected, active = true, filled;

    internal event Action? Click;

    internal PickerChip(string text, Look look = Look.Plain, double minWidth = 0, double height = 36)
    {
        this.look = look;
        Height = height;
        MinWidth = minWidth;
        CornerRadius = new CornerRadius(height / 2);
        Padding = new Thickness(14, 0, 14, 0);
        Cursor = Cursors.Hand;
        Child = label;
        Background = Brushes.Transparent;
        SetText(text);
        MouseEnter += (_, _) => { hover = true; Paint(); };
        MouseLeave += (_, _) => { hover = false; pressed = false; Paint(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!active) return;
            pressed = true;
            CaptureMouse();
            e.Handled = true;
            Paint();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            ReleaseMouseCapture();
            e.Handled = true;
            Paint();
            if (active && IsMouseOver) Click?.Invoke();
        };
        Paint();
    }

    /// <summary>Switches between the grey and the red "primary" look in place (button width does not change).</summary>
    internal bool IsPrimary
    {
        get => look == Look.Primary;
        set { look = value ? Look.Primary : Look.Plain; Paint(); }
    }

    /// <summary>A plain chip with a resting background (otherwise it is only highlighted on hover).</summary>
    internal bool IsFilled
    {
        get => filled;
        set { filled = value; Paint(); }
    }

    internal bool IsSelected
    {
        get => selected;
        set { selected = value; Paint(); }
    }

    /// <summary>A disabled chip stays visible but ignores clicks (e.g. "单击窗口以锁定").</summary>
    internal bool IsActive
    {
        get => active;
        set { active = value; Cursor = value ? Cursors.Hand : Cursors.Arrow; Paint(); }
    }

    /// <summary>Shows a Segoe Fluent / MDL2 icon glyph instead of text (e.g. the aspect-lock chain).</summary>
    internal void SetGlyph(string glyph)
    {
        label.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        label.FontSize = 15;
        label.Inlines.Clear();
        label.Inlines.Add(new System.Windows.Documents.Run(glyph));
    }

    internal void SetText(string main, string? muted = null)
    {
        label.Inlines.Clear();
        label.Inlines.Add(new System.Windows.Documents.Run(main));
        if (muted is not null)
            label.Inlines.Add(new System.Windows.Documents.Run("  " + muted) { FontSize = 11, FontWeight = FontWeights.Medium, Foreground = PickerStyle.WhiteMuted });
    }

    private void Paint()
    {
        if (look == Look.Primary)
        {
            if (!active)
            {
                Background = PickerStyle.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                label.Foreground = PickerStyle.WhiteMuted;
            }
            else
            {
                var red = PickerStyle.Red;
                var color = pressed ? Color.FromRgb(0xC2, 0x3A, 0x3F) : hover ? Color.FromRgb(0xEE, 0x5C, 0x61) : red;
                Background = PickerStyle.Frozen(color);
                label.Foreground = PickerStyle.White;
            }
            return;
        }
        byte alpha = selected ? (byte)0x3C : pressed ? (byte)0x38 : hover && active ? (byte)0x34 : filled ? (byte)0x26 : (byte)0;
        Background = alpha == 0 ? Brushes.Transparent : PickerStyle.Frozen(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        label.Foreground = active ? PickerStyle.White : PickerStyle.WhiteMuted;
    }
}

/// <summary>
/// Picker overlays are not activated by the mouse, so Esc / Ctrl+R cannot rely on keyboard focus.
/// This registers them as global hotkeys on a message-only window for as long as a picker is open.
/// </summary>
internal sealed class PickerKeyboard : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int IdEscape = 0x4D31;
    private const int IdConfirm = 0x4D32;

    private readonly HwndSource source;
    private readonly Action cancel;
    private readonly Action? confirm;
    private readonly List<int> registered = [];

    internal PickerKeyboard(Action cancel, Action? confirm)
    {
        this.cancel = cancel;
        this.confirm = confirm;
        // HWND_MESSAGE parent: invisible window used only to receive WM_HOTKEY.
        var parameters = new HwndSourceParameters("DogScPickerKeys") { Width = 0, Height = 0, ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        source = new HwndSource(parameters);
        source.AddHook(Hook);
        Register(IdEscape, ModNoRepeat, 0x1B);
        if (confirm is not null) Register(IdConfirm, ModControl | ModNoRepeat, 0x52);
    }

    private void Register(int id, uint modifiers, uint key)
    {
        // A failed registration (another app owns the combination) only loses the shortcut; buttons still work.
        if (NativeWindows.RegisterHotKey(source.Handle, id, modifiers, key)) registered.Add(id);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotKey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (id == IdEscape) { handled = true; cancel(); }
        else if (id == IdConfirm && confirm is not null) { handled = true; confirm(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in registered) NativeWindows.UnregisterHotKey(source.Handle, id);
        registered.Clear();
        source.RemoveHook(Hook);
        source.Dispose();
    }
}

/// <summary>
/// Borderless, topmost, capture-excluded overlay covering exactly one display in physical pixels.
/// Mixed-DPI safe: the rectangle is applied with SetWindowPos (not Left/Top) and re-verified after load.
/// </summary>
internal abstract class PickerOverlayWindow : Window
{
    private readonly bool noActivate;
    private int placementAttempts;

    internal PixelRect ScreenRect { get; private set; }

    /// <summary>Device pixels per DIP of the monitor this overlay is on.</summary>
    internal double Scale { get; private set; } = 1;

    protected PickerOverlayWindow(PixelRect screenRect, bool noActivate)
    {
        ScreenRect = screenRect;
        this.noActivate = noActivate;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // Alpha 1 keeps the whole surface hit-testable (fully transparent layered pixels are click-through).
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !noActivate;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screenRect.X;
        Top = screenRect.Y;
        Width = screenRect.Width;
        Height = screenRect.Height;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            PickerNative.MakeToolWindow(handle);
            if (this.noActivate) PickerNative.AddExStyle(handle, PickerNative.WsExNoActivate);
            NativeWindows.Exclude(this);
            Place();
        };
        Loaded += (_, _) => { EnsurePlacement(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(EnsurePlacement));
    }

    protected virtual void OnLayoutScaleChanged() { }

    /// <summary>Moves the overlay to another display rectangle (display hot-plug).</summary>
    internal void MoveTo(PixelRect rect)
    {
        ScreenRect = rect;
        placementAttempts = 0;
        Place();
        EnsurePlacement();
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        PickerNative.PlaceTopmost(handle, ScreenRect.X, ScreenRect.Y, ScreenRect.Width, ScreenRect.Height);
    }

    /// <summary>
    /// Crossing into a monitor with another DPI makes WPF rescale the window (WM_DPICHANGED); apply the
    /// exact physical rectangle again until it sticks.
    /// </summary>
    private void EnsurePlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!PickerNative.WindowRectEquals(handle, ScreenRect.X, ScreenRect.Y, ScreenRect.Width, ScreenRect.Height) && placementAttempts++ < 4)
        {
            Place();
            Dispatcher.BeginInvoke(new Action(EnsurePlacement), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        if (Math.Abs(scale - Scale) > .0001 || !IsScaleReported)
        {
            Scale = scale;
            IsScaleReported = true;
            OnLayoutScaleChanged();
        }
    }

    private bool IsScaleReported { get; set; }

    /// <summary>Physical screen point to local DIPs.</summary>
    internal Point ToLocal(int screenX, int screenY) => new((screenX - ScreenRect.X) / Scale, (screenY - ScreenRect.Y) / Scale);

    internal Rect ToLocal(PixelRect rect) => new((rect.X - ScreenRect.X) / Scale, (rect.Y - ScreenRect.Y) / Scale, rect.Width / Scale, rect.Height / Scale);

    internal static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
}
