using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

/// <summary>Hex colour parsing that never throws (Mac HexColor(_:) returns nil for bad input).</summary>
internal static class ColorParsing
{
    /// <summary>Accepts #RGB, #RRGGBB, with or without #, any case. Returns "#RRGGBB" in upper case.</summary>
    public static bool TryParse(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var raw = text.Trim();
        if (raw.StartsWith('#')) raw = raw[1..];
        if (raw.Length == 3) raw = string.Concat(raw.Select(c => new string(c, 2)));
        if (raw.Length != 6) return false;
        foreach (var c in raw)
            if (!Uri.IsHexDigit(c)) return false;
        normalized = "#" + raw.ToUpperInvariant();
        return true;
    }

    public static Color ToColor(string normalized)
    {
        var r = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb(r, g, b);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static (double H, double S, double B) ToHsb(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h = 0;
        if (delta > 1e-9)
        {
            if (max == r) h = ((g - b) / delta % 6 + 6) % 6;
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h /= 6;
        }
        return (h, max <= 0 ? 0 : delta / max, max);
    }

    public static Color FromHsb(double h, double s, double v)
    {
        h = Math.Clamp(h, 0, 1);
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var sector = h >= 1 ? 0 : h * 6;
        var index = (int)Math.Floor(sector);
        var f = sector - index;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = index switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}

public partial class MainWindow
{
    private static readonly string[] DefaultColorPresets =
    [
        "#F5F6F5", "#E8DED2", "#D8E8E3", "#DADFEF", "#ECD7DF", "#F3D1B1", "#2D3338", "#182A3E", "#151515",
        "#7C5CFC", "#38BDF8", "#10B981", "#F97316", "#EC4899", "#FFFFFF"
    ];

    /// <summary>
    /// Swatch + hex field + HSB popover (Mac EditorHexColorInput / EditorColorPalette). Valid text and
    /// palette drags preview live; Enter/blur/closing the popover commits one undo step; invalid text
    /// turns red and is never committed; Esc restores the committed colour.
    /// </summary>
    private void ColorField(string label, string value, Action<string> apply, string? undo = null, IReadOnlyList<string>? presets = null)
    {
        var gesture = new InspectorGesture(this, undo ?? "调整" + label);
        var committed = ColorParsing.TryParse(value, out var normalizedStart) ? normalizedStart : "#000000";
        var draft = committed;
        var invalid = false;
        var programmatic = false;

        if (label.Length > 0)
            InspectorHost.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("Secondary"), Margin = new Thickness(0, 12, 0, 4) });

        var swatchFill = new Border { Width = 32, Height = 22, CornerRadius = new CornerRadius(5), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1) };
        var swatch = new Button
        {
            Content = swatchFill, Padding = new Thickness(3), Margin = new Thickness(0, 4, 8, 4), Style = (Style)FindResource("PlainButton"),
            ToolTip = "打开取色器"
        };
        System.Windows.Automation.AutomationProperties.SetName(swatch, "选择" + label);
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 4), FontFamily = new FontFamily("Consolas"), MaxLength = 9, Text = committed };
        System.Windows.Automation.AutomationProperties.SetName(box, label + "十六进制值");
        RegisterInspectorFocus(box, label + "十六进制");
        RegisterInspectorFocus(swatch, label + "色块");

        void ShowColor(string hex) => swatchFill.Background = new SolidColorBrush(ColorParsing.ToColor(hex));
        void SetText(string text)
        {
            programmatic = true;
            try { box.Text = text; }
            finally { programmatic = false; }
        }
        void MarkInvalid(bool bad)
        {
            invalid = bad;
            if (bad)
            {
                box.BorderBrush = new SolidColorBrush(Color.FromRgb(216, 64, 64));
                box.ToolTip = "请输入 6 位十六进制颜色，例如 #D8B26A";
            }
            else
            {
                box.ClearValue(Control.BorderBrushProperty);
                box.ClearValue(FrameworkElement.ToolTipProperty);
            }
        }
        ShowColor(committed);

        void Preview(string hex)
        {
            draft = hex;
            ShowColor(hex);
            gesture.Preview(() => apply(hex));
        }
        void Revert()
        {
            gesture.Cancel();
            draft = committed;
            ShowColor(committed);
            SetText(committed);
            MarkInvalid(false);
        }
        void Finish(bool release)
        {
            if (invalid)
            {
                Revert();
                return;
            }
            var hex = draft;
            if (hex == committed)
            {
                gesture.Cancel();
                if (release) FocusEditor();
                return;
            }
            committed = hex;
            SetText(hex);
            if (release) CommitAndReleaseFocus(() => gesture.Commit(() => apply(hex)));
            else gesture.Commit(() => apply(hex));
        }

        var editing = false;
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            box.Focus();
            e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            if (programmatic) return;
            editing = true;
            if (!ColorParsing.TryParse(box.Text, out var hex))
            {
                MarkInvalid(true);
                return;
            }
            MarkInvalid(false);
            Preview(hex);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                editing = false;
                Revert();
                FocusEditor();
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                if (invalid) return;
                editing = false;
                Finish(true);
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!editing) return;
            editing = false;
            Finish(false);
        };

        var popup = new Popup
        {
            PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        swatch.Click += (_, _) =>
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }
            popup.Child = BuildColorPalette(label, draft, presets ?? DefaultColorPresets,
                hex =>
                {
                    SetText(hex);
                    MarkInvalid(false);
                    Preview(hex);
                },
                () =>
                {
                    Revert();
                    popup.IsOpen = false;
                    FocusEditor();
                });
            popup.IsOpen = true;
        };
        popup.Closed += (_, _) =>
        {
            if (gesture.Active) Finish(true);
        };

        var row = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);
        row.Children.Add(popup);
        row.Children.Add(box);
        InspectorHost.Children.Add(row);
    }

    /// <summary>HSB square + hue strip + preset swatches + hex read-out in a 272pt popover.</summary>
    private FrameworkElement BuildColorPalette(string title, string startHex, IReadOnlyList<string> presets, Action<string> changed, Action cancel)
    {
        var start = ColorParsing.ToColor(startHex);
        var (hue, saturation, brightness) = ColorParsing.ToHsb(start);
        const double planeWidth = 240, planeHeight = 150, stripWidth = 240;

        var root = new StackPanel { Width = 272, Focusable = true };
        var card = new Border
        {
            Background = Brush("Chrome"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16), Margin = new Thickness(8), Child = root,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .22, Direction = 270 }
        };
        root.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

        // Saturation / brightness plane
        var hueColor = new SolidColorBrush(ColorParsing.FromHsb(hue, 1, 1));
        var plane = new Grid { Width = planeWidth, Height = planeHeight, Cursor = Cursors.Cross, ClipToBounds = true, Background = Brushes.Transparent };
        plane.Children.Add(new Rectangle { Fill = hueColor, RadiusX = 8, RadiusY = 8 });
        plane.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)) });
        plane.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)) });
        var planeHandle = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        plane.Children.Add(planeHandle);
        root.Children.Add(plane);

        // Hue strip
        var stops = new GradientStopCollection();
        for (var i = 0; i <= 6; i++) stops.Add(new GradientStop(ColorParsing.FromHsb(i / 6d, 1, 1), i / 6d));
        var strip = new Grid { Width = stripWidth, Height = 20, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        strip.Children.Add(new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)), Margin = new Thickness(8, 0, 8, 0), IsHitTestVisible = false });
        var stripHandle = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        strip.Children.Add(stripHandle);
        root.Children.Add(strip);

        // Presets
        var presetGrid = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Width = 240 };
        foreach (var preset in presets)
        {
            if (!ColorParsing.TryParse(preset, out var normalized)) continue;
            var swatch = new Button
            {
                Width = 26, Height = 22, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), Style = (Style)FindResource("PlainButton"),
                Content = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ColorParsing.ToColor(normalized)), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1) },
                ToolTip = normalized
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "预设色 " + normalized);
            var target = normalized;
            swatch.Click += (_, _) =>
            {
                (hue, saturation, brightness) = ColorParsing.ToHsb(ColorParsing.ToColor(target));
                Publish(false);
                changed(target);
            };
            presetGrid.Children.Add(swatch);
        }
        root.Children.Add(presetGrid);

        var hexLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = Brush("Secondary"), Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(hexLabel);

        void Layout()
        {
            planeHandle.Margin = new Thickness(Math.Clamp(planeWidth * saturation, 8, planeWidth - 8) - 8, Math.Clamp(planeHeight * (1 - brightness), 8, planeHeight - 8) - 8, 0, 0);
            stripHandle.Margin = new Thickness(8 + hue * (stripWidth - 16) - 8, 0, 0, 0);
            var current = ColorParsing.FromHsb(hue, saturation, brightness);
            planeHandle.Fill = new SolidColorBrush(current);
            stripHandle.Fill = new SolidColorBrush(ColorParsing.FromHsb(hue, 1, 1));
            hueColor.Color = ColorParsing.FromHsb(hue, 1, 1);
            hexLabel.Text = "HEX  " + ColorParsing.ToHex(current);
        }
        void Publish(bool notify)
        {
            Layout();
            if (notify) changed(ColorParsing.ToHex(ColorParsing.FromHsb(hue, saturation, brightness)));
        }

        void PlaneAt(MouseEventArgs e)
        {
            var p = e.GetPosition(plane);
            saturation = Math.Clamp(p.X / planeWidth, 0, 1);
            brightness = 1 - Math.Clamp(p.Y / planeHeight, 0, 1);
            Publish(true);
        }
        void StripAt(MouseEventArgs e)
        {
            var p = e.GetPosition(strip);
            hue = Math.Clamp((p.X - 8) / (stripWidth - 16), 0, 1);
            Publish(true);
        }
        plane.MouseLeftButtonDown += (_, e) => { plane.CaptureMouse(); PlaneAt(e); };
        plane.MouseMove += (_, e) => { if (plane.IsMouseCaptured) PlaneAt(e); };
        plane.MouseLeftButtonUp += (_, _) => plane.ReleaseMouseCapture();
        strip.MouseLeftButtonDown += (_, e) => { strip.CaptureMouse(); StripAt(e); };
        strip.MouseMove += (_, e) => { if (strip.IsMouseCaptured) StripAt(e); };
        strip.MouseLeftButtonUp += (_, _) => strip.ReleaseMouseCapture();

        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            cancel();
            e.Handled = true;
        };
        root.Loaded += (_, _) => root.Focus();
        Layout();
        return card;
    }
}
