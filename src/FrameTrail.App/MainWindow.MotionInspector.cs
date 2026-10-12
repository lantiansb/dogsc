using System.Windows;
using System.Windows.Media.Imaging;
using FrameTrail.Media;
namespace FrameTrail.App;

/// <summary>
/// Zoom and screen-3D inspector panels plus the shared focus-blur controls.
/// Mac: EditorZoomViews.swift, MotionInspectorViews.swift, EditorFocusEffectInspector.swift.
/// </summary>
public partial class MainWindow
{
    // ---- Output-time helpers --------------------------------------------------------------------
    // Effect clips are stored in source time; the inspector reads and edits output (timeline) time.
    private static double OutputTimeOf(FrameTrailProject p, double source) =>
        p.Timeline.MapSourceToOutput(Math.Max(0, source)) ?? source;

    private static double SourceTimeOf(FrameTrailProject p, double output) =>
        p.Timeline.MapOutputToSource(Math.Clamp(output, 0, p.Timeline.Duration));

    /// <summary>
    /// "开始" and "长度" rows. Start is shown in output time. Both edits are clamped to the free span
    /// between the neighbouring clips so a number box can never push a clip over another one.
    /// </summary>
    private void TimingRows(FrameTrailProject p, double start, double length, (double Lo, double Hi) free,
        Action<double, double> set, double minLength = .1, string lengthLabel = "长度")
    {
        var mediaEnd = p.Media.Duration;
        var hi = Math.Min(free.Hi, mediaEnd);
        var lo = Math.Min(free.Lo, Math.Max(0, mediaEnd - minLength));
        var maxOutputStart = Math.Max(0, p.Timeline.Duration - minLength);
        Number("开始", OutputTimeOf(p, start), 0, maxOutputStart, v =>
        {
            // Keep the length when it still fits, otherwise shorten it to the free gap.
            var s = Math.Clamp(SourceTimeOf(p, v), lo, Math.Max(lo, hi - minLength));
            set(s, Math.Max(minLength, Math.Min(length, hi - s)));
        }, NumFormat.Seconds, log: false, undo: "调整片段开始时间");
        var maxLength = Math.Max(minLength, hi - start);
        Number(lengthLabel, Math.Min(length, maxLength), minLength, maxLength,
            v => set(start, Math.Clamp(v, minLength, maxLength)), NumFormat.Seconds, log: false, undo: "调整片段长度");
    }

    private BitmapSource? FocusBackdrop() => PreviewImage.Source as BitmapSource;

    private void BuildMotionPanel()
    {
        var p = project!;
        Section("运镜类型");
        Tabs(motionTab,
            ("zoom", "⌕  缩放", () => { motionTab = "zoom"; BuildInspector(); }),
            ("motion", "⬡  屏幕 3D", () => { motionTab = "motion"; BuildInspector(); }));
        if (motionTab == "zoom") BuildZoomPanel(p);
        else BuildScreenMotionPanel(p);
    }

    // ---- Zoom -----------------------------------------------------------------------------------
    private void BuildZoomPanel(FrameTrailProject p)
    {
        var z = selectedEffect is { Kind: "zoom" } sel ? p.ZoomClips.FirstOrDefault(x => x.Id == sel.Id) : null;
        if (z == null)
        {
            // Mac: with nothing selected the panel is an empty state, never the last/playhead clip.
            EmptyState("未选择缩放片段", "在时间线中选择一个缩放片段来调整构图与动画；录制了鼠标后也可以根据点击自动生成。", "trackZoom",
                ("添加手动缩放", AddManualZoom, true),
                ("根据点击生成自动运镜", GenerateAutomaticZoom, false));
            if (p.ZoomClips.Any(a => a.Automatic)) ActionButton("清除自动运镜", () => InspectorEdit(x => x.ZoomClips.RemoveAll(a => a.Automatic)));
            return;
        }

        var id = z.Id;
        ZoomClip Target(FrameTrailProject x) => x.ZoomClips.First(a => a.Id == id);

        ActionButton("根据点击生成自动运镜", GenerateAutomaticZoom);
        ActionButton("添加手动缩放", AddManualZoom);

        Section("构图");
        Segmented("定位方式", z.Automatic,
            [(true, "自动跟随"), (false, "手动定位")],
            auto => InspectorEdit(x =>
            {
                var t = Target(x);
                if (t.Automatic == auto) return;
                var crop = x.Settings.Canvas?.Crop ?? NormalizedRect.Full;
                // Automatic clips keep full-source coordinates, manual ones are crop-local.
                t.Focus = auto
                    ? new NormalizedPoint(crop.X + t.Focus.X * crop.Width, crop.Y + t.Focus.Y * crop.Height)
                    : ToCropLocalFocus(x, t.Focus);
                t.Automatic = auto;
            }), "切换缩放定位方式");
        if (z.Automatic)
            Note("自动跟随会按录制的鼠标位置确定焦点；切换到手动定位后可在下方图上指定。");
        else
        {
            PointPad("缩放焦点", z.Focus, v => InspectorEdit(x => { Target(x).Focus = v; Target(x).Automatic = false; }),
                centered: true, backdrop: FocusBackdrop(), undo: "调整缩放焦点");
            Toggle("点击画面调整焦点", ManualFocus.IsChecked == true, v => ManualFocus.IsChecked = v);
        }
        var scale = z.Scale;
        Number("目标大小", z.Scale, 1, 6, v => InspectorEdit(x => Target(x).Scale = v), NumFormat.Multiplier, log: true, inline: true, undo: "调整缩放倍率");
        if (p.ZoomClips.Count > 1)
            ActionButton("应用倍率到全部缩放", () => InspectorEdit(x => { foreach (var clip in x.ZoomClips) clip.Scale = scale; }, "应用倍率到全部缩放"));

        Section("片段与动画");
        var free = FreeSpan(p.ZoomClips.Where(a => a.Id != id).Select(a => (a.Start, a.End)), z.Start, z.End);
        TimingRows(p, z.Start, z.End - z.Start, free, (s, len) => InspectorEdit(x =>
        {
            var a = Target(x);
            a.Start = s;
            a.End = Math.Min(x.Media.Duration, s + len);
        }));
        Number("进入", z.EnterDuration, 0, 5, v => InspectorEdit(x => { Target(x).EnterDuration = v; Target(x).EnterProgressOffset = 0; }), NumFormat.Seconds, log: false, undo: "调整进入时长");
        Number("退出", z.ExitDuration, 0, 5, v => InspectorEdit(x => { Target(x).ExitDuration = v; Target(x).ExitProgressOffset = 0; }), NumFormat.Seconds, log: false, undo: "调整退出时长");
        Choices("曲线", z.Easing, Easings(), v => InspectorEdit(x => Target(x).Easing = v));
        MotionCurveControls(z.Easing, z.CustomCurve, v => InspectorEdit(x => Target(x).CustomCurve = v));

        FocusEffectControls(z.FocusEffect, linearOnly: false,
            x => Target(x).FocusEffect, (x, effect) => Target(x).FocusEffect = effect, backdrop: true);

        ActionButton("清除自动运镜", () => InspectorEdit(x => x.ZoomClips.RemoveAll(a => a.Automatic)));
        DeleteEffectButton("zoom", id, "删除缩放片段", "删除缩放片段");
    }

    /// <summary>
    /// "根据点击生成自动运镜": replaces only the Automatic clips and keeps the manual ones the user
    /// placed (Mac regenerates automatic animations without discarding manual edits).
    /// </summary>
    private void GenerateAutomaticZoom()
    {
        if (project == null) return;
        var scale = new[] { 1.3, 1.6, 2 }[Math.Clamp(ZoomScale.SelectedIndex, 0, 2)];
        InspectorEdit(p =>
        {
            p.Settings.AutoZoom = true;
            ReplaceAutomaticZoom(p, scale);
        }, "根据点击生成自动运镜");
    }

    private static void ReplaceAutomaticZoom(FrameTrailProject p, double? scale)
    {
        var events = p.PointerEvents.Select(e => e with { Time = p.MouseClock.ToSourceTime(e.Time) });
        var planned = scale is { } s ? AutoZoomPlanner.Plan(events, new AutoZoomOptions(Scale: s)) : AutoZoomPlanner.Plan(events);
        var manual = p.ZoomClips.Where(c => !c.Automatic).ToList();
        // Drop generated clips that would collide with a manual one (including its exit tail).
        var fresh = planned.Where(a => !manual.Any(m => a.Start < m.End + m.ExitDuration && m.Start < a.End + a.ExitDuration)).ToList();
        p.ZoomClips = manual.Concat(fresh).OrderBy(c => c.Start).ToList();
    }

    // ---- Screen 3D --------------------------------------------------------------------------------
    private void BuildScreenMotionPanel(FrameTrailProject p)
    {
        var clip = selectedEffect is { Kind: "motion" } sel ? p.ScreenMotions.FirstOrDefault(x => x.Id == sel.Id) : null;
        if (clip == null)
        {
            EmptyState("未选择屏幕 3D 片段", "在时间线中选择屏幕 3D 片段，或在播放头位置添加一个，然后调整空间姿态。", "track3d",
                ("添加屏幕 3D 片段", AddMotionClip, true));
            return;
        }

        var id = clip.Id;
        ScreenMotionClip Target(FrameTrailProject x) => x.ScreenMotions.First(a => a.Id == id);

        Section("构图");
        PointPad("缩放锚点", clip.Target.Position, v => InspectorEdit(x => Target(x).Target.Position = v), centered: true,
            hint: "放大时画面向锚点的反方向展开；缩小时会向锚点收拢，因此 1× 两侧的移动方向会自然相反。",
            backdrop: FocusBackdrop(), undo: "调整屏幕 3D 位置");
        Number("目标大小", clip.Target.Scale, .25, 4, v => InspectorEdit(x => Target(x).Target.Scale = v), NumFormat.Multiplier, log: true, inline: true, undo: "调整屏幕 3D 大小");

        Section("空间姿态");
        TiltPad("3D 倾斜", clip.Target.RotationX, clip.Target.RotationY,
            (rx, ry) => InspectorEdit(x => { Target(x).Target.RotationX = rx; Target(x).Target.RotationY = ry; }),
            hint: "上下控制 X · 左右控制 Y", undo: "调整屏幕 3D 倾斜");
        InspectorDisclosure("平面旋转与透视", () =>
        {
            Number("平面旋转", clip.Target.RotationZ, -30, 30, v => InspectorEdit(x => Target(x).Target.RotationZ = v), NumFormat.DegreesDecimal, log: false, undo: "调整屏幕平面旋转");
            Number("透视强度", clip.Target.Perspective, 0, 2, v => InspectorEdit(x => Target(x).Target.Perspective = v), NumFormat.Decimal2, log: false, undo: "调整屏幕透视");
        }, expanded: Math.Abs(clip.Target.RotationZ) > .01);

        FocusEffectControls(clip.Focus, linearOnly: true,
            x => Target(x).Focus, (x, effect) => Target(x).Focus = effect, backdrop: true);

        Section("动画时间");
        var free = FreeSpan(p.ScreenMotions.Where(a => a.Id != id).Select(a => (a.Start, a.End)), clip.Start, clip.End);
        TimingRows(p, clip.Start, clip.Duration, free, (s, len) => InspectorEdit(x =>
        {
            Target(x).Start = s;
            Target(x).Duration = len;
        }), lengthLabel: "时长");
        Number("进入", clip.EnterDuration, 0, 3, v => InspectorEdit(x => Target(x).EnterDuration = v), NumFormat.Seconds, log: false, undo: "调整进入时长");
        Number("回落", clip.ReturnDuration, 0, 3, v => InspectorEdit(x => Target(x).ReturnDuration = v), NumFormat.Seconds, log: false, undo: "调整回落时长");
        Choices("曲线", clip.Easing, Easings(), v => InspectorEdit(x => Target(x).Easing = v));
        MotionCurveControls(clip.Easing, clip.CustomCurve, v => InspectorEdit(x => Target(x).CustomCurve = v));

        DeleteEffectButton("motion", id, "删除屏幕 3D 片段", "删除屏幕 3D 片段");
    }

    // ---- Focus blur (shared) ------------------------------------------------------------------------
    /// <summary>
    /// Mac EditorFocusEffectInspector. Screen-3D clips always use the linear band (fixed target,
    /// no dimming); zoom clips are radial unless the user picks the linear shape. The position pad
    /// only appears for a Fixed target. Windows has no on-canvas band handle, so the linear band
    /// keeps its centre pad too.
    /// </summary>
    private void FocusEffectControls(FocusEffect? effect, bool linearOnly, Func<FrameTrailProject, FocusEffect?> get,
        Action<FrameTrailProject, FocusEffect?> set, bool backdrop)
    {
        var linear = linearOnly || effect?.Shape == FocusEffectShape.Linear;
        Section(linear ? "线性虚化" : "聚焦虚化");
        Toggle("启用聚焦", effect != null, enabled => InspectorEdit(x =>
        {
            if (!enabled) { set(x, null); return; }
            var created = new FocusEffect();
            if (linearOnly) NormalizeLinear(created, fresh: true);
            set(x, created);
        }, "切换聚焦"));
        if (effect == null) return;

        FocusEffect F(FrameTrailProject x) => get(x)!;
        Note(linear ? "清晰带 · 两侧渐变虚化" : "清晰焦点 · 柔化外围");
        if (!linearOnly)
            Segmented("形状", effect.Shape, [(FocusEffectShape.Radial, "径向"), (FocusEffectShape.Linear, "线性")],
                shape => InspectorEdit(x =>
                {
                    var f = F(x);
                    f.Shape = shape;
                    if (shape == FocusEffectShape.Linear) NormalizeLinear(f, fresh: false);
                }), "调整聚焦形状");

        if (linear)
            Number("方向", effect.AngleDegrees < 0 ? effect.AngleDegrees + 360 : effect.AngleDegrees, 0, 360,
                v => InspectorEdit(x => F(x).AngleDegrees = v >= 360 ? 0 : v), NumFormat.Degrees, log: false, undo: "调整聚焦方向");
        else
            Segmented("聚焦目标", effect.Target,
                [(FocusEffectTarget.Animation, "随运镜"), (FocusEffectTarget.Pointer, "随鼠标"), (FocusEffectTarget.Fixed, "固定")],
                target => InspectorEdit(x => F(x).Target = target), "调整聚焦目标");

        if (linear || effect.Target == FocusEffectTarget.Fixed)
            PointPad(linear ? "清晰带中心" : "聚焦位置", effect.Center, v => InspectorEdit(x => F(x).Center = v),
                backdrop: backdrop ? FocusBackdrop() : null, undo: "调整聚焦位置");

        Number("清晰范围", effect.Size, .1, 1, v => InspectorEdit(x => F(x).Size = v), NumFormat.Percent, log: false, undo: "调整清晰范围");
        Number("模糊程度", effect.Blur, 0, 1, v => InspectorEdit(x => F(x).Blur = v), NumFormat.Percent, log: false, undo: "调整模糊程度");
        Number("羽化范围", effect.Softness, 0, 1, v => InspectorEdit(x => F(x).Softness = v), NumFormat.Percent, log: false, undo: "调整羽化范围");
        // Mac: vignette only on the radial shape. The model validates dimming <= .75, so the UI caps there.
        if (!linear)
            Number("暗角", effect.Dimming, 0, .75, v => InspectorEdit(x => F(x).Dimming = v), NumFormat.Percent, log: false, undo: "调整暗角");
        Note("随动画进入和退出，摄像头与贴图保持清晰。");
    }

    /// <summary>Mac FocusEffect(linear:) defaults: fixed target, no dimming, band size .32.</summary>
    private static void NormalizeLinear(FocusEffect effect, bool fresh)
    {
        effect.Shape = FocusEffectShape.Linear;
        effect.Target = FocusEffectTarget.Fixed;
        effect.Dimming = 0;
        if (fresh)
        {
            effect.AngleDegrees = 0;
            effect.Size = .32;
        }
    }
}
