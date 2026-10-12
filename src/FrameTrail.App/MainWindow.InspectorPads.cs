using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FrameTrail.App;

/// <summary>
/// Draggable 2D surface shared by the position and tilt pads. Mac: EditorPositionPad /
/// EditorTiltPad — drag previews live, release commits, Esc cancels, a crosshair follows the
/// handle while it is being edited.
/// </summary>
internal sealed class PadSurface
{
    public const double Inset = 12;

    private readonly Grid grid;
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private readonly Ellipse halo;
    private readonly Ellipse dot;
    private readonly Line verticalGuide;
    private readonly Line horizontalGuide;
    private bool dragging;
    private NormalizedPoint start;
    private NormalizedPoint shown;

    /// <summary>The rectangle that maps to 0...1 in both axes.</summary>
    public Rect Map { get; }
    public FrameworkElement Element { get; }
    public Grid Content => grid;
    public bool Dragging => dragging;

    /// <summary>Snapping / dead zone applied to the raw pointer position.</summary>
    public Func<NormalizedPoint, NormalizedPoint> Constrain { get; set; } = p => p;
    public Action<NormalizedPoint>? Changed { get; set; }
    public Action<NormalizedPoint>? Ended { get; set; }
    public Action? Cancelled { get; set; }

    public PadSurface(double width, double height, Brush background, Brush border, Brush handle, Brush guide, Rect? map = null)
    {
        Map = map ?? new Rect(Inset, Inset, Math.Max(1, width - Inset * 2), Math.Max(1, height - Inset * 2));
        grid = new Grid { Width = width, Height = height, Background = background, ClipToBounds = true, Focusable = true, FocusVisualStyle = null, Cursor = Cursors.Cross };
        halo = new Ellipse { Width = 26, Height = 26, Fill = handle, Opacity = .12, IsHitTestVisible = false };
        dot = new Ellipse
        {
            Width = 15, Height = 15, Fill = handle, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), StrokeThickness = 3,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1.5, Direction = 270, Opacity = .42 }
        };
        var dashes = new DoubleCollection([2.5, 3.5]);
        verticalGuide = new Line { Stroke = guide, StrokeThickness = .75, StrokeDashArray = dashes, Opacity = .55, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        horizontalGuide = new Line { Stroke = guide, StrokeThickness = .75, StrokeDashArray = dashes, Opacity = .55, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        overlay.Children.Add(verticalGuide);
        overlay.Children.Add(horizontalGuide);
        overlay.Children.Add(halo);
        overlay.Children.Add(dot);
        grid.Children.Add(overlay);

        grid.MouseLeftButtonDown += OnDown;
        grid.MouseMove += OnMove;
        grid.MouseLeftButtonUp += OnUp;
        grid.LostMouseCapture += (_, _) => { if (dragging) Cancel(); };
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !dragging) return;
            Cancel();
            e.Handled = true;
        };

        Element = new Border
        {
            CornerRadius = new CornerRadius(12), BorderBrush = border, BorderThickness = new Thickness(.75), Child = grid,
            Width = width, Height = height, Clip = new RectangleGeometry(new Rect(0, 0, width, height), 12, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetPoint(new NormalizedPoint(.5, .5));
    }

    public void SetPoint(NormalizedPoint point)
    {
        shown = point;
        var x = Map.X + Math.Clamp(point.X, 0, 1) * Map.Width;
        var y = Map.Y + Math.Clamp(point.Y, 0, 1) * Map.Height;
        Canvas.SetLeft(dot, x - dot.Width / 2);
        Canvas.SetTop(dot, y - dot.Height / 2);
        Canvas.SetLeft(halo, x - halo.Width / 2);
        Canvas.SetTop(halo, y - halo.Height / 2);
        verticalGuide.X1 = verticalGuide.X2 = x;
        verticalGuide.Y1 = Map.Top;
        verticalGuide.Y2 = Map.Bottom;
        horizontalGuide.Y1 = horizontalGuide.Y2 = y;
        horizontalGuide.X1 = Map.Left;
        horizontalGuide.X2 = Map.Right;
    }

    private void Activate(bool active)
    {
        verticalGuide.Visibility = horizontalGuide.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        halo.Opacity = active ? .22 : .12;
    }

    private NormalizedPoint At(MouseEventArgs e)
    {
        var p = e.GetPosition(grid);
        var raw = new NormalizedPoint(
            Math.Clamp((p.X - Map.X) / Math.Max(Map.Width, 1), 0, 1),
            Math.Clamp((p.Y - Map.Y) / Math.Max(Map.Height, 1), 0, 1));
        return Constrain(raw);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        grid.Focus();
        dragging = true;
        start = shown;
        grid.CaptureMouse();
        Activate(true);
        var point = At(e);
        SetPoint(point);
        Changed?.Invoke(point);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        var point = At(e);
        SetPoint(point);
        Changed?.Invoke(point);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        var point = At(e);
        dragging = false;
        grid.ReleaseMouseCapture();
        Activate(false);
        SetPoint(point);
        Ended?.Invoke(point);
        e.Handled = true;
    }

    private void Cancel()
    {
        dragging = false;
        if (grid.IsMouseCaptured) grid.ReleaseMouseCapture();
        Activate(false);
        SetPoint(start);
        Cancelled?.Invoke();
    }

    /// <summary>Called by readout boxes so the crosshair shows while a number is being typed.</summary>
    public void ShowGuides(bool active) => Activate(active);
}

public partial class MainWindow
{
    private const double PadSnapThreshold = .035;
    private static readonly double[] PadSnapAnchors = [0, 1d / 3, .5, 2d / 3, 1];

    /// <summary>Mac EditorPositionPad.snappedToGrid: 0, 1/3, 1/2, 2/3, 1 within 3.5%.</summary>
    private static NormalizedPoint SnapToPadGrid(NormalizedPoint p)
    {
        static double Snap(double value)
        {
            foreach (var anchor in PadSnapAnchors)
                if (Math.Abs(value - anchor) <= PadSnapThreshold) return anchor;
            return value;
        }
        return new NormalizedPoint(Snap(p.X), Snap(p.Y));
    }

    private static string FormatPercentBox(double v) => NumFormatting.Text(NumFormat.Percent, v);

    /// <summary>
    /// Position pad with live preview, snapping, crosshair, Esc-to-cancel and exact X/Y entry.
    /// Compact (default) puts the readouts beside the pad; <paramref name="centered"/> stacks them.
    /// With <paramref name="backdrop"/> the pad becomes a wide focus map over the current frame.
    /// </summary>
    private void PointPad(string label, NormalizedPoint point, Action<NormalizedPoint> apply, bool centered = false,
        string? hint = null, bool snap = true, BitmapSource? backdrop = null, double? backdropAspect = null, string? undo = null)
    {
        var gesture = new InspectorGesture(this, undo ?? "调整" + label);
        var committed = point;
        var isCentered = Math.Abs(point.X - .5) < .0005 && Math.Abs(point.Y - .5) < .0005;

        var heading = new DockPanel { Margin = new Thickness(0, centered ? 10 : 13, 0, centered ? 8 : 10) };
        var reset = new Button
        {
            Content = "⌖  居中", Style = (Style)FindResource("PlainButton"), FontSize = 10, Foreground = Brush("Secondary"),
            Padding = new Thickness(0), Margin = new Thickness(0), IsEnabled = !isCentered
        };
        System.Windows.Automation.AutomationProperties.SetName(reset, "居中" + label);
        DockPanel.SetDock(reset, Dock.Right);
        reset.Click += (_, _) => gesture.Commit(() => apply(new NormalizedPoint(.5, .5)));
        heading.Children.Add(reset);
        heading.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Medium });
        InspectorHost.Children.Add(heading);
        if (hint != null)
            InspectorHost.Children.Add(new TextBlock { Text = hint, FontSize = 10, Foreground = Brush("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -2, 0, centered ? 8 : 10) });

        double width, height;
        Rect? map = null;
        if (backdrop != null)
        {
            var aspect = Math.Max(.2, backdropAspect ?? backdrop.PixelWidth / (double)Math.Max(1, backdrop.PixelHeight));
            width = 280;
            height = Math.Clamp(width / aspect, 120, 190);
            var padded = new Rect(8, 8, width - 16, height - 16);
            var fitWidth = padded.Height * aspect;
            map = fitWidth <= padded.Width
                ? new Rect(padded.X + (padded.Width - fitWidth) / 2, padded.Y, fitWidth, padded.Height)
                : new Rect(padded.X, padded.Y + (padded.Height - padded.Width / aspect) / 2, padded.Width, padded.Width / aspect);
        }
        else
        {
            width = centered ? 184 : 158;
            height = centered ? 132 : 112;
        }

        var surface = new PadSurface(width, height, Brush("ControlSurface"), Brush("Hairline"), Brush("Ink"), Brush("Ink"), map);
        if (backdrop != null && map is { } mapRect)
        {
            var image = new Image
            {
                Source = backdrop, Stretch = Stretch.Fill, Width = mapRect.Width, Height = mapRect.Height,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(mapRect.X, mapRect.Y, 0, 0), IsHitTestVisible = false
            };
            var frame = new Border
            {
                Width = mapRect.Width, Height = mapRect.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(mapRect.X, mapRect.Y, 0, 0), CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                BorderThickness = new Thickness(.75), IsHitTestVisible = false
            };
            surface.Content.Children.Insert(0, image);
            surface.Content.Children.Insert(1, frame);
        }
        else surface.Content.Children.Insert(0, new PadGuideVisual());
        if (snap) surface.Constrain = SnapToPadGrid;
        surface.SetPoint(point);
        System.Windows.Automation.AutomationProperties.SetName(surface.Content, label);
        RegisterInspectorFocus(surface.Content, label + "画板");

        InspectorReadout? xReadout = null, yReadout = null;
        void SyncBoxes(NormalizedPoint p)
        {
            xReadout?.SetText(FormatPercentBox(p.X));
            yReadout?.SetText(FormatPercentBox(p.Y));
        }

        surface.Changed = p =>
        {
            SyncBoxes(p);
            gesture.Preview(() => apply(p));
        };
        surface.Ended = p =>
        {
            if (Math.Abs(p.X - committed.X) < 1e-6 && Math.Abs(p.Y - committed.Y) < 1e-6)
            {
                gesture.Cancel();
                FocusEditor();
                return;
            }
            committed = p;
            CommitAndReleaseFocus(() => gesture.Commit(() => apply(p)));
        };
        surface.Cancelled = () =>
        {
            gesture.Cancel();
            SyncBoxes(committed);
        };

        FrameworkElement ReadoutRow(string axis, bool isX)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(centered ? 16 : 0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = axis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Brush("Secondary") });
            var box = new TextBox { Width = 64, Margin = centered ? new Thickness(0) : new Thickness(0, 4, 0, 4), Padding = new Thickness(6), TextAlignment = TextAlignment.Center, FontSize = 11 };
            System.Windows.Automation.AutomationProperties.SetName(box, label + " " + axis + " 坐标");
            RegisterInspectorFocus(box, label + axis);
            double Current() => isX ? committed.X : committed.Y;
            NormalizedPoint With(double v) => isX ? committed with { X = v } : committed with { Y = v };
            var readout = new InspectorReadout(box,
                raw => NumFormatting.TryParse(NumFormat.Percent, raw, out var v) ? Math.Clamp(v, 0, 1) : null,
                FormatPercentBox, v => NumFormatting.EditText(NumFormat.Percent, v), Current,
                v =>
                {
                    var next = With(v);
                    surface.SetPoint(next);
                    gesture.Preview(() => apply(next));
                },
                v =>
                {
                    var next = With(v);
                    if (Math.Abs(v - Current()) < 1e-8)
                    {
                        gesture.Cancel();
                        FocusEditor();
                        return;
                    }
                    committed = next;
                    CommitAndReleaseFocus(() => gesture.Commit(() => apply(next)));
                },
                () =>
                {
                    gesture.Cancel();
                    surface.SetPoint(committed);
                    SyncBoxes(committed);
                    FocusEditor();
                });
            if (isX) xReadout = readout; else yReadout = readout;
            row.Children.Add(box);
            return row;
        }

        var fields = new StackPanel { Orientation = centered ? Orientation.Horizontal : Orientation.Vertical, HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(ReadoutRow("X", true));
        fields.Children.Add(ReadoutRow("Y", false));

        if (centered || backdrop != null)
        {
            var stack = new StackPanel();
            stack.Children.Add(surface.Element);
            fields.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(fields);
            InspectorHost.Children.Add(stack);
        }
        else
        {
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            surface.Element.HorizontalAlignment = HorizontalAlignment.Left;
            layout.Children.Add(surface.Element);
            Grid.SetColumn(fields, 1);
            layout.Children.Add(fields);
            InspectorHost.Children.Add(layout);
        }
    }

    /// <summary>
    /// Mac EditorTiltPad: handle up = +X tilt (±28°), handle right = +Y tilt (±32°), 1.4° dead zone.
    /// </summary>
    private void TiltPad(string label, double rotationX, double rotationY, Action<double, double> apply, string? hint = null, string? undo = null)
    {
        const double maxX = 28, maxY = 32, deadZone = 1.4;
        var gesture = new InspectorGesture(this, undo ?? "调整" + label);
        var committedX = Math.Clamp(rotationX, -maxX, maxX);
        var committedY = Math.Clamp(rotationY, -maxY, maxY);
        var neutral = Math.Abs(rotationX) < .0005 && Math.Abs(rotationY) < .0005;

        var heading = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        var reset = new Button
        {
            Content = "⌖  归零", Style = (Style)FindResource("PlainButton"), FontSize = 10, Foreground = Brush("Secondary"),
            Padding = new Thickness(0), Margin = new Thickness(0), IsEnabled = !neutral
        };
        System.Windows.Automation.AutomationProperties.SetName(reset, "归零 3D 倾斜");
        DockPanel.SetDock(reset, Dock.Right);
        reset.Click += (_, _) => gesture.Commit(() => apply(0, 0));
        heading.Children.Add(reset);
        heading.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Medium });
        InspectorHost.Children.Add(heading);
        if (hint != null)
            InspectorHost.Children.Add(new TextBlock { Text = hint, FontSize = 10, Foreground = Brush("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -2, 0, 8) });

        static NormalizedPoint ToPad(double x, double y) => new(y / (maxY * 2) + .5, .5 - x / (maxX * 2));
        static (double X, double Y) ToTilt(NormalizedPoint p)
        {
            var x = (.5 - p.Y) * maxX * 2;
            var y = (p.X - .5) * maxY * 2;
            return (Math.Abs(x) <= deadZone ? 0 : x, Math.Abs(y) <= deadZone ? 0 : y);
        }

        var surface = new PadSurface(184, 132,
            new LinearGradientBrush(Color.FromRgb(158, 160, 162), Color.FromRgb(218, 220, 221), 90),
            Brush("Hairline"), Brush("Ink"), Brush("Ink"));
        var visual = new TiltPadVisual { RotationX = committedX, RotationY = committedY };
        surface.Content.Children.Insert(0, new PadGuideVisual());
        surface.Content.Children.Insert(1, visual);
        surface.Constrain = p =>
        {
            var tilt = ToTilt(p);
            return ToPad(tilt.X, tilt.Y);
        };
        surface.SetPoint(ToPad(committedX, committedY));
        System.Windows.Automation.AutomationProperties.SetName(surface.Content, label);
        RegisterInspectorFocus(surface.Content, label + "画板");

        InspectorReadout? xReadout = null, yReadout = null;
        void Show(double x, double y)
        {
            visual.RotationX = x;
            visual.RotationY = y;
            visual.InvalidateVisual();
            xReadout?.SetText(NumFormatting.Text(NumFormat.DegreesDecimal, x));
            yReadout?.SetText(NumFormatting.Text(NumFormat.DegreesDecimal, y));
        }

        surface.Changed = p =>
        {
            var tilt = ToTilt(p);
            Show(tilt.X, tilt.Y);
            gesture.Preview(() => apply(tilt.X, tilt.Y));
        };
        surface.Ended = p =>
        {
            var tilt = ToTilt(p);
            if (Math.Abs(tilt.X - committedX) < 1e-6 && Math.Abs(tilt.Y - committedY) < 1e-6)
            {
                gesture.Cancel();
                FocusEditor();
                return;
            }
            committedX = tilt.X;
            committedY = tilt.Y;
            CommitAndReleaseFocus(() => gesture.Commit(() => apply(tilt.X, tilt.Y)));
        };
        surface.Cancelled = () =>
        {
            gesture.Cancel();
            Show(committedX, committedY);
        };

        var fields = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        FrameworkElement Row(string axis, bool isX)
        {
            var limit = isX ? maxX : maxY;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 3, 12, 3) };
            row.Children.Add(new TextBlock { Text = axis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Brush("Secondary") });
            var box = new TextBox { Width = 68, Margin = new Thickness(0), Padding = new Thickness(6), TextAlignment = TextAlignment.Center, FontSize = 11 };
            System.Windows.Automation.AutomationProperties.SetName(box, label + " " + axis + " 角度");
            RegisterInspectorFocus(box, label + axis);
            double Current() => isX ? committedX : committedY;
            var readout = new InspectorReadout(box,
                raw => NumFormatting.TryParse(NumFormat.DegreesDecimal, raw, out var v) ? Math.Clamp(v, -limit, limit) : null,
                v => NumFormatting.Text(NumFormat.DegreesDecimal, v), v => NumFormatting.EditText(NumFormat.DegreesDecimal, v), Current,
                v =>
                {
                    var x = isX ? v : committedX;
                    var y = isX ? committedY : v;
                    surface.SetPoint(ToPad(x, y));
                    Show(x, y);
                    gesture.Preview(() => apply(x, y));
                },
                v =>
                {
                    var x = isX ? v : committedX;
                    var y = isX ? committedY : v;
                    if (Math.Abs(v - Current()) < 1e-8)
                    {
                        gesture.Cancel();
                        FocusEditor();
                        return;
                    }
                    committedX = x;
                    committedY = y;
                    CommitAndReleaseFocus(() => gesture.Commit(() => apply(x, y)));
                },
                () =>
                {
                    gesture.Cancel();
                    surface.SetPoint(ToPad(committedX, committedY));
                    Show(committedX, committedY);
                    FocusEditor();
                });
            if (isX) xReadout = readout; else yReadout = readout;
            row.Children.Add(box);
            return row;
        }
        fields.Children.Add(Row("X", true));
        fields.Children.Add(Row("Y", false));
        var stack = new StackPanel();
        stack.Children.Add(surface.Element);
        stack.Children.Add(fields);
        InspectorHost.Children.Add(stack);
    }
}
