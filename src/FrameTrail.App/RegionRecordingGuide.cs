using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using FrameTrail.Capture;

namespace FrameTrail.App;

/// <summary>
/// D-04 (Mac RegionRecordingGuide): while a region is being recorded, a thin frame with corner marks is drawn just OUTSIDE the
/// recorded rectangle, so it never covers the content, can be seen by the presenter and is not part of the recording.
/// The window is click-through (WS_EX_TRANSPARENT), never activates, stays out of Alt+Tab (WS_EX_TOOLWINDOW) and is excluded from
/// capture (NativeWindows.Exclude, done by the picker base class). The region is in physical screen pixels; placement and the
/// DIP conversion come from <see cref="PickerOverlayWindow"/> so mixed-DPI monitors work.
/// </summary>
internal sealed class RegionRecordingGuide : PickerOverlayWindow
{
    /// <summary>Device pixels between the recorded rectangle and the outer edge of this window; the frame lives in this band.</summary>
    private const int Band = 6;
    private static readonly Color Accent = Color.FromRgb(227, 101, 85);
    private readonly PixelRect region;
    private readonly Canvas canvas = new() { IsHitTestVisible = false };

    internal RegionRecordingGuide(PixelRect region)
        : base(new PixelRect(region.X - Band, region.Y - Band, region.Width + 2 * Band, region.Height + 2 * Band), noActivate: true)
    {
        this.region = region;
        Title = "DogSC · 录制区域";
        // The base class paints an alpha-1 background so pickers are hit-testable; this guide must let every click through.
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        Focusable = false;
        Content = canvas;
        SourceInitialized += (_, _) => PickerNative.AddExStyle(new WindowInteropHelper(this).Handle, WsExTransparent);
        Draw();
    }

    private const long WsExTransparent = 0x00000020L;

    protected override void OnLayoutScaleChanged() => Draw();

    private void Draw()
    {
        if (region is null) return; // base constructor may report the scale before the fields are set
        canvas.Children.Clear();
        var scale = Scale > 0 ? Scale : 1;
        var thin = 2 / scale;      // 2 device pixels
        var thick = 4 / scale;     // corner marks
        var inner = ToLocal(region);
        // White line directly outside the region, with a faint dark halo further out so it reads on light backgrounds as well.
        AddFrame(inner, thin, Color.FromArgb(235, 255, 255, 255), 0);
        AddFrame(inner, 1 / scale, Color.FromArgb(120, 0, 0, 0), thin);
        // Corner brackets in the recording red, 18 DIP long, lying outside the region.
        var length = Math.Min(18, Math.Min(inner.Width, inner.Height) / 3);
        var accent = new SolidColorBrush(Accent);
        foreach (var (cx, cy, dx, dy) in new[] { (inner.Left, inner.Top, 1, 1), (inner.Right, inner.Top, -1, 1), (inner.Left, inner.Bottom, 1, -1), (inner.Right, inner.Bottom, -1, -1) })
        {
            // Horizontal arm.
            AddBar(accent, dx > 0 ? cx - thick : cx - length, dy > 0 ? cy - thick : cy, length + thick, thick);
            // Vertical arm.
            AddBar(accent, dx > 0 ? cx - thick : cx, dy > 0 ? cy - thick : cy - length, thick, length + thick);
        }
    }

    private void AddFrame(Rect inner, double thickness, Color color, double offset)
    {
        var rectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(color), StrokeThickness = thickness, Fill = null, IsHitTestVisible = false,
            Width = inner.Width + 2 * (offset + thickness), Height = inner.Height + 2 * (offset + thickness)
        };
        Canvas.SetLeft(rectangle, inner.Left - offset - thickness);
        Canvas.SetTop(rectangle, inner.Top - offset - thickness);
        canvas.Children.Add(rectangle);
    }

    private void AddBar(Brush brush, double x, double y, double width, double height)
    {
        var bar = new Rectangle { Fill = brush, Width = width, Height = height, IsHitTestVisible = false };
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        canvas.Children.Add(bar);
    }
}
