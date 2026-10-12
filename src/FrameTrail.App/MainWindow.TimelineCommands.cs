using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FrameTrail.App;

public partial class MainWindow
{
    private async void SplitClick(object sender, RoutedEventArgs e) => await Guard(SplitAtPlayhead);
    private async void TrimLeftClick(object sender, RoutedEventArgs e) => await Guard(() => TrimAtPlayhead(true));
    private async void TrimRightClick(object sender, RoutedEventArgs e) => await Guard(() => TrimAtPlayhead(false));

    private Task SplitAtPlayhead()
    {
        if (project is null) return Task.CompletedTask;
        var selection = CurrentTimelineSelection();
        if (selection is not null && selection.Kind != "screen")
        {
            if (ToTimelineEffectKind(selection.Kind) is not { } kind)
            {
                Status.Text = selection.Kind is "camera" or "audio"
                    ? "S 暂不拆分摄像头或音轨；请选中主片段、缩放、屏幕 3D、摄像头运动、贴图或柔化片段。"
                    : "当前选择不支持分割。";
                return Task.CompletedTask;
            }

            var sourceTime = project.Timeline.MapOutputToSource(EditOutputTime());
            var span = Span(project, selection);
            var minimumSide = kind switch
            {
                TimelineEffectKind.Zoom => .16,
                TimelineEffectKind.ScreenMotion or TimelineEffectKind.CameraMotion => .05,
                _ => .01
            };
            if (span is not { } effect || sourceTime - effect.Start < minimumSide || effect.Start + effect.Duration - sourceTime < minimumSide)
            {
                Status.Text = $"所选效果分割点两侧都至少需要 {minimumSide:0.##} 秒。";
                return Task.CompletedTask;
            }

            var rightEffectId = Guid.Empty;
            Edit(p => rightEffectId = EffectTimelineOperations.Split(p, kind, selection.Id, sourceTime), "分割" + EffectLaneSupport.KindName(selection.Kind));
            if (rightEffectId != Guid.Empty)
            {
                selectedEffect = new(selection.Kind, rightEffectId);
                TimelineView.Selection = selectedEffect;
                TimelineView.MultiSelection.Clear();
                TimelineView.MultiSelection.Add(selectedEffect);
                RefreshEditorShell();
                RevealTimelinePlayhead();
            }
            return Task.CompletedTask;
        }

        return SplitPrimaryAt(EditOutputTime());
    }

    /// <summary>Splits the primary segment under an output time and selects the right half (Mac splitPrimaryAt; also used by Alt+click).</summary>
    private Task SplitPrimaryAt(double outputTime)
    {
        if (project is null) return Task.CompletedTask;
        if (!TryFindScreenSegmentAtOutput(project, outputTime, out var segment, out var segmentOutputStart))
        {
            Status.Text = "播放头位置没有可分割的主片段。";
            return Task.CompletedTask;
        }

        var source = segment.SourceStart + (outputTime - segmentOutputStart) * segment.PlaybackRate;
        var frame = 1d / Math.Max(1, project.Media.FrameRate);
        var outputOffset = outputTime - segmentOutputStart;
        if (outputOffset < frame || segment.OutputDuration - outputOffset < frame)
        {
            Status.Text = "请将切分点放在主片段内部，并在两端各留出至少一帧。";
            return Task.CompletedTask;
        }

        var rightSegmentId = Guid.Empty;
        Edit(p => rightSegmentId = p.Timeline.Split(segment.Id, source).Right.Id, "分割片段");
        SelectScreenSegment(rightSegmentId);
        if (Math.Abs(Playhead.Value - outputTime) > 1e-6) SeekToOutputTime(outputTime);
        else RevealTimelinePlayhead();
        return Task.CompletedTask;
    }

    private Task TrimAtPlayhead(bool leading)
    {
        if (project is null) return Task.CompletedTask;
        var selection = CurrentTimelineSelection();
        if (selection is not null && selection.Kind != "screen")
        {
            if (ToTimelineEffectKind(selection.Kind) is not { } kind)
            {
                Status.Text = selection.Kind is "camera" or "audio"
                    ? "Q/W 暂不裁切摄像头或音轨；请选中主片段或定时效果片段。"
                    : "当前选择不支持 Q/W 裁切。";
                return Task.CompletedTask;
            }

            var sourceTime = project.Timeline.MapOutputToSource(EditOutputTime());
            var span = Span(project, selection);
            var minimumEdge = .01;
            var minimumRemaining = kind switch
            {
                TimelineEffectKind.Zoom => .16,
                TimelineEffectKind.ScreenMotion or TimelineEffectKind.CameraMotion => .08,
                _ => .01
            };
            if (span is not { } effect || sourceTime <= effect.Start + minimumEdge || sourceTime >= effect.Start + effect.Duration - minimumEdge
                || (leading ? effect.Start + effect.Duration - sourceTime : sourceTime - effect.Start) < minimumRemaining)
            {
                Status.Text = $"播放头需离效果边缘超过 {minimumEdge:0.##} 秒，修剪后至少保留 {minimumRemaining:0.##} 秒。";
                return Task.CompletedTask;
            }

            var newStart = leading ? sourceTime : effect.Start;
            var newDuration = leading ? effect.Start + effect.Duration - sourceTime : sourceTime - effect.Start;
            Edit(p => ApplyOneTimelineChange(p, new(selection, leading ? "trim-start" : "trim-end", newStart, newDuration)), (leading ? "Q 修剪" : "W 修剪") + EffectLaneSupport.KindName(selection.Kind));
            // Mac seeks to the new edge after Q / W on an effect.
            if (project.Timeline.MapSourceToOutput(Math.Max(0, leading ? newStart : newStart + newDuration)) is { } edge) SeekToOutputTime(edge);
            return Task.CompletedTask;
        }

        var outputTime = EditOutputTime();
        if (!TryFindScreenSegmentAtOutput(project, outputTime, out var segment, out var segmentOutputStart))
        {
            Status.Text = "播放头位置没有可裁切的主片段。";
            return Task.CompletedTask;
        }

        var source = segment.SourceStart + (outputTime - segmentOutputStart) * segment.PlaybackRate;
        var frame = 1d / Math.Max(1, project.Media.FrameRate);
        var outputOffset = outputTime - segmentOutputStart;
        var remainingOutput = segment.OutputDuration - outputOffset;
        if (leading ? outputOffset <= frame / 2 || remainingOutput < frame
                    : outputOffset < frame || remainingOutput <= frame / 2)
        {
            Status.Text = "播放头离片段边缘过近，无法保留至少一帧的内容。";
            return Task.CompletedTask;
        }

        var nextPlayhead = leading ? segmentOutputStart : outputTime;
        Edit(p =>
        {
            var newStart = leading ? source : segment.SourceStart;
            var newEnd = leading ? segment.SourceEnd : source;
            RecordingMarkerRules.MoveMarkersToTrimEdges(p, segment.Id, newStart, newEnd);
            p.Timeline.Trim(segment.Id, newStart, newEnd);
        }, leading ? "Q 删除前段" : "W 删除后段");
        SelectScreenSegment(segment.Id);
        Playhead.Value = Math.Clamp(nextPlayhead, 0, Playhead.Maximum);
        RevealTimelinePlayhead();
        return Task.CompletedTask;
    }

    private TimelineSelection? CurrentTimelineSelection()
    {
        if (project is null) return null;
        if (selectedEffect is { } effect)
        {
            if (SelectionExists(project, effect) || ToTimelineEffectKind(effect.Kind) is not null) return effect;
        }
        if (TimelineView.Selection is { } timelineSelection)
        {
            if (SelectionExists(project, timelineSelection) || ToTimelineEffectKind(timelineSelection.Kind) is not null) return timelineSelection;
        }
        // No fallback to the (hidden) clip list: commands only act on an explicit selection (Mac EditorSelection).
        return null;
    }

    private (TimelineSelection Selection, double Start, double Duration)? CaptureEffectSelectionForHistory()
    {
        if (project is null) return null;
        var candidate = selectedEffect is { } selected && ToTimelineEffectKind(selected.Kind) is not null && SelectionExists(project, selected)
            ? selected
            : TimelineView.Selection is { } timeline && ToTimelineEffectKind(timeline.Kind) is not null && SelectionExists(project, timeline)
                ? timeline : (TimelineSelection?)null;
        if (candidate is not { } selection || Span(project, selection) is not { } span) return null;
        return (selection, span.Start, span.Duration);
    }

    private void RestoreEffectSelectionAfterHistory((TimelineSelection Selection, double Start, double Duration)? previous)
    {
        if (project is null || previous is not { } state) return;
        TimelineSelection? restored = SelectionExists(project, state.Selection) ? state.Selection : null;
        if (restored is null)
        {
            var oldEnd = state.Start + state.Duration;
            restored = EffectSelections(project, state.Selection.Kind)
                .Select(selection => (Selection: selection, Span: Span(project, selection)))
                .Where(item => item.Span.HasValue)
                .Select(item => (item.Selection, Span: item.Span!.Value,
                    ContainsOldRange: item.Span.Value.Start <= state.Start + 1e-9 && item.Span.Value.Start + item.Span.Value.Duration >= oldEnd - 1e-9,
                    Overlap: Math.Max(0, Math.Min(oldEnd, item.Span.Value.Start + item.Span.Value.Duration) - Math.Max(state.Start, item.Span.Value.Start))))
                .Where(item => item.Overlap > 0)
                .OrderByDescending(item => item.ContainsOldRange)
                .ThenByDescending(item => item.Overlap)
                .ThenBy(item => Math.Abs(item.Span.Start - state.Start))
                .ThenByDescending(item => item.Span.Duration)
                .Select(item => (TimelineSelection?)item.Selection)
                .FirstOrDefault();
        }

        if (restored is { } selection)
        {
            selectedEffect = selection;
            TimelineView.Selection = selection;
            TimelineView.MultiSelection.Clear();
            return;
        }

        selectedEffect = null;
        TimelineView.Selection = null;
        TimelineView.MultiSelection.Clear();
        Status.Text = "撤销/重做后原效果片段已不存在，已清除时间线选择。";
    }

    private static IEnumerable<TimelineSelection> EffectSelections(FrameTrailProject p, string kind) => kind switch
    {
        "zoom" => p.ZoomClips.Select(x => new TimelineSelection("zoom", x.Id)),
        "motion" => p.ScreenMotions.Select(x => new TimelineSelection("motion", x.Id)),
        "cameraMotion" => p.CameraMotions.Select(x => new TimelineSelection("cameraMotion", x.Id)),
        "overlay" => p.Overlays.Select(x => new TimelineSelection("overlay", x.Id)),
        "blur" => p.BlurClips.Select(x => new TimelineSelection("blur", x.Id)),
        _ => []
    };

    private static bool SelectionExists(FrameTrailProject p, TimelineSelection selection) => selection.Kind switch
    {
        "screen" => p.Timeline.Segments.Any(x => x.Id == selection.Id),
        "zoom" => p.ZoomClips.Any(x => x.Id == selection.Id),
        "motion" => p.ScreenMotions.Any(x => x.Id == selection.Id),
        "cameraMotion" => p.CameraMotions.Any(x => x.Id == selection.Id),
        "overlay" => p.Overlays.Any(x => x.Id == selection.Id),
        "blur" => p.BlurClips.Any(x => x.Id == selection.Id),
        "camera" => p.Camera is not null,
        "audio" => Enumerable.Range(0, p.AudioTracks.Count).Any(i => EditorTimelineControl.AudioId(i) == selection.Id),
        _ => false
    };

    private static TimelineEffectKind? ToTimelineEffectKind(string kind) => kind switch
    {
        "zoom" => TimelineEffectKind.Zoom,
        "motion" => TimelineEffectKind.ScreenMotion,
        "cameraMotion" => TimelineEffectKind.CameraMotion,
        "overlay" => TimelineEffectKind.Overlay,
        "blur" => TimelineEffectKind.Blur,
        _ => null
    };

    private static bool TryFindScreenSegmentAtOutput(FrameTrailProject p, double outputTime, out TimelineSegment segment, out double outputStart)
    {
        var elapsed = 0d;
        for (var index = 0; index < p.Timeline.Segments.Count; index++)
        {
            var candidate = p.Timeline.Segments[index];
            var end = elapsed + candidate.OutputDuration;
            if (outputTime < end || index == p.Timeline.Segments.Count - 1)
            {
                segment = candidate;
                outputStart = elapsed;
                return true;
            }
            elapsed = end;
        }
        segment = null!;
        outputStart = 0;
        return false;
    }

    private void SelectScreenSegment(Guid id)
    {
        var item = Clips.Items.Cast<ClipItem>().FirstOrDefault(x => x.Id == id);
        if (item is not null)
        {
            refreshing = true;
            Clips.SelectedItem = item;
            refreshing = false;
        }
        selectedEffect = null;
        selectedScreenSegmentId = id;
        TimelineView.Selection = new("screen", id);
        TimelineView.MultiSelection.Clear();
        TimelineView.MultiSelection.Add(TimelineView.Selection);
        TimelineView.Refresh();
        UpdateTimelineCommandStates();
    }

    private void StepFrames(int frames)
    {
        if (project is null || !double.IsFinite(project.Media.FrameRate) || project.Media.FrameRate <= 0) return;
        // Align to the frame grid first so scrubbed / marker positions never leave the playhead half a frame off.
        Playhead.Value = Math.Clamp(EditOutputTime() + frames / project.Media.FrameRate, 0, Playhead.Maximum);
        RevealTimelinePlayhead();
    }

    private double EditOutputTime()
    {
        if (project is null || !double.IsFinite(project.Media.FrameRate) || project.Media.FrameRate <= 0) return 0;
        var duration = project.Timeline.Duration;
        var frame = 1d / project.Media.FrameRate;
        var time = Math.Clamp(double.IsFinite(Playhead.Value) ? Playhead.Value : 0, 0, duration);
        if (time <= frame / 2 || duration - time <= frame / 2) return time <= frame / 2 ? 0 : duration;
        return Math.Clamp(Math.Round(time / frame, MidpointRounding.AwayFromZero) * frame, 0, duration);
    }

    /// <summary>
    /// True when the focused control owns this key. Only editable text boxes and open drop-downs take every key; sliders,
    /// combo boxes and lists only keep their navigation keys (Mac: the shortcut monitor yields to NSTextView editing and to
    /// arrow keys on sliders, nothing else).
    /// </summary>
    private static bool FocusedControlOwnsKey(Key key, ModifierKeys modifiers)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBoxBase { IsReadOnly: false } or PasswordBox) return true;
        for (var node = focused; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ComboBox { IsDropDownOpen: true }) return true;
        // Shift+arrow on a slider is its large step; plain arrows are its small step. Either way the slider keeps the key.
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            return focused is Slider or ScrollBar or ComboBox or ListBox or ListBoxItem or ComboBoxItem or TabItem or TreeViewItem or RadioButton or Selector;
        return false;
    }

    private void HandleTimelineShortcut(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        var modifiers = Keyboard.Modifiers;
        // Alt alone shows the Alt+click split guide on the timeline and must not open the system menu.
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            TimelineView.RefreshHoverGuide();
            if (TimelineView.IsMouseOver) e.Handled = true;
            return;
        }
        // Saving is always available, even from a text box.
        if (modifiers == ModifierKeys.Control && key == Key.S) { SaveProject(); e.Handled = true; return; }
        // The recording sheet is modal-like: the project underneath must not react to editing keys.
        if (RecorderOverlay.Visibility == Visibility.Visible) return;
        if (FocusedControlOwnsKey(key, modifiers)) return;

        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            if (TimelineView.CancelGesture()) e.Handled = true;
            return;
        }
        // A timeline drag / scrub, or an inspector control mid live-edit, owns the keyboard: no shortcut may edit under it.
        if (TimelineView.GestureInProgress && key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
        { e.Handled = true; return; }
        if (LiveEditActive) return;
        if (modifiers == ModifierKeys.None && key == Key.F1) { ShowShortcutCheatsheet(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.O) { _ = Guard(PickProject); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.OemComma) { ShowUserPreferences(); e.Handled = true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.I) { ToggleInspector(); e.Handled = true; return; }
        // Everything below edits the project and must not fire while an export / media job runs.
        if (operationCancellation != null) return;
        if (modifiers == ModifierKeys.Control && key == Key.E) { ExportClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Alt && key == Key.X) { ExportSelectedPrimaryRange(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.Z) { UndoClick(this, new()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.Y) { RedoClick(this, new()); e.Handled = true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z) { RedoClick(this, new()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.C) { CopyEffect(); e.Handled = true; return; }
        if ((modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && key == Key.V)
        { PasteEffect(modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && e.IsRepeat && key is (Key.S or Key.Q or Key.W or Key.D or Key.Delete or Key.Space))
        { e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key is (Key.D or Key.Delete)) { DeleteCurrentSelection(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.S) { SplitClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.Q) { TrimLeftClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.W) { TrimRightClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.Space) { TogglePlayback(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.I) { SetExportRange(true); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.O) { SetExportRange(false); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.Left) { StepFrames(-1); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.Right) { StepFrames(1); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Shift && key == Key.Left) { StepFrames(-5); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Shift && key == Key.Right) { StepFrames(5); e.Handled = true; }
    }

    private void DeleteCurrentSelection()
    {
        if (project is null) return;
        var selection = CurrentTimelineSelection();
        if (selection is not null && !SelectionExists(project, selection))
        {
            Status.Text = "所选效果片段已不存在，请重新选择。";
            return;
        }
        // Nothing selected: nothing to delete (the key is simply ignored, like Mac).
        if (selection is null && TimelineView.MultiSelection.Count == 0) return;
        DeleteTimelineSelection();
    }
}
