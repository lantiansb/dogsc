using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FrameTrail.Media;
namespace FrameTrail.App;

// Direct manipulation on the preview canvas. Mac counterparts: EditorCanvasPreview.swift (screen / camera
// overlays, crop editor), EditorCanvasOverlayInteraction.swift (stickers / mosaic), EditorCanvasDragFeedback.swift
// (snap guides and read-out), EditorStore.beginInteraction / updateInteraction / commitInteraction (live preview).
//
// Every pointer gesture works on one CanvasEditTarget that is resolved in a single place, so the box that is drawn
// and the object that is written back can never differ (B-01). Dragging shows the change through PreviewLiveEdit and
// the release commits exactly one labelled undo step (B-04/B-05).
public partial class MainWindow
{
    private enum CanvasEditKind { None, Overlay, Blur, ScreenMotion, Camera, CameraMotion, Frame }
    private enum CanvasGesture { None, Move, Resize, Rotate, Focus }
    private enum ShapePart { None, Body, Corner, Dot, Rotate }

    private readonly record struct CanvasEditTarget(CanvasEditKind Kind, Guid Id);
    /// <summary>Selection geometry in PreviewSurface pixels (corner order TL, TR, BR, BL).</summary>
    private sealed record CanvasShape(Point[] Quad, Point[]? Corners, Point? Dot, Point? RotateHandle, Point? RotateAnchor);
    private readonly record struct CanvasObject(CanvasEditTarget Target, Point[] Quad);

    private const double HandleHitRadius = 14;
    private const double GestureStartDistance = 2;

    // Gesture state. Everything the drag needs is captured when the pointer goes down, because the displayed
    // preview scene / project are replaced by temporary copies while a live edit is rendering.
    private CanvasGesture gesture;
    private CanvasEditTarget gestureTarget;
    private Point gestureStart, gestureHandleStart, gestureCenter;
    private bool gestureMoved, gestureSourceBlur;
    private Rect gestureBounds;
    private FrameScene? gestureScene;
    private (int SW, int SH, int OW, int OH) gestureSize;
    private NormalizedRect gestureRect;
    private NormalizedPoint gesturePosition;
    private double gestureScale, gestureRotation;
    private int gestureCorner;
    private NormalizedPoint? gestureSourceStart;
    private CanvasShape? gestureOriginShape;
    private Action<FrameTrailProject>? gestureEdit;
    private string gestureLabel = "";
    private TimelineSelection? gesturePreviousSelection;
    private DispatcherTimer? liveEditTimer;
    private Action<FrameTrailProject>? pendingLiveEdit;
    private long lastLiveEditTicks;

    private static NormalizedPoint ToCropLocalFocus(FrameTrailProject p,NormalizedPoint source){var crop=p.Settings.Canvas?.Crop??NormalizedRect.Full;return new(Math.Clamp((source.X-crop.X)/crop.Width,0,1),Math.Clamp((source.Y-crop.Y)/crop.Height,0,1));}

    // ---- geometry helpers ------------------------------------------------------------------------------------

    /// <summary>The image rectangle inside PreviewSurface (PreviewImage fills the surface, so the origins agree).</summary>
    private Rect PreviewImageBounds()
    {
        if (PreviewImage.Source == null || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return Rect.Empty;
        var aspect = PreviewImage.Source.Width / PreviewImage.Source.Height;
        var w = PreviewImage.ActualWidth; var h = w / aspect;
        if (h > PreviewImage.ActualHeight) { h = PreviewImage.ActualHeight; w = h * aspect; }
        return new((PreviewImage.ActualWidth - w) / 2, (PreviewImage.ActualHeight - h) / 2, w, h);
    }

    private static Point ToSurface(Rect b, NormalizedPoint n) => new(b.X + n.X * b.Width, b.Y + n.Y * b.Height);

    private static Point[] RectQuad(Rect b, NormalizedRect r, double rotationDegrees)
    {
        var center = new Point(b.X + (r.X + r.Width / 2) * b.Width, b.Y + (r.Y + r.Height / 2) * b.Height);
        var hw = r.Width * b.Width / 2; var hh = r.Height * b.Height / 2;
        var angle = (double.IsFinite(rotationDegrees) ? rotationDegrees : 0) * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        Point P(double x, double y) => new(center.X + x * cos - y * sin, center.Y + x * sin + y * cos);
        return [P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh)];
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static Point Centroid(Point[] quad) => new(quad.Average(p => p.X), quad.Average(p => p.Y));
    private static string Pct(double value) => $"{(int)Math.Round(value * 100)}%";
    private static string XY(double x, double y) => $"X {Pct(x)} · Y {Pct(y)}";

    private static bool InsidePolygon(Point point, IReadOnlyList<Point> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>True when the last rendered frame belongs to the committed project, so its scene can be trusted.</summary>
    private bool SceneFresh => displayedPreviewScene is not null && previewSize.SW > 0 && project is not null
        && ReferenceEquals(displayedPreviewProject, project);

    /// <summary>Source-time test used so only clips effective at the playhead are drawn and respond (B-07).</summary>
    private bool IsActiveNow(double start, double end)
    {
        var t = SourceNow;
        return t >= start - 1e-6 && t <= end + 1e-6;
    }

    private PreparedFrameSceneEvaluator EnsureEvaluator()
    {
        if (!ReferenceEquals(evaluatedProject, project) || previewEvaluator is null)
        {
            previewEvaluator = new PreparedFrameSceneEvaluator(project!);
            evaluatedProject = project;
        }
        return previewEvaluator;
    }

    private FrameScene? EvaluateEditScene(double outputTime)
    {
        try { return EnsureEvaluator().Evaluate(Math.Clamp(outputTime, 0, project!.Timeline.Duration)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private bool TryScreenQuad(FrameScene scene, (int SW, int SH, int OW, int OH) size, Rect b, out Point[] quad)
    {
        try
        {
            var corners = GpuFrameRenderer.GetProjectedScreenCorners(scene, size.SW, size.SH, size.OW, size.OH);
            quad = corners.Select(c => ToSurface(b, c)).ToArray();
            return quad.Length == 4;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { quad = []; return false; }
    }

    private static bool TryProjectSourceRect(FrameScene scene, (int SW, int SH, int OW, int OH) size, NormalizedRect rect, out NormalizedPoint[] points)
    {
        var corners = new[] { new NormalizedPoint(rect.X, rect.Y), new NormalizedPoint(rect.X + rect.Width, rect.Y),
            new NormalizedPoint(rect.X + rect.Width, rect.Y + rect.Height), new NormalizedPoint(rect.X, rect.Y + rect.Height) };
        var result = new NormalizedPoint[4];
        for (var i = 0; i < 4; i++)
            if (!GpuFrameRenderer.TryMapSourcePointToCanvas(scene, size.SW, size.SH, size.OW, size.OH, corners[i], out result[i])) { points = []; return false; }
        points = result; return true;
    }

    private static bool Intersects(NormalizedRect a, NormalizedRect b) => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    /// <summary>
    /// Source area the card shows: only the user's canvas crop. Zoom now magnifies the card (it travels past the canvas edge)
    /// instead of cropping inside it, so scene.CropOrigin / CropSize no longer describe what is visible.
    /// </summary>
    private static NormalizedRect CanvasSourceCrop(FrameScene scene) => scene.Canvas?.Crop ?? NormalizedRect.Full;

    /// <summary>True when the projected polygon (canvas-normalized, from the renderer's own geometry) overlaps the canvas at all.</summary>
    private static bool OverlapsCanvas(NormalizedPoint[] points)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
        }
        return maxX > 0 && minX < 1 && maxY > 0 && minY < 1;
    }

    private bool TryMapSurfacePoint(FrameScene scene, (int SW, int SH, int OW, int OH) size, Rect b, Point point, out NormalizedPoint source)
    {
        source = default;
        if (b.IsEmpty || size.SW <= 0) return false;
        var canvas = new NormalizedPoint((point.X - b.X) / b.Width, (point.Y - b.Y) / b.Height);
        return GpuFrameRenderer.TryMapCanvasPointToSource(scene, size.SW, size.SH, size.OW, size.OH, canvas, out source);
    }

    private static NormalizedRect ConstrainWithin(NormalizedRect r)
    {
        var width = Math.Clamp(r.Width, .01, 1); var height = Math.Clamp(r.Height, .01, 1);
        return new(Math.Clamp(r.X, 0, 1 - width), Math.Clamp(r.Y, 0, 1 - height), width, height);
    }

    // ---- B-01: one place decides what the canvas edits --------------------------------------------------------

    /// <summary>
    /// The object canvas gestures edit. The inspector tab wins for camera / frame (those tabs have no timeline
    /// selection of their own); otherwise the timeline selection must match the tab. Both the selection box and
    /// every commit use this result.
    /// </summary>
    private CanvasEditTarget ResolveCanvasEditTarget()
    {
        if (project is null || cropMode) return default;
        switch (inspectorTab)
        {
            case "camera":
                if (project.Camera is null) return default;
                if (selectedEffect is { Kind: "cameraMotion" } cm && project.CameraMotions.Any(x => x.Id == cm.Id))
                    return new(CanvasEditKind.CameraMotion, cm.Id);
                return new(CanvasEditKind.Camera, Guid.Empty);
            case "frame":
                return frameTab == "layout" ? new(CanvasEditKind.Frame, Guid.Empty) : default;
            case "overlay":
                if (selectedEffect is { Kind: "overlay" } o && project.Overlays.Any(x => x.Id == o.Id)) return new(CanvasEditKind.Overlay, o.Id);
                break;
            case "blur":
                if (selectedEffect is { Kind: "blur" } bl && project.BlurClips.Any(x => x.Id == bl.Id)) return new(CanvasEditKind.Blur, bl.Id);
                break;
            case "zoom":
                if (selectedEffect is { Kind: "motion" } m && project.ScreenMotions.Any(x => x.Id == m.Id)) return new(CanvasEditKind.ScreenMotion, m.Id);
                break;
        }
        return default;
    }

    // ---- shapes ------------------------------------------------------------------------------------------------

    private bool TryBlurQuad(BlurClip clip, Rect b, out Point[] quad)
    {
        quad = [];
        if (!clip.SourceSpace) { quad = RectQuad(b, clip.Rect, 0); return true; }
        if (!SceneFresh) return false;
        var scene = displayedPreviewScene!;
        if (!Intersects(clip.Rect, CanvasSourceCrop(scene)) || !TryProjectSourceRect(scene, previewSize, clip.Rect, out var projected) || !OverlapsCanvas(projected)) return false;
        quad = projected.Select(p => ToSurface(b, p)).ToArray();
        return true;
    }

    private CanvasShape WithRotateHandle(Point[] quad, Rect b)
    {
        var center = Mid(quad[0], quad[2]);
        (Point Anchor, Point Handle)? Stem(Point a, Point c)
        {
            var anchor = Mid(a, c);
            var v = anchor - center;
            if (v.Length < 1e-6) return null;
            v.Normalize();
            return (anchor, anchor + v * 34);
        }
        var safe = new Rect(b.X - 8, b.Y - 8, b.Width + 16, b.Height + 16);
        var top = Stem(quad[0], quad[1]); var bottom = Stem(quad[3], quad[2]);
        var pick = top is { } t && safe.Contains(t.Handle) ? top : bottom is { } bt && safe.Contains(bt.Handle) ? bottom : top;
        return new(quad, quad, null, pick?.Handle, pick?.Anchor);
    }

    /// <summary>Builds the selection chrome for a target at the current playhead. Sets <paramref name="stale"/> when the needed scene is not trustworthy yet.</summary>
    private CanvasShape? BuildShape(CanvasEditTarget t, Rect b, out bool stale)
    {
        stale = false;
        if (project is null || b.IsEmpty) return null;
        switch (t.Kind)
        {
            case CanvasEditKind.Overlay:
            {
                var clip = project.Overlays.FirstOrDefault(x => x.Id == t.Id);
                if (clip is null || !IsActiveNow(clip.Start, clip.End)) return null;
                return WithRotateHandle(RectQuad(b, clip.Rect, clip.Rotation), b);
            }
            case CanvasEditKind.Blur:
            {
                var clip = project.BlurClips.FirstOrDefault(x => x.Id == t.Id);
                if (clip is null || !IsActiveNow(clip.Start, clip.End)) return null;
                if (clip.SourceSpace && !SceneFresh) { stale = true; return null; }
                return TryBlurQuad(clip, b, out var quad) ? new(quad, quad, null, null, null) : null;
            }
            case CanvasEditKind.Camera:
            case CanvasEditKind.CameraMotion:
            {
                if (!SceneFresh) { stale = true; return null; }
                if (displayedPreviewScene!.Camera is not { } camera) return null;
                var quad = RectQuad(b, camera.Rect, 0);
                return new(quad, null, quad[2], null, null);
            }
            case CanvasEditKind.Frame:
            case CanvasEditKind.ScreenMotion:
            {
                if (!SceneFresh) { stale = true; return null; }
                return TryScreenQuad(displayedPreviewScene!, previewSize, b, out var quad) ? new(quad, null, quad[2], null, null) : null;
            }
        }
        return null;
    }

    /// <summary>Selectable objects at the playhead, front to back: stickers, blur regions, camera, screen.</summary>
    private IEnumerable<CanvasObject> EnumerateCanvasObjects(Rect b)
    {
        if (project is null || b.IsEmpty) yield break;
        foreach (var o in project.Overlays.Where(x => IsActiveNow(x.Start, x.End)).OrderByDescending(x => x.Layer).ThenByDescending(x => x.Start))
            yield return new(new(CanvasEditKind.Overlay, o.Id), RectQuad(b, o.Rect, o.Rotation));
        foreach (var c in project.BlurClips.Where(x => IsActiveNow(x.Start, x.End)).OrderByDescending(x => x.Start))
            if (TryBlurQuad(c, b, out var blurQuad)) yield return new(new(CanvasEditKind.Blur, c.Id), blurQuad);
        if (!SceneFresh) yield break;
        var scene = displayedPreviewScene!;
        if (scene.Camera is { } camera) yield return new(new(CanvasEditKind.Camera, Guid.Empty), RectQuad(b, camera.Rect, 0));
        if (TryScreenQuad(scene, previewSize, b, out var screenQuad)) yield return new(new(CanvasEditKind.Frame, Guid.Empty), screenQuad);
    }

    private CanvasObject? FrontObjectAt(Rect b, Point p)
    {
        foreach (var candidate in EnumerateCanvasObjects(b))
            if (InsidePolygon(p, candidate.Quad)) return candidate;
        return null;
    }

    private (ShapePart Part, int Index) HitShape(CanvasShape shape, Point p)
    {
        if (shape.RotateHandle is { } rotate && (p - rotate).Length <= HandleHitRadius) return (ShapePart.Rotate, -1);
        if (shape.Dot is { } dot && (p - dot).Length <= HandleHitRadius + 2) return (ShapePart.Dot, 2);
        if (shape.Corners is { } corners)
        {
            var best = -1; var bestDistance = HandleHitRadius;
            for (var i = 0; i < corners.Length; i++)
            {
                var d = (p - corners[i]).Length;
                if (d <= bestDistance) { best = i; bestDistance = d; }
            }
            if (best >= 0) return (ShapePart.Corner, best);
        }
        return InsidePolygon(p, shape.Quad) ? (ShapePart.Body, -1) : (ShapePart.None, -1);
    }

    // ---- selection chrome --------------------------------------------------------------------------------------

    private void ApplyShape(CanvasShape? shape, bool editing = false)
    {
        SelectionVisual.Quad = shape?.Quad;
        SelectionVisual.CornerHandles = shape?.Corners;
        SelectionVisual.ResizeDot = shape?.Dot;
        SelectionVisual.RotateHandle = shape?.RotateHandle;
        SelectionVisual.RotateAnchor = shape?.RotateAnchor;
        SelectionVisual.Editing = editing;
        SelectionVisual.InvalidateVisual();
    }

    private void SetFeedback(double? guideX, double? guideY, string? readout, double objectY)
    {
        SelectionVisual.CanvasBounds = gestureBounds;
        SelectionVisual.GuideX = guideX; SelectionVisual.GuideY = guideY;
        SelectionVisual.Readout = readout; SelectionVisual.ReadoutAtTop = objectY > .65;
        SelectionVisual.InvalidateVisual();
    }

    private void ClearFeedback()
    {
        SelectionVisual.GuideX = null; SelectionVisual.GuideY = null; SelectionVisual.Readout = null;
        SelectionVisual.InvalidateVisual();
    }

    /// <summary>Redraws the selection chrome from the current project and last rendered scene.</summary>
    private void RefreshPreviewSelection()
    {
        if (project == null || SelectionVisual == null) return;
        if (gesture != CanvasGesture.None) return; // the running gesture owns the chrome
        if (cropMode) { SelectionVisual.Reset(); HideQuickControls(); UpdateCropOverlayBounds(); return; }
        if (playing || playbackStarting) { SelectionVisual.Reset(); HideQuickControls(); return; } // B-06: no edit chrome while playing
        var b = PreviewImageBounds();
        SelectionVisual.CanvasBounds = b;
        var target = ResolveCanvasEditTarget();
        if (target.Kind == CanvasEditKind.None || b.IsEmpty) { SelectionVisual.Reset(); HideQuickControls(); return; }
        var shape = BuildShape(target, b, out var stale);
        if (shape is null && stale) return; // a render is on its way; keep what is drawn instead of flickering
        SelectionVisual.HoverQuad = null;
        ApplyShape(shape);
        UpdateQuickControls(target, shape);
    }

    private void PreviewSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // B-21: a resize only needs the chrome recomputed (the inspector is not rebuilt).
        RefreshPreviewSelection();
    }

    // ---- canvas selection (B-02) ---------------------------------------------------------------------------------

    /// <summary>
    /// Selects a canvas object through the same state the timeline selection uses (selectedEffect, inspector tab,
    /// timeline highlight). A null target clears the selection like a click on the empty canvas.
    /// </summary>
    private void SelectFromCanvas(CanvasEditTarget? target)
    {
        if (project is null) return;
        selectedScreenSegmentId = null;
        switch (target?.Kind)
        {
            case CanvasEditKind.Overlay: selectedEffect = new("overlay", target.Value.Id); inspectorTab = "overlay"; break;
            case CanvasEditKind.Blur: selectedEffect = new("blur", target.Value.Id); inspectorTab = "blur"; break;
            case CanvasEditKind.Camera: selectedEffect = new("camera", Guid.Empty); inspectorTab = "camera"; break;
            case CanvasEditKind.Frame: selectedEffect = null; inspectorTab = "frame"; frameTab = "layout"; break;
            default:
                selectedEffect = null;
                if (inspectorTab is "overlay" or "blur" or "camera" or "zoom") inspectorTab = "frame";
                break;
        }
        TimelineView.MultiSelection.Clear();
        if (selectedEffect is not null) TimelineView.MultiSelection.Add(selectedEffect);
        TimelineView.Selection = selectedEffect;
        TimelineView.InvalidateVisual();
        BuildInspector();
    }

    // ---- pointer handling ----------------------------------------------------------------------------------------

    private bool ManualFocusActive() => inspectorTab == "zoom" && motionTab == "zoom" && ManualFocus.IsChecked == true;

    private void PreviewEditDown(object sender, MouseButtonEventArgs e)
    {
        if (cropMode || project == null || e.ChangedButton != MouseButton.Left) return;
        if (quickBar is { IsMouseOver: true }) return; // the floating quick controls handle their own input
        PreviewSurface.Focus(); // B-02: the canvas owns the keyboard so Space and friends work after a click
        Keyboard.Focus(PreviewSurface);
        if (playing || playbackStarting) return;
        var b = PreviewImageBounds();
        if (b.IsEmpty) return;
        var p = e.GetPosition(PreviewSurface);
        if (gesture != CanvasGesture.None) return;

        if (ManualFocusActive())
        {
            if (BeginFocusGesture(p, b)) { PreviewSurface.CaptureMouse(); e.Handled = true; }
            return;
        }

        // 1. handles and body of the object that is being edited
        var target = ResolveCanvasEditTarget();
        if (target.Kind != CanvasEditKind.None && BuildShape(target, b, out _) is { } shape)
        {
            var (part, index) = HitShape(shape, p);
            // Handles always win; the body only wins when nothing is stacked above the selected object.
            if (part != ShapePart.None && (part != ShapePart.Body || FrontObjectAt(b, p) is not { } front || front.Target == target))
            {
                var mode = part switch { ShapePart.Rotate => CanvasGesture.Rotate, ShapePart.Corner or ShapePart.Dot => CanvasGesture.Resize, _ => CanvasGesture.Move };
                if (BeginGesture(target, mode, index, p, b, shape)) { PreviewSurface.CaptureMouse(); e.Handled = true; }
                return;
            }
        }

        // 2. another object under the pointer: select it and start dragging it right away (Mac: hover chrome is live)
        foreach (var candidate in EnumerateCanvasObjects(b))
        {
            if (!InsidePolygon(p, candidate.Quad)) continue;
            SelectFromCanvas(candidate.Target);
            var next = ResolveCanvasEditTarget();
            if (next.Kind != CanvasEditKind.None && BuildShape(next, b, out _) is { } nextShape
                && BeginGesture(next, CanvasGesture.Move, -1, p, b, nextShape))
                PreviewSurface.CaptureMouse();
            e.Handled = true;
            return;
        }

        // 3. empty canvas: clear the selection
        SelectFromCanvas(null);
        e.Handled = true;
    }

    private void PreviewEditMove(object sender, MouseEventArgs e)
    {
        if (cropMode || project == null) return;
        var p = e.GetPosition(PreviewSurface);
        if (gesture != CanvasGesture.None) { UpdateGesture(p); return; }
        if (playing || playbackStarting || quickBar is { IsMouseOver: true }) { PreviewSurface.Cursor = null; return; }
        UpdateHover(p);
    }

    private void PreviewEditUp(object sender, MouseButtonEventArgs e)
    {
        if (gesture == CanvasGesture.None) return;
        UpdateGesture(e.GetPosition(PreviewSurface));
        EndGesture(commit: true);
        e.Handled = true;
    }

    private void PreviewLostCapture(object sender, MouseEventArgs e)
    {
        // B-05: losing the capture (alt-tab, popup) abandons the gesture instead of leaving a half-applied preview.
        if (gesture != CanvasGesture.None) CancelCanvasGesture();
    }

    private void PreviewMouseLeaveCanvas(object sender, MouseEventArgs e)
    {
        if (gesture != CanvasGesture.None) return;
        PreviewSurface.Cursor = null;
        if (SelectionVisual.HoverQuad is not null) { SelectionVisual.HoverQuad = null; SelectionVisual.InvalidateVisual(); }
    }

    private void UpdateHover(Point p)
    {
        var b = PreviewImageBounds();
        if (b.IsEmpty) return;
        Cursor? cursor = null; Point[]? hover = null;
        var target = ResolveCanvasEditTarget();
        var part = ShapePart.None; var index = -1; CanvasShape? shape = null;
        if (target.Kind != CanvasEditKind.None && (shape = BuildShape(target, b, out _)) is not null) (part, index) = HitShape(shape, p);
        if (part == ShapePart.Body && FrontObjectAt(b, p) is { } front && front.Target != target) part = ShapePart.None;
        if (part != ShapePart.None)
        {
            cursor = part switch
            {
                ShapePart.Rotate => Cursors.Cross,
                ShapePart.Dot => Cursors.SizeNWSE,
                ShapePart.Corner => index is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW,
                _ => Cursors.SizeAll
            };
        }
        else
        {
            foreach (var candidate in EnumerateCanvasObjects(b))
                if (InsidePolygon(p, candidate.Quad)) { hover = candidate.Quad; cursor = Cursors.Hand; break; }
        }
        PreviewSurface.Cursor = cursor;
        var changed = !ReferenceEquals(SelectionVisual.HoverQuad, hover) && (hover is null || SelectionVisual.HoverQuad is null || !hover.SequenceEqual(SelectionVisual.HoverQuad));
        if (changed) { SelectionVisual.HoverQuad = hover; SelectionVisual.InvalidateVisual(); }
    }

    // ---- gestures -----------------------------------------------------------------------------------------------

    private bool BeginGesture(CanvasEditTarget target, CanvasGesture mode, int corner, Point p, Rect b, CanvasShape shape)
    {
        if (project is null) return false;
        gestureTarget = target; gestureStart = p; gestureBounds = b; gestureMoved = false; gestureCorner = corner;
        gestureScale = 1; gestureRotation = 0; gestureSourceBlur = false; gestureSourceStart = null; gestureEdit = null;
        gestureScene = displayedPreviewScene; gestureSize = previewSize; gestureOriginShape = shape;
        gestureCenter = Centroid(shape.Quad);
        gestureHandleStart = mode == CanvasGesture.Rotate && shape.RotateHandle is { } rh ? rh
            : shape.Dot is { } dot ? dot : corner >= 0 && shape.Corners is { } cs && corner < cs.Length ? cs[corner] : p;

        switch (target.Kind)
        {
            case CanvasEditKind.Overlay:
            {
                var clip = project.Overlays.FirstOrDefault(x => x.Id == target.Id);
                if (clip is null) return false;
                gestureRect = clip.Rect; gestureRotation = clip.Rotation;
                gestureCenter = new(b.X + (clip.Rect.X + clip.Rect.Width / 2) * b.Width, b.Y + (clip.Rect.Y + clip.Rect.Height / 2) * b.Height);
                break;
            }
            case CanvasEditKind.Blur:
            {
                var clip = project.BlurClips.FirstOrDefault(x => x.Id == target.Id);
                if (clip is null) return false;
                gestureRect = clip.Rect; gestureSourceBlur = clip.SourceSpace;
                if (clip.SourceSpace)
                {
                    if (!SceneFresh || gestureScene is null || !TryMapSurfacePoint(gestureScene, gestureSize, b, p, out var source)) return false;
                    gestureSourceStart = source;
                }
                break;
            }
            case CanvasEditKind.Camera:
            {
                if (project.Camera is not { } camera || !SceneFresh || gestureScene?.Camera is not { } cam) return false;
                gesturePosition = camera.Position; gestureScale = camera.Size; gestureRect = cam.Rect;
                gestureCenter = new(b.X + (cam.Rect.X + cam.Rect.Width / 2) * b.Width, b.Y + (cam.Rect.Y + cam.Rect.Height / 2) * b.Height);
                break;
            }
            case CanvasEditKind.CameraMotion:
            {
                var clip = project.CameraMotions.FirstOrDefault(x => x.Id == target.Id);
                if (clip is null || !SceneFresh) return false;
                // Mac interactionPreviewTime: while dragging a camera motion the canvas shows its end state.
                var at = project.Timeline.MapSourceToOutput(Math.Max(clip.Start, clip.End - .001));
                var endScene = at is { } outputTime ? EvaluateEditScene(outputTime) : gestureScene;
                if (endScene?.Camera is not { } cam) return false;
                gestureScene = endScene;
                gesturePosition = clip.TargetPosition; gestureScale = clip.TargetSize; gestureRect = cam.Rect;
                gestureCenter = new(b.X + (cam.Rect.X + cam.Rect.Width / 2) * b.Width, b.Y + (cam.Rect.Y + cam.Rect.Height / 2) * b.Height);
                gestureOriginShape = new CanvasShape(RectQuad(b, cam.Rect, 0), null, ToSurface(b, new(cam.Rect.X + cam.Rect.Width, cam.Rect.Y + cam.Rect.Height)), null, null);
                livePreviewTime = at;
                break;
            }
            case CanvasEditKind.Frame:
            {
                if (!SceneFresh || gestureScene is null) return false;
                var canvas = project.Settings.Canvas ?? new CanvasSettings();
                gesturePosition = canvas.ContentPosition; gestureScale = canvas.ContentScale;
                break;
            }
            case CanvasEditKind.ScreenMotion:
            {
                var clip = project.ScreenMotions.FirstOrDefault(x => x.Id == target.Id);
                if (clip is null || !SceneFresh || gestureScene is null) return false;
                gesturePosition = clip.Target.Position; gestureScale = clip.Target.Scale;
                // The live frame shows the motion's target state (Mac: end of clip); draw the box from the same scene.
                gestureScene = gestureScene with { ScreenMotion = EnsureEvaluator().EvaluateTargetForMotion(clip.Id) };
                if (TryScreenQuad(gestureScene, gestureSize, b, out var targetQuad))
                {
                    gestureOriginShape = new CanvasShape(targetQuad, null, targetQuad[2], null, null);
                    gestureCenter = Centroid(targetQuad); gestureHandleStart = targetQuad[2];
                }
                break;
            }
            default: return false;
        }
        gesture = mode;
        HideQuickControls();
        return true;
    }

    private bool BeginFocusGesture(Point p, Rect b)
    {
        if (!SceneFresh || displayedPreviewScene is null || project is null) return false;
        gestureScene = displayedPreviewScene; gestureSize = previewSize; gestureBounds = b;
        if (!TryMapSurfacePoint(gestureScene, gestureSize, b, p, out _)) return false;
        gestureStart = p; gestureMoved = false; gestureEdit = null; gestureTarget = default; gestureOriginShape = null;
        gestureLabel = "设置缩放焦点";
        gesturePreviousSelection = selectedEffect;
        if (selectedEffect?.Kind != "zoom")
        {
            // No clip selected: a zoom already under the playhead is selected for editing; otherwise the drag creates one with the
            // Mac defaults (EffectDefaults.NewManualZoom) fitted into the lane gap (A's EffectLaneRules decides start / end).
            var startSource = project.Timeline.MapOutputToSource(Math.Clamp(Playhead.Value, 0, project.Timeline.Duration));
            var lane = EffectLaneSupport.Items(project, "zoom");
            if (EffectLaneRules.FindAt(lane, startSource) is { } existingZoom) { selectedEffect = new("zoom", existingZoom.Id); focusNewClip = null; }
            else
            {
                var click = 1.5 * SegmentRateAt(project, Playhead.Value);
                var fit = EffectLaneRules.FitNew(lane, startSource, startSource, EffectLaneSupport.Limit(project), click, EffectLaneSupport.CreateMinimumDuration("zoom"), EffectLaneSupport.DefaultTail("zoom"));
                if (fit is not { } placed) { ShowNotice("此处没有足够空间", true); gestureScene = null; return false; }
                focusNewClip = NewZoomFromDefaults(project, Guid.NewGuid(), placed, placed.Duration);
            }
        }
        else focusNewClip = null;
        gesture = CanvasGesture.Focus;
        return true;
    }
    private ZoomClip? focusNewClip;

    private void UpdateGesture(Point p)
    {
        if (gesture == CanvasGesture.None || project is null) return;
        var d = p - gestureStart;
        if (!gestureMoved && d.Length < GestureStartDistance) return;
        gestureMoved = true;
        if (gesture == CanvasGesture.Focus) { UpdateFocusGesture(p); return; }
        var b = gestureBounds;
        switch (gestureTarget.Kind)
        {
            case CanvasEditKind.Overlay: UpdateOverlayGesture(p, d, b); break;
            case CanvasEditKind.Blur: UpdateBlurGesture(p, d, b); break;
            case CanvasEditKind.Camera:
            case CanvasEditKind.CameraMotion: UpdateCameraGesture(d, b); break;
            case CanvasEditKind.Frame:
            case CanvasEditKind.ScreenMotion: UpdateScreenGesture(p, d, b); break;
        }
    }

    private void SetGestureEdit(Action<FrameTrailProject> edit, string label)
    {
        gestureEdit = edit; gestureLabel = label;
        ScheduleLiveEdit(edit);
    }

    private double ThresholdX(double travel) => CanvasSnapMath.NormalizedThreshold(travel);

    private void UpdateOverlayGesture(Point p, Vector d, Rect b)
    {
        var id = gestureTarget.Id; var r = gestureRect;
        if (gesture == CanvasGesture.Move)
        {
            var proposed = new NormalizedPoint(r.X + r.Width / 2 + d.X / b.Width, r.Y + r.Height / 2 + d.Y / b.Height);
            var snap = CanvasSnapMath.SnapCenterInside(proposed, r.Width / 2, r.Height / 2, ThresholdX(b.Width), ThresholdX(b.Height));
            var rect = r with { X = snap.Point.X - r.Width / 2, Y = snap.Point.Y - r.Height / 2 };
            SetGestureEdit(x => { if (x.Overlays.FirstOrDefault(c => c.Id == id) is { } clip) clip.Rect = rect; }, "移动贴图");
            ApplyShape(WithRotateHandle(RectQuad(b, rect, gestureRotation), b), true);
            SetFeedback(snap.GuideX, snap.GuideY, "贴图位置  " + XY(snap.Point.X, snap.Point.Y), snap.Point.Y);
        }
        else if (gesture == CanvasGesture.Resize)
        {
            // Mac stickerResizeGesture: scale about the centre by the pointer's distance ratio, aspect ratio locked.
            var handle = gestureHandleStart + d;
            var radius0 = Math.Max((gestureHandleStart - gestureCenter).Length, 1);
            var factor = (handle - gestureCenter).Length / radius0;
            var maxWidth = Math.Min(1, r.Width / Math.Max(r.Height, 1e-6));
            var width = Math.Clamp(r.Width * factor, Math.Min(.03, r.Width), Math.Max(Math.Min(.03, r.Width), maxWidth));
            var height = width * r.Height / Math.Max(r.Width, 1e-6);
            var cx = r.X + r.Width / 2; var cy = r.Y + r.Height / 2;
            var rect = ConstrainWithin(new(cx - width / 2, cy - height / 2, width, height));
            SetGestureEdit(x => { if (x.Overlays.FirstOrDefault(c => c.Id == id) is { } clip) clip.Rect = rect; }, "缩放贴图");
            ApplyShape(WithRotateHandle(RectQuad(b, rect, gestureRotation), b), true);
            SetFeedback(null, null, "贴图大小  W " + Pct(rect.Width), rect.Y + rect.Height / 2);
        }
        else if (gesture == CanvasGesture.Rotate)
        {
            // Mac stickerRotationGesture: angle change of the handle around the centre, -180/-90/0/90/180 snap within 3 degrees.
            var handle = gestureHandleStart + d;
            var angle0 = Math.Atan2(gestureHandleStart.Y - gestureCenter.Y, gestureHandleStart.X - gestureCenter.X);
            var angle = Math.Atan2(handle.Y - gestureCenter.Y, handle.X - gestureCenter.X);
            var delta = CanvasSnapMath.NormalizeDegrees((angle - angle0) * 180 / Math.PI);
            var degrees = CanvasSnapMath.SnapRotationDegrees(gestureRotation + delta);
            SetGestureEdit(x => { if (x.Overlays.FirstOrDefault(c => c.Id == id) is { } clip) clip.Rotation = degrees; }, "旋转贴图");
            ApplyShape(WithRotateHandle(RectQuad(b, r, degrees), b), true);
            var snapped = CanvasSnapMath.IsRotationSnapped(degrees);
            SetFeedback(null, null, "贴图旋转  " + (snapped ? $"{degrees:0}° · 已吸附" : $"{degrees:0.0}°"), r.Y + r.Height / 2);
        }
    }

    private void UpdateBlurGesture(Point p, Vector d, Rect b)
    {
        var id = gestureTarget.Id; var r = gestureRect;
        if (gestureSourceBlur)
        {
            if (gestureScene is null || gestureSourceStart is not { } start || !TryMapSurfacePoint(gestureScene, gestureSize, b, p, out var current)) return;
            var dx = current.X - start.X; var dy = current.Y - start.Y;
            var updated = r;
            if (gesture == CanvasGesture.Resize)
            {
                NormalizedPoint[] anchors = [new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height), new(r.X, r.Y), new(r.X + r.Width, r.Y)];
                var anchor = anchors[Math.Clamp(gestureCorner, 0, 3)];
                var left = Math.Min(anchor.X, current.X); var top = Math.Min(anchor.Y, current.Y);
                var right = Math.Max(anchor.X, current.X); var bottom = Math.Max(anchor.Y, current.Y);
                if (right - left < .02) right = Math.Min(1, left + .02);
                if (bottom - top < .02) bottom = Math.Min(1, top + .02);
                updated = new(left, top, right - left, bottom - top);
            }
            else updated = r with { X = r.X + dx, Y = r.Y + dy };
            var rect = ConstrainWithin(updated);
            SetGestureEdit(x => { if (x.BlurClips.FirstOrDefault(c => c.Id == id) is { } clip) clip.Rect = rect; }, gesture == CanvasGesture.Resize ? "调整柔化区大小" : "移动柔化区");
            if (TryProjectSourceRect(gestureScene, gestureSize, rect, out var projected))
            {
                var quad = projected.Select(q => ToSurface(b, q)).ToArray();
                ApplyShape(new(quad, quad, null, null, null), true);
            }
            return;
        }
        var thresholdX = ThresholdX(b.Width); var thresholdY = ThresholdX(b.Height);
        CanvasSnapMath.RectSnapResult result;
        if (gesture == CanvasGesture.Resize)
        {
            var corners = new[] { new NormalizedPoint(r.X, r.Y), new NormalizedPoint(r.X + r.Width, r.Y), new NormalizedPoint(r.X + r.Width, r.Y + r.Height), new NormalizedPoint(r.X, r.Y + r.Height) };
            var index = Math.Clamp(gestureCorner, 0, 3);
            var anchor = corners[(index + 2) % 4];
            var moved = new NormalizedPoint(Math.Clamp(corners[index].X + d.X / b.Width, 0, 1), Math.Clamp(corners[index].Y + d.Y / b.Height, 0, 1));
            var proposed = new NormalizedRect(Math.Min(anchor.X, moved.X), Math.Min(anchor.Y, moved.Y), Math.Abs(moved.X - anchor.X), Math.Abs(moved.Y - anchor.Y));
            var dragged = moved.X < anchor.X ? (moved.Y < anchor.Y ? 0 : 3) : (moved.Y < anchor.Y ? 1 : 2);
            result = CanvasSnapMath.SnapRectResize(proposed, dragged, thresholdX, thresholdY, .02);
        }
        else result = CanvasSnapMath.SnapRectMove(r with { X = r.X + d.X / b.Width, Y = r.Y + d.Y / b.Height }, thresholdX, thresholdY);
        var next = result.Rect;
        SetGestureEdit(x => { if (x.BlurClips.FirstOrDefault(c => c.Id == id) is { } clip) clip.Rect = ConstrainWithin(next); }, gesture == CanvasGesture.Resize ? "调整柔化区大小" : "移动柔化区");
        var rectQuad = RectQuad(b, next, 0);
        ApplyShape(new(rectQuad, rectQuad, null, null, null), true);
        SetFeedback(result.GuideX, result.GuideY, gesture == CanvasGesture.Resize
            ? $"区域大小  W {Pct(next.Width)} · H {Pct(next.Height)}"
            : "区域位置  " + XY(next.X + next.Width / 2, next.Y + next.Height / 2), next.Y + next.Height / 2);
    }

    private void UpdateCameraGesture(Vector d, Rect b)
    {
        var id = gestureTarget.Id; var motion = gestureTarget.Kind == CanvasEditKind.CameraMotion;
        var r = gestureRect;
        if (gesture == CanvasGesture.Move)
        {
            // Edge-aligned cameras (new projects, Mac CompositionScene) store position as travel along
            // canvas - camera, so a pointer delta maps to delta / (1 - size); legacy centre-based ones map 1:1.
            var edgeAligned = project?.Camera?.EdgeAlignedPlacement == true;
            var proposed = edgeAligned
                ? CameraPlacement.PositionFromRect(r with { X = r.X + d.X / b.Width, Y = r.Y + d.Y / b.Height }, true)
                : new NormalizedPoint(gesturePosition.X + d.X / b.Width, gesturePosition.Y + d.Y / b.Height);
            // Mac anchors [0, .5, 1] on the camera travel: centred, or flush against a canvas edge.
            var snap = CanvasSnapMath.SnapCenterInside(proposed, edgeAligned ? 0 : r.Width / 2, edgeAligned ? 0 : r.Height / 2, ThresholdX(b.Width - r.Width * b.Width), ThresholdX(b.Height - r.Height * b.Height));
            var position = new NormalizedPoint(Math.Clamp(snap.Point.X, 0, 1), Math.Clamp(snap.Point.Y, 0, 1));
            if (motion) SetGestureEdit(x => { if (x.CameraMotions.FirstOrDefault(c => c.Id == id) is { } clip) clip.TargetPosition = position; }, "调整摄像运动位置");
            else SetGestureEdit(x => { if (x.Camera is { } camera) camera.Position = position; }, "移动摄像头");
            var moved = edgeAligned ? CameraPlacement.RectFromPosition(position, r.Width, r.Height, true) : r with { X = r.X + position.X - gesturePosition.X, Y = r.Y + position.Y - gesturePosition.Y };
            var quad = RectQuad(b, moved, 0);
            ApplyShape(new(quad, null, quad[2], null, null), true);
            SetFeedback(snap.GuideX, snap.GuideY, "摄像头位置  " + XY(position.X, position.Y), position.Y);
        }
        else
        {
            // Mac: size follows the diagonal drag, (dx + dy) / short edge * 1.2, 5%..80% (5%..100% for a motion target).
            var shortEdge = Math.Max(Math.Min(b.Width, b.Height), 1);
            var delta = (d.X + d.Y) / shortEdge * 1.2;
            var legacy = !motion && project?.Camera?.Layout is null;
            if (legacy) delta *= Math.Min(1, b.Width / b.Height); // legacy sizes are fractions of the canvas height
            var upper = Math.Max(motion ? 1 : .8, gestureScale);
            var size = Math.Clamp(gestureScale + delta, Math.Min(.05, gestureScale), upper);
            if (motion) SetGestureEdit(x => { if (x.CameraMotions.FirstOrDefault(c => c.Id == id) is { } clip) clip.TargetSize = size; }, "调整摄像运动大小");
            else SetGestureEdit(x => { if (x.Camera is { } camera) camera.Size = size; }, "缩放摄像头");
            var factor = gestureScale > 1e-6 ? size / gestureScale : 1;
            var cx = r.X + r.Width / 2; var cy = r.Y + r.Height / 2;
            var scaled = new NormalizedRect(cx - r.Width * factor / 2, cy - r.Height * factor / 2, r.Width * factor, r.Height * factor);
            var quad = RectQuad(b, scaled, 0);
            ApplyShape(new(quad, null, quad[2], null, null), true);
            SetFeedback(null, null, "摄像头大小  " + Pct(size), cy);
        }
    }

    private void UpdateScreenGesture(Point p, Vector d, Rect b)
    {
        if (gestureScene is null) return;
        var id = gestureTarget.Id; var motion = gestureTarget.Kind == CanvasEditKind.ScreenMotion;
        var position = gesturePosition; var scale = gestureScale;
        string label; string readout; double? guideX = null, guideY = null;
        if (gesture == CanvasGesture.Move)
        {
            var proposed = new NormalizedPoint(Math.Clamp(gesturePosition.X + d.X / b.Width, 0, 1), Math.Clamp(gesturePosition.Y + d.Y / b.Height, 0, 1));
            // Mac: the screen only snaps to the canvas centre.
            var snap = CanvasSnapMath.Snap(proposed, [.5], [.5], ThresholdX(b.Width), ThresholdX(b.Height));
            position = snap.Point; guideX = snap.GuideX; guideY = snap.GuideY;
            label = motion ? "调整屏幕 3D 位置" : "移动屏幕素材";
            readout = "屏幕位置  " + XY(position.X, position.Y);
        }
        else
        {
            var radius0 = Math.Max((gestureHandleStart - gestureCenter).Length, 1);
            var factor = (p - gestureCenter).Length / radius0;
            var lower = motion ? .25 : .7; var upper = motion ? 4 : 2.2;
            scale = Math.Clamp(gestureScale * factor, Math.Min(lower, gestureScale), Math.Max(upper, gestureScale));
            label = motion ? "调整屏幕 3D 大小" : "缩放屏幕素材";
            readout = $"屏幕大小  {scale:0.00}×";
        }
        var pos = position; var sc = scale;
        if (motion) SetGestureEdit(x => { if (x.ScreenMotions.FirstOrDefault(c => c.Id == id) is { } clip) clip.Target = clip.Target with { Position = pos, Scale = sc }; }, label);
        else SetGestureEdit(x => { var canvas = CanvasSettingsFor(x); canvas.ContentPosition = pos; canvas.ContentScale = sc; }, label);

        // Exact outline: project the same scene with the new position / scale.
        var scene = motion
            ? gestureScene with { ScreenMotion = (gestureScene.ScreenMotion ?? new ScreenMotionState()) with { Position = pos, Scale = sc } }
            : gestureScene with { Canvas = (gestureScene.Canvas ?? new CanvasSettings()) with { ContentPosition = pos, ContentScale = sc } };
        if (TryScreenQuad(scene, gestureSize, b, out var quad)) ApplyShape(new(quad, null, quad[2], null, null), true);
        SetFeedback(guideX, guideY, readout, pos.Y);
    }

    private void UpdateFocusGesture(Point p)
    {
        if (gestureScene is null || project is null) return;
        if (!TryMapSurfacePoint(gestureScene, gestureSize, gestureBounds, p, out var source)) return;
        var focus = ToCropLocalFocus(project, source);
        var selected = focusNewClip is null && selectedEffect?.Kind == "zoom" ? selectedEffect.Id : (Guid?)null;
        var created = focusNewClip;
        SetGestureEdit(x =>
        {
            if (created is not null) { x.ZoomClips.Add(created with { Focus = focus }); return; }
            if (x.ZoomClips.FirstOrDefault(z => z.Id == selected) is { } clip) { clip.Focus = focus; clip.Automatic = false; }
        }, "设置缩放焦点");
        SelectionVisual.CanvasBounds = gestureBounds;
        SetFeedback(null, null, "缩放焦点  " + XY(focus.X, focus.Y), focus.Y);
    }

    // ---- live edit throttle ----------------------------------------------------------------------------------------

    private void ScheduleLiveEdit(Action<FrameTrailProject> edit)
    {
        pendingLiveEdit = edit;
        var elapsed = lastLiveEditTicks == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(lastLiveEditTicks).TotalMilliseconds;
        if (elapsed >= 40 && liveEditTimer is not { IsEnabled: true }) { FlushLiveEdit(); return; }
        if (liveEditTimer is { IsEnabled: true }) return;
        liveEditTimer ??= new DispatcherTimer();
        liveEditTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 40 - elapsed));
        liveEditTimer.Tick -= LiveEditTimerTick;
        liveEditTimer.Tick += LiveEditTimerTick;
        liveEditTimer.Start();
    }

    private void LiveEditTimerTick(object? sender, EventArgs e)
    {
        liveEditTimer?.Stop();
        FlushLiveEdit();
    }

    private void FlushLiveEdit()
    {
        if (pendingLiveEdit is not { } edit) return;
        pendingLiveEdit = null;
        lastLiveEditTicks = Stopwatch.GetTimestamp();
        PreviewLiveEdit(edit);
    }

    private void StopLiveEditTimer()
    {
        liveEditTimer?.Stop();
        pendingLiveEdit = null;
    }

    // ---- finishing -------------------------------------------------------------------------------------------------

    /// <summary>Releases the pointer: commits one labelled undo step if the object moved (B-05), otherwise changes nothing.</summary>
    private void EndGesture(bool commit)
    {
        var kind = gesture;
        var edit = gestureEdit; var label = gestureLabel; var moved = gestureMoved;
        var newClip = focusNewClip; var previousSelection = gesturePreviousSelection;
        StopLiveEditTimer();
        gesture = CanvasGesture.None; gestureScene = null; focusNewClip = null;
        livePreviewTime = null;
        if (PreviewSurface.IsMouseCaptured) PreviewSurface.ReleaseMouseCapture();
        ClearFeedback();
        if (commit && moved && edit is not null)
        {
            if (kind == CanvasGesture.Focus && newClip is not null) selectedEffect = new("zoom", newClip.Id);
            CommitLiveEdit(edit, label);
        }
        else
        {
            if (kind == CanvasGesture.Focus) selectedEffect = previousSelection;
            if (LiveEditActive) CancelLiveEdit();
            RefreshPreviewSelection();
        }
    }

    /// <summary>Esc / lost capture: restore the committed frame and the chrome as it was before the drag.</summary>
    private void CancelCanvasGesture()
    {
        if (gesture == CanvasGesture.None) return;
        var origin = gestureOriginShape; var wasFocus = gesture == CanvasGesture.Focus; var previous = gesturePreviousSelection;
        StopLiveEditTimer();
        gesture = CanvasGesture.None; gestureScene = null; focusNewClip = null; livePreviewTime = null;
        if (PreviewSurface.IsMouseCaptured) PreviewSurface.ReleaseMouseCapture();
        if (wasFocus) selectedEffect = previous;
        if (LiveEditActive) CancelLiveEdit();
        ClearFeedback();
        if (origin is not null) ApplyShape(origin);
        RefreshPreviewSelection();
    }

    /// <summary>Window-level key hook (called before the timeline shortcuts). Returns true when the key was consumed.</summary>
    private bool HandleCanvasKey(KeyEventArgs e)
    {
        if (cropMode)
        {
            // A readout with typed text owns the first Esc (it reverts the text); otherwise Esc leaves the crop editor.
            if (e.Key == Key.Escape && !InspectorReadout.IsEditingFocused()) { CropCancelClick(this, new RoutedEventArgs()); e.Handled = true; return true; }
            if (e.Key == Key.Enter && e.OriginalSource is not (TextBoxBase or System.Windows.Controls.Primitives.ButtonBase)) { CropDoneClick(this, new RoutedEventArgs()); e.Handled = true; return true; }
            // Mac disables undo / redo while cropping.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y) { e.Handled = true; return true; }
            return false;
        }
        if (gesture != CanvasGesture.None && e.Key == Key.Escape) { CancelCanvasGesture(); e.Handled = true; return true; }
        // While an object is being dragged on the canvas no editing key (undo, delete, step, split ...) may change the project underneath it.
        if (gesture != CanvasGesture.None && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin))
        { e.Handled = true; return true; }
        return false;
    }
}
