using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using FrameTrail.Media;

namespace FrameTrail.App;

/// <summary>
/// Sticker (overlay) and blur-region inspector panels.
/// Mac: EditorOverlayInspector.swift (stickerInspector / mosaicInspector).
/// </summary>
public partial class MainWindow
{
    /// <summary>Overlays whose width/height ratio the user explicitly unlocked ("解除比例").</summary>
    private readonly HashSet<Guid> overlayRatioUnlocked = [];
    private readonly Dictionary<string, double> overlayImageAspects = [];

    /// <summary>Position pad plus width/height sliders shared by blur regions and the camera crop.</summary>
    private void RectControls(NormalizedRect rect, Action<NormalizedRect> apply, string moveUndo = "移动区域")
    {
        PointPad("位置", new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
            v => apply(rect with { X = v.X - rect.Width / 2, Y = v.Y - rect.Height / 2 }), snap: false, undo: moveUndo);
        Number("宽度", rect.Width, .01, 1, v => apply(rect with { Width = v }), NumFormat.Percent, log: false, undo: "调整区域宽度");
        Number("高度", rect.Height, .01, 1, v => apply(rect with { Height = v }), NumFormat.Percent, log: false, undo: "调整区域高度");
    }

    /// <summary>Pixel aspect ratio (width / height) of a sticker image, or 0 when it cannot be read.</summary>
    private double OverlayImageAspect(OverlayClip clip)
    {
        if (overlayImageAspects.TryGetValue(clip.MediaPath, out var cached)) return cached;
        double aspect = 0;
        try
        {
            if (projectFile != null)
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, clip.MediaPath);
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                aspect = frame.PixelWidth / (double)Math.Max(1, frame.PixelHeight);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or ArgumentException or System.IO.FileFormatException)
        {
        }
        if (!double.IsFinite(aspect) || aspect <= 0) aspect = 0;
        overlayImageAspects[clip.MediaPath] = aspect;
        return aspect;
    }

    /// <summary>Resizes around the current centre while keeping the image's pixel aspect ratio.</summary>
    private static void ResizeOverlayKeepingRatio(FrameTrailProject project, OverlayClip target, double width, double pixelAspect)
    {
        var canvasWidth = Math.Max(1, project.Settings.CanvasWidth);
        var canvasHeight = Math.Max(1, project.Settings.CanvasHeight);
        var height = width * canvasWidth / (canvasHeight * pixelAspect);
        if (height > 1)
        {
            height = 1;
            width = height * canvasHeight * pixelAspect / canvasWidth;
        }
        var centerX = target.Rect.X + target.Rect.Width / 2;
        var centerY = target.Rect.Y + target.Rect.Height / 2;
        target.Rect = ConstrainRect(new(centerX - width / 2, centerY - height / 2, width, height));
    }

    /// <summary>Mac moveStickerLayer: re-ranks all stickers, then renumbers Layer 0...n-1.</summary>
    private static void MoveOverlayLayer(FrameTrailProject project, Guid id, int move)
    {
        var ordered = project.Overlays.Select((clip, index) => (clip, index))
            .OrderBy(x => x.clip.Layer).ThenBy(x => x.index).Select(x => x.clip).ToList();
        var from = ordered.FindIndex(x => x.Id == id);
        if (from < 0) return;
        var item = ordered[from];
        ordered.RemoveAt(from);
        var to = move switch
        {
            -2 => 0,
            -1 => Math.Max(0, from - 1),
            1 => Math.Min(ordered.Count, from + 1),
            _ => ordered.Count
        };
        ordered.Insert(to, item);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Layer = i;
    }

    private void BuildOverlayInspector()
    {
        var p = project!;
        var clip = selectedEffect is { Kind: "overlay" } selection ? p.Overlays.FirstOrDefault(x => x.Id == selection.Id) : null;
        if (clip == null)
        {
            // Mac: nothing selected means an empty state with an add button, never the last sticker.
            var atPlayhead = p.Overlays.OrderByDescending(x => x.Layer).FirstOrDefault(x => SourceNow >= x.Start && SourceNow <= x.End);
            var actions = new List<(string Label, Action Action, bool Primary)> { ("添加图片贴图…", () => AddOverlayClick(this, new()), true) };
            if (atPlayhead != null) actions.Add(("选中播放头处的贴图", () => SelectEffect("overlay", atPlayhead.Id), false));
            InspectorEmptyState("未选择贴图", "在时间线中选择一个贴图来调整它的位置、外观与出入场动画。", "trackOverlay", actions.ToArray());
            return;
        }

        var id = clip.Id;
        OverlayClip Target(FrameTrailProject x) => x.Overlays.First(a => a.Id == id);

        ActionButton("添加图片贴图…", () => AddOverlayClick(this, new()));

        Section("贴图布局");
        Note(Path.GetFileName(clip.MediaPath));
        PointPad("位置", new(clip.Rect.X + clip.Rect.Width / 2, clip.Rect.Y + clip.Rect.Height / 2),
            v => InspectorEdit(x =>
            {
                var t = Target(x);
                t.Rect = ConstrainRect(t.Rect with { X = v.X - clip.Rect.Width / 2, Y = v.Y - clip.Rect.Height / 2 });
            }), snap: false, undo: "移动贴图");

        var unlocked = overlayRatioUnlocked.Contains(id);
        if (unlocked)
        {
            Number("宽度", clip.Rect.Width, .02, 1, v => InspectorEdit(x => { var t = Target(x); t.Rect = ConstrainRect(t.Rect with { Width = v }); }),
                NumFormat.Percent, log: false, undo: "调整贴图宽度");
            Number("高度", clip.Rect.Height, .02, 1, v => InspectorEdit(x => { var t = Target(x); t.Rect = ConstrainRect(t.Rect with { Height = v }); }),
                NumFormat.Percent, log: false, undo: "调整贴图高度");
        }
        else
        {
            var canvasWidth = Math.Max(1, p.Settings.CanvasWidth);
            var canvasHeight = Math.Max(1, p.Settings.CanvasHeight);
            var pixelAspect = OverlayImageAspect(clip);
            if (pixelAspect <= 0) pixelAspect = clip.Rect.Width * canvasWidth / Math.Max(1e-6, clip.Rect.Height * canvasHeight);
            Number("大小", clip.Rect.Width, .02, 1, v => InspectorEdit(x => ResizeOverlayKeepingRatio(x, Target(x), v, pixelAspect)),
                NumFormat.Percent, log: false, undo: "调整贴图大小");
        }
        Toggle("解除比例", unlocked, v =>
        {
            if (v) overlayRatioUnlocked.Add(id); else overlayRatioUnlocked.Remove(id);
            BuildInspector();
        });
        Number("旋转", clip.Rotation, -180, 180, v => InspectorEdit(x => Target(x).Rotation = v), NumFormat.Degrees, log: false, undo: "调整贴图旋转");
        Number("透明度", clip.Opacity, 0, 1, v => InspectorEdit(x => Target(x).Opacity = v), NumFormat.Percent, log: false, undo: "调整贴图透明度");

        if (p.Overlays.Count > 1)
        {
            Section("层级");
            var ranked = p.Overlays.Select((c, index) => (c, index)).OrderBy(x => x.c.Layer).ThenBy(x => x.index).Select(x => x.c.Id).ToList();
            var rank = ranked.IndexOf(id);
            var last = ranked.Count - 1;
            LayerButtons(id, rank, last);
            Note($"当前第 {rank + 1} 层，共 {ranked.Count} 层");
        }

        OverlayStyleControls(clip);

        Section("出入场");
        Choices("动效节奏", clip.AnimationCurve is { } curve ? (int)curve : -1,
            new[] { (-1, "线性"), ((int)ElementMotionCurve.Smooth, "顺滑"), ((int)ElementMotionCurve.Swift, "利落"), ((int)ElementMotionCurve.Gentle, "柔和") },
            v => InspectorEdit(x => Target(x).AnimationCurve = v < 0 ? null : (ElementMotionCurve)v, "调整贴图动效节奏"));
        Choices("入场", clip.EnterMotion, Enum.GetValues<OverlayMotion>().Select(x => (x, MotionName(x))), v => InspectorEdit(x => Target(x).EnterMotion = v, "调整贴图入场方式"));
        Number("入场时长", clip.EnterDuration, 0, 2, v => InspectorEdit(x => Target(x).EnterDuration = v), NumFormat.Seconds, log: false, undo: "调整贴图入场时长");
        Choices("出场", clip.ExitMotion, Enum.GetValues<OverlayMotion>().Select(x => (x, MotionName(x))), v => InspectorEdit(x => Target(x).ExitMotion = v, "调整贴图出场方式"));
        Number("出场时长", clip.ExitDuration, 0, 2, v => InspectorEdit(x => Target(x).ExitDuration = v), NumFormat.Seconds, log: false, undo: "调整贴图出场时长");

        Section("片段");
        TimingRows(p, clip.Start, clip.Duration, (0, double.PositiveInfinity), (s, len) => InspectorEdit(x =>
        {
            var t = Target(x);
            t.Start = s;
            t.Duration = len;
        }));

        DeleteEffectButton("overlay", id, "删除贴图", "删除贴图");
    }

    private void LayerButtons(Guid id, int rank, int last)
    {
        var row = new UniformGrid { Columns = 4, Margin = new(-2, 4, -2, 4) };
        Button Make(string text, int move, bool disabled)
        {
            var button = new Button { Content = text, Margin = new(2), Padding = new(4, 7, 4, 7), IsEnabled = !disabled, Background = Brush("ControlSurface") };
            System.Windows.Automation.AutomationProperties.SetName(button, "贴图" + text);
            button.Click += (_, _) => CommitInspectorAction("调整贴图层级", () => InspectorEdit(x => MoveOverlayLayer(x, id, move)));
            return button;
        }
        row.Children.Add(Make("置底", -2, rank <= 0));
        row.Children.Add(Make("下移", -1, rank <= 0));
        row.Children.Add(Make("上移", 1, rank >= last));
        row.Children.Add(Make("置顶", 2, rank >= last));
        InspectorHost.Children.Add(row);
    }

    private static string MotionName(OverlayMotion x) => x switch
    {
        OverlayMotion.None => "无",
        OverlayMotion.Fade => "淡入淡出",
        OverlayMotion.Scale => "缩放",
        OverlayMotion.SlideUp => "向上",
        OverlayMotion.SlideDown => "向下",
        OverlayMotion.SlideLeft => "向左",
        OverlayMotion.SlideRight => "向右",
        OverlayMotion.SlideTopLeft => "左上",
        OverlayMotion.SlideTopRight => "右上",
        OverlayMotion.SlideBottomLeft => "左下",
        _ => "右下"
    };

    // ---- Blur region (Mac mosaicInspector) -------------------------------------------------------
    private void BuildBlurInspector()
    {
        var p = project!;
        var clip = selectedEffect is { Kind: "blur" } selection ? p.BlurClips.FirstOrDefault(x => x.Id == selection.Id) : null;
        if (clip == null)
        {
            var atPlayhead = p.BlurClips.FirstOrDefault(x => SourceNow >= x.Start && SourceNow <= x.End);
            var actions = new List<(string Label, Action Action, bool Primary)> { ("添加柔化片段", AddBlurClip, true) };
            if (atPlayhead != null) actions.Add(("选中播放头处的柔化", () => SelectEffect("blur", atPlayhead.Id), false));
            InspectorEmptyState("未选择柔化区域", "在时间线中选择一个柔化片段，或在播放头位置添加一个来遮挡画面中的局部内容。", "trackBlur", actions.ToArray());
            return;
        }

        var id = clip.Id;
        BlurClip Target(FrameTrailProject x) => x.BlurClips.First(a => a.Id == id);

        ActionButton("添加柔化片段", AddBlurClip);

        Section("柔化效果");
        Segmented("处理方式", clip.Kind, [(BlurKind.Blur, "柔化"), (BlurKind.Pixelate, "马赛克"), (BlurKind.Spotlight, "突出")],
            v => InspectorEdit(x => Target(x).Kind = v), "调整柔化处理方式");
        if (clip.Kind == BlurKind.Spotlight)
            Number("暗度", clip.SpotlightDimming, 0, .75, v => InspectorEdit(x => Target(x).SpotlightDimming = v), NumFormat.Percent, log: false, undo: "调整区域外暗度");
        else
            Number("强度", clip.Strength, 0, 1, v => InspectorEdit(x => Target(x).Strength = v), NumFormat.Percent, log: false, undo: "调整柔化强度");
        Number("圆角", clip.CornerRadius, 0, .5, v => InspectorEdit(x => Target(x).CornerRadius = v), NumFormat.Percent, log: false, undo: "调整柔化圆角");
        Toggle("随源画面缩放和倾斜", clip.SourceSpace, v => InspectorEdit(x => Target(x).SourceSpace = v, "切换随源画面变换"));

        Section("过渡");
        Segmented("过渡方式", clip.TransitionStyle is { } style ? (int)style : -1,
            [(-1, "标准"), ((int)RegionTransitionStyle.None, "无"), ((int)RegionTransitionStyle.Linear, "线性"), ((int)RegionTransitionStyle.Smooth, "平滑")],
            v => InspectorEdit(x => Target(x).TransitionStyle = v < 0 ? null : (RegionTransitionStyle)v), "调整柔化过渡方式");
        if (clip.TransitionStyle != RegionTransitionStyle.None)
        {
            Number("进入时长", clip.EnterDuration, 0, 2, v => InspectorEdit(x => Target(x).EnterDuration = v), NumFormat.Seconds, log: false, undo: "调整柔化进入时长");
            Number("退出时长", clip.ExitDuration, 0, 2, v => InspectorEdit(x => Target(x).ExitDuration = v), NumFormat.Seconds, log: false, undo: "调整柔化退出时长");
        }

        Section("位置与尺寸");
        RectControls(clip.Rect, v => InspectorEdit(x => Target(x).Rect = ConstrainRect(v)), "移动柔化区域");

        Section("片段");
        TimingRows(p, clip.Start, clip.Duration, (0, double.PositiveInfinity),
            (s, len) => InspectorEdit(x =>
            {
                var t = Target(x);
                t.Start = s;
                t.Duration = len;
            }));

        DeleteEffectButton("blur", id, "删除柔化", "删除柔化");
    }
}
