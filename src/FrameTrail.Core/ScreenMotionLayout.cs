namespace FrameTrail;

/// <summary>
/// How a screen-motion state's position is interpreted (Mac ScreenMotionFraming).
/// Placement is the project's ordinary card placement; Focus treats the position as a point of interest inside the screen.
/// </summary>
public enum ScreenMotionFraming { Placement, Focus }

/// <summary>
/// Resolved rect-space blend between two screen states plus the projection pivot.
/// The renderer converts both endpoints to pixel offsets and mixes those (Mac interpolateLayout), so the zoomed anchor
/// stays pinned on the segment between the two end rects.
/// </summary>
public sealed record ScreenMotionBlend(ScreenMotionState From, ScreenMotionState To, ScreenMotionFraming FromFraming, ScreenMotionFraming ToFraming, double Progress, NormalizedPoint ProjectionAnchor);

/// <summary>
/// Pixel-space measurements needed to place the screen card (Mac ScreenAnchorViewport).
/// Decoration values are the chrome that hangs outside the recorded content at card scale one.
/// </summary>
public readonly record struct ScreenAnchorViewport(double CanvasWidth, double CanvasHeight, double FittedWidth, double FittedHeight,
    double DecorationTop = 0, double DecorationRight = 0, double DecorationBottom = 0, double DecorationLeft = 0, double ScaleMultiplier = 1);

/// <summary>Port of ScreenMotion.swift directOffset / interpolateLayout, in output pixels.</summary>
public static class ScreenMotionLayout
{
    /// <summary>Card scale actually drawn for a state (state scale times the project's frame scale).</summary>
    public static double CardScale(ScreenMotionState state, ScreenAnchorViewport viewport)
    {
        var scale = (double.IsFinite(state.Scale) ? state.Scale : 1) * (double.IsFinite(viewport.ScaleMultiplier) ? viewport.ScaleMultiplier : 1);
        return Math.Clamp(scale, .1, 8);
    }

    /// <summary>Offset of the content centre from the canvas centre for one state.</summary>
    public static (double X, double Y) DirectOffset(ScreenMotionState state, ScreenAnchorViewport viewport, ScreenMotionFraming framing)
    {
        var cardScale = CardScale(state, viewport);
        var contentWidth = viewport.FittedWidth * cardScale;
        var contentHeight = viewport.FittedHeight * cardScale;
        var left = viewport.DecorationLeft * cardScale;
        var right = viewport.DecorationRight * cardScale;
        var top = viewport.DecorationTop * cardScale;
        var bottom = viewport.DecorationBottom * cardScale;
        var px = double.IsFinite(state.Position.X) ? state.Position.X : .5;
        var py = double.IsFinite(state.Position.Y) ? state.Position.Y : .5;
        double centerX, centerY;
        if (framing == ScreenMotionFraming.Placement)
        {
            // Solve the content centre from the complete asymmetric card bounds so edge values keep chrome inside the canvas.
            var decoratedWidth = contentWidth + left + right;
            var decoratedHeight = contentHeight + top + bottom;
            centerX = px * (viewport.CanvasWidth - decoratedWidth) + contentWidth / 2 + left;
            centerY = py * (viewport.CanvasHeight - decoratedHeight) + contentHeight / 2 + top;
        }
        else
        {
            var focusX = Math.Clamp(px, 0, 1);
            var focusY = Math.Clamp(py, 0, 1);
            // A target is a point of interest, not an instruction to pin the card to a canvas corner. As the card grows
            // the selected point moves toward the centre (about 31%/69% at 3x and above, Mac ScreenMotion.swift:582-591).
            var recenter = Math.Clamp((cardScale - 1) / 2, 0, 1) * .62;
            var boundedX = left + focusX * Math.Max(viewport.CanvasWidth - left - right, 0);
            var boundedY = top + focusY * Math.Max(viewport.CanvasHeight - top - bottom, 0);
            var targetX = Mix(boundedX, (viewport.CanvasWidth + left - right) / 2, recenter);
            var targetY = Mix(boundedY, (viewport.CanvasHeight + top - bottom) / 2, recenter);
            centerX = targetX + (.5 - focusX) * contentWidth;
            centerY = targetY + (.5 - focusY) * contentHeight;
        }
        return (centerX - viewport.CanvasWidth / 2, centerY - viewport.CanvasHeight / 2);
    }

    /// <summary>Mixes the pixel offsets of both blend endpoints with the blend progress.</summary>
    public static (double X, double Y) BlendOffset(ScreenMotionBlend blend, ScreenAnchorViewport viewport)
    {
        var from = DirectOffset(blend.From, viewport, blend.FromFraming);
        var to = DirectOffset(blend.To, viewport, blend.ToFraming);
        var t = Math.Clamp(double.IsFinite(blend.Progress) ? blend.Progress : 0, 0, 1);
        return (Mix(from.X, to.X, t), Mix(from.Y, to.Y, t));
    }

    /// <summary>Shortest-path angle interpolation (Mac mixAngle).</summary>
    public static double MixAngle(double from, double to, double t)
    {
        var delta = (to - from) % 360;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        return from + delta * t;
    }

    public static double Mix(double from, double to, double t) => from + (to - from) * t;
}
