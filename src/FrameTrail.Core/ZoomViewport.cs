namespace FrameTrail;

/// <summary>
/// Hand-off between two fully framed manual zoom viewports.
/// Mac: RecorderCore/ZoomAnimation.swift ManualZoomViewportTransition.
/// Interpolating the source focus first would cross the framing clamps midway, so both
/// endpoints are framed before they are interpolated.
/// </summary>
public readonly record struct ManualZoomViewportTransition(double FromScale, NormalizedPoint FromFocus, double ToScale, NormalizedPoint ToFocus, double Progress);

/// <summary>
/// One evaluated zoom sample (Mac ZoomSample). Focus is local to the canvas crop, scale is the
/// requested magnification and FramingProgress blends the focus toward the composed 30% framing area.
/// </summary>
public sealed record ZoomSampleState(double Scale, NormalizedPoint Focus, double FramingProgress = 1, ManualZoomViewportTransition? ManualTransition = null)
{
    public static ZoomSampleState Identity { get; } = new(1, new(.5, .5), 0);
}

/// <summary>
/// Card-local viewport transform. The whole screen card (border, rounded corners, shadow, window
/// chrome) is scaled by <see cref="Scale"/> about its own centre and shifted by
/// <see cref="Translation"/> (in units of the unzoomed card size).
/// Mac: RecorderCore/ZoomAnimation.swift ZoomViewportTransform.
/// </summary>
public readonly record struct ZoomViewportTransform(double Scale, NormalizedPoint Translation)
{
    /// <summary>At full zoom edge targets are composed inside this destination area (Mac framingTargetInset).</summary>
    public const double FramingTargetInset = .30;
    public static ZoomViewportTransform Identity => new(1, new(0, 0));

    /// <summary>Maps a card-local normalized point (0..1) through the viewport (Mac applying(to:)).</summary>
    public NormalizedPoint Apply(NormalizedPoint point) => new(.5 + Scale * (point.X - .5) + Translation.X, .5 + Scale * (point.Y - .5) + Translation.Y);

    /// <summary>
    /// Builds the viewport. <paramref name="covering"/> is the canvas in unzoomed card coordinates and must be supplied only
    /// when the base card already covers the whole canvas (Mac keepsCanvasCovered): the translation is then clamped so no
    /// background is exposed.
    /// </summary>
    public static ZoomViewportTransform Make(ZoomSampleState sample, NormalizedRect? covering = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.ManualTransition is { } transition)
        {
            var from = Make(new ZoomSampleState(transition.FromScale, transition.FromFocus, 1), covering);
            var to = Make(new ZoomSampleState(transition.ToScale, transition.ToFocus, 1), covering);
            // Frame both endpoints before interpolating so an edge-to-edge hand-off does not dwell on the clamp.
            var t = Math.Clamp(Finite(transition.Progress, 0), 0, 1);
            return new(from.Scale + (to.Scale - from.Scale) * t,
                new(from.Translation.X + (to.Translation.X - from.Translation.X) * t, from.Translation.Y + (to.Translation.Y - from.Translation.Y) * t));
        }
        var scale = Math.Clamp(Finite(sample.Scale, 1), 1, 6);
        var focus = new NormalizedPoint(Math.Clamp(Finite(sample.Focus.X, .5), 0, 1), Math.Clamp(Finite(sample.Focus.Y, .5), 0, 1));
        var destination = focus.Clamp(FramingTargetInset);
        var progress = Math.Clamp(Finite(sample.FramingProgress, 1), 0, 1);
        var desired = new NormalizedPoint(focus.X + (destination.X - focus.X) * progress, focus.Y + (destination.Y - focus.Y) * progress);
        var scaledPoint = new NormalizedPoint(.5 + scale * (focus.X - .5), .5 + scale * (focus.Y - .5));
        var tx = desired.X - scaledPoint.X;
        var ty = desired.Y - scaledPoint.Y;
        if (covering is { } visible)
        {
            // The transformed source spans [0.5 - scale/2 + t, 0.5 + scale/2 + t]; keep both canvas edges inside it.
            tx = Math.Min(Math.Max(tx, visible.X + visible.Width - .5 - scale / 2), visible.X - .5 + scale / 2);
            ty = Math.Min(Math.Max(ty, visible.Y + visible.Height - .5 - scale / 2), visible.Y - .5 + scale / 2);
        }
        return new(scale, new(tx, ty));
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
