namespace FrameTrail;

/// <summary>What can be done at the cut between two consecutive primary segments.</summary>
public enum TimelineSeamKind
{
    /// <summary>The two segments overlap in source time (e.g. after reordering): nothing to merge or restore.</summary>
    None,
    /// <summary>Source-contiguous, same speed and audio: the split can be removed.</summary>
    Merge,
    /// <summary>Source material between the segments was deleted and can be put back.</summary>
    Restore
}

/// <summary>
/// Primary-lane cut seams: merge a structural split, restore a deleted source gap, restore trimmed head / tail.
/// Ported from Mac RecorderCore/ProjectTimelineEditing.swift (mergeAdjacentPrimarySegments, restorePrimaryGap,
/// restorePrimaryLeadingGap, restorePrimaryTrailingGap). Effects are stored in source time on Windows, so no
/// ripple of effect clips is needed when a source interval is inserted.
/// </summary>
public static class TimelineSeamOperations
{
    private const double Epsilon = 1e-6;

    /// <summary>Classifies the seam between segment <paramref name="previousIndex"/> and the next one.</summary>
    public static TimelineSeamKind Classify(ProjectTimeline timeline, int previousIndex)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (previousIndex < 0 || previousIndex + 1 >= timeline.Segments.Count) return TimelineSeamKind.None;
        var previous = timeline.Segments[previousIndex];
        var next = timeline.Segments[previousIndex + 1];
        var gap = next.SourceStart - previous.SourceEnd;
        if (Math.Abs(gap) <= Epsilon)
            return Math.Abs(previous.PlaybackRate - next.PlaybackRate) <= Epsilon && SameAudio(previous, next)
                ? TimelineSeamKind.Merge : TimelineSeamKind.None;
        return gap > Epsilon && !OverlapsOther(timeline, previous.SourceEnd, next.SourceStart, previous.Id, next.Id)
            ? TimelineSeamKind.Restore : TimelineSeamKind.None;
    }

    /// <summary>Removes the split between two source-contiguous segments; output time and effects stay unchanged.</summary>
    public static void MergeAdjacent(ProjectTimeline timeline, Guid previousId, Guid nextId)
    {
        var index = FindJunction(timeline, previousId, nextId);
        var previous = timeline.Segments[index];
        var next = timeline.Segments[index + 1];
        if (Math.Abs(next.SourceStart - previous.SourceEnd) > Epsilon)
            throw new InvalidOperationException("这两个片段之间有被剪掉的素材，请先还原再合并。");
        if (Math.Abs(previous.PlaybackRate - next.PlaybackRate) > Epsilon)
            throw new InvalidOperationException("两个片段速度不同，无法合并。");
        if (!SameAudio(previous, next))
            throw new InvalidOperationException("两个片段的声音设置不同，无法合并。");
        previous.SourceDuration = next.SourceEnd - previous.SourceStart;
        timeline.Segments.RemoveAt(index + 1);
    }

    /// <summary>Inserts the deleted source interval between two segments back as a new segment (1x speed).</summary>
    public static Guid RestoreGap(ProjectTimeline timeline, Guid previousId, Guid nextId)
    {
        var index = FindJunction(timeline, previousId, nextId);
        var previous = timeline.Segments[index];
        var next = timeline.Segments[index + 1];
        var start = previous.SourceEnd;
        var duration = next.SourceStart - start;
        if (duration <= Epsilon) throw new InvalidOperationException("这里没有被剪掉的素材可以还原。");
        EnsureFree(timeline, start, next.SourceStart, previousId, nextId);
        var restored = NewSegment(start, duration, previous);
        timeline.Segments.Insert(index + 1, restored);
        return restored.Id;
    }

    /// <summary>Restores the source material removed before the first retained segment.</summary>
    public static Guid RestoreLeading(ProjectTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Segments.Count == 0) throw new InvalidOperationException("时间线没有片段。");
        var first = timeline.Segments[0];
        if (first.SourceStart <= Epsilon) throw new InvalidOperationException("开头没有被剪掉的素材。");
        EnsureFree(timeline, 0, first.SourceStart, first.Id, first.Id);
        var restored = NewSegment(0, first.SourceStart, first);
        timeline.Segments.Insert(0, restored);
        return restored.Id;
    }

    /// <summary>Restores the source material removed after the last retained segment (up to the full recording).</summary>
    public static Guid RestoreTrailing(ProjectTimeline timeline, double fullSourceDuration)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Segments.Count == 0) throw new InvalidOperationException("时间线没有片段。");
        var last = timeline.Segments[^1];
        var duration = fullSourceDuration - last.SourceEnd;
        if (!double.IsFinite(fullSourceDuration) || duration <= Epsilon) throw new InvalidOperationException("结尾没有被剪掉的素材。");
        EnsureFree(timeline, last.SourceEnd, fullSourceDuration, last.Id, last.Id);
        var restored = NewSegment(last.SourceEnd, duration, last);
        timeline.Segments.Add(restored);
        return restored.Id;
    }

    /// <summary>True when material before the first segment can be restored.</summary>
    public static bool CanRestoreLeading(ProjectTimeline timeline)
        => timeline.Segments.Count > 0 && timeline.Segments[0].SourceStart > Epsilon
            && !OverlapsOther(timeline, 0, timeline.Segments[0].SourceStart, timeline.Segments[0].Id, timeline.Segments[0].Id);

    /// <summary>True when material after the last segment can be restored.</summary>
    public static bool CanRestoreTrailing(ProjectTimeline timeline, double fullSourceDuration)
        => timeline.Segments.Count > 0 && double.IsFinite(fullSourceDuration) && fullSourceDuration - timeline.Segments[^1].SourceEnd > Epsilon
            && !OverlapsOther(timeline, timeline.Segments[^1].SourceEnd, fullSourceDuration, timeline.Segments[^1].Id, timeline.Segments[^1].Id);

    private static int FindJunction(ProjectTimeline timeline, Guid previousId, Guid nextId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var index = timeline.Segments.FindIndex(x => x.Id == previousId);
        if (index < 0 || index + 1 >= timeline.Segments.Count || timeline.Segments[index + 1].Id != nextId)
            throw new InvalidOperationException("找不到这两个相邻的片段。");
        return index;
    }

    private static TimelineSegment NewSegment(double sourceStart, double sourceDuration, TimelineSegment audioDonor) => new()
    {
        SourceStart = sourceStart,
        SourceDuration = sourceDuration,
        PlaybackRate = 1,
        AudioOverrides = audioDonor.AudioOverrides is null ? null : audioDonor.AudioOverrides with { }
    };

    private static bool SameAudio(TimelineSegment left, TimelineSegment right)
        => Equals(left.AudioOverrides is { IsEmpty: true } ? null : left.AudioOverrides,
                  right.AudioOverrides is { IsEmpty: true } ? null : right.AudioOverrides);

    private static void EnsureFree(ProjectTimeline timeline, double start, double end, Guid ignoreA, Guid ignoreB)
    {
        if (OverlapsOther(timeline, start, end, ignoreA, ignoreB))
            throw new InvalidOperationException("这段素材已被时间线上的其它片段使用，无法还原。");
    }

    private static bool OverlapsOther(ProjectTimeline timeline, double start, double end, Guid ignoreA, Guid ignoreB)
    {
        foreach (var segment in timeline.Segments)
        {
            if (segment.Id == ignoreA || segment.Id == ignoreB) continue;
            if (segment.SourceStart < end - Epsilon && segment.SourceEnd > start + Epsilon) return true;
        }
        return false;
    }
}
