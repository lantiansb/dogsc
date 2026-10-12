namespace FrameTrail;

/// <summary>Marker maintenance for primary-segment edits (Mac moves markers with the output clock when footage is removed).</summary>
public static class RecordingMarkerRules
{
    /// <summary>
    /// Call BEFORE removing primary segments. Markers that lie only inside the segments about to be removed are moved to the cut
    /// point instead of becoming unmappable: the start of the next retained segment, or the end of the previous one when the tail is removed.
    /// Returns the number of markers moved. Does nothing if every segment would be removed.
    /// </summary>
    public static int MoveMarkersToCutPoints(FrameTrailProject project, IReadOnlyCollection<Guid> removedSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(removedSegmentIds);
        var segments = project.Timeline.Segments;
        if (project.Markers.Count == 0 || removedSegmentIds.Count == 0 || segments.All(x => removedSegmentIds.Contains(x.Id))) return 0;
        var moved = 0;
        for (var m = 0; m < project.Markers.Count; m++)
        {
            var marker = project.Markers[m];
            if (segments.Any(s => !removedSegmentIds.Contains(s.Id) && marker.SourceTime >= s.SourceStart && marker.SourceTime <= s.SourceEnd)) continue;
            var removedIndex = segments.FindIndex(s => removedSegmentIds.Contains(s.Id) && marker.SourceTime >= s.SourceStart && marker.SourceTime <= s.SourceEnd);
            if (removedIndex < 0) continue;
            var next = segments.Skip(removedIndex + 1).FirstOrDefault(s => !removedSegmentIds.Contains(s.Id));
            var target = next is not null
                ? next.SourceStart
                : segments.Take(removedIndex).LastOrDefault(s => !removedSegmentIds.Contains(s.Id))?.SourceEnd;
            if (target is not { } time || Math.Abs(time - marker.SourceTime) < 1e-9) continue;
            project.Markers[m] = marker with { SourceTime = time };
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Call BEFORE trimming a primary segment to [newStart, newEnd] (Q / W ripple delete of the part before / after the playhead).
    /// Markers inside the part being cut off, and not covered by another segment, move to the new edge so they stay reachable.
    /// Same semantics as <see cref="MoveMarkersToCutPoints"/> for the "trim" case. Returns the number of markers moved.
    /// </summary>
    public static int MoveMarkersToTrimEdges(FrameTrailProject project, Guid segmentId, double newStart, double newEnd)
    {
        ArgumentNullException.ThrowIfNull(project);
        var segments = project.Timeline.Segments;
        var segment = segments.FirstOrDefault(x => x.Id == segmentId);
        if (segment is null || project.Markers.Count == 0 || !double.IsFinite(newStart) || !double.IsFinite(newEnd) || newEnd <= newStart) return 0;
        var moved = 0;
        for (var m = 0; m < project.Markers.Count; m++)
        {
            var marker = project.Markers[m];
            var time = marker.SourceTime;
            if (time < segment.SourceStart || time > segment.SourceEnd || (time >= newStart && time <= newEnd)) continue;
            if (segments.Any(s => s.Id != segmentId && time >= s.SourceStart && time <= s.SourceEnd)) continue;
            project.Markers[m] = marker with { SourceTime = time < newStart ? newStart : newEnd };
            moved++;
        }
        return moved;
    }
}
