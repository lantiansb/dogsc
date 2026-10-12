using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void BuildMockupInspector()
    {
        var canvas = project!.Settings.Canvas ?? new CanvasSettings();
        Note("选择样机；内容圆角、描边与阴影在“布局”中调整。");
        Section("样机");
        ChoiceCards("", canvas.Frame, Enum.GetValues<ScreenFrameKind>().Select(value =>
            (value, value switch
            {
                ScreenFrameKind.None => "无", ScreenFrameKind.WindowLight => "浅色窗口",
                ScreenFrameKind.WindowDark => "深色窗口", ScreenFrameKind.BrowserLight => "浅色浏览器",
                ScreenFrameKind.BrowserDark => "深色浏览器", ScreenFrameKind.Phone => "手机", _ => "平板"
            }, "window")), value => InspectorEdit(p =>
            {
                var current = CanvasSettingsFor(p);
                // ChoiceCards ignores re-selecting the current mockup (Mac ScreenFrameEditorCommand.select guard),
                // so a custom size is only reset when the mockup really changes.
                if (current.Frame == value) return;
                current.Frame = value;
                ResetMockupDimensions(current);
            }, "选择样机"),
            columns: 2, preview: BuildMockupPreview);
        if (canvas.Frame == ScreenFrameKind.None) return;
        Number("框体比例", canvas.FrameScale, .6, 1.6, value => InspectorEdit(p => CanvasSettingsFor(p).FrameScale = value), NumFormat.Multiplier, log: false, undo: "调整样机比例");
        var desktop = canvas.Frame is ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark or ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark;
        if (desktop)
            Number("顶栏高度", canvas.FrameToolbarScale, .65, 1.6, value => InspectorEdit(p => CanvasSettingsFor(p).FrameToolbarScale = value), NumFormat.Multiplier, log: false, undo: "调整样机顶栏高度");
        var radius = canvas.FrameOuterRadius ?? (canvas.Frame switch
        {
            ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => 22,
            ScreenFrameKind.Phone => 1080 * .11,
            ScreenFrameKind.Tablet => 1080 * .065,
            _ => 18
        });
        Number("框体外圆角", radius, 0, 160, value => InspectorEdit(p => CanvasSettingsFor(p).FrameOuterRadius = value), NumFormat.Pixels, undo: "调整样机圆角");
        if (desktop)
            InspectorDisclosure("顶栏文字", () =>
            {
                // Mac clamps the title to 120 and the address to 240 characters.
                TextField("窗口标题", canvas.Title, value => InspectorEdit(p => CanvasSettingsFor(p).Title = value, "修改窗口标题"), 120);
                if (canvas.Frame is ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark)
                    TextField("网址", canvas.Address, value => InspectorEdit(p => CanvasSettingsFor(p).Address = value, "修改网址"), 240);
            });
        ActionButton("恢复样机默认尺寸", () => InspectorEdit(p =>
        {
            var current = CanvasSettingsFor(p);
            ResetMockupDimensions(current);
        }));
    }

    private static void ResetMockupDimensions(CanvasSettings canvas)
    {
        canvas.FrameScale = canvas.Frame == ScreenFrameKind.Phone ? .9 : 1;
        // This is a multiplier of the renderer's per-style header height.
        canvas.FrameToolbarScale = 1;
        canvas.FrameOuterRadius = null;
    }

    private FrameworkElement BuildMockupPreview(ScreenFrameKind kind)
    {
        var preview = new Grid { Width = 112, Height = 66 };
        var phone = kind == ScreenFrameKind.Phone;
        var tablet = kind == ScreenFrameKind.Tablet;
        var dark = kind is ScreenFrameKind.WindowDark or ScreenFrameKind.BrowserDark || phone || tablet;
        var desktop = kind != ScreenFrameKind.None && !phone && !tablet;
        var frame = new Border
        {
            Width = phone ? 34 : tablet ? 78 : 108, Height = phone ? 62 : 62,
            Background = dark ? new SolidColorBrush(Color.FromRgb(39, 42, 46)) : Brush("Chrome"),
            BorderBrush = dark ? Brush("Ink") : Brush("Hairline"), BorderThickness = new(1),
            CornerRadius = new(phone ? 8 : tablet ? 6 : 5),
            Padding = new(phone || tablet ? 4 : 2), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var interior = new Grid();
        var image = new Border
        {
            CornerRadius = new(phone || tablet ? 4 : 2),
            Background = new LinearGradientBrush(Color.FromRgb(128, 185, 170), Color.FromRgb(195, 221, 228), 35),
            Margin = new(0, desktop ? 9 : 0, 0, 0)
        };
        interior.Children.Add(image);
        if (desktop)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new(3, 2, 0, 0) };
            foreach (var color in new[] { Colors.IndianRed, Colors.Goldenrod, Colors.SeaGreen })
                dots.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = new SolidColorBrush(color), Margin = new(1, 0, 1, 0) });
            interior.Children.Add(dots);
        }
        if (phone)
            interior.Children.Add(new Border { Width = 12, Height = 3, CornerRadius = new(2), Background = Brush("Ink"), VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 0, 0) });
        frame.Child = interior; preview.Children.Add(frame); return preview;
    }
}
