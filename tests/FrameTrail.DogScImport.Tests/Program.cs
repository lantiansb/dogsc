using System.Security.Cryptography;
using FrameTrail;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("失败：" + label);
    Console.WriteLine("通过：" + label);
}

static void Write(string root, string relative, byte[] data)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, data);
}

var temporaryRoot = Path.Combine(Path.GetTempPath(), "frametrail-dogsc-import-fixture-" + Guid.NewGuid().ToString("N"));
var source = Path.Combine(temporaryRoot, "schema-fixture.dogscproject");
var destination = Path.Combine(temporaryRoot, "Imported.frametrail");
Directory.CreateDirectory(source);
try
{
    var screenBytes = Enumerable.Range(0, 257).Select(i => (byte)(i % 251)).ToArray();
    var cameraBytes = "camera-fixture"u8.ToArray();
    var microphoneBytes = "microphone-fixture"u8.ToArray();
    var backgroundVideoBytes = "background-video-fixture"u8.ToArray();
    var stickerBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 40, 0, 0, 0, 20 };
    Write(source, "media/screen-0001.mp4", screenBytes);
    Write(source, "media/camera-0001.mov", cameraBytes);
    Write(source, "media/microphone.m4a", microphoneBytes);
    Write(source, "assets/background.mp4", backgroundVideoBytes);
    Write(source, "assets/coast.png", stickerBytes);
    Write(source, "events/pointer.jsonl", """
        {"time":0.25,"location":{"x":0.2,"y":0.3},"kind":"move"}
        {"time":1.0,"location":{"x":0.72,"y":0.61},"kind":"leftClick","modifiers":[],"cursorAssetID":"system.arrow"}
        {"time":2.0,"location":{"x":0.8,"y":0.7},"kind":"rightClick"}
        """u8.ToArray());

    var sourceJson = """
    {
      "version":27,
      "title":"迁移验证工程",
      "createdAt":"2026-10-08T12:00:00Z",
      "media":{
        "screen":{"relativePath":"media/screen-0001.mp4","sourceTimeScale":1.2},
        "camera":{"relativePath":"media/camera-0001.mov","startOffset":0.1,"sourceStartTime":0.2,"sourceEndTime":3.8,"sourceTimeScale":1.2,"syncAnchors":[{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","sourceTime":1.0,"offset":0.1},{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","sourceTime":2.0,"offset":0.0}]},
        "microphone":{"relativePath":"media/microphone.m4a","startOffset":0.05,"sourceStartTime":0.1,"sourceEndTime":3.9,"sourceTimeScale":0.8,"syncAnchors":[{"id":"cccccccc-cccc-cccc-cccc-cccccccccccc","sourceTime":1.0,"offset":-0.05},{"id":"dddddddd-dddd-dddd-dddd-dddddddddddd","sourceTime":2.0,"offset":0.0}]},
        "pointerEvents":{"relativePath":"events/pointer.jsonl","startOffset":0.2,"sourceStartTime":0.4,"sourceTimeScale":1.2}
      },
      "timeline":{
        "sourceSequence":{"kind":"edited","segments":[
          {"id":"11111111-1111-1111-1111-111111111111","sourceStart":0.5,"sourceDuration":2.0,"playbackRate":2.0},
          {"id":"22222222-2222-2222-2222-222222222222","sourceStart":2.5,"sourceDuration":1.0,"playbackRate":1.0}
        ]},
        "primarySegmentAudioOverrides":{
          "11111111-1111-1111-1111-111111111111":{"systemVolume":0.3,"isMicrophoneMuted":true},
          "22222222-2222-2222-2222-222222222222":{"microphoneVolume":0.45,"isSystemMuted":true}
        },
        "zoomClips":[{"id":"33333333-3333-3333-3333-333333333333","startTime":0.8,"endTime":1.5,"scale":1.8,"focus":{"x":0.72,"y":0.61},"origin":"automatic","easing":"custom","customCurve":{"x1":0.2,"y1":0.05,"x2":0.8,"y2":0.95},"enterDuration":0.3,"exitDuration":0.4,"enterProgressOffset":0.22,"exitProgressOffset":0.37,"focusEffect":{"shape":"linear","target":"pointer","center":{"x":0.15,"y":0.25},"size":0.4,"blur":0.7,"softness":0.3,"dimming":0.12,"angleDegrees":35}}],
        "screenMotionClips":[{"id":"44444444-4444-4444-4444-444444444444","timing":{"startTime":1.1,"duration":2.0,"leadInDuration":0.5,"returnDuration":0.5,"easing":"custom","customCurve":{"x1":0.25,"y1":0.1,"x2":0.75,"y2":0.9}},"target":{"position":{"x":0.6,"y":0.5},"scale":1.25,"rotationX":8,"rotationY":-4,"rotationZ":2,"perspective":0.6},"focusEffect":{"shape":"radial","target":"pointer","center":{"x":0.5,"y":0.5},"size":0.4,"blur":0.6,"softness":0.3,"dimming":0.2}}],
        "cameraMotionClips":[{"id":"66666666-6666-6666-6666-666666666666","timing":{"startTime":1.5,"duration":1.0,"leadInDuration":0.3,"returnDuration":0.4,"easing":"custom","customCurve":{"x1":0.3,"y1":0.2,"x2":0.7,"y2":0.8}},"target":{"layout":{"kind":"shape","shape":"circle"},"position":{"x":0.72,"y":0.75},"size":0.32,"roundness":0.5,"opacity":0.9}}],
        "mosaicClips":[{"id":"77777777-7777-7777-7777-777777777777","timing":{"startTime":0.8,"duration":1.6},"sourceRect":{"x":0.2,"y":0.25,"width":0.3,"height":0.2},"cornerRadius":0.08,"style":"blur","intensity":0.7,"spotlightDimming":0.22,"transitionStyle":"smooth","transitionInDuration":0.25,"transitionOutDuration":0.3}],
        "stickerClips":[{"id":"88888888-8888-8888-8888-888888888888","timing":{"startTime":0.5,"duration":2.5},"relativePath":"assets/coast.png","position":{"x":0.75,"y":0.75},"width":0.3,"rotationDegrees":4,"opacity":0.85,"cornerRadius":18,"borderWidth":2,"borderColor":"#FFFFFF","shadowOpacity":0.3,"shadowRadius":20,"shadowOffsetX":0,"shadowOffsetY":5,"animation":"pop","animationCurve":"swift","exitAnimation":"fade","enterDuration":0.4,"exitDuration":0.3,"backdropBlur":12,"backdropBlurIncludesCamera":false,"hidesScreen":false,"hidesCamera":false,"layerIndex":2}]
      },
      "openingSequence":{"isEnabled":true,"preset":"轻 3D","motionCurve":"swift","duration":2.4,"stagger":0.2,"includedElements":["屏幕","贴图"],"elementOrder":["屏幕","摄像头","贴图"]},
      "recordingMarkers":[{"id":"55555555-5555-5555-5555-555555555555","sourceTime":1.25,"number":2}],
      "canvas":{"aspectRatio":"16:9","padding":90,"cornerRadius":24,"shadowStrength":0.4,"contentPosition":{"x":0.4,"y":0.6},"contentScale":1.2,"crop":{"x":0.1,"y":0.05,"width":0.8,"height":0.9},"screenFrame":"windowDark","screenFrameScale":1.1,"screenFrameToolbarScale":1.3,"screenFrameOuterCornerRadius":32,"screenFrameContentCornerRadius":8,"borderWidth":3,"borderHex":"#AABBCC","screenFrameTitle":"Demo","screenFrameBrowserAddress":"example.test","backgroundSource":{"kind":"projectVideo","relativePath":"assets/background.mp4"}},
      "cursorStyle":{"assetID":"system.arrow","hideWhenIdle":true,"idleDelay":2.0,"size":1.2,"clickEffectStyle":"ripple","clickColor":"#FFAA00","clickScale":1.35,"clickOpacity":0.65,"motionTiltStrength":3.2},
      "motion":{"cursor":"none","screenSpringMass":1.9,"screenSpringStiffness":320,"screenSpringDamping":36},
      "camera":{"shape":"circle","position":{"x":0.83,"y":0.78},"size":0.24,"borderWidth":2,"shadowStrength":0.42,"scaleDuringZoom":0.62,"roundness":0.75,"isMirrored":true,"isHidden":true,"contentPosition":{"x":0.55,"y":0.45},"contentScale":1.4},
      "audio":{"systemVolume":0.82,"microphoneVolume":0.73,"isSystemMuted":false,"isMicrophoneMuted":false},
      "exportSettings":{"frameRate":"60 FPS","resolution":"1080p"}
    }
    """;
    var manifest = Path.Combine(source, "project.json");
    File.WriteAllText(manifest, sourceJson);
    var manifestHashBefore = SHA256.HashData(File.ReadAllBytes(manifest));

    // The ratio is supplied by the caller after probing the real copied media; it is not a DogSC schema field.
    var result = DogScProjectImporter.Import(source, destination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 }, cameraAspectRatio: 16d / 9);
    var loaded = FrameTrailProjectStore.Load(result.ProjectFilePath);
    Check(File.Exists(result.ProjectFilePath), "目标工程已保存为独立 FrameTrail 项目");
    Check(loaded.DisplayName == "迁移验证工程", "DogSC 项目标题映射到目标项目名称");
    Check(loaded.Settings.LayoutModel == FrameTrailSettings.MacLayoutModel, "DogSC 导入工程使用 Mac 画面语义（LayoutModel 2）");
    Check(loaded.MediaPath == "media/screen.mp4" && File.ReadAllBytes(Path.Combine(destination, loaded.MediaPath)).SequenceEqual(screenBytes), "主视频素材在目标工程内逐字节复制");
    Check(loaded.Timeline.Segments.Count == 2 && Math.Abs(loaded.Timeline.Segments[0].SourceStart - .5) < 1e-9 && loaded.Timeline.Segments[0].PlaybackRate == 2, "已编辑片段顺序、裁剪区间和播放速率映射");
    Check(loaded.Timeline.Segments[0].AudioOverrides is { SystemVolume: .3, MicrophoneMuted: true }
        && loaded.Timeline.Segments[1].AudioOverrides is { MicrophoneVolume: .45, SystemMuted: true },
        "每个 DogSC 主片段的系统/麦克风音量与静音覆写导入并绑定片段 ID");
    Check(loaded.ZoomClips.Count == 1 && loaded.ZoomClips[0].Automatic && loaded.ZoomClips[0].Focus == new NormalizedPoint(.72, .61)
        && loaded.ZoomClips[0].Easing == ZoomEasing.Custom && loaded.ZoomClips[0].CustomCurve == new ZoomBezierCurve(.2, .05, .8, .95), "缩放片段及自定义 Bezier 参数映射");
    Check(loaded.ZoomClips[0].EnterProgressOffset == .22 && loaded.ZoomClips[0].ExitProgressOffset == .37,
        "DogSC 缩放入口与回落进度锚点映射");
    Check(loaded.ZoomClips[0].FocusEffect is { Shape: FocusEffectShape.Linear, Target: FocusEffectTarget.Pointer, Size: .4, Blur: .7, Softness: .3, Dimming: .12, AngleDegrees: 35 }, "缩放片段的线性鼠标聚焦效果字段映射");
    Check(loaded.PointerEvents.Count == 3 && loaded.PointerEvents[1].Kind == PointerEventKind.LeftClick && loaded.PointerEvents[1].CursorAsset == "system.arrow" && Math.Abs(loaded.MouseClock.OffsetSeconds + .2) < 1e-9 && Math.Abs(loaded.MouseClock.Rate - 1.2) < 1e-9, "指针 JSONL、点击类型/光标 ID 和独立时钟校正映射");
    Check(loaded.Markers.Count == 1 && Math.Abs(loaded.Markers[0].SourceTime - 1.25) < 1e-9, "录制标记以源时间导入");
    Check(loaded.Camera is not null && Math.Abs(loaded.Camera.TimelineStart - .1) < 1e-9 && loaded.Camera.Opacity == 0 && File.ReadAllBytes(Path.Combine(destination, loaded.Camera.MediaPath)).SequenceEqual(cameraBytes), "摄像头素材、基础布局和隐藏状态映射");
    Check(loaded.Camera!.SyncSegments is { Count: 3 } cameraSync && Math.Abs(cameraSync[0].SourceStart - .21) < 1e-9
        && Math.Abs(cameraSync[0].SourceRate - 1.3) < 1e-9 && Math.Abs(cameraSync[1].SourceRate - 1.1) < 1e-9
        && Math.Abs(cameraSync[2].SourceStart + cameraSync[2].Duration * cameraSync[2].SourceRate - 3.8) < 1e-9,
        "摄像头 sourceTimeScale、piecewise anchors 与 sourceEndTime 映射到源时钟分段");
    var editedCamera = loaded.Camera with { SyncSegments = loaded.Camera.SyncSegments!.Select(x => x with { }).ToList() };
    CameraTimelineOperations.Move(editedCamera, .5);
    CameraTimelineOperations.TrimTimelineStart(editedCamera, 1.6);
    Check(editedCamera.SyncSegments is { Count: 2 } editedCameraParts && Math.Abs(editedCameraParts[0].TimelineStart - 1.6) < 1e-9
        && Math.Abs(editedCameraParts[0].SourceStart - 1.49) < 1e-9 && Math.Abs(editedCameraParts[0].SourceRate - 1.1) < 1e-9,
        "摄像头移动与裁首按piecewise SourceRate保持源时钟映射");
    Check(loaded.Camera?.Shape == CameraShape.Circle && loaded.Camera.Crop.Width < 1, "摄像头圆形布局和内部取景缩放映射");
    Check(loaded.Camera?.Layout == CameraMotionLayoutKind.Circle && loaded.Camera.CornerRadius == .75 && Math.Abs(loaded.Camera.AssetAspectRatio!.Value - 16d / 9) < 1e-9,
        "摄像头原始roundness 0..1、基础形状和调用方探测的素材宽高比映射");
    Check(loaded.Camera?.ScaleDuringZoom == .62 && loaded.Camera.ShadowStrength == .42
        && !result.Report.Warnings.Any(x => x.Contains("摄像头随屏幕缩放联动", StringComparison.Ordinal) || x.Contains("DogSC 摄像头阴影", StringComparison.Ordinal)),
        "摄像头缩放联动和阴影强度导入，不再报告为丢失字段");
    Check(loaded.AudioTracks.Count == 1 && Math.Abs(loaded.AudioTracks[0].Volume - .73) < 1e-9 && File.ReadAllBytes(Path.Combine(destination, loaded.AudioTracks[0].MediaPath)).SequenceEqual(microphoneBytes), "独立麦克风轨道和素材映射");
    Check(loaded.AudioTracks[0].Source == AudioTrackSource.Microphone && loaded.AudioTracks[0].SyncSegments is { Count: 3 } micSync
        && Math.Abs(micSync[0].SourceStart - .0975) < 1e-9 && Math.Abs(micSync[0].SourceRate - .75) < 1e-9 && Math.Abs(micSync[1].SourceRate - .85) < 1e-9
        && Math.Abs(loaded.AudioTracks[0].SourceRate - .8) < 1e-9,
        "麦克风 sourceTimeScale 与 piecewise syncAnchors 保留在可导出的分段映射中");
    var editedAudio = loaded.AudioTracks[0] with { SyncSegments = loaded.AudioTracks[0].SyncSegments!.Select(x => x with { }).ToList() };
    AudioTimelineOperations.Move(editedAudio, .25);
    AudioTimelineOperations.TrimTimelineStart(editedAudio, .5);
    Check(editedAudio.SyncSegments is { Count: 3 } editedAudioParts && Math.Abs(editedAudioParts[0].TimelineStart - .5) < 1e-9
        && Math.Abs(editedAudioParts[0].SourceStart - .2475) < 1e-9 && Math.Abs(editedAudioParts[0].SourceRate - .75) < 1e-9,
        "麦克风分段移动与裁首按SourceRate保持源时钟映射");
    var syncedScene = new PreparedFrameSceneEvaluator(loaded).Evaluate(.3);
    Check(Math.Abs(syncedScene.SourceTime - 1.1) < 1e-9 && Math.Abs(syncedScene.Camera!.SourceTime - 1.49) < 1e-9,
        "摄像头同步分段在重排时间线的输出时间上按camera源速率求值");
    Check(Math.Abs(loaded.Settings.MainAudioVolume - .82) < 1e-9 && !loaded.Settings.MainAudioMuted, "内嵌系统音轨音量和静音状态映射");
    Check(loaded.Settings.Canvas?.Frame == ScreenFrameKind.WindowDark && loaded.Settings.Canvas.Crop == new NormalizedRect(.1, .05, .8, .9), "常用画布布局和窗口外框映射");
    Check(loaded.Settings.Canvas!.FrameToolbarScale == 1.3 && loaded.Settings.Canvas.FrameOuterRadius == 32 && Math.Abs(loaded.Settings.CornerRadius - 8d / 1080) < 1e-9, "样机顶栏、外圆角与内容圆角独立导入");
    Check(loaded.Settings.BackgroundStyle?.Kind == BackgroundKind.Video && loaded.Settings.BackgroundStyle.VideoPath == "assets/background.mp4" && File.ReadAllBytes(Path.Combine(destination, loaded.Settings.BackgroundStyle.VideoPath)).SequenceEqual(backgroundVideoBytes), "DogSC 项目循环视频背景安全复制并持久化");
    Check(loaded.Settings.Cursor?.Style == CursorStyle.SystemArrow && loaded.Settings.Cursor.Scale == 1.2 && Math.Abs(loaded.Settings.Cursor.ClickDuration - .36) < 1e-9
        && loaded.Settings.Cursor.ClickEffect == ClickEffectStyle.Ripple && loaded.Settings.Cursor.ClickScale == 1.35
        && loaded.Settings.Cursor.ClickOpacity == .65 && !loaded.Settings.Cursor.MotionTiltEnabled && loaded.Settings.Cursor.MotionTiltStrength == 3.2,
        "系统光标尺寸、倾斜开关和点击动画参数映射");
    Check(loaded.Settings.Spring.Mass == 1.9 && loaded.Settings.Spring.Stiffness == 320 && loaded.Settings.Spring.Damping == 36,
        "项目级 screen spring 参数映射");

    var originalCameraSource = Path.Combine(temporaryRoot, "original-camera.dogscproject");
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        Write(originalCameraSource, Path.GetRelativePath(source, file), File.ReadAllBytes(file));
    var originalCameraJson = sourceJson.Replace("\"shape\":\"circle\",\"position\":{\"x\":0.83", "\"shape\":\"original\",\"position\":{\"x\":0.83", StringComparison.Ordinal);
    File.WriteAllText(Path.Combine(originalCameraSource, "project.json"), originalCameraJson);
    var missingRatioImport = DogScProjectImporter.Import(originalCameraSource, Path.Combine(temporaryRoot, "original-camera.frametrail"),
        new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
    var missingRatioProject = FrameTrailProjectStore.Load(missingRatioImport.ProjectFilePath);
    Check(missingRatioProject.Camera?.Layout == CameraMotionLayoutKind.Original && missingRatioProject.Camera.AssetAspectRatio is null
        && missingRatioImport.Report.Warnings.Any(w => w.Contains("Original", StringComparison.Ordinal)),
        "DogSC 未保存摄像头尺寸时不伪造 Original 比例，并在导入报告提示调用方需探测素材");
    Check(loaded.ScreenMotions.Count == 1 && loaded.ScreenMotions[0].Focus?.Target == FocusEffectTarget.Pointer && loaded.ScreenMotions[0].Target.RotationY == -4
        && loaded.ScreenMotions[0].Easing == ZoomEasing.Custom && loaded.ScreenMotions[0].CustomCurve == new ZoomBezierCurve(.25, .1, .75, .9), "屏幕 3D 姿态、自定义曲线与指针聚焦效果映射");
    Check(loaded.CameraMotions.Count == 1 && loaded.CameraMotions[0].TargetSize == .32 && loaded.CameraMotions[0].TargetPosition.X == .72
        && loaded.CameraMotions[0].TargetLayout == CameraMotionLayoutKind.Circle && loaded.CameraMotions[0].TargetRoundness == .5 && loaded.CameraMotions[0].TargetOpacity == .9
        && loaded.CameraMotions[0].Easing == ZoomEasing.Custom && loaded.CameraMotions[0].CustomCurve == new ZoomBezierCurve(.3, .2, .7, .8), "摄像头形状、圆角、透明度运动和自定义曲线映射");
    Check(loaded.BlurClips.Count == 1 && loaded.BlurClips[0].Rect == new NormalizedRect(.2, .25, .3, .2) && loaded.BlurClips[0].EnterDuration == .25, "DogSC 区域柔化与过渡映射");
    Check(loaded.Overlays.Count == 1 && File.ReadAllBytes(Path.Combine(destination, loaded.Overlays[0].MediaPath)).SequenceEqual(stickerBytes) && loaded.Overlays[0].EnterMotion == OverlayMotion.Scale, "贴图素材安全复制且主体/动画映射");
    Check(Math.Abs(loaded.Overlays[0].Rect.Width / loaded.Overlays[0].Rect.Height * 16 / 9 - 2) < .02, "贴图按原始图像宽高比映射到画布");
    Check(loaded.Settings.Opening?.Enabled == true && loaded.Settings.Opening.Preset == OpeningPreset.Light3D && loaded.Settings.Opening.Duration == 2.4 && loaded.Settings.Opening.ElementCurve == ElementMotionCurve.Swift && loaded.Settings.Opening.IncludedElements.SequenceEqual([OpeningElement.Screen, OpeningElement.Overlays]) && loaded.Settings.Opening.ElementOrder.SequenceEqual([OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays]) && loaded.Settings.Opening.Stagger == .2, "开场元素启用、顺序、延迟、Light3D预设、Swift精确曲线和时长映射");
    Check(loaded.Overlays[0].AnimationCurve == ElementMotionCurve.Swift && Math.Abs(loaded.Overlays[0].CornerRadius - .005) < 1e-12
        && loaded.Overlays[0].BorderWidth == 2
        && loaded.Overlays[0].ShadowOpacity == .3 && loaded.Overlays[0].ShadowRadius == 20 && loaded.Overlays[0].ShadowOffsetY == 5,
        "贴图曲线、边框和阴影字段完整导入");
    Check(loaded.Overlays[0].BackdropBlur == 12 && !loaded.Overlays[0].BackdropBlurIncludesCamera
        && !result.Report.Warnings.Any(x => x.Contains("背景/摄像头柔化", StringComparison.Ordinal) || x.Contains("开场", StringComparison.Ordinal)
            || x.Contains("边框宽度/颜色没有", StringComparison.Ordinal) || x.Contains("动画曲线 swift", StringComparison.Ordinal)), "贴图背景柔化完整导入，已迁移字段不再误报损失");
    Check(File.Exists(Path.Combine(destination, "dogsc-import-report.json")), "目标工程附带字段损失/未支持项报告");
    Check(result.Report.Warnings.Any(w => w.Contains("主屏幕素材 sourceTimeScale 尚未映射", StringComparison.Ordinal)),
        "主屏幕媒体时钟倍率未实现时在DogSC导入报告中明确提示");
    Check(SHA256.HashData(File.ReadAllBytes(manifest)).SequenceEqual(manifestHashBefore), "原 DogSC project.json 未发生改写");

    var spotlightSource = Path.Combine(temporaryRoot, "spotlight.dogscproject");
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        Write(spotlightSource, Path.GetRelativePath(source, file), File.ReadAllBytes(file));
    File.WriteAllText(Path.Combine(spotlightSource, "project.json"), sourceJson.Replace("\"style\":\"blur\"", "\"style\":\"spotlight\"", StringComparison.Ordinal));
    var spotlightResult = DogScProjectImporter.Import(spotlightSource, Path.Combine(temporaryRoot,"spotlight-import.frametrail"), new MediaAsset { Width=1280, Height=720, Duration=4 }, cameraAspectRatio: 16d / 9);
    var importedSpotlight = FrameTrailProjectStore.Load(spotlightResult.ProjectFilePath).BlurClips.Single();
    Check(importedSpotlight.Kind == BlurKind.Spotlight && importedSpotlight.SourceSpace && importedSpotlight.SpotlightDimming == .22
        && importedSpotlight.TransitionStyle == RegionTransitionStyle.Smooth && importedSpotlight.Rect == new NormalizedRect(.2,.25,.3,.2)
        && !spotlightResult.Report.Warnings.Any(x => x.Contains("Spotlight",StringComparison.OrdinalIgnoreCase)),
        "Spotlight源区域、外区压暗与顺滑转场导入并持久化，不再退化成模糊");

    foreach (var (asset, expected) in new[] { ("system.automatic", CursorStyle.FollowRecorded), ("system.hand", CursorStyle.SystemHand), ("system.ibeam", CursorStyle.SystemText) })
    {
        var variant = Path.Combine(temporaryRoot, asset + ".dogscproject");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            Write(variant, Path.GetRelativePath(source, file), File.ReadAllBytes(file));
        var variantJson = sourceJson.Replace("\"assetID\":\"system.arrow\"", $"\"assetID\":\"{asset}\"", StringComparison.Ordinal)
            .Replace("\"playbackRate\":1.0", "\"playbackRate\":0.25", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(variant, "project.json"), variantJson);
        var imported = DogScProjectImporter.Import(variant, Path.Combine(temporaryRoot, asset + ".frametrail"), new MediaAsset { Width = 1280, Height = 720, Duration = 4 }, cameraAspectRatio: 16d / 9);
        var saved = FrameTrailProjectStore.Load(imported.ProjectFilePath);
        Check(saved.Settings.Cursor?.Style == expected && saved.Timeline.Segments[1].PlaybackRate == .25
            && !imported.Report.Warnings.Any(w => w.Contains("光标资源", StringComparison.Ordinal)), $"{asset} 样式与 0.25x 慢放导入保存重开不降级");
    }

    var unsafeSource = Path.Combine(temporaryRoot, "unsafe.dogscproject");
    Directory.CreateDirectory(unsafeSource);
    File.WriteAllText(Path.Combine(unsafeSource, "project.json"), """
        {"version":27,"media":{"screen":{"relativePath":"../outside.mp4"}}}
        """);
    var unsafeDestination = Path.Combine(temporaryRoot, "Unsafe.frametrail");
    try
    {
        DogScProjectImporter.Import(unsafeSource, unsafeDestination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：越界素材路径应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝素材路径越界"); }
    Check(!Directory.Exists(unsafeDestination), "路径校验失败时不留下半成品目标工程");

    var unsafeStickerSource = Path.Combine(temporaryRoot, "unsafe-sticker.dogscproject");
    Directory.CreateDirectory(unsafeStickerSource);
    Write(unsafeStickerSource, "screen.mp4", screenBytes);
    File.WriteAllText(Path.Combine(unsafeStickerSource, "project.json"), """
        {"version":27,"media":{"screen":{"relativePath":"screen.mp4"}},"timeline":{"stickerClips":[{"timing":{"startTime":0,"duration":1},"relativePath":"../escape.png"}]}}
        """);
    var unsafeStickerDestination = Path.Combine(temporaryRoot, "UnsafeSticker.frametrail");
    try
    {
        DogScProjectImporter.Import(unsafeStickerSource, unsafeStickerDestination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：越界贴图路径应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝贴图素材路径越界"); }
    Check(!Directory.Exists(unsafeStickerDestination), "贴图路径校验失败时不留下半成品目标工程");

    var futureSource = Path.Combine(temporaryRoot, "future.dogscproject");
    Directory.CreateDirectory(futureSource);
    File.WriteAllText(Path.Combine(futureSource, "project.json"), """
        {"version":28,"media":{"screen":{"relativePath":"media/screen.mp4"}}}
        """);
    try
    {
        DogScProjectImporter.Import(futureSource, Path.Combine(temporaryRoot, "Future.frametrail"), new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：未来 schema 应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝未知未来 DogSC schema"); }

    Console.WriteLine("DOGSC_IMPORT_FIXTURE=PASS；此构造 schema fixture 不是原 Mac 项目互通认证。");
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}
