using System.Globalization;
using System.Windows;
using System.Windows.Controls;
namespace FrameTrail.App;

// Canvas ratio menu (Mac EditorCanvasRatioSelector): adaptive / 16:9 / 4:3 / 2.39:1 / 1:1 / custom,
// plus a landscape / portrait switch. The canvas ratio is stored as integer CanvasWidth:CanvasHeight.
public partial class MainWindow
{
    private void AspectClick(object sender, RoutedEventArgs e)
    {
        if (project is null) { Status.Text = "请先打开项目。"; return; }
        var width = project.Settings.CanvasWidth; var height = project.Settings.CanvasHeight;
        var adaptive = project.Settings.Canvas?.AdaptiveAspect == true;
        var portrait = height > width;
        var menu = NewMenu();

        var adaptiveEntry = new MenuItem { Header = "自适应（跟随素材×裁切）", IsCheckable = true, IsChecked = adaptive };
        adaptiveEntry.Click += (_, _) => SetAdaptiveAspect();
        menu.Items.Add(adaptiveEntry);
        menu.Items.Add(new Separator());

        var square = false;
        foreach (var (label, a, b) in new[] { ("16:9", 16, 9), ("4:3", 4, 3), ("2.39:1 电影", 239, 100), ("1:1", 1, 1) })
        {
            var (w, h) = portrait && a != b ? (b, a) : (a, b);
            var matches = !adaptive && ((long)width * h == (long)height * w);
            if (matches && a == b) square = true;
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = matches };
            entry.Click += (_, _) => SetAspect(w, h);
            menu.Items.Add(entry);
        }
        var custom = new MenuItem { Header = "自定义比例…" };
        custom.Click += (_, _) => PromptCustomAspect();
        menu.Items.Add(custom);

        menu.Items.Add(new Separator());
        foreach (var (label, wantPortrait) in new[] { ("横向", false), ("竖向", true) })
        {
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = portrait == wantPortrait && width != height, IsEnabled = !adaptive && width != height && !square };
            entry.Click += (_, _) => { if (portrait != wantPortrait) SetAspect(height, width); };
            menu.Items.Add(entry);
        }
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void PromptCustomAspect()
    {
        if (project is null) return;
        var value = PromptText(this, "自定义画布比例", "输入宽:高，可用小数，例如 3:2 或 2.39:1（比例范围 0.1–10）", project.Settings.Canvas?.AdaptiveAspect == true ? $"{project.Settings.CanvasWidth}:{project.Settings.CanvasHeight}" : AspectText(project));
        if (value is null) return;
        var parts = value.Replace('：', ':').Replace('/', ':').Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0 || w / h is < 0.1 or > 10)
        {
            SurfaceDialog.Show(this, "请输入宽:高比例，例如 3:2 或 2.39:1，比例范围 0.1–10。", "比例无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var (iw, ih) = ToIntegerRatio(w, h);
        SetAspect(iw, ih);
    }

    /// <summary>Converts a decimal ratio such as 2.39:1 to the smallest integer pair (239:100) with both sides at least 2.</summary>
    internal static (int Width, int Height) ToIntegerRatio(double width, double height)
    {
        var w = Math.Max(1, (long)Math.Round(width * 1000)); var h = Math.Max(1, (long)Math.Round(height * 1000));
        var g = Gcd64(w, h); w /= g; h /= g;
        while ((w < 2 || h < 2) && w < 1000 && h < 1000) { w *= 2; h *= 2; }
        while (w > 20000 || h > 20000) { w = Math.Max(2, w / 2); h = Math.Max(2, h / 2); }
        return ((int)Math.Max(2, w), (int)Math.Max(2, h));
    }

    private static long Gcd64(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(1, a); }

    private void SetAspect(int width, int height)
    {
        ApplyEdit(p =>
        {
            p.Settings.CanvasWidth = width; p.Settings.CanvasHeight = height;
            var canvas = p.Settings.Canvas ??= new CanvasSettings();
            canvas.AspectWidth = width; canvas.AspectHeight = height; canvas.AdaptiveAspect = false;
        });
    }

    private void SetAdaptiveAspect()
    {
        ApplyEdit(p =>
        {
            var canvas = p.Settings.Canvas ??= new CanvasSettings();
            canvas.AdaptiveAspect = true;
            ApplyAdaptiveCanvas(p);
        });
    }

    /// <summary>Keeps an adaptive canvas at source size times crop (called from every edit, so the crop editor and inspector keep it in sync).</summary>
    private static void ApplyAdaptiveCanvas(FrameTrailProject p)
    {
        if (p.Settings.Canvas is not { AdaptiveAspect: true } canvas) return;
        var crop = canvas.Crop.IsFinite && canvas.Crop.Width > 0 && canvas.Crop.Height > 0 ? canvas.Crop : NormalizedRect.Full;
        var width = p.Media.Width * crop.Width; var height = p.Media.Height * crop.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 2 || height < 2) return;
        var (w, h) = ToIntegerRatio(width, height);
        // Pixel dimensions are kept when they are plausible; ratio form is only used for odd sizes.
        var pixelW = (int)Math.Round(width); var pixelH = (int)Math.Round(height);
        if (pixelW is >= 2 and <= 20000 && pixelH is >= 2 and <= 20000) { w = pixelW; h = pixelH; }
        p.Settings.CanvasWidth = w; p.Settings.CanvasHeight = h;
        canvas.AspectWidth = w; canvas.AspectHeight = h;
    }

    /// <summary>Toolbar label for the canvas ratio (adaptive, 2.39:1 and decimal ratios read naturally).</summary>
    private static string AspectText(FrameTrailProject p)
    {
        if (p.Settings.Canvas?.AdaptiveAspect == true) return "自适应";
        var w = p.Settings.CanvasWidth; var h = p.Settings.CanvasHeight;
        var g = (int)Gcd64(w, h); var a = w / g; var b = h / g;
        if (a <= 40 && b <= 40) return $"{a}:{b}";
        return a >= b ? $"{(double)a / b:0.##}:1" : $"1:{(double)b / a:0.##}";
    }
}
