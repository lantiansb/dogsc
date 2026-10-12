using FrameTrail;

/// <summary>
/// Regression for the Mac-parity core model: zoom viewport framing, screen-motion placement, output-clock transitions,
/// edge-aligned camera placement, new-effect defaults, marker maintenance and pointer re-seeding at cuts.
/// Expected values are derived by hand from RecorderCore ZoomAnimation.swift, ScreenMotion.swift and CompositionScene.swift.
/// </summary>
internal static class MacParityCoreProbe
{
    public static void Run()
    {
        ZoomViewport();
        ScreenMotionPlacement();
        TransitionsFollowOutputClock();
        EdgeAlignedCamera();
        NewEffectDefaults();
        MarkersMoveToCutPoint();
        PointerNoneAndCutReseed();
        Console.WriteLine("通过：Mac对齐核心模型（缩放视口、屏幕运动位置、输出时钟过渡、贴边摄像头、新建默认值、剪切标记、指针重播种）");
    }

    private static void ZoomViewport()
    {
        // Focus at the centre needs no travel.
        var centred = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.5, .5), 1));
        Near(centred.Translation.X, 0, "centred focus has no translation"); Near(centred.Scale, 2, "scale passes through");

        // Edge focus is composed into the 30% framing inset: Apply(focus) lands on 0.3, not on the canvas edge.
        var edge = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.1, .1), 1));
        var landed = edge.Apply(new(.1, .1));
        Near(landed.X, .3, "edge focus is composed to the framing inset"); Near(landed.Y, .3, "vertical framing inset");

        // Partial framing progress blends the destination.
        var half = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.1, .1), .5));
        Near(half.Apply(new(.1, .1)).X, .2, "framing progress blends the destination");

        // A card that already covers the canvas is clamped so no background is exposed.
        var covering = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.1, .1), 1), new NormalizedRect(0, 0, 1, 1));
        Near(covering.Translation.X, .5, "covering card clamps the translation to the canvas edge");
        Near(covering.Apply(new(0, 0)).X, 0, "covered canvas edge stays pinned to the card edge");
        var free = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.1, .1), 1));
        Check(free.Translation.X > 0 && Math.Abs(free.Translation.X - .6) < 1e-9, "uncovered card travels past the canvas edge and exposes the background");

        // Manual hand-off interpolates the framed viewports, not the focus.
        var from = ZoomViewportTransform.Make(new ZoomSampleState(1.5, new(.1, .5), 1));
        var to = ZoomViewportTransform.Make(new ZoomSampleState(2.5, new(.9, .5), 1));
        var middle = ZoomViewportTransform.Make(new ZoomSampleState(2, new(.5, .5), 1,
            new ManualZoomViewportTransition(1.5, new(.1, .5), 2.5, new(.9, .5), .5)));
        Near(middle.Scale, 2, "hand-off mixes scale"); Near(middle.Translation.X, (from.Translation.X + to.Translation.X) / 2, "hand-off mixes the framed translations");
    }

    private static void ScreenMotionPlacement()
    {
        var viewport = new ScreenAnchorViewport(1000, 500, 800, 400);
        var right = ScreenMotionLayout.DirectOffset(new ScreenMotionState { Position = new(1, .5), Scale = 1 }, viewport, ScreenMotionFraming.Placement);
        Near(right.X, 100, "placement position 1 sits flush against the right edge");
        var left = ScreenMotionLayout.DirectOffset(new ScreenMotionState { Position = new(0, .5), Scale = 1 }, viewport, ScreenMotionFraming.Placement);
        Near(left.X, -100, "placement position 0 sits flush against the left edge");
        // Cards smaller than the canvas still reach the edge (slack = canvas - card).
        var small = ScreenMotionLayout.DirectOffset(new ScreenMotionState { Position = new(1, .5), Scale = .5 }, viewport, ScreenMotionFraming.Placement);
        Near(small.X, 300, "a half-size card can be docked to the edge");

        // Focus framing: at scale 3 the point of interest is pulled toward the centre by 62%.
        var focus = ScreenMotionLayout.DirectOffset(new ScreenMotionState { Position = new(1, .5), Scale = 3 }, viewport, ScreenMotionFraming.Focus);
        var recentred = ScreenMotionLayout.Mix(1000, 500, .62);
        Near(focus.X, recentred + (.5 - 1) * 800 * 3 - 500, "focus framing recentres by 0.62 at three times and above");

        // Evaluated scene: base placement is reported through the blend, 3D pivot is the authored target.
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 6 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 6 }] }, Settings = new() { AutoZoom = false },
            ScreenMotions = [new() { Start = 1, Duration = 3, EnterDuration = .5, ReturnDuration = .5, Easing = ZoomEasing.Linear,
                Target = new() { Position = new(.8, .3), Scale = 1.2, RotationY = 20 } }]
        };
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(2);
        Check(scene.ScreenBlend is { } blend && Math.Abs(blend.ProjectionAnchor.X - .8) < 1e-9 && Math.Abs(blend.ProjectionAnchor.Y - .3) < 1e-9
            && blend.ToFraming == ScreenMotionFraming.Focus, "held screen motion pivots on its authored point of interest");
    }

    private static void TransitionsFollowOutputClock()
    {
        // A 4x segment: the authored 0.7 s output transition must still take 0.7 s of output time.
        var fast = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 8 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 8, PlaybackRate = 4 }] }, Settings = new() { AutoZoom = false },
            ZoomClips = [new() { Start = 0, End = 6, Scale = 2, Focus = new(.5, .5), EnterDuration = .7, ExitDuration = .7, Easing = ZoomEasing.Linear }],
            ScreenMotions = [new() { Start = 0, Duration = 6, EnterDuration = .7, ReturnDuration = .7, Easing = ZoomEasing.Linear, Target = new() { Position = new(.9, .5), Scale = 1.4 } }]
        };
        var evaluator = new PreparedFrameSceneEvaluator(fast);
        Near(evaluator.Evaluate(.35).Zoom, 1.5, "0.7 s zoom entry is half done after 0.35 s of output in a 4x segment");
        Near(evaluator.Evaluate(.7).Zoom, 2, "zoom entry completes after 0.7 s of output");
        Near(evaluator.Evaluate(.35).ScreenMotion!.Scale, 1.2, "screen-motion entry also follows the output clock");

        var normal = Clone(fast);
        normal.Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 8 }] };
        Near(new PreparedFrameSceneEvaluator(normal).Evaluate(.35).Zoom, 1.5, "unchanged at 1x");

        // Adjacent manual zoom hand-off: the sample carries the framed-viewport transition.
        var chained = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 6 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 6 }] }, Settings = new() { AutoZoom = false },
            ZoomClips =
            [
                new() { Start = 1, End = 2, Scale = 1.5, Focus = new(.1, .5), EnterDuration = .4, ExitDuration = 0, Easing = ZoomEasing.Linear },
                new() { Start = 2, End = 3, Scale = 2.5, Focus = new(.9, .5), EnterDuration = .5, ExitDuration = .4, Easing = ZoomEasing.Linear }
            ]
        };
        var handoff = new PreparedFrameSceneEvaluator(chained).Evaluate(2.25);
        Check(handoff.ZoomSample?.ManualTransition is { } transition && Math.Abs(transition.Progress - .5) < 1e-9 && transition.FromScale == 1.5 && transition.ToScale == 2.5,
            "adjacent manual zoom hands off between framed viewports");
        var view = ZoomViewportTransform.Make(handoff.ZoomSample!);
        var a = ZoomViewportTransform.Make(new ZoomSampleState(1.5, new(.1, .5), 1)); var b = ZoomViewportTransform.Make(new ZoomSampleState(2.5, new(.9, .5), 1));
        Near(view.Translation.X, (a.Translation.X + b.Translation.X) / 2, "hand-off translation is the mean of the two framed viewports");
    }

    private static void EdgeAlignedCamera()
    {
        var edge = CameraPlacement.RectFromPosition(new(1, 1), .2, .3, true);
        Near(edge.X + edge.Width, 1, "edge-aligned camera at position 1 touches the right edge"); Near(edge.Y + edge.Height, 1, "and the bottom edge");
        var centred = CameraPlacement.RectFromPosition(new(.5, .5), .2, .3, false);
        Near(centred.X, .4, "legacy placement stays centre based"); Near(centred.Y, .35, "legacy vertical centre");
        var round = CameraPlacement.PositionFromRect(edge, true);
        Near(round.X, 1, "inverse mapping returns the stored position"); Near(round.Y, 1, "inverse mapping vertical");
        var legacyRound = CameraPlacement.PositionFromRect(centred, false);
        Near(legacyRound.X, .5, "legacy inverse x"); Near(legacyRound.Y, .5, "legacy inverse y");

        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 4 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 4 }] }, Settings = new() { AutoZoom = false },
            Camera = EffectDefaults.ApplyNewCameraDefaults(new CameraTrack { MediaPath = "camera.mp4", Duration = 4 })
        };
        project.Camera!.Position = new(1, 1);
        var rect = new PreparedFrameSceneEvaluator(project).Evaluate(1).Camera!.Rect;
        Check(rect.X >= 0 && rect.Y >= 0 && Math.Abs(rect.X + rect.Width - 1) < 1e-9 && Math.Abs(rect.Y + rect.Height - 1) < 1e-9,
            "a new project's camera at position (1,1) stays inside the canvas flush to the corner");
        Check(!System.Text.Json.JsonSerializer.Deserialize<CameraTrack>("{\"MediaPath\":\"x\"}")!.EdgeAlignedPlacement,
            "projects saved before the option keep their centre-based camera position");
    }

    private static void NewEffectDefaults()
    {
        var settings = EffectDefaults.NewProjectSettings(1920, 1080);
        Near(settings.Padding, 100d / 1080, "padding follows the Mac 100 pt on a 1080 short edge"); Near(settings.CornerRadius, 24d / 1080, "corner radius 24 pt");
        Near(settings.Shadow, .28, "shadow .28");
        Check(settings.BackgroundStyle?.Kind == BackgroundKind.Pattern && settings.Cursor?.Scale == 1.25 && settings.Cursor.ClickEffect == ClickEffectStyle.Ripple
            && settings.Cursor.IdleSeconds == 1.2 && Math.Abs(settings.Cursor.ClickOpacity - .8) < 1e-9, "dark grid background and Mac cursor defaults");
        Near(CursorSettings.DefaultClickDuration(ClickEffectStyle.Burst), .22, "burst click duration");

        var motion = EffectDefaults.NewScreenMotion(1, 10);
        Check(motion.Target.Scale == 1.2 && motion.Target.RotationX == 0 && motion.Target.RotationY == 0 && motion.EnterDuration == .7 && motion.ReturnDuration == .7
            && motion.Easing == ZoomEasing.Spring && motion.Target.Position == new NormalizedPoint(.5, .5), "new screen 3D is a flat 1.2x spring move");
        var tail = EffectDefaults.NewBlurClip(9.9, 10);
        Check(tail.Start <= 9.75 + 1e-9 && tail.Duration >= .25 - 1e-9 && tail.Start + tail.Duration <= 10 + 1e-9, "new clips at the tail are clamped into the video");
        Check(tail.Strength == .55 && tail.CornerRadius == .08 && tail.TransitionStyle == RegionTransitionStyle.None, "redaction defaults are a hard cut");
        var focus = EffectDefaults.NewLinearFocusEffect();
        Check(focus.Shape == FocusEffectShape.Linear && focus.Size == .32 && focus.Dimming == 0, "focus effect defaults");

        var first = EffectDefaults.NewOverlay("a.png", 1, 10, 1, 16d / 9);
        Near(first.Rect.Width, .38, "first sticker is .38 wide"); Near(first.Rect.X + first.Rect.Width / 2, .5, "first sticker is centred");
        Check(first.EnterDuration == .7 && first.ExitDuration == .22 && first.EnterMotion == OverlayMotion.Scale, "pop transition timings");
        var second = EffectDefaults.NewOverlay("b.png", 1.5, 10, 1, 16d / 9, [first]);
        Near(second.Rect.Width, .28, "a later sticker is narrower");
        Check(Math.Abs(second.Rect.X + second.Rect.Width / 2 - .5) > .2 || Math.Abs(second.Rect.Y + second.Rect.Height / 2 - .5) > .2, "a later sticker avoids the existing one");
    }

    private static void MarkersMoveToCutPoint()
    {
        var keep = new TimelineSegment { SourceStart = 0, SourceDuration = 2 };
        var removed = new TimelineSegment { SourceStart = 2, SourceDuration = 2 };
        var tail = new TimelineSegment { SourceStart = 4, SourceDuration = 2 };
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Timeline = new() { Segments = [keep, removed, tail] },
            Markers = [new(Guid.NewGuid(), 1, "keep"), new(Guid.NewGuid(), 3, "inside"), new(Guid.NewGuid(), 5, "after")]
        };
        var moved = RecordingMarkerRules.MoveMarkersToCutPoints(project, [removed.Id]);
        Check(moved == 1 && project.Markers[1].SourceTime == 4 && project.Markers[0].SourceTime == 1 && project.Markers[2].SourceTime == 5,
            "markers inside a deleted range move to the cut point");
        var last = new FrameTrailProject { MediaPath = "x", Timeline = new() { Segments = [keep, removed] }, Markers = [new(Guid.NewGuid(), 3, "tail")] };
        RecordingMarkerRules.MoveMarkersToCutPoints(last, [removed.Id]);
        Near(last.Markers[0].SourceTime, 2, "markers after a removed tail move to the previous segment's end");
    }

    private static void PointerNoneAndCutReseed()
    {
        var raw = new PreparedPointerTrack([new(0, new(0, 0)), new(.1, new(1, 1))], PointerSmoothing.None).Sample(.05).Position!.Value;
        Near(raw.X, .5, "None passes the recorded trajectory straight through"); Near(raw.Y, .5, "None vertical");

        // Segment [0,1] then [5,6]: the retained clip resumes at the recorded position instead of replaying the deleted movement.
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 8 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }, new() { SourceStart = 5, SourceDuration = 1 }] },
            Settings = new() { AutoZoom = false },
            PointerEvents = [new(0, new(.1, .1)), new(.9, new(.1, .1)), new(4.9, new(.1, .1)), new(4.95, new(.9, .9)), new(5, new(.9, .9)), new(5.9, new(.9, .9))]
        };
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var beforeCut = evaluator.Evaluate(.999).Cursor!.Value; var atCut = evaluator.Evaluate(1.0).Cursor!.Value; var settled = evaluator.Evaluate(1.3).Cursor!.Value;
        Check(Math.Abs(atCut.X - beforeCut.X) < .02, "the pointer glides out of the cut instead of teleporting");
        Check(Math.Abs(settled.X - .9) < .005 && Math.Abs(settled.Y - .9) < .005, "after the cut the pointer sits at the retained clip's recorded position, not a spring tail from deleted footage");
    }

    private static FrameTrailProject Clone(FrameTrailProject project) =>
        System.Text.Json.JsonSerializer.Deserialize<FrameTrailProject>(System.Text.Json.JsonSerializer.Serialize(project))!;

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-6)
            throw new InvalidOperationException($"Mac parity core probe failed: {message}; expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Mac parity core probe failed: " + message);
    }
}
