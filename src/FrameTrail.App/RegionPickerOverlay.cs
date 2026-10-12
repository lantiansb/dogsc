using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameTrail.Capture;
using ShapeEllipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;

namespace FrameTrail.App;

/// <summary>Aspect preset / lock state shared by the per-display region overlays (Mac AspectPreset, dimensionLinkEnabled).</summary>
internal sealed class RegionPickerState
{
    internal static readonly string[] PresetNames = ["自由", "16:9", "4:3", "1:1", "9:16"];
    internal static readonly double?[] PresetRatios = [null, 16d / 9, 4d / 3, 1, 9d / 16];

    internal int Preset { get; set; }
    internal bool LinkEnabled { get; set; }
    internal double? Ratio => PresetRatios[Preset];
}

/// <summary>
/// Mac CaptureAreaSelection (v0.1.3/0.1.4). Drag a region (≥ 24 DIP); releasing keeps it with 8 handles,
/// dragging inside moves it, a capsule toolbar offers aspect presets, W×H entry (Enter applies, min 64 px, even),
/// aspect lock, cancel and "开始录制 Ctrl+R". Enter / Ctrl+R confirm, Esc cancels. Output is the absolute
/// physical-pixel rectangle on a single display with even width and height.
/// </summary>
internal sealed class RegionPickerSession
{
    private readonly List<RegionPickerWindow> windows = [];
    private readonly RegionPickerState state = new();
    private readonly TaskCompletionSource<PixelRect?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PickerKeyboard? keys;
    private RegionPickerWindow? active;
    private bool finished;

    /// <summary>Returns the chosen region in absolute physical pixels, or null when cancelled.</summary>
    internal static Task<PixelRect?> PickAsync(IReadOnlyList<DisplayInfo> displays) => new RegionPickerSession().RunAsync(displays);

    private async Task<PixelRect?> RunAsync(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) throw new InvalidOperationException("没有可录制的显示器。");
        try
        {
            foreach (var display in displays)
            {
                var window = new RegionPickerWindow(display.Bounds, state);
                window.SelectionStarted += OnSelectionStarted;
                window.ConfirmRequested += Confirm;
                window.CancelRequested += () => Finish(null);
                windows.Add(window);
                window.Show();
            }
            keys = new PickerKeyboard(() => Finish(null), Confirm);
            // Changing the monitor layout mid-selection invalidates the coordinates: cancel (Mac closes the selector).
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            PickerNative.GetCursorPos(out var cursor);
            var home = windows.FirstOrDefault(w => cursor.X >= w.ScreenRect.X && cursor.X < w.ScreenRect.X + w.ScreenRect.Width &&
                cursor.Y >= w.ScreenRect.Y && cursor.Y < w.ScreenRect.Y + w.ScreenRect.Height) ?? windows[0];
            home.Activate();
            home.Focus();
            return await done.Task;
        }
        finally
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            keys?.Dispose();
            foreach (var window in windows) window.Close();
            windows.Clear();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Finish(null)));

    private void Finish(PixelRect? result)
    {
        if (finished) return;
        finished = true;
        done.TrySetResult(result);
    }

    private void OnSelectionStarted(RegionPickerWindow window)
    {
        active = window;
        foreach (var other in windows)
        {
            other.HideHint();
            if (!ReferenceEquals(other, window)) other.ClearSelection();
        }
    }

    private void Confirm()
    {
        if (finished) return;
        var target = active ?? windows.FirstOrDefault(w => w.HasSelection);
        if (target is null || !target.TryGetFinalRect(out var rect))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        Finish(rect);
    }
}

internal sealed class RegionPickerWindow : PickerOverlayWindow
{
    private enum Handle { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }
    private enum Operation { None, Create, Move, Resize }

    private readonly RegionPickerState state;
    private readonly Grid root = new();
    private readonly RectangleGeometry full = new();
    private readonly RectangleGeometry cut = new();
    private readonly ShapePath dimPath = new() { IsHitTestVisible = false };
    private readonly ShapePath outline = new() { IsHitTestVisible = false, Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
    private readonly Canvas layer = new();
    private readonly ShapeEllipse[] handleDots = new ShapeEllipse[8];
    private readonly Border hint = new();
    private readonly Border toolbar = new();
    private readonly PickerChip[] presetChips = new PickerChip[5];
    private readonly PickerChip linkChip = new("", PickerChip.Look.Plain, 36, 34);
    private readonly TextBox widthBox = new();
    private readonly TextBox heightBox = new();

    private Rect? selection;           // local physical pixels (0,0 = display top-left)
    private Operation operation;
    private Point anchor;
    private Vector moveOffset;
    private Handle resizeHandle;
    private Rect resizeOriginal;

    internal event Action<RegionPickerWindow>? SelectionStarted;
    internal event Action? ConfirmRequested;
    internal event Action? CancelRequested;

    internal bool HasSelection => selection is not null;

    private double DisplayWidth => ScreenRect.Width;
    private double DisplayHeight => ScreenRect.Height;
    private double MinSide => Math.Ceiling(24 * Scale);

    internal RegionPickerWindow(PixelRect screenRect, RegionPickerState state) : base(screenRect, noActivate: false)
    {
        this.state = state;
        Title = "DogSC · 框选录制区域";
        Cursor = Cursors.Cross;
        Focusable = true;

        dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, cut);
        dimPath.Fill = PickerStyle.Frozen(Color.FromArgb(0x8C, 0, 0, 0));
        outline.Data = cut;
        root.Children.Add(dimPath);
        root.Children.Add(outline);
        for (var i = 0; i < handleDots.Length; i++)
        {
            handleDots[i] = new ShapeEllipse
            {
                Width = 10, Height = 10, Fill = Brushes.White, Stroke = PickerStyle.Frozen(Color.FromArgb(0x99, 0, 0, 0)),
                StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            layer.Children.Add(handleDots[i]);
        }
        BuildHint();
        BuildToolbar();
        layer.Children.Add(toolbar);
        root.Children.Add(layer);
        root.Children.Add(hint);
        Content = root;

        root.SizeChanged += (_, e) => { full.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height); Redraw(); };
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => { if (operation != Operation.None) EndOperation(); };
        PreviewKeyDown += OnKeyDown;
    }

    protected override void OnLayoutScaleChanged() => Redraw();

    // ---- UI construction -------------------------------------------------------------------------------

    private void BuildHint()
    {
        hint.Child = new TextBlock
        {
            Text = "拖动选择录制区域 · Esc 取消", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = PickerStyle.White
        };
        hint.Background = PickerStyle.Glass;
        hint.BorderBrush = PickerStyle.GlassBorder;
        hint.BorderThickness = new Thickness(1);
        hint.CornerRadius = new CornerRadius(28);
        hint.Padding = new Thickness(26, 14, 26, 14);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
    }

    private void BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < presetChips.Length; i++)
        {
            var index = i;
            var chip = new PickerChip(RegionPickerState.PresetNames[i], PickerChip.Look.Plain, 0, 34) { Margin = new Thickness(0, 0, 2, 0) };
            chip.Padding = new Thickness(11, 0, 11, 0);
            chip.Click += () => ChangePreset(index);
            presetChips[i] = chip;
            row.Children.Add(chip);
        }
        row.Children.Add(Separator());
        ConfigureBox(widthBox, "宽度（像素）");
        ConfigureBox(heightBox, "高度（像素）");
        row.Children.Add(widthBox);
        row.Children.Add(new TextBlock { Text = "×", Foreground = PickerStyle.WhiteMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
        row.Children.Add(heightBox);
        linkChip.SetGlyph("");
        linkChip.Padding = new Thickness(0);
        linkChip.Margin = new Thickness(4, 0, 0, 0);
        linkChip.Click += () => { state.LinkEnabled = !state.LinkEnabled; RefreshToolbarState(); };
        row.Children.Add(linkChip);
        row.Children.Add(Separator());
        var cancel = new PickerChip("取消", PickerChip.Look.Plain, 0, 34);
        cancel.Click += () => CancelRequested?.Invoke();
        row.Children.Add(cancel);
        var start = new PickerChip("● 开始录制", PickerChip.Look.Primary, 0, 34) { Margin = new Thickness(4, 0, 0, 0) };
        start.SetText("● 开始录制", "Ctrl+R");
        start.Click += () => ConfirmRequested?.Invoke();
        row.Children.Add(start);

        toolbar.Child = row;
        toolbar.Background = PickerStyle.Glass;
        toolbar.BorderBrush = PickerStyle.GlassBorder;
        toolbar.BorderThickness = new Thickness(1);
        toolbar.CornerRadius = new CornerRadius(30);
        toolbar.Padding = new Thickness(10, 7, 8, 7);
        toolbar.Cursor = Cursors.Arrow;
        toolbar.Visibility = Visibility.Collapsed;
        RefreshToolbarState();
    }

    private static Border Separator() => new()
    {
        Width = 1, Height = 20, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = PickerStyle.Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
    };

    private void ConfigureBox(TextBox box, string tooltip)
    {
        box.Width = 62;
        box.Height = 32;
        box.Margin = new Thickness(0);
        box.Padding = new Thickness(2, 0, 2, 0);
        box.MaxLength = 5;
        box.TextAlignment = TextAlignment.Center;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.FontSize = 13;
        box.Foreground = PickerStyle.White;
        box.CaretBrush = Brushes.White;
        box.Background = PickerStyle.Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        box.ToolTip = tooltip + "，回车应用";
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }

    private void RefreshToolbarState()
    {
        for (var i = 0; i < presetChips.Length; i++) presetChips[i].IsSelected = i == state.Preset;
        linkChip.IsSelected = state.LinkEnabled;
        linkChip.ToolTip = state.LinkEnabled ? "宽高比例已锁定" : "宽高比例未锁定";
    }

    // ---- Public API for the session ---------------------------------------------------------------------

    internal void HideHint() => hint.Visibility = Visibility.Collapsed;

    internal void ClearSelection()
    {
        selection = null;
        operation = Operation.None;
        Redraw();
    }

    /// <summary>Absolute physical rectangle with even size, or false when the selection is missing / too small.</summary>
    internal bool TryGetFinalRect(out PixelRect rect)
    {
        rect = new PixelRect(0, 0, 0, 0);
        if (selection is not { } s || s.Width < MinSide || s.Height < MinSide) return false;
        var left = (int)Math.Max(0, Math.Floor(s.Left)) / 2 * 2;
        var top = (int)Math.Max(0, Math.Floor(s.Top)) / 2 * 2;
        var right = (int)Math.Min(DisplayWidth, Math.Ceiling(s.Right));
        var bottom = (int)Math.Min(DisplayHeight, Math.Ceiling(s.Bottom));
        var width = (right - left) / 2 * 2;
        var height = (bottom - top) / 2 * 2;
        if (width < 2 || height < 2) return false;
        rect = new PixelRect(ScreenRect.X + left, ScreenRect.Y + top, width, height);
        return true;
    }

    // ---- Geometry helpers (all in local physical pixels) -----------------------------------------------

    private Point LocalPixels(MouseEventArgs e)
    {
        var p = e.GetPosition(root);
        return new Point(Math.Clamp(p.X * Scale, 0, DisplayWidth), Math.Clamp(p.Y * Scale, 0, DisplayHeight));
    }

    private static Rect Integral(Rect r)
    {
        var left = Math.Floor(r.Left);
        var top = Math.Floor(r.Top);
        return new Rect(left, top, Math.Ceiling(r.Right) - left, Math.Ceiling(r.Bottom) - top);
    }

    /// <summary>Mac selectionRect(from:to:constrainedAspect:).</summary>
    private Rect RectFrom(Point from, Point to, double? aspect)
    {
        var horizontal = to.X >= from.X ? 1 : -1;
        var vertical = to.Y >= from.Y ? 1 : -1;
        var maxWidth = horizontal > 0 ? DisplayWidth - from.X : from.X;
        var maxHeight = vertical > 0 ? DisplayHeight - from.Y : from.Y;
        var width = Math.Min(Math.Abs(to.X - from.X), maxWidth);
        var height = Math.Min(Math.Abs(to.Y - from.Y), maxHeight);
        if (aspect is { } ratio && width > 0 && height > 0)
        {
            if (width / height > ratio) height = width / ratio; else width = height * ratio;
            if (width > maxWidth) { width = maxWidth; height = width / ratio; }
            if (height > maxHeight) { height = maxHeight; width = height * ratio; }
        }
        return Integral(new Rect(horizontal > 0 ? from.X : from.X - width, vertical > 0 ? from.Y : from.Y - height, width, height));
    }

    private static Point HandlePoint(Handle handle, Rect r) => handle switch
    {
        Handle.TopLeft => new Point(r.Left, r.Top),
        Handle.Top => new Point(r.Left + r.Width / 2, r.Top),
        Handle.TopRight => new Point(r.Right, r.Top),
        Handle.Left => new Point(r.Left, r.Top + r.Height / 2),
        Handle.Right => new Point(r.Right, r.Top + r.Height / 2),
        Handle.BottomLeft => new Point(r.Left, r.Bottom),
        Handle.Bottom => new Point(r.Left + r.Width / 2, r.Bottom),
        _ => new Point(r.Right, r.Bottom)
    };

    private Handle? HandleNear(Point point, Rect r)
    {
        var radius = 16 * Scale;
        foreach (var handle in Enum.GetValues<Handle>())
        {
            var location = HandlePoint(handle, r);
            if (Math.Sqrt((point.X - location.X) * (point.X - location.X) + (point.Y - location.Y) * (point.Y - location.Y)) <= radius) return handle;
        }
        return null;
    }

    private Rect Resized(Rect original, Handle handle, Point point)
    {
        var min = MinSide;
        switch (handle)
        {
            case Handle.TopLeft: return RectFrom(new Point(original.Right, original.Bottom), point, state.Ratio);
            case Handle.TopRight: return RectFrom(new Point(original.Left, original.Bottom), point, state.Ratio);
            case Handle.BottomLeft: return RectFrom(new Point(original.Right, original.Top), point, state.Ratio);
            case Handle.BottomRight: return RectFrom(new Point(original.Left, original.Top), point, state.Ratio);
            case Handle.Left:
            {
                var left = Math.Min(Math.Max(point.X, 0), original.Right - min);
                return Integral(new Rect(left, original.Top, original.Right - left, original.Height));
            }
            case Handle.Right:
            {
                var width = Math.Min(Math.Max(point.X - original.Left, min), DisplayWidth - original.Left);
                return Integral(new Rect(original.Left, original.Top, width, original.Height));
            }
            case Handle.Top:
            {
                var top = Math.Min(Math.Max(point.Y, 0), original.Bottom - min);
                return Integral(new Rect(original.Left, top, original.Width, original.Bottom - top));
            }
            default:
            {
                var height = Math.Min(Math.Max(point.Y - original.Top, min), DisplayHeight - original.Top);
                return Integral(new Rect(original.Left, original.Top, original.Width, height));
            }
        }
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value) / 2 * 2);

    /// <summary>Mac aspectRect: new aspect, same centre and area, clamped into the display.</summary>
    private Rect AspectRect(double aspect, Rect current)
    {
        var centerX = current.Left + current.Width / 2;
        var centerY = current.Top + current.Height / 2;
        var width = Math.Sqrt(current.Width * current.Height * aspect);
        var height = width / aspect;
        var maxWidth = DisplayWidth - 16;
        var maxHeight = DisplayHeight - 16;
        if (width > maxWidth) { width = maxWidth; height = width / aspect; }
        if (height > maxHeight) { height = maxHeight; width = height * aspect; }
        int w = Even(width), h = Even(height);
        var x = Math.Clamp(Math.Round(centerX - w / 2d), 8, Math.Max(8, DisplayWidth - w - 8));
        var y = Math.Clamp(Math.Round(centerY - h / 2d), 8, Math.Max(8, DisplayHeight - h - 8));
        return new Rect(x, y, w, h);
    }

    // ---- Mouse -------------------------------------------------------------------------------------------

    private bool IsInsideToolbar(DependencyObject? element)
    {
        for (var current = element; current is not null;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, toolbar)) return true;
        return false;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideToolbar(e.OriginalSource as DependencyObject)) return;
        // Clicking the canvas drops keyboard focus from the size boxes so Enter / Esc act on the selection again.
        Keyboard.ClearFocus();
        Focus();
        var point = LocalPixels(e);
        if (selection is { } existing && HandleNear(point, existing) is { } handle)
        {
            operation = Operation.Resize;
            resizeHandle = handle;
            resizeOriginal = existing;
        }
        else if (selection is { } inside && inside.Contains(point))
        {
            operation = Operation.Move;
            moveOffset = new Vector(point.X - inside.Left, point.Y - inside.Top);
        }
        else
        {
            SelectionStarted?.Invoke(this);
            selection = null;
            operation = Operation.Create;
            anchor = point;
        }
        e.Handled = true;
        CaptureMouse();
        Redraw();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = LocalPixels(e);
        if (operation == Operation.None)
        {
            if (!IsInsideToolbar(e.OriginalSource as DependencyObject)) UpdateCursor(point);
            return;
        }
        switch (operation)
        {
            case Operation.Create:
                selection = RectFrom(anchor, point, state.Ratio);
                break;
            case Operation.Move when selection is { } current:
                var x = Math.Clamp(point.X - moveOffset.X, 0, DisplayWidth - current.Width);
                var y = Math.Clamp(point.Y - moveOffset.Y, 0, DisplayHeight - current.Height);
                selection = new Rect(Math.Round(x), Math.Round(y), current.Width, current.Height);
                break;
            case Operation.Resize:
                selection = Resized(resizeOriginal, resizeHandle, point);
                break;
        }
        Redraw();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (operation == Operation.None) return;
        e.Handled = true;
        ReleaseMouseCapture();
        EndOperation();
    }

    private void EndOperation()
    {
        operation = Operation.None;
        // A click or tiny drag keeps the selector open (Mac beeps and stays in the selecting state).
        if (selection is not { } rect || rect.Width < MinSide || rect.Height < MinSide)
        {
            selection = null;
            System.Media.SystemSounds.Beep.Play();
        }
        Redraw();
    }

    private void UpdateCursor(Point point)
    {
        if (selection is not { } rect) { Cursor = Cursors.Cross; return; }
        if (HandleNear(point, rect) is { } handle)
        {
            Cursor = handle switch
            {
                Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
                Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
                Handle.Top or Handle.Bottom => Cursors.SizeNS,
                _ => Cursors.SizeWE
            };
        }
        else Cursor = rect.Contains(point) ? Cursors.SizeAll : Cursors.Cross;
    }

    // ---- Keyboard ----------------------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke();
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ConfirmRequested?.Invoke();
        }
        else if (e.Key is Key.Return or Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.FocusedElement == widthBox || Keyboard.FocusedElement == heightBox) ApplyManualSize();
            else ConfirmRequested?.Invoke();
        }
    }

    // ---- Toolbar actions ---------------------------------------------------------------------------------

    private void ChangePreset(int index)
    {
        // Clicking the current preset again does nothing (Mac changeAspectPreset guard).
        if (index == state.Preset) return;
        state.Preset = index;
        state.LinkEnabled = state.Ratio is not null;
        RefreshToolbarState();
        if (state.Ratio is { } ratio && selection is { } current) selection = AspectRect(ratio, current);
        Redraw();
    }

    /// <summary>Mac applyManualSize: min 64 px, even, must fit the display; unlinked edits reset the preset to 自由.</summary>
    private void ApplyManualSize()
    {
        if (!int.TryParse(widthBox.Text, out var width) || !int.TryParse(heightBox.Text, out var height) || width < 64 || height < 64)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        if (state.LinkEnabled && state.Ratio is { } ratio)
        {
            height = Math.Max((int)Math.Round(width / ratio), 64);
            heightBox.Text = height.ToString();
        }
        else
        {
            state.Preset = 0;
            RefreshToolbarState();
        }
        width = Math.Max(width / 2 * 2, 64);
        height = Math.Max(height / 2 * 2, 64);
        if (width > DisplayWidth || height > DisplayHeight)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        var center = selection is { } current
            ? new Point(current.Left + current.Width / 2, current.Top + current.Height / 2)
            : new Point(DisplayWidth / 2, DisplayHeight / 2);
        var x = Math.Clamp(Math.Round(center.X - width / 2d), 0, DisplayWidth - width);
        var y = Math.Clamp(Math.Round(center.Y - height / 2d), 0, DisplayHeight - height);
        selection = new Rect(x, y, width, height);
        Redraw(updateFields: false);
    }

    // ---- Rendering ---------------------------------------------------------------------------------------

    private void Redraw(bool updateFields = true)
    {
        if (root.ActualWidth <= 0 || Scale <= 0) return;
        if (selection is not { } rect)
        {
            cut.Rect = new Rect(0, 0, 0, 0);
            outline.Visibility = Visibility.Collapsed;
            foreach (var dot in handleDots) dot.Visibility = Visibility.Collapsed;
            toolbar.Visibility = Visibility.Collapsed;
            dimPath.Fill = PickerStyle.Frozen(Color.FromArgb(0x8C, 0, 0, 0));
            return;
        }
        var local = new Rect(rect.Left / Scale, rect.Top / Scale, rect.Width / Scale, rect.Height / Scale);
        cut.Rect = local;
        outline.Visibility = Visibility.Visible;
        var handles = Enum.GetValues<Handle>();
        for (var i = 0; i < handles.Length; i++)
        {
            var p = HandlePoint(handles[i], local);
            Canvas.SetLeft(handleDots[i], p.X - 5);
            Canvas.SetTop(handleDots[i], p.Y - 5);
            handleDots[i].Visibility = Visibility.Visible;
        }
        var valid = rect.Width >= MinSide && rect.Height >= MinSide;
        // A new drag keeps the toolbar hidden; moving / resizing a settled region keeps it attached.
        var showToolbar = valid && operation != Operation.Create;
        toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
        if (updateFields)
        {
            widthBox.Text = ((int)Math.Round(rect.Width)).ToString();
            heightBox.Text = ((int)Math.Round(rect.Height)).ToString();
        }
        if (showToolbar) PlaceToolbar(local);
    }

    /// <summary>Mac controlCardFrame: 14 below the selection, otherwise above, otherwise clamped inside.</summary>
    private void PlaceToolbar(Rect selectionRect)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = toolbar.DesiredSize;
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        var x = Math.Clamp(selectionRect.Left + selectionRect.Width / 2 - size.Width / 2, 8, Math.Max(8, width - size.Width - 8));
        var y = selectionRect.Bottom + 14;
        if (y + size.Height > height - 8) y = selectionRect.Top - 14 - size.Height;
        if (y < 8) y = Math.Clamp(selectionRect.Bottom - size.Height - 14, 8, Math.Max(8, height - size.Height - 8));
        Canvas.SetLeft(toolbar, x);
        Canvas.SetTop(toolbar, y);
    }
}

/// <summary>
/// Compatibility wrapper for the hidden legacy "选择区域" button in the editor overlay: same API as the old
/// single-drag selector, now backed by the full region picker. Blocks (nested message loop) until done.
/// </summary>
public sealed class RegionSelector
{
    private readonly PixelRect display;

    public PixelRect? Region { get; private set; }

    public RegionSelector(PixelRect display) => this.display = display;

    public bool? ShowDialog()
    {
        var task = RegionPickerSession.PickAsync([new DisplayInfo("legacy", "legacy", display, false)]);
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Region = task.IsCompletedSuccessfully ? task.Result : null;
        return Region is not null;
    }
}
