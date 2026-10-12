using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    private static readonly (BackgroundKind Value, string Label, string Icon)[] BackgroundSources =
    [
        (BackgroundKind.Solid, "纯色", "frame"),
        (BackgroundKind.LinearGradient, "渐变", "frame"),
        (BackgroundKind.Pattern, "图案", "layers"),
        (BackgroundKind.Dynamic, "动态", "cube"),
        (BackgroundKind.Image, "图片", "screen"),
        (BackgroundKind.Video, "视频", "opening")
    ];

    private void BuildBackgroundInspector()
    {
        var current = project!.Settings.BackgroundStyle ?? new BackgroundSettings
        {
            Color = project.Settings.Background,
            EndColor = project.Settings.Background
        };

        Section("背景素材");
        ChoiceCards(
            "背景来源",
            current.Kind,
            BackgroundSources,
            SelectBackgroundSource,
            columns: 3,
            preview: kind => BackgroundSourcePreview(kind, current));

        if (current.Kind is BackgroundKind.Solid or BackgroundKind.LinearGradient)
        {
            Section(current.Kind == BackgroundKind.Solid ? "颜色" : "渐变颜色");
            AddBackgroundPalette(current.Color);
            ColorField(current.Kind == BackgroundKind.Solid ? "背景颜色" : "起始颜色", current.Color, value => InspectorEdit(p =>
            {
                var bg = BackgroundSettingsFor(p);
                bg.Color = value;
                p.Settings.Background = bg.Color;
            }), "调整背景颜色");

            if (current.Kind == BackgroundKind.LinearGradient)
            {
                ColorField("结束颜色", current.EndColor, value => InspectorEdit(p =>
                    BackgroundSettingsFor(p).EndColor = value), "调整渐变结束颜色");
                Number("方向", current.AngleDegrees, 0, 360, value => InspectorEdit(p =>
                    BackgroundSettingsFor(p).AngleDegrees = value), NumFormat.Degrees, log: false, undo: "调整渐变方向");
            }
        }
        else if (current.Kind == BackgroundKind.Pattern)
        {
            Section("图案样式");
            ChoiceCards(
                "图案",
                current.Pattern,
                new[]
                {
                    (BackgroundPatternKind.Grid, "细网格", "square.grid.3x3"),
                    (BackgroundPatternKind.Dots, "点阵", "circle.grid.3x3")
                },
                value => InspectorEdit(p => BackgroundSettingsFor(p).Pattern = value, "调整背景图案"),
                columns: 2,
                preview: pattern => PatternPreview(pattern, current.Color, current.PatternColor,
                    current.PatternSpacing, current.PatternStrength));
            Number("间隔", current.PatternSpacing, 8, 120, value => InspectorEdit(p =>
                BackgroundSettingsFor(p).PatternSpacing = value), NumFormat.Pixels, undo: "调整图案间隔");
            Number("强度", current.PatternStrength, 0, 1, value => InspectorEdit(p =>
                BackgroundSettingsFor(p).PatternStrength = value), NumFormat.Percent, log: false, undo: "调整图案强度");
            ColorField("图案颜色", current.PatternColor, value => InspectorEdit(p =>
                BackgroundSettingsFor(p).PatternColor = value), "调整图案颜色");
        }
        else if (current.Kind == BackgroundKind.Dynamic)
        {
            Section("动态样式");
            ChoiceCards(
                "动效",
                current.Preset,
                new[]
                {
                    (DynamicBackgroundPreset.Aurora, "极光", "sparkles"),
                    (DynamicBackgroundPreset.Ocean, "海洋", "water.waves"),
                    (DynamicBackgroundPreset.Sunset, "落日", "sunset.fill")
                },
                value => InspectorEdit(p => BackgroundSettingsFor(p).Preset = value, "调整动态背景"),
                columns: 3,
                preview: preset => DynamicPreview(preset));
            Number("速度", current.Speed, .1, 3, value => InspectorEdit(p =>
                BackgroundSettingsFor(p).Speed = value), NumFormat.Multiplier, undo: "调整动态背景速度");
        }
        else
        {
            var isVideo = current.Kind == BackgroundKind.Video;
            var assetPath = isVideo ? current.VideoPath : current.ImagePath;
            ActionButton(
                assetPath == null ? "选择图片或视频…" : "更换图片或视频…",
                () => _ = Guard(ImportBackgroundMedia),
                primary: assetPath == null);
            if (assetPath is { Length: > 0 })
                Note(Path.GetFileName(assetPath));
            else
                Note("选择文件后会复制到项目素材目录，并用于预览和导出。", true);

            // Mac backgroundBlurSection: blur sits directly under the wallpaper media, 0...80.
            Number("背景模糊", Math.Min(current.Blur, .8) * 100, 0, 80, value => InspectorEdit(p =>
                BackgroundSettingsFor(p).Blur = value / 100), NumFormat.Pixels, undo: "调整背景模糊");
            InspectorDisclosure("填充方式", () =>
            {
                Choices("填充", current.ImageFit, new[]
                {
                    (BackgroundImageFit.Cover, "铺满"),
                    (BackgroundImageFit.Contain, "完整显示"),
                    (BackgroundImageFit.Stretch, "拉伸")
                }, value => InspectorEdit(p => BackgroundSettingsFor(p).ImageFit = value, "调整背景填充"));
            });

            if (isVideo)
            {
                Section("视频播放");
                Toggle("循环播放", current.VideoLoop, value => InspectorEdit(p =>
                    BackgroundSettingsFor(p).VideoLoop = value, "切换背景视频循环"));
                Number("开始时间", current.VideoStartTime, 0, 3600, value => InspectorEdit(p =>
                    BackgroundSettingsFor(p).VideoStartTime = value), NumFormat.Seconds, undo: "调整背景视频开始时间");
            }
        }
    }

    private void SelectBackgroundSource(BackgroundKind kind)
    {
        var settings = project!.Settings.BackgroundStyle ?? new BackgroundSettings
        {
            Color = project.Settings.Background,
            EndColor = project.Settings.Background
        };
        if (kind == BackgroundKind.Image && string.IsNullOrWhiteSpace(settings.ImagePath) ||
            kind == BackgroundKind.Video && string.IsNullOrWhiteSpace(settings.VideoPath))
        {
            _ = Guard(ImportBackgroundMedia);
            return;
        }

        InspectorEdit(p => BackgroundSettingsFor(p).Kind = kind, "选择背景来源");
    }

    private async Task ImportBackgroundMedia()
    {
        if (projectFile == null) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片或视频",
            Filter = "图片或视频|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.mov;*.mkv;*.webm;*.avi",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var isVideo = extension is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi";
        var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        if (!isVideo && !isImage)
            throw new InvalidDataException("背景文件格式不受支持。");

        var info = await MediaTools.ProbeAsync(dialog.FileName);
        if (info.Width < 1 || info.Height < 1 || (isVideo && info.Duration <= 0))
            throw new InvalidDataException(isVideo ? "视频没有可用画面或有效时长。" : "图片无法解码。");

        var relative = "media/background-" + Guid.NewGuid().ToString("N") + extension;
        var target = FrameTrailProjectStore.ResolveAssetPath(projectFile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(dialog.FileName, target);

        try
        {
            ApplyEdit(p =>
            {
                var bg = BackgroundSettingsFor(p);
                if (isVideo)
                {
                    bg.Kind = BackgroundKind.Video;
                    bg.VideoPath = relative;
                    bg.VideoStartTime = 0;
                }
                else
                {
                    bg.Kind = BackgroundKind.Image;
                    bg.ImagePath = relative;
                }
            });
            auxiliaryDurations[relative] = info.Duration;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }
    }

    private void AddBackgroundPalette(string selectedColor)
    {
        var colors = new[]
        {
            "#F5F6F5", "#E8DED2", "#D8E8E3", "#DADFEF", "#ECD7DF",
            "#F3D1B1", "#2D3338", "#182A3E", "#151515"
        };
        var palette = new UniformGrid { Columns = 5, Margin = new Thickness(0, 6, 0, 8) };
        foreach (var value in colors)
        {
            var brush = BrushFromHex(value);
            var button = new Button
            {
                Content = new Border { Width = 32, Height = 24, Background = brush, CornerRadius = new CornerRadius(7) },
                Width = 42,
                Height = 34,
                Margin = new Thickness(3),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(value.Equals(selectedColor, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                BorderBrush = value.Equals(selectedColor, StringComparison.OrdinalIgnoreCase)
                    ? Brush("Accent") : Brush("Hairline"),
                ToolTip = value
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "背景颜色 " + value);
            button.Click += (_, _) => CommitInspectorAction("选择背景颜色", () => InspectorEdit(p =>
            {
                var bg = BackgroundSettingsFor(p);
                bg.Color = value;
                p.Settings.Background = value;
            }));
            palette.Children.Add(button);
        }
        InspectorHost.Children.Add(palette);
    }

    private FrameworkElement BackgroundSourcePreview(BackgroundKind kind, BackgroundSettings settings)
    {
        return kind switch
        {
            BackgroundKind.Solid => ColorPreview(settings.Color),
            BackgroundKind.LinearGradient => GradientPreview(settings.Color, settings.EndColor, settings.AngleDegrees),
            BackgroundKind.Pattern => PatternPreview(settings.Pattern, settings.Color, settings.PatternColor,
                settings.PatternSpacing, settings.PatternStrength),
            BackgroundKind.Dynamic => DynamicPreview(settings.Preset),
            BackgroundKind.Image when !string.IsNullOrWhiteSpace(settings.ImagePath) =>
                ImageAssetPreview(settings.ImagePath),
            _ => IconPreview(kind switch
            {
                BackgroundKind.Image => "图片文件",
                BackgroundKind.Video => "视频文件",
                _ => ""
            }, kind == BackgroundKind.Image ? "screen" : "opening")
        };
    }

    private FrameworkElement ImageAssetPreview(string relativePath)
    {
        try
        {
            var path = FrameTrailProjectStore.ResolveAssetPath(projectFile!, relativePath);
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 180;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Border
                {
                    Height = 48,
                    CornerRadius = new CornerRadius(7),
                    ClipToBounds = true,
                    Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException
            or NotSupportedException or ArgumentException or System.IO.FileFormatException)
        {
            App.Log(ex);
        }
        return IconPreview("找不到图片", "screen");
    }

    private static FrameworkElement ColorPreview(string hex) => new Border
    {
        MinWidth = 72,
        Height = 48,
        CornerRadius = new CornerRadius(7),
        Background = BrushFromHex(hex)
    };

    private static FrameworkElement GradientPreview(string start, string end, double angle)
    {
        var radians = angle * Math.PI / 180;
        var dx = Math.Cos(radians);
        var dy = Math.Sin(radians);
        return new Border
        {
            MinWidth = 72,
            Height = 48,
            CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush(BrushFromHex(start).Color, BrushFromHex(end).Color,
                new Point(.5 - dx * .5, .5 - dy * .5), new Point(.5 + dx * .5, .5 + dy * .5))
        };
    }

    private FrameworkElement PatternPreview(BackgroundPatternKind kind, string backgroundColor,
        string patternColor, double spacing, double strength)
    {
        var canvas = new Canvas { Width = 96, Height = 48, Background = BrushFromHex(backgroundColor), ClipToBounds = true };
        var lineBrush = BrushFromHex(patternColor);
        lineBrush.Opacity = Math.Clamp(strength, 0, 1);
        var step = Math.Clamp(spacing / 3, 9, 24);
        if (kind == BackgroundPatternKind.Grid)
        {
            for (var x = 0d; x <= 96; x += step)
                canvas.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = 48, Stroke = lineBrush, StrokeThickness = 1 });
            for (var y = 0d; y <= 48; y += step)
                canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = 96, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
        }
        else
        {
            for (var x = step / 2; x < 96; x += step)
            for (var y = step / 2; y < 48; y += step)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Fill = lineBrush };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, y);
                canvas.Children.Add(dot);
            }
        }
        return new Border { MinWidth = 72, Height = 48, CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = canvas };
    }

    private static FrameworkElement DynamicPreview(DynamicBackgroundPreset preset)
    {
        var colors = preset switch
        {
            DynamicBackgroundPreset.Aurora => ("#173A52", "#64BFA4"),
            DynamicBackgroundPreset.Ocean => ("#063B63", "#3CA9C2"),
            _ => ("#692F54", "#F29B62")
        };
        var grid = new Grid { Width = 96, Height = 48, ClipToBounds = true };
        grid.Children.Add(GradientPreview(colors.Item1, colors.Item2, 35));
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 58,
            Height = 58,
            Fill = new RadialGradientBrush(Color.FromArgb(175, 255, 255, 255), Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -16, 18, 0)
        };
        grid.Children.Add(glow);
        return new Border { MinWidth = 72, Height = 48, CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = grid };
    }

    private FrameworkElement IconPreview(string label, string glyph)
    {
        var panel = new StackPanel { Height = 48, Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new EditorIcon { Kind = glyph, Width = 20, Height = 20,
            Margin = new Thickness(0, 0, 8, 0), Stroke = Brush("Secondary") });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Secondary") });
        return new Border { MinWidth = 72, Height = 48, CornerRadius = new CornerRadius(7), Background = Brush("ControlSurface"), Child = panel };
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return new SolidColorBrush(Colors.Transparent); }
    }
}
