using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FrameTrail.App;

/// <summary>
/// Camera track and camera-motion panels. Mac: EditorInspectorMediaControls.swift
/// (cameraInspector / cameraInspectorControls / cameraSyncCorrectionControls) and
/// MotionInspectorViews.swift (CameraMotionTargetInspector).
/// </summary>
public partial class MainWindow
{
    private double cameraVisibleOpacity = 1;

    private void BuildCameraInspector()
    {
        var p = project!;
        if (p.Camera is not { } camera)
        {
            InspectorEmptyState("没有摄像头轨", "这个项目没有可编辑的摄像头素材。录制时可启用摄像头，也可导入独立视频。", "trackCamera",
                ("导入摄像头素材…", () => _ = Guard(ImportCamera), true));
            return;
        }

        var motion = selectedEffect is { Kind: "cameraMotion" } selection ? p.CameraMotions.FirstOrDefault(x => x.Id == selection.Id) : null;
        if (motion != null)
        {
            BuildCameraMotionInspector(motion);
            return;
        }

        Note("基础布局作用于全片；时间线动画可在指定时段覆盖它。");
        Toggle("显示摄像头", camera.Opacity > 0, visible =>
        {
            if (!visible && camera.Opacity > 0) cameraVisibleOpacity = camera.Opacity;
            InspectorEdit(x => x.Camera!.Opacity = visible ? cameraVisibleOpacity : 0, "切换摄像头显示");
        });

        if (camera.Opacity > 0)
        {
            Section("位置与形状");
            Note("直接在画布中拖动，拖右下角调整大小。");
            PointPad("摄像头位置", camera.Position, v => InspectorEdit(x => x.Camera!.Position = v), undo: "移动摄像头");
            Number("摄像头大小", camera.Size, .05, .8, v => InspectorEdit(x => x.Camera!.Size = v), NumFormat.Percent, log: false, undo: "调整摄像头大小");
            var baseLayout = camera.Layout ?? (camera.Shape == CameraShape.Circle ? CameraMotionLayoutKind.Circle : CameraMotionLayoutKind.RoundedRectangle);
            ChoiceCards("形状", baseLayout, CameraLayouts(), v => InspectorEdit(x =>
            {
                var target = x.Camera!;
                if (target.Layout is null) target.CornerRadius = Math.Clamp(2 * target.CornerRadius / Math.Max(target.Size, .001), 0, 1);
                target.Layout = v;
                target.Shape = v == CameraMotionLayoutKind.Circle ? CameraShape.Circle : CameraShape.RoundedRectangle;
            }, "调整摄像头形状"), 3, CameraLayoutPreview);
            if (baseLayout is CameraMotionLayoutKind.RoundedRectangle or CameraMotionLayoutKind.Horizontal or CameraMotionLayoutKind.Vertical or CameraMotionLayoutKind.Original)
                Number("圆角程度", camera.Layout is null ? Math.Clamp(camera.CornerRadius * 2, 0, 1) : CameraRoundness(camera), 0, 1,
                    v => InspectorEdit(x => { var target = x.Camera!; target.CornerRadius = target.Layout is null ? v / 2 : v; }),
                    NumFormat.Percent, log: false, undo: "调整摄像头圆角");

            Section("基础外观");
            Toggle("镜像摄像头", camera.Mirrored, v => InspectorEdit(x => x.Camera!.Mirrored = v, "切换摄像头镜像"));
            Number("描边", camera.BorderWidth, 0, 18, v => InspectorEdit(x => x.Camera!.BorderWidth = v), NumFormat.Pixels, log: false, undo: "调整摄像头描边");
            Number("不透明度", camera.Opacity, 0, 1, v => InspectorEdit(x => x.Camera!.Opacity = v), NumFormat.Percent, log: false, undo: "调整摄像头不透明度");
            Number("阴影", camera.ShadowStrength, 0, 1, v => InspectorEdit(x => x.Camera!.ShadowStrength = v), NumFormat.Percent, log: false, undo: "调整摄像头阴影");

            Section("播放头布局动画");
            ActionButton("在播放头添加布局动画", () => AddCameraMotion(camera));

            InspectorDisclosure("缩放联动", () =>
            {
                Number("屏幕放大时的摄像头倍率", camera.ScaleDuringZoom, .35, 1.25, v => InspectorEdit(x => x.Camera!.ScaleDuringZoom = v),
                    NumFormat.Multiplier, undo: "调整摄像头缩放跟随");
                Note("跟随屏幕缩放调整摄像头大小；1 倍保持原大小。");
            });
            InspectorDisclosure("蒙版内取景", () =>
            {
                Note("只调整素材在当前形状内的取景，不移动摄像头外框。");
                RectControls(camera.Crop, v => InspectorEdit(x => x.Camera!.Crop = ConstrainRect(v)), "调整蒙版内取景");
                ActionButton("重置取景", () => InspectorEdit(x => x.Camera!.Crop = NormalizedRect.Full, "重置蒙版内取景"));
            });
        }
        else
        {
            Note("摄像头初始隐藏；仍可在播放头添加出现动画。开启后可调整布局和外观。");
            ActionButton("在播放头添加出现动画", () => AddCameraMotion(camera), primary: true);
        }

        InspectorDisclosure("音画同步", () =>
        {
            if (camera.SyncSegments is { Count: > 1 } segments)
                Note($"录制素材含 {segments.Count} 段同步区间；暂停间隙不显示相机画面。");
            Number("时间偏移", camera.TimelineStart * 1000, -10000, 10000,
                v => InspectorEdit(x => CameraTimelineOperations.Move(x.Camera!, v / 1000 - x.Camera!.TimelineStart)),
                NumFormat.Milliseconds, log: false, undo: "校准摄像头音画同步");
            NudgeRow("摄像头", ms => InspectorEdit(x => CameraTimelineOperations.Move(x.Camera!, ms / 1000), "校准摄像头音画同步"),
                () => InspectorEdit(x => CameraTimelineOperations.Move(x.Camera!, -x.Camera!.TimelineStart), "归零摄像头音画同步"),
                Math.Abs(camera.TimelineStart) > 1e-4);
            Number("素材起点", camera.SourceStart, 0, Math.Max(.04, AvailableAssetDuration(camera.MediaPath, camera.SourceStart + camera.Duration) - .04),
                v => InspectorEdit(x => ApplyCameraSourceStart(x, v)), NumFormat.Seconds, log: false, undo: "调整摄像头素材起点");
            Note("负值让摄像头提前，正值让它延后；用于校准画面与声音。");
        });
        InspectorDisclosure("摄像头素材", () =>
        {
            Note(Path.GetFileName(camera.MediaPath));
            ActionButton("导入摄像头素材…", () => _ = Guard(ImportCamera));
        });
    }

    /// <summary>Same rules as ChangeCameraSourceStart, but as a pure project edit usable by live preview.</summary>
    private void ApplyCameraSourceStart(FrameTrailProject p, double value)
    {
        if (p.Camera is not { } camera) return;
        if (camera.SyncSegments is { Count: > 0 })
        {
            CameraTimelineOperations.SlipAssetStart(camera, value, AvailableAssetDuration(camera.MediaPath, CameraAssetEnd(camera)));
            return;
        }
        var available = AvailableAssetDuration(camera.MediaPath, camera.SourceStart + camera.Duration);
        camera.SourceStart = Math.Clamp(value, 0, Math.Max(0, available - .04));
        camera.Duration = Math.Min(camera.Duration, available - camera.SourceStart);
    }

    /// <summary>
    /// ±10 ms / ±100 ms nudge buttons plus a reset (Mac EditorNumericStepControl +
    /// "提前/延后 100 ms" buttons). Negative nudges move the item earlier.
    /// </summary>
    private void NudgeRow(string name, Action<double> nudgeMilliseconds, Action reset, bool canReset)
    {
        var row = new UniformGrid { Columns = 5, Margin = new(-2, 2, -2, 4) };
        Button Make(string text, string automation, Action action, bool enabled = true)
        {
            var button = new Button { Content = text, Margin = new(2), Padding = new(2, 7, 2, 7), FontSize = 11, Background = Brush("ControlSurface"), IsEnabled = enabled };
            System.Windows.Automation.AutomationProperties.SetName(button, automation);
            button.Click += (_, _) => CommitInspectorAction(null, action);
            return button;
        }
        row.Children.Add(Make("−100", name + "提前 100 毫秒", () => nudgeMilliseconds(-100)));
        row.Children.Add(Make("−10", name + "提前 10 毫秒", () => nudgeMilliseconds(-10)));
        row.Children.Add(Make("+10", name + "延后 10 毫秒", () => nudgeMilliseconds(10)));
        row.Children.Add(Make("+100", name + "延后 100 毫秒", () => nudgeMilliseconds(100)));
        row.Children.Add(Make("归零", "归零" + name + "时间偏移", reset, canReset));
        InspectorHost.Children.Add(row);
    }

    private void BuildCameraMotionInspector(CameraMotionClip motion)
    {
        var p = project!;
        var id = motion.Id;
        CameraMotionClip M(FrameTrailProject x) => x.CameraMotions.First(a => a.Id == id);
        var camera = p.Camera!;
        var selected = motion.TargetLayout ?? (motion.TargetRoundness is not null
            ? CameraMotionLayoutKind.RoundedRectangle
            : camera.Layout ?? (camera.Shape == CameraShape.Circle ? CameraMotionLayoutKind.Circle : CameraMotionLayoutKind.RoundedRectangle));

        ActionButton("返回摄像头初始状态", () =>
        {
            selectedEffect = null;
            TimelineView.Selection = null;
            TimelineView.MultiSelection.Clear();
            TimelineView.InvalidateVisual();
            BuildInspector();
        });

        Section("摄像头动画");
        Note("布局、形状、大小和透明度会在进入与回落过程中连续过渡。");
        ChoiceCards("目标形状", selected, CameraLayouts(), v => InspectorEdit(x => M(x).TargetLayout = v, "调整摄像运动形状"), 3, CameraLayoutPreview);
        PointPad("目标位置", motion.TargetPosition, v => InspectorEdit(x => M(x).TargetPosition = v), undo: "调整摄像运动位置");
        Number("目标大小", motion.TargetSize, .05, 1, v => InspectorEdit(x => M(x).TargetSize = v), NumFormat.Percent, log: false, undo: "调整摄像运动大小");
        if (selected is CameraMotionLayoutKind.RoundedRectangle or CameraMotionLayoutKind.Horizontal or CameraMotionLayoutKind.Vertical or CameraMotionLayoutKind.Original)
            Number("目标圆角", motion.TargetRoundness ?? CameraRoundness(camera), 0, 1, v => InspectorEdit(x => M(x).TargetRoundness = v),
                NumFormat.Percent, log: false, undo: "调整摄像运动圆角");
        Number("目标不透明度", motion.TargetOpacity ?? camera.Opacity, 0, 1, v => InspectorEdit(x => M(x).TargetOpacity = v),
            NumFormat.Percent, log: false, undo: "调整摄像运动透明度");

        Section("片段与动画");
        var free = FreeSpan(p.CameraMotions.Where(a => a.Id != id).Select(a => (a.Start, a.End)), motion.Start, motion.End);
        TimingRows(p, motion.Start, motion.Duration, free, (s, len) => InspectorEdit(x =>
        {
            var t = M(x);
            t.Start = s;
            t.Duration = len;
            t.EnterDuration = Math.Min(t.EnterDuration, len);
        }), lengthLabel: "时长");
        Number("进入", motion.EnterDuration, 0, Math.Max(.1, Math.Min(3, motion.Duration)), v => InspectorEdit(x => M(x).EnterDuration = v),
            NumFormat.Seconds, log: false, undo: "调整摄像运动进入时长");
        Number("回落", motion.ReturnDuration, 0, 3, v => InspectorEdit(x => M(x).ReturnDuration = v), NumFormat.Seconds, log: false, undo: "调整摄像运动回落时长");
        ChoiceCards("曲线", motion.Easing, Easings().Select(x => (x.Item1, x.Item2, "wave")), v => InspectorEdit(x => M(x).Easing = v, "调整摄像运动曲线"), 2);
        MotionCurveControls(motion.Easing, motion.CustomCurve, v => InspectorEdit(x => M(x).CustomCurve = v, "调整摄像运动曲线"));

        DeleteEffectButton("cameraMotion", id, "删除摄像头动画", "删除摄像头动画");
    }
}
