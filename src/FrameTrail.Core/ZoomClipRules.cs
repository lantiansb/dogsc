namespace FrameTrail;

/// <summary>
/// Zoom-clip rules shared by the editor and the evaluator.
/// Mac: RecorderCore/ZoomTransitionResolution.swift (resolve), EditorStore (zoom clips are stored clips only).
/// </summary>
public static class ZoomClipRules
{
    /// <summary>Gap (source seconds) under which two zoom clips count as adjacent (Mac ZoomInterpolator.adjacencyTolerance).</summary>
    public const double AdjacencyTolerance = 1d / 120;

    /// <summary>
    /// Mac ZoomTransitionResolution.resolve. Clips whose transitions are already expressed in source seconds are fitted so that
    /// an exit never runs into the next clip and the last clip never leaves the end of the output.
    /// Only the evaluated windows change; the authored project ranges stay intact.
    /// Windows has no "preferred" transition fields, so the stored enter/exit durations are the requested ones (0 stays 0).
    /// </summary>
    /// <param name="clips">Clips with Enter/ExitDuration in source seconds.</param>
    /// <param name="sourceEnd">Source time of the output end (end of the last timeline segment).</param>
    public static ZoomClip[] ResolveTransitions(IEnumerable<ZoomClip> clips, double sourceEnd)
    {
        var all = clips.ToArray();
        if (!double.IsFinite(sourceEnd) || sourceEnd <= 0) return all;
        // Clips that start at or after the output end (or are degenerate) are never evaluated; they are passed through untouched
        // so lookups by id keep working, and they do not count as the "next" clip of a visible one.
        var hidden = all.Where(c => !(c.Start < sourceEnd && c.End - c.Start > .001));
        var ordered = all.Where(c => c.Start < sourceEnd && c.End - c.Start > .001).OrderBy(c => c.Start).ThenBy(c => c.Id).ToArray();
        var result = new ZoomClip[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var authored = ordered[i];
            var clip = authored with { };
            var requestedEnter = Math.Max(authored.EnterDuration, 0);
            var requestedExit = Math.Max(authored.ExitDuration, 0);
            if (i + 1 < ordered.Length)
            {
                var gap = Math.Max(ordered[i + 1].Start - clip.End, 0);
                clip.ExitDuration = gap <= AdjacencyTolerance ? 0 : Math.Min(requestedExit, gap);
            }
            else
            {
                var available = Math.Max(sourceEnd - clip.Start, 0);
                // A short final zoom keeps a real entrance and return; a longer one only loses part of its hold.
                var exit = Math.Min(requestedExit, Math.Max(available - Math.Min(requestedEnter, available * .5), 0));
                clip.End = Math.Min(clip.End, sourceEnd - exit);
                if (clip.End < authored.End - 1e-6) clip.ExitProgressOffset = 0;
                clip.ExitDuration = Math.Min(requestedExit, Math.Max(sourceEnd - clip.End, 0));
            }
            clip.EnterDuration = Math.Min(requestedEnter, Math.Max(clip.End - clip.Start, 0));
            result[i] = clip;
        }
        return result.Concat(hidden).ToArray();
    }

    /// <summary>
    /// Old projects (and recordings made with the auto-zoom preference off) keep Settings.AutoZoom = true with no stored clips,
    /// which made the evaluator plan zooms on the fly. Mac keeps zoom animations only as stored clips, so the plan is written into
    /// the project once. Returns true when the project changed.
    /// </summary>
    public static bool MaterializeLegacyAutoZoom(FrameTrailProject project)
    {
        if (project.ZoomClips.Count > 0 || !project.Settings.AutoZoom) return false;
        var planned = AutoZoomPlanner.Plan(project.PointerEvents.Select(e => e with { Time = project.MouseClock.ToSourceTime(e.Time) }));
        // Whether or not anything was planned, the stored lane is now the single source of truth.
        project.Settings.AutoZoom = false;
        project.ZoomClips = planned;
        return true;
    }

    /// <summary>
    /// Edit normalisation (F-01): when an edit empties a lane that held zoom clips, auto zoom is switched off so the evaluator
    /// fallback cannot re-plan ghost zooms. Applies to every edit path (inspector, live edit commit, timeline).
    /// </summary>
    public static void NormalizeAfterEdit(int zoomClipCountBefore, FrameTrailProject after)
    {
        if (zoomClipCountBefore > 0 && after.ZoomClips.Count == 0) after.Settings.AutoZoom = false;
    }
}
