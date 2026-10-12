using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FrameTrail.App;

public partial class MainWindow
{
    private static readonly string[] PasteableImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <summary>Mac canPasteOverlayImage: the clipboard holds an image or an image file.</summary>
    private bool CanPasteOverlayImage()
    {
        if (project is null || projectFile is null || cropMode) return false;
        try
        {
            if (Clipboard.ContainsImage()) return true;
            return Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsPasteableImageFile);
        }
        catch (COMException) { return false; }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private static bool IsPasteableImageFile(string path) =>
        PasteableImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>
    /// B-29 (Mac EditorView.addPastedSticker): pasting an image (bitmap or image file) with no effect on the clipboard adds it as a
    /// sticker at the playhead. The picture is stored in the project's media folder like an imported sticker.
    /// </summary>
    private void PasteImageAsSticker()
    {
        if (project is null || projectFile is null) return;
        string relative;
        try
        {
            string? droppedFile = Clipboard.ContainsFileDropList()
                ? Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsPasteableImageFile) : null;
            if (droppedFile is not null)
            {
                relative = "media/asset-" + Guid.NewGuid().ToString("N") + Path.GetExtension(droppedFile).ToLowerInvariant();
                var target = FrameTrailProjectStore.ResolveAssetPath(projectFile, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(droppedFile, target);
            }
            else
            {
                var image = Clipboard.GetImage() ?? throw new InvalidOperationException("剪贴板中没有可用的图片。");
                relative = "media/asset-" + Guid.NewGuid().ToString("N") + ".png";
                var target = FrameTrailProjectStore.ResolveAssetPath(projectFile, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(target);
                encoder.Save(stream);
            }
        }
        catch (ExternalException) { throw new InvalidOperationException("暂时无法读取剪贴板，请稍后重试。"); }
        CreateEffectClip("overlay", Playhead.Value, null, relative);
    }
}
