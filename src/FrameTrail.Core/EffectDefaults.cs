namespace FrameTrail;

/// <summary>
/// Defaults for newly created projects and effects, matching the Mac app (RecorderCore ProjectModel / EditorStore / EditorInspectorCommands).
/// Model property initializers are intentionally unchanged because older project files rely on them for missing fields;
/// creation entry points call these factories instead.
/// </summary>
public static class EffectDefaults
{
    /// <summary>Mac CanvasStyle: padding 100 and corner 24 on a 1080 short edge, shadow .28, obsidianGrid background.</summary>
    public const double ProjectPadding = 100d / 1080;
    public const double ProjectCornerRadius = 24d / 1080;
    public const double ProjectShadow = .28;

    /// <summary>Settings for a brand-new project: dark obsidian grid, Mac padding/corner/shadow, Mac cursor defaults.</summary>
    public static FrameTrailSettings NewProjectSettings(int canvasWidth, int canvasHeight) => new()
    {
        Background = "#0F0F0F",
        CanvasWidth = canvasWidth,
        CanvasHeight = canvasHeight,
        Padding = ProjectPadding,
        CornerRadius = ProjectCornerRadius,
        Shadow = ProjectShadow,
        BackgroundStyle = NewObsidianGridBackground(),
        Cursor = NewCursorSettings()
    };

    /// <summary>Mac BackgroundSource.pattern(.obsidianGrid): #0F0F0F with #3D3D3D lines, tile 4.25% of the short edge (about 46 px at 1080).</summary>
    public static BackgroundSettings NewObsidianGridBackground() => new()
    {
        Kind = BackgroundKind.Pattern,
        Color = "#0F0F0F",
        EndColor = "#0F0F0F",
        Pattern = BackgroundPatternKind.Grid,
        PatternSpacing = 45.9,
        PatternStrength = 1,
        PatternColor = "#3D3D3D"
    };

    /// <summary>Mac CursorStyle defaults: size 1.25, idle 1.2 s, ripple click at opacity .8, tilt 2.</summary>
    public static CursorSettings NewCursorSettings() => new()
    {
        Visible = true,
        Style = CursorStyle.FollowRecorded,
        Scale = 1.25,
        HideWhenIdle = false,
        IdleSeconds = 1.2,
        ClickEffectsEnabled = true,
        ClickEffect = ClickEffectStyle.Ripple,
        ClickColor = "#EDA647",
        ClickDuration = CursorSettings.DefaultClickDuration(ClickEffectStyle.Ripple),
        ClickOpacity = .8,
        ClickScale = 1,
        MotionTiltEnabled = true,
        MotionTiltStrength = 2
    };

    /// <summary>
    /// Applies Mac CameraStyle defaults to a freshly created track: square, edge-aligned at (.96, .47), size .29,
    /// border 3, shadow .25, roundness .5, mirrored, scale during zoom .7.
    /// </summary>
    public static CameraTrack ApplyNewCameraDefaults(CameraTrack camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.EdgeAlignedPlacement = true;
        camera.Position = new(.96, .47);
        camera.Size = .29;
        camera.Shape = CameraShape.RoundedRectangle;
        camera.Layout = CameraMotionLayoutKind.RoundedRectangle;
        camera.CornerRadius = .5;
        camera.BorderWidth = 3;
        camera.BorderColor = "#FFFFFF";
        camera.ShadowStrength = .25;
        camera.Mirrored = true;
        camera.ScaleDuringZoom = .7;
        return camera;
    }

    /// <summary>Mac EditorStore.addMosaic / addSticker: start clamped into [0, duration - 0.25], length max(min(preferred, remaining), 0.25).</summary>
    public static (double Start, double Duration) ClampNewClip(double start, double preferredDuration, double totalDuration, double minimumDuration = .25)
    {
        var total = Math.Max(double.IsFinite(totalDuration) ? totalDuration : 0, 0);
        var floor = Math.Min(minimumDuration, total);
        var safeStart = Math.Min(Math.Max(double.IsFinite(start) ? start : 0, 0), Math.Max(total - floor, 0));
        var duration = Math.Max(Math.Min(preferredDuration, total - safeStart), floor);
        return (safeStart, duration);
    }

    /// <summary>Manual zoom: scale 1.6, 0.7 s transitions, spring easing, 1.5 s long. Focus is crop-local; pass the pointer position at the playhead when known.</summary>
    public static ZoomClip NewManualZoom(double start, double mediaDuration, NormalizedPoint? focus = null, double scale = 1.6, double length = 1.5)
    {
        var (safeStart, duration) = ClampNewClip(start, length, mediaDuration);
        return new ZoomClip
        {
            Start = safeStart, End = safeStart + duration, Scale = scale, Focus = (focus ?? new NormalizedPoint(.5, .5)).Clamp(),
            EnterDuration = .7, ExitDuration = .7, Easing = ZoomEasing.Spring, Automatic = false
        };
    }

    /// <summary>Screen 3D: scale 1.2, centred, flat plane, 0.7 s in/return, spring easing (project default).</summary>
    public static ScreenMotionClip NewScreenMotion(double start, double mediaDuration, double length = 3)
    {
        var (safeStart, duration) = ClampNewClip(start, length, mediaDuration);
        return new ScreenMotionClip
        {
            Start = safeStart, Duration = duration, EnterDuration = Math.Min(.7, duration), ReturnDuration = .7, Easing = ZoomEasing.Spring,
            Target = new ScreenMotionState { Position = new(.5, .5), Scale = 1.2, RotationX = 0, RotationY = 0, RotationZ = 0 }
        };
    }

    /// <summary>Mac FocusEffect(linear: true): linear shape, fixed target, size .32, no dimming. Used for 3D focus.</summary>
    public static FocusEffect NewLinearFocusEffect() => new()
    {
        Shape = FocusEffectShape.Linear, Target = FocusEffectTarget.Fixed, Size = .32, Dimming = 0, AngleDegrees = 0
    };

    /// <summary>Redaction / spotlight: strength .55, corner .08, hard cut (None) for blur and pixelate, smooth .30 s edges for spotlight.</summary>
    public static BlurClip NewBlurClip(double start, double mediaDuration, BlurKind kind = BlurKind.Blur, double length = 3)
    {
        var (safeStart, duration) = ClampNewClip(start, length, mediaDuration);
        var spotlight = kind == BlurKind.Spotlight;
        return new BlurClip
        {
            Start = safeStart, Duration = duration, Kind = kind, Strength = .55, CornerRadius = .08, SpotlightDimming = .22,
            TransitionStyle = spotlight ? RegionTransitionStyle.Smooth : RegionTransitionStyle.None,
            EnterDuration = .3, ExitDuration = .3
        };
    }

    /// <summary>
    /// Sticker: width .38 centred; when other stickers are visible at the start, the candidate position farthest from them
    /// with width .28; pop transition (Scale) with the swift curve, enter .7 s / exit .22 s, corner 18/1080, shadow .28, backdrop blur 20.
    /// </summary>
    /// <param name="imageAspect">Pixel width / height of the image.</param>
    /// <param name="canvasAspect">Canvas width / height.</param>
    public static OverlayClip NewOverlay(string mediaPath, double start, double mediaDuration, double imageAspect, double canvasAspect,
        IEnumerable<OverlayClip>? existing = null, double preferredDuration = 3)
    {
        var (safeStart, duration) = ClampNewClip(start, preferredDuration, mediaDuration);
        var active = (existing ?? []).Where(x => safeStart >= x.Start && safeStart < x.End).ToList();
        var candidates = new NormalizedPoint[]
        {
            new(.5, .5), new(.20, .22), new(.80, .22), new(.20, .78), new(.80, .78), new(.5, .20), new(.5, .80), new(.18, .5), new(.82, .5)
        };
        NormalizedPoint Center(OverlayClip c) => new(c.Rect.X + c.Rect.Width / 2, c.Rect.Y + c.Rect.Height / 2);
        double Clearance(NormalizedPoint p) => active.Count == 0 ? (p == candidates[0] ? 1 : 0) : active.Min(c => Math.Sqrt(Math.Pow(p.X - Center(c).X, 2) + Math.Pow(p.Y - Center(c).Y, 2)));
        // Swift's max(by:) keeps the first element among equals.
        var position = candidates[0]; var best = Clearance(position);
        foreach (var candidate in candidates.Skip(1)) { var value = Clearance(candidate); if (value > best) { best = value; position = candidate; } }
        var width = active.Count == 0 ? .38 : .28;
        var safeImage = double.IsFinite(imageAspect) && imageAspect > .01 ? imageAspect : 1;
        var safeCanvas = double.IsFinite(canvasAspect) && canvasAspect > .01 ? canvasAspect : 16d / 9;
        var height = width / safeImage * safeCanvas;
        if (height > .9) { width *= .9 / height; height = .9; }
        var rect = new NormalizedRect(Math.Clamp(position.X - width / 2, 0, 1 - width), Math.Clamp(position.Y - height / 2, 0, 1 - height), width, height);
        return new OverlayClip
        {
            MediaPath = mediaPath, Start = safeStart, Duration = duration, Rect = rect,
            Layer = (existing?.Select(x => x.Layer).DefaultIfEmpty(-1).Max() ?? -1) + 1,
            EnterMotion = OverlayMotion.Scale, ExitMotion = OverlayMotion.Scale, AnimationCurve = ElementMotionCurve.Swift,
            EnterDuration = .7, ExitDuration = .22, CornerRadius = 18d / 1080, ShadowOpacity = .28, ShadowRadius = 24, ShadowOffsetY = 10, BackdropBlur = 20
        };
    }
}
