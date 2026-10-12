using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>What the user chose on the recording completion card.</summary>
internal enum RecordingCompletionChoice { Save, Edit, Trash }

/// <summary>Everything the completion card shows about a finished (already saved) recording.</summary>
internal sealed record RecordingCompletionInfo(
    string ProjectPath, string RecordingFolder, string Title,
    double DurationSeconds, int Width, int Height, BitmapSource? Thumbnail, string? Notice);

/// <summary>
/// The card shown after a recording ends. Mac: RecordingCompletionWindow.swift (card with first frame, project name,
/// "保存项目" / "进入编辑 →") and AppModel+ProjectCommands.resolveCompletedRecordingClose (X or Esc turns the same card into
/// "保留这次录制吗？" with 取消 / 移到回收站 / 保存项目; Return = 保存, Esc = back).
/// It sits at the bottom-right of the work area, is topmost, never takes focus when it appears and is excluded from capture.
/// While a choice is being carried out (saving, opening the editor, moving files to the recycle bin) all controls are disabled.
/// </summary>
internal sealed class RecordingCompletionWindow : Window
{
    private const double CardWidth = 336;
    private const double ScreenInset = 28;
    private static readonly Color DangerRed = Color.FromRgb(227, 101, 85);

    private readonly RecordingCompletionInfo info;
    private readonly Func<RecordingCompletionChoice, Task> resolve;
    private readonly Rect workArea;
    private readonly StackPanel resultPage = new();
    private readonly StackPanel decisionPage = new();
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(10, 8, 10, 2), Visibility = Visibility.Collapsed };
    private readonly TextBlock busyText = new() { FontSize = 12, Margin = new Thickness(10, 8, 10, 2), Visibility = Visibility.Collapsed };
    private readonly List<Button> buttons = [];
    private bool busy;
    private bool closed;
    private bool allowClose;
    private bool onDecisionPage;

    /// <summary>Set once a choice has been carried out successfully; null when the card was dismissed without one.</summary>
    internal RecordingCompletionChoice? ResolvedChoice { get; private set; }

    internal RecordingCompletionWindow(RecordingCompletionInfo info, Rect workArea, Func<RecordingCompletionChoice, Task> resolve)
    {
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        this.workArea = workArea.IsEmpty || workArea.Width < 200 ? SystemParameters.WorkArea : workArea;

        Title = "DogSC · 录制完成";
        Width = Math.Min(CardWidth, this.workArea.Width - 2 * 8);
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        // The card appears by itself while the user may be typing or presenting: never take focus.
        ShowActivated = false;
        SurfaceWindow.Configure(this, closeOnEscape: false, captionHeight: 0);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = this.workArea.Right - Width - ScreenInset;
        Top = this.workArea.Bottom - 300 - ScreenInset;

        BuildResultPage();
        BuildDecisionPage();
        decisionPage.Visibility = Visibility.Collapsed;
        var root = new StackPanel { Margin = new Thickness(8) };
        root.Children.Add(resultPage);
        root.Children.Add(decisionPage);
        root.Children.Add(busyText);
        root.Children.Add(errorText);
        errorText.Foreground = new SolidColorBrush(DangerRed);
        busyText.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        Content = SurfaceWindow.WrapContent(this, root, Title, showCloseButton: false, showTitleBar: false);

        SourceInitialized += (_, _) => NativeWindows.Exclude(this);
        // The bottom edge stays anchored when the page changes height.
        Loaded += (_, _) => PlaceAtCorner();
        SizeChanged += (_, _) => PlaceAtCorner();
        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += (_, e) =>
        {
            if (busy || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        Closing += OnClosing;
        Closed += (_, _) => closed = true;
    }

    /// <summary>Closes the card without a decision (application exit, or a new recording starting). The project is already on disk.</summary>
    internal void Dismiss()
    {
        if (closed) return;
        allowClose = true;
        Close();
    }

    private void PlaceAtCorner()
    {
        if (ActualHeight <= 0) return;
        Left = Math.Max(workArea.Left + 8, workArea.Right - ActualWidth - ScreenInset);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - ScreenInset);
    }

    private Button Track(Button button)
    {
        buttons.Add(button);
        return button;
    }

    private void BuildResultPage()
    {
        var previewHeight = Math.Min((CardWidth - 16) * 9 / 16, Math.Max(48, workArea.Height - 350));
        var preview = new Grid { Height = previewHeight };
        var surface = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true };
        if (info.Thumbnail is { } thumbnail)
            surface.Background = new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill };
        else
        {
            surface.SetResourceReference(Border.BackgroundProperty, "ControlSurface");
            var none = new TextBlock { Text = "预览暂不可用", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
            preview.Children.Add(none);
        }
        preview.Children.Insert(0, surface);

        // Duration and resolution sit on the picture, bottom-left.
        var meta = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = MetaText(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        preview.Children.Add(meta);

        var close = Track(OverlayCloseButton());
        close.Click += (_, _) => ShowDecision();
        preview.Children.Add(close);
        resultPage.Children.Add(preview);

        var titleRow = new DockPanel { Margin = new Thickness(6, 12, 6, 0) };
        var check = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var disc = new Ellipse();
        disc.SetResourceReference(Shape.FillProperty, "Accent");
        check.Children.Add(disc);
        check.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0 4 L 3 7 L 9 0"), Stroke = Brushes.White, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(check, Dock.Left);
        titleRow.Children.Add(check);
        var title = new TextBlock { Text = info.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = info.Title };
        titleRow.Children.Add(title);
        resultPage.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(info.Notice))
        {
            var notice = new TextBlock { Text = "⚠ " + info.Notice, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6, 6, 0), ToolTip = info.Notice };
            notice.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
            resultPage.Children.Add(notice);
        }

        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var save = Track(ActionButton("保存项目", primary: false));
        save.Click += async (_, _) => await ResolveAsync(RecordingCompletionChoice.Save);
        var edit = Track(ActionButton("进入编辑 →", primary: true));
        edit.Click += async (_, _) => await ResolveAsync(RecordingCompletionChoice.Edit);
        Grid.SetColumn(edit, 2);
        actions.Children.Add(save);
        actions.Children.Add(edit);
        resultPage.Children.Add(actions);
    }

    private void BuildDecisionPage()
    {
        var header = new DockPanel { Margin = new Thickness(4, 6, 4, 0) };
        var back = Track(new Button { Content = "←", Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0), ToolTip = "返回录制结果" });
        if (TryFindResource("PlainButton") is Style plain) back.Style = plain;
        System.Windows.Automation.AutomationProperties.SetName(back, "返回录制结果");
        back.Click += (_, _) => ShowResult();
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        header.Children.Add(new TextBlock { Text = "保留这次录制吗？", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        decisionPage.Children.Add(header);

        var message = new TextBlock { Text = "保存项目以便稍后编辑，或将这次录制移到回收站。", TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(8, 12, 8, 0), LineHeight = 20 };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        decisionPage.Children.Add(message);
        decisionPage.Children.Add(new TextBlock { Text = info.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 10, 8, 0), ToolTip = info.Title });

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 2) };
        for (var i = 0; i < 5; i++)
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(8) : new GridLength(1, GridUnitType.Star) });
        var cancel = Track(ActionButton("取消", primary: false));
        cancel.Click += (_, _) => ShowResult();
        var trash = Track(ActionButton("移到回收站", primary: false));
        trash.Foreground = new SolidColorBrush(DangerRed);
        trash.Click += async (_, _) => await ResolveAsync(RecordingCompletionChoice.Trash);
        var save = Track(ActionButton("保存项目", primary: true));
        save.Click += async (_, _) => await ResolveAsync(RecordingCompletionChoice.Save);
        Grid.SetColumn(trash, 2);
        Grid.SetColumn(save, 4);
        actions.Children.Add(cancel);
        actions.Children.Add(trash);
        actions.Children.Add(save);
        decisionPage.Children.Add(actions);
    }

    private Button ActionButton(string text, bool primary)
    {
        var button = new Button { Content = text, Height = 44, Margin = new Thickness(0), Padding = new Thickness(8, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        if (primary && TryFindResource("PrimaryButton") is Style style) button.Style = style;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    private Button OverlayCloseButton()
    {
        var content = new Grid { Width = 26, Height = 26 };
        content.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) });
        content.Children.Add(new TextBlock { Text = "✕", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "关闭录制结果" };
        if (TryFindResource("PlainButton") is Style plain) button.Style = plain;
        System.Windows.Automation.AutomationProperties.SetName(button, "关闭录制结果");
        return button;
    }

    private string MetaText()
    {
        var duration = RecordingBar.FormatElapsed(TimeSpan.FromSeconds(Math.Max(0, info.DurationSeconds)));
        return info.Width > 0 && info.Height > 0 ? $"{duration} · {info.Width}×{info.Height}" : duration;
    }

    private void ShowDecision()
    {
        if (busy) return;
        onDecisionPage = true;
        ClearError();
        resultPage.Visibility = Visibility.Collapsed;
        decisionPage.Visibility = Visibility.Visible;
        // An explicit close action is a user interaction, so taking keyboard focus here is expected.
        Activate();
    }

    private void ShowResult()
    {
        if (busy) return;
        onDecisionPage = false;
        ClearError();
        decisionPage.Visibility = Visibility.Collapsed;
        resultPage.Visibility = Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (busy) { e.Handled = true; return; }
        if (e.IsRepeat) return;
        if (e.Key == Key.Escape)
        {
            // Esc or the X open the decision; Esc on the decision goes back to the result.
            if (onDecisionPage) ShowResult(); else ShowDecision();
            e.Handled = true;
        }
        else if (e.Key == Key.Return && onDecisionPage)
        {
            _ = ResolveAsync(RecordingCompletionChoice.Save);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        // Alt+F4 or the taskbar must not silently drop the decision: it opens the same question as the X button.
        e.Cancel = true;
        if (!busy && !onDecisionPage) ShowDecision();
    }

    private async Task ResolveAsync(RecordingCompletionChoice choice)
    {
        if (busy || closed) return;
        ClearError();
        SetBusy(true, choice switch
        {
            RecordingCompletionChoice.Save => "正在保存…",
            RecordingCompletionChoice.Edit => "正在打开编辑器…",
            _ => "正在移到回收站…"
        });
        try
        {
            await resolve(choice);
            ResolvedChoice = choice;
            allowClose = true;
            if (!closed) Close();
        }
        catch (Exception error)
        {
            // The card stays up so nothing is lost; the user can retry or pick another action.
            App.Log(error);
            SetBusy(false, null);
            errorText.Text = error.Message;
            errorText.Visibility = Visibility.Visible;
        }
    }

    private void SetBusy(bool value, string? text)
    {
        busy = value;
        foreach (var button in buttons) button.IsEnabled = !value;
        busyText.Text = text ?? "";
        busyText.Visibility = value && text is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearError()
    {
        errorText.Text = "";
        errorText.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// A small, non-activating card offered once after start-up when interrupted recordings are found.
/// Mac shows "恢复中断录制" in the save popover; here the offer also appears by itself, since a crash leaves nothing else to see.
/// </summary>
internal sealed class RecoveryOfferWindow : Window
{
    private readonly Func<Task> recover;
    private readonly DispatcherTimer autoClose = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button recoverButton = new() { Content = "恢复最近一条", Height = 38, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button ignoreButton = new() { Content = "忽略", Height = 38, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0) };
    private bool busy;

    internal RecoveryOfferWindow(int count, DateTime latest, Rect workArea, Func<Task> recover)
    {
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
        var area = workArea.IsEmpty || workArea.Width < 200 ? SystemParameters.WorkArea : workArea;
        Title = "DogSC · 发现未完成的录制";
        Width = Math.Min(336, area.Width - 16);
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SurfaceWindow.Configure(this, closeOnEscape: true, captionHeight: 0);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Right - Width - 28;
        Top = area.Bottom - 160 - 28;

        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        panel.Children.Add(new TextBlock { Text = "发现未完成的录制", FontSize = 14, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock
        {
            Text = count > 1 ? $"共 {count} 条，最近一条保存于 {latest:MM-dd HH:mm}。程序可能曾意外退出，已写入的素材仍在。" : $"保存于 {latest:MM-dd HH:mm}。程序可能曾意外退出，已写入的素材仍在。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        panel.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (TryFindResource("PrimaryButton") is Style primary) recoverButton.Style = primary;
        actions.Children.Add(ignoreButton);
        actions.Children.Add(recoverButton);
        panel.Children.Add(actions);
        errorText.Foreground = new SolidColorBrush(Color.FromRgb(227, 101, 85));
        panel.Children.Add(errorText);
        Content = SurfaceWindow.WrapContent(this, panel, Title, showCloseButton: false, showTitleBar: false);

        ignoreButton.Click += (_, _) => { if (!busy) Close(); };
        recoverButton.Click += async (_, _) => await RecoverAsync();
        autoClose.Tick += (_, _) => { autoClose.Stop(); if (!busy) Close(); };
        SourceInitialized += (_, _) => NativeWindows.Exclude(this);
        Loaded += (_, _) =>
        {
            Left = Math.Max(area.Left + 8, area.Right - ActualWidth - 28);
            Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 28);
            autoClose.Start();
        };
        Closed += (_, _) => autoClose.Stop();
    }

    private async Task RecoverAsync()
    {
        if (busy) return;
        busy = true;
        autoClose.Stop();
        recoverButton.IsEnabled = false;
        ignoreButton.IsEnabled = false;
        recoverButton.Content = "正在恢复…";
        try
        {
            await recover();
            Close();
        }
        catch (Exception error)
        {
            App.Log(error);
            busy = false;
            recoverButton.IsEnabled = true;
            ignoreButton.IsEnabled = true;
            recoverButton.Content = "重试恢复";
            errorText.Text = error.Message;
            errorText.Visibility = Visibility.Visible;
        }
    }
}
