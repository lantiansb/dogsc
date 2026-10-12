using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FrameTrail.Capture;
using ShapePath = System.Windows.Shapes.Path;

namespace FrameTrail.App;

internal sealed record PickedWindow(IntPtr Handle, PixelRect Bounds, string Title);

/// <summary>
/// Mac CaptureWindowSelection (v0.1.4): the whole desktop is dimmed with a hole over the window under the pointer;
/// click locks it, the capsule then offers "● 开始录制 Ctrl+R" (or click outside to unlock), Esc cancels.
/// The frontmost capturable top-level window is found with WindowFromPoint + GA_ROOT, skipping our own overlays.
/// </summary>
internal sealed class WindowPickerSession
{
    private readonly Func<Task<IReadOnlyList<DisplayInfo>>> load;
    private readonly IntPtr allowedOwnWindow;
    private readonly uint ownProcessId = (uint)Environment.ProcessId;
    private readonly Dictionary<string, WindowPickerWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<PickedWindow?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<IntPtr, string> applicationNames = [];
    private IReadOnlyList<DisplayInfo> displays = [];
    private PickerKeyboard? keys;
    private IntPtr hover, locked;
    private PixelRect? shownRect;
    private bool finished;

    private WindowPickerSession(Func<Task<IReadOnlyList<DisplayInfo>>> load, IntPtr allowedOwnWindow)
    {
        this.load = load;
        this.allowedOwnWindow = allowedOwnWindow;
    }

    /// <summary>Returns the window to record, or null when cancelled.</summary>
    internal static Task<PickedWindow?> PickAsync(Func<Task<IReadOnlyList<DisplayInfo>>> load, IntPtr allowedOwnWindow) =>
        new WindowPickerSession(load, allowedOwnWindow).RunAsync();

    private async Task<PickedWindow?> RunAsync()
    {
        displays = await load();
        if (displays.Count == 0) throw new InvalidOperationException("没有可录制的显示器。");
        try
        {
            SyncWindows();
            keys = new PickerKeyboard(() => Finish(null), StartLocked);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            timer.Tick += (_, _) => Tick();
            timer.Start();
            Tick();
            return await done.Task;
        }
        finally
        {
            timer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            keys?.Dispose();
            foreach (var window in windows.Values) window.Close();
            windows.Clear();
        }
    }

    private void Finish(PickedWindow? result)
    {
        if (finished) return;
        finished = true;
        done.TrySetResult(result);
    }

    private void SyncWindows()
    {
        foreach (var id in windows.Keys.Where(id => !displays.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            windows[id].Close();
            windows.Remove(id);
        }
        foreach (var display in displays)
        {
            if (windows.TryGetValue(display.Id, out var existing))
            {
                if (existing.ScreenRect != display.Bounds) existing.MoveTo(display.Bounds);
                continue;
            }
            var window = new WindowPickerWindow(display.Bounds);
            window.CanvasClicked += OnCanvasClick;
            window.StartRequested += StartLocked;
            window.CancelRequested += () => Finish(null);
            windows[display.Id] = window;
            window.Show();
        }
        shownRect = null;
        hover = IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (finished) return;
            try
            {
                var latest = await load();
                if (finished || latest.Count == 0) return;
                displays = latest;
                SyncWindows();
            }
            catch (Exception error) { App.Log(error); }
        }));

    private static PixelRect ToPixelRect(PickerNative.NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private bool IsOverControls(PickerNative.NativePoint point) =>
        windows.Values.Any(window => window.ControlsContain(point.X, point.Y));

    private void Tick()
    {
        if (finished) return;
        if (locked != IntPtr.Zero)
        {
            // Follow the locked window; if it vanished or was minimised, drop the lock (Mac unlockMissingSelection).
            if (!PickerNative.IsWindow(locked) || PickerNative.IsIconic(locked) || !PickerNative.TryGetFrameBounds(locked, out var lockedBounds))
            {
                locked = IntPtr.Zero;
                hover = IntPtr.Zero;
                shownRect = null;
                Refresh(animate: false);
                return;
            }
            var rect = ToPixelRect(lockedBounds);
            if (rect != shownRect) { shownRect = rect; Refresh(animate: false); }
            return;
        }
        PickerNative.GetCursorPos(out var point);
        if (IsOverControls(point)) return;
        var found = PickerNative.FindCapturableWindowAt(point, ownProcessId, allowedOwnWindow);
        PixelRect? foundRect = null;
        if (found != IntPtr.Zero && PickerNative.TryGetFrameBounds(found, out var bounds)) foundRect = ToPixelRect(bounds);
        if (found == hover && foundRect == shownRect) return;
        hover = found;
        shownRect = foundRect;
        Refresh(animate: true);
    }

    private void OnCanvasClick()
    {
        if (finished) return;
        PickerNative.GetCursorPos(out var point);
        if (locked != IntPtr.Zero)
        {
            // Clicking inside the locked window keeps it; clicking elsewhere unlocks (Mac handleSelectionClick).
            if (PickerNative.TryGetFrameBounds(locked, out var frame) &&
                point.X >= frame.Left && point.X < frame.Right && point.Y >= frame.Top && point.Y < frame.Bottom) return;
            locked = IntPtr.Zero;
            hover = IntPtr.Zero;
            shownRect = null;
            Tick();
            return;
        }
        var target = PickerNative.FindCapturableWindowAt(point, ownProcessId, allowedOwnWindow);
        if (target == IntPtr.Zero) return;
        locked = target;
        hover = target;
        shownRect = PickerNative.TryGetFrameBounds(target, out var bounds) ? ToPixelRect(bounds) : shownRect;
        Refresh(animate: false);
    }

    private void StartLocked()
    {
        if (finished || locked == IntPtr.Zero) return;
        if (!PickerNative.IsWindow(locked) || PickerNative.IsIconic(locked) || !PickerNative.TryGetFrameBounds(locked, out var bounds)) return;
        Finish(new PickedWindow(locked, ToPixelRect(bounds), PickerNative.GetWindowTitle(locked)));
    }

    private string ApplicationName(IntPtr handle)
    {
        if (!applicationNames.TryGetValue(handle, out var name))
        {
            name = PickerNative.GetApplicationName(handle);
            if (applicationNames.Count > 128) applicationNames.Clear();
            applicationNames[handle] = name;
        }
        return name;
    }

    private void Refresh(bool animate)
    {
        var handle = locked != IntPtr.Zero ? locked : hover;
        var rect = handle != IntPtr.Zero ? shownRect : null;
        PickerNative.GetCursorPos(out var cursor);
        // Capsule lives on the display that holds the window's centre (or the pointer when nothing is hovered).
        var anchorX = rect is null ? cursor.X : rect.X + rect.Width / 2;
        var anchorY = rect is null ? cursor.Y : rect.Y + rect.Height / 2;
        var host = windows.Values.FirstOrDefault(w => anchorX >= w.ScreenRect.X && anchorX < w.ScreenRect.X + w.ScreenRect.Width &&
            anchorY >= w.ScreenRect.Y && anchorY < w.ScreenRect.Y + w.ScreenRect.Height) ?? windows.Values.FirstOrDefault();

        string name = "选择要录制的窗口", detail = "移动到窗口上，单击锁定 · Esc 取消";
        if (rect is not null)
        {
            var title = PickerNative.GetWindowTitle(handle);
            var application = ApplicationName(handle);
            name = string.IsNullOrWhiteSpace(application) ? title : application;
            // A window titled after its own app says nothing new; show its size instead (Mac v0.1.4).
            detail = !string.IsNullOrWhiteSpace(title) && !string.Equals(title, application, StringComparison.CurrentCultureIgnoreCase)
                ? title : $"{rect.Width} × {rect.Height}";
        }
        foreach (var window in windows.Values)
        {
            window.ShowTarget(rect, locked != IntPtr.Zero, animate);
            window.ShowCapsule(window == host, name, detail, locked != IntPtr.Zero);
        }
    }
}

internal sealed class WindowPickerWindow : PickerOverlayWindow
{
    private readonly Grid root = new();
    private readonly RectangleGeometry full = new();
    private readonly RectangleGeometry cut = new() { RadiusX = 8, RadiusY = 8 };
    private readonly ShapePath dimPath = new() { IsHitTestVisible = false };
    private readonly ShapePath outline = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border capsule = new();
    private readonly TextBlock nameText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = PickerStyle.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
    private readonly TextBlock detailText = new() { FontSize = 11.5, Foreground = PickerStyle.WhiteMuted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280, Margin = new Thickness(0, 2, 0, 0) };
    private readonly PickerChip startChip = new("单击窗口以锁定", PickerChip.Look.Plain, 168, 40) { IsFilled = true, IsActive = false };
    private bool hasTarget;

    internal event Action? CanvasClicked;
    internal event Action? StartRequested;
    internal event Action? CancelRequested;

    internal WindowPickerWindow(PixelRect screenRect) : base(screenRect, noActivate: true)
    {
        Title = "DogSC · 选择窗口";
        dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, cut);
        dimPath.Fill = PickerStyle.Frozen(Color.FromArgb(0x40, 0, 0, 0));
        outline.Data = cut;
        outline.Stroke = PickerStyle.Frozen(PickerStyle.Accent);
        outline.StrokeThickness = 3;
        root.Children.Add(dimPath);
        root.Children.Add(outline);

        var cancel = new PickerChip("取消", PickerChip.Look.Plain, 0, 40);
        cancel.Click += () => CancelRequested?.Invoke();
        startChip.Click += () => StartRequested?.Invoke();
        var text = new StackPanel { Margin = new Thickness(10, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(nameText);
        text.Children.Add(detailText);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(cancel);
        startChip.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(startChip);
        capsule.Child = row;
        capsule.Background = PickerStyle.Glass;
        capsule.BorderBrush = PickerStyle.GlassBorder;
        capsule.BorderThickness = new Thickness(1);
        capsule.CornerRadius = new CornerRadius(30);
        capsule.Padding = new Thickness(10, 8, 8, 8);
        capsule.HorizontalAlignment = HorizontalAlignment.Center;
        capsule.VerticalAlignment = VerticalAlignment.Bottom;
        capsule.Margin = new Thickness(0, 0, 0, 96);
        capsule.Visibility = Visibility.Collapsed;
        root.Children.Add(capsule);
        Content = root;

        root.SizeChanged += (_, e) => full.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInsideCapsule(source)) return;
            e.Handled = true;
            CanvasClicked?.Invoke();
        };
    }

    private bool IsInsideCapsule(DependencyObject element)
    {
        for (var current = element; current is not null;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, capsule)) return true;
        return false;
    }

    /// <summary>True when a physical screen point lies over this overlay's capsule (hover must not change then).</summary>
    internal bool ControlsContain(int screenX, int screenY)
    {
        if (capsule.Visibility != Visibility.Visible || !capsule.IsLoaded) return false;
        try
        {
            var bounds = capsule.TransformToAncestor(root).TransformBounds(new Rect(capsule.RenderSize));
            bounds.Inflate(8, 8);
            return bounds.Contains(ToLocal(screenX, screenY));
        }
        catch (InvalidOperationException) { return false; }
    }

    internal void ShowTarget(PixelRect? target, bool locked, bool animate)
    {
        if (target is null)
        {
            cut.BeginAnimation(RectangleGeometry.RectProperty, null);
            cut.Rect = new Rect(0, 0, 0, 0);
            outline.Visibility = Visibility.Collapsed;
            dimPath.Fill = PickerStyle.Frozen(Color.FromArgb(0x40, 0, 0, 0));
            hasTarget = false;
            return;
        }
        var local = ToLocal(target);
        dimPath.Fill = PickerStyle.Frozen(Color.FromArgb(0x78, 0, 0, 0));
        outline.StrokeThickness = locked ? 4 : 3;
        outline.Stroke = PickerStyle.Frozen(locked ? Colors.White : PickerStyle.Accent);
        if (!hasTarget || !animate || !PickerStyle.Animate)
        {
            cut.BeginAnimation(RectangleGeometry.RectProperty, null);
            cut.Rect = local;
        }
        else
        {
            // Mac: the highlight slides between windows (0.26s) instead of jumping.
            cut.BeginAnimation(RectangleGeometry.RectProperty,
                new RectAnimation(local, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (!hasTarget)
        {
            outline.Visibility = Visibility.Visible;
            if (PickerStyle.Animate) outline.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        hasTarget = true;
    }

    internal void ShowCapsule(bool visible, string name, string detail, bool locked)
    {
        capsule.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        nameText.Text = name;
        detailText.Text = detail;
        startChip.IsPrimary = locked;
        startChip.IsActive = locked;
        if (locked) startChip.SetText("● 开始录制", "Ctrl+R");
        else startChip.SetText("单击窗口以锁定");
    }
}
