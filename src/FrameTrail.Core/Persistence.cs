using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail;

/// <summary>Persists FrameTrail's own versioned project format with a recoverable previous copy.</summary>
public static class FrameTrailProjectStore
{
    public const string BackupSuffix = ".bak";
    private static readonly int[] ReplaceRetryDelaysMilliseconds = [20, 40, 80, 160];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static FrameTrailProject Load(string projectFile)
    {
        var path = Path.GetFullPath(projectFile);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var project = JsonSerializer.Deserialize<FrameTrailProject>(stream, JsonOptions) ?? throw new InvalidDataException("项目文件为空。");
        Validate(project, Path.GetDirectoryName(path)!); return project;
    }
    public static void Save(string projectFile, FrameTrailProject project)
    {
        ArgumentNullException.ThrowIfNull(project); var path = Path.GetFullPath(projectFile); var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory); Validate(project, directory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, project, JsonOptions); stream.Flush(true); }
            if (File.Exists(path)) File.Copy(path, path + BackupSuffix, true);
            MoveWithSharingRetry(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static FrameTrailProject RestoreBackup(string projectFile)
    {
        var path = Path.GetFullPath(projectFile); var backup = path + BackupSuffix;
        if (!File.Exists(backup)) throw new FileNotFoundException("找不到项目备份。", backup);
        var restored = Load(backup); var temp = path + ".restore-" + Guid.NewGuid().ToString("N");
        try { File.Copy(backup, temp); MoveWithSharingRetry(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return restored;
    }

    private static void MoveWithSharingRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, true);
                return;
            }
            catch (Exception ex) when (IsTransientWindowsReplaceError(ex))
            {
                if (attempt >= ReplaceRetryDelaysMilliseconds.Length)
                    throw new IOException($"无法替换项目文件“{destination}”：有限重试后仍被占用或访问被拒绝。", ex);
                Thread.Sleep(ReplaceRetryDelaysMilliseconds[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法替换项目文件“{destination}”。", ex);
            }
        }
    }

    private static bool IsTransientWindowsReplaceError(Exception exception)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Exception? current = exception;
        while (current is not null)
        {
            var code = Marshal.GetHRForException(current) & 0xffff;
            if (code is 5 or 32 or 33) return true;
            current = current.InnerException;
        }
        return false;
    }
    public static string ResolveMediaPath(string projectFile, FrameTrailProject project)
    {
        return ResolveAssetPath(projectFile, project.MediaPath);
    }
    public static string ResolveAssetPath(string projectFile, string relativePath)
    {
        ValidateRelativeMediaPath(relativePath);
        var root = Path.GetFullPath(Path.GetDirectoryName(projectFile)!);
        var result = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && !string.Equals(result, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("素材路径越出项目目录。");
        // Lexical containment alone is insufficient when an imported project contains a
        // symlink/junction under its directory. Reject every existing reparse point in the
        // resolved asset path so media readers cannot follow it outside the project root.
        var current = root;
        foreach (var component in relativePath.Replace('\\', '/').Split('/'))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("项目素材路径不能经过符号链接或重解析点。");
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        return result;
    }
    private static void Validate(FrameTrailProject project, string projectDirectory)
    {
        if (project.Format != "FrameTrailProject" || project.FormatVersion != 1 || project.ProjectId == Guid.Empty) throw new InvalidDataException("不支持此项目格式或版本；FrameTrail 项目与原 dogscproject 格式不同。");
        project.Timeline.Validate(); _ = ResolveMediaPath(Path.Combine(projectDirectory, "project.frametrail"), project);
        if (project.Media.Duration < 0 || project.Media.Width < 0 || project.Media.Height < 0 || !double.IsFinite(project.Media.Duration + project.Media.Width + project.Media.Height)
            || !double.IsFinite(project.Media.FrameRate) || project.Media.FrameRate is <= 0 or > 240) throw new InvalidDataException("媒体元数据无效。");
        if (project.Settings.CanvasWidth < 2 || project.Settings.CanvasHeight < 2 || !double.IsFinite(project.Settings.Padding) || project.Settings.Padding is < 0 or > .5 || !double.IsFinite(project.Settings.CornerRadius) || project.Settings.CornerRadius is < 0 or > .5 || !double.IsFinite(project.Settings.Shadow) || project.Settings.Shadow is < 0 or > 1) throw new InvalidDataException("画布设置无效。");
        if (!Enum.IsDefined(project.Settings.PointerSmoothing) || !double.IsFinite(project.Settings.MainAudioVolume) || project.Settings.MainAudioVolume is < 0 or > 4 || project.DisplayName?.Length > 240) throw new InvalidDataException("指针平滑模式、项目名称或主音轨音量无效。");
        if (!ValidSpring(project.Settings.Spring)) throw new InvalidDataException("动画弹簧参数无效。");
        if (project.Settings.Opening is { } opening && (!Enum.IsDefined(opening.Preset) || !Enum.IsDefined(opening.Easing) || opening.ElementCurve is { } elementCurve && !Enum.IsDefined(elementCurve) || !ValidCurve(opening.CustomCurve) || !double.IsFinite(opening.Duration) || opening.Duration is <= 0 or > 10 || !double.IsFinite(opening.Stagger) || opening.Stagger is < 0 or > 1.2 || opening.IncludedElements is null || opening.ElementOrder is null || opening.IncludedElements.Any(x => !Enum.IsDefined(x)) || opening.ElementOrder.Any(x => !Enum.IsDefined(x)) || opening.IncludedElements.Distinct().Count() != opening.IncludedElements.Count || opening.ElementOrder.Distinct().Count() != opening.ElementOrder.Count || !double.IsFinite(opening.ScaleFrom) || opening.ScaleFrom is < .1 or > 4 || !double.IsFinite(opening.OffsetX) || Math.Abs(opening.OffsetX) > 1 || !double.IsFinite(opening.OffsetY) || Math.Abs(opening.OffsetY) > 1 || !double.IsFinite(opening.RotationFrom) || Math.Abs(opening.RotationFrom) > 180 || !double.IsFinite(opening.OpacityFrom) || opening.OpacityFrom is < 0 or > 1)) throw new InvalidDataException("开场动画参数无效。");
        if (project.PointerEvents.Any(x => !double.IsFinite(x.Time) || x.Time < 0 || !x.Location.IsFinite) || project.ZoomClips.Any(x => !double.IsFinite(x.Start + x.End + x.Scale + x.EnterDuration + x.ExitDuration + x.EnterProgressOffset + x.ExitProgressOffset) || !Enum.IsDefined(x.Easing) || !ValidCurve(x.CustomCurve) || x.Start < 0 || x.End <= x.Start || x.Scale < 1 || x.EnterDuration is < 0 or > 5 || x.ExitDuration is < 0 or > 5 || x.EnterProgressOffset is < 0 or > 1 || x.ExitProgressOffset is < 0 or > 1 || !x.Focus.IsFinite || x.FocusEffect is { } zoomFocusEffect && !ValidFocus(zoomFocusEffect))) throw new InvalidDataException("指针或缩放轨道数据无效。");
        if (project.Settings.Canvas is { } canvas && (!Enum.IsDefined(canvas.Frame) || canvas.AspectWidth < 2 || canvas.AspectHeight < 2 || !canvas.Crop.IsFinite || !IsUnitRect(canvas.Crop) || !canvas.ContentPosition.IsFinite || canvas.ContentScale is < .1 or > 4 || !double.IsFinite(canvas.ContentScale) || canvas.FrameScale is < .6 or > 1.6 || !double.IsFinite(canvas.FrameScale) || canvas.BorderWidth is < 0 or > 60 || !double.IsFinite(canvas.BorderWidth) || !double.IsFinite(canvas.FrameToolbarScale) || canvas.FrameToolbarScale is < .65 or > 1.6 || canvas.FrameOuterRadius is { } outerRadius && (!double.IsFinite(outerRadius) || outerRadius is < 0 or > 160))) throw new InvalidDataException("画布裁切或布局无效。");
        if (project.Settings.BackgroundStyle is { } background)
        {
            if (!Enum.IsDefined(background.Kind) || !Enum.IsDefined(background.ImageFit) || !Enum.IsDefined(background.Pattern) || !Enum.IsDefined(background.Preset) || background.Blur is < 0 or > 1 || !double.IsFinite(background.Blur) || !double.IsFinite(background.VideoStartTime) || background.VideoStartTime < 0 || !double.IsFinite(background.AngleDegrees) || !double.IsFinite(background.PatternSpacing) || background.PatternSpacing is < 8 or > 512 || !double.IsFinite(background.PatternStrength) || background.PatternStrength is < 0 or > 1 || !double.IsFinite(background.Speed) || background.Speed is < 0 or > 10) throw new InvalidDataException("背景设置无效。");
            if (background.Kind == BackgroundKind.Image) ValidateRelativeMediaPath(background.ImagePath ?? "");
            if (background.Kind == BackgroundKind.Video) ValidateRelativeMediaPath(background.VideoPath ?? "");
        }
        if (project.Settings.Cursor is { } cursor && (!Enum.IsDefined(cursor.Style) || !Enum.IsDefined(cursor.ClickEffect) || !double.IsFinite(cursor.Scale) || cursor.Scale is < .1 or > 5 || !double.IsFinite(cursor.IdleSeconds) || cursor.IdleSeconds is < 0 or > 60 || !double.IsFinite(cursor.ClickDuration) || cursor.ClickDuration is <= 0 or > 5 || !double.IsFinite(cursor.MotionTiltStrength) || cursor.MotionTiltStrength is < 2 or > 4 || !double.IsFinite(cursor.ClickScale) || cursor.ClickScale is < .4 or > 3 || !double.IsFinite(cursor.ClickOpacity) || cursor.ClickOpacity is < .05 or > 1)) throw new InvalidDataException("光标设置无效。");
        if (project.ScreenMotions.Count > 10000 || project.ScreenMotions.Any(x => !Enum.IsDefined(x.Easing) || !ValidCurve(x.CustomCurve) || !double.IsFinite(x.Start) || x.Start < 0 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !double.IsFinite(x.EnterDuration) || x.EnterDuration < 0 || x.EnterDuration > x.Duration || !double.IsFinite(x.ReturnDuration) || x.ReturnDuration is < 0 or > 5 || !ValidMotion(x.Target) || x.Focus is { } effect && !ValidFocus(effect))) throw new InvalidDataException("屏幕运动轨道无效。");
        if (project.Overlays.Count > 10000 || project.Overlays.Any(x => !Enum.IsDefined(x.EnterMotion) || !Enum.IsDefined(x.ExitMotion) || string.IsNullOrWhiteSpace(x.MediaPath) || !double.IsFinite(x.Start) || x.Start < 0 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !x.Rect.IsFinite || !IsUnitRect(x.Rect) || !double.IsFinite(x.Rotation) || !double.IsFinite(x.Opacity) || x.Opacity is < 0 or > 1 || !double.IsFinite(x.EnterDuration) || x.EnterDuration is < 0 or > 5 || !double.IsFinite(x.ExitDuration) || x.ExitDuration is < 0 or > 5 || !double.IsFinite(x.CornerRadius) || x.CornerRadius is < 0 or > .5 || !double.IsFinite(x.Blur) || x.Blur is < 0 or > 100)) throw new InvalidDataException("贴图轨道无效。");
        foreach (var overlay in project.Overlays)
        {
            ValidateRelativeMediaPath(overlay.MediaPath);
            if (overlay.AnimationCurve is { } curve && !Enum.IsDefined(curve)
                || !double.IsFinite(overlay.BorderWidth) || overlay.BorderWidth is < 0 or > 60
                || string.IsNullOrWhiteSpace(overlay.BorderColor) || overlay.BorderColor.Length > 32
                || !double.IsFinite(overlay.ShadowOpacity) || overlay.ShadowOpacity is < 0 or > 1
                || !double.IsFinite(overlay.ShadowRadius) || overlay.ShadowRadius is < 0 or > 160
                || !double.IsFinite(overlay.ShadowOffsetX) || Math.Abs(overlay.ShadowOffsetX) > 250
                || !double.IsFinite(overlay.ShadowOffsetY) || Math.Abs(overlay.ShadowOffsetY) > 250
                || !double.IsFinite(overlay.BackdropBlur) || overlay.BackdropBlur is < 0 or > 100)
                throw new InvalidDataException("贴图边框、阴影或转场曲线无效。");
        }
        if (project.BlurClips.Count > 10000 || project.BlurClips.Any(x => !Enum.IsDefined(x.Kind) || !double.IsFinite(x.Start) || x.Start < 0 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !x.Rect.IsFinite || !IsUnitRect(x.Rect) || !double.IsFinite(x.Strength) || x.Strength is < 0 or > 1 || !double.IsFinite(x.CornerRadius) || x.CornerRadius is < 0 or > .5 || !double.IsFinite(x.EnterDuration) || x.EnterDuration is < 0 or > 5 || !double.IsFinite(x.ExitDuration) || x.ExitDuration is < 0 or > 5 || !double.IsFinite(x.SpotlightDimming) || x.SpotlightDimming is < 0 or > 1 || x.TransitionStyle is { } transitionStyle && !Enum.IsDefined(transitionStyle))) throw new InvalidDataException("区域柔化轨道无效。");
        if (project.Camera is { } camera && (string.IsNullOrWhiteSpace(camera.MediaPath) || !Enum.IsDefined(camera.Shape) || camera.Layout is { } cameraLayout && !Enum.IsDefined(cameraLayout) || camera.AssetAspectRatio is { } assetAspect && (!double.IsFinite(assetAspect) || assetAspect is < .1 or > 10) || !camera.Crop.IsFinite || !IsUnitRect(camera.Crop) || !double.IsFinite(camera.SourceStart) || camera.SourceStart < 0 || !double.IsFinite(camera.Duration) || camera.Duration <= 0 || !double.IsFinite(camera.TimelineStart) || camera.TimelineStart is < -86400 or > 86400 || !camera.Position.IsFinite || !double.IsFinite(camera.Size) || camera.Size is < .02 or > 1 || !double.IsFinite(camera.CornerRadius) || (camera.Layout is null ? camera.CornerRadius is < 0 or > .5 : camera.CornerRadius is < 0 or > 1) || !double.IsFinite(camera.BorderWidth) || camera.BorderWidth is < 0 or > 18 || !double.IsFinite(camera.Opacity) || camera.Opacity is < 0 or > 1 || !double.IsFinite(camera.Volume) || camera.Volume is < 0 or > 4 || !double.IsFinite(camera.ScaleDuringZoom) || camera.ScaleDuringZoom is < .1 or > 2 || !double.IsFinite(camera.ShadowStrength) || camera.ShadowStrength is < 0 or > 1)) throw new InvalidDataException("摄像头轨道无效。");
        if (project.Camera?.SyncSegments is { } cameraSegments && (cameraSegments.Count > 10000
            || cameraSegments.Any(x => !double.IsFinite(x.SourceStart) || x.SourceStart < 0 || !double.IsFinite(x.TimelineStart) || x.TimelineStart is < -86400 or > 86400 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !double.IsFinite(x.SourceRate) || x.SourceRate is <= 0 or > 100 || !double.IsFinite(x.SourceStart + x.Duration * x.SourceRate) || !double.IsFinite(x.TimelineStart + x.Duration))
            || cameraSegments.OrderBy(x => x.TimelineStart).Zip(cameraSegments.OrderBy(x => x.TimelineStart).Skip(1), (a, b) => a.TimelineStart + a.Duration <= b.TimelineStart + 1e-6).Any(valid => !valid))) throw new InvalidDataException("摄像头同步片段无效或时间范围重叠。");
        if (project.Camera is { } cameraPath) ValidateRelativeMediaPath(cameraPath.MediaPath);
        if (project.CameraMotions.Count > 10000 || project.CameraMotions.Any(x => !Enum.IsDefined(x.Easing) || !ValidCurve(x.CustomCurve) || x.TargetLayout is { } targetLayout && !Enum.IsDefined(targetLayout) || x.TargetRoundness is { } targetRoundness && (!double.IsFinite(targetRoundness) || targetRoundness is < 0 or > 1) || x.TargetOpacity is { } targetOpacity && (!double.IsFinite(targetOpacity) || targetOpacity is < 0 or > 1) || !double.IsFinite(x.Start) || x.Start < 0 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !double.IsFinite(x.EnterDuration) || x.EnterDuration is < 0 || x.EnterDuration > x.Duration || !double.IsFinite(x.ReturnDuration) || x.ReturnDuration is < 0 or > 5 || !x.TargetPosition.IsFinite || x.TargetPosition.X is < 0 or > 1 || x.TargetPosition.Y is < 0 or > 1 || !double.IsFinite(x.TargetSize) || x.TargetSize is < .02 or > 1)) throw new InvalidDataException("摄像头运动轨道无效。");
        if (project.AudioTracks.Count > 64 || project.AudioTracks.Any(x => string.IsNullOrWhiteSpace(x.MediaPath) || !double.IsFinite(x.SourceStart) || x.SourceStart < 0 || !double.IsFinite(x.Duration) || x.Duration <= 0 || !double.IsFinite(x.OffsetSeconds) || Math.Abs(x.OffsetSeconds) > 86400 || !double.IsFinite(x.SourceRate) || x.SourceRate is <= 0 or > 100 || x.Source is { } audioSource && !Enum.IsDefined(audioSource) || !double.IsFinite(x.Volume) || x.Volume is < 0 or > 4
            || x.SyncSegments is { } audioSegments && (audioSegments.Count > 10000 || audioSegments.Any(segment => !double.IsFinite(segment.SourceStart) || segment.SourceStart < 0 || !double.IsFinite(segment.TimelineStart) || segment.TimelineStart is < -86400 or > 86400 || !double.IsFinite(segment.Duration) || segment.Duration <= 0 || !double.IsFinite(segment.SourceRate) || segment.SourceRate is <= 0 or > 100 || !double.IsFinite(segment.SourceStart + segment.Duration * segment.SourceRate) || !double.IsFinite(segment.TimelineStart + segment.Duration)) || audioSegments.OrderBy(segment => segment.TimelineStart).Zip(audioSegments.OrderBy(segment => segment.TimelineStart).Skip(1), (a, b) => a.TimelineStart + a.Duration <= b.TimelineStart + 1e-6).Any(valid => !valid)))) throw new InvalidDataException("独立音轨无效。");
        foreach (var audio in project.AudioTracks) ValidateRelativeMediaPath(audio.MediaPath);
    }
    private static bool IsUnitRect(NormalizedRect rect) => rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0 && rect.X + rect.Width <= 1 && rect.Y + rect.Height <= 1;
    private static bool ValidCurve(ZoomBezierCurve curve) => curve.IsValid;
    private static bool ValidSpring(SpringAnimationSettings? spring) => spring is not null
        && double.IsFinite(spring.Mass) && spring.Mass is >= .01 and <= 100
        && double.IsFinite(spring.Stiffness) && spring.Stiffness is >= .01 and <= 10000
        && double.IsFinite(spring.Damping) && spring.Damping is >= .01 and <= 1000;
    private static bool ValidMotion(ScreenMotionState state) => state.Position.IsFinite && state.Position.X is >= 0 and <= 1 && state.Position.Y is >= 0 and <= 1 && double.IsFinite(state.Scale) && state.Scale is >= .2 and <= 4 && double.IsFinite(state.RotationX) && Math.Abs(state.RotationX) <= 80 && double.IsFinite(state.RotationY) && Math.Abs(state.RotationY) <= 80 && double.IsFinite(state.RotationZ) && Math.Abs(state.RotationZ) <= 360 && double.IsFinite(state.Perspective) && state.Perspective is >= 0 and <= 2;
    private static bool ValidFocus(FocusEffect effect) => Enum.IsDefined(effect.Shape) && Enum.IsDefined(effect.Target) && effect.Center.IsFinite && double.IsFinite(effect.Size) && effect.Size is >= .1 and <= 1 && double.IsFinite(effect.Blur) && effect.Blur is >= 0 and <= 1 && double.IsFinite(effect.Softness) && effect.Softness is >= 0 and <= 1 && double.IsFinite(effect.Dimming) && effect.Dimming is >= 0 and <= .75 && double.IsFinite(effect.AngleDegrees) && Math.Abs(effect.AngleDegrees) <= 360;
    private static void ValidateRelativeMediaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\')) throw new InvalidDataException("素材必须使用项目目录内的相对路径。");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(x => x is ".." or "")) throw new InvalidDataException("素材相对路径无效。");
    }
}

/// <summary>Bounded, snapshot-based undo/redo for immutable or cloneable project states.</summary>
public sealed class UndoRedoHistory<T> where T : class
{
    private readonly Func<T, T> clone;
    private readonly int capacity;
    private readonly List<T> undo = [];
    private readonly List<T> redo = [];
    private readonly List<string?> undoLabels = [];
    private readonly List<string?> redoLabels = [];
    /// <summary>Action name of the step <see cref="Undo()"/> would revert (Mac: "撤销 移动运镜").</summary>
    public string? UndoLabel => undoLabels.Count > 0 ? undoLabels[^1] : null;
    /// <summary>Action name of the step <see cref="Redo()"/> would re-apply.</summary>
    public string? RedoLabel => redoLabels.Count > 0 ? redoLabels[^1] : null;
    public T Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public UndoRedoHistory(T initial, Func<T, T> clone, int capacity = 100)
    { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); Current = clone(initial); this.clone = clone; this.capacity = capacity; }
    public void Commit(T next) => Commit(next, null);
    public void Commit(T next, string? label)
    {
        undo.Add(clone(Current)); undoLabels.Add(label);
        if (undo.Count > capacity) { undo.RemoveAt(0); undoLabels.RemoveAt(0); }
        Current = clone(next); redo.Clear(); redoLabels.Clear();
    }
    public T Undo() => Undo(null);
    public T Redo() => Redo(null);

    /// <summary>Runs persistence before changing either history stack; a failure leaves history intact.</summary>
    public T Undo(Action<T>? persist) => Restore(undo, redo, undoLabels, redoLabels, persist, "没有可撤销的操作。");
    public T Redo(Action<T>? persist) => Restore(redo, undo, redoLabels, undoLabels, persist, "没有可重做的操作。");

    private T Restore(List<T> source, List<T> destination, List<string?> sourceLabels, List<string?> destinationLabels, Action<T>? persist, string emptyMessage)
    {
        if (source.Count == 0) throw new InvalidOperationException(emptyMessage);
        var previous = clone(Current);
        var next = clone(source[^1]);
        var result = clone(next);
        persist?.Invoke(result);
        destination.Add(previous);
        destinationLabels.Add(sourceLabels.Count > 0 ? sourceLabels[^1] : null);
        Current = next;
        source.RemoveAt(source.Count - 1);
        if (sourceLabels.Count > 0) sourceLabels.RemoveAt(sourceLabels.Count - 1);
        return result;
    }
}
