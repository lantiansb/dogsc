namespace FrameTrail.App;

public partial class MainWindow
{
    /// <summary>Mac stickerInspector: 外观 and 背景处理 sections.</summary>
    private void OverlayStyleControls(OverlayClip clip)
    {
        var id = clip.Id;
        OverlayClip Target(FrameTrailProject p) => p.Overlays.First(x => x.Id == id);

        Section("外观");
        Number("圆角", clip.CornerRadius, 0, .5, v => InspectorEdit(p => Target(p).CornerRadius = v), NumFormat.Percent, log: false, undo: "调整贴图圆角");
        Number("贴图模糊", clip.Blur, 0, 1, v => InspectorEdit(p => Target(p).Blur = v), NumFormat.Percent, log: false, undo: "调整贴图模糊");
        Number("描边", clip.BorderWidth, 0, 40, v => InspectorEdit(p => Target(p).BorderWidth = v), NumFormat.Pixels, undo: "调整贴图描边");
        if (clip.BorderWidth > 0)
            ColorField("描边颜色", clip.BorderColor, v => InspectorEdit(p => Target(p).BorderColor = v), "调整贴图描边颜色");
        Number("阴影", clip.ShadowOpacity, 0, 1, v => InspectorEdit(p => Target(p).ShadowOpacity = v), NumFormat.Percent, log: false, undo: "调整贴图阴影");
        if (clip.ShadowOpacity > 0)
        {
            Number("阴影柔化", clip.ShadowRadius, 0, 80, v => InspectorEdit(p => Target(p).ShadowRadius = v), NumFormat.Pixels, undo: "调整贴图阴影柔化");
            Number("阴影水平偏移", clip.ShadowOffsetX, -250, 250, v => InspectorEdit(p => Target(p).ShadowOffsetX = v), NumFormat.Pixels, undo: "调整贴图阴影偏移");
            Number("阴影垂直偏移", clip.ShadowOffsetY, -250, 250, v => InspectorEdit(p => Target(p).ShadowOffsetY = v), NumFormat.Pixels, undo: "调整贴图阴影偏移");
        }

        Section("背景处理");
        if (clip.HidesScreen)
            Note("仅保留画布背景与贴图：录屏画面已隐藏，录屏虚化不会再让画布背景变得雾蒙蒙。");
        else
        {
            Number("录屏虚化", clip.BackdropBlur, 0, 60, v => InspectorEdit(p => Target(p).BackdropBlur = v), NumFormat.Pixels, undo: "调整录屏虚化");
            if (clip.BackdropBlur > .01)
                Toggle("同时虚化摄像头", clip.BackdropBlurIncludesCamera, v => InspectorEdit(p => Target(p).BackdropBlurIncludesCamera = v, "切换同时虚化摄像头"));
        }
        Toggle("贴图期间隐藏录屏画面", clip.HidesScreen, v => InspectorEdit(p => Target(p).HidesScreen = v, "切换贴图期间隐藏录屏"));
        Toggle("贴图期间隐藏摄像头", clip.HidesCamera, v => InspectorEdit(p => Target(p).HidesCamera = v, "切换贴图期间隐藏摄像头"));
        Note("隐藏与虚化会跟随贴图的入场和退场曲线，不会突然切换。");
    }
}
