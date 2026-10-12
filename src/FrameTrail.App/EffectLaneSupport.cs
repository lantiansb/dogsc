namespace FrameTrail.App;

/// <summary>
/// Per-lane settings and lane snapshots shared by the timeline control (drag preview) and MainWindow (create / paste).
/// Mirrors Mac EditorTimelineView.adjustedTiming / RecorderCore.EditorTimelineMath.fitZoomAnimation / fitMotionTiming:
/// zoom, screen 3D and camera motion lanes are exclusive (clips never overlap); sticker and blur lanes may stack.
/// All times here are source seconds, which is how effect clips are stored.
/// </summary>
internal static class EffectLaneSupport
{
    /// <summary>Lanes whose clips may not overlap (Mac: zoom, screen motion, camera motion).</summary>
    public static bool IsExclusive(string kind) => kind is "zoom" or "motion" or "cameraMotion";

    /// <summary>Lanes that can be created on from the timeline.</summary>
    public static bool IsCreatable(string kind) => kind is "zoom" or "motion" or "cameraMotion" or "overlay" or "blur";

    /// <summary>Smallest clip a drag may leave behind.</summary>
    public static double MinimumDuration(string kind) => kind switch
    {
        "zoom" => .08,
        "motion" or "cameraMotion" => .16,
        _ => .1
    };

    /// <summary>Smallest clip a click / drag may create (Mac addMosaic/addSticker use .25).</summary>
    public static double CreateMinimumDuration(string kind) => kind switch
    {
        "zoom" => .08,
        "motion" or "cameraMotion" => .16,
        _ => .25
    };

    /// <summary>Exit / return window assumed when a clip has none of its own.</summary>
    public static double DefaultTail(string kind) => kind == "zoom" ? .7 : 0;

    /// <summary>Last visible source second: effects past the final retained segment can never be seen.</summary>
    public static double Limit(FrameTrailProject project)
    {
        var media = Math.Max(project.Media.Duration, 0);
        if (project.Timeline.Segments.Count == 0) return media;
        var visible = project.Timeline.Segments.Max(x => x.SourceEnd);
        return media > 0 ? Math.Min(media, visible) : visible;
    }

    public static string KindName(string kind) => kind switch
    {
        "zoom" => "缩放",
        "motion" => "屏幕 3D",
        "cameraMotion" => "摄像运动",
        "overlay" => "贴图",
        "blur" => "柔化",
        _ => "效果"
    };

    /// <summary>Snapshot of one lane. <paramref name="excluded"/> ids are left out (e.g. clips that are being dragged).</summary>
    public static List<EffectLaneItem> Items(FrameTrailProject project, string kind, ICollection<Guid>? excluded = null)
    {
        IEnumerable<EffectLaneItem> source = kind switch
        {
            "zoom" => project.ZoomClips.Select(x => new EffectLaneItem(x.Id, x.Start, x.End, x.ExitDuration)),
            "motion" => project.ScreenMotions.Select(x => new EffectLaneItem(x.Id, x.Start, x.End, x.ReturnDuration)),
            "cameraMotion" => project.CameraMotions.Select(x => new EffectLaneItem(x.Id, x.Start, x.End, x.ReturnDuration)),
            "overlay" => project.Overlays.Select(x => new EffectLaneItem(x.Id, x.Start, x.End)),
            "blur" => project.BlurClips.Select(x => new EffectLaneItem(x.Id, x.Start, x.End)),
            _ => []
        };
        return (excluded is null || excluded.Count == 0 ? source : source.Where(x => !excluded.Contains(x.Id))).ToList();
    }

    /// <summary>
    /// True when two clips of an exclusive lane overlap by more than rounding noise. Touching clips are fine.
    /// </summary>
    public static bool Overlaps(double startA, double endA, double startB, double endB)
        => startA < endB - 1e-6 && endA > startB + 1e-6;
}
