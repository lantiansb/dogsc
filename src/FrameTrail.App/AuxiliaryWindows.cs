using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameTrail.Capture;

namespace FrameTrail.App;
internal static class NativeWindows
{
    [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    public static void Exclude(Window window)
    {
        if(!SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle,0x11))
            App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法从采集中排除此辅助窗口。"));
    }
}
/// <summary>
/// The floating bar shown while a recording runs. Mac RecorderViews.swift (recording phase): breathing red dot
/// (amber and still while paused), elapsed time, marker button with count badge, memo, pause/resume, "..." menu and
/// a round stop button at the far right. Warnings/notices appear inline (warning icon + short text, folded after 8 s
/// but kept as a tooltip). The whole bar is draggable except its buttons.
/// </summary>
public sealed class RecordingBar : Window
{
    private static readonly Color RecordingRed = Color.FromRgb(227, 101, 85);
    private static readonly Color PausedAmber = Color.FromRgb(232, 163, 23);
    private readonly Ellipse dot = new() { Width = 10, Height = 10, Margin = new(4, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(RecordingRed) };
    private readonly TextBlock label = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, MinWidth = 50, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock noticeIcon = new() { Text = "⚠", FontSize = 14, Margin = new(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(PausedAmber) };
    private readonly TextBlock noticeText = new() { FontSize = 12, MaxWidth = 280, Margin = new(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer noticeTimer = new();
    private readonly Stopwatch fallbackWatch = Stopwatch.StartNew();
    private readonly Func<TimeSpan>? elapsed;
    private readonly List<string> notices = [];
    private readonly Border markBadge;
    private readonly TextBlock markBadgeText = new() { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 3, 0) };
    private readonly Button pause = new() { Width = 36, Height = 36, Padding = new(0), Margin = new(2, 0, 2, 0), ToolTip = "暂停录制" };
    private readonly Button end;
    private readonly Button mark;
    private readonly Button? menu;
    private readonly Button? memo;
    private readonly Func<Task> stop;
    private readonly Action<Exception>? stopFailed;
    private bool stopInProgress;
    private bool closeAfterRecording;
    private string? stopStatus;
    private bool paused;
    private bool labelShowsPaused;
    private string markBaseTip = "";

    public RecordingBar(Action addMarker, Func<Task> stop, Func<Task> togglePause, Action<Exception>? stopFailed = null, string? markerShortcut = "Ctrl+Alt+M", bool markerShortcutIsFallback = false, Action<Button>? showMenu = null, Action? showMemo = null, Func<TimeSpan>? elapsed = null)
    {
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop)); this.stopFailed = stopFailed; this.elapsed = elapsed;
        Title = "DogSC · 录制条"; Topmost = true; ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight;
        SurfaceWindow.Configure(this, closeOnEscape: false, captionHeight: 0);
        // First appears where the recorder setup bar was (Mac keeps one panel frame for both phases), then remembers its own spot.
        FloatingWindowPlacement.Attach(this, "recording-bar", "recorder");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(12, 7, 8, 7) };
        Content = SurfaceWindow.WrapContent(this, panel, Title, showCloseButton: false, showTitleBar: false);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        noticeText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        panel.Children.Add(dot); panel.Children.Add(label); panel.Children.Add(noticeIcon); panel.Children.Add(noticeText);

        var markGrid = new Grid { Width = 36, Height = 36 };
        var flag = new EditorIcon { Kind = "flag", Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        flag.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
        markGrid.Children.Add(flag);
        markBadge = new Border { MinWidth = 15, Height = 15, CornerRadius = new(7.5), Background = new SolidColorBrush(RecordingRed), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = markBadgeText };
        markGrid.Children.Add(markBadge);
        markBaseTip = markerShortcut == null ? "标记快捷键暂不可用，点击此按钮添加标记。" : markerShortcutIsFallback ? "Ctrl+Alt+M 已被占用，使用 " + markerShortcut + " 或点击添加标记。" : "添加标记 · " + markerShortcut;
        mark = new Button { Content = markGrid, Width = 36, Height = 36, Padding = new(0), Margin = new(2, 0, 2, 0), ToolTip = markBaseTip };
        var plain = TryFindResource("PlainButton") as Style;
        if (plain is not null) { mark.Style = plain; pause.Style = plain; }
        System.Windows.Automation.AutomationProperties.SetName(mark, "添加标记"); System.Windows.Automation.AutomationProperties.SetAutomationId(mark, "RecordingAddMarker"); System.Windows.Automation.AutomationProperties.SetHelpText(mark, mark.ToolTip.ToString());
        mark.Click += (_, _) => addMarker(); panel.Children.Add(mark);

        if (showMemo is not null)
        {
            var memoIcon = new EditorIcon { Kind = "memo", Width = 18, Height = 18 };
            memoIcon.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
            memo = new Button { Content = memoIcon, Width = 36, Height = 36, Padding = new(0), Margin = new(2, 0, 2, 0), ToolTip = "口播提词器" };
            if (plain is not null) memo.Style = plain;
            System.Windows.Automation.AutomationProperties.SetName(memo, "口播提词器"); System.Windows.Automation.AutomationProperties.SetAutomationId(memo, "RecordingMemo");
            memo.Click += (_, _) => showMemo(); panel.Children.Add(memo);
        }

        pause.Content = PauseGlyph(false); System.Windows.Automation.AutomationProperties.SetName(pause, "暂停录制"); System.Windows.Automation.AutomationProperties.SetAutomationId(pause, "RecordingPause");
        pause.Click += async (_, _) => { pause.IsEnabled = false; try { await togglePause(); } finally { pause.IsEnabled = !stopInProgress; } }; panel.Children.Add(pause);

        if (showMenu is not null)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++)
            {
                var spot = new Ellipse { Width = 3.5, Height = 3.5, Margin = new(1.5, 0, 1.5, 0) };
                spot.SetResourceReference(Shape.FillProperty, "Secondary"); dots.Children.Add(spot);
            }
            var menuButton = new Button { Content = dots, Width = 32, Height = 36, Padding = new(0), Margin = new(2, 0, 2, 0), ToolTip = "更多录制操作" };
            if (plain is not null) menuButton.Style = plain;
            System.Windows.Automation.AutomationProperties.SetName(menuButton, "更多录制操作");
            System.Windows.Automation.AutomationProperties.SetAutomationId(menuButton, "RecordingBarMenu");
            menuButton.Click += (_, _) => showMenu(menuButton);
            menu = menuButton; panel.Children.Add(menuButton);
        }

        // The one solid, coloured control: a round stop button at the far right.
        var stopGrid = new Grid { Width = 36, Height = 36 };
        stopGrid.Children.Add(new Ellipse { Fill = new SolidColorBrush(RecordingRed) });
        stopGrid.Children.Add(new Rectangle { Width = 11, Height = 11, RadiusX = 2.5, RadiusY = 2.5, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        end = new Button { Content = stopGrid, Width = 36, Height = 36, Padding = new(0), Margin = new(6, 0, 0, 0), ToolTip = "结束录制" };
        if (plain is not null) end.Style = plain;
        System.Windows.Automation.AutomationProperties.SetName(end, "结束录制"); System.Windows.Automation.AutomationProperties.SetAutomationId(end, "RecordingStop");
        end.Click += async (_, _) => await RequestStopAsync(); panel.Children.Add(end);

        timer.Tick += (_, _) => { if (!stopInProgress) RefreshLabel(); }; RefreshLabel(); timer.Start();
        noticeTimer.Tick += (_, _) => FoldNotice();
        Closing += OnClosing; SourceInitialized += (_, _) => NativeWindows.Exclude(this); Closed += (_, _) => { timer.Stop(); noticeTimer.Stop(); dot.BeginAnimation(OpacityProperty, null); };
        Loaded += (_, _) => UpdateDot();
        // Drag from anywhere on the bar; buttons mark their own mouse-down as handled so they never start a drag.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1) { try { DragMove(); } catch (InvalidOperationException) { } } };
    }

    private static EditorIcon PauseGlyph(bool resume)
    {
        var icon = new EditorIcon { Kind = resume ? "play" : "pause", Width = 18, Height = 18 };
        icon.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
        return icon;
    }

    /// <summary>"mm:ss", or "h:mm:ss" from one hour on (Mac RecorderViews.elapsedText: "75:03" no longer looks like a duration).</summary>
    internal static string FormatElapsed(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeAfterRecording) return;
        e.Cancel = true;
        _ = Dispatcher.BeginInvoke(new Action(async () => await RequestStopAsync()));
    }

    private async Task RequestStopAsync()
    {
        if (stopInProgress || closeAfterRecording) return;
        stopInProgress = true; stopStatus = "正在结束录制…"; SetControlsEnabled(false); label.Text = stopStatus;
        try
        {
            await stop();
            CloseAfterRecording();
        }
        catch (Exception error)
        {
            stopStatus = "结束失败 · 可重试"; label.Text = stopStatus;
            try { stopFailed?.Invoke(error); } catch (Exception notificationError) { App.Log(notificationError); }
        }
        finally
        {
            stopInProgress = false;
            if (IsVisible && !closeAfterRecording) SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        end.IsEnabled = enabled; pause.IsEnabled = enabled; mark.IsEnabled = enabled && !paused;
        if (menu is not null) menu.IsEnabled = enabled;
        if (memo is not null) memo.IsEnabled = true;
    }

    public void CloseAfterRecording()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(CloseAfterRecording); return; }
        if (closeAfterRecording) return;
        closeAfterRecording = true;
        Close();
    }

    private void RefreshLabel()
    {
        var time = elapsed?.Invoke() ?? fallbackWatch.Elapsed;
        label.Text = stopStatus ?? FormatElapsed(time);
        var showPaused = paused && stopStatus is null;
        if (showPaused != labelShowsPaused) { labelShowsPaused = showPaused; label.SetResourceReference(TextBlock.ForegroundProperty, showPaused ? "Secondary" : "Ink"); }
    }

    public void SetMarkerCount(int value)
    {
        markBadgeText.Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        markBadge.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
        mark.ToolTip = markBaseTip + (value > 0 ? $"\n已添加 {value} 个标记" : "");
    }

    public void SetPaused(bool value)
    {
        paused = value;
        if (value) fallbackWatch.Stop(); else fallbackWatch.Start();
        pause.Content = PauseGlyph(value); pause.ToolTip = value ? "继续录制" : "暂停录制";
        System.Windows.Automation.AutomationProperties.SetName(pause, value ? "继续录制" : "暂停录制");
        mark.IsEnabled = !value && !stopInProgress; UpdateDot(); RefreshLabel();
    }

    /// <summary>Red dot that breathes while recording; amber and still while paused (Mac RecordingIndicator).</summary>
    private void UpdateDot()
    {
        dot.BeginAnimation(OpacityProperty, null); dot.Opacity = 1;
        dot.Fill = new SolidColorBrush(paused ? PausedAmber : RecordingRed);
        if (paused || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .3, TimeSpan.FromSeconds(.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }

    /// <summary>Shows a short inline message (warning icon + text). It folds after 8 s (3 s when not a warning) but stays as a tooltip.</summary>
    public void ShowNotice(string message, bool warning = true)
    {
        if (string.IsNullOrWhiteSpace(message) || closeAfterRecording) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ShowNotice(message, warning))); return; }
        if (warning) { notices.Remove(message); notices.Add(message); if (notices.Count > 3) notices.RemoveAt(0); }
        var tip = warning ? string.Join("\n", notices) : message;
        noticeText.Text = message; noticeText.ToolTip = tip; noticeText.Visibility = Visibility.Visible;
        noticeIcon.ToolTip = tip; noticeIcon.Visibility = warning || notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        noticeTimer.Stop(); noticeTimer.Interval = TimeSpan.FromSeconds(warning ? 8 : 3); noticeTimer.Start();
    }

    private void FoldNotice()
    {
        noticeTimer.Stop();
        noticeText.Visibility = Visibility.Collapsed;
        if (notices.Count == 0) noticeIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>Replaces the clock with a progress phrase such as "正在整理素材…" and locks the controls until the bar closes.</summary>
    public void SetBusy(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetBusy(text)); return; }
        if (closeAfterRecording) return;
        stopStatus = text; label.Text = text; SetControlsEnabled(false);
    }
}
public sealed class MemoWindow : Window
{
    private const string SaveFailureTitle = "备忘录保存失败";
    private const string SaveFailureMessage = "备忘录正文或外观设置未能保存，窗口仍会保持打开，当前文字也会保留在窗口中。请检查文件权限或磁盘空间，然后再次关闭窗口重试。";
    private readonly string file;
    private readonly string preferences;
    private readonly TextBox input;
    private readonly TextBlock saveStatus;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer scrollTimer;
    private readonly ScrollViewer readerScroll;
    private readonly TextBlock readerText;
    private readonly TextBlock speedLabel;
    private readonly Slider speedSlider;
    private readonly Border shell;
    private readonly Border backdrop;
    private readonly Grid footer;
    private readonly Button scrollButton;
    private readonly Button editButton;
    private readonly Button readButton;
    private readonly Button rewindButton;
    private readonly Action<Window, string, string> showSaveFailure;
    private readonly Action<Exception> errorLog;
    private bool isEditing = true;
    private bool isScrolling;
    private long lastScrollTimestamp;
    private double memoBackgroundOpacity = .92;
    private double scrollSpeed = DefaultScrollSpeed;
    private bool hideAfterSave;
    private const double DefaultScrollSpeed = 45;

    public MemoWindow()
        : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "memo.txt"),
            (owner, message, title) => SurfaceDialog.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning)) { }

    internal MemoWindow(string filePath, Action<Window, string, string>? showSaveFailure = null, Action<Exception>? errorLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        file = filePath;
        preferences = MemoStorage.SettingsPath(file);
        this.showSaveFailure = showSaveFailure ?? ((owner, message, title) =>
            SurfaceDialog.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
        this.errorLog = errorLog ?? App.Log;

        Title = "DogSC · 口播提词器";
        Width = 410;
        Height = 400;
        MinWidth = 390;
        MinHeight = 330;
        Topmost = true;
        ShowInTaskbar = false;
        Opacity = 1;
        input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 18,
            Margin = new Thickness(10),
            Background = Brushes.Transparent,
            // An empty memo shows a placeholder instead of real text (Mac RecorderMemo: "写下或粘贴要讲的内容…").
            // Older builds saved their old "录制提纲…" filler as the memo text; treat that as empty.
            Text = File.Exists(file) ? LegacyFillerAsEmpty(File.ReadAllText(file)) : ""
        };
        try
        {
            if (File.Exists(preferences))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(preferences));
                if (saved is { Length: >= 4 })
                {
                    input.FontSize = Math.Clamp(saved[0], 12, 48);
                    memoBackgroundOpacity = Math.Clamp(saved[1], .55, 1);
                    Width = Math.Clamp(saved[2], 390, 1000);
                    Height = Math.Clamp(saved[3], 330, 1000);
                    if (saved.Length >= 6)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = Math.Clamp(saved[4], SystemParameters.VirtualScreenLeft,
                            SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
                        Top = Math.Clamp(saved[5], SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
                    }
                    var teleprompter = MemoStorage.ReadTeleprompterSettings(saved,
                        input.Text.Length == 0, DefaultScrollSpeed);
                    isEditing = teleprompter.IsEditing;
                    scrollSpeed = teleprompter.ScrollSpeed;
                }
            }
        }
        catch (Exception ex) { this.errorLog(ex); }

        var root = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 12, 0) };
        controls.Children.Add(new TextBlock { Text = "字号", VerticalAlignment = VerticalAlignment.Center });
        var size = new Slider { Minimum = 16, Maximum = 40, Value = Math.Clamp(input.FontSize, 16, 40), Width = 90, Margin = new Thickness(8, 0, 9, 0), IsSnapToTickEnabled = true, TickFrequency = 1 };
        var sizeLabel = new TextBlock { Text = ((int)size.Value).ToString(), Width = 22, VerticalAlignment = VerticalAlignment.Center };
        input.FontSize = size.Value;
        size.ValueChanged += (_, _) => { input.FontSize = size.Value; if (readerText is not null) { readerText.FontSize = size.Value; readerText.LineHeight = size.Value * 1.45; } sizeLabel.Text = ((int)size.Value).ToString(); };
        controls.Children.Add(size);
        controls.Children.Add(sizeLabel);
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        editButton = CreateModeButton("编辑");
        readButton = CreateModeButton("阅读");
        editButton.Click += (_, _) => SetEditingMode(true);
        readButton.Click += (_, _) => SetEditingMode(false);
        mode.Children.Add(editButton);
        mode.Children.Add(readButton);
        controls.Children.Add(mode);
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);

        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 3), VerticalAlignment = VerticalAlignment.Center };
        opacityRow.Children.Add(new TextBlock { Text = "背景不透明度", VerticalAlignment = VerticalAlignment.Center });
        var opacitySlider = new Slider { Minimum = .55, Maximum = 1, Value = Math.Clamp(memoBackgroundOpacity, .55, 1), Width = 145, Margin = new Thickness(8, 0, 5, 0) };
        var opacityLabel = new TextBlock { Text = $"{opacitySlider.Value:P0}", MinWidth = 34, VerticalAlignment = VerticalAlignment.Center };
        opacitySlider.ValueChanged += (_, _) => { memoBackgroundOpacity = opacitySlider.Value; opacityLabel.Text = $"{opacitySlider.Value:P0}"; ApplyBackgroundOpacity(); };
        opacityRow.Children.Add(opacitySlider);
        opacityRow.Children.Add(opacityLabel);
        DockPanel.SetDock(opacityRow, Dock.Top);
        root.Children.Add(opacityRow);

        readerText = new TextBlock
        {
            Text = input.Text,
            FontSize = input.FontSize,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = input.FontSize * 1.45,
            Margin = new Thickness(12),
            Focusable = false
        };
        readerScroll = new ScrollViewer
        {
            Content = readerText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = true,
            Margin = new Thickness(10),
            Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible
        };
        input.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        var textHost = new Grid();
        textHost.Children.Add(readerScroll);
        textHost.Children.Add(input);
        var placeholder = new TextBlock { Text = "写下或粘贴要讲的内容…", IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 11, 14, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Opacity = .55 };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        placeholder.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(TextBox.FontSize)) { Source = input });
        void UpdatePlaceholder() => placeholder.Visibility = input.Text.Length == 0 && input.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => UpdatePlaceholder();
        input.IsVisibleChanged += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
        textHost.Children.Add(placeholder);

        footer = new Grid { Margin = new Thickness(12, 3, 12, 8) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        scrollButton = CreateModeButton("开始滚动");
        scrollButton.MinWidth = 82;
        scrollButton.Click += (_, _) => ToggleScrolling();
        rewindButton = CreateModeButton("回到开头");
        rewindButton.Margin = new Thickness(5, 0, 0, 0);
        rewindButton.Click += (_, _) => Rewind();
        actions.Children.Add(scrollButton);
        actions.Children.Add(rewindButton);
        Grid.SetRow(actions, 0);
        footer.Children.Add(actions);
        var speedRow = new Grid { Margin = new Thickness(3, 5, 3, 0) };
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var speedTitle = new TextBlock { Text = "速度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        speedSlider = new Slider { Minimum = 8, Maximum = 180, Value = scrollSpeed, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = "拖动调整提词器滚动速度" };
        speedLabel = new TextBlock { Text = $"{speedSlider.Value:0}", MinWidth = 34, VerticalAlignment = VerticalAlignment.Center, ToolTip = "滚动速度，数值越大越快" };
        speedSlider.ValueChanged += (_, _) => speedLabel.Text = $"{speedSlider.Value:0}";
        System.Windows.Automation.AutomationProperties.SetName(speedSlider, "提词器速度");
        Grid.SetColumn(speedTitle, 0);
        Grid.SetColumn(speedSlider, 1);
        Grid.SetColumn(speedLabel, 2);
        speedRow.Children.Add(speedTitle);
        speedRow.Children.Add(speedSlider);
        speedRow.Children.Add(speedLabel);
        Grid.SetRow(speedRow, 1);
        footer.Children.Add(speedRow);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        saveStatus = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(14, 3, 14, 0),
            Padding = new Thickness(5, 2, 5, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        saveStatus.SetResourceReference(TextBlock.BackgroundProperty, "Chrome");
        System.Windows.Automation.AutomationProperties.SetName(saveStatus, "备忘录保存状态");
        DockPanel.SetDock(saveStatus, Dock.Top);
        root.Children.Add(saveStatus);
        root.Children.Add(textHost);
        SurfaceWindow.Configure(this, resizable: true, centerOnOwner: false, closeOnEscape: false);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        shell = (Border)SurfaceWindow.WrapContent(this, root, "DogSC · 口播提词器");
        Content = shell;
        shell.Background = Brushes.Transparent;
        var shellGrid = (Grid)shell.Child;
        backdrop = new Border { CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
        backdrop.SetResourceReference(Border.BackgroundProperty, "Chrome");
        Grid.SetRowSpan(backdrop, shellGrid.RowDefinitions.Count);
        Grid.SetZIndex(backdrop, -1);
        shellGrid.Children.Insert(0, backdrop);
        ApplyBackgroundOpacity();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => SaveTextAfterEdit();
        input.TextChanged += (_, _) =>
        {
            readerText.Text = input.Text;
            timer.Stop();
            timer.Start();
        };
        scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        scrollTimer.Tick += (_, _) => AdvanceScroll();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && !hideAfterSave)
            {
                StopScrolling();
                timer.Stop();
                SaveSnapshotSafely(showFailureDialog: false);
            }
        };
        Closing += (_, args) => SaveBeforeClose(args);
        PreviewKeyDown += HandleTeleprompterKey;
        SourceInitialized += (_, _) => NativeWindows.Exclude(this);
        Closed += (_, _) =>
        {
            timer.Stop();
            scrollTimer.Stop();
            isScrolling = false;
        };
        UpdateModeButtons();
    }

    private static string LegacyFillerAsEmpty(string text) => text == "录制提纲…" ? "" : text;

    private Button CreateModeButton(string label)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 54,
            Height = 28,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(2, 0, 2, 0),
            Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 150, 160, 165)),
            Foreground = (Brush)Application.Current.FindResource("Ink")
        };
        button.SetResourceReference(Control.ForegroundProperty, "Ink");
        return button;
    }

    private void UpdateModeButtons()
    {
        editButton.FontWeight = isEditing ? FontWeights.Bold : FontWeights.Normal;
        readButton.FontWeight = isEditing ? FontWeights.Normal : FontWeights.Bold;
    }

    internal void SetEditingMode(bool editing)
    {
        if (isEditing == editing) return;
        StopScrolling();
        isEditing = editing;
        input.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        readerScroll.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        if (!editing) readerText.Text = input.Text;
        UpdateModeButtons();
        SaveSnapshotSafely(showFailureDialog: false);
        if (!editing) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => readerScroll.Focus()));
    }

    internal bool TryHideMemo()
    {
        StopScrolling();
        if (!SaveSnapshotSafely(showFailureDialog: true)) return false;
        hideAfterSave = true;
        Hide();
        hideAfterSave = false;
        return true;
    }

    private void ToggleScrolling()
    {
        if (isEditing) SetEditingMode(false);
        if (isScrolling) StopScrolling();
        else
        {
            if (readerScroll.ScrollableHeight - readerScroll.VerticalOffset <= .5) Rewind();
            isScrolling = true;
            lastScrollTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            scrollTimer.Start();
        }
        scrollButton.Content = isScrolling ? "暂停" : "开始滚动";
    }

    private void StopScrolling()
    {
        isScrolling = false;
        scrollTimer.Stop();
        lastScrollTimestamp = 0;
        scrollButton.Content = "开始滚动";
    }

    private void AdvanceScroll()
    {
        if (!isScrolling || !IsVisible || isEditing) { StopScrolling(); return; }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(lastScrollTimestamp, now).TotalSeconds;
        lastScrollTimestamp = now;
        var advance = MemoAutoScroll.Advance(readerScroll.VerticalOffset, readerScroll.ScrollableHeight, speedSlider.Value, elapsed);
        if (advance.ReachedEnd)
        {
            readerScroll.ScrollToVerticalOffset(advance.Offset);
            StopScrolling();
            return;
        }
        readerScroll.ScrollToVerticalOffset(advance.Offset);
    }

    private void Rewind()
    {
        StopScrolling();
        readerScroll.ScrollToTop();
    }

    private void HandleTeleprompterKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (isEditing || !readerScroll.IsKeyboardFocusWithin || e.IsRepeat) return;
        if (e.Key == System.Windows.Input.Key.Space)
        {
            ToggleScrolling();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Home)
        {
            Rewind();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            StopScrolling();
        }
    }

    private void ApplyBackgroundOpacity()
    {
        backdrop.Opacity = memoBackgroundOpacity;
    }

    private bool SaveSnapshotSafely(bool showFailureDialog)
    {
        timer.Stop();
        try
        {
            if (!double.IsFinite(Left) || !double.IsFinite(Top))
            {
                var workArea = SurfaceWindow.WorkArea(this);
                if (!double.IsFinite(Left)) Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
                if (!double.IsFinite(Top)) Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
            }
            MemoStorage.SaveSnapshot(file, input.Text, MemoStorage.CreateSettings(
                input.FontSize, memoBackgroundOpacity, Width, Height, Left, Top, isEditing, speedSlider?.Value ?? scrollSpeed));
            ClearSaveStatus();
            return true;
        }
        catch (Exception ex)
        {
            errorLog(ex);
            SetSaveStatus("保存失败，文字仍保留；再次操作可重试。", ex.Message);
            if (showFailureDialog) showSaveFailure(this, SaveFailureMessage, SaveFailureTitle);
            return false;
        }
    }

    private void SaveTextAfterEdit()
    {
        timer.Stop();
        try
        {
            MemoStorage.SaveText(file, input.Text);
            ClearSaveStatus();
        }
        catch (Exception ex)
        {
            errorLog(ex);
            SetSaveStatus("自动保存失败，关闭备忘录时可重试。", ex.Message);
        }
    }

    private void SaveBeforeClose(CancelEventArgs args)
    {
        StopScrolling();
        if (!SaveSnapshotSafely(showFailureDialog: true)) args.Cancel = true;
    }

    private void SetSaveStatus(string message, string detail)
    {
        saveStatus.Text = message;
        saveStatus.ToolTip = detail;
        saveStatus.Visibility = Visibility.Visible;
    }

    private void ClearSaveStatus()
    {
        saveStatus.Text = string.Empty;
        saveStatus.ToolTip = null;
        saveStatus.Visibility = Visibility.Collapsed;
    }

    internal TextBox EditorForProbe => input;
    internal TextBlock SaveStatusForProbe => saveStatus;
    internal bool AutoSaveTimerEnabledForProbe => timer.IsEnabled;
    internal bool IsScrollingForProbe => isScrolling;
    internal double ScrollSpeedForProbe => speedSlider.Value;
    internal double BackgroundOpacityForProbe => memoBackgroundOpacity;
    internal void SetBackgroundOpacityForProbe(double value)
    {
        memoBackgroundOpacity = Math.Clamp(value, .55, 1);
        ApplyBackgroundOpacity();
    }
    internal bool IsEditingForProbe => isEditing;
    internal FrameworkElement FooterForProbe => footer;
    internal Button ScrollButtonForProbe => scrollButton;
    internal Button RewindButtonForProbe => rewindButton;
    internal Slider SpeedSliderForProbe => speedSlider;
    internal TextBlock SpeedLabelForProbe => speedLabel;
    internal ScrollViewer ReaderForProbe => readerScroll;
    internal void SaveTextForProbe() => SaveTextAfterEdit();
    internal bool TryCloseForProbe()
    {
        var args = new CancelEventArgs();
        SaveBeforeClose(args);
        return !args.Cancel;
    }
}
public sealed class RegionSelector : Window
{
    public PixelRect? Region {get;private set;}
    private readonly Canvas canvas=new();
    private readonly Rectangle selection=new() { Stroke=Brushes.LightGreen,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(45,130,240,170)) };
    private Point start; private NativeWindows.POINT physicalStart; private bool dragging;
    public RegionSelector(PixelRect display)
    {
        Title="拖动框选区域，Esc 取消"; WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=new SolidColorBrush(Color.FromArgb(90,0,0,0)); Topmost=true; ShowInTaskbar=false; Cursor=Cursors.Cross; Content=canvas;
        canvas.Children.Add(new TextBlock { Text="拖动框选录制区域 · Esc 取消",FontSize=22,Foreground=Brushes.White,Margin=new(30) }); canvas.Children.Add(selection);
        SourceInitialized+=(_,_)=> { NativeWindows.SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),display.X,display.Y,display.Width,display.Height,0x0040); NativeWindows.Exclude(this); };
        MouseLeftButtonDown+=(_,e)=>{start=e.GetPosition(canvas);NativeWindows.GetCursorPos(out physicalStart);dragging=true;CaptureMouse();};
        MouseMove+=(_,e)=>{if(!dragging)return;var p=e.GetPosition(canvas);Canvas.SetLeft(selection,Math.Min(start.X,p.X));Canvas.SetTop(selection,Math.Min(start.Y,p.Y));selection.Width=Math.Abs(p.X-start.X);selection.Height=Math.Abs(p.Y-start.Y);};
        MouseLeftButtonUp+=(_,_)=>{if(!dragging)return;dragging=false;ReleaseMouseCapture();NativeWindows.GetCursorPos(out var end);var x=Math.Max(display.X,Math.Min(physicalStart.X,end.X));var y=Math.Max(display.Y,Math.Min(physicalStart.Y,end.Y));var right=Math.Min(display.X+display.Width,Math.Max(physicalStart.X,end.X));var bottom=Math.Min(display.Y+display.Height,Math.Max(physicalStart.Y,end.Y));if(right-x<16||bottom-y<16){DialogResult=false;return;}Region=new(x,y,(right-x)/2*2,(bottom-y)/2*2);DialogResult=true;};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)DialogResult=false;};
    }
}
