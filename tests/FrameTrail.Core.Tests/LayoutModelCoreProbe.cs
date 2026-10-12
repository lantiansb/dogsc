using System.Text.Json;
using FrameTrail;

/// <summary>
/// Semantic version of the screen-card geometry (FrameTrailSettings.LayoutModel): files without the field and hand-built settings mean the
/// original Windows layout (1), new projects and DogSC imports write the Mac card model (2), and the evaluator reports the model on the scene
/// together with the matching motion/zoom data (model 1 keeps b39f01f's plain interpolated screen-motion states).
/// </summary>
internal static class LayoutModelCoreProbe
{
    public static void Run()
    {
        Check(JsonSerializer.Deserialize<FrameTrailSettings>("{}")!.LayoutModel == 1, "a settings block without layoutModel means the original Windows layout (1)");
        Check(new FrameTrailSettings().LayoutModel == 1, "hand-built settings default to model 1");
        Check(EffectDefaults.NewProjectSettings(1920, 1080).LayoutModel == FrameTrailSettings.MacLayoutModel && FrameTrailSettings.MacLayoutModel == 2, "new projects use the Mac card model");
        var roundTrip = JsonSerializer.Deserialize<FrameTrailSettings>(JsonSerializer.Serialize(EffectDefaults.NewProjectSettings(1280, 720)))!;
        Check(roundTrip.LayoutModel == 2, "the layout model survives serialization");

        var legacy = Project(1);
        var mac = Project(2);
        var legacyEvaluator = new PreparedFrameSceneEvaluator(legacy); var macEvaluator = new PreparedFrameSceneEvaluator(mac);

        // Held motion: both models report the target state; only model 2 carries framing (blend) and zoom sample data.
        var legacyHold = legacyEvaluator.Evaluate(2.5); var macHold = macEvaluator.Evaluate(2.5);
        Check(legacyHold.LayoutModel == 1 && macHold.LayoutModel == 2, "scenes report the project's layout model");
        Check(legacyHold.ScreenBlend is null && legacyHold.ZoomSample is null, "model 1 scenes carry no framing blend or zoom sample");
        Check(macHold.ScreenBlend is not null && macHold.ZoomSample is not null, "model 2 scenes carry the framing blend and zoom sample");
        Near(legacyHold.ScreenMotion!.Position.X, .8, "model 1 hold keeps the authored card-centre position");
        Near(legacyHold.ScreenMotion!.Scale, 1.2, "model 1 hold keeps the authored scale");

        // Model 1 return: the pre-Mac sampler interpolates the plain state back to the default state (card centre 0.5, 0.5).
        var legacyReturn = legacyEvaluator.Evaluate(4.25);
        Near(legacyReturn.ScreenMotion!.Position.X, .65, "model 1 return interpolates the position linearly back to the centre");
        Near(legacyReturn.ScreenMotion!.RotationY, 10, "model 1 return interpolates the tilt linearly");
        Check(legacyEvaluator.Evaluate(4.6).ScreenMotion is null, "model 1 motion ends after the return window");

        // Model 1 zoom keeps the focus crop window the old renderer crops with.
        var legacyZoom = legacyEvaluator.Evaluate(1.5);
        Near(legacyZoom.Zoom, 2, "zoom scale passes through in model 1");
        Near(legacyZoom.CropSize.X, .5, "model 1 scenes keep the focus crop window");
    }

    private static FrameTrailProject Project(int layoutModel) => new()
    {
        MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 6 },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 6 }] },
        Settings = new() { AutoZoom = false, LayoutModel = layoutModel },
        ZoomClips = [new() { Start = 1, End = 2, Scale = 2, Focus = new(.5, .5), EnterDuration = 0, ExitDuration = 0, Easing = ZoomEasing.Linear }],
        ScreenMotions = [new() { Start = 2, Duration = 2, EnterDuration = 0, ReturnDuration = .5, Easing = ZoomEasing.Linear,
            Target = new() { Position = new(.8, .3), Scale = 1.2, RotationY = 20 } }]
    };

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-6)
            throw new InvalidOperationException($"Layout model probe failed: {message}; expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Layout model probe failed: " + message);
    }
}
