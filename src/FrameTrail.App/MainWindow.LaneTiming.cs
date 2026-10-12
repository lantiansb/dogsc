namespace FrameTrail.App;

public partial class MainWindow
{
    /// <summary>
    /// T-16 (Mac EditorTimelineView.adjustedTiming): typing a start / length into the inspector of a zoom, screen 3D or camera motion
    /// clip is placed by the same lane rules as a timeline drag. The clip stays between its neighbours (touching, or leaving room for
    /// the neighbour's return window); when no legal placement exists the change is rejected with a notice and the field is restored.
    /// <paramref name="proposedStart"/> / <paramref name="proposedEnd"/> are source seconds.
    /// </summary>
    private void ApplyLaneTiming(FrameTrailProject p, string kind, Guid id, EffectLaneEdit mode, double proposedStart, double proposedEnd,
        Action<double, double> set)
    {
        var lane = EffectLaneSupport.Items(p, kind);
        var fit = EffectLaneRules.Adjust(lane, id, mode, proposedStart, proposedEnd, EffectLaneSupport.Limit(p),
            EffectLaneSupport.MinimumDuration(kind), EffectLaneSupport.DefaultTail(kind));
        if (fit is not { } placed)
        {
            ShowNotice("此处放不下：同一轨道上的相邻片段会挡住它。", warning: true);
            // Put the rejected number back (the field shows what was typed).
            Dispatcher.BeginInvoke(new Action(() => { if (project is not null) BuildInspector(); }), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        set(placed.Start, placed.Duration);
    }
}
