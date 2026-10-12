using System.Text.Json;

namespace FrameTrail.App;

/// <summary>
/// Scene-preset helpers. Mac: EditorScenePresets / EditorStylePresetStore — a preset only carries
/// look-and-feel, so applying one never touches canvas size, crop, audio or recording related
/// settings, and an applied preset is flagged "已修改" once the style drifts from it.
/// </summary>
public partial class MainWindow
{
    private (string? ProjectFile, string PresetPath, string StyleJson)? appliedPreset;

    /// <summary>Copies only style fields of <paramref name="preset"/> onto <paramref name="current"/>.</summary>
    private static FrameTrailSettings MergePresetStyle(FrameTrailSettings current, FrameTrailSettings preset)
    {
        var merged = JsonSerializer.Deserialize<FrameTrailSettings>(JsonSerializer.Serialize(current))!;
        merged.Background = preset.Background;
        merged.Padding = preset.Padding;
        merged.CornerRadius = preset.CornerRadius;
        merged.Shadow = preset.Shadow;
        merged.CursorVisible = preset.CursorVisible;
        merged.PointerSmoothing = preset.PointerSmoothing;
        merged.Opening = preset.Opening;
        merged.BackgroundStyle = preset.BackgroundStyle;
        merged.Cursor = preset.Cursor;
        if (preset.Canvas is { } style)
        {
            // Aspect, crop and the content placement belong to this project's footage, not to the look.
            var canvas = style with { };
            var existing = current.Canvas;
            canvas.AspectWidth = existing?.AspectWidth ?? current.CanvasWidth;
            canvas.AspectHeight = existing?.AspectHeight ?? current.CanvasHeight;
            canvas.Crop = existing?.Crop ?? NormalizedRect.Full;
            canvas.ContentPosition = existing?.ContentPosition ?? new(.5, .5);
            canvas.ContentScale = existing?.ContentScale ?? 1;
            merged.Canvas = canvas;
        }
        return merged;
    }

    private static string PresetStyleJson(FrameTrailSettings settings)
    {
        var canvas = settings.Canvas is { } c ? c with { AspectWidth = 0, AspectHeight = 0, Crop = NormalizedRect.Full, ContentPosition = new(.5, .5), ContentScale = 1 } : null;
        return JsonSerializer.Serialize(new object?[]
        {
            settings.Background, settings.Padding, settings.CornerRadius, settings.Shadow, settings.CursorVisible,
            settings.PointerSmoothing, settings.Opening, settings.BackgroundStyle, settings.Cursor, canvas
        });
    }

    /// <summary>Menu suffix for the preset that was applied last and has been edited since.</summary>
    private string PresetModifiedSuffix(string presetPath)
    {
        if (appliedPreset is not { } applied || project is null) return "";
        if (!string.Equals(applied.ProjectFile, projectFile, StringComparison.OrdinalIgnoreCase) || !string.Equals(applied.PresetPath, presetPath, StringComparison.OrdinalIgnoreCase)) return "";
        return PresetStyleJson(project.Settings) == applied.StyleJson ? "" : "（已修改）";
    }
}
