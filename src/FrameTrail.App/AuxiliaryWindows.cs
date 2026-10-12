using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
public sealed class RecordingBar : Window
{
    private readonly TextBlock label=new() { Margin=new(6,0,16,0),MinWidth=106,VerticalAlignment=VerticalAlignment.Center };
    private readonly Stopwatch watch=Stopwatch.StartNew();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(250) };
    private int markers;
    private readonly Button pause=new() { Width=38,Height=38,Padding=new(0),ToolTip="暂停录制" };
    private readonly Button end;
    private readonly Button mark;
    private readonly Button? menu;
    private readonly Func<Task> stop;
    private readonly Action<Exception>? stopFailed;
    private bool stopInProgress;
    private bool closeAfterRecording;
    private string? stopStatus;
    private bool paused;
    public RecordingBar(Action addMarker,Func<Task> stop,Func<Task> togglePause,Action<Exception>? stopFailed=null,string? markerShortcut="Ctrl+Alt+M",bool markerShortcutIsFallback=false,Action<Button>? showMenu=null)
    {
        this.stop=stop??throw new ArgumentNullException(nameof(stop));this.stopFailed=stopFailed;
        Title="DogSC · 录制条"; Width=(markerShortcutIsFallback?458:428)+(showMenu is null?0:46); Height=72; Top=30; Left=120; Topmost=true; ShowInTaskbar=false;
        SurfaceWindow.Configure(this,closeOnEscape:false,captionHeight:0);
        var panel=new StackPanel { Orientation=Orientation.Horizontal,Margin=new(12,6,12,6) }; Content=SurfaceWindow.WrapContent(this,panel,Title,showCloseButton:false,showTitleBar:false); panel.Children.Add(label);
        var markerContent=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
        markerContent.Children.Add(new EditorIcon { Kind="flag",Width=17,Height=17,Margin=new(0,0,9,0),VerticalAlignment=VerticalAlignment.Center });
        var markerText=new StackPanel(); markerText.Children.Add(new TextBlock { Text="标记",FontSize=12,FontWeight=FontWeights.Medium });
        var shortcutText=new TextBlock { Text=markerShortcut??"快捷键不可用",FontSize=10.5,Margin=new(0,2,0,0) };shortcutText.SetResourceReference(TextBlock.ForegroundProperty,"Secondary");markerText.Children.Add(shortcutText);markerContent.Children.Add(markerText);
        mark=new Button { Content=markerContent,ToolTip=markerShortcut==null?"标记快捷键暂不可用，点击此按钮添加标记。":markerShortcutIsFallback?"Ctrl+Alt+M 已被占用，使用 "+markerShortcut+" 或点击添加标记。":"添加标记 · "+markerShortcut,MinWidth=markerShortcutIsFallback?169:139,Height=46,Padding=new(12,4,12,4) };
        System.Windows.Automation.AutomationProperties.SetName(mark,"添加标记");System.Windows.Automation.AutomationProperties.SetAutomationId(mark,"RecordingAddMarker");System.Windows.Automation.AutomationProperties.SetHelpText(mark,mark.ToolTip.ToString());
        mark.Click+=(_,_)=>addMarker(); panel.Children.Add(mark);
        pause.Content=new EditorIcon { Kind="pause" }; pause.Click+=async(_,_)=>{pause.IsEnabled=false;try{await togglePause();}finally{pause.IsEnabled=true;}};panel.Children.Add(pause);
        end=new Button { Content="结束录制",Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(227,101,85)),Margin=new(8,0,0,0),Height=38 }; end.Click+=async(_,_)=>await RequestStopAsync(); panel.Children.Add(end);
        if(showMenu is not null)
        {
            var icon=new EditorIcon { Kind="settings",Width=18,Height=18 };
            icon.SetResourceReference(EditorIcon.StrokeProperty,"Ink");
            var menuButton=new Button { Content=icon,Width=38,Height=38,Padding=new(0),Margin=new(8,0,0,0),ToolTip="DogSC 菜单" };
            System.Windows.Automation.AutomationProperties.SetName(menuButton,"DogSC 菜单");
            System.Windows.Automation.AutomationProperties.SetAutomationId(menuButton,"RecordingBarMenu");
            menuButton.Click+=(_,_)=>showMenu(menuButton);
            menu=menuButton;
            panel.Children.Add(menuButton);
        }
        timer.Tick+=(_,_)=>{if(!stopInProgress)RefreshLabel();};RefreshLabel();timer.Start();
        Closing+=OnClosing; SourceInitialized+=(_,_)=>NativeWindows.Exclude(this); Closed+=(_,_)=>timer.Stop();
        label.MouseLeftButtonDown+=(_,e)=>{ if(e.ClickCount==1)DragMove(); };
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeAfterRecording)return;
        e.Cancel=true;
        _=Dispatcher.BeginInvoke(new Action(async()=>await RequestStopAsync()));
    }
    private async Task RequestStopAsync()
    {
        if(stopInProgress||closeAfterRecording)return;
        stopInProgress=true;stopStatus="正在结束录制…";end.IsEnabled=false;pause.IsEnabled=false;mark.IsEnabled=false;if(menu is not null)menu.IsEnabled=false;label.Text=stopStatus;
        try
        {
            await stop();
            CloseAfterRecording();
        }
        catch(Exception error)
        {
            stopStatus="结束失败 · 可重试";label.Text=stopStatus;
            try{stopFailed?.Invoke(error);}catch(Exception notificationError){App.Log(notificationError);}
        }
        finally
        {
            stopInProgress=false;
            if(IsVisible&&!closeAfterRecording){end.IsEnabled=true;pause.IsEnabled=true;mark.IsEnabled=!paused;if(menu is not null)menu.IsEnabled=true;}
        }
    }
    public void CloseAfterRecording()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(CloseAfterRecording);return;}
        if(closeAfterRecording)return;
        closeAfterRecording=true;
        Close();
    }
    private void RefreshLabel()=>label.Text=stopStatus??$"{(paused?"Ⅱ":"●")} {watch.Elapsed:mm\\:ss} · {markers} 标记";
    public void SetMarkerCount(int value) { markers=value;RefreshLabel(); }
    public void SetPaused(bool value) { paused=value;if(value)watch.Stop();else watch.Start();pause.Content=new EditorIcon { Kind=value?"play":"pause" };pause.ToolTip=value?"继续录制":"暂停录制";mark.IsEnabled=!value&&!stopInProgress;RefreshLabel(); }
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
            Text = File.Exists(file) ? File.ReadAllText(file) : "录制提纲…"
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
                        input.Text.Length == 0 || input.Text == "录制提纲…", DefaultScrollSpeed);
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
