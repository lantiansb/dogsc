using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace FrameTrail.App;

/// <summary>
/// Canvas crop editor (Mac EditorCanvasPreview.cropEditor): dims everything outside the crop rectangle,
/// draws eight handles and lets the user drag the edges, corners or the whole rectangle. It edits a draft
/// only; the host commits it once from the "完成" button. Geometry is in PreviewSurface pixels.
/// </summary>
public sealed class CropOverlay : FrameworkElement
{
    private const double HandleHit = 15;
    private static readonly Color AccentColor = Color.FromRgb(70, 169, 134);
    private int dragHandle = -1; // 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L, 8 move
    private Point dragStart;
    private NormalizedRect dragOrigin;

    /// <summary>The source image rectangle inside this element.</summary>
    public Rect ImageBounds { get; set; } = Rect.Empty;
    private NormalizedRect crop = NormalizedRect.Full;
    /// <summary>Current draft in source-normalized coordinates.</summary>
    public NormalizedRect Crop { get => crop; set { crop = value; InvalidateVisual(); } }
    /// <summary>Minimum crop size as fractions of the source (Mac: max(24px / source, 0.02)).</summary>
    public double MinWidth01 { get; set; } = .02;
    public double MinHeight01 { get; set; } = .02;
    /// <summary>Raised on every pointer move that changes the draft.</summary>
    public event Action<NormalizedRect>? CropChanged;

    public CropOverlay()
    {
        Focusable = false;
        SizeChanged += (_, _) => InvalidateVisual();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => dragHandle = -1;
    }

    private Rect SelectionRect
    {
        get
        {
            var b = ImageBounds;
            return b.IsEmpty ? Rect.Empty : new(b.X + crop.X * b.Width, b.Y + crop.Y * b.Height, crop.Width * b.Width, crop.Height * b.Height);
        }
    }

    private static Point[] Handles(Rect r) =>
    [
        r.TopLeft, new(r.X + r.Width / 2, r.Y), r.TopRight, new(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new(r.X, r.Y + r.Height / 2)
    ];

    private int HitTest(Point p)
    {
        var r = SelectionRect;
        if (r.IsEmpty) return -1;
        var handles = Handles(r);
        var best = -1; var bestDistance = HandleHit;
        for (var i = 0; i < handles.Length; i++)
        {
            var d = (p - handles[i]).Length;
            if (d <= bestDistance) { best = i; bestDistance = d; }
        }
        if (best >= 0) return best;
        return r.Contains(p) ? 8 : -1;
    }

    private static Cursor CursorFor(int handle) => handle switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        3 or 7 => Cursors.SizeWE,
        8 => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var handle = HitTest(p);
        if (handle < 0) return;
        dragHandle = handle; dragStart = p; dragOrigin = crop;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (dragHandle < 0 || !IsMouseCaptured) { Cursor = CursorFor(HitTest(p)); return; }
        var b = ImageBounds;
        if (b.IsEmpty) return;
        var dx = (p.X - dragStart.X) / Math.Max(b.Width, 1);
        var dy = (p.Y - dragStart.Y) / Math.Max(b.Height, 1);
        crop = Resize(dragOrigin, dragHandle, dx, dy);
        InvalidateVisual();
        CropChanged?.Invoke(crop);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (dragHandle < 0) return;
        dragHandle = -1;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Mac resizedCrop / cropMoveGesture.</summary>
    private NormalizedRect Resize(NormalizedRect origin, int handle, double dx, double dy)
    {
        var left = origin.X; var right = origin.X + origin.Width;
        var top = origin.Y; var bottom = origin.Y + origin.Height;
        if (handle == 8)
        {
            var x = Math.Clamp(origin.X + dx, 0, 1 - origin.Width);
            var y = Math.Clamp(origin.Y + dy, 0, 1 - origin.Height);
            return new(x, y, origin.Width, origin.Height);
        }
        var moveLeft = handle is 0 or 6 or 7; var moveRight = handle is 2 or 3 or 4;
        var moveTop = handle is 0 or 1 or 2; var moveBottom = handle is 4 or 5 or 6;
        if (moveLeft) left = Math.Min(Math.Max(left + dx, 0), right - MinWidth01);
        if (moveRight) right = Math.Max(Math.Min(right + dx, 1), left + MinWidth01);
        if (moveTop) top = Math.Min(Math.Max(top + dy, 0), bottom - MinHeight01);
        if (moveBottom) bottom = Math.Max(Math.Min(bottom + dy, 1), top + MinHeight01);
        return new NormalizedRect(left, top, right - left, bottom - top).Clamp();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent fill keeps the whole surface hit-testable so handles that overhang the image still work.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var b = ImageBounds;
        if (b.IsEmpty) return;
        var r = SelectionRect;
        var dim = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)); // Mac: black 0.57
        dc.DrawRectangle(dim, null, new Rect(b.X, b.Y, b.Width, Math.Max(r.Y - b.Y, 0)));
        dc.DrawRectangle(dim, null, new Rect(b.X, r.Bottom, b.Width, Math.Max(b.Bottom - r.Bottom, 0)));
        dc.DrawRectangle(dim, null, new Rect(b.X, r.Y, Math.Max(r.X - b.X, 0), r.Height));
        dc.DrawRectangle(dim, null, new Rect(r.Right, r.Y, Math.Max(b.Right - r.Right, 0), r.Height));
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)), 1.5), r, 3, 3);
        var third = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), .75);
        for (var i = 1; i <= 2; i++)
        {
            dc.DrawLine(third, new(r.X + r.Width * i / 3, r.Y), new(r.X + r.Width * i / 3, r.Bottom));
            dc.DrawLine(third, new(r.X, r.Y + r.Height * i / 3), new(r.Right, r.Y + r.Height * i / 3));
        }
        var handlePen = new Pen(new SolidColorBrush(AccentColor), 1.5);
        foreach (var p in Handles(r)) dc.DrawRoundedRectangle(Brushes.White, handlePen, new Rect(p.X - 7, p.Y - 7, 14, 14), 3, 3);

        var hint = new FormattedText("拖动边缘或四角裁切 · Esc 取消", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12, new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var box = new Rect(b.X + (b.Width - hint.Width - 22) / 2, b.Bottom - 38, hint.Width + 22, 28);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(194, 0, 0, 0)), null, box, 14, 14);
        dc.DrawText(hint, new Point(box.X + 11, box.Y + (box.Height - hint.Height) / 2));
    }
}
