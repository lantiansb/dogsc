namespace FrameTrail;

/// <summary>Continuous authored camera layout. These values are resolved to geometry before rendering.</summary>
public enum CameraMotionLayoutKind
{
    RoundedRectangle,
    Circle,
    Horizontal,
    Vertical,
    Original,
    Fullscreen
}

/// <summary>
/// Continuous camera pose in canvas coordinates. Size is a fraction of the canvas short edge;
/// aspect ratio is the camera frame's pixel width divided by its pixel height.
/// </summary>
public readonly record struct CameraMotionPose(
    NormalizedPoint Position,
    double Size,
    double AspectRatio,
    double CornerFraction,
    double FullscreenProgress,
    double Opacity)
{
    /// <summary>Legacy-only fixed radius in short-edge units; null for continuous-layout authored states.</summary>
    public double? LegacyCornerRadius { get; init; }

    /// <summary>
    /// True when Position addresses the camera by edge alignment (x = position * (canvas - camera), Mac CompositionScene.evaluateCamera)
    /// so the camera never leaves the canvas; false keeps the older centre-based placement of projects created before this option.
    /// </summary>
    public bool EdgeAligned { get; init; }

    /// <summary>Resolves the pose into a normalized canvas rectangle without switching a discrete shape mid-transition.</summary>
    public NormalizedRect ToNormalizedRect(double canvasAspectRatio)
    {
        var aspect = SafePositive(canvasAspectRatio, 1);
        var frameAspect = SafePositive(AspectRatio, 1);
        var size = Math.Max(double.IsFinite(Size) ? Size : 0, 0);
        var shortEdgeHeightFraction = Math.Min(1, aspect);
        var shortEdgeWidthFraction = Math.Min(1, 1 / aspect);
        var width = size * frameAspect * shortEdgeWidthFraction;
        var height = size * shortEdgeHeightFraction;
        var fit = Math.Min(1, Math.Min(1.2 / Math.Max(width, 1e-9), 1.2 / Math.Max(height, 1e-9)));
        width *= fit;
        height *= fit;
        var px = Math.Clamp(double.IsFinite(Position.X) ? Position.X : .5, 0, 1);
        var py = Math.Clamp(double.IsFinite(Position.Y) ? Position.Y : .5, 0, 1);
        var x = EdgeAligned ? px * (1 - width) : px - width * .5;
        var y = EdgeAligned ? py * (1 - height) : py - height * .5;
        var shaped = new NormalizedRect(x, y, width, height);
        var fullscreen = Math.Clamp(double.IsFinite(FullscreenProgress) ? FullscreenProgress : 0, 0, 1);
        return new NormalizedRect(
            Mix(shaped.X, 0, fullscreen),
            Mix(shaped.Y, 0, fullscreen),
            Mix(shaped.Width, 1, fullscreen),
            Mix(shaped.Height, 1, fullscreen));
    }

    /// <summary>Returns the corner radius in the renderer's normalized short-edge units.</summary>
    public double ToNormalizedCornerRadius(double canvasAspectRatio)
    {
        if (LegacyCornerRadius is { } legacyRadius && FullscreenProgress <= 0)
            return Math.Clamp(double.IsFinite(legacyRadius) ? legacyRadius : 0, 0, .5);
        var rect = ToNormalizedRect(canvasAspectRatio);
        var aspect = SafePositive(canvasAspectRatio, 1);
        var minDimensionInShortEdgeUnits = Math.Min(rect.Width * Math.Max(aspect, 1), rect.Height * Math.Max(1 / aspect, 1));
        return minDimensionInShortEdgeUnits * Math.Clamp(double.IsFinite(CornerFraction) ? CornerFraction : 0, 0, .5);
    }

    private static double Mix(double from, double to, double t) => from + (to - from) * t;
    private static double SafePositive(double value, double fallback) => double.IsFinite(value) && value > .01 ? value : fallback;
}

/// <summary>Creates and interpolates resolved camera states independently of timeline/evaluator integration.</summary>
public static class CameraMotionEvaluator
{
    /// <summary>Resolves legacy track values. A missing camera remains absent, matching the old optional-camera path.</summary>
    public static CameraMotionPose? Base(
        CameraTrack? camera,
        CameraMotionLayoutKind? layout = null,
        double cameraAspectRatio = 1,
        double canvasAspectRatio = 1)
    {
        if (camera is null) return null;
        var authoredSize = double.IsFinite(camera.Size) ? Math.Max(camera.Size, 0) : 0;
        // Old projects express size as a fraction of canvas height. The continuous layout model
        // measures size against the canvas short edge, so convert only the legacy null-layout path.
        var size = layout is null
            ? authoredSize / Math.Min(1, SafePositive(canvasAspectRatio, 1))
            : authoredSize;
        var radius = Math.Clamp(double.IsFinite(camera.CornerRadius) ? camera.CornerRadius : 0, 0, layout is null ? .5 : 1);
        var legacyLayout = camera.Shape == CameraShape.Circle ? CameraMotionLayoutKind.Circle : CameraMotionLayoutKind.RoundedRectangle;
        var selectedLayout = layout ?? legacyLayout;
        // Explicit layouts store the source roundness value (0..1). Legacy projects
        // instead store an absolute short-edge radius and derive a fraction only
        // for interpolation while preserving that absolute radius below.
        var legacyRoundness = layout is null
            ? size > 1e-9 ? Math.Clamp(radius * 2 / size, 0, 1) : 0
            : Unit(radius);
        var resolved = Resolve(selectedLayout, legacyRoundness, cameraAspectRatio, canvasAspectRatio);
        // A null layout is the old CameraTrack path: preserve its absolute short-edge radius exactly.
        var cornerFraction = layout is null && camera.Shape != CameraShape.Circle && size > 1e-9
            ? Math.Clamp(radius / size, 0, .5)
            : resolved.CornerFraction;
        return new CameraMotionPose(
            camera.Position,
            size,
            resolved.AspectRatio,
            cornerFraction,
            resolved.FullscreenProgress,
            Math.Clamp(double.IsFinite(camera.Opacity) ? camera.Opacity : 1, 0, 1))
        {
            LegacyCornerRadius = layout is null && camera.Shape != CameraShape.Circle ? radius : null,
            EdgeAligned = camera.EdgeAlignedPlacement
        };
    }

    /// <summary>
    /// Resolves an explicit transition target. Camera aspect ratio must describe the cropped source;
    /// size remains relative to the canvas short edge, as in the upstream composition evaluator.
    /// </summary>
    public static CameraMotionPose? Target(
        CameraTrack? camera,
        NormalizedPoint position,
        double size,
        CameraMotionLayoutKind? layout,
        double roundness,
        double opacity,
        double cameraAspectRatio,
        double canvasAspectRatio)
    {
        if (camera is null) return null;
        var selectedLayout = layout ?? (camera.Shape == CameraShape.Circle ? CameraMotionLayoutKind.Circle : CameraMotionLayoutKind.RoundedRectangle);
        var resolved = Resolve(selectedLayout, Unit(roundness), cameraAspectRatio, canvasAspectRatio);
        var corner = layout is null && camera.Shape != CameraShape.Circle && size > 1e-9
            ? Math.Clamp((double.IsFinite(camera.CornerRadius) ? camera.CornerRadius : 0) / size, 0, .5)
            : resolved.CornerFraction;
        return new CameraMotionPose(
            position,
            Math.Max(double.IsFinite(size) ? size : 0, 0) / (layout is null ? Math.Min(1, SafePositive(canvasAspectRatio, 1)) : 1),
            resolved.AspectRatio,
            corner,
            resolved.FullscreenProgress,
            Unit(opacity))
        {
            LegacyCornerRadius = layout is null && camera.Shape != CameraShape.Circle
                ? Math.Clamp(double.IsFinite(camera.CornerRadius) ? camera.CornerRadius : 0, 0, .5)
                : null,
            EdgeAligned = camera.EdgeAlignedPlacement
        };
    }

    /// <summary>Linearly interpolates every continuous component, including shape aspect, roundness, and fullscreen geometry.</summary>
    public static CameraMotionPose? Interpolate(CameraMotionPose? from, CameraMotionPose? to, double progress)
    {
        if (from is not { } start || to is not { } target) return null;
        var t = Unit(progress);
        // Match upstream transition semantics: hidden targets fade at the inherited geometry;
        // a hidden source fades in at the target geometry rather than moving while invisible.
        if (target.Opacity < .01)
            return start with { Opacity = Mix(start.Opacity, 0, t) };
        if (start.Opacity < .01)
            return target with { Opacity = Mix(0, target.Opacity, t) };
        double? legacyCornerRadius = start.LegacyCornerRadius is { } startRadius && target.LegacyCornerRadius is { } targetRadius
            ? Mix(startRadius, targetRadius, t)
            : null;
        return new CameraMotionPose(
            new NormalizedPoint(Mix(start.Position.X, target.Position.X, t), Mix(start.Position.Y, target.Position.Y, t)),
            Mix(start.Size, target.Size, t),
            Mix(start.AspectRatio, target.AspectRatio, t),
            Mix(start.CornerFraction, target.CornerFraction, t),
            Mix(start.FullscreenProgress, target.FullscreenProgress, t),
            Mix(start.Opacity, target.Opacity, t))
        {
            LegacyCornerRadius = legacyCornerRadius,
            EdgeAligned = start.EdgeAligned
        };
    }

    private static (double AspectRatio, double CornerFraction, double FullscreenProgress) Resolve(
        CameraMotionLayoutKind layout, double roundness, double cameraAspectRatio, double canvasAspectRatio)
    {
        var canvasAspect = SafePositive(canvasAspectRatio, 1);
        var sourceAspect = SafePositive(cameraAspectRatio, 1);
        return layout switch
        {
            CameraMotionLayoutKind.Circle => (1, .5, 0),
            CameraMotionLayoutKind.Horizontal => (1.5, .5 * Unit(roundness), 0),
            CameraMotionLayoutKind.Vertical => (.72, .5 * Unit(roundness), 0),
            CameraMotionLayoutKind.Original => (sourceAspect, .5 * Unit(roundness), 0),
            CameraMotionLayoutKind.Fullscreen => (canvasAspect, 0, 1),
            _ => (1, .5 * Unit(roundness), 0)
        };
    }

    private static double Unit(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
    private static double Mix(double from, double to, double progress) => from + (to - from) * progress;
    private static double SafePositive(double value, double fallback) => double.IsFinite(value) && value > .01 ? value : fallback;
}

/// <summary>
/// Converts between a camera's stored Position and the rectangle it occupies, for either placement model.
/// Editors that drag the camera rectangle must use these instead of assuming a centre-based position.
/// </summary>
public static class CameraPlacement
{
    /// <summary>Normalized top-left rectangle for a stored position (edge-aligned: x = position * (1 - width)).</summary>
    public static NormalizedRect RectFromPosition(NormalizedPoint position, double width, double height, bool edgeAligned)
    {
        var px = Math.Clamp(double.IsFinite(position.X) ? position.X : .5, 0, 1);
        var py = Math.Clamp(double.IsFinite(position.Y) ? position.Y : .5, 0, 1);
        return edgeAligned ? new(px * (1 - width), py * (1 - height), width, height) : new(px - width / 2, py - height / 2, width, height);
    }

    /// <summary>Inverse of <see cref="RectFromPosition"/>; the result is clamped to the 0..1 position range.</summary>
    public static NormalizedPoint PositionFromRect(NormalizedRect rect, bool edgeAligned)
    {
        if (!edgeAligned) return new NormalizedPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2).Clamp();
        var slackX = 1 - rect.Width; var slackY = 1 - rect.Height;
        return new NormalizedPoint(Math.Abs(slackX) > 1e-9 ? rect.X / slackX : .5, Math.Abs(slackY) > 1e-9 ? rect.Y / slackY : .5).Clamp();
    }
}
