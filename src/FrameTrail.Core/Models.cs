using System.Text.Json.Serialization;

namespace FrameTrail;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public NormalizedPoint Clamp(double inset = 0) => new(Math.Clamp(X, inset, 1 - inset), Math.Clamp(Y, inset, 1 - inset));
}

public enum PointerEventKind { Move, LeftClick, RightClick }
/// <summary>Pointer reconstruction profile. Smooth uses the continuous second-order spring; Medium and Rapid use monotone Hermite motion with progressively less lag; None passes the recorded trajectory straight through (Mac CursorMotionStyle.none).</summary>
public enum PointerSmoothing { Smooth, Medium, Rapid, None }
public sealed record PointerEvent(double Time, NormalizedPoint Location, PointerEventKind Kind = PointerEventKind.Move, string? CursorAsset = null);
public enum ZoomEasing { Linear, Cubic, Quintic, Spring, Quadratic, Custom }

/// <summary>Control points for a cubic Bezier time curve.</summary>
public readonly record struct ZoomBezierCurve(double X1 = .65, double Y1 = 0, double X2 = .35, double Y2 = 1)
{
    public static ZoomBezierCurve Linear => new(0, 0, 1, 1);
    public static ZoomBezierCurve Quadratic => new(.45, 0, .55, 1);
    public static ZoomBezierCurve Cubic => new(.65, 0, .35, 1);
    public static ZoomBezierCurve Quintic => new(.82, 0, .18, 1);
    public bool IsFinite => double.IsFinite(X1) && double.IsFinite(Y1) && double.IsFinite(X2) && double.IsFinite(Y2);
    public bool IsValid => IsFinite && X1 is >= 0 and <= 1 && X2 is >= 0 and <= 1 && Y1 is >= 0 and <= 1 && Y2 is >= 0 and <= 1;
}

/// <summary>Project-wide spring parameters matching RecorderCore.MotionStyle's screen spring.</summary>
public sealed record SpringAnimationSettings
{
    public double Mass { get; set; } = 2.4;
    public double Stiffness { get; set; } = 210;
    public double Damping { get; set; } = 42;
}

public sealed record ZoomClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    public double Scale { get; set; } = 1.6;
    public NormalizedPoint Focus { get; set; } = new(.5, .5);
    public double EnterDuration { get; set; } = .7;
    public double ExitDuration { get; set; } = .7;
    /// <summary>Progress phase offset for a continued zoom entrance, normalized to [0,1].</summary>
    public double EnterProgressOffset { get; set; }
    /// <summary>Progress phase offset for a continued zoom return, normalized to [0,1].</summary>
    public double ExitProgressOffset { get; set; }
    public ZoomEasing Easing { get; set; } = ZoomEasing.Spring;
    public ZoomBezierCurve CustomCurve { get; set; } = ZoomBezierCurve.Cubic;
    public bool Automatic { get; set; }
    /// <summary>Optional focus treatment evaluated with this zoom transition; null preserves older projects.</summary>
    public FocusEffect? FocusEffect { get; set; }
}
public sealed record TimelineSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double SourceStart { get; set; }
    public double SourceDuration { get; set; }
    public double PlaybackRate { get; set; } = 1;
    /// <summary>Optional audio settings for this source segment; null values inherit project or track settings.</summary>
    public PrimarySegmentAudioOverrides? AudioOverrides { get; set; }
    [JsonIgnore] public double SourceEnd => SourceStart + SourceDuration;
    [JsonIgnore] public double OutputDuration => SourceDuration / PlaybackRate;
    public TimelineSegment Copy(Guid? id = null) => new() { Id = id ?? Id, SourceStart = SourceStart, SourceDuration = SourceDuration, PlaybackRate = PlaybackRate, AudioOverrides = AudioOverrides is null ? null : AudioOverrides with { } };
}

/// <summary>Nullable per-segment overrides; unset values inherit the project or source-track setting.</summary>
public sealed record PrimarySegmentAudioOverrides
{
    public double? SystemVolume { get; set; }
    public double? MicrophoneVolume { get; set; }
    public bool? SystemMuted { get; set; }
    public bool? MicrophoneMuted { get; set; }

    public bool IsEmpty => SystemVolume is null && MicrophoneVolume is null && SystemMuted is null && MicrophoneMuted is null;
    public bool IsValid => (SystemVolume is null || double.IsFinite(SystemVolume.Value) && SystemVolume is >= 0 and <= 1)
        && (MicrophoneVolume is null || double.IsFinite(MicrophoneVolume.Value) && MicrophoneVolume is >= 0 and <= 1);
}

public sealed class ProjectTimeline
{
    public List<TimelineSegment> Segments { get; set; } = [];
    public double Duration => Segments.Sum(x => x.OutputDuration);

    public void Validate()
    {
        if (Segments.Count == 0) throw new InvalidDataException("时间线至少需要一个片段。");
        if (Segments.Select(x => x.Id).Distinct().Count() != Segments.Count) throw new InvalidDataException("片段 ID 重复。");
        foreach (var s in Segments)
            if (!double.IsFinite(s.SourceStart) || !double.IsFinite(s.SourceDuration) || !double.IsFinite(s.PlaybackRate) || s.SourceStart < 0 || s.SourceDuration <= 0 || s.PlaybackRate is < .25 or > 100 || !double.IsFinite(s.SourceEnd) || !double.IsFinite(s.OutputDuration) || s.AudioOverrides is { IsValid: false })
                throw new InvalidDataException($"片段 {s.Id} 的时间或速度无效。");
    }

    public (TimelineSegment Left, TimelineSegment Right) Split(Guid id, double sourceTime)
    {
        var i = Index(id); var s = Segments[i];
        if (!(sourceTime > s.SourceStart && sourceTime < s.SourceEnd)) throw new ArgumentOutOfRangeException(nameof(sourceTime), "拆分点必须位于片段内部。");
        var left = s.Copy(); left.SourceDuration = sourceTime - s.SourceStart;
        var right = s.Copy(Guid.NewGuid()); right.SourceStart = sourceTime; right.SourceDuration = s.SourceEnd - sourceTime;
        Segments[i] = left; Segments.Insert(i + 1, right); return (left, right);
    }

    public void Trim(Guid id, double sourceStart, double sourceEnd)
    {
        var s = Segments[Index(id)];
        if (!double.IsFinite(sourceStart) || !double.IsFinite(sourceEnd) || sourceStart < s.SourceStart || sourceEnd > s.SourceEnd || sourceEnd <= sourceStart) throw new ArgumentOutOfRangeException(nameof(sourceStart), "裁剪范围必须是片段内的非空区间。");
        s.SourceStart = sourceStart; s.SourceDuration = sourceEnd - sourceStart;
    }
    public void SetPlaybackRate(Guid id, double rate)
    {
        if (!double.IsFinite(rate) || rate is < .25 or > 100) throw new ArgumentOutOfRangeException(nameof(rate));
        Segments[Index(id)].PlaybackRate = rate;
    }
    public void Reorder(Guid id, int newIndex)
    {
        var old = Index(id); if ((uint)newIndex >= (uint)Segments.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        var value = Segments[old]; Segments.RemoveAt(old); Segments.Insert(newIndex, value);
    }
    public double MapOutputToSource(double outputTime)
    {
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        var elapsed = 0d;
        for (var i = 0; i < Segments.Count; i++) { var s = Segments[i]; var end = elapsed + s.OutputDuration; if (outputTime < end || i == Segments.Count - 1) return s.SourceStart + Math.Clamp(outputTime - elapsed, 0, s.OutputDuration) * s.PlaybackRate; elapsed = end; }
        var last = Segments[^1]; return last.SourceEnd;
    }
    public double? MapSourceToOutput(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        var elapsed = 0d;
        foreach (var s in Segments) { if (sourceTime >= s.SourceStart && sourceTime <= s.SourceEnd) return elapsed + (sourceTime - s.SourceStart) / s.PlaybackRate; elapsed += s.OutputDuration; }
        return null;
    }
    private int Index(Guid id) { var i = Segments.FindIndex(x => x.Id == id); return i >= 0 ? i : throw new KeyNotFoundException($"找不到片段 {id}。"); }
}

public sealed record MouseClockCalibration(double OffsetSeconds = 0, double Rate = 1)
{
    public double ToSourceTime(double pointerTime)
    {
        if (!double.IsFinite(pointerTime) || !double.IsFinite(OffsetSeconds) || !double.IsFinite(Rate) || Rate <= 0) throw new ArgumentOutOfRangeException(nameof(pointerTime));
        return Math.Max(0, pointerTime * Rate + OffsetSeconds);
    }
}

public sealed class FrameTrailProject
{
    public string Format { get; init; } = "FrameTrailProject";
    public int FormatVersion { get; init; } = 1;
    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public string? DisplayName { get; set; }
    public string MediaPath { get; set; } = "";
    public MediaAsset Media { get; set; } = new();
    public ProjectTimeline Timeline { get; set; } = new();
    public List<PointerEvent> PointerEvents { get; set; } = [];
    public List<RecordingMarker> Markers { get; set; } = [];
    public MouseClockCalibration MouseClock { get; set; } = new();
    public List<ZoomClip> ZoomClips { get; set; } = [];
    public List<ScreenMotionClip> ScreenMotions { get; set; } = [];
    public List<OverlayClip> Overlays { get; set; } = [];
    public List<BlurClip> BlurClips { get; set; } = [];
    public CameraTrack? Camera { get; set; }
    public List<CameraMotionClip> CameraMotions { get; set; } = [];
    public List<AudioTrack> AudioTracks { get; set; } = [];
    public FrameTrailSettings Settings { get; set; } = new();
}

public sealed record MediaAsset
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double Duration { get; set; }
    public double FrameRate { get; set; } = 30;
}
public sealed record RecordingMarker(Guid Id, double SourceTime, string Label);

public sealed record FrameTrailSettings
{
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public string Background { get; set; } = "#202124";
    public double Padding { get; set; } = .08;
    public double CornerRadius { get; set; } = .02;
    public double Shadow { get; set; } = .3;
    public bool CursorVisible { get; set; } = true;
    public PointerSmoothing PointerSmoothing { get; set; } = FrameTrail.PointerSmoothing.Smooth;
    public SpringAnimationSettings Spring { get; set; } = new();
    public bool AutoZoom { get; set; } = true;
    public double MainAudioVolume { get; set; } = 1;
    public bool MainAudioMuted { get; set; }
    public OpeningSettings? Opening { get; set; }
    public CanvasSettings? Canvas { get; set; }
    public BackgroundSettings? BackgroundStyle { get; set; }
    public CursorSettings? Cursor { get; set; }
    /// <summary>
    /// Screen-card geometry version. 1 (also what a project file without the field means) keeps the original Windows layout: the card is
    /// fitted to the whole source, ScreenMotion positions the card centre and zoom crops inside the card. 2 is the Mac card model
    /// (cropped aspect fit, placement/focus framing, zoom magnifies the whole card, pivot tilt). New projects and DogSC imports write 2.
    /// </summary>
    public int LayoutModel { get; set; } = 1;
    /// <summary>LayoutModel value of the Mac card model.</summary>
    public const int MacLayoutModel = 2;
}

public sealed record FrameScene(
    double OutputTime,
    double SourceTime,
    NormalizedPoint CropOrigin,
    NormalizedPoint CropSize,
    double Zoom,
    NormalizedPoint? Cursor,
    double ClickStrength,
    string Background,
    double Padding,
    double CornerRadius,
    int CanvasWidth,
    int CanvasHeight,
    double Shadow,
    CanvasSettings? Canvas = null,
    BackgroundSettings? BackgroundStyle = null,
    CursorSettings? CursorAppearance = null,
    ScreenMotionState? ScreenMotion = null,
    FocusEffect? Focus = null,
    double FocusProgress = 0,
    FrameCameraScene? Camera = null,
    IReadOnlyList<FrameOverlayScene>? Overlays = null,
    IReadOnlyList<FrameBlurScene>? BlurRegions = null,
    IReadOnlyList<CameraMotionClip>? CameraMotions = null,
    int SourceWidth = 0,
    int SourceHeight = 0,
    FrameOpeningState? Opening = null,
    FrameOpeningState? CameraOpening = null,
    FrameOpeningState? OverlayOpening = null,
    double CursorRotationRadians = 0,
    double? ClickProgress = null,
    PointerEventKind? ActiveClickKind = null,
    string? CursorAsset = null,
    FrameOverlayBackdrop? OverlayBackdrop = null,
    ZoomSampleState? ZoomSample = null,
    ScreenMotionBlend? ScreenBlend = null,
    int LayoutModel = 1);
