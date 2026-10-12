namespace FrameTrail;

/// <summary>
/// Canvas drag snapping. Port of Mac CanvasSnapMath (EditorCanvasInteractionSupport.swift) plus the
/// snapping helpers that live in EditorCanvasOverlayInteraction.swift (snappedOverlayCenter,
/// snappedMosaicRect, snappedMosaicResize, snappedStickerRotationDegrees).
/// All positions are canvas-normalized (0..1).
/// </summary>
public static class CanvasSnapMath
{
    /// <summary>Snap distance in screen pixels (Mac CanvasSnapMath.targetPixelDistance).</summary>
    public const double TargetPixelDistance = 9;

    public readonly record struct SnapResult(NormalizedPoint Point, double? GuideX, double? GuideY);
    public readonly record struct RectSnapResult(NormalizedRect Rect, double? GuideX, double? GuideY);

    /// <summary>Pixel snap distance converted to a normalized threshold along a travel of <paramref name="travelPixels"/>.</summary>
    public static double NormalizedThreshold(double travelPixels, double pixelDistance = TargetPixelDistance,
        double minimum = .003, double maximum = .08)
    {
        var travel = Math.Max(Math.Abs(double.IsFinite(travelPixels) ? travelPixels : 0), 1);
        return Math.Min(Math.Max(pixelDistance / travel, minimum), maximum);
    }

    /// <summary>Snaps each axis to the first anchor within its threshold (Mac CanvasSnapMath.snapped).</summary>
    public static SnapResult Snap(NormalizedPoint point, IReadOnlyList<double> anchorsX, IReadOnlyList<double> anchorsY,
        double thresholdX, double thresholdY)
    {
        var (x, guideX) = SnapAxis(point.X, anchorsX, thresholdX);
        var (y, guideY) = SnapAxis(point.Y, anchorsY, thresholdY);
        return new(new(x, y), guideX, guideY);
    }

    private static (double Value, double? Guide) SnapAxis(double value, IReadOnlyList<double> anchors, double threshold)
    {
        foreach (var anchor in anchors)
            if (Math.Abs(value - anchor) <= threshold) return (anchor, anchor);
        return (value, null);
    }

    /// <summary>
    /// Moves an object's centre: snaps to the canvas centre or to the position where the object touches an edge,
    /// otherwise keeps the whole object inside the canvas (Mac snappedOverlayCenter).
    /// </summary>
    public static SnapResult SnapCenterInside(NormalizedPoint proposed, double halfWidth, double halfHeight,
        double thresholdX, double thresholdY)
    {
        var (x, guideX) = SnapCenterAxis(proposed.X, halfWidth, thresholdX);
        var (y, guideY) = SnapCenterAxis(proposed.Y, halfHeight, thresholdY);
        return new(new(x, y), guideX, guideY);
    }

    private static (double Value, double? Guide) SnapCenterAxis(double value, double half, double threshold)
    {
        half = Math.Clamp(double.IsFinite(half) ? half : 0, 0, .5);
        if (Math.Abs(value - .5) <= threshold) return (.5, .5);
        if (Math.Abs(value - half) <= threshold) return (half, 0);
        if (Math.Abs(value - (1 - half)) <= threshold) return (1 - half, 1);
        return (Math.Clamp(value, half, 1 - half), null);
    }

    /// <summary>Moves a rectangle: snaps its centre to 0.5 or an edge to 0/1 (Mac snappedMosaicRect).</summary>
    public static RectSnapResult SnapRectMove(NormalizedRect proposed, double thresholdX, double thresholdY)
    {
        var rect = proposed.Clamp();
        double? guideX = null, guideY = null;
        var centerX = rect.X + rect.Width / 2;
        if (Math.Abs(centerX - .5) <= thresholdX) { rect = rect with { X = rect.X + .5 - centerX }; guideX = .5; }
        else if (Math.Abs(rect.X) <= thresholdX) { rect = rect with { X = 0 }; guideX = 0; }
        else if (Math.Abs(rect.X + rect.Width - 1) <= thresholdX) { rect = rect with { X = 1 - rect.Width }; guideX = 1; }
        var centerY = rect.Y + rect.Height / 2;
        if (Math.Abs(centerY - .5) <= thresholdY) { rect = rect with { Y = rect.Y + .5 - centerY }; guideY = .5; }
        else if (Math.Abs(rect.Y) <= thresholdY) { rect = rect with { Y = 0 }; guideY = 0; }
        else if (Math.Abs(rect.Y + rect.Height - 1) <= thresholdY) { rect = rect with { Y = 1 - rect.Height }; guideY = 1; }
        return new(rect.Clamp(), guideX, guideY);
    }

    /// <summary>
    /// Corner-resizes a rectangle: the dragged edges snap to 0, 0.5 or 1 and the rectangle keeps at least
    /// <paramref name="minimum"/> size (Mac snappedMosaicResize). Corner: 0 TL, 1 TR, 2 BR, 3 BL.
    /// </summary>
    public static RectSnapResult SnapRectResize(NormalizedRect proposed, int corner, double thresholdX, double thresholdY,
        double minimum = .02)
    {
        var left = proposed.X; var top = proposed.Y;
        var right = proposed.X + proposed.Width; var bottom = proposed.Y + proposed.Height;
        var movesLeft = corner is 0 or 3; var movesTop = corner is 0 or 1;
        double? guideX = null, guideY = null;
        double[] anchors = [0, .5, 1];
        var xEdge = movesLeft ? left : right; var yEdge = movesTop ? top : bottom;
        foreach (var anchor in anchors)
            if (Math.Abs(xEdge - anchor) <= thresholdX) { if (movesLeft) left = anchor; else right = anchor; guideX = anchor; break; }
        foreach (var anchor in anchors)
            if (Math.Abs(yEdge - anchor) <= thresholdY) { if (movesTop) top = anchor; else bottom = anchor; guideY = anchor; break; }
        if (right - left < minimum) { if (movesLeft) left = right - minimum; else right = left + minimum; }
        if (bottom - top < minimum) { if (movesTop) top = bottom - minimum; else bottom = top + minimum; }
        return new(new NormalizedRect(left, top, right - left, bottom - top).Clamp(), guideX, guideY);
    }

    /// <summary>Wraps degrees into -180..180.</summary>
    public static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        var value = degrees % 360;
        if (value > 180) value -= 360;
        if (value < -180) value += 360;
        return value;
    }

    /// <summary>Snaps a rotation to -180/-90/0/90/180 within <paramref name="tolerance"/> degrees (Mac snappedStickerRotationDegrees).</summary>
    public static double SnapRotationDegrees(double proposed, double tolerance = 3)
    {
        var normalized = NormalizeDegrees(proposed);
        double[] anchors = [-180, -90, 0, 90, 180];
        var nearest = anchors.MinBy(a => Math.Abs(normalized - a));
        return Math.Abs(normalized - nearest) <= tolerance ? nearest : normalized;
    }

    /// <summary>True when the rotation sits exactly on one of the snap anchors.</summary>
    public static bool IsRotationSnapped(double degrees) =>
        new[] { -180d, -90, 0, 90, 180 }.Any(a => Math.Abs(degrees - a) < .001);
}
