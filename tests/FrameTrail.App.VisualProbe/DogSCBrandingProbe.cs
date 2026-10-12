using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class DogSCBrandingProbe
{
    internal static void Verify(Application application, string output, Action<bool, string, object?> check)
    {
        var assembly = typeof(MainWindow).Assembly;
        var metadata = new
        {
            Name = assembly.GetName().Name,
            Version = assembly.GetName().Version?.ToString(),
            Title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title,
            Product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product,
            Company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company,
            Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        };
        check(metadata.Name == "DogSC" && metadata.Version == "0.2.18.0" &&
              metadata.Title == "DogSC" && metadata.Product == "DogSC" &&
              metadata.Company == "DogSC Windows社区贡献者" && metadata.Copyright?.Contains("神烦老狗", StringComparison.Ordinal) == true,
            "程序集标识与版本信息显示 DogSC 0.2.18、Windows 社区贡献者和原作者版权", metadata);

        var window = new MainWindow();
        check(window.Title == "DogSC", "主编辑器窗口显示 DogSC", window.Title);
        var logoUri = new Uri("pack://application:,,,/DogSC;component/Assets/DogSCLogo.png");
        var logo = Application.GetResourceStream(logoUri);
        check(logo is not null, "About 使用的 DogSC 原仓库图标资源已进入 WPF pack URI", logoUri.ToString());
        if (logo is not null)
        {
            using (logo.Stream)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = logo.Stream;
                image.EndInit();
                check(image.PixelWidth > 0 && image.PixelHeight > 0,
                    "DogSC About 图标能从应用资源流解码", new { image.PixelWidth, image.PixelHeight });
            }
        }
        var interfaceFont = (FontFamily)application.Resources["InterfaceFont"];
        var fontUri = new Uri("pack://application:,,,/DogSC;component/Assets/Fonts/AlibabaPuHuiTi-3-55-Regular.otf");
        var fontResource = Application.GetResourceStream(fontUri);
        check(interfaceFont.Source.Contains("/DogSC;component/Assets/Fonts/", StringComparison.Ordinal) && fontResource is not null,
            "AssemblyName 改为 DogSC 后，字体 pack URI 与链接字体资源仍可解析", new { interfaceFont.Source, FontResource = fontResource is not null });
        fontResource?.Stream.Dispose();
        var createAbout = typeof(MainWindow).GetMethod("CreateAboutDialog", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "CreateAboutDialog");
        var about = (Window)createAbout.Invoke(window, [false])!;
        check(about.SizeToContent == SizeToContent.Height && double.IsNaN(about.Height),
            "About 窗口按实际内容计算高度，避免固定高度裁切", new { about.Width, about.SizeToContent });
        var wrapped = (FrameworkElement)about.Content;
        var aboutContent = Descendants(wrapped).OfType<StackPanel>().Single();
        var requiredText = new[]
        {
            "关于 DogSC", "DogSC 0.2.18", "Windows 社区移植版 · 非官方 Windows 发行版",
            "原作者：神烦老狗", "DogSC 原仓库", "原仓库采用 MIT 许可证；Windows 移植由 lantiansb 与社区贡献者维护。",
            "Windows 社区仓库 · 下载与反馈", "使用 FFmpeg LGPLv3 动态库与独立 GPL 编码程序。对应源码和许可证随发行包提供。"
        };
        var text = Descendants(aboutContent).OfType<TextBlock>().ToArray();
        foreach (var value in requiredText.Skip(1))
            check(text.Any(item => item.Text == value || item.Inlines.Any(run => run is Hyperlink hyperlink && hyperlink.Inlines.OfType<Run>().Any(r => r.Text == value))),
                "About 完整呈现品牌、作者、上游仓库与许可证说明", value);
        var links = text.SelectMany(item => item.Inlines.OfType<Hyperlink>()).ToArray();
        var link = links.Single(item => item.NavigateUri?.AbsoluteUri == "https://github.com/laogou717/dogsc");
        check(link.NavigateUri?.AbsoluteUri == "https://github.com/laogou717/dogsc", "About 仓库链接指向原作者公开仓库", link.NavigateUri?.AbsoluteUri);
        check(links.Any(item => item.NavigateUri?.AbsoluteUri == "https://github.com/lantiansb/dogsc"),
            "About 提供 Windows 社区仓库的下载与反馈入口", null);
        var linkStyle = link.Style;
        check(link.Focusable && link.TextDecorations?.Any(decoration => decoration.Location == TextDecorationLocation.Underline) == true &&
              linkStyle?.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == ContentElement.IsFocusedProperty) == true,
            "About 链接保留下划线，并在键盘聚焦时以主题底色和字重反馈，不使用虚线焦点框", null);

        foreach (var theme in new[] { "light", "dark" })
        {
            var dark = theme == "dark";
            application.Resources["Workspace"] = Brush(dark ? "#252829" : "#F4F6F6");
            application.Resources["Chrome"] = Brush(dark ? "#393D3E" : "#FFFFFF");
            application.Resources["Ink"] = Brush(dark ? "#F1F2F2" : "#303232");
            wrapped.Measure(new Size(440, double.PositiveInfinity));
            wrapped.Arrange(new Rect(0, 0, 440, wrapped.DesiredSize.Height));
            wrapped.UpdateLayout();
            check(wrapped.ActualHeight >= 300 && wrapped.ActualHeight < 600 && aboutContent.ActualHeight > 180,
                theme + ": About 窗口壳按完整自然高度布局", new { ShellHeight = wrapped.ActualHeight, ContentHeight = aboutContent.ActualHeight, wrapped.DesiredSize });
            var outOfBounds = text.Where(item =>
            {
                var rect = item.TransformToAncestor(aboutContent).TransformBounds(new Rect(new Point(0, 0), item.RenderSize));
                return rect.Left < -0.5 || rect.Top < -0.5 || rect.Right > aboutContent.ActualWidth + 0.5 || rect.Bottom > aboutContent.ActualHeight + 0.5;
            }).Select(item => item.Text).ToArray();
            check(outOfBounds.Length == 0, theme + ": About 所有文字边界均在内容区域内", outOfBounds);
            var ink = ((SolidColorBrush)application.Resources["Ink"]).Color;
            var background = ((SolidColorBrush)application.Resources["Chrome"]).Color;
            check(Contrast(ink, background) >= 4.5, theme + ": About 前景色继承主题且对比度可读", new { Foreground = ink.ToString(), Background = background.ToString(), Ratio = Contrast(ink, background) });
            var linkForeground = ((SolidColorBrush)link.Foreground).Color;
            var linkRatio = Contrast(linkForeground, background);
            check(linkRatio >= 4.5, theme + ": About Hyperlink 实际前景对比度 >= 4.5", new { Foreground = linkForeground.ToString(), Background = background.ToString(), Ratio = linkRatio });
            Save(wrapped, Path.Combine(output, "about-" + theme + ".png"));
        }
        // Do not close this unshown MainWindow: its normal Closing handler performs async desktop cleanup.

        var fixture = Directory.CreateTempSubdirectory("DogSC-branding-");
        try
        {
            var dogScExe = Path.Combine(fixture.FullName, "DogSC.exe");
            var oldExe = Path.Combine(fixture.FullName, "FrameTrail.exe");
            File.WriteAllBytes(dogScExe, []);
            File.WriteAllBytes(oldExe, []);
            check(DesktopTrayIcon.ResolveApplicationExecutable(fixture.FullName, null) == dogScExe,
                "托盘默认读取 DogSC.exe 图标", DesktopTrayIcon.ResolveApplicationExecutable(fixture.FullName, null));
            check(DesktopTrayIcon.ResolveApplicationExecutable(fixture.FullName, oldExe) == oldExe,
                "兼容探针仍能从已运行的 FrameTrail.exe 读取图标", DesktopTrayIcon.ResolveApplicationExecutable(fixture.FullName, oldExe));
        }
        finally { fixture.Delete(true); }
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luma(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luma(foreground); var b = Luma(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
