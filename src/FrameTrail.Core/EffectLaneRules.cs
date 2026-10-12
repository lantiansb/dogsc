namespace FrameTrail;

/// <summary>One clip on an effect lane. Times are in whatever single clock the caller uses (the app uses source seconds).</summary>
/// <param name="Tail">Exit / return window that plays after <paramref name="End"/> (zoom exit, motion return); 0 for static clips.</param>
public readonly record struct EffectLaneItem(Guid Id, double Start, double End, double Tail = 0)
{
    public double Duration => End - Start;
}

/// <summary>A placement result: the clip occupies [Start, End] and its tail window is already shrunk to fit.</summary>
public readonly record struct EffectLaneFit(double Start, double End, double Tail)
{
    public double Duration => End - Start;
}

public enum EffectLaneEdit { Move, Leading, Trailing }

/// <summary>
/// Same-lane placement rules for timed effect clips (zoom, screen 3D, camera motion, stickers, blur).
/// Ported from Mac RecorderCore/EditorTimeline.swift (fitZoomAnimation / fitMotionTiming) and
/// EditorTimelineView.swift (EditorMotionTimelinePresentation.adjustedTiming): clips on one lane never
/// overlap, a clip may either touch its neighbour or leave room for the neighbour's tail window, a
/// proposal that falls into the "hole" snaps to the closest legal position, and a gap that cannot hold
/// the clip rejects the change (null) instead of overlapping.
/// </summary>
public static class EffectLaneRules
{
    /// <summary>Mac ZoomInterpolator.adjacencyTolerance (1/120 s): neighbours closer than this count as touching.</summary>
    public const double Adjacency = 1d / 120;
    private const double Eps = 1e-4;

    /// <summary>
    /// Fits a newly created clip into the gap around <paramref name="start"/>. A click
    /// (|end-start| &lt; 0.08) creates <paramref name="clickLength"/>; a drag keeps the dragged length.
    /// Returns null when the gap holds no clip at all.
    /// </summary>
    public static EffectLaneFit? FitNew(IReadOnlyList<EffectLaneItem> lane, double start, double end, double limit,
        double clickLength, double minimumDuration, double defaultTail)
    {
        if (!double.IsFinite(limit) || limit <= 0 || !double.IsFinite(start) || !double.IsFinite(end)) return null;
        var rawStart = Math.Min(Math.Max(Math.Min(start, end), 0), limit);
        var (previous, next) = CreationNeighbours(lane, rawStart);
        var lowerTouch = previous?.End ?? 0;
        var lowerClear = previous is { } p ? p.End + EffectiveTail(p, defaultTail) : 0;
        var upper = next?.Start ?? limit;
        if (upper - lowerTouch <= .001) return null;

        var s = Math.Min(Math.Max(Math.Min(start, end), lowerTouch), upper);
        // The start fell into the previous clip's return window: snap to touching it.
        if (s < lowerClear) s = lowerTouch;
        var isClick = Math.Abs(end - start) < .08;
        var requested = isClick ? clickLength : Math.Max(Math.Abs(end - start), minimumDuration);
        var e = s + requested;
        if (e > upper)
        {
            e = upper;
            // Keep the requested length first by shifting left into the gap; if even the minimum does not
            // fit, shrink to exactly fill the gap (both ends touching).
            var shifted = Math.Max(e - requested, lowerTouch);
            s = upper - shifted >= minimumDuration ? shifted : lowerTouch;
        }
        if (e - s < minimumDuration - Eps || e - s <= .001) return null;
        var tail = Math.Min(defaultTail, Math.Max(upper - e, 0));
        return new EffectLaneFit(s, e, tail);
    }

    /// <summary>
    /// Adjusts one existing clip that is being moved / resized. <paramref name="proposedStart"/> and
    /// <paramref name="proposedEnd"/> are the absolute edges the user asked for (only the moved edge(s) are read:
    /// Move uses the start and keeps the duration, Leading the start, Trailing the end).
    /// Returns null when the change cannot be placed legally; the caller keeps the original.
    /// </summary>
    public static EffectLaneFit? Adjust(IReadOnlyList<EffectLaneItem> lane, Guid movingId, EffectLaneEdit mode,
        double proposedStart, double proposedEnd, double limit, double minimumDuration, double defaultTail)
    {
        EffectLaneItem orig = default;
        var found = false;
        foreach (var item in lane)
            if (item.Id == movingId) { orig = item; found = true; break; }
        if (!found || !double.IsFinite(limit) || limit <= 0) return null;
        EffectLaneItem? previous = null, next = null;
        foreach (var candidate in lane)
        {
            if (candidate.Id == movingId) continue;
            if (candidate.Start < orig.Start - Eps)
            {
                if (previous is null || Precedes(previous.Value, candidate)) previous = candidate;
            }
            else if (candidate.Start >= orig.End - Eps && (next is null || Precedes(candidate, next.Value))) next = candidate;
        }
        var previousEnd = Math.Max(previous?.End ?? 0, 0);
        var previousClear = previous is { } pv ? Math.Max(pv.End + EffectiveTail(pv, defaultTail), 0) : 0;
        var nextStart = Math.Min(Math.Max(next?.Start ?? limit, 0), limit);
        var ownTail = EffectiveTail(orig, defaultTail);
        var duration = orig.Duration;

        bool IsValid(double start, double end)
        {
            var touchesPrevious = start <= previousEnd + Adjacency && start >= previousEnd - Eps;
            var clearsPrevious = start >= previousClear - Eps;
            var touchesNext = nextStart - end <= Adjacency && end <= nextStart + Eps;
            var clearsNext = end + ownTail <= nextStart + Eps;
            return (touchesPrevious || clearsPrevious) && (touchesNext || clearsNext);
        }

        double? Snap(double proposed, double lower, double upper, double[] candidates, Func<double, (double Start, double End)> value)
        {
            var clamped = Math.Min(Math.Max(proposed, lower), upper);
            var probe = value(clamped);
            if (IsValid(probe.Start, probe.End)) return clamped;
            var valid = candidates.Where(c =>
            {
                if (c < lower - Eps || c > upper + Eps) return false;
                var p = value(c);
                return IsValid(p.Start, p.End);
            }).ToArray();
            return valid.Length == 0 ? null : valid.MinBy(c => Math.Abs(c - proposed));
        }

        switch (mode)
        {
            case EffectLaneEdit.Move:
            {
                var lower = previousEnd;
                var upper = nextStart - duration;
                if (upper < previousEnd - Eps) return null;
                var start = Snap(proposedStart, lower, Math.Max(upper, lower),
                    [previousEnd, previousClear, upper, upper - ownTail], s => (s, s + duration));
                return start is { } ms ? Result(ms, ms + duration, nextStart, defaultTail, orig) : null;
            }
            case EffectLaneEdit.Leading:
            {
                var lower = previousEnd;
                var upper = orig.End - minimumDuration;
                if (upper < previousEnd - Eps) return null;
                var start = Snap(proposedStart, lower, Math.Max(upper, lower),
                    [previousEnd, previousClear, upper], s => (s, orig.End));
                return start is { } ls ? Result(ls, orig.End, nextStart, defaultTail, orig) : null;
            }
            default:
            {
                var lower = orig.Start + minimumDuration;
                var upper = nextStart;
                var end = Snap(proposedEnd, lower, Math.Max(upper, lower),
                    [upper, upper - ownTail, lower], e => (orig.Start, e));
                return end is { } te ? Result(orig.Start, te, nextStart, defaultTail, orig) : null;
            }
        }
    }

    /// <summary>
    /// Snaps a paste / duplicate position: appends at an existing end, fits directly before the next start,
    /// or sits at 0 / the lane end — whichever legal candidate is within <paramref name="tolerance"/>;
    /// otherwise returns the proposed time unchanged. Mirrors Mac EditorClipClipboard.insertionTime.
    /// </summary>
    public static double SnapPasteStart(IReadOnlyList<EffectLaneItem> lane, double proposed, double length, double limit, double tolerance, bool exclusive = true)
    {
        if (!double.IsFinite(proposed) || length <= 0) return proposed;
        var candidates = new List<double> { 0, limit - length };
        foreach (var item in lane)
        {
            candidates.Add(item.End);
            candidates.Add(item.Start - length);
        }
        var target = candidates.Where(start => double.IsFinite(start) && start >= 0 && start + length <= limit + 1e-6
                && Math.Abs(start - proposed) <= tolerance
                && (!exclusive || !lane.Any(i => start < i.End - 1e-6 && start + length > i.Start + 1e-6)))
            .Select(start => (double?)start).MinBy(start => Math.Abs(start!.Value - proposed));
        return target ?? proposed;
    }

    /// <summary>True when [start, start+length] overlaps nothing on the lane (Mac requireSpace).</summary>
    public static bool HasSpace(IReadOnlyList<EffectLaneItem> lane, double start, double length)
        => !lane.Any(i => start < i.End - 1e-6 && start + length > i.Start + 1e-6);

    /// <summary>The clip whose [Start, End) contains <paramref name="time"/>, if any.</summary>
    public static EffectLaneItem? FindAt(IReadOnlyList<EffectLaneItem> lane, double time)
    {
        EffectLaneItem? best = null;
        foreach (var item in lane)
        {
            if (time < item.Start - 1e-9 || time >= item.End) continue;
            if (best is null || item.Start >= best.Value.Start) best = item;
        }
        return best;
    }

    /// <summary>
    /// Fit-return for a clip followed closely by another one: a gap between the adjacency tolerance and the
    /// tail shrinks the tail to the gap (Mac fitReturnBesideSuccessor). Returns the new tail, or the old one.
    /// </summary>
    public static double FitTailBeforeSuccessor(double end, double tail, double? successorStart)
    {
        if (successorStart is not { } next) return tail;
        var gap = next - end;
        return gap > Adjacency && gap < tail ? gap : tail;
    }

    private static EffectLaneFit Result(double start, double end, double nextStart, double defaultTail, EffectLaneItem original)
    {
        var tail = EffectiveTail(original, defaultTail);
        // A gap smaller than the return window shrinks the window; touching neighbours keep it dormant.
        var room = nextStart - end;
        if (room > Adjacency && room < tail) tail = room;
        return new EffectLaneFit(start, end, tail);
    }

    private static double EffectiveTail(EffectLaneItem item, double defaultTail) => item.Tail > Eps ? item.Tail : defaultTail;

    private static bool Precedes(EffectLaneItem left, EffectLaneItem right)
        => left.Start == right.Start ? left.Id.CompareTo(right.Id) < 0 : left.Start < right.Start;

    private static (EffectLaneItem? Previous, EffectLaneItem? Next) CreationNeighbours(IReadOnlyList<EffectLaneItem> lane, double rawStart)
    {
        EffectLaneItem? previous = null;
        var previousStart = double.NegativeInfinity;
        foreach (var item in lane)
        {
            if (item.Start > rawStart + Eps || item.Start < previousStart) continue;
            previous = item; previousStart = item.Start;
        }
        var threshold = previous?.Start ?? -1;
        EffectLaneItem? next = null;
        var nextStart = double.PositiveInfinity;
        foreach (var item in lane)
        {
            if (item.Start <= threshold + Eps || item.Start >= nextStart) continue;
            next = item; nextStart = item.Start;
        }
        return (previous, next);
    }
}
