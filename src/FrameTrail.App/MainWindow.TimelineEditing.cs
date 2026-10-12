using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Timeline interaction glue: selection model, effect creation, zoom / scroll / view memory, markers, cut seams,
/// shortcuts helpers. Mac references: EditorTimelineView (selection, reveal), EditorTimelineZoomTransport (zoom, create),
/// EditorTimelineRecordingMarkers, EditorTimelinePrimaryEditing (seams), EditorView (Alt+X export).
/// </summary>
public partial class MainWindow
{
    private bool zoomSyncing, restoringTimelineView;
    private DispatcherTimer? timelineViewSaveTimer;
    private Guid? timelineViewProjectId;
    private ShortcutCheatsheetWindow? shortcutCheatsheet;
    private bool inspectorHidden;

    // ---- selection ---------------------------------------------------------------------------------------------------

    /// <summary>A click / Ctrl+click / right-click on a clip made it the current selection.</summary>
    private void OnTimelineSelectionRequested(TimelineSelection selection)
    {
        selectedEffect = selection.Kind == "screen" ? null : selection;
        selectedScreenSegmentId = selection.Kind == "screen" ? selection.Id : null;
        if (selection.Kind == "screen")
        {
            inspectorTab = "audio";
            refreshing = true;
            Clips.SelectedItem = Clips.Items.Cast<ClipItem>().FirstOrDefault(x => x.Id == selection.Id);
            refreshing = false;
        }
        else
        {
            inspectorTab = selection.Kind switch
            {
                "camera" or "cameraMotion" => "camera",
                "audio" => "audio",
                "overlay" => "overlay",
                "blur" => "blur",
                _ => "zoom"
            };
            motionTab = selection.Kind == "motion" ? "motion" : "zoom";
        }
        BuildInspector();
        UpdateTimelineCommandStates();
    }

    /// <summary>Click on empty timeline space: nothing is selected any more (Mac clearTimelineSelection).</summary>
    private void OnTimelineSelectionCleared()
    {
        selectedEffect = null;
        selectedScreenSegmentId = null;
        BuildInspector();
        UpdateTimelineCommandStates();
    }

    /// <summary>Programmatic selection (after creating / pasting / splitting) through the same path as a click.</summary>
    private void SelectTimelineItem(TimelineSelection selection)
    {
        TimelineView.Selection = selection;
        TimelineView.MultiSelection.Clear();
        TimelineView.MultiSelection.Add(selection);
        OnTimelineSelectionRequested(selection);
        TimelineView.InvalidateVisual();
    }

    private void ClearTimelineSelectionState()
    {
        selectedEffect = null;
        selectedScreenSegmentId = null;
        TimelineView.Selection = null;
        TimelineView.MultiSelection.Clear();
        TimelineView.InvalidateVisual();
        UpdateTimelineCommandStates();
    }

    /// <summary>Drops selections whose items no longer exist (after delete / undo / split) and keeps the host fields in sync.</summary>
    private void ReconcileTimelineSelection()
    {
        if (project is null)
        {
            selectedEffect = null; selectedScreenSegmentId = null;
            TimelineView.Selection = null; TimelineView.MultiSelection.Clear();
            return;
        }
        if (selectedEffect is { } effect && !SelectionExists(project, effect)) selectedEffect = null;
        if (selectedScreenSegmentId is { } segmentId && project.Timeline.Segments.All(x => x.Id != segmentId)) selectedScreenSegmentId = null;
        if (selectedEffect != null) TimelineView.Selection = selectedEffect;
        else if (selectedScreenSegmentId is { } id) TimelineView.Selection = new("screen", id);
        else if (TimelineView.Selection is { } current && !SelectionExists(project, current)) TimelineView.Selection = null;
        var stale = TimelineView.MultiSelection.Where(x => !SelectionExists(project, x)).ToList();
        foreach (var item in stale) TimelineView.MultiSelection.Remove(item);
    }

    private void UpdateTimelineCommandStates()
    {
        if (DeleteSelectionButton is null || TimelineView is null) return;
        DeleteSelectionButton.IsEnabled = project is not null && (TimelineView.MultiSelection.Count > 0 || CurrentTimelineSelection() is not null);
    }

    /// <summary>
    /// Mac EditorTimelineSelectionReveal: a selected effect must stay inspectable. If the playhead is already inside the
    /// clip nothing moves; otherwise return the first useful authored state (output seconds).
    /// </summary>
    private double? RevealTimeForSelection(TimelineSelection selection)
    {
        if (project is null) return null;
        double start, end, preferred;
        switch (selection.Kind)
        {
            case "zoom" when project.ZoomClips.FirstOrDefault(x => x.Id == selection.Id) is { } zoom:
                start = zoom.Start; end = zoom.End; preferred = zoom.Start + Math.Min(zoom.EnterDuration, zoom.End - zoom.Start); break;
            case "motion" when project.ScreenMotions.FirstOrDefault(x => x.Id == selection.Id) is { } motion:
                start = motion.Start; end = motion.End; preferred = motion.Start + Math.Min(motion.EnterDuration, motion.Duration); break;
            case "cameraMotion" when project.CameraMotions.FirstOrDefault(x => x.Id == selection.Id) is { } cameraMotion:
                start = cameraMotion.Start; end = cameraMotion.End; preferred = cameraMotion.Start + Math.Min(cameraMotion.EnterDuration, cameraMotion.Duration); break;
            case "overlay" when project.Overlays.FirstOrDefault(x => x.Id == selection.Id) is { } overlay:
                start = overlay.Start; end = overlay.End; preferred = overlay.Start; break;
            case "blur" when project.BlurClips.FirstOrDefault(x => x.Id == selection.Id) is { } blur:
                start = blur.Start; end = blur.End; preferred = blur.Start; break;
            default: return null;
        }
        if (project.Timeline.MapSourceToOutput(Math.Max(0, start)) is not { } outputStart) return null;
        var outputEnd = project.Timeline.MapSourceToOutput(Math.Max(0, end)) ?? project.Timeline.Duration;
        var playhead = Playhead.Value;
        if (playhead >= outputStart - 1e-9 && playhead < outputEnd) return null;
        if (outputEnd <= outputStart) return outputStart;
        var preferredOutput = project.Timeline.MapSourceToOutput(Math.Max(0, preferred)) ?? outputStart;
        return Math.Min(Math.Max(preferredOutput, outputStart), outputEnd - .001);
    }

    private void OnTimelineSelectionActivated(TimelineSelection selection)
    {
        if (RevealTimeForSelection(selection) is { } time) SeekToOutputTime(time);
    }

    /// <summary>Moves the playhead (stopping playback, rendering the frame) and scrolls it into view.</summary>
    internal void SeekToOutputTime(double time)
    {
        if (project is null) return;
        time = Math.Clamp(time, 0, project.Timeline.Duration);
        SeekTimeline(time);
        TimelineView.Position = time;
        TimelineView.RevealPlayhead();
    }

    /// <summary>Scrolls the timeline so the playhead is visible (Mac revealPlayheadIfOutsideViewport).</summary>
    internal void RevealTimelinePlayhead()
    {
        if (TimelineView is null) return;
        TimelineView.Position = Playhead.Value;
        TimelineView.RevealPlayhead();
    }

    private void TogglePlayback()
    {
        PlayClick(this, new RoutedEventArgs());
        // Mac makeFirstResponder(nil): playback keys keep working and a focused button is not re-triggered by Space.
        TimelineView.Focus();
        Dispatcher.BeginInvoke(RevealTimelinePlayhead, DispatcherPriority.Background);
    }

    private void PlayButtonClick(object sender, RoutedEventArgs e) => TogglePlayback();

    // ---- zoom, scroll and per-project view memory --------------------------------------------------------------------

    private static double ZoomFromSlider(double value) => Math.Exp(Math.Clamp(value, 0, 1) * Math.Log(EditorTimelineControl.MaxZoom));
    private static double SliderFromZoom(double zoom) => Math.Log(Math.Clamp(zoom, 1, EditorTimelineControl.MaxZoom)) / Math.Log(EditorTimelineControl.MaxZoom);

    /// <summary>Zooms around a control-space x (the pointer) so the time under it stays put (Mac zoomTimeline(to:pointerViewportX:)).</summary>
    internal void ZoomTimelineTo(double zoom, double? anchorX = null)
    {
        var view = TimelineView;
        if (view is null) return;
        zoom = Math.Clamp(zoom, 1, EditorTimelineControl.MaxZoom);
        var label = EditorTimelineControl.LabelColumnWidth;
        var viewport = view.ViewportWidth;
        var anchor = Math.Clamp((anchorX ?? label + viewport / 2) - label, 0, viewport);
        var anchorTime = (anchor + view.Offset) / Math.Max(view.Scale, 1e-9);
        view.Zoom = zoom;
        view.Offset = Math.Clamp(anchorTime * view.Scale - anchor, 0, view.MaximumOffset);
        zoomSyncing = true;
        try { TimelineZoom.Value = SliderFromZoom(zoom); }
        finally { zoomSyncing = false; }
        UpdateTimelineScrollMetrics();
        SaveTimelineViewSoon();
    }

    /// <summary>Lightweight metrics refresh for zoom / scroll / resize; never rebuilds the inspector.</summary>
    private void UpdateTimelineScrollMetrics()
    {
        if (TimelineView is null || TimelineScroll is null) return;
        var viewport = Math.Max(1, TimelineView.ViewportWidth);
        TimelineScroll.Maximum = TimelineView.MaximumOffset;
        TimelineScroll.ViewportSize = viewport;
        TimelineScroll.LargeChange = viewport * .9;
        TimelineScroll.SmallChange = Math.Max(8, viewport * .05);
        var offset = Math.Clamp(TimelineView.Offset, 0, TimelineView.MaximumOffset);
        TimelineView.Offset = offset;
        if (Math.Abs(TimelineScroll.Value - offset) > .01) TimelineScroll.Value = offset;
        TimelineView.InvalidateVisual();
    }

    private void RestoreTimelineViewState()
    {
        if (project is null) { timelineViewProjectId = null; return; }
        if (timelineViewProjectId == project.ProjectId) return;
        timelineViewProjectId = project.ProjectId;
        var saved = TimelineViewPreferences.Load(project.ProjectId);
        TimelineView.HiddenTracks.Clear();
        if (saved != null) foreach (var track in saved.HiddenTracks) TimelineView.HiddenTracks.Add(track);
        TimelineView.Zoom = 1; TimelineView.Offset = 0;
        restoringTimelineView = true;
        try { ZoomTimelineTo(saved?.Zoom ?? 1, EditorTimelineControl.LabelColumnWidth); }
        finally { restoringTimelineView = false; }
    }

    private void SaveTimelineViewSoon()
    {
        if (restoringTimelineView || project is null) return;
        timelineViewSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timelineViewSaveTimer.Tick -= SaveTimelineViewNow;
        timelineViewSaveTimer.Tick += SaveTimelineViewNow;
        timelineViewSaveTimer.Stop();
        timelineViewSaveTimer.Start();
    }

    private void SaveTimelineViewNow(object? sender, EventArgs e)
    {
        timelineViewSaveTimer?.Stop();
        if (project is null || timelineViewProjectId != project.ProjectId) return;
        TimelineViewPreferences.Save(project.ProjectId, TimelineView.Zoom, TimelineView.HiddenTracks);
    }

    private void SplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TimelineRow.Height = new GridLength(332);
        e.Handled = true;
    }

    /// <summary>Export range follows edits: clamped to the new duration, cleared when nothing is left.</summary>
    private void ClampExportRange()
    {
        if (project is null) return;
        var duration = project.Timeline.Duration;
        if (TimelineView.RangeEnd is { } end && end > duration) TimelineView.RangeEnd = duration;
        if (TimelineView.RangeStart is { } start && TimelineView.RangeEnd is { } clampedEnd && start >= clampedEnd - 1e-6)
        {
            TimelineView.RangeStart = null; TimelineView.RangeEnd = null;
        }
    }

    // ---- restore mode / reset ---------------------------------------------------------------------------------------

    private void RestoreModeClick(object sender, RoutedEventArgs e)
    {
        TimelineView.RestoreMode = !TimelineView.RestoreMode;
        UpdateTimelineDisplayButtons();
        TimelineView.InvalidateVisual();
        if (TimelineView.RestoreMode) ShowNotice("恢复剪辑模式：点击标尺上的按钮可合并或还原剪切缝。");
    }

    private void ResetTimelineToOriginal()
    {
        if (project is null) return;
        var answer = SurfaceDialog.Show(this,
            "这会撤销所有分割、重排、变速和片段声音设置，把时间线恢复为一整段原始录制。效果片段不会删除，之后可用 Ctrl+Z 撤销。",
            "全部恢复为原始录制", MessageBoxButton.YesNo, MessageBoxImage.Warning, "恢复", "取消", preferCancel: true);
        if (answer != MessageBoxResult.Yes) return;
        ApplyEdit(p => p.Timeline.Segments = [new() { SourceDuration = p.Media.Duration }], "恢复为原始录制");
    }

    private void OnTimelineSeamRequested(string kind, int index)
    {
        if (project is null) return;
        var segments = project.Timeline.Segments;
        _ = Guard(() =>
        {
            Guid selectAfter = Guid.Empty;
            switch (kind)
            {
                case "merge" when index >= 0 && index + 1 < segments.Count:
                {
                    var previous = segments[index].Id; var next = segments[index + 1].Id;
                    Edit(p => TimelineSeamOperations.MergeAdjacent(p.Timeline, previous, next), "合并片段");
                    selectAfter = previous;
                    break;
                }
                case "restore" when index >= 0 && index + 1 < segments.Count:
                {
                    var previous = segments[index].Id; var next = segments[index + 1].Id;
                    Edit(p => selectAfter = TimelineSeamOperations.RestoreGap(p.Timeline, previous, next), "还原剪切");
                    break;
                }
                case "leading":
                    Edit(p => selectAfter = TimelineSeamOperations.RestoreLeading(p.Timeline), "还原开头剪辑");
                    break;
                case "trailing":
                    Edit(p => selectAfter = TimelineSeamOperations.RestoreTrailing(p.Timeline, p.Media.Duration), "还原结尾剪辑");
                    break;
            }
            if (selectAfter != Guid.Empty && project!.Timeline.Segments.Any(x => x.Id == selectAfter)) SelectScreenSegment(selectAfter);
            return Task.CompletedTask;
        });
    }

    // ---- markers -----------------------------------------------------------------------------------------------------

    private void JumpToMarker(Guid id)
    {
        if (project is null || project.Markers.FirstOrDefault(x => x.Id == id) is not { } marker) return;
        if (project.Timeline.MapSourceToOutput(marker.SourceTime) is not { } time)
        {
            ShowNotice("此标记所在素材已从时间线裁去。", true);
            return;
        }
        SeekTimeline(time);
        TimelineView.Position = time;
        TimelineView.RevealTime(time);
    }

    private void MarkerContextMenu(Guid id)
    {
        if (project is null) return;
        var menu = new ContextMenu();
        var jump = new MenuItem { Header = "跳转到此标记" };
        jump.Click += (_, _) => JumpToMarker(id);
        var delete = new MenuItem { Header = "删除录制标记" };
        delete.Click += (_, _) => ApplyEdit(p => p.Markers.RemoveAll(x => x.Id == id), "删除标记");
        menu.Items.Add(jump); menu.Items.Add(delete);
        menu.PlacementTarget = TimelineView; menu.IsOpen = true;
    }

    // ---- effect creation (timeline gestures and inspector buttons) ----------------------------------------------------

    /// <summary>F-07 (Mac project.motion.defaultZoomScale ?? AppPreferences.rememberedZoomCreationScale ?? 1.6).</summary>
    private double DefaultZoomScale()
    {
        if (project?.Settings.DefaultZoomScale is { } projectScale && double.IsFinite(projectScale)) return Math.Clamp(projectScale, 1, 6);
        if (ReadUserPreferences().ZoomCreationScale is { } remembered && double.IsFinite(remembered)) return Math.Clamp(remembered, 1, 6);
        return 1.6;
    }

    /// <summary>Mac AppPreferences.rememberZoomCreationScale: stored only after a committed edit changed a zoom's scale (never by undo).</summary>
    private static void RememberZoomCreationScale(double scale)
    {
        if (!double.IsFinite(scale)) return;
        try
        {
            var preferences = ReadUserPreferences();
            var value = Math.Clamp(scale, 1, 6);
            if (preferences.ZoomCreationScale is { } existing && Math.Abs(existing - value) < 1e-9) return;
            preferences.ZoomCreationScale = value;
            SaveUserPreferences(preferences);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static double SegmentRateAt(FrameTrailProject p, double outputTime)
    {
        var elapsed = 0d;
        foreach (var segment in p.Timeline.Segments)
        {
            elapsed += segment.OutputDuration;
            if (outputTime < elapsed) return segment.PlaybackRate;
        }
        return p.Timeline.Segments.Count > 0 ? p.Timeline.Segments[^1].PlaybackRate : 1;
    }

    /// <summary>Default clip length in output seconds when a lane is clicked (Mac fitMotionTiming / createZoomRange).</summary>
    private static double DefaultClickLength(string kind, double outputDuration) => kind switch
    {
        "zoom" => 1.5,
        "motion" or "cameraMotion" => Math.Min(1.2, Math.Max(outputDuration * .15, .5)),
        _ => 3
    };

    // ---- default-parameter factories (EffectDefaults) -----------------------------------------------------------------
    // The lane fit (EffectLaneRules) decides start / end; EffectDefaults only supplies the Mac parameter defaults.

    /// <summary>Mac createZoomRange: scale from the picker (default 1.6), focus at the pointer under the playhead, otherwise centred.</summary>
    private ZoomClip NewZoomFromDefaults(FrameTrailProject p, Guid id, EffectLaneFit fit, double length)
    {
        var sourceNow = Math.Clamp(fit.Start, 0, Math.Max(p.Media.Duration, 0));
        NormalizedPoint? focus = EffectDefaults.PointerLocationAt(p.PointerEvents, sourceNow) is { } pointer ? ToCropLocalFocus(p, pointer) : null;
        var clip = EffectDefaults.NewManualZoom(fit.Start, Math.Max(p.Media.Duration, fit.End), focus, DefaultZoomScale());
        return clip with
        {
            Id = id, Start = fit.Start, End = fit.End,
            EnterDuration = Math.Min(clip.EnterDuration, length), ExitDuration = Math.Min(fit.Tail, length)
        };
    }

    private static ScreenMotionClip NewScreenMotionFromDefaults(FrameTrailProject p, Guid id, EffectLaneFit fit, double length, double nextStart)
    {
        var clip = EffectDefaults.NewScreenMotion(fit.Start, Math.Max(p.Media.Duration, fit.End));
        var enter = Math.Min(clip.EnterDuration, length);
        return clip with
        {
            Id = id, Start = fit.Start, Duration = length, EnterDuration = enter,
            ReturnDuration = EffectLaneRules.FitTailBeforeSuccessor(fit.End, Math.Min(clip.ReturnDuration, length), nextStart)
        };
    }

    private OverlayClip NewOverlayFromDefaults(FrameTrailProject p, string media, Guid id, EffectLaneFit fit, double length)
    {
        var canvasAspect = p.Settings.CanvasHeight > 0 ? (double)p.Settings.CanvasWidth / p.Settings.CanvasHeight : 16d / 9;
        var clip = EffectDefaults.NewOverlay(media, fit.Start, Math.Max(p.Media.Duration, fit.End), ImageAspectOf(media), canvasAspect, p.Overlays);
        return clip with
        {
            Id = id, Start = fit.Start, Duration = length,
            EnterDuration = Math.Min(clip.EnterDuration, length / 2), ExitDuration = Math.Min(clip.ExitDuration, length / 2)
        };
    }

    private static BlurClip NewBlurFromDefaults(FrameTrailProject p, Guid id, EffectLaneFit fit, double length)
    {
        var clip = EffectDefaults.NewBlurClip(fit.Start, Math.Max(p.Media.Duration, fit.End));
        return clip with
        {
            Id = id, Start = fit.Start, Duration = length, SourceSpace = true,
            EnterDuration = Math.Min(clip.EnterDuration, length / 2), ExitDuration = Math.Min(clip.ExitDuration, length / 2)
        };
    }

    /// <summary>Pixel aspect of an imported sticker image; 1 when it cannot be decoded (e.g. webp without a codec).</summary>
    private double ImageAspectOf(string relativePath)
    {
        try
        {
            if (projectFile is null) return 1;
            using var stream = File.OpenRead(FrameTrailProjectStore.ResolveAssetPath(projectFile, relativePath));
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            return frame.PixelHeight > 0 ? (double)frame.PixelWidth / frame.PixelHeight : 1;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException or System.IO.FileFormatException or ArgumentException) { return 1; }
    }

    private void OnTimelineCreateRequested(string kind, double outputStart, double? outputEnd) => CreateEffectClip(kind, outputStart, outputEnd);

    /// <summary>
    /// The single entry for adding a zoom / screen 3D / camera motion / sticker / blur clip. A click (no end) creates the
    /// default length, a drag creates the range. An existing clip of an exclusive lane at the start time is selected
    /// instead; a gap that cannot hold the clip reports "此处没有足够空间" (Mac fitZoomAnimation / fitMotionTiming).
    /// </summary>
    internal void CreateEffectClip(string kind, double outputStart, double? outputEnd = null, string? overlayMedia = null)
    {
        if (project is null || !EffectLaneSupport.IsCreatable(kind)) return;
        _ = Guard(() => { CreateEffectClipCore(kind, outputStart, outputEnd, overlayMedia); return Task.CompletedTask; });
    }

    private void CreateEffectClipCore(string kind, double outputStart, double? outputEnd, string? overlayMedia)
    {
        var p = project!;
        if (kind == "cameraMotion" && p.Camera is null) throw new InvalidOperationException("请先添加摄像头轨道。");
        var duration = p.Timeline.Duration;
        var startOutput = Math.Clamp(outputStart, 0, duration);
        var startSource = p.Timeline.MapOutputToSource(startOutput);
        var limit = EffectLaneSupport.Limit(p);
        var exclusive = EffectLaneSupport.IsExclusive(kind);
        var lane = EffectLaneSupport.Items(p, kind);

        if (outputEnd is null && exclusive && EffectLaneRules.FindAt(lane, startSource) is { } existing)
        {
            var selection = new TimelineSelection(kind, existing.Id);
            SelectTimelineItem(selection);
            if (RevealTimeForSelection(selection) is { } time) SeekToOutputTime(time);
            return;
        }

        var rate = SegmentRateAt(p, startOutput);
        var click = DefaultClickLength(kind, duration) * rate;
        var endSource = outputEnd is { } end ? p.Timeline.MapOutputToSource(Math.Clamp(end, 0, duration)) : startSource;
        var minimum = EffectLaneSupport.CreateMinimumDuration(kind);
        EffectLaneFit fit;
        if (exclusive)
        {
            fit = EffectLaneRules.FitNew(lane, startSource, endSource, limit, click, minimum, EffectLaneSupport.DefaultTail(kind))
                ?? throw new InvalidOperationException("此处没有足够空间");
        }
        else
        {
            var from = Math.Min(startSource, endSource);
            var to = outputEnd is null ? from + click : Math.Max(startSource, endSource);
            from = Math.Max(0, Math.Min(from, limit - minimum));
            to = Math.Min(limit, Math.Max(to, from + minimum));
            if (to - from < minimum - 1e-6) throw new InvalidOperationException("此处没有足够空间");
            fit = new EffectLaneFit(from, to, 0);
        }

        if (kind == "overlay" && overlayMedia is null)
        {
            ImportImageAsset(media => CreateEffectClip(kind, outputStart, outputEnd, media));
            return;
        }

        var id = Guid.NewGuid();
        var length = fit.Duration;
        double nextStart = lane.Where(x => x.Start >= fit.End - 1e-4).Select(x => x.Start).DefaultIfEmpty(limit).Min();
        Action<FrameTrailProject> add = kind switch
        {
            "zoom" => x => x.ZoomClips.Add(NewZoomFromDefaults(p, id, fit, length)),
            "motion" => x => x.ScreenMotions.Add(NewScreenMotionFromDefaults(p, id, fit, length, nextStart)),
            "cameraMotion" => x =>
            {
                var camera = x.Camera!;
                // Mac addCameraMotion: layout / position / roundness of the base camera, size x1.25 (max 1), full opacity.
                x.CameraMotions.Add(new CameraMotionClip
                {
                    Id = id, Start = fit.Start, Duration = length, TargetPosition = camera.Position, TargetSize = Math.Min(1, camera.Size * 1.25),
                    TargetLayout = camera.Layout, TargetRoundness = camera.Layout is null ? null : CameraRoundness(camera), TargetOpacity = 1,
                    EnterDuration = Math.Min(.35, length),
                    ReturnDuration = EffectLaneRules.FitTailBeforeSuccessor(fit.End, Math.Min(.35, length), nextStart)
                });
            },
            "overlay" => x => x.Overlays.Add(NewOverlayFromDefaults(p, overlayMedia!, id, fit, length)),
            _ => x => x.BlurClips.Add(NewBlurFromDefaults(p, id, fit, length))
        };

        TimelineView.HiddenTracks.Remove(kind);
        SaveTimelineViewSoon();
        selectedEffect = new(kind, id);
        Edit(add, "添加" + EffectLaneSupport.KindName(kind));
        if (project is null || !SelectionExists(project, new(kind, id))) return;
        var created = new TimelineSelection(kind, id);
        SelectTimelineItem(created);
        TimelineView.Refresh();
        if (RevealTimeForSelection(created) is { } reveal) SeekToOutputTime(reveal);
        else RevealTimelinePlayhead();
    }

    // ---- misc commands -----------------------------------------------------------------------------------------------

    internal void ShowShortcutCheatsheet()
    {
        if (shortcutCheatsheet is { IsLoaded: true }) { shortcutCheatsheet.Activate(); return; }
        shortcutCheatsheet = new ShortcutCheatsheetWindow(this);
        shortcutCheatsheet.Closed += (_, _) => shortcutCheatsheet = null;
        shortcutCheatsheet.Show();
    }

    private void ToggleInspector()
    {
        inspectorHidden = !inspectorHidden;
        ApplyInspectorWidth();
        ShowNotice(inspectorHidden ? "已隐藏检查器（Ctrl+Alt+I 再次显示）" : "已显示检查器");
    }

    private void ApplyInspectorWidth()
    {
        if (InspectorColumn is null) return;
        if (inspectorHidden) { InspectorColumn.Width = new GridLength(0); return; }
        var compression = Math.Clamp(Math.Max((1440 - ActualWidth) / 360, (820 - ActualHeight) / 220), 0, 1);
        InspectorColumn.Width = new GridLength(Math.Clamp(ActualWidth * .23, 340, 416) * (1 - .14 * compression));
    }

    /// <summary>Alt+X: export the span of the selected primary segments (Mac EditorView exportSelectedRange).</summary>
    private void ExportSelectedPrimaryRange()
    {
        if (project is null) return;
        var ids = TimelineView.MultiSelection.Where(x => x.Kind == "screen").Select(x => x.Id).ToHashSet();
        if (ids.Count == 0 && TimelineView.Selection is { Kind: "screen" } single) ids.Add(single.Id);
        if (ids.Count == 0)
        {
            ShowNotice("请先选择一个或多个主片段，再按 Alt+X 导出选区。", true);
            return;
        }
        double? start = null, end = null;
        var elapsed = 0d;
        foreach (var segment in project.Timeline.Segments)
        {
            var next = elapsed + segment.OutputDuration;
            if (ids.Contains(segment.Id))
            {
                start = Math.Min(start ?? elapsed, elapsed);
                end = Math.Max(end ?? next, next);
            }
            elapsed = next;
        }
        if (start is not { } from || end is not { } to || to <= from) return;
        var previous = (TimelineView.RangeStart, TimelineView.RangeEnd);
        TimelineView.RangeStart = from; TimelineView.RangeEnd = to;
        TimelineView.InvalidateVisual();
        _ = Guard(async () =>
        {
            try { await ChooseExport(false); }
            finally
            {
                (TimelineView.RangeStart, TimelineView.RangeEnd) = previous;
                TimelineView.InvalidateVisual();
            }
        });
    }

    private void WindowKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.LeftAlt or Key.RightAlt)) return;
        TimelineView.RefreshHoverGuide();
        // A lone Alt press / release would otherwise activate the (hidden) system menu over the timeline.
        if (TimelineView.IsMouseOver) e.Handled = true;
    }
}
