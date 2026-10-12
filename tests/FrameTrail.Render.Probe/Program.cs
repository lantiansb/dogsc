using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.Media;
using Vortice.D3DCompiler;

const int sourceWidth = 320, sourceHeight = 180, outputWidth = 640, outputHeight = 360;
if (args.Length > 0 && args[0] == "--frame-clock")
{
    await FrameTrail.Render.Probe.FrameClockProbe.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--playback-throughput")
{
    await FrameTrail.Render.Probe.PlaybackThroughputProbe.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--scrub-decoder")
{
    await FrameTrail.Render.Probe.ScrubFrameDecoderProbe.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--native-seek-decoder")
{
    await FrameTrail.Render.Probe.NativeSeekDecoderProbe.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--timeline-assets")
{
    await TimelineAssetsProbe.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--native-playback")
{
    await FrameTrail.Render.Probe.NativePlaybackProbe.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--color-slow-motion")
{
    await ColorAndSlowMotionProbe.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length > 0 && args[0] == "--dogsc-camera-import")
{
    await FrameTrail.Render.Probe.DogScCameraImportProbe.RunAsync();
    return;
}
if (args.Length == 2 && args[0] == "--real-paused-project")
{
    await RunRealPausedProjectExport(args[1]);
    return;
}
var source = MakeCheckerboard(sourceWidth, sourceHeight);
CursorDynamicsRenderProbe.Run();
RecordedCursorRenderProbe.Run();
ZoomFocusRenderProbe.Run();
LayerShadowRenderProbe.Run();
OverlayBackdropRenderProbe.Run();
SpotlightRenderProbe.Run();
FrameTrail.Render.Probe.SourceSpaceMappingProbe.Run();
CameraMotionRenderProbe.Run();
using (var textLoader = new SceneAssetLoader(Path.Combine(Path.GetTempPath(), "frametrail-text-test.frametrail"), outputWidth, outputHeight))
{
    var textLayers = await textLoader.LoadAsync(new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .07, .05, outputWidth, outputHeight, .2,
        Canvas: new CanvasSettings { Frame = ScreenFrameKind.BrowserDark, Title = "FrameTrail 标题", Address = "https://example.test" }, SourceWidth: 1920, SourceHeight: 1080));
    Check(textLayers.Count == 1 && textLayers[0].Bgra.Any(x => x != 0) && textLayers[0].Rect.Y < .2, "WPF title and address rasterize into the frame header layer");
}
var audioTimeline = new ProjectTimeline { Segments = [new() { SourceStart = 0, SourceDuration = 2 }, new() { SourceStart = 10, SourceDuration = 2, PlaybackRate = 2 }] };
var audioFilter = MediaTools.AudioTrackFilter(audioTimeline, [new AudioTrack { MediaPath = "mic.wav", SourceStart = 5, Duration = 10, OffsetSeconds = 1, Volume = .5 }]);
Check(audioFilter.Contains("atrim=start=5:end=6") && audioFilter.Contains("atrim=start=14:end=15") && audioFilter.Contains("adelay=1000") && audioFilter.Contains("atempo=2"), "independent audio follows source timeline trims, rate and output offsets");
try
{
    _ = MediaTools.Ffmpeg;
    var audioRoot = Path.Combine(Path.GetTempPath(), "frametrail-audio-probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(audioRoot);
    try
    {
        var input = Path.Combine(audioRoot, "source.wav"); var script = Path.Combine(audioRoot, "filter.txt"); var output = Path.Combine(audioRoot, "mixed.wav");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=20", "-c:a", "pcm_s16le", "-y", input]);
        await File.WriteAllTextAsync(script, audioFilter);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", input, "-filter_complex_script", script, "-map", "[out]", "-c:a", "pcm_s16le", "-y", output]);
        var info = await MediaTools.ProbeAsync(output);
        Check(Math.Abs(info.Duration - audioTimeline.Duration) < .02, "FFmpeg renders independent track at exact edited timeline duration");
        var sourceRms = await MeasureAudioRms(input, 5, .5);
        var silentLeadRms = await MeasureAudioRms(output, 0, .5);
        var firstSpeedRms = await MeasureAudioRms(output, 1.25, .5);
        var secondSpeedRms = await MeasureAudioRms(output, 2.125, .25);
        var silentTailRms = await MeasureAudioRms(output, 2.75, .25);
        Check(silentLeadRms < sourceRms * .02, "independent audio preserves the 1s silent lead");
        Check(Math.Abs(firstSpeedRms / sourceRms - .5) < .08, "first edited region has 0.5x gain after source trim");
        Check(Math.Abs(secondSpeedRms / sourceRms - .5) < .08, "second edited region keeps 0.5x gain through 2x retiming");
        Check(silentTailRms < sourceRms * .02, "independent audio preserves the final 0.5s silence");

        var pausedWav = Path.Combine(audioRoot, "paused-track.wav");
        var pausedOutput = Path.Combine(audioRoot, "paused-track.m4a");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=71", "-c:a", "pcm_s16le", "-y", pausedWav]);
        var reorderedTimeline = new ProjectTimeline { Segments = [new() { SourceStart = 35.4666665, SourceDuration = 35.4666665, PlaybackRate = 2 }, new() { SourceStart = 0, SourceDuration = 35.4666665 }] };
        var pausedTracks = new AudioTrack[]
        {
            new() { MediaPath = "system.wav", SourceStart = 0, Duration = 1.3002917, OffsetSeconds = .4033104, Volume = 1 },
            new() { MediaPath = "system.wav", SourceStart = 1.6830296, Duration = 68.8148345, OffsetSeconds = 1.8665808, Volume = 1 }
        };
        var pausedFilterPath = Path.Combine(audioRoot, "paused-filter.txt");
        await File.WriteAllTextAsync(pausedFilterPath, MediaTools.AudioTrackFilter(reorderedTimeline, pausedTracks));
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", pausedWav, "-i", pausedWav,
            "-filter_complex_script", pausedFilterPath, "-map", "[out]", "-vn", "-c:a", "aac", "-b:a", "192k", "-y", pausedOutput]);
        var pausedInfo = await MediaTools.ProbeAsync(pausedOutput);
        Check(pausedInfo.HasAudio && Math.Abs(pausedInfo.Duration - reorderedTimeline.Duration) < .12,
            "multiple paused intervals from one WAV remain monotonic after retiming, reordering and 2x playback");
    }
    finally { Directory.Delete(audioRoot, true); }
}
catch (FileNotFoundException) { }
var ffmpegAvailable = true;
try { _ = MediaTools.Ffmpeg; _ = MediaTools.Ffprobe; }
catch (FileNotFoundException) { ffmpegAvailable = false; }
if (ffmpegAvailable) await RunCompositeExportProbe();
if (ffmpegAvailable) await ColorAndSlowMotionProbe.Run();
if (ffmpegAvailable) await OverlaySceneExportProbe.Run();
if (ffmpegAvailable) await CameraMediaPipelineProbe.RunAsync();
if (ffmpegAvailable) await CameraClockMediaProbe.RunAsync();
if (ffmpegAvailable) await SceneAssetCacheProbe.RunAsync();
var hlslPath = FindWorkspaceFile(Path.Combine("src", "FrameTrail.Media", "Scene.hlsl"));
var hlsl = File.ReadAllText(hlslPath);
_ = Compiler.Compile(hlsl, "VSMain", "Scene.hlsl", "vs_5_0");
_ = Compiler.Compile(hlsl, "PSMain", "Scene.hlsl", "ps_5_0");
var plain = new byte[outputWidth * outputHeight * 4];
var zoomed = new byte[plain.Length];
var cursor = new byte[plain.Length];
var layered = new byte[plain.Length];
var styled = new byte[plain.Length];
var patterned = new byte[plain.Length];
var dynamicA = new byte[plain.Length];
var dynamicB = new byte[plain.Length];
var baseScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .07, .055, 1920, 1080, .2);
var sourceRoundTrip = new NormalizedPoint(.61, .43);
var mappingScene = new FrameScene(.5, .5, new(0, 0), new(1, 1), 1, null, 0, "#202124", .1, 0, 640, 360, 0,
    Canvas: new() { AspectWidth = 16, AspectHeight = 9, Crop = new(.1, .08, .8, .84), ContentPosition = new(.53, .46) },
    ScreenMotion: new() { Position = new(.53, .46), Scale = 1.1, RotationX = 11, RotationY = -7, RotationZ = 5, Perspective = .7 },
    SourceWidth: 320, SourceHeight: 180, LayoutModel: 2);
var canvasRoundTrip = ProjectSourcePoint(mappingScene, sourceRoundTrip, 320, 180, 640, 360);
Check(GpuFrameRenderer.TryMapCanvasPointToSource(mappingScene, 320, 180, 640, 360, canvasRoundTrip, out var recoveredSource)
    && Math.Abs(recoveredSource.X - sourceRoundTrip.X) < 1e-5 && Math.Abs(recoveredSource.Y - sourceRoundTrip.Y) < 1e-5,
    "canvas-to-source mapping reverses crop, framing, XYZ perspective and Z rotation");
var projectedBody = GpuFrameRenderer.GetProjectedScreenCorners(mappingScene, 320, 180, 640, 360, includeHeader: false);
var projectedCard = GpuFrameRenderer.GetProjectedScreenCorners(mappingScene with { Canvas = mappingScene.Canvas! with { Frame = ScreenFrameKind.WindowLight } }, 320, 180, 640, 360);
Check(projectedBody.Count == 4 && projectedCard.Count == 4 && projectedBody.All(p => p.IsFinite) && projectedCard.All(p => p.IsFinite)
    && projectedCard[0].Y < projectedBody[0].Y,
    "projected selection corners follow XYZ perspective and include mock header bounds");
var quadScene = mappingScene with { CropOrigin = new(0, 0), CropSize = new(1, 1), Zoom = 1, CornerRadius = 0, Padding = .3,
    Canvas = mappingScene.Canvas! with { Crop = NormalizedRect.Full } };
var quadCorners = GpuFrameRenderer.GetProjectedScreenCorners(quadScene, 320, 180, 640, 360, includeHeader: false);
var expectedQuadSources = new[] { new NormalizedPoint(0, 0), new(1, 0), new(1, 1), new(0, 1) };
var cornerMappings = Enumerable.Range(0, 4).Select(i => (GpuFrameRenderer.TryMapCanvasPointToSource(quadScene, 320, 180, 640, 360, quadCorners[i], out var point), point)).ToArray();
var cornersValid = Enumerable.Range(0, 4).All(i => cornerMappings[i].Item1 && Math.Abs(cornerMappings[i].point.X - expectedQuadSources[i].X) < 1e-4 && Math.Abs(cornerMappings[i].point.Y - expectedQuadSources[i].Y) < 1e-4);
Check(cornersValid,
    "projected body corners invert to source corners: " + string.Join(";", Enumerable.Range(0, 4)
        .Select(i => quadCorners[i] + " -> " + cornerMappings[i].point + " (" + cornerMappings[i].Item1 + ")")));
var roundedScene = mappingScene with { CropOrigin = new(0, 0), CropSize = new(1, 1), Canvas = new() { AspectWidth = 16, AspectHeight = 9, ContentPosition = new(.5, .5) }, ScreenMotion = new() { Position = new(.5, .5) }, Padding = .1, CornerRadius = .1 };
Check(!GpuFrameRenderer.TryMapCanvasPointToSource(roundedScene, 320, 180, 640, 360, new(.02, .5), out _)
    && !GpuFrameRenderer.TryMapCanvasPointToSource(roundedScene, 320, 180, 640, 360, new(.102, .103), out _),
    "canvas-to-source mapping rejects the background and rounded screen corners");
// Zoom magnifies the whole card (Mac ZoomViewportTransform): 2x about the card centre, then the focus is composed into the 30% inset.
var zoomPlain = mappingScene with { ScreenMotion = null, Canvas = new() { AspectWidth = 16, AspectHeight = 9 }, Padding = .1, CornerRadius = 0 };
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(zoomPlain with { ZoomSample = new ZoomSampleState(2, new(.5, .5), 1) }, 320, 180, 640, 360, new(0, 0), out var zoomCorner)
    && Math.Abs(zoomCorner.X + .3) < 1e-4 && Math.Abs(zoomCorner.Y + .3) < 1e-4,
    "2x zoom magnifies the whole card about its centre and pushes the card corner off the canvas: " + zoomCorner);
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(zoomPlain with { ZoomSample = new ZoomSampleState(2, new(.8, .5), 1) }, 320, 180, 640, 360, new(.8, .5), out var zoomFocus)
    && Math.Abs(zoomFocus.X - .66) < 1e-4 && Math.Abs(zoomFocus.Y - .5) < 1e-4,
    "an edge focus is composed to 70% of the card, not pinned to the canvas centre: " + zoomFocus);
Check(GpuFrameRenderer.TryMapCanvasPointToSource(zoomPlain with { ZoomSample = new ZoomSampleState(2, new(.8, .5), 1) }, 320, 180, 640, 360, new(.66, .5), out var zoomBack)
    && Math.Abs(zoomBack.X - .8) < 1e-4 && Math.Abs(zoomBack.Y - .5) < 1e-4, "canvas-to-source mapping inverts the zoomed card: " + zoomBack);
// A covered full-bleed card clamps instead of exposing the background, and has no rounded corners.
var covered = zoomPlain with { Padding = 0 };
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(covered with { ZoomSample = new ZoomSampleState(2, new(.1, .1), 1) }, 320, 180, 640, 360, new(0, 0), out var coveredCorner)
    && Math.Abs(coveredCorner.X) < 1e-4 && Math.Abs(coveredCorner.Y) < 1e-4, "a card that covers the canvas keeps its edge pinned while zoomed: " + coveredCorner);
// A cropped picture keeps its own aspect ratio: crop 50% x 100% of a 16:9 source is 160x180, fitted to 256x288 inside the 512x288 area.
var croppedScene = zoomPlain with { Canvas = new() { AspectWidth = 16, AspectHeight = 9, Crop = new(0, 0, .5, 1) } };
var cropTopLeft = default(NormalizedPoint); var cropBottomRight = default(NormalizedPoint);
var cropMapped = GpuFrameRenderer.TryMapSourcePointToCanvas(croppedScene, 320, 180, 640, 360, new(0, 0), out cropTopLeft)
    && GpuFrameRenderer.TryMapSourcePointToCanvas(croppedScene, 320, 180, 640, 360, new(.5, 1), out cropBottomRight);
Check(cropMapped && Math.Abs(cropTopLeft.X - .3) < 1e-4 && Math.Abs(cropTopLeft.Y - .1) < 1e-4 && Math.Abs(cropBottomRight.X - .7) < 1e-4 && Math.Abs(cropBottomRight.Y - .9) < 1e-4,
    "a non-uniform crop is not stretched to the full source aspect: " + cropTopLeft + " " + cropBottomRight);
// LayoutModel 1 (projects saved before the Mac card model) keeps the b39f01f geometry: whole-source fit, card-centre position, in-card zoom crop.
var legacyMappingScene = mappingScene with { LayoutModel = 1, CropOrigin = new(.25, .2), CropSize = new(.5, .6), Zoom = 2 };
var legacyCanvasRoundTrip = ProjectSourcePointLegacy(legacyMappingScene, sourceRoundTrip, 320, 180, 640, 360);
Check(GpuFrameRenderer.TryMapCanvasPointToSource(legacyMappingScene, 320, 180, 640, 360, legacyCanvasRoundTrip, out var legacyRecovered)
    && Math.Abs(legacyRecovered.X - sourceRoundTrip.X) < 1e-5 && Math.Abs(legacyRecovered.Y - sourceRoundTrip.Y) < 1e-5,
    "model 1: canvas-to-source mapping reverses crop, in-card zoom, card-centre framing, XYZ perspective and Z rotation");
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(legacyMappingScene, 320, 180, 640, 360, sourceRoundTrip, out var legacyForward)
    && Math.Abs(legacyForward.X - legacyCanvasRoundTrip.X) < 1e-4 && Math.Abs(legacyForward.Y - legacyCanvasRoundTrip.Y) < 1e-4,
    "model 1: source-to-canvas mapping agrees with the b39f01f oracle: " + legacyForward + " vs " + legacyCanvasRoundTrip);
var legacyPlain = zoomPlain with { LayoutModel = 1, CropOrigin = new(.25, .2), CropSize = new(.5, .6), Zoom = 2 };
var legacyZoomTopLeft = default(NormalizedPoint); var legacyZoomBottomRight = default(NormalizedPoint);
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(legacyPlain, 320, 180, 640, 360, new(.25, .2), out legacyZoomTopLeft)
    && GpuFrameRenderer.TryMapSourcePointToCanvas(legacyPlain, 320, 180, 640, 360, new(.75, .8), out legacyZoomBottomRight)
    && Math.Abs(legacyZoomTopLeft.X - .1) < 1e-4 && Math.Abs(legacyZoomTopLeft.Y - .1) < 1e-4
    && Math.Abs(legacyZoomBottomRight.X - .9) < 1e-4 && Math.Abs(legacyZoomBottomRight.Y - .9) < 1e-4,
    "model 1: the zoom focus window fills the fixed card (the card does not grow): " + legacyZoomTopLeft + " " + legacyZoomBottomRight);
var legacyCropped = zoomPlain with { LayoutModel = 1, Canvas = new() { AspectWidth = 16, AspectHeight = 9, Crop = new(0, 0, .5, 1) } };
var legacyCropTopLeft = default(NormalizedPoint); var legacyCropBottomRight = default(NormalizedPoint);
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(legacyCropped, 320, 180, 640, 360, new(0, 0), out legacyCropTopLeft)
    && GpuFrameRenderer.TryMapSourcePointToCanvas(legacyCropped, 320, 180, 640, 360, new(.5, 1), out legacyCropBottomRight)
    && Math.Abs(legacyCropTopLeft.X - .1) < 1e-4 && Math.Abs(legacyCropTopLeft.Y - .1) < 1e-4
    && Math.Abs(legacyCropBottomRight.X - .9) < 1e-4 && Math.Abs(legacyCropBottomRight.Y - .9) < 1e-4,
    "model 1: a canvas crop is stretched over the card fitted to the whole source: " + legacyCropTopLeft + " " + legacyCropBottomRight);
// A screen-motion position means different things in the two models: card centre (1) versus point of interest (2).
var positionScene = zoomPlain with { LayoutModel = 1, ScreenMotion = new() { Position = new(.7, .4), Scale = 1 } };
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(positionScene, 320, 180, 640, 360, new(.5, .5), out var legacyCentre)
    && Math.Abs(legacyCentre.X - .7) < 1e-4 && Math.Abs(legacyCentre.Y - .4) < 1e-4,
    "model 1: the screen-motion position is the card centre: " + legacyCentre);
Check(GpuFrameRenderer.TryMapSourcePointToCanvas(positionScene with { LayoutModel = 2 }, 320, 180, 640, 360, new(.5, .5), out var macCentre)
    && Math.Abs(macCentre.X - .54) < 1e-4 && Math.Abs(macCentre.Y - .48) < 1e-4,
    "model 2: the same position is a point of interest, the card centre only shifts by the framing offset: " + macCentre);
// Header strip, projected corners and mapping stay consistent in both models.
foreach (var model in new[] { 1, 2 })
{
    var framed = mappingScene with { LayoutModel = model, CornerRadius = 0, ScreenMotion = null, Padding = .1,
        Canvas = new() { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.WindowLight, Crop = new(.1, .08, .8, .84) },
        CropOrigin = new(.25, .2), CropSize = new(.5, .6), Zoom = 2 };
    var headerRect = GpuFrameRenderer.GetScreenHeaderRect(framed, 320, 180, 640, 360);
    var body = GpuFrameRenderer.GetProjectedScreenCorners(framed, 320, 180, 640, 360, includeHeader: false);
    var card = GpuFrameRenderer.GetProjectedScreenCorners(framed, 320, 180, 640, 360, includeHeader: true);
    Check(headerRect.Height > 0 && Math.Abs(headerRect.X - body[0].X) < 1e-4 && Math.Abs(headerRect.Y + headerRect.Height - body[0].Y) < 1e-4
        && Math.Abs(card[0].Y - headerRect.Y) < 1e-4 && Math.Abs(headerRect.X + headerRect.Width - body[1].X) < 1e-4,
        $"model {model}: the header strip sits exactly on top of the projected screen body: {headerRect} {body[0]} {card[0]}");
    var bodyCentre = new NormalizedPoint((body[0].X + body[2].X) / 2, (body[0].Y + body[2].Y) / 2);
    Check(GpuFrameRenderer.TryMapCanvasPointToSource(framed, 320, 180, 640, 360, bodyCentre, out var centreSource)
        && GpuFrameRenderer.TryMapSourcePointToCanvas(framed, 320, 180, 640, 360, centreSource, out var centreBack)
        && Math.Abs(centreBack.X - bodyCentre.X) < 1e-4 && Math.Abs(centreBack.Y - bodyCentre.Y) < 1e-4,
        $"model {model}: canvas-to-source and source-to-canvas mapping are inverse at the card centre");
}
var headerScene = roundedScene with { CornerRadius = 0, Canvas = new() { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.WindowLight } };
Check(!GpuFrameRenderer.TryMapCanvasPointToSource(headerScene, 320, 180, 640, 360, new(.5, .11), out _),
    "canvas-to-source mapping does not treat a mock window header as recorded screen content");
using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, outputWidth, outputHeight);
renderer.Render(source, baseScene, plain);
var zoomScene = baseScene with { CropOrigin = new(.25, .25), CropSize = new(.5, .5), Zoom = 2 };
renderer.Render(source, zoomScene, zoomed);
var cursorScene = baseScene with { Cursor = new(.52, .5), ClickStrength = .7 };
renderer.Render(source, cursorScene, cursor);
var redOverlay = new FrameLayerInput(1, 1, [0, 0, 255, 255], new(.1, .1, .2, .2), Opacity: 1, Layer: 1, CornerRadius: .02);
renderer.Render(source, baseScene, layered, [redOverlay]);
var styledScene = baseScene with
{
    BackgroundStyle = new() { Kind = BackgroundKind.LinearGradient, Color = "#ff0000", EndColor = "#0000ff", AngleDegrees = 0 },
    Canvas = new() { Frame = ScreenFrameKind.BrowserDark, Title = "FrameTrail", Address = "https://example.test" },
    ScreenMotion = new() { Position = new(.5, .5), Scale = 1, RotationX = 12, RotationY = -8, RotationZ = 3, Perspective = .65 },
    Opening = new(.72, .08, .02, 8, .4),
    BlurRegions = [new(new(.28, .25, .44, .5), 1, BlurKind.Pixelate, .02, 1)]
};
renderer.Render(source, styledScene, styled);
var titleCanvas = new CanvasSettings { Frame = ScreenFrameKind.BrowserDark, Title = "透视标题", Address = "https://motion.test" };
var titleScene = baseScene with { Canvas = titleCanvas, SourceWidth = 1920, SourceHeight = 1080, ScreenMotion = new() { Position = new(.5, .5), Scale = 1, RotationX = 0, RotationY = 0, Perspective = .8 } };
var titleBlankScene = titleScene with { Canvas = new() { Frame = ScreenFrameKind.BrowserDark } };
var titleFlat = new byte[plain.Length]; var blankFlat = new byte[plain.Length];
var titleTilted = new byte[plain.Length]; var blankTilted = new byte[plain.Length];
using (var titleLoader = new SceneAssetLoader(Path.Combine(Path.GetTempPath(), "frametrail-title-transform.frametrail"), outputWidth, outputHeight))
{
    var titleLayers = await titleLoader.LoadAsync(titleScene);
    renderer.Render(source, titleScene, titleFlat, titleLayers);
    renderer.Render(source, titleBlankScene, blankFlat);
    var tiltedScene = titleScene with { ScreenMotion = titleScene.ScreenMotion! with { RotationX = 9, RotationY = -7, RotationZ = 3, Perspective = .55 } };
    var tiltedBlankScene = titleBlankScene with { ScreenMotion = tiltedScene.ScreenMotion };
    titleLayers = await titleLoader.LoadAsync(tiltedScene);
    renderer.Render(source, tiltedScene, titleTilted, titleLayers);
    renderer.Render(source, tiltedBlankScene, blankTilted);
}
var flatTitleBounds = DifferenceBounds(blankFlat, titleFlat, outputWidth, outputHeight);
var tiltedTitleBounds = DifferenceBounds(blankTilted, titleTilted, outputWidth, outputHeight);
Console.WriteLine($"Perspective title bounds: flat {flatTitleBounds.Width}×{flatTitleBounds.Height} → tilted {tiltedTitleBounds.Width}×{tiltedTitleBounds.Height}");
// Z rotation can enlarge a glyph's canvas-aligned box. The old height-decrease assertion
// accidentally depended on clipping the projected text to its untransformed header rectangle.
Check(flatTitleBounds.Width > 20 && tiltedTitleBounds.Width > 10 && tiltedTitleBounds.Width != flatTitleBounds.Width && tiltedTitleBounds.Height != flatTitleBounds.Height,
    "browser title and address follow screen X/Y/Z perspective instead of floating above the frame");
var blurAssetRoot = Path.Combine(Path.GetTempPath(), "frametrail-bg-blur-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(blurAssetRoot);
try
{
    var blurProjectFile = Path.Combine(blurAssetRoot, "project.frametrail"); File.WriteAllText(blurProjectFile, "{}");
    SavePngPixels(Path.Combine(blurAssetRoot, "checker.png"), 320, 180, MakeCheckerboard(320, 180));
    var sharpScene = baseScene with { BackgroundStyle = new() { Kind = BackgroundKind.Image, ImagePath = "checker.png", ImageFit = BackgroundImageFit.Cover, Blur = 0 } };
    var blurredScene = sharpScene with { BackgroundStyle = sharpScene.BackgroundStyle! with { Blur = 1 } };
    using var blurLoader = new SceneAssetLoader(blurProjectFile, outputWidth, outputHeight);
    var sharpBackground = new byte[plain.Length]; var blurredBackground = new byte[plain.Length];
    renderer.Render(source, sharpScene, sharpBackground, await blurLoader.LoadAsync(sharpScene));
    renderer.Render(source, blurredScene, blurredBackground, await blurLoader.LoadAsync(blurredScene));
    Check(CountDifferent(sharpBackground, blurredBackground) > 5000, "background image blur changes rendered pixels");
    SavePngPixels(Path.Combine(blurAssetRoot, "overlay-checker.png"), 320, 180, MakeCheckerboard(320, 180));
    var sharpOverlayScene = baseScene with { Overlays = [new(new Guid("11111111-1111-1111-1111-111111111111"), "overlay-checker.png", new(.2, .2, .4, .4), 0, 1, 1, 0, 0)] };
    var blurredOverlayScene = sharpOverlayScene with { Overlays = [sharpOverlayScene.Overlays![0] with { Blur = 1 }] };
    var sharpOverlay = new byte[plain.Length]; var blurredOverlay = new byte[plain.Length];
    renderer.Render(source, sharpOverlayScene, sharpOverlay, await blurLoader.LoadAsync(sharpOverlayScene));
    renderer.Render(source, blurredOverlayScene, blurredOverlay, await blurLoader.LoadAsync(blurredOverlayScene));
    Check(CountDifferent(sharpOverlay, blurredOverlay) > 100, "overlay blur property reaches the shared scene compositor and changes rendered pixels");
}
finally { Directory.Delete(blurAssetRoot, true); }

Check(plain[0] == 36 && plain[1] == 33 && plain[2] == 32 && plain[3] == 255, "background color at canvas corner");
var cornerProbe = (14 * outputWidth + 25) * 4;
Check(Math.Abs(plain[cornerProbe] - 36) < 3 && Math.Abs(plain[cornerProbe + 1] - 33) < 3, "screen corner is clipped to background");
{
    var solidScreen = MakeSolid(0, 0, 245, sourceWidth, sourceHeight);
    var frameScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0,
        outputWidth, outputHeight, 0, Canvas: new CanvasSettings { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.WindowLight },
        SourceWidth: sourceWidth, SourceHeight: sourceHeight);
    var windowPixels = new byte[plain.Length]; var browserPixels = new byte[plain.Length]; var noFramePixels = new byte[plain.Length];
    renderer.Render(solidScreen, frameScene, windowPixels);
    renderer.Render(solidScreen, frameScene with { Canvas = frameScene.Canvas! with { Frame = ScreenFrameKind.BrowserDark } }, browserPixels);
    renderer.Render(solidScreen, frameScene with { Canvas = frameScene.Canvas! with { Frame = ScreenFrameKind.None } }, noFramePixels);
    int RedAt(byte[] pixels, int x, int y) => pixels[(y * outputWidth + x) * 4 + 2];
    Check(RedAt(windowPixels, 14, 359) < 80 && RedAt(windowPixels, 20, 359) > 200,
        "window chrome keeps its 18px-scaled outer corner while content radius is zero and preserves the straight lower edge");
    Check(RedAt(browserPixels, 14, 359) < 80 && RedAt(browserPixels, 23, 359) > 200,
        "browser chrome uses its larger 22px-scaled outer corner and preserves the straight lower edge");
    Check(RedAt(noFramePixels, 14, 359) > 200,
        "no-frame content remains square when its content radius is zero");
    var explicitSquareOuter = frameScene with
    {
        CornerRadius = .08,
        Canvas = frameScene.Canvas! with { FrameOuterRadius = 0, BorderWidth = 16 }
    };
    var explicitSquarePixels = new byte[plain.Length];
    renderer.Render(solidScreen, explicitSquareOuter, explicitSquarePixels);
    Check(RedAt(explicitSquarePixels, 14, 0) > 200,
        "explicit zero desktop outer radius keeps the top chrome corner square despite rounded content and border width");
    Check(!GpuFrameRenderer.TryMapCanvasPointToSource(frameScene, sourceWidth, sourceHeight, outputWidth, outputHeight,
            new(14.5 / outputWidth, 359.5 / outputHeight), out _)
        && GpuFrameRenderer.TryMapCanvasPointToSource(frameScene, sourceWidth, sourceHeight, outputWidth, outputHeight,
            new(20.5 / outputWidth, 359.5 / outputHeight), out _),
        "canvas hit mapping rejects the window's outer clipped corner while accepting its straight edge");
}
{
    var shadowSource = MakeSolid(0, 0, 245, sourceWidth, sourceHeight);
    var shadowBase = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .1, 0,
        outputWidth, outputHeight, .5, Canvas: new CanvasSettings { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.None },
        SourceWidth: sourceWidth, SourceHeight: sourceHeight);
    var noFrameShadow = new byte[plain.Length]; var noFrameBorderedShadow = new byte[plain.Length];
    renderer.Render(shadowSource, shadowBase, noFrameShadow);
    renderer.Render(shadowSource, shadowBase with { Canvas = shadowBase.Canvas! with { BorderWidth = 16 } }, noFrameBorderedShadow);
    var outsideCorner = (32 * outputWidth + 60) * 4;
    Check(noFrameShadow.AsSpan(outsideCorner, 4).SequenceEqual(noFrameBorderedShadow.AsSpan(outsideCorner, 4)),
        "no-frame shadow corner keeps its original radius when border width changes");
}
Check(CountDifferent(plain, zoomed) > 10000, "zoom crop changes output pixels");
Check(CountDifferent(plain, cursor) > 50, "cursor and click pulse change output pixels");
var overlayProbe = ((int)(outputHeight * .15) * outputWidth + (int)(outputWidth * .15)) * 4;
Check(layered[overlayProbe + 2] > 220 && CountDifferent(plain, layered) > 1000, "decoded image layer alpha-composites at normalized canvas rect");
Check(CountDifferent(plain, styled) > 10000, "gradient, browser frame, opening transform and region pixelation render");
{
    const int deviceW = 360, deviceH = 640, deviceSourceW = 180, deviceSourceH = 320;
    var deviceSource = MakeSolid(32, 36, 220, deviceSourceW, deviceSourceH);
    var deviceScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#D8E4F0", .02, .05,
        deviceW, deviceH, .08, Canvas: new CanvasSettings { AspectWidth = 9, AspectHeight = 16, FrameScale = 1 },
        SourceWidth: deviceSourceW, SourceHeight: deviceSourceH);
    var noDevice = new byte[deviceW * deviceH * 4]; var phone = new byte[noDevice.Length]; var tablet = new byte[noDevice.Length];
    using var deviceRenderer = new GpuFrameRenderer(deviceSourceW, deviceSourceH, deviceW, deviceH);
    deviceRenderer.Render(deviceSource, deviceScene, noDevice);
    deviceRenderer.Render(deviceSource, deviceScene with { Canvas = deviceScene.Canvas! with { Frame = ScreenFrameKind.Phone } }, phone);
    deviceRenderer.Render(deviceSource, deviceScene with { Canvas = deviceScene.Canvas! with { Frame = ScreenFrameKind.Tablet } }, tablet);
    int CenterRedEdge(byte[] pixels)
    {
        for (var x = 0; x < deviceW / 2; x++)
        {
            var p = (deviceH / 2 * deviceW + x) * 4;
            if (pixels[p + 2] > 180 && pixels[p] < 80) return x;
        }
        return -1;
    }
    var noFrameEdge = CenterRedEdge(noDevice); var phoneEdge = CenterRedEdge(phone); var tabletEdge = CenterRedEdge(tablet);
    Console.WriteLine($"Device screen left edge: no frame={noFrameEdge}, phone={phoneEdge}, tablet={tabletEdge}");
    var chassisProbe = (deviceH / 2 * deviceW + Math.Max(0, noFrameEdge + 3)) * 4;
    Check(noFrameEdge >= 0 && phoneEdge > noFrameEdge && tabletEdge > phoneEdge
        && phone[chassisProbe + 2] < 160 && tablet[chassisProbe + 2] < 160,
        "phone and tablet render distinct metallic chassis and bezel widths around the screen");
    SavePngPixels(Path.Combine(AppContext.BaseDirectory, "frame-probe-phone.png"), deviceW, deviceH, phone);
    SavePngPixels(Path.Combine(AppContext.BaseDirectory, "frame-probe-tablet.png"), deviceW, deviceH, tablet);
}
renderer.Render(source, baseScene with { BackgroundStyle = new() { Kind = BackgroundKind.Pattern, Pattern = BackgroundPatternKind.Grid, PatternSpacing = 48, PatternStrength = .9, PatternColor = "#FFFFFF" } }, patterned);
renderer.Render(source, baseScene with { BackgroundStyle = new() { Kind = BackgroundKind.Dynamic, Preset = DynamicBackgroundPreset.Aurora, Speed = 1 } }, dynamicA);
renderer.Render(source, baseScene with { OutputTime = 3, BackgroundStyle = new() { Kind = BackgroundKind.Dynamic, Preset = DynamicBackgroundPreset.Aurora, Speed = 1 } }, dynamicB);
Check(CountDifferent(plain, patterned) > 1000, "normalized grid pattern changes output pixels");
Check(CountDifferent(dynamicA, dynamicB) > 100, "dynamic preset varies deterministically with output time");
var offCrop = new byte[plain.Length];
renderer.Render(source, zoomScene with { Cursor = new(.95, .95), ClickStrength = .9 }, offCrop);
Check(CountDifferent(zoomed, offCrop) == 0, "off-crop cursor and click ring are hidden");
var ringClip = new byte[plain.Length];
renderer.Render(source, baseScene with { Cursor = new(0, 0), ClickStrength = .7 }, ringClip);
var roundedCornerPoint = ((int)(outputHeight * .07 + 15) * outputWidth + (int)(outputWidth * .07 + 15)) * 4;
Check(ringClip[roundedCornerPoint] == plain[roundedCornerPoint] && ringClip[roundedCornerPoint + 1] == plain[roundedCornerPoint + 1], "click ring is clipped by rounded screen corner");
var ratio960 = CursorRelativeWidth(960, 540, sourceWidth, sourceHeight);
var ratio1080 = CursorRelativeWidth(1920, 1080, sourceWidth, sourceHeight);
Check(Math.Abs(ratio960 - ratio1080) < .01, "cursor width scales with canvas short edge");
Throws<ArgumentException>(() => renderer.Render(source, baseScene with { BlurRegions = Enumerable.Range(0, 5).Select(_ => new FrameBlurScene(new(.1, .1, .1, .1), .5, BlurKind.Blur, 0, 1)).ToArray() }, plain), "more than four blur regions are explicitly rejected");
Throws<ArgumentException>(() => renderer.Render(source, baseScene, plain, [.. Enumerable.Range(0, 9).Select(i => new FrameLayerInput(1, 1, [255, 255, 255, 255], new(.1, .1, .1, .1), Layer: i))]), "more than eight foreground layers are explicitly rejected");
Throws<ArgumentException>(() => renderer.Render(source, baseScene, plain, [new(1, 1, [0, 0, 0, 255], NormalizedRect.Full, BehindScreen: true), new(1, 1, [0, 0, 0, 255], NormalizedRect.Full, BehindScreen: true)]), "multiple background image layers are explicitly rejected");
var circular = new byte[plain.Length];
renderer.Render(source, baseScene, circular, [new(1, 1, [0, 0, 255, 255], new(.1, .1, .16875, .3), Shape: CameraShape.Circle)]);
var circleCenter = ((int)(outputHeight * .25) * outputWidth + (int)(outputWidth * (.1 + .16875 / 2))) * 4;
var circleCorner = ((int)(outputHeight * .105) * outputWidth + (int)(outputWidth * .105)) * 4;
Check(circular[circleCenter + 2] > 220 && circular[circleCorner + 2] == plain[circleCorner + 2], "camera circle mask clips to a pixel-circular inset on a 16:9 canvas");
var borderedCircle = new byte[plain.Length];
renderer.Render(source, baseScene, borderedCircle, [new(1, 1, [255, 0, 0, 255], new(.1, .1, .16875, .3), Shape: CameraShape.Circle, BorderWidth: 12, BorderColor: "#FFCC00")]);
var borderProbe = ((int)(outputHeight * .25) * outputWidth + (int)(outputWidth * .101)) * 4;
Check(borderedCircle[borderProbe + 2] > 220 && borderedCircle[borderProbe + 1] > 150 && borderedCircle[borderProbe] < 60,
    "camera border width and color are composited on the GPU in canvas pixel units");

var watch = Stopwatch.StartNew();
const int frames = 40;
for (var i = 0; i < frames; i++) renderer.Render(source, baseScene with { OutputTime = i / 30d }, plain);
watch.Stop();
var pngPath = Path.Combine(AppContext.BaseDirectory, "frame-probe.png");
SavePngPixels(pngPath, outputWidth, outputHeight, cursor);
Console.WriteLine($"Backend: {renderer.Backend}");
Console.WriteLine($"Average render plus readback: {watch.Elapsed.TotalMilliseconds / frames:F2} ms/frame ({frames} frames)");
Console.WriteLine($"Probe PNG: {pngPath}");
var perspectiveTitlePath = Path.Combine(AppContext.BaseDirectory, "frame-probe-perspective-title.png");
SavePngPixels(perspectiveTitlePath, outputWidth, outputHeight, titleTilted);
Console.WriteLine($"Perspective title probe PNG: {perspectiveTitlePath}");
SavePngPixels(Path.Combine(AppContext.BaseDirectory, "frame-probe-title-flat.png"), outputWidth, outputHeight, titleFlat);
var referenceWindow = baseScene with { Canvas = new() { Frame = ScreenFrameKind.WindowLight, Title = "海岸漫游" }, SourceWidth = 1920, SourceHeight = 1080 };
var referenceWindowBlank = referenceWindow with { Canvas = new() { Frame = ScreenFrameKind.WindowLight } };
var referenceWindowPixels = new byte[plain.Length]; var referenceWindowBlankPixels = new byte[plain.Length];
using (var windowLoader = new SceneAssetLoader(Path.Combine(Path.GetTempPath(), "frametrail-reference-window.frametrail"), outputWidth, outputHeight))
{
    renderer.Render(source, referenceWindow, referenceWindowPixels, await windowLoader.LoadAsync(referenceWindow));
    renderer.Render(source, referenceWindowBlank, referenceWindowBlankPixels);
}
var referenceHeaderBounds = DifferenceBounds(referenceWindowBlankPixels, referenceWindowPixels, outputWidth, outputHeight);
var redDotProbe = ((int)(outputHeight * .097) * outputWidth + (int)(outputWidth * .111)) * 4;
Console.WriteLine($"Reference header difference bounds: {referenceHeaderBounds.Left},{referenceHeaderBounds.Top}–{referenceHeaderBounds.Right},{referenceHeaderBounds.Bottom}; red control BGRA={referenceWindowPixels[redDotProbe]},{referenceWindowPixels[redDotProbe + 1]},{referenceWindowPixels[redDotProbe + 2]}");
Check(Math.Abs((referenceHeaderBounds.Left + referenceHeaderBounds.Right) * .5 - outputWidth * .5) < 8 && referenceHeaderBounds.Left > outputWidth * .35 && referenceWindowPixels[redDotProbe + 2] > 200,
    "window title is centered in gray while colored controls remain at the left of the frame header");
var referenceWindowPath = Path.Combine(AppContext.BaseDirectory, "frame-probe-reference-window.png");
SavePngPixels(referenceWindowPath, outputWidth, outputHeight, referenceWindowPixels);
Console.WriteLine($"Reference window header probe PNG: {referenceWindowPath}");
const int visualWidth = 1920, visualHeight = 1080;
using (var visualRenderer = new GpuFrameRenderer(sourceWidth, sourceHeight, visualWidth, visualHeight))
using (var visualLoader = new SceneAssetLoader(Path.Combine(Path.GetTempPath(), "frametrail-reference-window-visual.frametrail"), visualWidth, visualHeight))
{
    var chromeSource = MakeSolid(96, 96, 96, sourceWidth, sourceHeight);
    var visualScene = referenceWindow with { CanvasWidth = visualWidth, CanvasHeight = visualHeight };
    var visualFlat = new byte[visualWidth * visualHeight * 4];
    visualRenderer.Render(chromeSource, visualScene, visualFlat, await visualLoader.LoadAsync(visualScene));
    SavePngPixels(Path.Combine(AppContext.BaseDirectory, "frame-probe-reference-window-1920.png"), visualWidth, visualHeight, visualFlat);
    var perspectiveScene = visualScene with { ScreenMotion = new() { Position = new(.5, .5), Scale = .96, RotationX = 4, RotationY = -5, RotationZ = -3, Perspective = .6 } };
    var visualPerspective = new byte[visualFlat.Length];
    visualRenderer.Render(chromeSource, perspectiveScene, visualPerspective, await visualLoader.LoadAsync(perspectiveScene));
    SavePngPixels(Path.Combine(AppContext.BaseDirectory, "frame-probe-reference-window-perspective-1920.png"), visualWidth, visualHeight, visualPerspective);
    var perspectiveColorCounts = CountWindowControlColors(visualPerspective, visualWidth, visualHeight);
    Console.WriteLine($"Window control RGB counts after perspective {perspectiveColorCounts.Red}/{perspectiveColorCounts.Yellow}/{perspectiveColorCounts.Green}");
    Check(perspectiveColorCounts.Red > 50 && perspectiveColorCounts.Yellow > 50 && perspectiveColorCounts.Green > 50,
        "all three window control dots remain visible inside the header after screen perspective and rotation");
}

static byte[] MakeCheckerboard(int width, int height)
{
    var buffer = new byte[width * height * 4];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var light = ((x / 24) + (y / 24)) % 2 == 0;
        var offset = (y * width + x) * 4;
        buffer[offset] = light ? (byte)55 : (byte)210;
        buffer[offset + 1] = light ? (byte)165 : (byte)55;
        buffer[offset + 2] = light ? (byte)230 : (byte)30;
        buffer[offset + 3] = 255;
    }
    // A pair of contrasting lines provide a stable visual landmark in the PNG.
    for (var y = 52; y < 128; y++) for (var x = 148; x < 172; x++)
    { var o = (y * width + x) * 4; buffer[o] = 10; buffer[o + 1] = 10; buffer[o + 2] = 10; }
    for (var y = 88; y < 92; y++) for (var x = 100; x < 220; x++)
    { var o = (y * width + x) * 4; buffer[o] = 255; buffer[o + 1] = 255; buffer[o + 2] = 255; }
    return buffer;
}
static byte[] MakeSolid(byte blue, byte green, byte red, int width, int height)
{
    var buffer = new byte[width * height * 4];
    for (var i = 0; i < buffer.Length; i += 4) { buffer[i] = blue; buffer[i + 1] = green; buffer[i + 2] = red; buffer[i + 3] = 255; }
    return buffer;
}
/// <summary>
/// Independent oracle for the Mac-parity card model without zoom: the cropped picture is fitted by its own aspect ratio, a screen
/// motion position is a point of interest (focus framing, recentred by 0.62 * clamp((scale - 1) / 2)), and the 3D tilt pivots on that point.
/// </summary>
static NormalizedPoint ProjectSourcePoint(FrameScene scene, NormalizedPoint sourcePoint, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
{
    var canvasCrop = scene.Canvas?.Crop ?? NormalizedRect.Full;
    var localX = (sourcePoint.X - canvasCrop.X) / canvasCrop.Width;
    var localY = (sourcePoint.Y - canvasCrop.Y) / canvasCrop.Height;
    var padding = scene.Padding;
    var availWidth = outputWidth * (1 - 2 * padding); var availHeight = outputHeight * (1 - 2 * padding);
    var croppedWidth = sourceWidth * canvasCrop.Width; var croppedHeight = sourceHeight * canvasCrop.Height;
    var fit = Math.Min(availWidth / croppedWidth, availHeight / croppedHeight);
    var motion = scene.ScreenMotion ?? new ScreenMotionState { Position = scene.Canvas?.ContentPosition ?? new(.5, .5), Scale = scene.Canvas?.ContentScale ?? 1 };
    var cardScale = motion.Scale;
    var rectWidth = croppedWidth * fit * cardScale; var rectHeight = croppedHeight * fit * cardScale;
    var recenter = Math.Clamp((cardScale - 1) / 2, 0, 1) * .62;
    var boundedX = motion.Position.X * outputWidth; var boundedY = motion.Position.Y * outputHeight;
    var centerX = boundedX + (outputWidth / 2d - boundedX) * recenter + (.5 - motion.Position.X) * rectWidth;
    var centerY = boundedY + (outputHeight / 2d - boundedY) * recenter + (.5 - motion.Position.Y) * rectHeight;
    var x = localX * 2 - 1; var y = localY * 2 - 1;
    var pivotX = motion.Position.X * 2 - 1; var pivotY = motion.Position.Y * 2 - 1;
    var rx = motion.RotationX * Math.PI / 180; var ry = motion.RotationY * Math.PI / 180;
    var sx = Math.Sin(rx); var cx = Math.Cos(rx); var sy = Math.Sin(ry); var cy = Math.Cos(ry); var p = motion.Perspective;
    // Tilt about the pivot: move the pivot to the origin, tilt, project, move it back.
    var tx = x - pivotX; var ty = y - pivotY;
    var projectedX = tx * cy + ty * p * sy * sx;
    var projectedY = ty * cx;
    var projectedZ = 1 - tx * p * sy + ty * p * cy * sx;
    var px = centerX + (projectedX / projectedZ + pivotX) * rectWidth / 2;
    var py = centerY + (projectedY / projectedZ + pivotY) * rectHeight / 2;
    var angle = motion.RotationZ * Math.PI / 180;
    var dx = px - centerX; var dy = py - centerY;
    px = centerX + Math.Cos(angle) * dx - Math.Sin(angle) * dy;
    py = centerY + Math.Sin(angle) * dx + Math.Cos(angle) * dy;
    return new(px / outputWidth, py / outputHeight);
}
/// <summary>Independent oracle for the original (LayoutModel 1, b39f01f) geometry: whole-source fit, card-centre position, in-card zoom crop.</summary>
static NormalizedPoint ProjectSourcePointLegacy(FrameScene scene, NormalizedPoint sourcePoint, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
{
    var canvasCrop = scene.Canvas?.Crop ?? NormalizedRect.Full;
    var cropX = canvasCrop.X + scene.CropOrigin.X * canvasCrop.Width;
    var cropY = canvasCrop.Y + scene.CropOrigin.Y * canvasCrop.Height;
    var cropWidth = scene.CropSize.X * canvasCrop.Width;
    var cropHeight = scene.CropSize.Y * canvasCrop.Height;
    var localX = (sourcePoint.X - cropX) / cropWidth;
    var localY = (sourcePoint.Y - cropY) / cropHeight;
    var padding = scene.Padding;
    var availWidth = outputWidth * (1 - 2 * padding); var availHeight = outputHeight * (1 - 2 * padding);
    var fit = Math.Min(availWidth / sourceWidth, availHeight / sourceHeight);
    var scale = scene.Canvas?.ContentScale ?? 1;
    scale *= scene.ScreenMotion?.Scale ?? 1;
    var rectWidth = sourceWidth * fit * scale; var rectHeight = sourceHeight * fit * scale;
    var centerX = (scene.ScreenMotion?.Position.X ?? scene.Canvas?.ContentPosition.X ?? .5) * outputWidth;
    var centerY = (scene.ScreenMotion?.Position.Y ?? scene.Canvas?.ContentPosition.Y ?? .5) * outputHeight;
    var x = localX * 2 - 1; var y = localY * 2 - 1;
    var motion = scene.ScreenMotion ?? new ScreenMotionState();
    var rx = motion.RotationX * Math.PI / 180; var ry = motion.RotationY * Math.PI / 180;
    var sx = Math.Sin(rx); var cx = Math.Cos(rx); var sy = Math.Sin(ry); var cy = Math.Cos(ry); var p = motion.Perspective;
    var projectedX = x * cy + y * p * sy * sx;
    var projectedY = y * cx;
    var projectedZ = 1 - x * p * sy + y * p * cy * sx;
    var px = centerX + projectedX / projectedZ * rectWidth / 2;
    var py = centerY + projectedY / projectedZ * rectHeight / 2;
    var angle = motion.RotationZ * Math.PI / 180;
    var dx = px - centerX; var dy = py - centerY;
    px = centerX + Math.Cos(angle) * dx - Math.Sin(angle) * dy;
    py = centerY + Math.Sin(angle) * dx + Math.Cos(angle) * dy;
    return new(px / outputWidth, py / outputHeight);
}
static int CountDifferent(byte[] a, byte[] b)
{ var count = 0; for (var i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) count++; return count; }
static double CursorRelativeWidth(int width, int height, int sourceWidth, int sourceHeight)
{
    var source = MakeCheckerboard(sourceWidth, sourceHeight); var baseline = new byte[width * height * 4]; var decorated = new byte[baseline.Length];
    var scene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .07, .055, 1920, 1080, .2);
    using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, width, height);
    renderer.Render(source, scene, baseline); renderer.Render(source, scene with { Cursor = new(.52, .5) }, decorated);
    var bounds = DifferentBounds(baseline, decorated, width, height);
    return (bounds.Right - bounds.Left + 1) / (double)Math.Min(width, height);
}
static (int Left, int Right) DifferentBounds(byte[] a, byte[] b, int width, int height)
{
    var left = width; var right = -1;
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var i = (y * width + x) * 4;
        if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2]) continue;
        left = Math.Min(left, x); right = Math.Max(right, x);
    }
    return (left, right);
}
static (int Left, int Top, int Right, int Bottom, int Width, int Height) DifferenceBounds(byte[] a, byte[] b, int width, int height)
{
    var left = width; var right = -1; var top = height; var bottom = -1;
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var i = (y * width + x) * 4;
        if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2]) continue;
        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
    }
    return (left, top, right, bottom, right < left ? 0 : right - left + 1, bottom < top ? 0 : bottom - top + 1);
}
static (int Red, int Yellow, int Green) CountWindowControlColors(byte[] bgra, int width, int height)
{
    var red = 0; var yellow = 0; var green = 0;
    for (var y = 0; y < Math.Min(height, 180); y++) for (var x = 0; x < Math.Min(width, 420); x++)
    {
        var i = (y * width + x) * 4; var b = bgra[i]; var g = bgra[i + 1]; var r = bgra[i + 2];
        if (r > 180 && g < 130 && b < 130) red++;
        if (r > 180 && g > 130 && b < 130) yellow++;
        if (g > 150 && g > r + 30 && g > b + 20) green++;
    }
    return (red, yellow, green);
}
static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException($"Probe failed: {name}"); }
static void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Probe failed: {name}");
}
static async Task RunCompositeExportProbe()
{
    var root = Path.Combine(Path.GetTempPath(), "frametrail-composite-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "source.mp4");
        var cameraPath = Path.Combine(root, "camera.mp4");
        var seekCameraPath = Path.Combine(root, "seek-camera.mp4");
        var backgroundVideoPath = Path.Combine(root, "background-video.mp4");
        var audioPath = Path.Combine(root, "mic.wav");
        var overlayPath = Path.Combine(root, "overlay.png");
        var backgroundPath = Path.Combine(root, "background.png");
        var projectPath = Path.Combine(root, "project.frametrail");
        var outputPath = Path.Combine(root, "result.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1.2", "-f", "lavfi", "-i", "sine=frequency=440:duration=1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", sourcePath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=96x64:rate=30:duration=1.2", "-f", "lavfi", "-i", "sine=frequency=660:duration=1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", cameraPath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=1.2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p", "-y", seekCameraPath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=30:duration=0.2", "-f", "lavfi", "-i", "color=c=blue:size=160x90:rate=30:duration=0.2", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", backgroundVideoPath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:duration=1.2", "-c:a", "pcm_s16le", "-y", audioPath]);
        SavePng(overlayPath, 64, 64, [0, 0, 255, 255]);
        SavePng(backgroundPath, 64, 64, [40, 120, 200, 255]);
        await File.WriteAllTextAsync(projectPath, "{}");
        using (var backgroundAssets = new SceneAssetLoader(projectPath, 160, 90))
        {
            var videoBackground = new BackgroundSettings { Kind = BackgroundKind.Video, VideoPath = Path.GetFileName(backgroundVideoPath), VideoLoop = true };
            var firstBackground = await backgroundAssets.LoadAsync(new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, 160, 90, 0, BackgroundStyle: videoBackground));
            var secondBackground = await backgroundAssets.LoadAsync(new FrameScene(.25, .25, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, 160, 90, 0, BackgroundStyle: videoBackground));
            var loopedBackground = await backgroundAssets.LoadAsync(new FrameScene(.45, .45, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, 160, 90, 0, BackgroundStyle: videoBackground));
            var nonLoopBackground = await backgroundAssets.LoadAsync(new FrameScene(.45, .45, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, 160, 90, 0,
                BackgroundStyle: new BackgroundSettings { Kind = BackgroundKind.Video, VideoPath = Path.GetFileName(backgroundVideoPath), VideoLoop = false }));
            Check(firstBackground[0].Bgra[2] > firstBackground[0].Bgra[0] * 2 && secondBackground[0].Bgra[0] > secondBackground[0].Bgra[2] * 2
                && firstBackground[0].Bgra.SequenceEqual(loopedBackground[0].Bgra)
                && nonLoopBackground[0].Bgra[0] > nonLoopBackground[0].Bgra[2] * 2,
                "background video decoder changes frames, loops at media end, clamps non-loop playback to the final frame, and seeks back to the first frame");
        }
        var project = new FrameTrailProject
        {
            MediaPath = "source.mp4", Media = new() { Width = 320, Height = 180, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1.2 }] },
            Overlays = [new() { MediaPath = "overlay.png", Start = 0, Duration = 1.2, Rect = new(.08, .08, .18, .18), EnterMotion = OverlayMotion.None, ExitMotion = OverlayMotion.None, Layer = 1 }],
            Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 1.2, TimelineStart = 0, Position = new(.78, .78), Size = .25, Muted = true, Shape = CameraShape.Circle, Crop = new(.08, .08, .84, .84) },
            AudioTracks = [new() { MediaPath = "mic.wav", SourceStart = 0, Duration = 1.2, OffsetSeconds = 0, Volume = .3 }],
            ScreenMotions = [new() { Start = 0, Duration = 1.2, EnterDuration = .3, ReturnDuration = 0, Target = new() { Position = new(.5, .5), Scale = .88, RotationX = 7, RotationY = -6, RotationZ = 2, Perspective = .45 }, Focus = new() { Shape = FocusEffectShape.Radial, Center = new(.5, .5), Size = .55, Blur = .2, Dimming = .12 } }],
            BlurClips = [new() { Start = .2, Duration = .8, Rect = new(.4, .35, .2, .25), Kind = BlurKind.Pixelate, Strength = .45 }],
            Settings = new() { CanvasWidth = 160, CanvasHeight = 90, Padding = .08, Shadow = .3, CornerRadius = .03, Opening = new() { Enabled = true, Preset = OpeningPreset.Converge, Duration = .35, Stagger = .08, IncludedElements = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays], ElementOrder = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays] }, Canvas = new() { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.BrowserDark, Title = "综合场景", Address = "example.test" }, BackgroundStyle = new() { Kind = BackgroundKind.Image, ImagePath = "background.png", ImageFit = BackgroundImageFit.Cover, Blur = 1 } }
        };
        var openingScene = new PreparedFrameSceneEvaluator(project).Evaluate(0);
        Check(openingScene.Opening is not null && openingScene.CameraOpening is not null && openingScene.OverlayOpening is not null
            && openingScene.Camera!.Opacity == 0 && openingScene.Overlays!.Single().Opacity == 0, "opening sequence applies selected elements in order with an initial stagger");
        var result = await new ExportEngine().ExportAsync(project, sourcePath, outputPath, new(160, 90, 30, false, false, projectPath));
        var info = await MediaTools.ProbeAsync(outputPath);
        Check(result.FrameCount == 36 && info.HasAudio && Math.Abs(info.Duration - 1.2) < .12, "full export combines 3D/focus/blur/overlay/camera/opening/background/browser chrome and independent audio");
        var frame = await MediaTools.DecodeFrameAsync(outputPath, .6, 160, 90, CancellationToken.None);
        Check(frame.Length == 160 * 90 * 4 && frame.Where((_, i) => i % 4 == 3).All(x => x == 255), "combined export decodes to a complete opaque frame");
        using (var cameraAssets = new SceneAssetLoader(projectPath, 160, 90))
        {
            var cameraScene = new FrameScene(.5, .5, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, 160, 90, 0,
                Camera: new FrameCameraScene("camera.mp4", 1.3, new(.5, .5, .2, .2), .1, 0, 1, false, 1, true));
            try { _ = await cameraAssets.LoadAsync(cameraScene); throw new InvalidOperationException("Probe failed: out-of-range camera source time was accepted"); }
            catch (InvalidDataException ex) when (ex.Message.Contains("摄像头素材时间越界", StringComparison.Ordinal))
            { Check(true, "camera source time beyond the media asset duration fails with an actionable range error"); }
            var seekScene = cameraScene with { Camera = cameraScene.Camera! with { MediaPath = Path.GetFileName(seekCameraPath), SourceTime = 0 } };
            var firstCameraFrame = seekScene;
            await cameraAssets.LoadAsync(firstCameraFrame);
            var forwardCameraFrame = await cameraAssets.LoadAsync(seekScene with { Camera = seekScene.Camera! with { SourceTime = .9 } });
            var recoveredCameraFrame = await cameraAssets.LoadAsync(firstCameraFrame with { Camera = firstCameraFrame.Camera! with { SourceTime = .1 } });
            Check(forwardCameraFrame.Count == 1 && forwardCameraFrame[0].Bgra.Length > 0
                && recoveredCameraFrame.Count == 1 && recoveredCameraFrame[0].Bgra.Length > 0,
                "camera decoder handles a forward seek and a backward seek in the same asset loader");
        }
        var silentSourcePath = Path.Combine(root, "silent.mp4"); var audioOnlyPath = Path.Combine(root, "audio-only.m4a");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:size=160x90:rate=30:duration=1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", silentSourcePath]);
        var audioOnlyProject = new FrameTrailProject
        {
            MediaPath = "silent.mp4", Media = new() { Width = 160, Height = 90, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1.2 }] },
            Camera = new() { MediaPath = "camera.mp4", SourceStart = 0, Duration = 1.2, TimelineStart = 0, Muted = false, Volume = .75,
                SyncSegments = [new(0, 0, .4), new(.4, .8, .4)] }
        };
        var audioResult = await new ExportEngine().ExportAsync(audioOnlyProject, silentSourcePath, audioOnlyPath, new(160, 90, 30, false, true, projectPath));
        var audioInfo = await MediaTools.ProbeAsync(audioOnlyPath);
        Check(audioResult.FrameCount == 0 && audioInfo.HasAudio && audioInfo.Width == 0 && Math.Abs(audioInfo.Duration - 1.2) < .12, "AudioOnly exports camera audio through disjoint sync segments when the screen source has no audio");
        await RunRangeExportProbe(root, projectPath);
        var previewProject = new FrameTrailProject
        {
            MediaPath = "source.mp4", Media = new() { Width = 320, Height = 180, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceStart = .2, SourceDuration = .2 }, new() { SourceStart = .9, SourceDuration = .2, PlaybackRate = 2 }] },
            Settings = new() { CanvasWidth = 160, CanvasHeight = 90, BackgroundStyle = new() { Kind = BackgroundKind.Dynamic, Preset = DynamicBackgroundPreset.Ocean } }
        };
        await using (var preview = await PreviewPlaybackSession.CreateAsync(previewProject, sourcePath, projectPath, 160, 90, 30))
        {
            var previewBuffer = new byte[160 * 90 * 4];
            var first = await preview.ReadFrameAsync(0, previewBuffer);
            var cut = await preview.ReadFrameAsync(.2, previewBuffer);
            Check(Math.Abs(first.SourceTime - .2) < .001 && Math.Abs(cut.SourceTime - .9) < .001, "persistent preview maps output time through a source discontinuity at the cut");
            await preview.SeekAsync(.1);
            var sought = await preview.ReadFrameAsync(.1, previewBuffer);
            Check(Math.Abs(sought.SourceTime - .3) < .001 && previewBuffer.Where((_, i) => i % 4 == 3).All(x => x == 255), "preview seek restarts the decoder and returns a composed frame");
        }
        var videoBackgroundProject = new FrameTrailProject
        {
            MediaPath = "source.mp4", Media = new() { Width = 320, Height = 180, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1.2 }] },
            Settings = new() { CanvasWidth = 160, CanvasHeight = 90, Padding = .2, BackgroundStyle = new() { Kind = BackgroundKind.Video, VideoPath = Path.GetFileName(backgroundVideoPath), VideoLoop = true } }
        };
        var videoBackgroundOutput = Path.Combine(root, "video-background-export.mp4");
        await new ExportEngine().ExportAsync(videoBackgroundProject, sourcePath, videoBackgroundOutput, new(160, 90, 30, false, false, projectPath));
        await using (var backgroundPreview = await PreviewPlaybackSession.CreateAsync(videoBackgroundProject, sourcePath, projectPath, 160, 90, 30))
        {
            var previewBuffer = new byte[160 * 90 * 4];
            foreach (var time in new[] { .05, .25, .45, .65 })
            {
                await backgroundPreview.ReadFrameAsync(time, previewBuffer);
                var exported = await MediaTools.DecodeFrameAsync(videoBackgroundOutput, time, 160, 90, CancellationToken.None);
                var pixel = (2 * 160 + 2) * 4;
                Check(Math.Abs(previewBuffer[pixel] - exported[pixel]) < 24 && Math.Abs(previewBuffer[pixel + 1] - exported[pixel + 1]) < 24
                    && Math.Abs(previewBuffer[pixel + 2] - exported[pixel + 2]) < 24,
                    $"preview and export use the same looped video-background frame at {time:F2}s");
            }
        }
        await RunPreviewStressProbe(root, projectPath, cameraPath, overlayPath);
        Console.WriteLine("Integrated export probe: 36 frames, independent audio, and all scene layers passed.");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
static async Task RunRangeExportProbe(string root, string projectPath)
{
    var source = Path.Combine(root, "range-source.mp4");
    var output = Path.Combine(root, "range-export.mp4");
    var pcm = Path.Combine(root, "range-export.f32");
    var graph = "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v];[3:a][4:a][5:a]concat=n=3:v=0:a=1[a]";
    await MediaTools.RunAsync(MediaTools.Ffmpeg,
    ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=30:duration=1",
     "-f", "lavfi", "-i", "color=c=green:size=160x90:rate=30:duration=1",
     "-f", "lavfi", "-i", "color=c=blue:size=160x90:rate=30:duration=1",
     "-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=48000",
     "-f", "lavfi", "-i", "sine=frequency=880:duration=1:sample_rate=48000",
     "-f", "lavfi", "-i", "sine=frequency=1320:duration=1:sample_rate=48000",
     "-filter_complex", graph, "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", source]);
    var project = new FrameTrailProject
    {
        MediaPath = Path.GetFileName(source), Media = new() { Width = 160, Height = 90, Duration = 3 },
        Timeline = new() { Segments = [new() { SourceStart = 2, SourceDuration = 1, PlaybackRate = 2 }, new() { SourceStart = 0, SourceDuration = 1 }] },
        Settings = new() { CanvasWidth = 16, CanvasHeight = 9, Padding = 0, Shadow = 0 }
    };
    var result = await new ExportEngine().ExportAsync(project, source, output, new(160, 90, 30, false, false, projectPath));
    var info = await MediaTools.ProbeAsync(output);
    Check(result.FrameCount == 45 && info.HasAudio && Math.Abs(info.Duration - 1.5) < .12, "range export reports ffprobe duration for reordered and retimed source ranges");
    var first = await MediaTools.DecodeFrameAsync(output, .12, 160, 90, CancellationToken.None);
    var second = await MediaTools.DecodeFrameAsync(output, .72, 160, 90, CancellationToken.None);
    var firstPixel = Pixel(first, 80, 45, 160); var secondPixel = Pixel(second, 80, 45, 160);
    Check(firstPixel.B > firstPixel.R * 2 && secondPixel.R > secondPixel.G * 2, "range export frames follow source order blue then red after trim/reorder");
    await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", output, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "-y", pcm]);
    var bytes = await File.ReadAllBytesAsync(pcm); var samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    var spedTone = TonePower(samples, 48000, .08, .38, 1320);
    var normalTone = TonePower(samples, 48000, .72, 1.35, 440);
    Check(spedTone > TonePower(samples, 48000, .08, .38, 440) * 5 && normalTone > TonePower(samples, 48000, .72, 1.35, 1320) * 5,
        "exported main audio follows the same reordered source ranges and playback-rate boundary as video");
    Console.WriteLine("Range export probe: ffprobe duration, extracted frame order, and decoded audio ranges passed.");
    await RunResolutionMatrixProbe(root, projectPath);
}
static async Task RunResolutionMatrixProbe(string root, string projectPath)
{
    var source = Path.Combine(root, "resolution-source.mp4");
    await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=0.2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p", "-an", "-y", source]);
    foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440), (3840, 2160) })
    foreach (var fps in new[] { 30, 60 })
    {
        var output = Path.Combine(root, $"resolution-{width}x{height}-{fps}.mp4");
        var project = new FrameTrailProject
        {
            MediaPath = Path.GetFileName(source), Media = new() { Width = 1920, Height = 1080, Duration = .2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = .2 }] },
            Settings = new() { CanvasWidth = 16, CanvasHeight = 9, Padding = 0, Shadow = 0 }
        };
        var result = await new ExportEngine().ExportAsync(project, source, output, new(width, height, fps, false, false, projectPath));
        var info = await MediaTools.ProbeAsync(output);
        Check(result.FrameCount == (int)(.2 * fps) && info.Width == width && info.Height == height && Math.Abs(info.FrameRate - fps) < .01 && Math.Abs(info.Duration - .2) < .1,
            $"real H.264 export and ffprobe verify {width}x{height} at {fps}fps");
    }
    Console.WriteLine("Resolution matrix probe: source-size, 1440p and 4K exports at 30/60fps passed.");
}
static (int R, int G, int B) Pixel(byte[] bgra, int x, int y, int width)
{
    var i = (y * width + x) * 4;
    return (bgra[i + 2], bgra[i + 1], bgra[i]);
}
static double TonePower(float[] samples, int sampleRate, double start, double end, double frequency)
{
    var first = (int)(start * sampleRate); var count = Math.Min((int)((end - start) * sampleRate), samples.Length - first);
    if (count <= 0) return 0;
    var omega = 2 * Math.PI * frequency / sampleRate; var coefficient = 2 * Math.Cos(omega); double q0, q1 = 0, q2 = 0;
    for (var i = 0; i < count; i++) { q0 = coefficient * q1 - q2 + samples[first + i]; q2 = q1; q1 = q0; }
    return q1 * q1 + q2 * q2 - coefficient * q1 * q2;
}
static async Task RunPreviewStressProbe(string root, string projectPath, string cameraPath, string overlayPath)
{
    var staticPath = Path.Combine(root, "static-1080.mp4");
    var dynamicPath = Path.Combine(root, "testsrc2-1080.mp4");
    foreach (var (path, source) in new[] { (staticPath, "color=c=0x203050:size=1920x1080:rate=30:duration=6"), (dynamicPath, "testsrc2=size=1920x1080:rate=30:duration=6") })
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", source, "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p", "-y", path]);
    var project = new FrameTrailProject
    {
        MediaPath = Path.GetFileName(staticPath), Media = new() { Width = 1920, Height = 1080, Duration = 6 },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 6 }] },
        Settings = new() { CanvasWidth = 960, CanvasHeight = 540, BackgroundStyle = new() { Kind = BackgroundKind.Pattern, Pattern = BackgroundPatternKind.Dots, PatternStrength = .35 },
            Canvas = new() { AspectWidth = 16, AspectHeight = 9, Frame = ScreenFrameKind.BrowserDark, Title = "实时预览压力测试", Address = "preview.test" },
            Opening = new() { Enabled = true, Preset = OpeningPreset.RotateIn, Duration = .5 }, Cursor = new() { Visible = true } },
        PointerEvents = [new(0, new(.35, .4)), new(2, new(.65, .6))],
        ScreenMotions = [new() { Start = 0, Duration = 2.5, EnterDuration = .5, Target = new() { Position = new(.5, .5), Scale = .9, RotationX = 4, RotationY = -3, RotationZ = 1 }, Focus = new() { Blur = .15 } }],
        Overlays = [new() { MediaPath = Path.GetFileName(overlayPath), Start = 0, Duration = 5, Rect = new(.08, .08, .12, .12), EnterMotion = OverlayMotion.None, ExitMotion = OverlayMotion.None }],
        Camera = new() { MediaPath = Path.GetFileName(cameraPath), SourceStart = 0, TimelineStart = 0, Duration = 1.2, Position = new(.82, .8), Size = .22, Shape = CameraShape.Circle },
        BlurClips = [new() { Start = .3, Duration = 3, Rect = new(.35, .3, .3, .35), Strength = .25, Kind = BlurKind.Blur }]
    };
    foreach (var path in new[] { staticPath, dynamicPath })
    {
        project.MediaPath = Path.GetFileName(path);
        await using var session = await PreviewPlaybackSession.CreateAsync(project, path, projectPath, 960, 540, 30);
        var buffer = new byte[960 * 540 * 4];
        await session.ReadFrameAsync(0, buffer);
        var pid = session.ActiveDecoderProcessId ?? throw new InvalidOperationException("Preview decoder exited before backpressure probe.");
        using var process = Process.GetProcessById(pid);
        await Task.Delay(400);
        var privateA = process.PrivateMemorySize64;
        await Task.Delay(400);
        var privateB = process.PrivateMemorySize64;
        Check(privateB < 512L * 1024 * 1024 && Math.Abs(privateB - privateA) < 64L * 1024 * 1024, $"preview decoder backpressure bounds private memory ({Path.GetFileName(path)}: {privateA / 1048576}→{privateB / 1048576} MiB)");
        Console.WriteLine($"Preview decoder private memory ({Path.GetFileName(path)}): {privateA / 1048576}→{privateB / 1048576} MiB while stdout is backpressured");

        var times = new List<double>();
        for (var i = 1; i <= 60; i++)
        {
            var watch = Stopwatch.StartNew();
            await session.ReadFrameAsync(i / 30d, buffer);
            watch.Stop();
            if (i > 5) times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var p95 = times[(int)Math.Ceiling(times.Count * .95) - 1];
        Console.WriteLine($"Preview 1080p→960 ({Path.GetFileName(path)}): mean {times.Average():F2} ms, p95 {p95:F2} ms, 30fps budget 33.33 ms");
        Check(p95 <= 1000d / 30, $"preview throughput p95 stays within the 30fps budget on {Path.GetFileName(path)}");

        var oldPid = session.ActiveDecoderProcessId;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try { await session.ReadFrameAsync(4.5, buffer, cancel.Token); throw new InvalidOperationException("preview cancellation was not observed"); }
        catch (OperationCanceledException) { }
        Check(session.ActiveDecoderProcessId is null, "cancelled preview seek releases its decoder process");
        if (oldPid is { } id)
        {
            try { using var old = Process.GetProcessById(id); Check(old.HasExited, "cancelled preview decoder has exited"); }
            catch (ArgumentException) { }
        }
        var recovered = await session.ReadFrameAsync(4.5, buffer);
        Check(Math.Abs(recovered.SourceTime - 4.5) < .001, "preview decoder recovers on the next seek after cancellation");
    }
}
static void SavePng(string path, int width, int height, byte[] color)
{
    var pixels = new byte[width * height * 4];
    for (var i = 0; i < pixels.Length; i += 4) color.CopyTo(pixels, i);
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path); encoder.Save(stream);
}
static void SavePngPixels(string path, int width, int height, byte[] pixels)
{
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path); encoder.Save(stream);
}
static string FindWorkspaceFile(string relativePath)
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
    }
    throw new FileNotFoundException($"Cannot find {relativePath} from current probe directory.");
}
static async Task<double> MeasureAudioRms(string path, double start, double duration)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(MediaTools.Ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
    foreach (var argument in new[] { "-v", "error", "-ss", MediaTools.Number(start), "-i", path, "-t", MediaTools.Number(duration), "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) throw new InvalidOperationException("FFmpeg could not start for the audio timing probe.");
    using var pcm = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(pcm);
    var errors = process.StandardError.ReadToEndAsync();
    await copy; await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException("FFmpeg audio timing probe failed: " + await errors);
    var bytes = pcm.ToArray();
    if (bytes.Length < sizeof(float) * 1000) throw new InvalidDataException("FFmpeg returned too few audio samples for the timing probe.");
    double sum = 0;
    for (var offset = 0; offset + sizeof(float) <= bytes.Length; offset += sizeof(float))
    {
        var sample = BitConverter.ToSingle(bytes, offset);
        sum += sample * sample;
    }
    return Math.Sqrt(sum / (bytes.Length / sizeof(float)));
}
static async Task RunRealPausedProjectExport(string projectFile)
{
    projectFile = Path.GetFullPath(projectFile);
    var project = FrameTrailProjectStore.Load(projectFile);
    var source = FrameTrailProjectStore.ResolveMediaPath(projectFile, project);
    var outputDir = Path.Combine(Path.GetTempPath(), "frametrail-real-paused-export-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDir);
    try
    {
        var engine = new ExportEngine();
        var audioPath = Path.Combine(outputDir, "recovered-audio.m4a");
        var audioResult = await engine.ExportAsync(project, source, audioPath, new(1920, 1080, 30, true, true, projectFile));
        var audioInfo = await MediaTools.ProbeAsync(audioPath);
        Check(audioInfo.HasAudio && Math.Abs(audioInfo.Duration - project.Timeline.Duration) < .12, "actual paused-capture segment mix M4A validates at edited duration");
        var videoPath = Path.Combine(outputDir, "recovered-export.mp4");
        var videoResult = await engine.ExportAsync(project, source, videoPath, new(1920, 1080, 30, true, false, projectFile));
        var videoInfo = await MediaTools.ProbeAsync(videoPath);
        Check(videoInfo.HasAudio && videoInfo.Width == 1920 && videoInfo.Height == 1080
            && Math.Abs(videoInfo.Duration - project.Timeline.Duration) < .12,
            "actual paused-capture reordered MP4 validates with the same independently paused audio segments");
        Console.WriteLine($"Actual paused capture: audio {audioInfo.Duration:F3}s ({audioResult.Encoder}), video {videoInfo.Width}x{videoInfo.Height} {videoInfo.Duration:F3}s ({videoResult.Encoder}/{videoResult.Renderer})");
    }
    finally { try { Directory.Delete(outputDir, true); } catch { } }
}
