using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void BuildCursorInspector()
    {
        var p = project!;
        var cursor = p.Settings.Cursor ?? new() { Visible = p.Settings.CursorVisible };
        Section("光标外观");
        ChoiceCards("样式", cursor.Style,
        [
            (CursorStyle.SystemArrow, "箭头", "cursor"),
            (CursorStyle.Dot, "圆点", "dot"),
            (CursorStyle.Ring, "圆环", "ring"),
            (CursorStyle.FollowRecorded, "跟随录制", "cursor"),
            (CursorStyle.SystemHand, "手型", "hand"),
            (CursorStyle.SystemText, "文本", "text")
        ], value => InspectorEdit(x => CursorSettingsFor(x).Style = value, "调整光标样式"), columns: 3, preview: BuildCursorStylePreview);

        Toggle("显示光标", cursor.Visible, value => InspectorEdit(x =>
        {
            CursorSettingsFor(x).Visible = value;
            x.Settings.CursorVisible = value;
        }, "切换光标显示"));
        Number("大小", cursor.Scale, .25, 6, value => InspectorEdit(x => CursorSettingsFor(x).Scale = value), NumFormat.Multiplier, undo: "调整光标大小");
        Choices("移动平滑", p.Settings.PointerSmoothing,
            new[] { (PointerSmoothing.Smooth, "平滑"), (PointerSmoothing.Medium, "适中"), (PointerSmoothing.Rapid, "灵敏") },
            value => InspectorEdit(x => x.Settings.PointerSmoothing = value, "调整光标移动平滑"));
        Toggle("移动时倾斜", cursor.MotionTiltEnabled, value => InspectorEdit(x => CursorSettingsFor(x).MotionTiltEnabled = value, "切换光标倾斜"));
        if (cursor.MotionTiltEnabled)
            // Mac: five steps 200%...400% (the stored value is the multiple).
            Segmented("倾斜强度", NearestTiltStrength(cursor.MotionTiltStrength),
                new[] { (2d, "200%"), (2.5, "250%"), (3d, "300%"), (3.5, "350%"), (4d, "400%") },
                value => InspectorEdit(x => CursorSettingsFor(x).MotionTiltStrength = value), "调整光标摆动强度");
        Toggle("静止后隐藏", cursor.HideWhenIdle, value => InspectorEdit(x => CursorSettingsFor(x).HideWhenIdle = value, "切换静止隐藏"));
        if (cursor.HideWhenIdle)
            Number("静止隐藏延迟", cursor.IdleSeconds, .2, 8, value => InspectorEdit(x => CursorSettingsFor(x).IdleSeconds = value), NumFormat.Seconds, undo: "调整静止隐藏延迟");

        Section("点击反馈");
        Toggle("显示点击效果", cursor.ClickEffectsEnabled, value => InspectorEdit(x => CursorSettingsFor(x).ClickEffectsEnabled = value, "切换点击效果"));
        Choices("效果", cursor.ClickEffect,
            new[] { (ClickEffectStyle.Pulse, "脉冲"), (ClickEffectStyle.Ring, "柔光"), (ClickEffectStyle.Ripple, "涟漪"), (ClickEffectStyle.Burst, "迸发"), (ClickEffectStyle.None, "无") },
            value => InspectorEdit(x => CursorSettingsFor(x).ClickEffect = value, "调整点击动画样式"));
        if (cursor.ClickEffectsEnabled && cursor.ClickEffect != ClickEffectStyle.None)
        {
            ColorField("颜色", cursor.ClickColor, value => InspectorEdit(x => CursorSettingsFor(x).ClickColor = value), "调整点击动画颜色");
            Number("持续时间", cursor.ClickDuration, .05, 2, value => InspectorEdit(x => CursorSettingsFor(x).ClickDuration = value), NumFormat.Seconds, undo: "调整点击持续时间");
            Number("点击范围", cursor.ClickScale, .5, 2, value => InspectorEdit(x => CursorSettingsFor(x).ClickScale = value), NumFormat.Multiplier, undo: "调整点击动画大小");
            Number("点击不透明度", cursor.ClickOpacity, .1, 1, value => InspectorEdit(x => CursorSettingsFor(x).ClickOpacity = value), NumFormat.Percent, log: false, undo: "调整点击不透明度");
        }

        InspectorDisclosure("时间对齐", () =>
        {
            var offsetMs = p.MouseClock.OffsetSeconds * 1000;
            Number("光标时间偏移", offsetMs, -10000, 10000, value => SetMouseOffsetMilliseconds(value), NumFormat.Milliseconds, log: false, undo: "校准光标时间");
            NudgeRow("光标", ms => SetMouseOffsetMilliseconds(offsetMs + ms), () => SetMouseOffsetMilliseconds(0), Math.Abs(offsetMs) > .05);
            Note("负值让光标与点击提前，正值让它们延后；不改变移动手感，也不移动画面或声音。");
        });
    }

    /// <summary>Old projects may store 2 / 3 / 4 only; snap any stored value to the nearest Mac step.</summary>
    private static double NearestTiltStrength(double value) =>
        new[] { 2d, 2.5, 3d, 3.5, 4d }.MinBy(step => Math.Abs(step - value));

    /// <summary>
    /// Mac adjustCursorTiming: the pointer clock moves, and only the automatically generated zoom
    /// clips are re-planned from it; manual clips stay where the user put them.
    /// </summary>
    private void SetMouseOffsetMilliseconds(double milliseconds)
    {
        InspectorEdit(x =>
        {
            x.MouseClock = x.MouseClock with { OffsetSeconds = Math.Clamp(milliseconds / 1000, -10, 10) };
            if (x.ZoomClips.Any(clip => clip.Automatic))
                ReplaceAutomaticZoom(x, x.ZoomClips.First(clip => clip.Automatic).Scale);
        }, "校准光标时间");
    }

    private FrameworkElement BuildCursorStylePreview(CursorStyle style)
    {
        var canvas = new Canvas { Width = 44, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        if (style is CursorStyle.SystemArrow or CursorStyle.FollowRecorded)
        {
            var arrow = new Polygon
            {
                Points = new PointCollection { new(5, 3), new(5, 27), new(11, 21), new(17, 32), new(22, 29), new(16, 19), new(25, 18) },
                Fill = Brush("Ink"),
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
            canvas.Children.Add(arrow);
        }
        else if (style == CursorStyle.SystemHand)
        {
            var hand = new TextBlock { Text = "☝", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 29, Foreground = Brush("Ink"), Width = 44, TextAlignment = TextAlignment.Center };
            Canvas.SetTop(hand, -1); canvas.Children.Add(hand);
        }
        else if (style == CursorStyle.SystemText)
        {
            var beam = new Line { X1 = 22, X2 = 22, Y1 = 5, Y2 = 31, Stroke = Brush("Ink"), StrokeThickness = 2 };
            canvas.Children.Add(beam);
            canvas.Children.Add(new Line { X1 = 14, X2 = 30, Y1 = 6, Y2 = 6, Stroke = Brush("Ink"), StrokeThickness = 2 });
            canvas.Children.Add(new Line { X1 = 14, X2 = 30, Y1 = 30, Y2 = 30, Stroke = Brush("Ink"), StrokeThickness = 2 });
        }
        else if (style == CursorStyle.Dot)
        {
            var halo = new Ellipse { Width = 25, Height = 25, Fill = Brush("Selection"), Stroke = Brush("Accent"), StrokeThickness = 2 };
            Canvas.SetLeft(halo, 9.5); Canvas.SetTop(halo, 5.5); canvas.Children.Add(halo);
            var dot = new Ellipse { Width = 11, Height = 11, Fill = Brush("Ink"), Stroke = Brushes.White, StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, 16.5); Canvas.SetTop(dot, 12.5); canvas.Children.Add(dot);
        }
        else
        {
            var ring = new Ellipse { Width = 25, Height = 25, Fill = Brushes.Transparent, Stroke = Brush("Accent"), StrokeThickness = 3 };
            Canvas.SetLeft(ring, 9.5); Canvas.SetTop(ring, 5.5); canvas.Children.Add(ring);
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Brush("Ink") };
            Canvas.SetLeft(dot, 18.5); Canvas.SetTop(dot, 14.5); canvas.Children.Add(dot);
        }
        return canvas;
    }
}
