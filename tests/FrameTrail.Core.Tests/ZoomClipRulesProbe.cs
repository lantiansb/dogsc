using FrameTrail;

/// <summary>F-01 (auto zoom cannot come back after the lane is emptied) and F-06 (Mac ZoomTransitionResolution.resolve).</summary>
internal static class ZoomClipRulesProbe
{
    public static void Run()
    {
        // --- ResolveTransitions -------------------------------------------------------------------------
        var gapClips = ZoomClipRules.ResolveTransitions(
        [
            new ZoomClip { Start = 1, End = 2, ExitDuration = .7, EnterDuration = .3 },
            new ZoomClip { Start = 2.3, End = 3, ExitDuration = .7, EnterDuration = .3 }
        ], 10);
        Near(gapClips[0].ExitDuration, .3, "exit is fitted to the gap before the next zoom");
        Near(gapClips[1].End, 3, "a zoom with room before the output end keeps its end");
        Near(gapClips[1].ExitDuration, .7, "last zoom keeps its requested exit when it fits");

        var touching = ZoomClipRules.ResolveTransitions(
        [
            new ZoomClip { Start = 1, End = 2, ExitDuration = .7 },
            new ZoomClip { Start = 2, End = 3, ExitDuration = .7 }
        ], 10);
        Near(touching[0].ExitDuration, 0, "adjacent zoom clips hand off without an exit");

        var tail = ZoomClipRules.ResolveTransitions([new ZoomClip { Start = 3, End = 4, ExitDuration = .7, EnterDuration = .7, ExitProgressOffset = .4 }], 4);
        Near(tail[0].End, 3.5, "the last zoom gives up hold time so its return fits inside the output");
        Near(tail[0].ExitDuration, .5, "the return keeps the room that is left");
        Near(tail[0].ExitProgressOffset, 0, "a shortened tail drops the continued-return offset");

        var shortTail = ZoomClipRules.ResolveTransitions([new ZoomClip { Start = 9.5, End = 9.9, ExitDuration = .7, EnterDuration = .7 }], 10);
        Near(shortTail[0].End, 9.75, "a short last zoom reserves half the remaining time for the entrance");
        Near(shortTail[0].EnterDuration, .25, "entrance is fitted to the shortened clip");

        var hidden = new ZoomClip { Start = 12, End = 13 };
        var withHidden = ZoomClipRules.ResolveTransitions([new ZoomClip { Start = 8, End = 9, ExitDuration = .7 }, hidden], 10);
        Check(withHidden.Length == 2 && withHidden.Any(c => c.Id == hidden.Id), "clips past the output end stay addressable");
        Near(withHidden.First(c => c.Start == 8).ExitDuration, .7, "a clip past the output end is not the next clip of a visible one");

        // --- Evaluator uses the resolution ----------------------------------------------------------------
        FrameTrailProject Make(params ZoomClip[] clips) => new()
        {
            MediaPath = "capture.mp4", Media = new MediaAsset { Width = 1920, Height = 1080, Duration = 4 },
            Timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 4 }] },
            Settings = new FrameTrailSettings { AutoZoom = false }, ZoomClips = clips.ToList()
        };
        var gapEvaluator = new PreparedFrameSceneEvaluator(Make(
            new ZoomClip { Start = 1, End = 2, Scale = 2, ExitDuration = .7, EnterDuration = .2, Easing = ZoomEasing.Linear },
            new ZoomClip { Start = 2.3, End = 3, Scale = 2, ExitDuration = 0, EnterDuration = .2, Easing = ZoomEasing.Linear }));
        Near(gapEvaluator.Evaluate(2.15).Zoom, 1.5, "evaluated exit ends exactly where the next zoom starts");
        var tailEvaluator = new PreparedFrameSceneEvaluator(Make(new ZoomClip { Start = 3, End = 4, Scale = 2, ExitDuration = .7, EnterDuration = .2, Easing = ZoomEasing.Linear }));
        Near(tailEvaluator.Evaluate(4).Zoom, 1, "the last zoom has returned to the base zoom at the output end");
        Near(tailEvaluator.Evaluate(3.25).Zoom, 2, "the hold still reaches the full scale");

        // --- F-01 -----------------------------------------------------------------------------------------
        var legacy = Make();
        legacy.Settings.AutoZoom = true;
        legacy.PointerEvents = [new(1, new(.5, .5), PointerEventKind.LeftClick)];
        Check(ZoomClipRules.MaterializeLegacyAutoZoom(legacy), "legacy auto zoom is materialised on open");
        Check(legacy.ZoomClips.Count == 1 && legacy.ZoomClips[0].Automatic && !legacy.Settings.AutoZoom, "materialised plan is stored and the fallback is switched off");
        Check(!ZoomClipRules.MaterializeLegacyAutoZoom(legacy), "materialisation happens only once");

        var cleared = Make();
        cleared.Settings.AutoZoom = true;
        cleared.ZoomClips = [new ZoomClip { Start = 1, End = 2, Automatic = true }];
        cleared.PointerEvents = [new(1, new(.5, .5), PointerEventKind.LeftClick)];
        cleared.ZoomClips.Clear();
        ZoomClipRules.NormalizeAfterEdit(1, cleared);
        Check(!cleared.Settings.AutoZoom, "emptying the zoom lane turns auto zoom off");
        Near(new PreparedFrameSceneEvaluator(cleared).Evaluate(1.2).Zoom, 1, "no ghost zoom after clearing");

        var untouched = Make();
        untouched.Settings.AutoZoom = true;
        ZoomClipRules.NormalizeAfterEdit(0, untouched);
        Check(untouched.Settings.AutoZoom, "an edit that never had zoom clips leaves auto zoom alone");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 1e-6) throw new Exception($"{message}: expected {expected}, got {actual}");
    }
}
