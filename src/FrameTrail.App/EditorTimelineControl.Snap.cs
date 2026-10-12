using System.Windows.Input;

namespace FrameTrail.App;

/// <summary>
/// Magnetic snapping shared by every timeline gesture (scrub, create, move, trim, retime).
/// Mac reference: EditorTimelineMagneticSnap.swift. Targets are 0, the end, every primary segment boundary,
/// every visible effect lane edge and (when not scrubbing) the playhead. A target is captured within 7 px and
/// released beyond 14 px (hysteresis); Shift skips snapping temporarily. All times here are output seconds.
/// </summary>
public sealed partial class EditorTimelineControl
{
    const double SnapCapturePixels = 7, SnapReleasePixels = 14;

    List<(double Time, Guid Owner)>? snapCandidates;
    double? lockedTarget;
    int lockedEdge = -1;
    double? snapGuide;

    bool SnapActive => Snap && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    void ResetSnap()
    {
        snapCandidates = null;
        lockedTarget = null;
        lockedEdge = -1;
        snapGuide = null;
    }

    /// <summary>Builds the snap target list once per gesture. Clips in <paramref name="excluded"/> do not attract.</summary>
    List<(double Time, Guid Owner)> SnapTargets(ICollection<Guid>? excluded, bool includePlayhead)
    {
        if (snapCandidates != null) return snapCandidates;
        var list = new List<(double, Guid)>();
        if (Project == null) return snapCandidates = list;
        var timeline = Project.Timeline;
        list.Add((0, Guid.Empty));
        list.Add((timeline.Duration, Guid.Empty));
        var elapsed = 0d;
        foreach (var segment in timeline.Segments)
        {
            var skip = excluded != null && excluded.Contains(segment.Id);
            if (!skip)
            {
                list.Add((elapsed, segment.Id));
                list.Add((elapsed + segment.OutputDuration, segment.Id));
            }
            elapsed += segment.OutputDuration;
        }
        void Add(string kind, IEnumerable<(Guid Id, double Start, double End)> clips)
        {
            if (HiddenTracks.Contains(kind)) return;
            foreach (var clip in clips)
            {
                if (excluded != null && excluded.Contains(clip.Id)) continue;
                if (SourceToOutput(clip.Start) is { } a) list.Add((a, clip.Id));
                if (SourceToOutput(clip.End) is { } b) list.Add((b, clip.Id));
            }
        }
        Add("zoom", Project.ZoomClips.Select(x => (x.Id, x.Start, x.End)));
        Add("motion", Project.ScreenMotions.Select(x => (x.Id, x.Start, x.End)));
        Add("cameraMotion", Project.CameraMotions.Select(x => (x.Id, x.Start, x.End)));
        Add("overlay", Project.Overlays.Select(x => (x.Id, x.Start, x.End)));
        Add("blur", Project.BlurClips.Select(x => (x.Id, x.Start, x.End)));
        if (includePlayhead) list.Add((Position, Guid.Empty));
        return snapCandidates = list;
    }

    /// <summary>
    /// Returns the time shift (output seconds) that puts the closest of <paramref name="edges"/> exactly on a snap
    /// target, or 0. Remembers the captured target between calls so it is only released beyond 14 px.
    /// </summary>
    double SnapShift(double[] edges, ICollection<Guid>? excluded, bool includePlayhead)
    {
        if (!SnapActive || Project == null || edges.Length == 0)
        {
            lockedTarget = null; lockedEdge = -1; snapGuide = null;
            return 0;
        }
        var scale = Scale;
        if (lockedTarget is { } target && lockedEdge >= 0 && lockedEdge < edges.Length
            && Math.Abs(target - edges[lockedEdge]) * scale <= SnapReleasePixels)
        {
            snapGuide = target;
            return target - edges[lockedEdge];
        }
        lockedTarget = null; lockedEdge = -1; snapGuide = null;
        var bestDistance = SnapCapturePixels / Math.Max(scale, 1e-9);
        var targets = SnapTargets(excluded, includePlayhead);
        for (var edge = 0; edge < edges.Length; edge++)
            foreach (var candidate in targets)
            {
                var distance = Math.Abs(candidate.Time - edges[edge]);
                if (distance > bestDistance) continue;
                bestDistance = distance; lockedTarget = candidate.Time; lockedEdge = edge;
            }
        if (lockedTarget is { } captured)
        {
            snapGuide = captured;
            return captured - edges[lockedEdge];
        }
        return 0;
    }

    /// <summary>Snaps a single output time (scrub, create, ruler click).</summary>
    double SnapTime(double time, bool includePlayhead)
        => Math.Clamp(time + SnapShift([time], null, includePlayhead), 0, Project?.Timeline.Duration ?? 0);

    /// <summary>Snaps a source-time edge by way of its output position; returns the adjusted source time.</summary>
    double SnapSourceEdge(double sourceTime, double rate, ICollection<Guid>? excluded)
    {
        if (SourceToOutput(sourceTime) is not { } output) return sourceTime;
        return sourceTime + SnapShift([output], excluded, true) * rate;
    }
}
