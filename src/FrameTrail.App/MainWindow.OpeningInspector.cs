using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void BuildOpeningInspector()
    {
        var opening = project!.Settings.Opening ?? NewOpeningSettings();
        Section("全局开场");
        Note("让所有元素有序进入画面。");
        var enabled = new CheckBox
        {
            Content = "启用全局开场",
            IsChecked = opening.Enabled,
            Style = (Style)FindResource("InspectorToggle"),
            Margin = new Thickness(0, 4, 0, 10)
        };
        AutomationProperties.SetName(enabled, "启用全局开场");
        RoutedEventHandler enabledChanged = (_, _) =>
        {
                if (!buildingInspector) CommitInspectorAction("切换全局开场", () => InspectorEdit(p => (p.Settings.Opening ??= NewOpeningSettings()).Enabled = enabled.IsChecked == true));
        };
        enabled.Checked += enabledChanged;
        enabled.Unchecked += enabledChanged;
        InspectorHost.Children.Add(enabled);

        if (!opening.Enabled) return;

        var presets = new[]
        {
            (OpeningPreset.Converge, "聚拢", "merge"),
            (OpeningPreset.SideSlide, "侧滑", "arrow-right"),
            (OpeningPreset.Light3D, "轻 3D", "cube")
        };
        ChoiceCards("开场方式", opening.Preset, presets,
            value => InspectorEdit(p =>
            {
                var settings = p.Settings.Opening ??= NewOpeningSettings();
                settings.Preset = value;
                settings.ElementCurve ??= ElementMotionCurve.Swift;
            }, "选择开场方式"),
            columns: 3, preview: BuildOpeningPresetPreview);

        ChoiceCards("动效节奏", opening.ElementCurve ?? (ElementMotionCurve)(-1),
            new[] { (ElementMotionCurve.Smooth, "顺滑", "wave"), (ElementMotionCurve.Swift, "利落", "sparkles"), (ElementMotionCurve.Gentle, "柔和", "motion") },
            value => InspectorEdit(p => (p.Settings.Opening ??= NewOpeningSettings()).ElementCurve = value, "调整开场动效节奏"));
        Note(opening.ElementCurve switch
        {
            ElementMotionCurve.Smooth => "均衡加减速，适合大多数元素。",
            ElementMotionCurve.Swift => "快速进入并干净收尾，节奏更明确。",
            ElementMotionCurve.Gentle => "起落更慢，适合较大的画面和样机。",
            _ => "此工程沿用自定开场曲线；选择节奏后使用该节奏。"
        });
        Number("总时长", opening.Duration, .4, 8,
            value => InspectorEdit(p =>
            {
                var settings = p.Settings.Opening ??= new();
                settings.Duration = value;
                // Mac OpeningSequence.normalizeTiming: a shorter opening shrinks the stagger so no element is cut off.
                NormalizeOpeningTiming(settings);
            }), NumFormat.Seconds, log: false, undo: "调整开场时长");
        if (opening.IncludedElements.Count > 1)
            Number("元素间隔", opening.Stagger, 0, MaximumOpeningStagger(opening.Duration, opening.IncludedElements.Count),
                value => InspectorEdit(p => (p.Settings.Opening ??= new()).Stagger = value), NumFormat.Seconds, log: false, undo: "调整开场元素间隔");
        else Note("只有一个元素参与时无需设置间隔。");

        InspectorDisclosure("参与元素与顺序", () =>
        {
            var order = opening.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct().ToArray();
            foreach (var element in order)
            {
                var item = element;
                var label = OpeningElementLabel(element);
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var toggle = new CheckBox { Content = label, IsChecked = opening.IncludedElements.Contains(element), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(toggle, label + "参与开场");
                toggle.Click += (_, _) => CommitInspectorAction("调整开场参与元素", () => InspectorEdit(p =>
                {
                    var settings = p.Settings.Opening ??= new();
                    if (toggle.IsChecked == true && !settings.IncludedElements.Contains(item)) settings.IncludedElements.Add(item);
                    else if (toggle.IsChecked != true) settings.IncludedElements.Remove(item);
                    NormalizeOpeningTiming(settings);
                }));
                row.Children.Add(toggle);
                var up = DisclosureOrderButton(label + "提前", "↑", () => MoveOpeningElement(item, -1));
                Grid.SetColumn(up, 1); row.Children.Add(up);
                var down = DisclosureOrderButton(label + "延后", "↓", () => MoveOpeningElement(item, 1));
                Grid.SetColumn(down, 2); row.Children.Add(down);
                InspectorHost.Children.Add(row);
            }
        });

        var start = opening.ResolvePreset();
        InspectorDisclosure("自定开场与起始姿态", () =>
        {
            ChoiceCards("其他开场", opening.Preset,
                new[] { (OpeningPreset.Custom, "自定", "sparkles"), (OpeningPreset.ZoomUp, "放大入场", "zoom"), (OpeningPreset.Fade, "淡入", "eye"), (OpeningPreset.SlideUp, "上升", "arrow-up"), (OpeningPreset.RotateIn, "旋转入场", "rotate") },
                value => InspectorEdit(p => { var settings = p.Settings.Opening ??= new(); settings.Preset = value; settings.ElementCurve = null; }, "选择开场方式"), 3, BuildOpeningPresetPreview);
            Choices("自定曲线", opening.Easing, Easings(), value => InspectorEdit(p => { var settings = p.Settings.Opening ??= new(); settings.Easing = value; settings.ElementCurve = null; }, "调整开场曲线"));
            MotionCurveControls(opening.Easing, opening.CustomCurve, value => InspectorEdit(p => (p.Settings.Opening ??= new()).CustomCurve = value, "调整开场曲线"));
            Number("初始大小", start.ScaleFrom, .1, 2, value => EditOpeningCustom(x => x.ScaleFrom = value), NumFormat.Multiplier, undo: "调整开场初始大小");
            Number("水平位移", start.OffsetX, -1, 1, value => EditOpeningCustom(x => x.OffsetX = value), NumFormat.Percent, log: false, undo: "调整开场水平位移");
            Number("垂直位移", start.OffsetY, -1, 1, value => EditOpeningCustom(x => x.OffsetY = value), NumFormat.Percent, log: false, undo: "调整开场垂直位移");
            Number("初始旋转", start.RotationFrom, -180, 180, value => EditOpeningCustom(x => x.RotationFrom = value), NumFormat.Degrees, log: false, undo: "调整开场初始旋转");
            Number("初始不透明度", start.OpacityFrom, 0, 1, value => EditOpeningCustom(x => x.OpacityFrom = value), NumFormat.Percent, log: false, undo: "调整开场初始不透明度");
        }, expanded: opening.Preset is not (OpeningPreset.Converge or OpeningPreset.SideSlide or OpeningPreset.Light3D));

        var previewButton = new Button
        {
            Content = "从片头预览",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
            Style = (Style)FindResource("PlainButton")
        };
        AutomationProperties.SetName(previewButton, "从片头预览");
        previewButton.Click += (_, _) => { Playhead.Value = 0; PlayClick(this, new()); };
        InspectorHost.Children.Add(previewButton);
        Note("柔化和突出从首帧保持生效；从 0 秒开始的贴图自身入场由全局开场接管。");
    }

    private static OpeningSettings NewOpeningSettings() => new()
    {
        Preset = OpeningPreset.Light3D, ElementCurve = ElementMotionCurve.Swift, Duration = 2.2,
        IncludedElements = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays]
    };

    private static string OpeningElementLabel(OpeningElement element) => element switch
    {
        OpeningElement.Screen => "屏幕",
        OpeningElement.Camera => "摄像头",
        _ => "贴图"
    };

    private Button DisclosureOrderButton(string name, string glyph, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            Style = (Style)FindResource("PlainButton")
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private static double MaximumOpeningStagger(double duration, int elementCount) =>
        elementCount > 1 ? Math.Min(1.2, Math.Max(0, duration - .18) / (elementCount - 1)) : 1.2;

    /// <summary>Mac OpeningSequence.normalizeTiming (ProjectModel.swift): clamp duration and stagger.</summary>
    private static void NormalizeOpeningTiming(OpeningSettings settings)
    {
        settings.Duration = Math.Clamp(double.IsFinite(settings.Duration) ? settings.Duration : 2.2, .4, 8);
        var maximum = MaximumOpeningStagger(settings.Duration, settings.IncludedElements.Count);
        settings.Stagger = Math.Clamp(double.IsFinite(settings.Stagger) ? settings.Stagger : .16, 0, maximum);
    }

    private void MoveOpeningElement(OpeningElement element, int direction)
    {
        CommitInspectorAction("调整开场元素顺序", () => InspectorEdit(p =>
        {
            var settings = p.Settings.Opening ??= new();
            var order = settings.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct().ToList();
            var index = order.IndexOf(element);
            var target = Math.Clamp(index + direction, 0, order.Count - 1);
            if (target == index) return;
            (order[index], order[target]) = (order[target], order[index]);
            settings.ElementOrder = order;
        }));
    }

    private FrameworkElement BuildOpeningPresetPreview(OpeningPreset preset)
    {
        var preview = new Grid { Width = 56, Height = 36 };
        preview.Children.Add(new Border
        {
            Background = Brush("Hairline"),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brush("Secondary"),
            BorderThickness = new Thickness(.5)
        });
        var content = new Border
        {
            Width = 29,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = Brush("Ink"),
            RenderTransformOrigin = new Point(.5, .5),
            Opacity = preset == OpeningPreset.Fade ? .36 : 1
        };
        var scale = preset switch { OpeningPreset.ZoomUp => .65, OpeningPreset.Converge => .62, OpeningPreset.Light3D => .8, _ => 1 };
        var angle = preset == OpeningPreset.RotateIn ? -14 : 0;
        var (x, y) = preset switch
        {
            OpeningPreset.SlideUp => (0d, 8d),
            OpeningPreset.Converge => (-9d, 7d),
            OpeningPreset.SideSlide or OpeningPreset.Light3D => (-12d, 0d),
            _ => (0d, 0d)
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(scale, scale));
        transforms.Children.Add(new RotateTransform(angle));
        if (preset == OpeningPreset.Light3D) transforms.Children.Add(new SkewTransform(-12, 3));
        transforms.Children.Add(new TranslateTransform(x, y));
        content.RenderTransform = transforms;
        preview.Children.Add(content);
        return preview;
    }

    private void EditOpeningCustom(Action<OpeningSettings> edit) => InspectorEdit(p =>
    {
        var value = (p.Settings.Opening ?? new()).ResolvePreset() with { Preset = OpeningPreset.Custom, ElementCurve = null };
        edit(value);
        p.Settings.Opening = value;
    });
}
