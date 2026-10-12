using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.App;

/// <summary>DogSC 风格的独立导出面板。执行和进度均由调用方提供的导出引擎负责。</summary>
public sealed partial class ExportWindow : Window
{
    private readonly FrameTrailProject project;
    private readonly ExportRangeInfo? range;
    private readonly string projectName;
    private bool exportFull;
    private readonly ImageSource? preview;
    private readonly Func<string, ExportOptions, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> execute;
    private readonly TextBox fileName = new();
    private readonly TextBlock extensionBadge = new();
    private readonly TextBlock directoryText = new();
    private readonly TextBlock previewFormat = new();
    private readonly TextBlock previewSize = new();
    private readonly TextBlock previewRate = new();
    private readonly TextBlock previewDuration = new();
    private readonly TextBlock stageText = new();
    private readonly TextBlock timeText = new();
    private readonly TextBlock progressValue = new();
    private readonly TextBlock estimate = new();
    private readonly ProgressBar progressBar = new();
    private readonly List<Button> resolutionChoices = new();
    private readonly List<Button> frameRateChoices = new();
    private readonly CheckBox hardware = new();
    private readonly Button exportButton = new();
    private readonly Button videoModeButton = new();
    private readonly Button audioModeButton = new();
    private readonly Button cancelButton = new();
    private readonly Button openFileButton = new();
    private readonly Button openFolderButton = new();
    private readonly Button closeButton = new();
    private readonly Button browseButton = new();
    private readonly Button errorDetailsButton = new();
    private readonly TextBlock titleText = new();
    private readonly TextBlock nameNote = new();
    private readonly TextBlock rangeText = new();
    private readonly CheckBox fullToggle = new();
    private readonly CheckBox soundToggle = new();
    private readonly StackPanel rangePanel = new();
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch exportClock = new();
    private TimeSpan? lastRemaining;
    private TimeSpan lastRemainingAt;
    private DateTime lastAdvanceAt;
    private double lastFraction = -1;
    private string lastStage = "";
    private Task? exportTask;
    private bool customName;
    private bool initializing = true;
    private bool suppressNameEvents;
    private bool forceClose;
    private bool playCompletionSound = true;
    /// <summary>Test hook: answers the "cancel running export?" question without showing a dialog.</summary>
    internal Func<bool>? ConfirmCancelOnClose;
    private bool audioOnly;
    private bool running;
    private bool closeAfterCancellation;
    private long exportGeneration;
    private int resolutionIndex;
    private int frameRateIndex = 1;
    private string outputDirectory;
    private string? completedPath;
    private string? exportErrorDetails;
    private CancellationTokenSource? cancellation;

    /// <summary>True while an export job is running (the main window uses this to defer close/open/exit).</summary>
    internal bool IsExporting => running;

    /// <summary>The project the next export will render: the trimmed range snapshot, or the full project.</summary>
    internal FrameTrailProject ActiveProject => exportFull && range is not null ? range.FullProject : project;

    public ExportWindow(FrameTrailProject project, ImageSource? preview, string projectName,
        Func<string, ExportOptions, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> execute,
        bool initialAudioOnly = false, ExportRangeInfo? range = null)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.range = range;
        this.projectName = projectName;
        this.preview = preview;
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        // Mac replaceExportSettings: the last export choices are remembered (outside the undo history);
        // defaults are original resolution + 60 fps. Stored in the user preferences here.
        var preferences = MainWindow.ReadUserPreferences();
        resolutionIndex = Math.Clamp(preferences.ExportResolutionIndex, 0, 3);
        frameRateIndex = preferences.ExportFrameRate == 30 ? 0 : 1;
        playCompletionSound = preferences.ExportCompletionSound;
        outputDirectory = ExportLocation.Resolve(preferences.ExportFolder);
        audioOnly = initialAudioOnly;
        Title = (range is null ? "导出作品 · " : "导出选区 · ") + projectName;
        Width = 840;
        Height = 700;
        SurfaceWindow.Configure(this, closeOnEscape: true, resizable: true, captionHeight: 0, canClose: () => !running);
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        suppressNameEvents = true;
        fileName.Text = DefaultStem();
        suppressNameEvents = false;

        Content = BuildLayout(projectName);
        hardware.IsChecked = preferences.ExportHardware;
        soundToggle.IsChecked = preferences.ExportCompletionSound;
        RefreshPreview();
        UpdateMode();
        initializing = false;
        progressTimer.Tick += (_, _) => OnProgressTimer();
        Closing += OnWindowClosing;
        Closed += (_, _) => progressTimer.Stop();
        // Esc closes the panel only while idle (SurfaceWindow canClose); a running export is cancelled
        // solely by the "取消导出" button, like the Mac ExportSheet.
    }

    /// <summary>Cancels a running export (no confirmation), waits for engine cleanup and closes the window.</summary>
    internal async Task CancelAndCloseAsync()
    {
        forceClose = true;
        if (running)
        {
            CancelExport();
            if (exportTask is { } task) { try { await task; } catch { } }
        }
        if (IsLoaded || IsVisible) Close();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!running || forceClose) return;
        // Closing mid-export needs an explicit confirmation (Mac: the sheet cannot be dismissed).
        var confirmed = ConfirmCancelOnClose?.Invoke()
            ?? SurfaceDialog.Show(this, "正在导出。关闭窗口会取消导出并清理未完成的文件，确定取消导出吗？", "取消导出", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!running) { if (!confirmed) e.Cancel = true; return; } // finished while the question was open
        e.Cancel = true;
        if (!confirmed) return;
        closeAfterCancellation = true;
        CancelExport();
    }

    private UIElement BuildLayout(string projectName)
    {
        var root = new Grid { Background = Brush("Workspace") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });

        var header = new Grid { Background = Brush("Chrome"), Margin = new Thickness(0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var icon = new EditorIcon { Kind = "export", Width = 19, Height = 19, Stroke = Brush("Ink"), Opacity = .72, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(-2.5, 0, 0, 0) };
        header.Children.Add(icon);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleText.Text = range is null ? "导出作品" : "导出选区"; titleText.FontSize = 20; titleText.FontWeight = FontWeights.Medium; titleStack.Children.Add(titleText);
        titleStack.Children.Add(new TextBlock { Text = projectName, FontSize = 12, Foreground = Brush("Secondary"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(titleStack, 1); header.Children.Add(titleStack);
        closeButton.Width = 36; closeButton.Height = 36; closeButton.Style = FindStyle("PlainButton"); closeButton.Padding = new Thickness(0);
        closeButton.Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brush("ControlSurface"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), Child = new EditorIcon { Kind = "close", Width = 12, Height = 12, Stroke = Brush("Secondary"), Opacity = .68, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        closeButton.Click += (_, _) => Close(); System.Windows.Automation.AutomationProperties.SetName(closeButton, "关闭导出窗口"); Grid.SetColumn(closeButton, 2); header.Children.Add(closeButton);
        Border headerBorder = new() { Child = header, Background = Brush("Chrome"), Padding = new Thickness(22, 0, 18, 0), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.OriginalSource is not Button) DragMove(); };
        Grid.SetRow(headerBorder, 0); root.Children.Add(headerBorder);

        var body = new Grid { Background = Brush("Surface") };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(432) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(408) });
        var left = new StackPanel { Margin = new Thickness(30, 9, 29, 5) };
        left.Children.Add(Label("文件名"));
        var nameRow = new Grid { Height = 44 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition()); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileName.Margin = new Thickness(0); fileName.Padding = new Thickness(13, 1, 4, 1); fileName.Height = 44; fileName.MinHeight = 0; fileName.VerticalContentAlignment = VerticalAlignment.Center; fileName.Background = Brushes.Transparent; fileName.FontSize = 13; fileName.TextChanged += (_, _) => OnFileNameChanged();
        System.Windows.Automation.AutomationProperties.SetName(fileName, "导出文件名");
        nameRow.Children.Add(fileName);
        var nameShell = new Border { Background = Brush("Chrome"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 0, 5, 0) };
        nameShell.Child = nameRow;
        var badge = new Border { Background = Brush("ControlSurface"), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Child = extensionBadge; Grid.SetColumn(badge, 1); nameRow.Children.Add(badge);
        var editName = new Button { Style = FindStyle("PlainButton"), Padding = new Thickness(4), Margin = new Thickness(3, 0, 2, 0), ToolTip = "编辑文件名" };
        editName.Content = new EditorIcon { Kind = "pencil", Width = 14, Height = 14, Stroke = Brush("Secondary") }; editName.Click += (_, _) => { fileName.Focus(); fileName.SelectAll(); };
        System.Windows.Automation.AutomationProperties.SetName(editName, "编辑文件名"); Grid.SetColumn(editName, 2); nameRow.Children.Add(editName);
        left.Children.Add(nameShell);
        nameNote.FontSize = 11; nameNote.TextWrapping = TextWrapping.Wrap; nameNote.Margin = new Thickness(2, 6, 0, 0); nameNote.MinHeight = 16;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(nameNote, System.Windows.Automation.AutomationLiveSetting.Polite);
        left.Children.Add(nameNote);

        var dirRow = new Grid { Height = 36, Margin = new Thickness(0, 8, 0, 0) };
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); dirRow.ColumnDefinitions.Add(new ColumnDefinition()); dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderIcon = new EditorIcon { Kind = "folder", Width = 16, Height = 16, Stroke = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 9, 0) }; dirRow.Children.Add(folderIcon);
        directoryText.TextTrimming = TextTrimming.CharacterEllipsis; directoryText.VerticalAlignment = VerticalAlignment.Center; directoryText.Foreground = Brush("Secondary"); System.Windows.Automation.AutomationProperties.SetName(directoryText, "保存到"); Grid.SetColumn(directoryText, 1); dirRow.Children.Add(directoryText);
        browseButton.Content = new EditorIcon { Kind = "chevron", Width = 13, Height = 13, Stroke = Brush("Secondary") }; browseButton.Style = FindStyle("PlainButton"); browseButton.Padding = new Thickness(8); browseButton.ToolTip = "选择保存位置";
        System.Windows.Automation.AutomationProperties.SetName(browseButton, "选择保存位置"); browseButton.Click += (_, _) => ChooseOutputPath(); Grid.SetColumn(browseButton, 2); dirRow.Children.Add(browseButton); left.Children.Add(dirRow);

        var contentLabel = Label("导出内容"); contentLabel.Foreground = Brush("Ink"); contentLabel.FontSize = 13; contentLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(contentLabel);
        var modes = new UniformGrid { Columns = 2, Height = 40 };
        videoModeButton.Content = ModeOptionContent("视频成片"); System.Windows.Automation.AutomationProperties.SetName(videoModeButton, "视频成片"); videoModeButton.Style = FindStyle("PlainButton"); videoModeButton.Height = 36; videoModeButton.Margin = new Thickness(2); videoModeButton.Click += (_, _) => SelectMode(false);
        audioModeButton.Content = ModeOptionContent("仅导出音频"); System.Windows.Automation.AutomationProperties.SetName(audioModeButton, "仅导出音频"); audioModeButton.Style = FindStyle("PlainButton"); audioModeButton.Height = 36; audioModeButton.Margin = new Thickness(2); audioModeButton.Click += (_, _) => SelectMode(true);
        modes.Children.Add(videoModeButton); modes.Children.Add(audioModeButton); left.Children.Add(modes);

        var resolutionLabel = Label("分辨率"); resolutionLabel.Foreground = Brush("Ink"); resolutionLabel.FontSize = 13; resolutionLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(resolutionLabel);
        var resolutions = new UniformGrid { Columns = 4, Height = 40 };
        AddChoice(resolutions, resolutionChoices, new[] { "原始分辨率", "1080p", "1440p", "4K" }, index => { resolutionIndex = index; UpdateChoiceStates(); OnSettingsChanged(); }, "ExportResolution");
        left.Children.Add(resolutions);
        var frameRateLabel = Label("帧率"); frameRateLabel.Foreground = Brush("Ink"); frameRateLabel.FontSize = 13; frameRateLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(frameRateLabel);
        var rates = new UniformGrid { Columns = 2, Height = 40 };
        AddChoice(rates, frameRateChoices, new[] { "30 FPS", "60 FPS" }, index => { frameRateIndex = index; UpdateChoiceStates(); OnSettingsChanged(); }, "ExportFrameRate");
        left.Children.Add(rates);
        hardware.Content = "硬件编码"; hardware.IsChecked = true; hardware.Margin = new Thickness(0, 8, 0, 0); hardware.FontSize = 11; System.Windows.Automation.AutomationProperties.SetName(hardware, "优先使用硬件编码"); hardware.Checked += (_, _) => OnSettingsChanged(); hardware.Unchecked += (_, _) => OnSettingsChanged(); left.Children.Add(hardware);
        soundToggle.Content = "导出完成后播放提示音"; soundToggle.Margin = new Thickness(0, 6, 0, 0); soundToggle.FontSize = 11; System.Windows.Automation.AutomationProperties.SetName(soundToggle, "导出完成提示音");
        soundToggle.Checked += (_, _) => OnSoundToggled(); soundToggle.Unchecked += (_, _) => OnSoundToggled(); left.Children.Add(soundToggle);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var rightBorder = new Border { BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(0, 29, 0, 28), Padding = new Thickness(28, 0, 29, 0) };
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(203) }); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewTitle = new TextBlock { Text = "当前画面", FontSize = 13, Foreground = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewTitle, "ExportPreviewTitle"); Grid.SetRow(previewTitle, 0); right.Children.Add(previewTitle);
        var imageFrame = new Border { Background = DottedPreviewSurface(), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(16), ClipToBounds = true, Margin = new Thickness(0, 11, 0, 0) };
        if (preview != null)
        {
            imageFrame.Child = new Image { Source = preview, Stretch = Stretch.Uniform, Margin = new Thickness(18, 8, 18, 8) };
        }
        else imageFrame.Child = new TextBlock { Text = "当前画面预览", Foreground = Brush("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(imageFrame, "ExportPreviewImage"); Grid.SetRow(imageFrame, 1); right.Children.Add(imageFrame);
        var info = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewFormat, "ExportSummaryFormat");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewSize, "ExportSummaryResolution");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewRate, "ExportSummaryFrameRate");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewDuration, "ExportSummaryDuration");
        info.Children.Add(InfoLine("格式", previewFormat)); info.Children.Add(InfoLine("分辨率", previewSize)); info.Children.Add(InfoLine("帧率", previewRate)); info.Children.Add(InfoLine("时长", previewDuration)); estimate.Margin = new Thickness(0, 4, 0, 0); info.Children.Add(estimate);
        Grid.SetRow(info, 2); right.Children.Add(info);
        // Mac exportRangeSummary: range exports show start-end and duration, with a way back to the whole film.
        rangePanel.Margin = new Thickness(0, 6, 0, 0);
        rangeText.FontSize = 12; rangeText.TextWrapping = TextWrapping.Wrap; rangeText.FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI");
        rangeText.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        System.Windows.Automation.AutomationProperties.SetAutomationId(rangeText, "ExportRangeSummary");
        fullToggle.Content = "改为导出全片"; fullToggle.Margin = new Thickness(0, 8, 0, 0); fullToggle.FontSize = 12; fullToggle.Style = FindStyle("InspectorToggle");
        System.Windows.Automation.AutomationProperties.SetName(fullToggle, "改为导出全片");
        fullToggle.Checked += (_, _) => OnRangeToggled(); fullToggle.Unchecked += (_, _) => OnRangeToggled();
        rangePanel.Children.Add(rangeText); rangePanel.Children.Add(fullToggle);
        rangePanel.Visibility = range is null ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(rangePanel, 3); right.Children.Add(rangePanel);
        Grid.SetColumn(rightBorder, 1); rightBorder.Child = right; body.Children.Add(rightBorder);
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(bodyScroll, 1); root.Children.Add(bodyScroll);

        var footer = new Grid { Background = Brush("Chrome"), Margin = new Thickness(0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) }); footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) }); footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var progressPanel = new Grid { Margin = new Thickness(30, 2, 30, 0) };
        progressPanel.ColumnDefinitions.Add(new ColumnDefinition()); progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stageText.Text = ""; stageText.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); stageText.FontSize = 12; stageText.TextTrimming = TextTrimming.CharacterEllipsis; stageText.VerticalAlignment = VerticalAlignment.Center; progressPanel.Children.Add(stageText);
        timeText.Text = ""; timeText.SetResourceReference(TextBlock.ForegroundProperty, "Secondary"); timeText.FontSize = 11; timeText.TextTrimming = TextTrimming.CharacterEllipsis; timeText.MaxWidth = 390; timeText.Margin = new Thickness(14, 0, 0, 0); timeText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(timeText, 1); progressPanel.Children.Add(timeText);
        errorDetailsButton.Content = "查看详情"; errorDetailsButton.Visibility = Visibility.Hidden; errorDetailsButton.Height = 26; errorDetailsButton.MinWidth = 68; errorDetailsButton.Padding = new Thickness(6, 2, 6, 2); errorDetailsButton.Margin = new Thickness(8, 0, 0, 0); errorDetailsButton.Style = FindStyle("PlainButton"); errorDetailsButton.Click += (_, _) => ShowExportErrorDetails(); System.Windows.Automation.AutomationProperties.SetName(errorDetailsButton, "查看导出错误详情"); Grid.SetColumn(errorDetailsButton, 2); progressPanel.Children.Add(errorDetailsButton);
        progressValue.FontSize = 11; progressValue.SetResourceReference(TextBlock.ForegroundProperty, "Secondary"); progressValue.Margin = new Thickness(12, 0, 0, 0); progressValue.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(progressValue, 3); progressPanel.Children.Add(progressValue);
        System.Windows.Automation.AutomationProperties.SetName(progressValue, "导出进度百分比");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(stageText, System.Windows.Automation.AutomationLiveSetting.Polite);
        Grid.SetRow(progressPanel, 0); footer.Children.Add(progressPanel);
        progressBar.Minimum = 0; progressBar.Maximum = 1; progressBar.Height = 4; progressBar.Margin = new Thickness(30, 0, 30, 0); progressBar.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(progressBar, "导出进度");
        Grid.SetRow(progressBar, 1); Grid.SetColumnSpan(progressBar, 2); footer.Children.Add(progressBar);
        var actions = new Grid { Height = 42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(27, 1, 27, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        estimate.Foreground = Brush("Secondary"); estimate.VerticalAlignment = VerticalAlignment.Center; estimate.FontSize = 11; estimate.TextWrapping = TextWrapping.Wrap;
        cancelButton.Content = "取消"; cancelButton.Style = FindStyle("PlainButton"); cancelButton.Padding = new Thickness(0); cancelButton.MinWidth = 58; cancelButton.Height = 40; cancelButton.Click += (_, _) => CancelExport(); Grid.SetColumn(cancelButton, 1); actions.Children.Add(cancelButton);
        openFolderButton.Content = "在资源管理器中显示"; openFolderButton.Visibility = Visibility.Collapsed; openFolderButton.Padding = new Thickness(12, 8, 12, 8); openFolderButton.Height = 40; openFolderButton.Click += (_, _) => OpenCompleted(false); Grid.SetColumn(openFolderButton, 2); actions.Children.Add(openFolderButton);
        openFileButton.Content = "打开文件"; openFileButton.Visibility = Visibility.Collapsed; openFileButton.Style = FindStyle("PrimaryButton"); openFileButton.MinWidth = 145; openFileButton.Height = 42; openFileButton.Padding = new Thickness(18, 8, 18, 8); openFileButton.Margin = new Thickness(7, 0, 0, 0); openFileButton.Click += (_, _) => OpenCompleted(true); Grid.SetColumn(openFileButton, 3); actions.Children.Add(openFileButton);
        exportButton.Content = ExportButtonContent("开始导出视频", true); System.Windows.Automation.AutomationProperties.SetName(exportButton, "开始导出视频"); exportButton.Style = FindStyle("PrimaryButton"); exportButton.MinWidth = 145; exportButton.Height = 42; exportButton.Padding = new Thickness(18, 8, 18, 8); exportButton.Margin = new Thickness(7, 0, 0, 0); exportButton.Click += async (_, _) => await StartExport(); Grid.SetColumn(exportButton, 4); actions.Children.Add(exportButton);
        Grid.SetRow(actions, 2); footer.Children.Add(actions);
        Border footerBorder = new() { Child = footer, BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 2); root.Children.Add(footerBorder);
        var shell = new Border { Background = Brush("Surface"), CornerRadius = new CornerRadius(18), Child = root };
        shell.SizeChanged += (_, _) => shell.Clip = new RectangleGeometry(new Rect(0, 0, shell.ActualWidth, shell.ActualHeight), 18, 18);
        return shell;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush("Secondary"), FontSize = 12, Margin = new Thickness(0, 17, 0, 9) };
    private static Grid ModeOptionContent(string text)
    {
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var check = new TextBlock { Text = "✓", FontSize = 9, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 7, 0) };
        Grid.SetColumn(check, 1); content.Children.Add(check); return content;
    }
    private static DrawingBrush DottedPreviewSurface()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brush("Workspace"), null, new RectangleGeometry(new Rect(0, 0, 14, 14))));
        drawing.Children.Add(new GeometryDrawing(Brush("Dot"), null, new EllipseGeometry(new Point(7, 7), 1, 1)));
        return new DrawingBrush(drawing) { Stretch = Stretch.Fill, TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, 14, 14), ViewboxUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 14, 14), ViewportUnits = BrushMappingMode.Absolute };
    }
    private static UIElement InfoLine(string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 12 });
        value.FontSize = 12; Grid.SetColumn(value, 1); grid.Children.Add(value); return grid;
    }
    private void AddChoice(UniformGrid host, List<Button> choices, string[] labels, Action<int> select, string automationPrefix)
    {
        for (var index = 0; index < labels.Length; index++)
        {
            var selectedIndex = index;
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = labels[index], VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            var check = new TextBlock { Text = "✓", FontSize = 9, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, -3, 0) };
            Grid.SetColumn(check, 1); content.Children.Add(check);
            var button = new Button { Content = content, Height = 36, Margin = new Thickness(2), Padding = new Thickness(3, 5, 3, 5), FontSize = 13, BorderThickness = new Thickness(.75), BorderBrush = Brush("Hairline") };
            System.Windows.Automation.AutomationProperties.SetName(button, labels[index]);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationPrefix + "_" + index);
            button.Click += (_, _) => select(selectedIndex);
            choices.Add(button); host.Children.Add(button);
        }
        UpdateChoiceStates();
    }

    private void UpdateChoiceStates()
    {
        for (var i = 0; i < resolutionChoices.Count; i++)
        {
            resolutionChoices[i].Background = i == resolutionIndex ? Brush("Chrome") : Brush("ControlSurface");
            resolutionChoices[i].Foreground = i == resolutionIndex ? Brush("Ink") : Brush("Secondary");
            resolutionChoices[i].FontWeight = i == resolutionIndex ? FontWeights.SemiBold : FontWeights.Normal;
            if (resolutionChoices[i].Content is Grid g && g.Children.Count > 1) g.Children[1].Visibility = i == resolutionIndex ? Visibility.Visible : Visibility.Collapsed;
        }
        for (var i = 0; i < frameRateChoices.Count; i++)
        {
            frameRateChoices[i].Background = i == frameRateIndex ? Brush("Chrome") : Brush("ControlSurface");
            frameRateChoices[i].Foreground = i == frameRateIndex ? Brush("Ink") : Brush("Secondary");
            frameRateChoices[i].FontWeight = i == frameRateIndex ? FontWeights.SemiBold : FontWeights.Normal;
            if (frameRateChoices[i].Content is Grid g && g.Children.Count > 1) g.Children[1].Visibility = i == frameRateIndex ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private Button MakeButton(string text) => new() { Content = text, Margin = new Thickness(2), Padding = new Thickness(10, 8, 10, 8), Style = FindStyle("PlainButton"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1) };
    private static Brush Brush(string key) => (Brush)(Application.Current?.TryFindResource(key) ?? Brushes.Gray);
    private Style? FindStyle(string key) => TryFindResource(key) as Style ?? Application.Current?.TryFindResource(key) as Style;

    private void UpdateMode()
    {
        extensionBadge.Text = audioOnly ? ".m4a" : ".mp4";
        var completed = completedPath is not null;
        var exportLabel = running ? "正在导出…" : completed ? (audioOnly ? "再次导出音频" : "再次导出") : audioOnly ? (range is null || exportFull ? "导出音频" : "导出选区音频") : (range is null || exportFull ? "开始导出视频" : "开始导出选区视频");
        exportButton.Content = ExportButtonContent(exportLabel, !running);
        System.Windows.Automation.AutomationProperties.SetName(exportButton, exportLabel);
        // After a successful export the button stays available ("再次导出"); changing any option resets the finished state.
        exportButton.Visibility = Visibility.Visible;
        videoModeButton.Background = audioOnly ? Brush("ControlSurface") : Brush("Chrome");
        audioModeButton.Background = audioOnly ? Brush("Chrome") : Brush("ControlSurface");
        videoModeButton.FontWeight = audioOnly ? FontWeights.Normal : FontWeights.Medium;
        audioModeButton.FontWeight = audioOnly ? FontWeights.Medium : FontWeights.Normal;
        if (videoModeButton.Content is Grid videoContent && videoContent.Children.Count > 1) videoContent.Children[1].Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        if (audioModeButton.Content is Grid audioContent && audioContent.Children.Count > 1) audioContent.Children[1].Visibility = audioOnly ? Visibility.Visible : Visibility.Collapsed;
        foreach (var choice in resolutionChoices) choice.IsEnabled = !audioOnly && !running;
        foreach (var choice in frameRateChoices) choice.IsEnabled = !audioOnly && !running;
        hardware.IsEnabled = !audioOnly && !running;
        videoModeButton.IsEnabled = audioModeButton.IsEnabled = browseButton.IsEnabled = !running;
        fullToggle.IsEnabled = !running;
        // Only the explicit "取消导出" button cancels a running export.
        cancelButton.Content = running ? "取消导出" : completed ? "完成" : "取消";
        System.Windows.Automation.AutomationProperties.SetName(cancelButton, running ? "取消导出" : completed ? "完成" : "取消");
        fileName.IsEnabled = !running; cancelButton.IsEnabled = true;
        titleText.Text = range is null || exportFull ? "导出作品" : "导出选区";
        Title = (range is null || exportFull ? "导出作品 · " : "导出选区 · ") + projectName;
    }

    /// <summary>Any option change after a finished export returns the panel to its ready state.</summary>
    private void ResetCompletion()
    {
        if (running || completedPath is null) return;
        completedPath = null;
        openFileButton.Visibility = Visibility.Collapsed; openFolderButton.Visibility = Visibility.Collapsed;
        progressBar.Visibility = Visibility.Collapsed; progressValue.Text = ""; stageText.Text = ""; stageText.ToolTip = null; timeText.Text = ""; timeText.ToolTip = null;
        UpdateMode();
    }

    private void SelectMode(bool audio)
    {
        if (running) return;
        audioOnly = audio;
        ResetCompletion();
        RefreshNameForSettings();
        UpdateMode(); RefreshPreview();
    }

    private void OnSettingsChanged()
    {
        if (initializing) return;
        ResetCompletion();
        RefreshNameForSettings();
        RefreshPreview();
        SavePreferences(p => { p.ExportResolutionIndex = resolutionIndex; p.ExportFrameRate = frameRateIndex == 1 ? 60 : 30; p.ExportHardware = hardware.IsChecked == true; });
    }

    private void OnSoundToggled()
    {
        if (initializing) return;
        playCompletionSound = soundToggle.IsChecked == true;
        SavePreferences(p => p.ExportCompletionSound = playCompletionSound);
    }

    private void OnRangeToggled()
    {
        if (initializing || range is null) return;
        exportFull = fullToggle.IsChecked == true;
        ResetCompletion();
        RefreshNameForSettings();
        UpdateMode(); RefreshPreview();
    }

    private void OnFileNameChanged()
    {
        if (suppressNameEvents) return;
        customName = true;
        ResetCompletion();
        RefreshPreview();
    }

    /// <summary>Settings are remembered outside the undo history; a failing write never blocks exporting.</summary>
    private static void SavePreferences(Action<MainWindow.UserPreferences> change)
    {
        try
        {
            var preferences = MainWindow.ReadUserPreferences();
            change(preferences);
            MainWindow.SaveUserPreferences(preferences);
        }
        catch (Exception error) { App.Log(error); }
    }

    // Mac makeDefaultOutputURL: video "<name>-<fps>fps", audio "<name>-音频"; range exports add "-选区" after the name.
    private string DefaultStem()
    {
        var baseName = SafeStem(projectName);
        if (range is not null && !exportFull) baseName += "-选区";
        return audioOnly ? baseName + "-音频" : baseName + "-" + (frameRateIndex == 1 ? 60 : 30) + "fps";
    }

    /// <summary>Until the user types their own name, the suggested name follows mode, frame rate and range.</summary>
    private void RefreshNameForSettings()
    {
        if (customName) return;
        suppressNameEvents = true;
        fileName.Text = DefaultStem();
        suppressNameEvents = false;
    }

    private void RefreshPreview()
    {
        directoryText.Text = outputDirectory;
        directoryText.ToolTip = outputDirectory;
        var (width, height) = GetSize();
        previewFormat.Text = audioOnly ? "M4A 音频" : "MP4 视频";
        previewSize.Text = audioOnly ? "—" : $"{width} × {height}";
        previewRate.Text = audioOnly ? "—" : (frameRateIndex == 1 ? "60 FPS" : "30 FPS");
        var active = ActiveProject;
        var duration = active.Timeline?.Duration ?? active.Media.Duration;
        previewDuration.Text = double.IsFinite(duration) && duration >= 0 ? TimeSpan.FromSeconds(duration).ToString(duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "—";
        estimate.Text = EstimateFileSize(duration);
        if (range is not null)
        {
            var rangeDuration = range.End - range.Start;
            rangeText.Text = exportFull ? "将导出完整作品" : $"选区 {FormatElapsed(TimeSpan.FromSeconds(range.Start))} – {FormatElapsed(TimeSpan.FromSeconds(range.End))}  ·  时长 {FormatElapsed(TimeSpan.FromSeconds(rangeDuration))}";
            if (fullToggle.IsChecked != exportFull) { var was = initializing; initializing = true; fullToggle.IsChecked = exportFull; initializing = was; }
        }
        RefreshNameNote();
    }

    // Red text for characters Windows cannot store; otherwise a hint when a same-named file will get "-2", "-3"…
    private void RefreshNameNote()
    {
        var error = FileNameError(fileName.Text);
        exportButton.IsEnabled = !running && error is null;
        if (error is not null)
        {
            nameNote.Text = error;
            nameNote.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
            return;
        }
        var stem = NormalizeStem(fileName.Text);
        var extension = audioOnly ? ".m4a" : ".mp4";
        var target = ExportLocation.NextFreePath(outputDirectory, stem, extension);
        if (completedPath is null && !string.Equals(Path.GetFileName(target), stem + extension, StringComparison.OrdinalIgnoreCase))
        {
            nameNote.Text = "已有同名文件，将保存为 " + Path.GetFileName(target);
            nameNote.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        }
        else nameNote.Text = "";
    }

    private static readonly char[] ForbiddenNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    // The extension badge is shown separately; a typed ".mp4"/".m4a" suffix is folded away (Mac normalizedOutputFileName).
    private static string NormalizeStem(string text)
    {
        var name = (text ?? "").Trim();
        foreach (var extension in new[] { ".mp4", ".m4a" })
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) { name = name[..^extension.Length]; break; }
        return name.Trim();
    }

    private static string? FileNameError(string text)
    {
        var name = NormalizeStem(text);
        if (name.Length == 0) return "请输入文件名。";
        if (name is "." or "..") return "文件名不能是 . 或 ..。";
        var bad = name.Where(c => ForbiddenNameChars.Contains(c) || char.IsControl(c)).Distinct().ToArray();
        if (bad.Length > 0) return "文件名不能包含 " + string.Join(" ", bad.Select(c => char.IsControl(c) ? "换行/控制字符" : c.ToString())) + "。";
        if (name.EndsWith('.')) return "文件名不能以句点结尾。";
        var reserved = name.Split('.')[0].ToUpperInvariant();
        if (reserved is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "LPT1" or "LPT2" or "LPT3") return "这是 Windows 保留名称，请换一个文件名。";
        return null;
    }

    private string EstimateFileSize(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return "预计大小：—";
        if (audioOnly)
        {
            var bytes = duration * 192_000 / 8d;
            return $"预计约 {FormatSize(bytes)}（AAC 192 kb/s）";
        }

        var (width, height) = GetSize();
        var frameRate = frameRateIndex == 1 ? 60 : 30;
        var audioBits = 192_000d;
        double lowBitsPerSecond;
        double highBitsPerSecond;
        if (hardware.IsChecked == true)
        {
            // The current hardware path requests 16 Mb/s; actual encoders may vary or fall back.
            lowBitsPerSecond = 12_800_000 + audioBits;
            highBitsPerSecond = 19_200_000 + audioBits;
        }
        else
        {
            // CRF 20 is content-dependent. Use a broad pixel-rate range rather than a point estimate.
            var pixelRate = (double)width * height * frameRate;
            lowBitsPerSecond = pixelRate * .035 + audioBits;
            highBitsPerSecond = pixelRate * .14 + audioBits;
        }

        var lowBytes = duration * lowBitsPerSecond / 8d;
        var highBytes = duration * highBitsPerSecond / 8d;
        return $"预计约 {FormatSize(lowBytes)}–{FormatSize(highBytes)}（含 AAC 192 kb/s，实际大小随画面变化）";
    }

    private static string FormatSize(double bytes)
    {
        const double megabyte = 1_000_000d;
        return $"{bytes / megabyte:0.#} MB";
    }

    private (int Width, int Height) GetSize()
    {
        var index = resolutionIndex;
        var project = ActiveProject;
        var sourceW = Math.Max(16, (int)Math.Round(project.Media.Width > 0 ? project.Media.Width : project.Settings.CanvasWidth));
        var sourceH = Math.Max(16, (int)Math.Round(project.Media.Height > 0 ? project.Media.Height : project.Settings.CanvasHeight));
        if (index == 0) return (Even(sourceW), Even(sourceH));
        var longEdge = index switch { 2 => 2560, 3 => 3840, _ => 1920 };
        var aspect = project.Settings.CanvasWidth > 0 && project.Settings.CanvasHeight > 0 ? (double)project.Settings.CanvasWidth / project.Settings.CanvasHeight : (double)sourceW / sourceH;
        var w = aspect >= 1 ? longEdge : (int)Math.Round(longEdge * aspect);
        var h = aspect >= 1 ? (int)Math.Round(longEdge / aspect) : longEdge;
        return (Even(w), Even(h));
    }
    private static int Even(int value) => Math.Max(16, value / 2 * 2);
    private static string SafeStem(string text)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((string.IsNullOrWhiteSpace(text) ? "DogSC" : text).Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "DogSC" : cleaned;
    }

    // Mac chooseOutputLocation: a folder picker; the choice becomes the remembered export folder.
    private void ChooseOutputPath()
    {
        var dialog = new OpenFolderDialog { Title = "选择导出文件夹", Multiselect = false, InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) != true) return;
        outputDirectory = dialog.FolderName;
        SavePreferences(p => p.ExportFolder = outputDirectory);
        ResetCompletion();
        RefreshPreview();
    }

    private Task StartExport() => exportTask = StartExportCore();

    private async Task StartExportCore()
    {
        if (running) return;
        if (FileNameError(fileName.Text) is { } nameError) { nameNote.Text = nameError; return; }
        var stem = NormalizeStem(fileName.Text);
        string outputPath;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            // Same-named files are never replaced: the next free "-2", "-3"… name is used (Mac makeOutputURL).
            outputPath = ExportLocation.NextFreePath(outputDirectory, stem, audioOnly ? ".m4a" : ".mp4");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stageText.Text = "无法使用导出文件夹，请换一个位置。"; timeText.Text = ex.Message; return;
        }
        running = true; completedPath = null; exportErrorDetails = null; errorDetailsButton.Visibility = Visibility.Hidden; closeAfterCancellation = false; cancellation = new CancellationTokenSource();
        var generation = ++exportGeneration;
        progressBar.Visibility = Visibility.Visible; progressBar.Value = 0; progressBar.Opacity = 1; stageText.Text = "正在准备…"; stageText.ToolTip = null; timeText.Text = ""; timeText.ToolTip = null; progressValue.Text = "";
        exportButton.IsEnabled = false; openFileButton.Visibility = Visibility.Collapsed; openFolderButton.Visibility = Visibility.Collapsed; UpdateMode();
        exportClock.Restart(); lastRemaining = null; lastRemainingAt = TimeSpan.Zero; lastAdvanceAt = DateTime.UtcNow; lastFraction = -1; lastStage = "";
        progressTimer.Start(); OnProgressTimer();
        var reporter = new Progress<ExportProgress>(p =>
        {
            // Progress<T> posts to the dispatcher. Ignore reports queued before cancel,
            // completion, or a subsequent retry so terminal states remain stable.
            if (!running || generation != exportGeneration || cancellation?.IsCancellationRequested != false) return;
            if (double.IsFinite(p.Fraction))
            {
                progressBar.Value = Math.Clamp(p.Fraction, 0, .995);
                progressValue.Text = $"{Math.Floor(progressBar.Value * 100):0}%";
            }
            if (Math.Abs(p.Fraction - lastFraction) > 1e-6 || p.Stage != lastStage) lastAdvanceAt = DateTime.UtcNow;
            lastFraction = p.Fraction; lastStage = p.Stage;
            lastRemaining = p.Remaining; lastRemainingAt = exportClock.Elapsed;
            stageText.Text = p.Stage.Split(" · ", 2, StringSplitOptions.None)[0];
            stageText.ToolTip = p.Stage;
            OnProgressTimer();
        });
        try
        {
            var (w, h) = GetSize(); var options = new ExportOptions(w, h, frameRateIndex == 1 ? 60 : 30, hardware.IsChecked == true, audioOnly);
            var result = await execute(outputPath, options, reporter, cancellation.Token);
            completedPath = result.Path; progressBar.Value = 1; progressValue.Text = "100%"; stageText.Text = "导出完成";
            stageText.ToolTip = $"{result.Encoder} · {result.Elapsed.TotalSeconds:0.0} 秒";
            timeText.Text = "文件已保存：" + Path.GetFileName(result.Path); timeText.ToolTip = result.Path;
            openFileButton.Visibility = Visibility.Visible; openFolderButton.Visibility = Visibility.Visible;
            NotifyCompleted();
        }
        catch (OperationCanceledException) { progressBar.Opacity = .4; progressValue.Text = ""; stageText.Text = "已取消导出"; timeText.Text = "可以调整设置后重新导出"; }
        catch (Exception ex) { progressBar.Opacity = .4; progressValue.Text = ""; stageText.Text = "导出失败，可修改设置后重试"; timeText.Text = "导出出错，查看详情"; timeText.ToolTip = ex.Message; exportErrorDetails = ex.ToString(); errorDetailsButton.Visibility = Visibility.Visible; }
        finally
        {
            progressTimer.Stop(); exportClock.Stop();
            cancellation?.Dispose(); cancellation = null; running = false; exportButton.IsEnabled = true; UpdateMode(); RefreshNameNote();
            if (closeAfterCancellation) Close();
        }
    }

    // Refreshed every second (not only when the engine reports) so a quiet preparation phase still looks alive.
    private void OnProgressTimer()
    {
        if (!running) return;
        var text = "已用 " + FormatElapsed(exportClock.Elapsed);
        if (lastRemaining is { } remaining)
        {
            var left = remaining - (exportClock.Elapsed - lastRemainingAt);
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            text += " · 预计剩余 " + FormatElapsed(left);
        }
        // Mac ExportStatus: no progress for 20 s -> tell the user it is still working.
        if (cancellation?.IsCancellationRequested == false && DateTime.UtcNow - lastAdvanceAt > TimeSpan.FromSeconds(20)) text += " · 仍在处理，请稍候…";
        if (cancellation?.IsCancellationRequested == false) timeText.Text = text;
    }

    // Mac: completion sound (a setting, default on), and the window flashes in the taskbar when not in front.
    private void NotifyCompleted()
    {
        try
        {
            if (playCompletionSound) System.Media.SystemSounds.Asterisk.Play();
            if (!IsActive)
            {
                var target = Owner is { } owner && new WindowInteropHelper(owner).Handle != IntPtr.Zero ? new WindowInteropHelper(owner).Handle : new WindowInteropHelper(this).Handle;
                var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = target, Flags = FlashAll | FlashTimerNoForeground, Count = uint.MaxValue, Timeout = 0 };
                FlashWindowEx(ref info);
            }
        }
        catch (Exception error) { App.Log(error); }
    }

    private const uint FlashAll = 3;
    private const uint FlashTimerNoForeground = 12;
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void CancelExport()
    {
        if (!running) { Close(); return; }
        cancellation?.Cancel(); cancelButton.IsEnabled = false; stageText.Text = "正在取消…"; timeText.Text = "正在收尾并清理临时文件";
    }

    private void ShowExportErrorDetails()
    {
        var errorDetails = exportErrorDetails;
        if (string.IsNullOrWhiteSpace(errorDetails)) return;
        var dialog = new Window { Title = "导出错误详情", Width = 680, Height = 440, MinWidth = 420, MinHeight = 280, ShowInTaskbar = false };
        SurfaceWindow.Configure(dialog, this, resizable: true, captionHeight: 38);

        var content = new Grid { Margin = new Thickness(18, 12, 18, 16) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var details = new TextBox
        {
            Text = errorDetails,
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            Padding = new Thickness(10),
            Background = Brush("Surface"),
            Foreground = Brush("Ink"),
            BorderBrush = Brush("Hairline"),
            BorderThickness = new Thickness(1)
        };
        System.Windows.Automation.AutomationProperties.SetName(details, "导出错误详情文本");
        Grid.SetRow(details, 0);
        content.Children.Add(details);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "复制详情", MinWidth = 90, Height = 36, Margin = new Thickness(0, 0, 8, 0), Style = FindStyle("PlainButton") };
        System.Windows.Automation.AutomationProperties.SetName(copy, "复制导出错误详情");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(errorDetails);
                copy.Content = "已复制";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copy.ToolTip = "剪贴板暂不可用；可在文本框中选择并复制内容。";
            }
        };
        actions.Children.Add(copy);
        var close = new Button { Content = "关闭", MinWidth = 76, Height = 36, Padding = new Thickness(16, 5, 16, 5), Style = FindStyle("PrimaryButton") };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);

        dialog.Content = SurfaceWindow.WrapContent(dialog, content, dialog.Title);
        dialog.ShowDialog();
    }

    private static string FormatElapsed(TimeSpan value) => value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    private StackPanel ExportButtonContent(string label, bool showIcon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (showIcon)
        {
            var icon = new EditorIcon { Kind = "export", Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetBinding(EditorIcon.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
            content.Children.Add(icon);
        }
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }
    private void OpenCompleted(bool file)
    {
        if (completedPath is not { } path || !File.Exists(path)) { stageText.Text = "找不到已导出的文件。"; return; }
        if (file) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); // select the file in Explorer
    }
}

/// <summary>Range shown by "导出选区": output-time start/end plus the untrimmed project for "改为导出全片".</summary>
internal sealed record ExportRangeInfo(double Start, double End, FrameTrailProject FullProject);

/// <summary>Export folder defaults and non-overwriting file names.</summary>
internal static class ExportLocation
{
    /// <summary>
    /// Configured folder, else the default. Mac default is Movies/DogSC/导出. Earlier Windows builds exported straight into
    /// Videos\FrameTrail, so users who already have exported files there (and no new folder yet) keep that location.
    /// </summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { return Path.GetFullPath(configured); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var preferred = Path.Combine(videos, "DogSC", "导出");
        var legacy = Path.Combine(videos, "FrameTrail");
        if (!Directory.Exists(preferred) && HasLegacyExports(legacy)) return legacy;
        return preferred;
    }

    private static bool HasLegacyExports(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(file =>
                file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Mac makeOutputURL: "name.ext", then "name-2.ext", "name-3.ext"… never overwriting.</summary>
    public static string NextFreePath(string directory, string stem, string extension)
    {
        var candidate = Path.Combine(directory, stem + extension);
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, stem + "-" + suffix + extension);
        return candidate;
    }
}
