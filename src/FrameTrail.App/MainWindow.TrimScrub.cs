namespace FrameTrail.App;

public partial class MainWindow
{
    /// <summary>
    /// T-11 (Mac EditorTimelinePrimaryEditing trim scrubbing): while an edge of the main clip is dragged, the preview follows the handle.
    /// The left handle shows the new first frame of the clip, the right handle shows the new last frame. Rendering is coalesced
    /// (held-drag semantics) and nothing is written to history or disk; the release commits the trim as one undo step.
    /// </summary>
    private void PreviewTrimScrub(TimelineChange change)
    {
        if (project is null) return;
        var frame = 1d / Math.Max(1, project.Media.FrameRate);
        double? at = null;
        try
        {
            var probe = Clone(project);
            ApplyChange(probe, change);
            probe.Timeline.Validate();
            var elapsed = 0d;
            foreach (var segment in probe.Timeline.Segments)
            {
                if (segment.Id == change.Selection.Id)
                {
                    at = change.Operation == "trim-start" ? elapsed : Math.Max(elapsed, elapsed + segment.OutputDuration - frame);
                    break;
                }
                elapsed += segment.OutputDuration;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        if (at is null) return;
        livePreviewTime = at;
        try { PreviewLiveEdit(p => ApplyChange(p, change)); }
        finally { livePreviewTime = null; }
    }
}
