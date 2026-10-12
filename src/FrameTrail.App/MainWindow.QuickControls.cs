using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace FrameTrail.App;

// Floating quick controls next to the selected sticker / blur region (Mac EditorCanvasOverlayQuickControls.swift):
// the values that are tweaked most often can be changed without moving the pointer to the inspector. Slider drags
// preview live and commit one labelled undo step on release.
public partial class MainWindow
{
    private Border? quickBar;
    private object? quickBarKey;
    private bool quickBuilding;
    private Action<FrameTrailProject>? quickPendingEdit;
    private string quickPendingLabel = "";

    private void HideQuickControls()
    {
        if (quickBar is null) return;
        quickBar.Visibility = Visibility.Collapsed;
        quickBarKey = null;
    }

    private void UpdateQuickControls(CanvasEditTarget target, CanvasShape? shape)
    {
        if (project is null || shape is null || target.Kind is not (CanvasEditKind.Overlay or CanvasEditKind.Blur) || gesture != CanvasGesture.None)
        {
            HideQuickControls();
            return;
        }
        object key;
        if (target.Kind == CanvasEditKind.Overlay)
        {
            var clip = project.Overlays.FirstOrDefault(x => x.Id == target.Id);
            if (clip is null) { HideQuickControls(); return; }
            key = (target, Math.Round(clip.Opacity, 3), Math.Round(clip.CornerRadius, 3), clip.HidesScreen, clip.EnterMotion, Math.Round(clip.BackdropBlur, 1));
        }
        else
        {
            var clip = project.BlurClips.FirstOrDefault(x => x.Id == target.Id);
            if (clip is null) { HideQuickControls(); return; }
            key = (target, clip.Kind, Math.Round(clip.Strength, 3), Math.Round(clip.SpotlightDimming, 3), Math.Round(clip.CornerRadius, 3));
        }
        if (quickBar is null)
        {
            quickBar = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .2 }
            };
            quickBar.SetResourceReference(Border.BackgroundProperty, "Surface");
            quickBar.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            Panel.SetZIndex(quickBar, 5);
            PreviewSurface.Children.Add(quickBar);
        }
        if (!Equals(quickBarKey, key))
        {
            quickBarKey = key;
            quickBuilding = true;
            try { quickBar.Child = target.Kind == CanvasEditKind.Overlay ? BuildOverlayQuickRow(target.Id) : BuildBlurQuickRow(target.Id); }
            finally { quickBuilding = false; }
        }
        quickBar.Visibility = Visibility.Visible;
        PositionQuickBar(shape.Quad);
    }

    private void PositionQuickBar(Point[] quad)
    {
        if (quickBar is null) return;
        var width = PreviewSurface.ActualWidth; var height = PreviewSurface.ActualHeight;
        quickBar.Measure(new Size(Math.Max(width, 1), double.PositiveInfinity));
        var size = quickBar.DesiredSize;
        var centerX = quad.Average(p => p.X); var top = quad.Min(p => p.Y); var bottom = quad.Max(p => p.Y);
        var x = Math.Clamp(centerX - size.Width / 2, 4, Math.Max(4, width - size.Width - 4));
        var y = bottom + 14;
        if (y + size.Height > height - 4) y = top - size.Height - 44; // above, clear of the rotation handle
        y = Math.Clamp(y, 4, Math.Max(4, height - size.Height - 4));
        quickBar.Margin = new Thickness(x, y, 0, 0);
    }

    private TextBlock QuickLabel(string text) => new() { Text = text, FontSize = 11, Foreground = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    private Slider QuickSlider(double value, double min, double max, Func<double, Action<FrameTrailProject>> makeEdit, string label)
    {
        var slider = new InspectorSlider
        {
            Style = (Style)FindResource(typeof(Slider)), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            Width = 96, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, _) =>
        {
            if (quickBuilding) return;
            var edit = makeEdit(slider.Value);
            quickPendingEdit = edit; quickPendingLabel = label;
            ScheduleLiveEdit(edit);
        };
        void Commit()
        {
            if (quickPendingEdit is not { } edit) return;
            var text = quickPendingLabel; quickPendingEdit = null;
            StopLiveEditTimer();
            CommitLiveEdit(edit, text);
        }
        slider.PreviewMouseLeftButtonUp += (_, _) => Commit();
        slider.KeyUp += (_, _) => Commit();
        slider.LostMouseCapture += (_, _) => Commit();
        slider.LostKeyboardFocus += (_, _) => Commit();
        return slider;
    }

    private void QuickCommit(Action<FrameTrailProject> edit, string label) =>
        _ = Guard(() => { Edit(edit, label); return Task.CompletedTask; });

    private UIElement BuildOverlayQuickRow(Guid id)
    {
        var clip = project!.Overlays.First(x => x.Id == id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BuildStickerAnimationMenu(clip));
        row.Children.Add(QuickLabel("不透明度"));
        row.Children.Add(QuickSlider(clip.Opacity, 0, 1, v => p => { if (p.Overlays.FirstOrDefault(c => c.Id == id) is { } c) c.Opacity = v; }, "调整贴图不透明度"));
        row.Children.Add(QuickLabel("圆角"));
        row.Children.Add(QuickSlider(clip.CornerRadius, 0, .5, v => p => { if (p.Overlays.FirstOrDefault(c => c.Id == id) is { } c) c.CornerRadius = v; }, "调整贴图圆角"));
        // B-12 (Mac stickerQuickEditor "虚化"): blur of the screen behind the sticker, unavailable while the sticker hides the screen.
        row.Children.Add(QuickLabel("虚化"));
        var blurSlider = QuickSlider(clip.BackdropBlur, 0, 60, v => p => { if (p.Overlays.FirstOrDefault(c => c.Id == id) is { } c) c.BackdropBlur = v; }, "调整贴图录屏虚化");
        blurSlider.IsEnabled = !clip.HidesScreen; blurSlider.Opacity = clip.HidesScreen ? .42 : 1;
        blurSlider.ToolTip = clip.HidesScreen ? "仅背景模式下不可用" : "虚化贴图下方的录屏画面";
        row.Children.Add(blurSlider);
        var hides = new CheckBox { Content = "仅背景", IsChecked = clip.HidesScreen, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "隐藏录屏画面，仅保留画布背景与贴图" };
        hides.Click += (_, _) =>
        {
            var on = hides.IsChecked == true;
            QuickCommit(p => { if (p.Overlays.FirstOrDefault(c => c.Id == id) is { } c) c.HidesScreen = on; }, on ? "开启贴图仅背景" : "关闭贴图仅背景");
        };
        row.Children.Add(hides);
        return row;
    }

    /// <summary>
    /// Mac "贴图动画" menu (StickerAnimationPreset): one choice sets the enter motion and its mirrored exit
    /// (OverlayMotionEvaluator.AutomaticExit) in a single undo step.
    /// </summary>
    private UIElement BuildStickerAnimationMenu(OverlayClip clip)
    {
        var id = clip.Id;
        var button = new Button { Content = "动画：" + MotionName(clip.EnterMotion) + " ▾", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 14, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "选择贴图动画（入场，出场自动镜像）" };
        if (TryFindResource("PlainButton") is Style plain) button.Style = plain;
        System.Windows.Automation.AutomationProperties.SetName(button, "贴图动画");
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var motion in Enum.GetValues<OverlayMotion>())
        {
            var chosen = motion;
            var item = new MenuItem { Header = MotionName(chosen), IsCheckable = true, IsChecked = clip.EnterMotion == chosen };
            item.Click += (_, _) => QuickCommit(p =>
            {
                if (p.Overlays.FirstOrDefault(c => c.Id == id) is not { } c) return;
                c.EnterMotion = chosen; c.ExitMotion = OverlayMotionEvaluator.AutomaticExit(chosen);
            }, "选择贴图动画");
            menu.Items.Add(item);
        }
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    private UIElement BuildBlurQuickRow(Guid id)
    {
        var clip = project!.BlurClips.First(x => x.Id == id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, text) in new[] { (BlurKind.Blur, "柔化"), (BlurKind.Spotlight, "突出") })
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0), FontSize = 11 };
            if (clip.Kind == kind) button.SetResourceReference(Control.BackgroundProperty, "Selection");
            var chosen = kind;
            button.Click += (_, _) => QuickCommit(p => { if (p.BlurClips.FirstOrDefault(c => c.Id == id) is { } c) c.Kind = chosen; }, "切换柔化模式");
            row.Children.Add(button);
        }
        var spotlight = clip.Kind == BlurKind.Spotlight;
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(QuickLabel(spotlight ? "暗度" : "强度"));
        row.Children.Add(spotlight
            ? QuickSlider(clip.SpotlightDimming, 0, .75, v => p => { if (p.BlurClips.FirstOrDefault(c => c.Id == id) is { } c) c.SpotlightDimming = v; }, "调整突出暗度")
            : QuickSlider(clip.Strength, 0, 1, v => p => { if (p.BlurClips.FirstOrDefault(c => c.Id == id) is { } c) c.Strength = v; }, "调整柔化强度"));
        row.Children.Add(QuickLabel("圆角"));
        row.Children.Add(QuickSlider(clip.CornerRadius, 0, .3, v => p => { if (p.BlurClips.FirstOrDefault(c => c.Id == id) is { } c) c.CornerRadius = v; }, "调整柔化圆角"));
        return row;
    }
}
