using FrameTrail;

internal static class CanvasSnapMathProbe
{
    private static void Near(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"{message}: expected {expected}, got {actual}");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static void Run()
    {
        // 9px over a 900px travel is 1%; the threshold is clamped to the Mac limits.
        Near(CanvasSnapMath.NormalizedThreshold(900), .01, "9px over 900px");
        Near(CanvasSnapMath.NormalizedThreshold(10), .08, "short travel clamps to the upper limit");
        Near(CanvasSnapMath.NormalizedThreshold(100000), .003, "long travel clamps to the lower limit");
        Near(CanvasSnapMath.NormalizedThreshold(0), .08, "zero travel is treated as 1px");

        var snapped = CanvasSnapMath.Snap(new(.505, .2), [.5], [.5], .01, .01);
        Near(snapped.Point.X, .5, "x snaps to centre"); Near(snapped.Point.Y, .2, "y keeps its value");
        Check(snapped.GuideX == .5 && snapped.GuideY is null, "only the snapped axis reports a guide");
        var first = CanvasSnapMath.Snap(new(.02, .5), [0, .5, 1], [0, .5, 1], .03, .01);
        Near(first.Point.X, 0, "x snaps to the left anchor"); Check(first.GuideX == 0 && first.GuideY == .5, "guides for both axes");

        // Centre move: centre snap, edge-touch snap, otherwise clamped inside the canvas.
        var center = CanvasSnapMath.SnapCenterInside(new(.51, .49), .1, .1, .02, .02);
        Near(center.Point.X, .5, "centre snap x"); Near(center.Point.Y, .5, "centre snap y");
        var edge = CanvasSnapMath.SnapCenterInside(new(.11, .9), .1, .1, .02, .02);
        Near(edge.Point.X, .1, "left edge touches canvas"); Check(edge.GuideX == 0, "left edge guide is 0");
        Near(edge.Point.Y, .9, "bottom edge touches canvas"); Check(edge.GuideY == 1, "bottom edge guide is 1");
        var inside = CanvasSnapMath.SnapCenterInside(new(-.4, 1.7), .1, .1, .005, .005);
        Near(inside.Point.X, .1, "object is kept inside on the left"); Near(inside.Point.Y, .9, "object is kept inside at the bottom");
        Check(inside.GuideX is null && inside.GuideY is null, "no guide while merely clamped");

        // Rect move.
        var move = CanvasSnapMath.SnapRectMove(new(.2995, .0005, .4, .2), .01, .01);
        Near(move.Rect.X, .3, "rect centre snaps to 0.5"); Near(move.Rect.Y, 0, "rect top snaps to 0");
        Check(move.GuideX == .5 && move.GuideY == 0, "rect move guides");

        // Rect resize keeps the minimum size and snaps the dragged edge.
        var resize = CanvasSnapMath.SnapRectResize(new(.2, .2, .295, .25), 2, .01, .01);
        Near(resize.Rect.Width, .3, "right edge snaps to 0.5"); Check(resize.GuideX == .5 && resize.GuideY is null, "resize guides");
        var tiny = CanvasSnapMath.SnapRectResize(new(.4, .4, .001, .5), 2, .005, .005);
        Near(tiny.Rect.Width, .02, "minimum width"); Near(tiny.Rect.X, .4, "anchored edge stays");
        var tinyTopLeft = CanvasSnapMath.SnapRectResize(new(.499, .3, .001, .4), 0, 0, 0);
        Near(tinyTopLeft.Rect.Width, .02, "minimum width when dragging the left edge"); Near(tinyTopLeft.Rect.X + tinyTopLeft.Rect.Width, .5, "right edge stays");

        // Rotation snapping.
        Near(CanvasSnapMath.SnapRotationDegrees(2.5), 0, "snaps to 0 within 3 degrees");
        Near(CanvasSnapMath.SnapRotationDegrees(87.2), 90, "snaps to 90");
        Near(CanvasSnapMath.SnapRotationDegrees(45), 45, "keeps free angles");
        Near(CanvasSnapMath.SnapRotationDegrees(358), 0, "wraps 358 to 0");
        Near(CanvasSnapMath.SnapRotationDegrees(-179), -180, "snaps near -180");
        Near(CanvasSnapMath.NormalizeDegrees(190), -170, "wraps 190");
        Check(CanvasSnapMath.IsRotationSnapped(90) && !CanvasSnapMath.IsRotationSnapped(45), "snapped detection");
    }
}
