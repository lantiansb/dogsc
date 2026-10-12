using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace FrameTrail.App;

/// <summary>
/// Canvas editing chrome drawn over the preview image: selection outline and handles, hover outline,
/// dashed snap guides and the drag read-out bubble (Mac EditorCanvasPreview / EditorCanvasDragFeedback).
/// All geometry is in PreviewSurface device-independent pixels; the element never takes hit tests.
/// </summary>
public sealed class PreviewSelection : FrameworkElement
{
    private static readonly Color AccentColor = Color.FromRgb(70, 169, 134);
    private static readonly Pen GuidePen = MakeGuidePen();

    /// <summary>Outline of the selected object (4 points) or null.</summary>
    public IReadOnlyList<Point>? Quad { get; set; }
    /// <summary>Square corner handles (blur regions, overlays).</summary>
    public IReadOnlyList<Point>? CornerHandles { get; set; }
    /// <summary>Single round resize handle (screen, camera).</summary>
    public Point? ResizeDot { get; set; }
    /// <summary>Overlay rotation handle and the outline point its stem starts from.</summary>
    public Point? RotateHandle { get; set; }
    public Point? RotateAnchor { get; set; }
    /// <summary>Faint outline of the object under the pointer when it is not selected.</summary>
    public IReadOnlyList<Point>? HoverQuad { get; set; }
    /// <summary>Highlights the outline while a gesture is running.</summary>
    public bool Editing { get; set; }
    /// <summary>Image rectangle inside the surface; snap guides span it.</summary>
    public Rect CanvasBounds { get; set; } = Rect.Empty;
    public double? GuideX { get; set; }
    public double? GuideY { get; set; }
    /// <summary>Read-out bubble text; shown at the top when <see cref="ReadoutAtTop"/>.</summary>
    public string? Readout { get; set; }
    public bool ReadoutAtTop { get; set; }

    public PreviewSelection()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Clears every drawn item.</summary>
    public void Reset()
    {
        Quad = null; CornerHandles = null; ResizeDot = null; RotateHandle = null; RotateAnchor = null; HoverQuad = null;
        Editing = false; GuideX = null; GuideY = null; Readout = null;
        InvalidateVisual();
    }

    private static Pen MakeGuidePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(224, AccentColor.R, AccentColor.G, AccentColor.B)), 1)
        { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!CanvasBounds.IsEmpty) DrawGuides(dc);
        if (HoverQuad is { Count: 4 } hover) DrawPolygon(dc, hover, new Pen(new SolidColorBrush(Color.FromArgb(120, AccentColor.R, AccentColor.G, AccentColor.B)), 1), null);
        if (Quad is { Count: 4 } quad)
        {
            var pen = new Pen(new SolidColorBrush(AccentColor), Editing ? 2 : 1.5);
            DrawPolygon(dc, quad, pen, new SolidColorBrush(Color.FromArgb(Editing ? (byte)16 : (byte)8, AccentColor.R, AccentColor.G, AccentColor.B)));
            if (RotateHandle is { } handle && RotateAnchor is { } anchor)
            {
                dc.DrawLine(pen, anchor, handle);
                dc.DrawEllipse(Brushes.White, pen, handle, 6, 6);
            }
            if (CornerHandles is { } corners)
                foreach (var p in corners)
                    dc.DrawRoundedRectangle(Brushes.White, pen, new Rect(p.X - 5, p.Y - 5, 10, 10), 2, 2);
            if (ResizeDot is { } dot)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(Editing ? (byte)90 : (byte)50, AccentColor.R, AccentColor.G, AccentColor.B)), null, dot, Editing ? 13 : 11, Editing ? 13 : 11);
                dc.DrawEllipse(new SolidColorBrush(AccentColor), new Pen(Brushes.White, 1.5), dot, 8, 8);
            }
        }
        if (!string.IsNullOrEmpty(Readout) && !CanvasBounds.IsEmpty) DrawReadout(dc);
    }

    private static void DrawPolygon(DrawingContext dc, IReadOnlyList<Point> points, Pen pen, Brush? fill)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = fill != null };
        for (var i = 1; i < points.Count; i++) figure.Segments.Add(new LineSegment(points[i], true));
        var geometry = new PathGeometry([figure]);
        dc.DrawGeometry(fill, pen, geometry);
    }

    private void DrawGuides(DrawingContext dc)
    {
        var b = CanvasBounds;
        if (GuideX is { } gx) dc.DrawLine(GuidePen, new(b.X + gx * b.Width, b.Y), new(b.X + gx * b.Width, b.Bottom));
        if (GuideY is { } gy) dc.DrawLine(GuidePen, new(b.X, b.Y + gy * b.Height), new(b.Right, b.Y + gy * b.Height));
        if (GuideX is { } cx && GuideY is { } cy)
        {
            var p = new Point(b.X + cx * b.Width, b.Y + cy * b.Height);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(28, 31, 33)), new Pen(new SolidColorBrush(AccentColor), 1.25), p, 3.5, 3.5);
        }
    }

    private void DrawReadout(DrawingContext dc)
    {
        var text = new FormattedText(Readout!, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var width = text.Width + 24;
        var height = 30d;
        var b = CanvasBounds;
        var x = b.X + (b.Width - width) / 2;
        var y = ReadoutAtTop ? b.Y + 10 : b.Bottom - height - 10;
        var box = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(245, 28, 31, 33)), new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), .75), box, 9, 9);
        dc.DrawText(text, new Point(x + 12, y + (height - text.Height) / 2));
    }
}
