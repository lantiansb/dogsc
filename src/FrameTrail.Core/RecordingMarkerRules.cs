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
}
