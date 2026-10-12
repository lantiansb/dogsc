using System.Windows;
using System.Windows.Controls;
namespace FrameTrail.App;

// Canvas crop editor (Mac EditorViewCropAndPreset.swift beginCrop / confirmCrop / discardCrop / resetCrop and
// EditorCanvasPreview.cropEditor). Crop mode renders the whole source at its own aspect without any styling
// (Mac cropSourceProject); the CropOverlay edits a draft and "完成" commits it as one undo step.
public partial class MainWindow
{
    private bool cropMode;
    private FrameTrailProject? cropSourceProject;
    private Guid cropProjectId;

    private void CropClick(object sender, RoutedEventArgs e) => _ = Guard(() => { BeginCropMode(); return Task.CompletedTask; });

    private void BeginCropMode()
    {
        if (project is null || projectFile is null) { Status.Text = "请先打开项目。"; return; }
        if (cropMode) return;
        if (operationCancellation != null) throw new InvalidOperationException("媒体处理进行中，请稍后再裁切。");
        if (project.Media.Width < 2 || project.Media.Height < 2) throw new InvalidOperationException("素材尺寸未知，无法裁切。");
        CancelCanvasGesture();
        if (LiveEditActive) CancelLiveEdit();
        StopPlayback();
        cropSourceProject = BuildCropSourceProject(project);
        cropProjectId = project.ProjectId;
        cropMode = true;
        CropVisual.MinWidth01 = Math.Max(24 / project.Media.Width, .02);
        CropVisual.MinHeight01 = Math.Max(24 / project.Media.Height, .02);
        CropVisual.ImageBounds = Rect.Empty;
        CropVisual.Crop = project.Settings.Canvas?.Crop ?? NormalizedRect.Full;
        CropVisual.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Visible;
        CanvasToolbar.Visibility = Visibility.Hidden;
        UndoButton.IsEnabled = RedoButton.IsEnabled = ExportButton.IsEnabled = PlayButton.IsEnabled = false;
        SelectionVisual.Reset();
        PreviewSurface.Cursor = null;
        _ = Guard(() => RenderPreview(Playhead.Value));
    }

    /// <summary>Source-only project: full frame, no padding, zoom, overlays, camera or cursor (Mac cropSourceProject).</summary>
    private static FrameTrailProject BuildCropSourceProject(FrameTrailProject source)
    {
        var c = Clone(source);
        var width = Math.Max(2, (int)Math.Round(source.Media.Width));
        var height = Math.Max(2, (int)Math.Round(source.Media.Height));
        c.Settings.CanvasWidth = width; c.Settings.CanvasHeight = height;
        c.Settings.Padding = 0; c.Settings.CornerRadius = 0; c.Settings.Shadow = 0;
        c.Settings.AutoZoom = false; c.Settings.CursorVisible = false; c.Settings.Opening = null;
        c.Settings.Cursor = (c.Settings.Cursor ?? new CursorSettings()) with { Visible = false };
        c.Settings.Background = "#000000";
        c.Settings.BackgroundStyle = new BackgroundSettings { Color = "#000000", EndColor = "#000000" };
        c.Settings.Canvas = new CanvasSettings { AspectWidth = width, AspectHeight = height };
        c.ZoomClips.Clear(); c.ScreenMotions.Clear(); c.CameraMotions.Clear(); c.Overlays.Clear(); c.BlurClips.Clear();
        c.Camera = null;
        return c;
    }

    private void ExitCropMode(bool rerender)
    {
        if (!cropMode) return;
        cropMode = false;
        cropSourceProject = null;
        CropVisual.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        CanvasToolbar.Visibility = Visibility.Visible;
        UndoButton.IsEnabled = RedoButton.IsEnabled = ExportButton.IsEnabled = true;
        UpdatePlaybackAvailability();
        if (rerender) _ = Guard(() => RenderPreview(Playhead.Value));
    }

    private void CropClearClick(object sender, RoutedEventArgs e)
    {
        if (cropMode) CropVisual.Crop = NormalizedRect.Full;
    }

    private void CropCancelClick(object sender, RoutedEventArgs e)
    {
        if (!cropMode) return;
        ExitCropMode(rerender: true);
        inspectorTab = "frame"; frameTab = "layout";
        BuildInspector();
    }

    private void CropDoneClick(object sender, RoutedEventArgs e)
    {
        if (!cropMode) return;
        var draft = CropVisual.Crop.Clamp();
        ExitCropMode(rerender: false);
        inspectorTab = "frame"; frameTab = "layout"; selectedEffect = null;
        _ = Guard(() =>
        {
            Edit(p => CanvasSettingsFor(p).Crop = draft, "裁切画面");
            BuildInspector();
            return RenderPreview(Playhead.Value);
        });
    }

    private void UpdateCropOverlayBounds()
    {
        if (CropVisual is null) return;
        CropVisual.ImageBounds = ReferenceEquals(displayedPreviewProject, cropSourceProject) ? PreviewImageBounds() : Rect.Empty;
        CropVisual.InvalidateVisual();
    }

    /// <summary>Leaves crop mode when the project it belongs to was replaced (opened another project, closed it).</summary>
    private void EnsureCropModeValid()
    {
        if (cropMode && (project is null || project.ProjectId != cropProjectId)) ExitCropMode(rerender: false);
    }
}
