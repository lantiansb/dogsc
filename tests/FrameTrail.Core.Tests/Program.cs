using System.Text.Json;
using FrameTrail;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static FrameTrailProject Copy(FrameTrailProject p) => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(p))!;

OpeningMotionProbe.Run();
OpeningMotionProbe.RunFrameSceneIntegration();
PointerDynamicsProbe.Run();
FrameScenePointerDynamicsProbe.Run();
SlowMotionProbe.Run();
AutoFollowProbe.Run();
ZoomFocusEffectProbe.Run();
OverlayStyleProbe.Run();
OverlayBackdropProbe.Run();
CameraMotionStateProbe.Run();
CameraMotionEvaluationProbe.Run();
ZoomProgressAnchorProbe.Run();
ZoomHandoffReturnProbe.Run();
SpotlightProbe.Run();
FrameTrail.Core.Tests.LegacySyncSegmentProbe.Run();
FrameTrail.Core.Tests.EffectTimelineSplitProbe.Run();
RecordingAudioClockSegmentProbe.Run();
RecordingMarkerTimelineProbe.Run();
HistoryPersistenceProbe.Run();
CanvasSnapMathProbe.Run();
FrameTrail.Core.Tests.ProjectAssetRelocationTransactionProbe.Run();
await FrameTrail.Core.Tests.AssetCandidateResolverProbe.RunAsync();

Check(JsonSerializer.Deserialize<MediaAsset>("{\"width\":1920,\"height\":1080,\"duration\":4}")!.FrameRate == 30,
    "legacy projects without source frame rate default to 30fps");

var timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 10, SourceDuration = 8, PlaybackRate = 2 }, new TimelineSegment { SourceStart = 30, SourceDuration = 4 }] };
var first = timeline.Segments[0]; var split = timeline.Split(first.Id, 14);
Check(Math.Abs(timeline.MapOutputToSource(1) - 12) < 1e-9, "split mapping before junction");
Check(Math.Abs(timeline.MapOutputToSource(2) - 14) < 1e-9, "split mapping at junction");
Check(Math.Abs(timeline.MapOutputToSource(3) - 16) < 1e-9, "split mapping after junction");
timeline.SetPlaybackRate(split.Right.Id, 4);
Check(Math.Abs(timeline.MapOutputToSource(2.5) - 16) < 1e-9, "retimed segment mapping");
Check(Math.Abs(timeline.MapSourceToOutput(16)!.Value - 2.5) < 1e-9, "inverse source mapping");
timeline.Reorder(timeline.Segments[^1].Id, 0);
Check(Math.Abs(timeline.MapOutputToSource(0) - 30) < 1e-9, "reorder mapping");
var discontinuous = new ProjectTimeline { Segments = [new() { SourceStart = 0, SourceDuration = 2 }, new() { SourceStart = 10, SourceDuration = 3 }] };
Check(Math.Abs(discontinuous.MapOutputToSource(2) - 10) < 1e-9, "cut boundary maps to following source segment");
var repeatedSource = new ProjectTimeline { Segments = [new() { SourceStart = 5, SourceDuration = 2 }, new() { SourceStart = 5, SourceDuration = 2 }] };
Check(Math.Abs(repeatedSource.MapOutputToSource(2) - 5) < 1e-9 && repeatedSource.MapSourceToOutput(6) == 1, "repeated source range remains deterministic");
Check(repeatedSource.MapSourceToOutput(5) == 0, "source-to-output selects first duplicate occurrence");

var mappedTracks = RecordingTimelineMapper.MapAudio(
    screenOriginTicks: 200,
    screenCapture: new(100, 12_000),
    screenPauses: [new(2_200, 3_200), new(4_800, 5_400), new(5_000, 6_000), new(8_000, 8_500), new(20_000, 21_000), new(900, 900)],
    audioCapture: new(500, 13_000),
    audioPauses: [new(1_500, 2_000), new(3_900, 4_500)],
    ticksFrequency: 1_000);
var expectedTrackMap = new[] { new MappedAudioInterval(0, 1, .3), new(1, .2, 1.8), new(2.2, .7, 2), new(2.9, .3, 3.3), new(4.4, 2, 3.6), new(6.9, 3.5, 5.6) };
Check(mappedTracks.Count == expectedTrackMap.Length && mappedTracks.Zip(expectedTrackMap).All(pair =>
        Math.Abs(pair.First.SourceStart - pair.Second.SourceStart) < 1e-9
        && Math.Abs(pair.First.Duration - pair.Second.Duration) < 1e-9
        && Math.Abs(pair.First.TimelineStart - pair.Second.TimelineStart) < 1e-9),
    "recording mapper handles first-frame origin, start delays, three screen pauses, merged pauses and both capture tails");
Check(RecordingTimelineMapper.MapAudio(10, new(0, 100), [], new(101, 200), [], 1_000).Count == 0,
    "recording mapper returns no intervals when independent capture ranges do not intersect");
try { _ = RecordingTimelineMapper.MapAudio(0, new(0, 1), [], new(0, 1), [], 0); throw new Exception("zero clock frequency was accepted"); }
catch (ArgumentOutOfRangeException) { }

var clicks = new[] {
    new PointerEvent(1, new(.2, .3), PointerEventKind.LeftClick),
    new PointerEvent(1.7, new(.8, .7), PointerEventKind.RightClick),
    new PointerEvent(5, new(.5, .5), PointerEventKind.LeftClick)
};
var clips = AutoZoomPlanner.Plan(clicks);
Check(clips.Count == 2 && clips[0].End < clips[1].Start, "click grouping");
var animationProject = new FrameTrailProject { MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10 }] }, ZoomClips = [clips[0]], Settings = new() { AutoZoom = false } };
var animationLeft = FrameSceneEvaluator.Evaluate(animationProject, clips[0].Start + .2).Zoom;
var animationRight = FrameSceneEvaluator.Evaluate(animationProject, clips[0].Start + .201).Zoom;
Check(Math.Abs(animationRight - animationLeft) < .01, "zoom animation continuity");
var returnEnd = clips[0].End + clips[0].ExitDuration;
Check(Math.Abs(FrameSceneEvaluator.Evaluate(animationProject, returnEnd).Zoom - FrameSceneEvaluator.Evaluate(animationProject, returnEnd - .001).Zoom) < .01, "zoom return continuity");
var activityPlan = AutoZoomPlanner.Plan([new(1, new(.5, .5), PointerEventKind.LeftClick), new(1.8, new(.55, .5)), new(2.2, new(.6, .5))]);
Check(activityPlan[0].End > 1.9, "automatic zoom hold extends through pointer activity");
var handoffProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 5 }] }, Settings = new() { AutoZoom = false },
    ZoomClips = [
        new() { Start = 1, End = 2, Scale = 1.6, Focus = new(.25, .4), EnterDuration = .4, ExitDuration = 0 },
        new() { Start = 2, End = 3, Scale = 2.2, Focus = new(.75, .6), EnterDuration = .5, ExitDuration = .4 }
    ]
};
var handoffEvaluator = new PreparedFrameSceneEvaluator(handoffProject);
var beforeHandoff = handoffEvaluator.Evaluate(2 - 1e-6);
var atHandoff = handoffEvaluator.Evaluate(2);
var afterHandoff = handoffEvaluator.Evaluate(2 + 1e-3);
Check(Math.Abs(beforeHandoff.Zoom - atHandoff.Zoom) < 1e-5 && Math.Abs(atHandoff.Zoom - 1.6) < 1e-9
    && atHandoff.CropOrigin == beforeHandoff.CropOrigin && Math.Abs(afterHandoff.Zoom - atHandoff.Zoom) < .01,
    "adjacent zoom clips inherit the previous viewport instead of jumping to base framing at the handoff");
var shortZoomProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] }, Settings = new() { AutoZoom = false },
    ZoomClips = [new() { Start = .2, End = .3, Scale = 2, Focus = new(.7, .3), EnterDuration = 1, ExitDuration = 0 }]
};
var shortZoomEnd = new PreparedFrameSceneEvaluator(shortZoomProject).Evaluate(.3);
Check(Math.Abs(shortZoomEnd.Zoom - 2) < 1e-9, "zoom entry duration is bounded by clip duration so a short clip reaches its authored target");
var croppedAutoZoom = new FrameTrailProject
{
    MediaPath = "capture.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 2 },
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    PointerEvents = [new(0, new(.4, .5)), new(.5, new(.4, .5), PointerEventKind.LeftClick)],
    ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.4, .5), EnterDuration = 0, ExitDuration = 0, Automatic = true }],
    Settings = new() { AutoZoom = false, Canvas = new() { Crop = new(.25, 0, .5, 1) } }
};
var croppedAutoEvaluator = new PreparedFrameSceneEvaluator(croppedAutoZoom);
var croppedAutoScene = croppedAutoEvaluator.Evaluate(.5);
var croppedSourceCenter = new NormalizedPoint(.25 + (croppedAutoScene.CropOrigin.X + croppedAutoScene.CropSize.X / 2) * .5,
    croppedAutoScene.CropOrigin.Y + croppedAutoScene.CropSize.Y / 2);
var croppedTargetScene = croppedAutoEvaluator.EvaluateTargetForZoom(croppedAutoZoom.ZoomClips[0].Id, .5);
Check(Math.Abs(croppedSourceCenter.X - .4) < 1e-9 && Math.Abs(croppedSourceCenter.Y - .5) < 1e-9
    && croppedAutoScene.Cursor == new NormalizedPoint(.4, .5)
    && Math.Abs(croppedTargetScene.CropOrigin.X - .05) < 1e-9,
    $"automatic zoom crop mapping (center={croppedSourceCenter}, cursor={croppedAutoScene.Cursor}, target={croppedTargetScene.CropOrigin})");

double SampleEase(ZoomEasing easing, double progress, ZoomBezierCurve? curve = null, SpringAnimationSettings? spring = null)
{
    var easeProject = new FrameTrailProject
    {
        MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
        Settings = new() { AutoZoom = false, Spring = spring ?? new() },
        ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.5, .5), EnterDuration = 1, ExitDuration = 0, Easing = easing, CustomCurve = curve ?? ZoomBezierCurve.Cubic }]
    };
    return new PreparedFrameSceneEvaluator(easeProject).Evaluate(progress).Zoom - 1;
}
Check(Math.Abs(SampleEase(ZoomEasing.Cubic, .25) - .07079658) < 1e-6
    && Math.Abs(SampleEase(ZoomEasing.Quintic, .25) - .04530125) < 1e-6
    && Math.Abs(SampleEase(ZoomEasing.Quadratic, .25) - .12015) < 1e-4
    && Math.Abs(SampleEase(ZoomEasing.Spring, .5) - .88059446) < 1e-6,
    "zoom easing matches Swift quadratic/cubic/quintic Bezier controls and normalized default spring samples");
var customCurve = new ZoomBezierCurve(.2, .05, .8, .95);
var customSpring = new SpringAnimationSettings { Mass = 1.8, Stiffness = 320, Damping = 28 };
Check(Math.Abs(SampleEase(ZoomEasing.Custom, .25, customCurve) - .210574325622496) < 1e-9
    && SampleEase(ZoomEasing.Spring, .5, spring: customSpring) > .999999,
    "custom Bezier and configured spring match separately calculated normalized response samples");
var customEase = .210574325622496;
var customSceneBase = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    Settings = new() { AutoZoom = false, Opening = new() { Enabled = true, Duration = 1, Easing = ZoomEasing.Custom, CustomCurve = customCurve } },
    ScreenMotions = [new() { Start = 0, Duration = 1, EnterDuration = 1, ReturnDuration = 0, Easing = ZoomEasing.Custom, CustomCurve = customCurve, Target = new() { Position = new(.9, .5) } }],
    Camera = new() { MediaPath = "camera.mp4", Duration = 2, TimelineStart = 0 },
    CameraMotions = [new() { Start = 0, Duration = 1, EnterDuration = 1, ReturnDuration = 0, Easing = ZoomEasing.Custom, CustomCurve = customCurve, TargetPosition = new(.9, .8) }]
};
var customScene = new PreparedFrameSceneEvaluator(customSceneBase).Evaluate(.25);
var customCameraCenter = customScene.Camera!.Rect.X + customScene.Camera.Rect.Width / 2;
Check(Math.Abs(customScene.ScreenMotion!.Position.X - (.5 + .4 * customEase)) < 1e-9
    && Math.Abs(customCameraCenter - (.82 + .08 * customEase)) < 1e-9
    && Math.Abs(customScene.Opening!.Scale - (.85 + .15 * customEase)) < 1e-9,
    "custom Bezier curve is applied to screen, camera and opening transitions");

var track = new[] { new PointerEvent(0, new(.1, .2)), new PointerEvent(.1, new(.2, .3)), new PointerEvent(.2, new(.4, .5)) };
var pointer = PointerTrack.Sample(track, .15, PointerSmoothing.Medium).Position;
Check(pointer is not null && pointer.Value.X > .2 && pointer.Value.X < .4, "monotone pointer interpolation");
Check(JsonSerializer.Deserialize<FrameTrailSettings>("{}")!.PointerSmoothing == PointerSmoothing.Smooth,
    "projects without a pointer smoothing field retain the original smooth default");
Check(JsonSerializer.Deserialize<FrameTrailSettings>("{}")!.Spring is { Mass: 2.4, Stiffness: 210, Damping: 42 }
    && JsonSerializer.Deserialize<ZoomClip>("{}")!.CustomCurve == ZoomBezierCurve.Cubic,
    "legacy projects receive the original default animation spring and cubic custom curve");
var serializedCurveProject = Copy(customSceneBase);
Check(serializedCurveProject.ZoomClips.Count == 0 && serializedCurveProject.ScreenMotions[0].CustomCurve == customCurve
    && serializedCurveProject.CameraMotions[0].CustomCurve == customCurve && serializedCurveProject.Settings.Opening!.CustomCurve == customCurve
    && serializedCurveProject.Settings.Spring == customSceneBase.Settings.Spring,
    "curve and spring fields survive project JSON cloning used by history snapshots");
Check(new CanvasSettings().FrameToolbarScale == 1 && new CanvasSettings().FrameOuterRadius is null,
    "legacy mock frames retain the default toolbar scale and inferred outer radius");
const double springStep = 1d / 240;
var spring = new PreparedPointerTrack([new(0, new(.1, .2)), new(.2, new(.12, .215))], PointerSmoothing.Smooth).Sample(springStep).Position!.Value;
var expectedSpringX = .1 + (450 * ((.12 - .1) * springStep / .2) / 2.8 * springStep) * springStep;
var expectedSpringY = .2 + (450 * ((.215 - .2) * springStep / .2) / 2.8 * springStep) * springStep;
Check(Math.Abs(spring.X - expectedSpringX) < 1e-12 && Math.Abs(spring.Y - expectedSpringY) < 1e-12,
    "smooth pointer follows the upstream 240Hz second-order spring integration at its first step");
var profileEvents = new[] { new PointerEvent(0, new(.1, .2)), new PointerEvent(.1, new(.4, .35)), new PointerEvent(.2, new(.7, .65)), new PointerEvent(.3, new(.9, .8)) };
var smoothSample = new PreparedPointerTrack(profileEvents, PointerSmoothing.Smooth).Sample(.15).Position!.Value;
var mediumSample = new PreparedPointerTrack(profileEvents, PointerSmoothing.Medium).Sample(.15).Position!.Value;
var rapidSample = new PreparedPointerTrack(profileEvents, PointerSmoothing.Rapid).Sample(.15).Position!.Value;
Check(Math.Abs(smoothSample.X - mediumSample.X) > .005 && Math.Abs(mediumSample.X - rapidSample.X) > .0001,
    $"Smooth/Medium/Rapid use distinct spring or Hermite responses ({smoothSample.X:F6}, {mediumSample.X:F6}, {rapidSample.X:F6})");
var clickTrack = new PreparedPointerTrack([new(0, new(.1, .1)), new(.1, new(.8, .7), PointerEventKind.LeftClick), new(.2, new(.8, .7))]);
Check(clickTrack.Sample(.1).Position == new NormalizedPoint(.8, .7)
    && clickTrack.Sample(.1).ClickStrength == 0
    && clickTrack.Sample(.19).ClickStrength > 0,
    "spring smoothing anchors exact click location while retaining click pulse timing");
var longGap = new[] { new PointerEvent(0, new(.2, .2)), new PointerEvent(.1, new(.3, .3)), new PointerEvent(1, new(.8, .8)) };
var gapSample = new PreparedPointerTrack(longGap, PointerSmoothing.Smooth).Sample(.75).Position!.Value;
Check(gapSample.X < .4 && new PreparedPointerTrack(longGap, PointerSmoothing.Rapid).Sample(.75).Position == new NormalizedPoint(.3, .3),
    "long silence holds the last event instead of interpolating toward a future movement");
var sparseLongTrack = new PreparedPointerTrack([new(0, new(.2, .2)), new(86_400, new(.21, .21))], PointerSmoothing.Smooth);
Check(sparseLongTrack.Sample(43_200).Position is { } sparseLongPosition && sparseLongPosition.X is > .19 and < .22,
    "a 24-hour sparse pointer track skips settled idle spans instead of allocating a full-rate spring cache");
var calibratedSmoothA = new PreparedFrameSceneEvaluator(new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] },
    PointerEvents = track.ToList(), Settings = new() { AutoZoom = false, PointerSmoothing = PointerSmoothing.Medium }
}).Evaluate(.15).Cursor;
var calibratedSmoothBProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] },
    PointerEvents = track.ToList(), MouseClock = new(.05, 1), Settings = new() { AutoZoom = false, PointerSmoothing = PointerSmoothing.Medium }
};
var calibratedSmoothB = new PreparedFrameSceneEvaluator(calibratedSmoothBProject).Evaluate(.2).Cursor;
Check(calibratedSmoothA is { } calibratedA && calibratedSmoothB is { } calibratedB
    && Math.Abs(calibratedA.X - calibratedB.X) < 1e-12 && Math.Abs(calibratedA.Y - calibratedB.Y) < 1e-12
    && calibratedSmoothBProject.Settings.PointerSmoothing == PointerSmoothing.Medium,
    "mouse time calibration shifts sampling time without changing the selected smoothing profile");

var root = Path.Combine(Path.GetTempPath(), "frametrail-core-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var file = Path.Combine(root, "sample.frametrail");
    var project = new FrameTrailProject { MediaPath = "media/recording.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 40 }, Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 40 }] }, PointerEvents = clicks.ToList(), Settings = new() { PointerSmoothing = PointerSmoothing.Rapid }, Camera = new() { MediaPath = "media/camera.mp4", SourceStart = 0, TimelineStart = 0, Duration = 4, SyncSegments = [new(0, 0, 1), new(1, 3, 1)] } };
    FrameTrailProjectStore.Save(file, project);
    project.Settings.Background = "#ffffff";
    FrameTrailProjectStore.Save(file, project);
    Check(File.Exists(file + FrameTrailProjectStore.BackupSuffix), "backup creation");
    var restored = FrameTrailProjectStore.RestoreBackup(file);
    Check(restored.Settings.Background == "#202124" && restored.Settings.PointerSmoothing == PointerSmoothing.Rapid && restored.Camera?.SyncSegments is { Count: 2 } && restored.Camera.SyncSegments[1] == new CameraSyncSegment(1, 3, 1), "backup restore preserves pointer smoothing and camera sync segment mappings");
    if (OperatingSystem.IsWindows())
    {
        var transientSave = Copy(restored); transientSave.DisplayName = "短时文件占用后保存";
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var release = Task.Run(async () => { await Task.Delay(75); held.Dispose(); });
        FrameTrailProjectStore.Save(file, transientSave);
        await release;
        elapsed.Stop();
        Check(elapsed.ElapsedMilliseconds >= 50 && elapsed.Elapsed < TimeSpan.FromSeconds(4)
            && FrameTrailProjectStore.Load(file).DisplayName == "短时文件占用后保存",
            "atomic project replacement retries a short Windows sharing lock and loads the new project");

        var originalBytes = File.ReadAllBytes(file);
        var blockedSave = Copy(FrameTrailProjectStore.Load(file)); blockedSave.DisplayName = "持续占用时不得替换";
        using (var persistentHandle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blockedTimer = System.Diagnostics.Stopwatch.StartNew();
            try { FrameTrailProjectStore.Save(file, blockedSave); throw new Exception("persistent project file lock was ignored"); }
            catch (IOException ex)
            {
                blockedTimer.Stop();
                Check(ex.Message.Contains(file, StringComparison.OrdinalIgnoreCase)
                    && blockedTimer.Elapsed < TimeSpan.FromSeconds(4),
                    "persistent replacement failure is bounded and names the target project");
            }
        }
        Check(File.ReadAllBytes(file).SequenceEqual(originalBytes)
            && Directory.GetFiles(root, Path.GetFileName(file) + ".tmp-*").Length == 0
            && FrameTrailProjectStore.Load(file).DisplayName == "短时文件占用后保存",
            "a persistent replacement lock preserves the original project and cleans the temporary file");
    }
    restored.Media.FrameRate = 0;
    try { FrameTrailProjectStore.Save(file, restored); throw new Exception("invalid source frame rate was accepted"); } catch (InvalidDataException) { }
    restored.Media.FrameRate = 30;
    Check(FrameTrailProjectStore.ResolveMediaPath(file, restored) == Path.Combine(root, "media", "recording.mp4"), "relative media path");
    restored.MediaPath = "../secret.mp4";
    try { FrameTrailProjectStore.Save(file, restored); throw new Exception("path traversal was accepted"); } catch (InvalidDataException) { }
    var externalRoot = Path.Combine(Path.GetTempPath(), "frametrail-external-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(externalRoot);
    var linkPath = Path.Combine(root, "linked-media");
    try
    {
        Directory.CreateSymbolicLink(linkPath, externalRoot);
        try { _ = FrameTrailProjectStore.ResolveAssetPath(file, "linked-media/secret.mp4"); throw new Exception("symlink escape was accepted"); }
        catch (InvalidDataException) { Check(true, "project media resolver rejects symlink and junction escapes"); }
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symlink containment probe requires Windows Developer Mode or elevated privileges."); }
    finally { Directory.Delete(externalRoot, true); }
    var futureFile = Path.Combine(root, "future.frametrail");
    var futureProject = new FrameTrailProject { FormatVersion = 2, MediaPath = "media/source.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] } };
    File.WriteAllText(futureFile, JsonSerializer.Serialize(futureProject, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    try { _ = FrameTrailProjectStore.Load(futureFile); throw new Exception("future project version was accepted"); } catch (InvalidDataException) { }
}
finally { Directory.Delete(root, true); }

var history = new UndoRedoHistory<FrameTrailProject>(new FrameTrailProject(), Copy);
var changed = Copy(history.Current); changed.Settings.Background = "#abcdef"; history.Commit(changed);
Check(history.Undo().Settings.Background == "#202124" && history.Redo().Settings.Background == "#abcdef", "undo redo");

var sceneProject = new FrameTrailProject { MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 4, SourceDuration = 10 }] }, PointerEvents = [new(4, new(.5, .5), PointerEventKind.LeftClick)], Settings = new() { AutoZoom = true } };
var scene = FrameSceneEvaluator.Evaluate(sceneProject, 1);
Check(Math.Abs(scene.SourceTime - 5) < 1e-9 && scene.CropSize.X <= 1 && scene.Zoom >= 1, "frame scene source mapping and crop");
var prepared = new PreparedFrameSceneEvaluator(sceneProject);
foreach (var t in new[] { 0d, .1, 1, 4.9, 9.99, 10d })
{
    var direct = FrameSceneEvaluator.Evaluate(sceneProject, t); var cached = prepared.Evaluate(t);
    Check(direct.SourceTime == cached.SourceTime && direct.CropOrigin == cached.CropOrigin && direct.CropSize == cached.CropSize && direct.Cursor == cached.Cursor && direct.Zoom == cached.Zoom,
        $"prepared evaluation matches compatibility evaluator at {t}");
}
var negativeOffsetProject = new FrameTrailProject
{
    MediaPath = "capture.mp4",
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    MouseClock = new(-.5, 1),
    PointerEvents = [new(1.25, new(.7, .6))],
    Settings = new() { AutoZoom = false }
};
Check(negativeOffsetProject.MouseClock.ToSourceTime(.1) == 0, "negative calibration clamps pre-recording time");
var offsetScene = new PreparedFrameSceneEvaluator(negativeOffsetProject).Evaluate(.75);
Check(offsetScene.Cursor == new NormalizedPoint(.7, .6), "negative mouse offset maps cursor clock independently");
var calibrationEvents = new[] { new PointerEvent(0, new(.1, .2)), new PointerEvent(.1, new(.2, .5)), new PointerEvent(.2, new(.65, .8)) };
var rawPointer = new PreparedPointerTrack(calibrationEvents).Sample(.15);
var calibratedProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    MouseClock = new(.5, 1.25), PointerEvents = calibrationEvents.ToList(), Settings = new() { AutoZoom = false }
};
var calibratedPointer = new PreparedFrameSceneEvaluator(calibratedProject).Evaluate(.5 + .15 * 1.25);
Check(rawPointer.Position == calibratedPointer.Cursor && Math.Abs(rawPointer.ClickStrength - calibratedPointer.ClickStrength) < 1e-9,
    "independent clock calibration changes pointer sample time without changing the interpolation curve");
var reorderedEffectProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 10, SourceDuration = 2 }, new() { SourceStart = 30, SourceDuration = 2 }] },
    Settings = new() { AutoZoom = false }, ZoomClips = [new() { Start = 31, End = 32, Scale = 2, Focus = new(.8, .3), EnterDuration = 0, ExitDuration = 0 }]
};
var targetBeforeReorder = new PreparedFrameSceneEvaluator(reorderedEffectProject).Evaluate(3).Zoom;
reorderedEffectProject.Timeline.Reorder(reorderedEffectProject.Timeline.Segments[1].Id, 0);
var targetAfterReorder = new PreparedFrameSceneEvaluator(reorderedEffectProject).Evaluate(1).Zoom;
Check(targetBeforeReorder == 2 && targetAfterReorder == 2,
    "source-time zoom target follows its source range when the screen timeline is reordered");

var effectsProject = new FrameTrailProject
{
    MediaPath = "capture.mp4",
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10 }] },
    Settings = new() { AutoZoom = false, PointerSmoothing = PointerSmoothing.Rapid, Cursor = new() { Visible = true, HideWhenIdle = true, IdleSeconds = .4 }, Opening = new() { Enabled = true, Preset = OpeningPreset.RotateIn, Duration = .8 }, BackgroundStyle = new() { Kind = BackgroundKind.Pattern, Pattern = BackgroundPatternKind.Dots } },
    PointerEvents = [new(1, new(.3, .4)), new(1.2, new(.32, .42))],
    ScreenMotions = [new() { Start = 2, Duration = 2, EnterDuration = 1, ReturnDuration = 1, Target = new() { Position = new(.7, .6), Scale = 1.3, RotationY = 12 }, Focus = new() { Target = FocusEffectTarget.Pointer } }, new() { Start = 6, Duration = 1, EnterDuration = .5, Target = new() { Position = new(.2, .5) } }],
    Camera = new() { MediaPath = "media/camera.mp4", SourceStart = 3, Duration = 4, TimelineStart = 2, Position = new(.8, .8), Shape = CameraShape.Circle, Crop = new(.1, .2, .7, .6) },
    CameraMotions = [new() { Start = 2, Duration = 1, EnterDuration = .5, ReturnDuration = .5, TargetPosition = new(.5, .5), TargetSize = .4 }],
    Overlays = [new() { MediaPath = "media/sticker.png", Start = 2, Duration = 2, EnterDuration = .5, ExitDuration = .5, EnterMotion = OverlayMotion.Fade }],
    BlurClips = [new() { Start = 2, Duration = 1, Rect = new(.1, .2, .3, .4), Kind = BlurKind.Pixelate, Strength = .8 }]
};
var effectEvaluator = new PreparedFrameSceneEvaluator(effectsProject);
var beforeMotion = effectEvaluator.Evaluate(1.999);
var openingStart = effectEvaluator.Evaluate(0).Opening;
var openingMid = effectEvaluator.Evaluate(.4).Opening;
Check(openingStart?.Scale == .86 && openingStart.Opacity == .2 && openingMid!.Scale > .86 && openingMid.Scale < 1, "opening preset transition follows output time independently of source cuts");
var motionStart = effectEvaluator.Evaluate(2);
var motionMid = effectEvaluator.Evaluate(2.5);
Check(beforeMotion.ScreenMotion is null && motionStart.ScreenMotion is not null && motionStart.ScreenMotion.Position.X == .5, "screen motion starts continuously from centered pose");
Check(motionMid.ScreenMotion!.Position.X > .5 && motionMid.ScreenMotion.Position.X < .7 && motionMid.Focus?.Center == new NormalizedPoint(.32, .42), "motion easing and pointer-target focus resolve");
Check(effectEvaluator.Evaluate(5.999).ScreenMotion is null && effectEvaluator.Evaluate(6).ScreenMotion?.Position.X == .5, "separated screen motion clips return to the base pose continuously");
foreach (var secondStart in new[] { 1.4, 2d })
{
    var adjacentMotionProject = new FrameTrailProject
    {
        MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 5 }] }, Settings = new() { AutoZoom = false },
        ScreenMotions = [
            new() { Start = 1, Duration = 1, EnterDuration = 1, ReturnDuration = .4, Target = new() { Position = new(.72, .6), Scale = 1.2, RotationZ = 7 } },
            new() { Start = secondStart, Duration = 1, EnterDuration = .5, ReturnDuration = .3, Target = new() { Position = new(.28, .45), Scale = .9, RotationZ = -5 } }
        ]
    };
    var adjacentMotionEvaluator = new PreparedFrameSceneEvaluator(adjacentMotionProject);
    var motionBeforeHandoff = adjacentMotionEvaluator.Evaluate(secondStart - 1e-8).ScreenMotion!;
    var motionAtHandoff = adjacentMotionEvaluator.Evaluate(secondStart).ScreenMotion!;
    Check(Math.Abs(motionBeforeHandoff.Position.X - motionAtHandoff.Position.X) < 1e-6
        && Math.Abs(motionBeforeHandoff.Position.Y - motionAtHandoff.Position.Y) < 1e-6
        && Math.Abs(motionBeforeHandoff.Scale - motionAtHandoff.Scale) < 1e-6
        && Math.Abs(motionBeforeHandoff.RotationZ - motionAtHandoff.RotationZ) < 1e-6,
        secondStart < 2 ? "overlapping screen motion starts from the current interpolated pose" : "adjacent screen motion starts from the previous held pose");
}
Check(motionMid.Camera?.SourceTime == 3.5 && Math.Abs(motionMid.Camera.Rect.Width - .225) < 1e-9 && motionMid.Camera.Rect.Height == .4 && motionMid.Camera.Shape == CameraShape.Circle && motionMid.Camera.Crop == new NormalizedRect(.1, .2, .7, .6), "camera time, pixel-circular shape and source crop map into the frame scene");
Check(motionMid.Camera!.Rect.X < .72 && motionMid.Camera.Rect.Width > .15, "camera motion changes inset position and size");
foreach (var secondStart in new[] { .15, 1d })
{
    var cameraMotionProject = new FrameTrailProject
    {
        MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 4 }] },
        Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 4, TimelineStart = 0, Position = new(.8, .8), Size = .25 },
        CameraMotions = [
            new() { Start = 0, Duration = 1, EnterDuration = .4, ReturnDuration = .6, TargetPosition = new(.65, .68), TargetSize = .4 },
            new() { Start = secondStart, Duration = 1, EnterDuration = .5, ReturnDuration = .4, TargetPosition = new(.3, .35), TargetSize = .2 }
        ]
    };
    var cameraMotionEvaluator = new PreparedFrameSceneEvaluator(cameraMotionProject);
    var before = cameraMotionEvaluator.Evaluate(secondStart - 1e-6).Camera!;
    var at = cameraMotionEvaluator.Evaluate(secondStart).Camera!;
    Check(Math.Abs(before.Rect.X + before.Rect.Width / 2 - at.Rect.X - at.Rect.Width / 2) < 1e-5
        && Math.Abs(before.Rect.Y + before.Rect.Height / 2 - at.Rect.Y - at.Rect.Height / 2) < 1e-5
        && Math.Abs(before.Rect.Height - at.Rect.Height) < 1e-5,
        secondStart == 0.15 ? "overlapping camera motion continues from the current interpolated pose" : "adjacent camera motion continues from the held target");
}
Check(motionStart.Overlays?.Count == 1 && motionStart.Overlays[0].Opacity == 0 && effectEvaluator.Evaluate(6).Camera is null, "overlay fade and camera interval endpoints");
Check(motionStart.BlurRegions?.Count == 1 && motionStart.BlurRegions[0].Kind == BlurKind.Pixelate && motionStart.BlurRegions[0].Opacity == 0, "region blur animation starts at zero opacity");
Check(effectEvaluator.Evaluate(1.3).Cursor is not null && effectEvaluator.Evaluate(1.7).Cursor is null, "cursor idle hide follows calibrated pointer clock");
var zoomTargetProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 10, SourceDuration = 8 }] },
    Settings = new() { AutoZoom = false },
    ZoomClips = [new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Start = 2, End = 4, Scale = 2, Focus = new(.7, .6) }]
};
var zoomTargetEvaluator = new PreparedFrameSceneEvaluator(zoomTargetProject);
var normalZoomScene = zoomTargetEvaluator.Evaluate(1);
var forcedZoomScene = zoomTargetEvaluator.EvaluateTargetForZoom(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
Check(normalZoomScene.SourceTime == forcedZoomScene.SourceTime && normalZoomScene.OutputTime == forcedZoomScene.OutputTime
    && forcedZoomScene.Zoom == 2 && forcedZoomScene.CropSize == new NormalizedPoint(.5, .5)
    && Math.Abs(forcedZoomScene.CropOrigin.X - .45) < 1e-9 && Math.Abs(forcedZoomScene.CropOrigin.Y - .35) < 1e-9, "selected zoom target crop can be previewed without changing playhead or source time");
var openingSequenceProject = new FrameTrailProject
{
    MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 5 }] },
    Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 5, TimelineStart = 0 },
    Overlays = [new() { MediaPath = "sticker.png", Start = 0, Duration = 2, Rect = new(.4, .4, .2, .2), EnterMotion = OverlayMotion.None, ExitMotion = OverlayMotion.None }],
    Settings = new() { Opening = new() { Enabled = true, Preset = OpeningPreset.Converge, Duration = 2, Stagger = .3,
        IncludedElements = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays],
        ElementOrder = [OpeningElement.Overlays, OpeningElement.Camera, OpeningElement.Screen] } }
};
var openingSequenceEvaluator = new PreparedFrameSceneEvaluator(openingSequenceProject);
var openingSequenceFrame = openingSequenceEvaluator.Evaluate(0);
Check(openingSequenceFrame.OverlayOpening?.OffsetX == -1 && openingSequenceFrame.CameraOpening?.OffsetX == 1
    && openingSequenceFrame.Opening?.OffsetX == -1 && openingSequenceFrame.Camera?.Opacity == 0
    && openingSequenceFrame.Overlays?.Single().Opacity == 0, "opening sequence applies included element order and stagger to screen, camera and overlays");
foreach (var preset in Enum.GetValues<OpeningPreset>())
{
    var allElementsProject = new FrameTrailProject
    {
        MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 3 }] },
        Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 3, TimelineStart = 0 },
        Overlays = [new() { MediaPath = "sticker.png", Start = 0, Duration = 3, Rect = new(.4, .4, .2, .2), EnterMotion = OverlayMotion.None, ExitMotion = OverlayMotion.None }],
        Settings = new() { Opening = new() { Enabled = true, Preset = preset, Duration = 1.2, Stagger = .2,
            IncludedElements = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays],
            ElementOrder = [OpeningElement.Overlays, OpeningElement.Camera, OpeningElement.Screen] } }
    };
    var allElementsEvaluator = new PreparedFrameSceneEvaluator(allElementsProject);
    var atStart = allElementsEvaluator.Evaluate(0);
    var atFirstStagger = allElementsEvaluator.Evaluate(.2);
    var atSecondStagger = allElementsEvaluator.Evaluate(.4);
    var atThirdStagger = allElementsEvaluator.Evaluate(.6);
    var settled = allElementsEvaluator.Evaluate(1.19);
    var expectedScreen = preset switch
    {
        OpeningPreset.ZoomUp => (.72, 0d, .045), OpeningPreset.Fade => (1d, 0d, 0d), OpeningPreset.SlideUp => (1d, 0d, .16),
        OpeningPreset.RotateIn => (.86, 0d, .02), OpeningPreset.Converge => (.72, -1d, -.62), OpeningPreset.SideSlide => (.94, -1d, 0d),
        OpeningPreset.Light3D => (.8, -1d, .08), _ => (.85, 0d, .04)
    };
    var expectedCamera = preset switch
    {
        OpeningPreset.Converge => (.58, 1d, .72), OpeningPreset.SideSlide => (.88, 1d, 0d), OpeningPreset.Light3D => (.72, 1d, .16),
        _ => expectedScreen
    };
    var expectedOverlay = preset switch
    {
        OpeningPreset.Converge => (.62, -1d, -.70), OpeningPreset.SideSlide => (.9, -1d, 0d), OpeningPreset.Light3D => (.76, -1d, -.12),
        _ => expectedScreen
    };
    Check(atStart.Opening is not null && atStart.CameraOpening is not null && atStart.OverlayOpening is not null
        && (atStart.Opening.Scale, atStart.Opening.OffsetX, atStart.Opening.OffsetY) == expectedScreen
        && (atStart.CameraOpening.Scale, atStart.CameraOpening.OffsetX, atStart.CameraOpening.OffsetY) == expectedCamera
        && (atStart.OverlayOpening.Scale, atStart.OverlayOpening.OffsetX, atStart.OverlayOpening.OffsetY) == expectedOverlay
        && atFirstStagger.OverlayOpening != atStart.OverlayOpening
        && atFirstStagger.CameraOpening == atStart.CameraOpening && atFirstStagger.Opening == atStart.Opening
        && atSecondStagger.OverlayOpening != atFirstStagger.OverlayOpening && atSecondStagger.CameraOpening != atFirstStagger.CameraOpening
        && atSecondStagger.Opening == atFirstStagger.Opening && atThirdStagger.Opening != atSecondStagger.Opening
        && settled.Opening?.Scale > .99 && settled.CameraOpening?.Scale > .99 && settled.OverlayOpening?.Scale > .99,
        $"opening preset {preset} animates all enabled elements in the requested stagger order and settles before duration");
}

var negativeCameraProject = new FrameTrailProject
{
    MediaPath = "capture.mp4",
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    Camera = new() { MediaPath = "camera.mp4", SourceStart = 1, Duration = 1, TimelineStart = -.5, BorderWidth = 4, BorderColor = "#ffcc00" }
};
var negativeCameraFrame = new PreparedFrameSceneEvaluator(negativeCameraProject).Evaluate(0);
Check(negativeCameraFrame.Camera?.SourceTime == 1.5 && negativeCameraFrame.Camera.BorderWidth == 4 && negativeCameraFrame.Camera.BorderColor == "#ffcc00", "negative camera timeline offset and border settings map into frame output");

var syncedCameraProject = new FrameTrailProject
{
    MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 13 },
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 3 }, new() { SourceStart = 10, SourceDuration = 3 }] },
    Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 12, TimelineStart = 0,
        SyncSegments = [new(0, 0, 1), new(1, 2, 1), new(2, 10, 2)] }
};
var syncedCameraEvaluator = new PreparedFrameSceneEvaluator(syncedCameraProject);
Check(Math.Abs(syncedCameraEvaluator.Evaluate(.5).Camera!.SourceTime - .5) < 1e-9
    && syncedCameraEvaluator.Evaluate(1.5).Camera is null
    && Math.Abs(syncedCameraEvaluator.Evaluate(2.5).Camera!.SourceTime - 1.5) < 1e-9
    && Math.Abs(syncedCameraEvaluator.Evaluate(4).Camera!.SourceTime - 3) < 1e-9,
    "camera sync segments follow screen source time across a pause gap and reordered timeline mapping");
var editedSyncedCamera = Copy(syncedCameraProject).Camera!;
CameraTimelineOperations.TrimTimelineStart(editedSyncedCamera, .5);
CameraTimelineOperations.TrimTimelineEnd(editedSyncedCamera, 2.5);
CameraTimelineOperations.Move(editedSyncedCamera, .25);
CameraTimelineOperations.TrimAssetStart(editedSyncedCamera, 1.25, 3);
CameraTimelineOperations.TrimAssetEnd(editedSyncedCamera, 1.75, 3);
Check(editedSyncedCamera.SyncSegments is { Count: 1 } editedParts
    && Math.Abs(editedParts[0].SourceStart - 1.25) < 1e-9
    && Math.Abs(editedParts[0].TimelineStart - 2.5) < 1e-9
    && Math.Abs(editedParts[0].Duration - .25) < 1e-9
    && Math.Abs(editedSyncedCamera.Duration - .25) < 1e-9,
    "camera trim, move and asset-bound operations preserve discontinuous source mappings");
var slippedCameraTime = CameraTimelineOperations.SlipAssetStart(editedSyncedCamera, 2.9, 3);
Check(Math.Abs(slippedCameraTime - 2.75) < 1e-9
    && Math.Abs(editedSyncedCamera.SyncSegments![0].TimelineStart - 2.5) < 1e-9
    && Math.Abs(editedSyncedCamera.SyncSegments[0].Duration - .25) < 1e-9,
    "camera slip shifts asset time without moving timeline alignment and clamps at media end");
try
{
    var invalid = Copy(negativeCameraProject);
    invalid.Settings.Canvas = new() { BorderWidth = 61 };
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-border.frametrail"), invalid);
    throw new Exception("oversized pixel canvas border was accepted");
}
catch (InvalidDataException) { }
try
{
    var invalid = Copy(negativeCameraProject);
    invalid.Settings.Canvas = new() { FrameOuterRadius = 160.01 };
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-frame-radius.frametrail"), invalid);
    throw new Exception("oversized mock frame radius was accepted");
}
catch (InvalidDataException) { }
try
{
    var invalid = Copy(customSceneBase);
    invalid.ZoomClips = [new() { Start = 0, End = 1, Easing = ZoomEasing.Custom, CustomCurve = new(double.NaN, 0, 1, 1) }];
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-custom-curve.frametrail"), invalid);
    throw new Exception("non-finite custom easing point was accepted");
}
catch (InvalidDataException) { }
try
{
    var invalid = Copy(customSceneBase);
    invalid.Settings.Spring.Stiffness = double.NaN;
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-spring.frametrail"), invalid);
    throw new Exception("non-finite spring parameter was accepted");
}
catch (InvalidDataException) { }
Check((int)ZoomEasing.Linear == 0 && (int)ZoomEasing.Quadratic == 4 && (int)ZoomEasing.Custom == 5,
    "custom easing is appended without changing existing enum values");
try
{
    var invalid = Copy(negativeCameraProject);
    invalid.Settings.Canvas = new() { FrameToolbarScale = .64 };
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-toolbar-scale.frametrail"), invalid);
    throw new Exception("out-of-range mock frame toolbar scale was accepted");
}
catch (InvalidDataException) { }
try
{
    var invalid = Copy(negativeCameraProject);
    invalid.Settings.BackgroundStyle = new() { Blur = 1.01 };
    FrameTrailProjectStore.Save(Path.Combine(root, "invalid-background-blur.frametrail"), invalid);
    throw new Exception("out-of-range normalized background blur was accepted");
}
catch (InvalidDataException) { }

Console.WriteLine("FrameTrail.Core tests passed.");

