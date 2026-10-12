using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Pointer, wheel and gesture handling. Mac references: EditorTimelineLayout (hit testing, background clicks),
/// EditorTimelinePrimaryEditing (reorder / trim / retime / seam buttons), EditorTimelineZoomTransport (wheel zoom,
/// lane creation drags), EditorTimelineInteraction (drag drafts).
/// Gesture model: only the ruler scrubs; clicking a clip selects it (the host decides whether the playhead moves);
/// clicking empty space clears the selection and, on an effect lane, creates a clip (click) or a range (drag).
/// </summary>
public sealed partial class EditorTimelineControl
{
    Hit? drag;
    Point down, pointer;
    double downOffset;
    string dragMode = "";
    bool dragging, seeking, scrubbed, pendingCollapse;
    TimelineChange? outcome;
    string? createLane;
    bool createDragging, createClearedSelection;
    double createStartTime, createCurrentTime;
    TimelineSelection? hoverSelection;
    DispatcherTimer? autoScrollTimer;
    double autoScrollSpeed;
    HwndSource? hwndSource;

    /// <summary>True while a drag / scrub / create gesture has visible feedback.</summary>
    bool GestureActive => dragging || seeking || createDragging;

    /// <summary>True from button-down to button-up of any left-button gesture.</summary>
    public bool GestureInProgress => drag != null || seeking || createLane != null;

    double PointerDocX() => pointer.X + Offset;
    double DownDocX() => down.X + downOffset;

    Hit? HitAt(Point p) => hits.LastOrDefault(h => h.Rect.Contains(p));
    MarkerHit? MarkerAt(Point p) => p.Y < RulerHeight + 4 ? markerHits.LastOrDefault(h => h.Rect.Contains(p)) : null;
    SeamHit? SeamAt(Point p) => RestoreMode ? seamHits.LastOrDefault(h => h.Rect.Contains(p)) : null;

    double FrameRateOf() => Project is { } project && double.IsFinite(project.Media.FrameRate) && project.Media.FrameRate > 0 ? project.Media.FrameRate : 30;

    // ---- mouse entry points ------------------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (Project == null) return;
        if (p.X < LabelWidth)
        {
            if (e.ChangedButton == MouseButton.Left && p.Y < RulerHeight) { TrackMenuRequested?.Invoke(); return; }
            if (e.ChangedButton == MouseButton.Left && p.Y >= RulerHeight + PrimaryHeight && p.X >= LabelWidth - 30)
            {
                var lanes = LaneNames().ToArray();
                var index = (int)((p.Y - (RulerHeight + PrimaryHeight)) / LaneHeight);
                if ((uint)index < (uint)lanes.Length) { HiddenTracks.Add(lanes[index].Key); Refresh(); TracksChanged?.Invoke(); e.Handled = true; }
            }
            return;
        }
        HandlePointerDown(p, e.ChangedButton, Keyboard.Modifiers, true);
        e.Handled = e.ChangedButton is MouseButton.Left or MouseButton.Right;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HandlePointerMove(e.GetPosition(this), IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;
        HandlePointerUp(e.ChangedButton, e.GetPosition(this), true);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsMouseCaptured) return;
        HoverTime = null;
        ToolTip = null;
        if (hoverSelection != null) { hoverSelection = null; InvalidateVisual(); }
        SetHoverGuide(null);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!GestureInProgress) return;
        ResetGesture();
        GestureCancelled?.Invoke();
        InvalidateVisual();
    }

    // ---- pointer down ------------------------------------------------------------------------------------------------

    internal void HandlePointerDown(Point p, MouseButton button, ModifierKeys modifiers, bool capturePointer)
    {
        if (Project == null) return;
        pointer = p;
        var seam = SeamAt(p);
        var marker = MarkerAt(p);
        var hit = HitAt(p);

        if (button == MouseButton.Right)
        {
            if (marker != null) { MarkerContextRequested?.Invoke(marker.Id); return; }
            if (hit != null)
            {
                // Right click selects the clip under the pointer (keeping an existing multi-selection that contains it).
                if (!MultiSelection.Contains(hit.Selection) || MultiSelection.Count < 2)
                {
                    MultiSelection.Clear(); MultiSelection.Add(hit.Selection);
                }
                Selection = hit.Selection;
                SelectionRequested?.Invoke(hit.Selection);
                SelectionChanged?.Invoke();
                InvalidateVisual();
            }
            ContextRequested?.Invoke(hit?.Selection, Time(p.X));
            return;
        }
        if (button != MouseButton.Left) return;

        ResetGesture();
        pointer = p; down = p; downOffset = Offset;
        if (seam != null) { SeamRequested?.Invoke(seam.Kind, seam.Index); return; }
        if (marker != null) { MarkerClicked?.Invoke(marker.Id); return; }

        if (hit != null)
        {
            if (modifiers.HasFlag(ModifierKeys.Alt) && hit.Selection.Kind == "screen")
            {
                SplitAtRequested?.Invoke(SplitTime(p.X));
                return;
            }
            if (!SelectOnPress(hit, p, modifiers)) { InvalidateVisual(); return; }
        }
        else if (p.Y < RulerHeight)
        {
            // The ruler is the only scrub surface.
            seeking = true;
            SeekRequested?.Invoke(FrameTime(SnapTime(Time(p.X), false)));
        }
        else
        {
            var hadSelection = Selection != null || MultiSelection.Count > 0;
            if (hadSelection) ClearSelectionInternal();
            var lane = LaneAtY(p.Y);
            if (lane != null && EffectLaneSupport.IsCreatable(lane))
            {
                createLane = lane;
                createClearedSelection = hadSelection;
                createStartTime = createCurrentTime = SnapTime(Time(p.X), true);
            }
        }
        if (capturePointer && GestureInProgress) CaptureMouse();
        InvalidateVisual();
    }

    void ClearSelectionInternal()
    {
        Selection = null;
        MultiSelection.Clear();
        SelectionCleared?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Applies click / Ctrl / Shift selection. Returns true when the press may start a drag.</summary>
    bool SelectOnPress(Hit hit, Point p, ModifierKeys modifiers)
    {
        var leading = false;
        var trailing = false;
        var edge = Math.Min(hit.Selection.Kind == "screen" ? 10 : 8, hit.Rect.Width / 3);
        leading = hit.IsFirst && p.X - hit.Rect.Left < edge;
        trailing = hit.IsLast && hit.Rect.Right - p.X < edge;

        // Ctrl + drag on the right edge of a primary clip = free retime (Mac: Control-drag trailing handle).
        if (modifiers == ModifierKeys.Control && trailing && hit.Selection.Kind == "screen")
        {
            SelectPlain(hit);
            drag = hit; dragMode = "retime";
            return true;
        }
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Selection != null) MultiSelection.Add(Selection);
            if (!MultiSelection.Add(hit.Selection)) MultiSelection.Remove(hit.Selection);
            Selection = MultiSelection.Contains(hit.Selection) ? hit.Selection : MultiSelection.LastOrDefault();
            if (Selection == null) SelectionCleared?.Invoke(); else SelectionRequested?.Invoke(Selection);
            SelectionChanged?.Invoke();
            return false;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift) && Selection?.Kind == hit.Selection.Kind)
        {
            var previous = hits.Where(h => h.Selection == Selection).ToList();
            if (previous.Count > 0)
            {
                if (hit.Selection.Kind == "screen")
                {
                    var from = Math.Min(previous[0].Index, hit.Index);
                    var to = Math.Max(previous[0].Index, hit.Index);
                    foreach (var h in hits.Where(h => h.Selection.Kind == "screen" && h.Index >= from && h.Index <= to)) MultiSelection.Add(h.Selection);
                }
                else
                {
                    var from = Math.Min(previous.Min(h => h.Rect.X), hit.Rect.X);
                    var to = Math.Max(previous.Min(h => h.Rect.X), hit.Rect.X);
                    foreach (var h in hits.Where(h => h.Selection.Kind == hit.Selection.Kind && h.Rect.X >= from - 1e-6 && h.Rect.X <= to + 1e-6)) MultiSelection.Add(h.Selection);
                }
            }
            MultiSelection.Add(hit.Selection);
            Selection = hit.Selection;
            SelectionRequested?.Invoke(hit.Selection);
            SelectionChanged?.Invoke();
            return false;
        }

        // Plain press on a member of a multi-selection keeps the group so it can be dragged as one.
        if (MultiSelection.Count > 1 && MultiSelection.Contains(hit.Selection))
        {
            pendingCollapse = true;
            Selection = hit.Selection;
            SelectionRequested?.Invoke(hit.Selection);
            SelectionChanged?.Invoke();
        }
        else SelectPlain(hit);
        drag = hit;
        dragMode = leading ? "trim-start" : trailing ? "trim-end" : "move";
        return true;
    }

    void SelectPlain(Hit hit)
    {
        MultiSelection.Clear();
        MultiSelection.Add(hit.Selection);
        Selection = hit.Selection;
        SelectionRequested?.Invoke(hit.Selection);
        SelectionChanged?.Invoke();
    }

    /// <summary>Frame-aligned output time for an Alt+click split; snaps to segment edges and the playhead within 7 px.</summary>
    double SplitTime(double x)
    {
        var time = Time(x);
        var candidates = new List<double> { Position };
        var elapsed = 0d;
        if (Project != null)
            foreach (var segment in Project.Timeline.Segments) { candidates.Add(elapsed); elapsed += segment.OutputDuration; candidates.Add(elapsed); }
        var best = candidates.MinBy(c => Math.Abs(c - time));
        if (Snap && Math.Abs(best - time) * Scale <= SnapCapturePixels) time = best;
        return FrameTime(time);
    }

    // ---- pointer move ------------------------------------------------------------------------------------------------

    internal void HandlePointerMove(Point p, bool captured)
    {
        if (Project == null) return;
        pointer = p;
        HoverTime = p.X >= LabelWidth ? Time(p.X) : null;
        if (captured)
        {
            UpdateGesture(p);
            UpdateAutoScroll(p);
            return;
        }
        UpdateHover(p);
    }

    void UpdateGesture(Point p)
    {
        pointer = p;
        if (seeking)
        {
            scrubbed = true;
            ScrubRequested?.Invoke(FrameTime(SnapTime(Time(p.X), false)));
            InvalidateVisual();
        }
        else if (drag != null)
        {
            if (!dragging && Math.Abs(PointerDocX() - DownDocX()) > 3) dragging = true;
            if (!dragging) return;
            outcome = ComputeOutcome();
            if (outcome != null) ChangePreviewRequested?.Invoke(outcome);
            InvalidateVisual();
        }
        else if (createLane != null)
        {
            createCurrentTime = SnapTime(Time(p.X), true);
            if (!createDragging && Math.Abs(PointerDocX() - DownDocX()) > 4) createDragging = true;
            InvalidateVisual();
        }
    }

    void UpdateHover(Point p)
    {
        if (p.X < LabelWidth)
        {
            if (hoverSelection != null) { hoverSelection = null; InvalidateVisual(); }
            ToolTip = null;
            if (p.Y >= RulerHeight + PrimaryHeight && p.X >= LabelWidth - 30)
            {
                var lanes = LaneNames().ToArray();
                var index = (int)((p.Y - (RulerHeight + PrimaryHeight)) / LaneHeight);
                Cursor = (uint)index < (uint)lanes.Length ? Cursors.Hand : Cursors.Arrow;
            }
            else Cursor = Cursors.Arrow;
            SetHoverGuide(null);
            return;
        }
        var marker = MarkerAt(p);
        var hit = marker == null ? HitAt(p) : null;
        var seam = SeamAt(p);
        var hovered = hit?.Selection;
        if (hovered != hoverSelection) { hoverSelection = hovered; InvalidateVisual(); }
        ToolTip = marker?.Tip ?? seam?.Tip;
        if (marker != null || seam != null) Cursor = Cursors.Hand;
        else if (hit != null)
        {
            var edge = Math.Min(hit.Selection.Kind == "screen" ? 10 : 8, hit.Rect.Width / 3);
            var onEdge = hit.IsFirst && p.X - hit.Rect.Left < edge || hit.IsLast && hit.Rect.Right - p.X < edge;
            Cursor = onEdge ? Cursors.SizeWE : Cursors.Hand;
        }
        else if (p.Y >= RulerHeight + PrimaryHeight && LaneAtY(p.Y) is { } lane && EffectLaneSupport.IsCreatable(lane)) Cursor = Cursors.Cross;
        else Cursor = Cursors.Arrow;
        UpdateAltGuide(p, hit);
    }

    // ---- hover guide (Alt + primary clip: split position) -------------------------------------------------------------

    /// <summary>Re-evaluates the Alt split guide; the host calls it when Alt is pressed or released.</summary>
    public void RefreshHoverGuide()
    {
        if (Project == null || !IsMouseOver) { SetHoverGuide(null); return; }
        var p = Mouse.GetPosition(this);
        UpdateAltGuide(p, p.X >= LabelWidth ? HitAt(p) : null);
    }

    void UpdateAltGuide(Point p, Hit? hit)
    {
        if (hit != null && hit.Selection.Kind == "screen" && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !GestureInProgress)
            SetHoverGuide(X(SplitTime(p.X)));
        else SetHoverGuide(null);
    }

    double? hoverGuideX;
    void SetHoverGuide(double? x)
    {
        if (hoverGuideX == x) return;
        hoverGuideX = x;
        using (var dc = hoverDrawing.Open())
        {
            if (x is { } value)
            {
                var accent = B("Accent");
                dc.DrawLine(new Pen(accent, 1.4) { DashStyle = DashStyles.Dash }, new(value, RulerHeight), new(value, RulerHeight + PrimaryHeight));
                dc.DrawEllipse(accent, null, new(value, RulerHeight + 8), 3.5, 3.5);
            }
        }
        InvalidateVisual();
    }

    // ---- pointer up --------------------------------------------------------------------------------------------------

    internal void HandlePointerUp(MouseButton button, Point p, bool releasePointer)
    {
        if (button != MouseButton.Left) return;
        pointer = p;
        if (seeking && Project != null && (scrubbed || p.X != down.X))
            ScrubRequested?.Invoke(FrameTime(SnapTime(Time(p.X), false)));
        TimelineChange? commit = null;
        TimelineSelection? activated = null;
        if (drag != null && Project != null)
        {
            if (dragging)
            {
                outcome = ComputeOutcome();
                if (outcome != null && !IsUnchanged(drag, outcome)) commit = outcome;
            }
            else
            {
                if (pendingCollapse)
                {
                    MultiSelection.Clear(); MultiSelection.Add(drag.Selection);
                    Selection = drag.Selection;
                    SelectionRequested?.Invoke(drag.Selection);
                    SelectionChanged?.Invoke();
                }
                activated = drag.Selection;
            }
        }
        (string Lane, double Start, double? End)? create = null;
        if (createLane != null)
        {
            if (createDragging)
            {
                createCurrentTime = SnapTime(Time(p.X), true);
                create = (createLane, Math.Min(createStartTime, createCurrentTime), Math.Max(createStartTime, createCurrentTime));
            }
            else if (!createClearedSelection) create = (createLane, createStartTime, null);
        }
        var wasGesture = GestureInProgress;
        ResetGesture();
        InvalidateVisual();
        if (releasePointer && IsMouseCaptured) ReleaseMouseCapture();
        if (commit != null) ChangeRequested?.Invoke(commit);
        else if (wasGesture && create == null) GestureCancelled?.Invoke();
        if (activated != null) SelectionActivated?.Invoke(activated);
        if (create is { } c) CreateRequested?.Invoke(c.Lane, c.Start, c.End);
    }

    static bool IsUnchanged(Hit hit, TimelineChange change)
        => change.Operation switch
        {
            "reorder" => false,
            "retime" => Math.Abs(change.Rate - hit.Rate) < 1e-6,
            _ => Math.Abs(change.SourceStart - hit.SourceStart) < 1e-6 && Math.Abs(change.SourceDuration - hit.SourceDuration) < 1e-6
        };

    void ResetGesture()
    {
        drag = null; dragging = false; outcome = null; dragMode = "";
        seeking = false; scrubbed = false; pendingCollapse = false;
        createLane = null; createDragging = false; createClearedSelection = false;
        StopAutoScroll();
        ResetSnap();
    }

    /// <summary>Esc: abandons the running drag / scrub / create gesture without committing (Mac recoverAbandonedTimelineGesture).</summary>
    public bool CancelGesture()
    {
        if (!GestureInProgress) return false;
        ResetGesture();
        if (IsMouseCaptured) ReleaseMouseCapture();
        GestureCancelled?.Invoke();
        InvalidateVisual();
        return true;
    }

    // ---- edge auto scroll --------------------------------------------------------------------------------------------

    void UpdateAutoScroll(Point p)
    {
        if (!GestureActive && !(drag != null) && createLane == null) { StopAutoScroll(); return; }
        var rightZone = ActualWidth - 28;
        var leftZone = LabelWidth + 28;
        var speed = 0d;
        if (p.X > rightZone) speed = Math.Min(48, (p.X - rightZone) * .6 + 6);
        else if (p.X < leftZone) speed = -Math.Min(48, (leftZone - p.X) * .6 + 6);
        autoScrollSpeed = speed;
        if (speed == 0 || MaximumOffset <= 0) { StopAutoScroll(); return; }
        if (autoScrollTimer == null)
        {
            autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            autoScrollTimer.Tick += (_, _) =>
            {
                if (!GestureInProgress) { StopAutoScroll(); return; }
                var before = Offset;
                ScrollRequested?.Invoke(Math.Clamp(Offset + autoScrollSpeed, 0, MaximumOffset));
                if (Math.Abs(Offset - before) > .01) UpdateGesture(pointer);
            };
        }
        if (!autoScrollTimer.IsEnabled) autoScrollTimer.Start();
    }

    void StopAutoScroll()
    {
        autoScrollSpeed = 0;
        autoScrollTimer?.Stop();
    }

    // ---- drag results ------------------------------------------------------------------------------------------------

    /// <summary>The change a drag would commit right now (shared by OnRender, live preview and pointer up).</summary>
    TimelineChange? ComputeOutcome()
    {
        if (drag == null || Project == null) return null;
        var deltaOutput = (PointerDocX() - DownDocX()) / Scale;
        return drag.Selection.Kind == "screen" ? ComputePrimaryOutcome(drag, deltaOutput) : ComputeEffectOutcome(drag, deltaOutput);
    }

    ICollection<Guid> DraggedIds()
    {
        if (drag == null) return [];
        if (MultiSelection.Count > 1 && MultiSelection.Contains(drag.Selection)) return MultiSelection.Select(x => x.Id).ToHashSet();
        return [drag.Selection.Id];
    }

    TimelineChange ComputePrimaryOutcome(Hit hit, double deltaOutput)
    {
        var project = Project!;
        var fps = FrameRateOf();
        double RoundFrame(double source) => Math.Round(source * fps) / fps;
        var others = project.Timeline.Segments.Where(s => s.Id != hit.Selection.Id).ToList();
        var end = hit.SourceStart + hit.SourceDuration;
        switch (dragMode)
        {
            case "trim-start":
            {
                var edge = hit.OutputStart + deltaOutput;
                edge += SnapShift([edge], DraggedIds(), true);
                var proposed = hit.SourceStart + (edge - hit.OutputStart) * hit.Rate;
                var lower = others.Where(s => s.SourceEnd <= hit.SourceStart + 1e-6).Select(s => s.SourceEnd).DefaultIfEmpty(0).Max();
                var upper = end - hit.Rate / fps;
                var start = Math.Max(lower, Math.Min(RoundFrame(proposed), upper));
                if (start > end - 1e-6) start = hit.SourceStart;
                return new(hit.Selection, "trim-start", start, end - start);
            }
            case "trim-end":
            {
                var edge = hit.OutputStart + hit.OutputDuration + deltaOutput;
                edge += SnapShift([edge], DraggedIds(), true);
                var proposed = hit.SourceStart + (edge - hit.OutputStart) * hit.Rate;
                var upperBound = others.Where(s => s.SourceStart >= end - 1e-6).Select(s => s.SourceStart).DefaultIfEmpty(project.Media.Duration).Min();
                var lowerBound = hit.SourceStart + hit.Rate / fps;
                var newEnd = Math.Min(upperBound, Math.Max(RoundFrame(proposed), lowerBound));
                if (newEnd < hit.SourceStart + 1e-6) newEnd = end;
                return new(hit.Selection, "trim-end", hit.SourceStart, newEnd - hit.SourceStart);
            }
            case "retime":
            {
                var edge = hit.OutputStart + hit.OutputDuration + deltaOutput;
                edge += SnapShift([edge], DraggedIds(), true);
                var outputDuration = edge - hit.OutputStart;
                var minimum = hit.SourceDuration / 100;
                var maximum = hit.SourceDuration / .25;
                outputDuration = Math.Max(minimum, Math.Min(outputDuration, maximum));
                var rate = Math.Round(Math.Clamp(hit.SourceDuration / outputDuration, .25, 100), 2);
                return new(hit.Selection, "retime", hit.SourceStart, hit.SourceDuration, Rate: rate);
            }
            default:
            {
                // Reorder: compare the dragged group's centre with the other clips' midpoints (Mac PrimarySegmentReorderDraft).
                var natural = NaturalLayout();
                var group = ReorderGroup();
                var moving = natural.Where(x => group.Contains(x.Segment.Id)).ToList();
                if (moving.Count == 0) return new(hit.Selection, "reorder", hit.SourceStart, hit.SourceDuration, hit.Index);
                var start = moving.Min(x => x.OutputStart);
                var stop = moving.Max(x => x.OutputStart + x.OutputDuration);
                var center = (start + stop) / 2 + deltaOutput;
                var index = natural.Count(x => !group.Contains(x.Segment.Id) && x.OutputStart + x.OutputDuration / 2 < center);
                return new(hit.Selection, "reorder", hit.SourceStart, hit.SourceDuration, index);
            }
        }
    }

    TimelineChange ComputeEffectOutcome(Hit hit, double deltaOutput)
    {
        var project = Project!;
        var kind = hit.Selection.Kind;
        var start0 = hit.SourceStart;
        var duration0 = hit.SourceDuration;
        var end0 = start0 + duration0;
        var rate = Math.Max(hit.Rate, 1e-6);
        var delta = deltaOutput * rate;
        var minimum = EffectLaneSupport.MinimumDuration(kind);
        var limit = kind is "camera" or "audio" ? project.Media.Duration : EffectLaneSupport.Limit(project);
        var excluded = DraggedIds();
        double start = start0, end = end0;
        var edit = EffectLaneEdit.Move;
        switch (dragMode)
        {
            case "trim-start":
            {
                edit = EffectLaneEdit.Leading;
                start = SnapSourceEdge(start0 + delta, rate, excluded);
                start = Math.Max(0, Math.Min(start, end0 - minimum));
                break;
            }
            case "trim-end":
            {
                edit = EffectLaneEdit.Trailing;
                end = SnapSourceEdge(end0 + delta, rate, excluded);
                end = Math.Min(limit, Math.Max(end, start0 + minimum));
                break;
            }
            default:
            {
                start = start0 + delta;
                end = end0 + delta;
                var a = SourceToOutput(start);
                var b = SourceToOutput(end);
                if (a is { } outA && b is { } outB) delta += SnapShift([outA, outB], excluded, true) * rate;
                start = Math.Max(0, Math.Min(start0 + delta, Math.Max(0, limit - duration0)));
                end = start + duration0;
                break;
            }
        }
        var tail = -1d;
        if (EffectLaneSupport.IsExclusive(kind))
        {
            var lane = EffectLaneSupport.Items(project, kind);
            var fit = EffectLaneRules.Adjust(lane, hit.Selection.Id, edit, start, end, limit, minimum, EffectLaneSupport.DefaultTail(kind));
            if (fit is { } f) { start = f.Start; end = f.End; tail = f.Tail; }
            else { start = start0; end = end0; }
        }
        return new(hit.Selection, dragMode, start, end - start, Tail: tail);
    }

    // ---- wheel -------------------------------------------------------------------------------------------------------

    bool HasVerticalOverflow()
    {
        for (DependencyObject? node = this; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer) return viewer.ScrollableHeight > 0.5;
        return false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Project == null || e.Handled) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift) && !modifiers.HasFlag(ModifierKeys.Control))
        {
            ScrollRequested?.Invoke(Math.Clamp(Offset - e.Delta, 0, MaximumOffset));
            e.Handled = true;
            return;
        }
        // Ctrl always zooms; without vertical overflow the plain wheel zooms too (Mac: scroll = zoom around the pointer).
        if (modifiers.HasFlag(ModifierKeys.Control) || !HasVerticalOverflow())
        {
            var anchor = Math.Max(LabelWidth, e.GetPosition(this).X);
            var zoom = Math.Clamp(Zoom * Math.Pow(1.24, e.Delta / 120d), 1, MaxZoom);
            if (Math.Abs(zoom - Zoom) > 1e-9) ZoomRequested?.Invoke(zoom, anchor);
            e.Handled = true;
        }
    }

    // Horizontal (tilt) wheel is not routed through WPF mouse events.
    void AttachHorizontalWheel()
    {
        DetachHorizontalWheel();
        hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        hwndSource?.AddHook(HorizontalWheelHook);
    }

    void DetachHorizontalWheel()
    {
        hwndSource?.RemoveHook(HorizontalWheelHook);
        hwndSource = null;
    }

    IntPtr HorizontalWheelHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseHWheel = 0x020E;
        if (message != WmMouseHWheel || !IsMouseOver || Project == null) return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        ScrollRequested?.Invoke(Math.Clamp(Offset + delta / 120d * ViewportWidth * .12, 0, MaximumOffset));
        handled = true;
        return IntPtr.Zero;
    }

    // ---- playhead visibility -----------------------------------------------------------------------------------------

    /// <summary>
    /// Scrolls so the playhead is inside the viewport (Mac revealPlayheadIfOutsideViewport). With
    /// <paramref name="centered"/> the playhead lands in the middle, otherwise it is only nudged to the nearest edge.
    /// </summary>
    public void RevealPlayhead(bool centered = true)
    {
        if (Project == null || MaximumOffset <= 0) return;
        var x = X(Position);
        if (x >= LabelWidth + 8 && x <= ActualWidth - 8) return;
        var target = Position * Scale - (centered ? ViewportWidth / 2 : x < LabelWidth ? 40 : ViewportWidth - 40);
        ScrollRequested?.Invoke(Math.Clamp(target, 0, MaximumOffset));
    }

    /// <summary>Scrolls so an output time is visible with a margin (Mac recording marker jump: 40 % position).</summary>
    public void RevealTime(double outputTime)
    {
        if (Project == null || MaximumOffset <= 0) return;
        var x = X(outputTime);
        if (x >= LabelWidth + 24 && x <= ActualWidth - 24) return;
        ScrollRequested?.Invoke(Math.Clamp(outputTime * Scale - ViewportWidth * .4, 0, MaximumOffset));
    }
}
