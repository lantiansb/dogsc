using FrameTrail;

namespace FrameTrail.Core.Tests;

/// <summary>Seams between the parity work packages: pointer focus for new zooms and markers following Q / W trims.</summary>
internal static class IntegrationSeamProbe
{
    public static void Run()
    {
        PointerFocusForNewZoom();
        MarkersFollowTrim();
        Console.WriteLine("通过：集成接缝（新建缩放取指针焦点、Q/W 裁剪时标记移到新边缘）");
    }

    private static void PointerFocusForNewZoom()
    {
        Check(EffectDefaults.PointerLocationAt([], 1) is null, "no pointer data means no focus (the zoom is centred)");
        Check(EffectDefaults.PointerLocationAt(null, 1) is null, "null events are tolerated");
        var events = new[]
        {
            new PointerEvent(2, new(.8, .6)),
            new PointerEvent(1, new(.2, .4)),
            new PointerEvent(float.NaN, new(.9, .9))
        };
        Near(EffectDefaults.PointerLocationAt(events, 1.5)!.Value.X, .5, "interpolates between the surrounding events (unordered input)");
        Near(EffectDefaults.PointerLocationAt(events, 1.5)!.Value.Y, .5, "interpolates vertically");
        Near(EffectDefaults.PointerLocationAt(events, 0)!.Value.X, .2, "holds the first location before the recording's first event");
        Near(EffectDefaults.PointerLocationAt(events, 9)!.Value.X, .8, "holds the last location after the final event");
        var zoom = EffectDefaults.NewManualZoom(1, 10, EffectDefaults.PointerLocationAt(events, 1.5), 2);
        Check(zoom.Scale == 2 && Math.Abs(zoom.Focus.X - .5) < 1e-9 && !zoom.Automatic, "the zoom takes the chosen scale and the pointer focus");
    }

    private static void MarkersFollowTrim()
    {
        var first = new TimelineSegment { SourceStart = 0, SourceDuration = 10 };
        var other = new TimelineSegment { SourceStart = 12, SourceDuration = 4 };
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Timeline = new() { Segments = [first, other] },
            Markers = [new(Guid.NewGuid(), 1, "head"), new(Guid.NewGuid(), 5, "kept"), new(Guid.NewGuid(), 9, "tail"), new(Guid.NewGuid(), 13, "other")]
        };
        // W at source 6 trims [6,10]: the marker at 9 moves to the new end, the others stay.
        var moved = RecordingMarkerRules.MoveMarkersToTrimEdges(project, first.Id, 0, 6);
        Check(moved == 1 && Math.Abs(project.Markers[2].SourceTime - 6) < 1e-9, "a marker in the trimmed-off tail moves to the new end");
        Near(project.Markers[0].SourceTime, 1, "markers before the trim stay");
        Near(project.Markers[3].SourceTime, 13, "markers of other segments stay");
        // Q at source 3 trims [0,3]: the head marker moves to the new start.
        moved = RecordingMarkerRules.MoveMarkersToTrimEdges(project, first.Id, 3, 10);
        Check(moved == 1 && Math.Abs(project.Markers[0].SourceTime - 3) < 1e-9, "a marker in the trimmed-off head moves to the new start");
        // A marker also covered by another segment is not touched.
        var overlap = new TimelineSegment { SourceStart = 0, SourceDuration = 4 };
        var shared = new FrameTrailProject { MediaPath = "x", Timeline = new() { Segments = [first, overlap] }, Markers = [new(Guid.NewGuid(), 2, "shared")] };
        Check(RecordingMarkerRules.MoveMarkersToTrimEdges(shared, first.Id, 5, 10) == 0 && shared.Markers[0].SourceTime == 2, "a marker still reachable through another segment stays");
    }

    private static void Near(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"{message} (actual {actual}, expected {expected})");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
