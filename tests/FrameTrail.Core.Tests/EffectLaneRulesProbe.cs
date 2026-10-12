using FrameTrail;

namespace FrameTrail.Core.Tests;

/// <summary>Same-lane placement rules (Mac fitZoomAnimation / adjustedTiming) and primary-lane seam operations.</summary>
public static class EffectLaneRulesProbe
{
    public static void Run()
    {
        ClickCreatesDefaultLengthInEmptyLane();
        DragKeepsDraggedLength();
        CreationShrinksIntoGap();
        CreationRejectedWithoutRoom();
        MoveClampsBetweenNeighbours();
        MoveIntoOccupiedHoleSnapsOrRejects();
        TrailingResizeStopsAtNeighbour();
        LeadingResizeStopsAtPreviousEnd();
        PasteSnapAppendsAtEnd();
        FindAtPicksContainingClip();
        SeamClassification();
        MergeJoinsContiguousSegments();
        MergeRejectsDifferentSpeed();
        RestoreGapInsertsMissingSource();
        RestoreLeadingAndTrailing();
        RestoreRejectsReusedSource();
        Console.WriteLine("通过：同轨效果片段拟合/约束与剪切缝合并/还原回归。");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    private static EffectLaneItem Item(double start, double end, double tail = 0) => new(Guid.NewGuid(), start, end, tail);

    private static void ClickCreatesDefaultLengthInEmptyLane()
    {
        var fit = EffectLaneRules.FitNew([], 2, 2, 20, clickLength: 1.5, minimumDuration: .1, defaultTail: .5);
        Check(fit is { } f && Near(f.Start, 2) && Near(f.End, 3.5), "click in an empty lane creates the default length at the click");
    }

    private static void DragKeepsDraggedLength()
    {
        var fit = EffectLaneRules.FitNew([], 4, 7, 20, 1.5, .1, .5);
        Check(fit is { } f && Near(f.Start, 4) && Near(f.End, 7), "drag creates the dragged range");
        var reversed = EffectLaneRules.FitNew([], 7, 4, 20, 1.5, .1, .5);
        Check(reversed is { } r && Near(r.Start, 4) && Near(r.End, 7), "right-to-left drag is normalised");
    }

    private static void CreationShrinksIntoGap()
    {
        var lane = new[] { Item(0, 2), Item(3, 6) };
        var fit = EffectLaneRules.FitNew(lane, 2.2, 2.2, 20, 1.5, .1, 0);
        Check(fit is { } f && f.Start >= 2 - 1e-6 && f.End <= 3 + 1e-6 && f.Duration > .1, "click shrinks into the gap between neighbours");
        var past = EffectLaneRules.FitNew(lane, 19.9, 19.9, 20, 1.5, .1, 0);
        Check(past is { } p && p.End <= 20 + 1e-6 && p.Start >= 6 - 1e-6, "click near the end stays inside the media");
    }

    private static void CreationRejectedWithoutRoom()
    {
        var lane = new[] { Item(0, 10) };
        Check(EffectLaneRules.FitNew(lane, 5, 5, 10, 1.5, .1, 0) is null, "no room after the last clip when it ends at the limit");
        var tight = new[] { Item(0, 2), Item(2.05, 6) };
        Check(EffectLaneRules.FitNew(tight, 2.01, 2.01, 20, 1.5, .1, 0) is null, "gap smaller than the minimum duration is rejected");
    }

    private static void MoveClampsBetweenNeighbours()
    {
        var a = Item(0, 2); var b = Item(3, 4); var c = Item(8, 10);
        var lane = new[] { a, b, c };
        var fit = EffectLaneRules.Adjust(lane, b.Id, EffectLaneEdit.Move, 20, 0, 30, .1, 0);
        Check(fit is { } f && Near(f.Duration, 1) && f.End <= 8 + 1e-6 && f.Start >= 2 - 1e-6, "dragging far right stops against the next clip");
        var left = EffectLaneRules.Adjust(lane, b.Id, EffectLaneEdit.Move, -5, 0, 30, .1, 0);
        Check(left is { } l && Near(l.Start, 2) && Near(l.Duration, 1), "dragging far left stops at the previous clip end");
    }

    private static void MoveIntoOccupiedHoleSnapsOrRejects()
    {
        var a = Item(0, 2, tail: .5); var b = Item(5, 6);
        var lane = new[] { a, b };
        // Inside the previous clip's return window: legal positions are touching (2) or clear of the tail (2.5).
        var fit = EffectLaneRules.Adjust(lane, b.Id, EffectLaneEdit.Move, 2.2, 0, 30, .1, .5);
        Check(fit is { } f && (Near(f.Start, 2) || Near(f.Start, 2.5)), "a move into the return window snaps to a legal position");
        var sandwiched = new[] { Item(0, 2), Item(2, 3), Item(3, 5) };
        var stuck = EffectLaneRules.Adjust(sandwiched, sandwiched[1].Id, EffectLaneEdit.Move, 2.5, 0, 30, .1, 0);
        Check(stuck is { } s && Near(s.Start, 2), "a clip squeezed between touching neighbours cannot leave its slot");
    }

    private static void TrailingResizeStopsAtNeighbour()
    {
        var a = Item(1, 2); var b = Item(5, 6);
        var fit = EffectLaneRules.Adjust([a, b], a.Id, EffectLaneEdit.Trailing, 0, 99, 30, .1, 0);
        Check(fit is { } f && Near(f.Start, 1) && Near(f.End, 5), "extending the end stops where the next clip starts");
        var shrink = EffectLaneRules.Adjust([a, b], a.Id, EffectLaneEdit.Trailing, 0, 1.01, 30, .1, 0);
        Check(shrink is { } s && Near(s.End, 1.1), "shrinking never goes below the minimum duration");
    }

    private static void LeadingResizeStopsAtPreviousEnd()
    {
        var a = Item(0, 2); var b = Item(4, 6);
        var fit = EffectLaneRules.Adjust([a, b], b.Id, EffectLaneEdit.Leading, -3, 0, 30, .1, 0);
        Check(fit is { } f && Near(f.Start, 2) && Near(f.End, 6), "extending the start stops at the previous clip end");
    }

    private static void PasteSnapAppendsAtEnd()
    {
        var lane = new[] { Item(1, 3) };
        var snapped = EffectLaneRules.SnapPasteStart(lane, 3.05, 1, 20, .2);
        Check(Near(snapped, 3), "paste next to an existing clip end appends exactly after it");
        Check(Near(EffectLaneRules.SnapPasteStart(lane, 10, 1, 20, .2), 10), "paste far from any edge keeps the requested time");
        Check(EffectLaneRules.HasSpace(lane, 3, 1) && !EffectLaneRules.HasSpace(lane, 2.5, 1), "HasSpace detects overlap");
    }

    private static void FindAtPicksContainingClip()
    {
        var a = Item(1, 2); var b = Item(2, 3);
        Check(EffectLaneRules.FindAt([a, b], 2) is { } hit && hit.Id == b.Id, "a boundary time belongs to the clip that starts there");
        Check(EffectLaneRules.FindAt([a, b], 3.5) is null, "no clip outside every range");
    }

    private static ProjectTimeline Timeline(params (double Start, double Duration, double Rate)[] segments)
        => new() { Segments = segments.Select(s => new TimelineSegment { SourceStart = s.Start, SourceDuration = s.Duration, PlaybackRate = s.Rate }).ToList() };

    private static void SeamClassification()
    {
        var contiguous = Timeline((0, 2, 1), (2, 2, 1));
        Check(TimelineSeamOperations.Classify(contiguous, 0) == TimelineSeamKind.Merge, "contiguous equal segments merge");
        var gap = Timeline((0, 2, 1), (5, 2, 1));
        Check(TimelineSeamOperations.Classify(gap, 0) == TimelineSeamKind.Restore, "a deleted source gap restores");
        var reordered = Timeline((5, 2, 1), (0, 2, 1));
        Check(TimelineSeamOperations.Classify(reordered, 0) == TimelineSeamKind.None, "reordered segments have no seam action");
        Check(TimelineSeamOperations.Classify(gap, 3) == TimelineSeamKind.None, "out of range index is None");
    }

    private static void MergeJoinsContiguousSegments()
    {
        var timeline = Timeline((0, 2, 1), (2, 3, 1), (10, 1, 1));
        var before = timeline.Duration;
        TimelineSeamOperations.MergeAdjacent(timeline, timeline.Segments[0].Id, timeline.Segments[1].Id);
        Check(timeline.Segments.Count == 2 && Near(timeline.Segments[0].SourceDuration, 5) && Near(timeline.Duration, before), "merge removes the split without changing the duration");
    }

    private static void MergeRejectsDifferentSpeed()
    {
        var timeline = Timeline((0, 2, 1), (2, 2, 2));
        var threw = false;
        try { TimelineSeamOperations.MergeAdjacent(timeline, timeline.Segments[0].Id, timeline.Segments[1].Id); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && timeline.Segments.Count == 2, "segments with different speeds cannot be merged");
    }

    private static void RestoreGapInsertsMissingSource()
    {
        var timeline = Timeline((0, 2, 1), (5, 2, 1));
        var id = TimelineSeamOperations.RestoreGap(timeline, timeline.Segments[0].Id, timeline.Segments[1].Id);
        Check(timeline.Segments.Count == 3 && timeline.Segments[1].Id == id && Near(timeline.Segments[1].SourceStart, 2) && Near(timeline.Segments[1].SourceDuration, 3),
            "restore inserts the missing source interval between the neighbours");
        Check(TimelineSeamOperations.Classify(timeline, 0) == TimelineSeamKind.Merge, "after restoring, both seams are mergeable");
    }

    private static void RestoreLeadingAndTrailing()
    {
        var timeline = Timeline((3, 2, 1));
        Check(TimelineSeamOperations.CanRestoreLeading(timeline) && TimelineSeamOperations.CanRestoreTrailing(timeline, 10), "head and tail can be restored");
        TimelineSeamOperations.RestoreLeading(timeline);
        TimelineSeamOperations.RestoreTrailing(timeline, 10);
        Check(timeline.Segments.Count == 3 && Near(timeline.Segments[0].SourceStart, 0) && Near(timeline.Segments[0].SourceDuration, 3)
            && Near(timeline.Segments[2].SourceStart, 5) && Near(timeline.Segments[2].SourceEnd, 10), "leading and trailing restore reach the full recording");
        Check(!TimelineSeamOperations.CanRestoreLeading(timeline) && !TimelineSeamOperations.CanRestoreTrailing(timeline, 10), "nothing left to restore afterwards");
    }

    private static void RestoreRejectsReusedSource()
    {
        // Source 2-5 is already used by the third segment, so the gap between the first two cannot be re-inserted.
        var timeline = Timeline((0, 2, 1), (6, 2, 1), (2, 3, 1));
        var threw = false;
        try { TimelineSeamOperations.RestoreGap(timeline, timeline.Segments[0].Id, timeline.Segments[1].Id); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "restore refuses to duplicate source material that another segment already uses");
    }
}
