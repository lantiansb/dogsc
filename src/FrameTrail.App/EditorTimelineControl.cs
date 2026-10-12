using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace FrameTrail.App;

public sealed record TimelineSelection(string Kind, Guid Id);

/// <summary>
/// One committed timeline edit. <see cref="Rate"/> carries the new playback rate for "retime"; <see cref="Tail"/> the
/// shrunk exit / return window for lane-constrained effect edits (negative = leave the clip's own value alone).
/// </summary>
public sealed record TimelineChange(TimelineSelection Selection, string Operation, double SourceStart, double SourceDuration, int NewIndex = -1, double Rate = 0, double Tail = -1);

/// <summary>
/// Self-drawn multi-track timeline. Rendering lives in this file, pointer gestures in EditorTimelineControl.Input.cs and
/// magnetic snapping in EditorTimelineControl.Snap.cs. Mac references: EditorTimelineLayout / EditorTimelinePrimaryEditing /
/// EditorTimelineZoomTransport / EditorTimelineMagneticSnap (DogSCApp).
/// </summary>
public sealed partial class EditorTimelineControl : FrameworkElement
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    /// <summary>Largest timeline zoom (document width / viewport width). 1.24^n steps reach it in about 28 notches.</summary>
    public const double MaxZoom = 400;

    internal FrameTrailProject? Project { get; set; }
    internal TimelineAssetData? Assets { get; set; }
    public TimelineSelection? Selection { get; set; }
    public HashSet<TimelineSelection> MultiSelection { get; } = [];
    public HashSet<string> HiddenTracks { get; } = [];
    public double? RangeStart { get; set; }
    public double? RangeEnd { get; set; }
    /// <summary>Shows merge / restore buttons on every cut seam of the ruler (Mac "恢复剪辑模式").</summary>
    public bool RestoreMode { get; set; }
    public event Action? TrackMenuRequested;
    DrawingGroup playheadDrawing = new();
    TranslateTransform playheadTransform = new();
    DrawingGroup hoverDrawing = new();
    int renderPassCount;
    internal int RenderPassCount => renderPassCount;
    public double Position
    {
        get => position;
        set { position = value; UpdatePlayheadTransform(); }
    }
    double position;
    public double Zoom { get; set; } = 1;
    public double Offset { get; set; }
    public bool Filmstrip { get; set; } = true;
    public bool Waveform { get; set; } = true;
    public bool Snap { get; set; } = true;

    public event Action<double>? SeekRequested;
    public event Action<double>? ScrubRequested;
    public event Action<TimelineSelection>? SelectionRequested;
    /// <summary>A plain click (no drag) on a clip finished: the host may reveal the clip's first useful frame (Mac activateTimelineSelection).</summary>
    public event Action<TimelineSelection>? SelectionActivated;
    /// <summary>Selection / multi-selection changed in any way (also when only cleared).</summary>
    public event Action? SelectionChanged;
    /// <summary>A click on empty timeline space cleared the selection (Mac clearTimelineSelection).</summary>
    public event Action? SelectionCleared;
    public event Action<TimelineChange>? ChangeRequested;
    /// <summary>Throttled stream of the change a drag would commit, for live canvas preview.</summary>
    public event Action<TimelineChange>? ChangePreviewRequested;
    /// <summary>A drag / scrub / create gesture ended without a commit (Esc, lost capture, no-op).</summary>
    public event Action? GestureCancelled;
    /// <summary>New zoom factor and the control-space x the zoom is anchored to.</summary>
    public event Action<double, double>? ZoomRequested;
    public event Action<double>? ScrollRequested;
    /// <summary>Right click: the selection under the pointer (null on background) and the output time there.</summary>
    public event Action<TimelineSelection?, double>? ContextRequested;
    /// <summary>Empty-lane click (end == null) or horizontal drag on an effect lane: kind, output start, output end.</summary>
    public event Action<string, double, double?>? CreateRequested;
    public event Action<Guid>? MarkerClicked;
    public event Action<Guid>? MarkerContextRequested;
    /// <summary>Restore-mode seam button: "merge" / "restore" with the index of the segment before the seam, or "leading" / "trailing".</summary>
    public event Action<string, int>? SeamRequested;
    /// <summary>Alt+click on a primary segment: split at this output time.</summary>
    public event Action<double>? SplitAtRequested;
    public event Action? TracksChanged;

    record Hit(Rect Rect, TimelineSelection Selection, double SourceStart, double SourceDuration, double Rate, int Index,
        double OutputStart = 0, double OutputDuration = 0, bool IsFirst = true, bool IsLast = true);
    sealed record PrimaryItem(TimelineSegment Segment, int Index, double OutputStart, double OutputDuration, double SourceStart, double SourceDuration, double Rate,
        bool Placeholder = false, double FloatingOutputStart = double.NaN);
    sealed record MarkerHit(Rect Rect, Guid Id, string Tip);
    sealed record SeamHit(Rect Rect, string Kind, int Index, string Tip);

    readonly List<Hit> hits = [];
    readonly List<MarkerHit> markerHits = [];
    readonly List<SeamHit> seamHits = [];

    const double LabelWidth = 152, RulerHeight = 40, PrimaryHeight = 90, LaneHeight = 52;
    static readonly Brush EffectLabelBrush = CreateEffectLabelBrush();
    bool isHighContrast;

    /// <summary>Width of the lane label column (the timeline viewport starts to its right).</summary>
    internal static double LabelColumnWidth => LabelWidth;
    internal double ViewportWidth => Math.Max(1, ActualWidth - LabelWidth);
    public double DocumentWidth => ViewportWidth * Zoom;
    /// <summary>Largest valid scroll offset.</summary>
    public double MaximumOffset => Math.Max(0, DocumentWidth - ViewportWidth);
    internal double Scale => DocumentWidth / Math.Max(Project?.Timeline.Duration ?? 1, .01);
    double X(double t) => LabelWidth + t * Scale - Offset;
    double Time(double x) => Math.Clamp((x - LabelWidth + Offset) / Scale, 0, Project?.Timeline.Duration ?? 0);
    /// <summary>Control-space x of an output time for the current zoom and scroll.</summary>
    internal double TimeToViewX(double t) => X(t);

    /// <summary>Output time of the pointer while it hovers the timeline body; null otherwise (Mac clipboardContextTime).</summary>
    public double? HoverTime { get; private set; }

    double FrameTime(double time)
    {
        var project = Project; if (project == null) return 0;
        var duration = project.Timeline.Duration; var rate = project.Media.FrameRate;
        if (!double.IsFinite(rate) || rate <= 0) rate = 30;
        var safe = Math.Clamp(time, 0, duration); var elapsed = 0d;
        for (var index = 0; index < project.Timeline.Segments.Count; index++)
        {
            var segment = project.Timeline.Segments[index]; var outputEnd = elapsed + segment.OutputDuration;
            if (safe < outputEnd || index == project.Timeline.Segments.Count - 1)
            {
                var source = Math.Clamp(segment.SourceStart + (safe - elapsed) * segment.PlaybackRate, segment.SourceStart, segment.SourceEnd);
                var sourceLimit = Math.Min(segment.SourceEnd, project.Media.Duration);
                var maxFrame = Math.Max(0, Math.Ceiling(sourceLimit * rate) - 1);
                var frame = Math.Min(Math.Floor(source * rate), maxFrame);
                var frameTime = Math.Max(segment.SourceStart, frame / rate);
                return elapsed + Math.Clamp((frameTime - segment.SourceStart) / segment.PlaybackRate, 0, segment.OutputDuration);
            }
            elapsed = outputEnd;
        }
        return 0;
    }

    bool IsHighContrast => isHighContrast;
    Brush B(string name)
    {
        if (IsHighContrast) return name switch
        {
            "Surface" or "Chrome" or "ControlSurface" or "Workspace" => SystemColors.WindowBrush,
            "Ink" or "Secondary" => SystemColors.WindowTextBrush,
            "Hairline" or "Dot" => SystemColors.GrayTextBrush,
            "Accent" or "Selection" => SystemColors.HighlightBrush,
            _ => SystemColors.WindowTextBrush
        };
        return (Brush?)TryFindResource(name) ?? Brushes.Gray;
    }

    public EditorTimelineControl()
    {
        Focusable = true; MinHeight = 234; SizeChanged += (_, _) => InvalidateVisual();
        // This surface consumes editing shortcuts and never accepts composed text.
        InputMethod.SetIsInputMethodEnabled(this, false);
        SetResourceReference(FocusVisualStyleProperty, "TimelineKeyboardFocus");
        System.Windows.Automation.AutomationProperties.SetName(this, "时间线");
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "左右方向键逐帧定位；空格播放或暂停；S 分割所选片段。");
        if (Application.Current?.TryFindResource("TransientPreferences") is TransientPreferences preferences)
        {
            isHighContrast = preferences.HighContrast;
            PropertyChangedEventManager.AddHandler(preferences, OnPreferencesChanged, string.Empty);
        }
        Loaded += (_, _) => AttachHorizontalWheel();
        Unloaded += (_, _) => DetachHorizontalWheel();
    }

    void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        isHighContrast = (sender as TransientPreferences)?.HighContrast == true;
        InvalidateVisual();
    }

    static Brush CreateEffectLabelBrush() { var brush = new SolidColorBrush(Color.FromRgb(48, 50, 50)); brush.Freeze(); return brush; }

    public void Refresh() { var lanes = LaneNames().Count(); Height = RulerHeight + PrimaryHeight + lanes * LaneHeight; InvalidateVisual(); }

    void Text(DrawingContext dc, string text, Point p, double size = 11, Brush? brush = null, FontWeight? weight = null, double maxWidth = double.PositiveInfinity, TextAlignment alignment = TextAlignment.Left)
    {
        var family = (FontFamily?)TryFindResource("InterfaceFont") ?? new FontFamily("Microsoft YaHei UI");
        var f = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush ?? B("Secondary"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = alignment };
        if (double.IsFinite(maxWidth)) { f.MaxTextWidth = Math.Max(1, maxWidth); f.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(f, p);
    }

    // ---- lanes ------------------------------------------------------------------------------------------------------

    /// <summary>Lanes in display order. Zoom, 3D, sticker and blur lanes are permanent so they can be created on (Mac);
    /// camera lanes exist while a camera track does.</summary>
    IEnumerable<(string Key, string Icon, string Label)> LaneNames()
    {
        if (!HiddenTracks.Contains("zoom")) yield return ("zoom", "trackZoom", "缩放");
        if (!HiddenTracks.Contains("motion")) yield return ("motion", "track3d", "屏幕 3D");
        if (Project?.Camera != null && !HiddenTracks.Contains("camera")) yield return ("camera", "trackCamera", "摄像头");
        if (Project?.Camera != null && !HiddenTracks.Contains("cameraMotion")) yield return ("cameraMotion", "trackCameraMotion", "摄像运动");
        if (!HiddenTracks.Contains("overlay")) yield return ("overlay", "trackOverlay", "贴图");
        if (!HiddenTracks.Contains("blur")) yield return ("blur", "trackBlur", "柔化");
        if (Project?.AudioTracks.Count > 0 && !HiddenTracks.Contains("audio")) yield return ("audio", "trackAudio", "声音");
    }

    /// <summary>The lane under a control-space y, or null for ruler / primary lane / below the lanes.</summary>
    string? LaneAtY(double y)
    {
        if (y < RulerHeight + PrimaryHeight) return null;
        var index = (int)((y - (RulerHeight + PrimaryHeight)) / LaneHeight);
        var lanes = LaneNames().ToArray();
        return (uint)index < (uint)lanes.Length ? lanes[index].Key : null;
    }

    double LaneTop(string kind)
    {
        var y = RulerHeight + PrimaryHeight;
        foreach (var lane in LaneNames()) { if (lane.Key == kind) return y; y += LaneHeight; }
        return y;
    }

    static string LaneHint(string kind) => kind switch
    {
        "zoom" => "单击或拖拽添加缩放",
        "motion" => "单击或拖拽添加屏幕 3D",
        "cameraMotion" => "单击或拖拽添加摄像运动",
        "overlay" => "单击或拖拽添加贴图",
        "blur" => "单击或拖拽添加柔化",
        _ => ""
    };

    bool LaneHasClips(string kind) => Project != null && kind switch
    {
        "zoom" => Project.ZoomClips.Count > 0,
        "motion" => Project.ScreenMotions.Count > 0,
        "cameraMotion" => Project.CameraMotions.Count > 0,
        "overlay" => Project.Overlays.Count > 0,
        "blur" => Project.BlurClips.Count > 0,
        _ => true
    };

    // ---- primary lane layout ------------------------------------------------------------------------------------------

    List<PrimaryItem> NaturalLayout()
    {
        var items = new List<PrimaryItem>();
        if (Project == null) return items;
        var elapsed = 0d;
        for (var i = 0; i < Project.Timeline.Segments.Count; i++)
        {
            var s = Project.Timeline.Segments[i];
            items.Add(new(s, i, elapsed, s.OutputDuration, s.SourceStart, s.SourceDuration, s.PlaybackRate));
            elapsed += s.OutputDuration;
        }
        return items;
    }

    /// <summary>Natural layout, or the in-flight reorder / trim / retime preview (Mac PrimarySegmentReorderDraft / TrimDraft).</summary>
    List<PrimaryItem> BuildPrimaryLayout()
    {
        var natural = NaturalLayout();
        if (drag?.Selection.Kind != "screen" || !dragging || outcome is not { } result) return natural;
        switch (dragMode)
        {
            case "move":
            {
                var group = ReorderGroup();
                var others = natural.Where(x => !group.Contains(x.Segment.Id)).ToList();
                var moving = natural.Where(x => group.Contains(x.Segment.Id)).ToList();
                var index = Math.Clamp(result.NewIndex, 0, others.Count);
                var order = new List<PrimaryItem>(others); order.InsertRange(index, moving);
                var placed = new List<PrimaryItem>(); var start = 0d;
                var dxOut = (PointerDocX() - DownDocX()) / Scale;
                foreach (var item in order)
                {
                    var original = natural[item.Index];
                    var isMoving = group.Contains(item.Segment.Id);
                    placed.Add(item with { OutputStart = start, Placeholder = isMoving, FloatingOutputStart = isMoving ? original.OutputStart + dxOut : double.NaN });
                    start += item.OutputDuration;
                }
                return placed;
            }
            case "trim-start" or "trim-end" or "retime":
            {
                var layout = new List<PrimaryItem>(); var start = 0d;
                foreach (var item in natural)
                {
                    var current = item;
                    if (item.Segment.Id == drag.Selection.Id)
                    {
                        var rate = dragMode == "retime" ? result.Rate : item.Rate;
                        current = item with { SourceStart = result.SourceStart, SourceDuration = result.SourceDuration, Rate = rate, OutputDuration = result.SourceDuration / rate };
                    }
                    layout.Add(current with { OutputStart = start });
                    start += current.OutputDuration;
                }
                return layout;
            }
        }
        return natural;
    }

    HashSet<Guid> ReorderGroup()
    {
        var screens = MultiSelection.Where(x => x.Kind == "screen").Select(x => x.Id).ToHashSet();
        if (drag != null && screens.Count > 1 && screens.Contains(drag.Selection.Id)) return screens;
        return drag == null ? [] : [drag.Selection.Id];
    }

    // ---- rendering ---------------------------------------------------------------------------------------------------

    TimelineAssetData? peakAssets; double peakMaximum = .001;
    double PeakMaximum()
    {
        if (Assets == null || Assets.Peaks.Count == 0) return .001;
        if (!ReferenceEquals(peakAssets, Assets)) { peakAssets = Assets; peakMaximum = Math.Max(.001, Assets.Peaks.Max()); }
        return peakMaximum;
    }

    static readonly double[] RulerSteps = [.01, .02, .05, .1, .25, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];

    protected override void OnRender(DrawingContext dc)
    {
        renderPassCount++;
        dc.DrawRectangle(B("Surface"), null, new Rect(0, 0, ActualWidth, ActualHeight)); hits.Clear(); markerHits.Clear(); seamHits.Clear();
        var pen = new Pen(B("Hairline"), .65);
        EditorIcon.DrawVector(dc, "layers", new Rect(16, 9, 16, 16), B("Ink")); Text(dc, "轨道", new(38, 12), 11, B("Ink"));
        EditorIcon.DrawVector(dc, "chevron", new Rect(LabelWidth - 25, 10, 12, 12), B("Secondary")); dc.DrawLine(pen, new(LabelWidth, 0), new(LabelWidth, ActualHeight));
        if (Project == null) { Text(dc, "录制或导入视频后，片段与效果将在此显示", new(LabelWidth + 25, 70), 12); return; }
        var s = Scale; var duration = Project.Timeline.Duration;
        var step = RulerSteps.FirstOrDefault(x => x * s >= 100); if (step == 0) step = 1200;
        dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, 0, Math.Max(0, ActualWidth - LabelWidth), ActualHeight)));

        // Ruler ticks: only the visible time window is drawn, so deep zoom stays cheap.
        var firstTick = Math.Floor(Time(LabelWidth) / step) * step; var lastTick = Math.Min(duration, Time(ActualWidth) + step);
        for (var t = firstTick; t <= lastTick; t += step)
        {
            var x = X(t); dc.DrawLine(pen, new(x, RulerHeight - 12), new(x, ActualHeight)); Text(dc, TimeSpan.FromSeconds(t).ToString(@"m\:ss\.ff"), new(x + 4, 10), 10);
            for (var n = 1; n < 4; n++) dc.DrawLine(pen, new(x + step * s * n / 4, RulerHeight - 6), new(x + step * s * n / 4, RulerHeight));
        }

        // Primary lane.
        var layout = BuildPrimaryLayout();
        foreach (var item in layout) if (!item.Placeholder) DrawPrimaryItem(dc, item, item.OutputStart, 1);
        foreach (var item in layout) if (item.Placeholder) DrawPrimarySlot(dc, item);
        foreach (var item in layout) if (item.Placeholder && !double.IsNaN(item.FloatingOutputStart)) DrawPrimaryItem(dc, item, item.FloatingOutputStart, .72, floating: true);

        // Effect lanes.
        var y = RulerHeight + PrimaryHeight;
        void Lane(string kind, IEnumerable<(Guid, double, double, string)> clips, Color color)
        {
            if (HiddenTracks.Contains(kind)) return;
            DrawEffects(dc, kind, clips, y, color);
            if (EffectLaneSupport.IsCreatable(kind) && !LaneHasClips(kind) && createLane != kind)
                Text(dc, LaneHint(kind), new(LabelWidth + 16, y + 18), 11, new SolidColorBrush(Color.FromArgb(120, 140, 145, 145)));
            y += LaneHeight;
        }
        Lane("zoom", Project.ZoomClips.Select(z => (z.Id, z.Start, z.End - z.Start, $"缩放 {z.Scale:0.#}× · {(z.Automatic ? "自动" : "手动")}")), Color.FromRgb(231, 214, 183));
        Lane("motion", Project.ScreenMotions.Select((z, i) => (z.Id, z.Start, z.Duration, $"过渡 {i + 1}  {TimeSpan.FromSeconds(z.Duration):m\\:ss\\.ff}")), Color.FromRgb(224, 162, 83));
        if (Project.Camera is { } camera) Lane("camera", new[] { (Guid.Empty, camera.TimelineStart, camera.Duration, "摄像头") }, Color.FromRgb(179, 200, 219));
        if (Project.Camera != null) Lane("cameraMotion", Project.CameraMotions.Select(z => (z.Id, z.Start, z.Duration, "摄像头运动")), Color.FromRgb(152, 181, 206));
        Lane("overlay", Project.Overlays.Select(z => (z.Id, z.Start, z.Duration, "贴图 · " + System.IO.Path.GetFileNameWithoutExtension(z.MediaPath))), Color.FromRgb(188, 216, 192));
        Lane("blur", Project.BlurClips.Select(z => (z.Id, z.Start, z.Duration, z.Kind switch { BlurKind.Blur => "柔化", BlurKind.Spotlight => "聚光", _ => "马赛克" })), Color.FromRgb(208, 192, 220));
        if (Project.AudioTracks.Count > 0) Lane("audio", Project.AudioTracks.Select((z, i) => (AudioId(i), z.OffsetSeconds, z.Duration, z.Muted ? "音频 · 静音" : "音频 · " + (i + 1))), Color.FromRgb(198, 200, 228));

        DrawRangeBar(dc);
        DrawRulerMarkers(dc);
        if (RestoreMode) DrawSeamButtons(dc, layout);
        DrawEffectDragPreview(dc);
        DrawCreateGhost(dc);
        DrawSnapGuide(dc);

        using (var playhead = playheadDrawing.Open())
        {
            var ink = B("Ink"); playhead.DrawLine(new Pen(ink, 1.25), new(0, 5), new(0, ActualHeight)); playhead.DrawEllipse(ink, null, new(0, 7), 4, 4);
        }
        playheadDrawing.Transform = playheadTransform; UpdatePlayheadTransform(); dc.DrawDrawing(playheadDrawing);
        dc.DrawDrawing(hoverDrawing);
        dc.Pop(); dc.DrawRectangle(B("Surface"), null, new Rect(0, RulerHeight, LabelWidth, Math.Max(0, ActualHeight - RulerHeight)));
        EditorIcon.DrawVector(dc, "trackScreen", new Rect(16, RulerHeight + 31, 18, 18), B("Ink")); Text(dc, "屏幕录制", new(40, RulerHeight + 35), 11, B("Ink")); y = RulerHeight + PrimaryHeight;
        foreach (var lane in LaneNames())
        {
            dc.DrawLine(pen, new(0, y), new(ActualWidth, y)); EditorIcon.DrawVector(dc, lane.Icon, new Rect(17, y + 16, 17, 17), B("Secondary")); Text(dc, lane.Label, new(41, y + 18), 11, B("Ink"));
            DrawHideButton(dc, new Rect(LabelWidth - 30, y + 12, 24, 28), B("Secondary")); y += LaneHeight;
        }
        dc.DrawLine(pen, new(0, RulerHeight), new(ActualWidth, RulerHeight)); dc.DrawLine(pen, new(LabelWidth, 0), new(LabelWidth, ActualHeight));
    }

    void UpdatePlayheadTransform() => playheadTransform.X = X(Position);

    static void DrawHideButton(DrawingContext dc, Rect bounds, Brush brush)
    {
        EditorIcon.DrawVector(dc, "eye", new Rect(bounds.X + 6, bounds.Y + 8, 12, 12), brush); dc.DrawLine(new Pen(brush, .85), new(bounds.X + 6, bounds.Y + 21), new(bounds.X + 18, bounds.Y + 7));
    }

    public static Guid AudioId(int i) => new(i + 1, 0, 0, new byte[8]);

    bool IsSelected(string kind, Guid id) => Selection?.Kind == kind && Selection.Id == id || MultiSelection.Contains(new(kind, id));

    /// <summary>Draws one primary segment at the given output start (also used for the floating reorder copy).</summary>
    void DrawPrimaryItem(DrawingContext dc, PrimaryItem item, double outputStart, double opacity, bool floating = false)
    {
        var s = Scale; var segment = item.Segment;
        var rect = new Rect(X(outputStart) + 2, RulerHeight + 3, Math.Max(2, item.OutputDuration * s - 4), PrimaryHeight - 7);
        if (rect.Right < LabelWidth - 4 || rect.Left > ActualWidth + 4) return;
        var selected = IsSelected("screen", segment.Id);
        var hovered = hoverSelection?.Kind == "screen" && hoverSelection.Id == segment.Id;
        // Very large rectangles (deep zoom) are cut to the viewport; the cut edges are off screen.
        var visible = Rect.Intersect(rect, new Rect(LabelWidth - 24, rect.Y, ActualWidth - LabelWidth + 48, rect.Height));
        if (visible.IsEmpty) return;
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(B("Chrome"), new Pen(floating ? B("Accent") : selected ? B("Ink") : B("Hairline"), floating ? 2 : selected ? 1.6 : 1), visible, 10, 10);
        dc.PushClip(new RectangleGeometry(visible, 9, 9));
        if (Filmstrip && Assets?.Thumbnails.Count > 0)
        {
            var tile = 90d; var first = rect.X + Math.Floor((visible.Left - rect.X) / tile) * tile;
            for (var px = first; px < visible.Right; px += tile)
            {
                var sourceTime = item.SourceStart + (px - rect.X) / s * item.Rate; var best = Assets.Thumbnails[0]; var bestDistance = double.MaxValue;
                foreach (var thumbnail in Assets.Thumbnails) { var d = Math.Abs(thumbnail.SourceTime - sourceTime); if (d < bestDistance) { bestDistance = d; best = thumbnail; } }
                dc.DrawImage(best.Image, new Rect(px, rect.Y, tile, 58));
            }
        }
        else dc.DrawRectangle(IsHighContrast ? B("Chrome") : new SolidColorBrush(Color.FromRgb(228, 232, 230)), null, new Rect(visible.X, rect.Y, visible.Width, 58));
        if (Waveform && Assets?.Peaks.Count > 0 && Assets.Duration > 0)
        {
            var peak = PeakMaximum(); var geometry = new StreamGeometry(); var mid = rect.Bottom - 11d; var left = visible.Left; var right = visible.Right; var waveStep = 2d;
            double Amplitude(double px) { var st = item.SourceStart + (px - rect.X) / s * item.Rate; var k = Math.Clamp((int)(st / Assets.Duration * Assets.Peaks.Count), 0, Assets.Peaks.Count - 1); return Math.Clamp(Assets.Peaks[k] / peak * 8, .5, 8); }
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(left, mid), true, true);
                for (var px = left; px <= right; px += waveStep) ctx.LineTo(new Point(px, mid - Amplitude(px)), true, false);
                ctx.LineTo(new Point(right, mid), true, false);
                for (var px = right; px >= left; px -= waveStep) ctx.LineTo(new Point(px, mid + Amplitude(px)), true, false);
                ctx.LineTo(new Point(left, mid), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(IsHighContrast ? B("Ink") : new SolidColorBrush(Color.FromRgb(120, 125, 125)), null, geometry);
        }
        var label = TimeSpan.FromSeconds(item.OutputDuration).ToString(@"m\:ss\.ff") + (Math.Abs(item.Rate - 1) > 1e-6 ? $" ×{item.Rate:0.##}" : "");
        var badgeWidth = Math.Abs(item.Rate - 1) > 1e-6 ? 84d : 55d;
        var badgeX = Math.Clamp(Math.Max(rect.Left + 4, rect.Right - badgeWidth - 5), visible.Left + 2, Math.Max(visible.Left + 2, visible.Right - badgeWidth - 2));
        var badge = new Rect(badgeX, rect.Top + 5, badgeWidth, 19);
        if (IsHighContrast) { dc.DrawRoundedRectangle(B("Chrome"), new Pen(B("Ink"), .8), badge, 8, 8); Text(dc, label, new(badge.Left + 5, badge.Top + 2), 10, B("Ink")); }
        else { dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(145, 30, 33, 33)), null, badge, 8, 8); Text(dc, label, new(badge.Left + 5, badge.Top + 2), 10, Brushes.White); }
        dc.Pop();
        if ((selected || hovered) && rect.Width >= 24) DrawHandles(dc, rect, true, true, floating ? B("Accent") : selected ? B("Ink") : B("Secondary"));
        if (opacity < 1) dc.Pop();
        if (!floating && !item.Placeholder)
            hits.Add(new(rect, new("screen", segment.Id), segment.SourceStart, segment.SourceDuration, segment.PlaybackRate, item.Index, item.OutputStart, item.OutputDuration));
    }

    /// <summary>Empty slot a floating reorder copy will drop into.</summary>
    void DrawPrimarySlot(DrawingContext dc, PrimaryItem item)
    {
        var rect = new Rect(X(item.OutputStart) + 2, RulerHeight + 3, Math.Max(2, item.OutputDuration * Scale - 4), PrimaryHeight - 7);
        var visible = Rect.Intersect(rect, new Rect(LabelWidth - 24, rect.Y, ActualWidth - LabelWidth + 48, rect.Height));
        if (visible.IsEmpty) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(24, 70, 169, 134)), new Pen(B("Accent"), 1.4) { DashStyle = DashStyles.Dash }, visible, 10, 10);
    }

    /// <summary>Two grips at the clip edges, shown for selected / hovered clips (Mac 12pt handles).</summary>
    void DrawHandles(DrawingContext dc, Rect rect, bool leading, bool trailing, Brush brush)
    {
        var h = Math.Min(28, Math.Max(12, rect.Height - 16)); var top = rect.Top + (rect.Height - h) / 2;
        if (leading) dc.DrawRoundedRectangle(brush, null, new Rect(rect.Left + 3, top, 3.5, h), 1.75, 1.75);
        if (trailing) dc.DrawRoundedRectangle(brush, null, new Rect(rect.Right - 6.5, top, 3.5, h), 1.75, 1.75);
    }

    void DrawEffects(DrawingContext dc, string kind, IEnumerable<(Guid Id, double Start, double Duration, string Label)> clips, double y, Color color)
    {
        if (Project == null) return;
        var clipList = clips.ToList();
        // Sticker and blur lanes may stack: overlapping clips share the lane height in sub-rows (Mac packs them into rows).
        var rows = new Dictionary<Guid, int>(); var rowCount = 1;
        if (kind is "overlay" or "blur")
        {
            var ends = new List<double>();
            foreach (var c in clipList.OrderBy(c => c.Start))
            {
                var row = ends.FindIndex(e => e <= c.Start + 1e-6);
                if (row < 0) { if (ends.Count < 3) { ends.Add(0); row = ends.Count - 1; } else row = ends.Count - 1; }
                ends[row] = c.Start + c.Duration; rows[c.Id] = row;
            }
            rowCount = Math.Max(1, ends.Count);
        }
        var rowHeight = (LaneHeight - 15) / rowCount;
        var viewport = new Rect(LabelWidth - 40, 0, ActualWidth - LabelWidth + 80, ActualHeight);
        foreach (var c in clipList)
        {
            var pieces = new List<(Rect Rect, double Rate)>(); var outputStart = 0d;
            var row = rows.TryGetValue(c.Id, out var r0) ? r0 : 0;
            foreach (var seg in Project.Timeline.Segments)
            {
                var a = Math.Max(c.Start, seg.SourceStart); var b = Math.Min(c.Start + c.Duration, seg.SourceEnd);
                if (b > a) pieces.Add((new Rect(X(outputStart + (a - seg.SourceStart) / seg.PlaybackRate) + 1, y + 8 + row * rowHeight, Math.Max(4, (b - a) / seg.PlaybackRate * Scale - 2), rowHeight - (rowCount > 1 ? 2 : 0)), seg.PlaybackRate));
                outputStart += seg.OutputDuration;
            }
            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i].Rect; if (!piece.IntersectsWith(viewport)) continue;
                var drawn = Rect.Intersect(piece, viewport);
                var selected = IsSelected(kind, c.Id); var hovered = hoverSelection?.Kind == kind && hoverSelection.Id == c.Id;
                var fill = IsHighContrast ? selected ? B("Accent") : B("Chrome") : new SolidColorBrush(color);
                var label = IsHighContrast ? selected ? SystemColors.HighlightTextBrush : B("Ink") : EffectLabelBrush;
                var outline = IsHighContrast ? selected ? B("Accent") : B("Ink") : selected ? B("Ink") : new SolidColorBrush(Color.FromArgb(30, 60, 60, 60));
                var dim = dragging && drag != null && drag.Selection.Kind == kind && drag.Selection.Id == c.Id && (dragMode is "move" or "trim-start" or "trim-end");
                if (dim) dc.PushOpacity(.38);
                var radius = Math.Min(9, rowHeight / 2);
                dc.DrawRoundedRectangle(fill, new Pen(outline, selected ? 1.4 : .6), drawn, radius, radius);
                if (drawn.Width > 30) Text(dc, c.Label, new(Math.Max(drawn.Left, piece.Left) + 6, drawn.Top + Math.Max(2, (drawn.Height - 14) / 2)), 11, label, FontWeights.Medium, drawn.Width - 12, TextAlignment.Center);
                if ((selected || hovered) && piece.Width >= 22) DrawHandles(dc, piece, i == 0, i == pieces.Count - 1, selected ? B("Ink") : B("Secondary"));
                if (dim) dc.Pop();
                hits.Add(new(piece, new(kind, c.Id), c.Start, c.Duration, pieces[i].Rate, -1, 0, 0, i == 0, i == pieces.Count - 1));
            }
        }
    }

    void DrawRangeBar(DrawingContext dc)
    {
        if (!(RangeStart.HasValue && RangeEnd > RangeStart)) return;
        var a = X(RangeStart.Value); var b = X(RangeEnd.Value); var rangeColor = IsHighContrast ? SystemColors.HighlightColor : Color.FromRgb(70, 169, 134);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(IsHighContrast ? 70 : 50), rangeColor.R, rangeColor.G, rangeColor.B)), new Pen(B("Accent"), 1), new Rect(a, 3, Math.Max(1, b - a), 30), 4, 4);
    }

    /// <summary>Recording markers live in the ruler only and are clickable (Mac EditorTimelineRecordingMarkers).</summary>
    void DrawRulerMarkers(DrawingContext dc)
    {
        if (Project == null) return;
        var ordered = Project.Markers.OrderBy(m => m.SourceTime).ToList();
        var markerBrush = IsHighContrast ? B("Accent") : new SolidColorBrush(Color.FromRgb(220, 159, 73));
        for (var i = 0; i < ordered.Count; i++)
        {
            var marker = ordered[i];
            if (Project.Timeline.MapSourceToOutput(marker.SourceTime) is not { } t) continue;
            var x = X(t); if (x < LabelWidth - 20 || x > ActualWidth + 20) continue;
            dc.DrawLine(new Pen(markerBrush, 1.5), new(x, 4), new(x, RulerHeight));
            EditorIcon.DrawVector(dc, "flag", new Rect(x + 1, 3, 11, 12), markerBrush);
            var tip = $"{(string.IsNullOrWhiteSpace(marker.Label) ? "标记" : marker.Label)} {i + 1} · {TimeSpan.FromSeconds(t):m\\:ss\\.ff}";
            markerHits.Add(new(new Rect(x - 4, 2, 20, 22), marker.Id, tip));
        }
    }

    /// <summary>Restore mode: merge / restore buttons on every cut seam, head and tail (Mac EditorTimelineLayout seam buttons).</summary>
    void DrawSeamButtons(DrawingContext dc, List<PrimaryItem> layout)
    {
        if (Project == null) return;
        var timeline = Project.Timeline; var ink = B("Ink"); var surface = B("Chrome"); var accent = B("Accent");
        void Button(double x, string kind, int index, string tip)
        {
            if (x < LabelWidth - 12 || x > ActualWidth + 12) return;
            var center = new Point(x, 20);
            dc.DrawEllipse(surface, new Pen(accent, 1.4), center, 10, 10);
            var pen = new Pen(ink, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (kind == "merge")
            {
                dc.DrawLine(pen, new(x - 6, 20), new(x - 2, 20)); dc.DrawLine(pen, new(x + 2, 20), new(x + 6, 20));
                dc.DrawLine(pen, new(x - 3, 17), new(x - 1, 20)); dc.DrawLine(pen, new(x - 3, 23), new(x - 1, 20));
                dc.DrawLine(pen, new(x + 3, 17), new(x + 1, 20)); dc.DrawLine(pen, new(x + 3, 23), new(x + 1, 20));
            }
            else { dc.DrawLine(pen, new(x - 5, 20), new(x + 5, 20)); dc.DrawLine(pen, new(x, 15), new(x, 25)); }
            seamHits.Add(new(new Rect(x - 11, 9, 22, 22), kind, index, tip));
        }
        for (var i = 0; i + 1 < timeline.Segments.Count; i++)
        {
            var kind = TimelineSeamOperations.Classify(timeline, i);
            if (kind == TimelineSeamKind.None) continue;
            var x = X(layout.FirstOrDefault(p => p.Index == i + 1 && !p.Placeholder)?.OutputStart ?? timeline.Segments.Take(i + 1).Sum(p => p.OutputDuration));
            Button(x, kind == TimelineSeamKind.Merge ? "merge" : "restore", i, kind == TimelineSeamKind.Merge ? "合并：去掉此处的切分" : "还原：把此处被剪掉的素材放回来");
        }
        if (TimelineSeamOperations.CanRestoreLeading(timeline)) Button(X(0) + 14, "leading", 0, "还原开头被剪掉的素材");
        if (TimelineSeamOperations.CanRestoreTrailing(timeline, Project.Media.Duration)) Button(X(timeline.Duration) - 14, "trailing", timeline.Segments.Count - 1, "还原结尾被剪掉的素材");
    }

    void DrawEffectDragPreview(DrawingContext dc)
    {
        if (!dragging || drag == null || outcome is not { } result || drag.Selection.Kind == "screen" || Project == null) return;
        if (dragMode is not ("move" or "trim-start" or "trim-end")) return;
        var accent = B("Accent"); var lane = LaneTop(drag.Selection.Kind);
        var startOut = SourceToOutput(result.SourceStart); var endOut = SourceToOutput(result.SourceStart + result.SourceDuration);
        if (startOut is null || endOut is null) return;
        var rect = new Rect(X(startOut.Value) + 1, lane + 8, Math.Max(4, (endOut.Value - startOut.Value) * Scale - 2), LaneHeight - 15);
        var visible = Rect.Intersect(rect, new Rect(LabelWidth - 10, 0, ActualWidth + 20, ActualHeight));
        var fill = new SolidColorBrush(Color.FromArgb(70, 70, 169, 134));
        if (!visible.IsEmpty) dc.DrawRoundedRectangle(fill, new Pen(accent, 2), visible, 9, 9);
        // Other members of a multi-selection follow by the same shift.
        if (dragMode == "move" && MultiSelection.Count > 1 && MultiSelection.Contains(drag.Selection))
        {
            var original = SourceToOutput(drag.SourceStart);
            var shift = original is null ? 0 : (startOut.Value - original.Value) * Scale;
            foreach (var h in hits.Where(h => h.Selection != drag.Selection && MultiSelection.Contains(h.Selection) && h.Selection.Kind != "screen"))
            {
                var moved = h.Rect; moved.Offset(shift, 0);
                dc.DrawRoundedRectangle(fill, new Pen(accent, 1.4), moved, 9, 9);
            }
        }
    }

    void DrawCreateGhost(DrawingContext dc)
    {
        if (createLane == null || !createDragging) return;
        var top = LaneTop(createLane);
        var a = X(createStartTime); var b = X(createCurrentTime);
        var rect = new Rect(Math.Min(a, b), top + 8, Math.Max(3, Math.Abs(b - a)), LaneHeight - 15);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 70, 169, 134)), new Pen(B("Accent"), 1.6) { DashStyle = DashStyles.Dash }, rect, 9, 9);
        var label = TimeSpan.FromSeconds(Math.Abs(createCurrentTime - createStartTime)).ToString(@"m\:ss\.ff");
        if (rect.Width > 56) Text(dc, label, new(rect.Left + 6, rect.Top + 9), 11, B("Ink"));
    }

    void DrawSnapGuide(DrawingContext dc)
    {
        if (snapGuide is not { } guide || !GestureActive) return;
        var x = X(guide); var accent = B("Accent");
        dc.DrawLine(new Pen(accent, 1), new(x, RulerHeight - 4), new(x, ActualHeight));
        dc.DrawEllipse(accent, null, new(x, RulerHeight - 4), 3.5, 3.5);
    }

    // ---- model helpers -----------------------------------------------------------------------------------------------

    double? SourceToOutput(double source)
    {
        if (Project == null || !double.IsFinite(source)) return null;
        return Project.Timeline.MapSourceToOutput(Math.Max(0, source));
    }

    double OutputToSource(double output)
    {
        if (Project == null) return 0;
        return Project.Timeline.MapOutputToSource(Math.Clamp(output, 0, Project.Timeline.Duration));
    }
}
