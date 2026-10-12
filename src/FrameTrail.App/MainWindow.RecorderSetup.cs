using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FrameTrail.Capture;

namespace FrameTrail.App;

/// <summary>
/// Recorder setup bar (before recording starts), aligned with Mac v0.1.4 RecorderSetupSurface:
/// a "current mode" control + one red start button. The mode is cycled by click / wheel / arrow keys and only
/// the red button enters the display / window / region selection overlays. Settings are persisted at once.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] RecorderModeNames = ["显示器", "窗口", "区域"];
    private static readonly string[] RecorderModeIcons = ["screen", "window", "region"];

    // True while a selection overlay is open or the recording is being prepared (guards against re-entry).
    private bool recorderSelecting;

    // ---- Preferences (D-07) ------------------------------------------------------------------------------

    /// <summary>Read-modify-write of the recorder related fields of settings.json (Mac setDefault*/select*).</summary>
    private void PersistRecorderPreferences()
    {
        if (diagnosticRecordingRoot is not null) return;
        try
        {
            var settings = ReadUserPreferences();
            settings.RecordMicrophone = Microphone.IsChecked == true;
            settings.RecordCamera = RecordCamera.IsChecked == true;
            settings.RecordSystemSound = SystemSound.IsChecked == true;
            settings.MicrophoneId = (MicDevices.SelectedItem as AudioDeviceInfo)?.Id;
            settings.CameraId = (CameraDevices.SelectedItem as CameraInfo)?.Id;
            settings.RecordingFolder = preferredRecordingRoot;
            settings.FrameRate = CaptureFps.SelectedIndex == 0 ? 30 : 60;
            settings.OriginalResolution = CaptureResolution.SelectedIndex == 1;
            settings.CaptureMode = Math.Clamp(CaptureKind.SelectedIndex, 0, 2);
            SaveUserPreferences(settings);
        }
        catch (Exception error) { App.Log(error); }
    }

    /// <summary>Restores the remembered mode (Mac recorder.capture-mode) without triggering a source refresh.</summary>
    private void ApplySavedCaptureMode()
    {
        var mode = Math.Clamp(ReadUserPreferences().CaptureMode, 0, 2);
        changingRecorderMode = true;
        try { CaptureKind.SelectedIndex = mode; }
        finally { changingRecorderMode = false; }
    }

    /// <summary>
    /// A saved device that no longer exists must not silently turn into "the first device":
    /// the toggle is switched off instead (D-07).
    /// </summary>
    private void ReconcileSavedDevices()
    {
        var saved = ReadUserPreferences();
        var changed = false;
        if (Microphone.IsChecked == true && (MicDevices.SelectedItem is not AudioDeviceInfo mic ||
            !string.IsNullOrEmpty(saved.MicrophoneId) && !string.Equals(mic.Id, saved.MicrophoneId, StringComparison.Ordinal)))
        {
            Microphone.IsChecked = false;
            changed = true;
        }
        if (RecordCamera.IsChecked == true && (CameraDevices.SelectedItem is not CameraInfo camera ||
            !string.IsNullOrEmpty(saved.CameraId) && !string.Equals(camera.Id, saved.CameraId, StringComparison.Ordinal)))
        {
            RecordCamera.IsChecked = false;
            changed = true;
        }
        if (changed) PersistRecorderPreferences();
    }

    /// <summary>Refreshes only the microphone / camera lists, keeping the current selection (D-17, G-07).</summary>
    private async Task RefreshInputDevices()
    {
        var oldMicrophone = (MicDevices.SelectedItem as AudioDeviceInfo)?.Id;
        var oldCamera = (CameraDevices.SelectedItem as CameraInfo)?.Id;
        var microphones = (await trackCapture.EnumerateAudioDevicesAsync()).Where(x => x.Kind == AudioDeviceKind.Microphone).ToArray();
        var cameras = (await trackCapture.EnumerateCamerasAsync()).ToArray();
        MicDevices.ItemsSource = microphones;
        MicDevices.SelectedItem = microphones.FirstOrDefault(x => x.Id == oldMicrophone) ?? microphones.FirstOrDefault();
        CameraDevices.ItemsSource = cameras;
        CameraDevices.SelectedItem = cameras.FirstOrDefault(x => x.Id == oldCamera) ?? cameras.FirstOrDefault();
    }

    // ---- First position (D-27) ---------------------------------------------------------------------------

    /// <summary>With no saved position, appear in the centre of the work area of the display holding the pointer.</summary>
    private static void CenterOnCursorDisplay(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SourceInitialized += (_, _) =>
        {
            PickerNative.GetCursorPos(out var cursor);
            if (!PickerNative.TryGetMonitorWorkArea(cursor, out var work, out var scale)) return;
            var handle = new WindowInteropHelper(window).Handle;
            var width = (int)Math.Round(window.Width * scale);
            var height = (int)Math.Round(Math.Max(window.MinHeight, 80) * scale);
            var x = work.Left + (work.Right - work.Left - width) / 2;
            var y = work.Top + (work.Bottom - work.Top - height) / 2;
            // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            NativeWindows.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        };
    }

    // ---- Setup bar ---------------------------------------------------------------------------------------

    internal Window BuildFloatingRecorderWindow()
    {
        var window = new Window { Title = "DogSC · 录制", Width = 818, Height = 80, MinHeight = 80, SizeToContent = SizeToContent.Height, Topmost = true, ShowInTaskbar = false };
        SurfaceWindow.Configure(window, captionHeight: 0);
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 8, 6, 8) };

        // "正在准备录制…" cover (D-23): blocks repeated clicks while the native capture starts.
        var preparing = new Border { CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed };
        preparing.SetResourceReference(Border.BackgroundProperty, "Chrome");
        var preparingContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        preparingContent.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        preparingContent.Children.Add(new TextBlock { Text = "正在准备录制…", FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        preparing.Child = preparingContent;
        var root = new Grid();
        root.Children.Add(panel);
        root.Children.Add(preparing);
        window.Content = SurfaceWindow.WrapContent(window, root, "录制", showCloseButton: false, showTitleBar: false);

        void SetPreparing(bool on)
        {
            preparing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            panel.IsEnabled = !on;
        }

        void Divider(double margin = 10)
        {
            var divider = new Border { Width = 1, Height = 34, Margin = new Thickness(margin, 0, margin, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, "Hairline");
            panel.Children.Add(divider);
        }

        // -- mode "wheel": click / wheel / arrow keys cycle display → window → region (Mac RecorderModeWheelInput)
        var modeIcon = new EditorIcon { Kind = "screen", Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        modeIcon.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
        var modeText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), MinWidth = 42 };
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var glyph in new[] { "", "" })
        {
            var arrow = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
            arrows.Children.Add(arrow);
        }
        var modeContent = new StackPanel { Orientation = Orientation.Horizontal };
        modeContent.Children.Add(modeIcon);
        modeContent.Children.Add(modeText);
        modeContent.Children.Add(arrows);
        var mode = new Button { Content = modeContent, Height = 56, Padding = new Thickness(14, 0, 12, 0), Margin = new Thickness(0), Style = (Style)FindResource("PlainButton"), ToolTip = "录制范围：点击、滚轮或方向键切换（显示器 / 窗口 / 区域）" };
        System.Windows.Automation.AutomationProperties.SetName(mode, "录制范围");

        void RefreshMode()
        {
            var index = Math.Clamp(CaptureKind.SelectedIndex, 0, 2);
            modeIcon.Kind = RecorderModeIcons[index];
            modeText.Text = RecorderModeNames[index];
        }

        void StepMode(int delta)
        {
            // Changing the mode never starts a recording; it only remembers the choice.
            var next = ((Math.Clamp(CaptureKind.SelectedIndex, 0, 2) + delta) % 3 + 3) % 3;
            changingRecorderMode = true;
            try { CaptureKind.SelectedIndex = next; }
            finally { changingRecorderMode = false; }
            RefreshMode();
            PersistRecorderPreferences();
        }

        mode.Click += (_, _) => StepMode(1);
        mode.MouseWheel += (_, e) => { if (e.Delta != 0) { StepMode(e.Delta > 0 ? -1 : 1); e.Handled = true; } };
        window.PreviewKeyDown += (_, e) =>
        {
            if (preparing.Visibility == Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
            switch (e.Key)
            {
                case Key.Up or Key.Left: StepMode(-1); e.Handled = true; break;
                case Key.Down or Key.Right: StepMode(1); e.Handled = true; break;
            }
        };
        panel.Children.Add(mode);
        SelectionChangedEventHandler sourceSelectionChanged = (_, _) => RefreshMode();
        CaptureKind.SelectionChanged += sourceSelectionChanged;
        window.Closed += (_, _) => CaptureKind.SelectionChanged -= sourceSelectionChanged;
        RefreshMode();
        Divider();

        // -- microphone / camera / system sound switches
        Button Round(string icon, string label, CheckBox? state, Action<Button> click)
        {
            var grid = new Grid { Width = 42, Height = 42 };
            var background = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(.6) };
            background.SetResourceReference(Border.BackgroundProperty, "Chrome");
            background.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            grid.Children.Add(background);
            var glyph = new EditorIcon { Kind = icon, Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(EditorIcon.StrokeProperty, "Secondary");
            grid.Children.Add(glyph);
            if (state != null)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Stroke = Brushes.White, StrokeThickness = 1.3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1) };
                void Refresh()
                {
                    if (state.IsChecked == true)
                    {
                        dot.Fill = new SolidColorBrush(Color.FromRgb(56, 179, 120));
                        glyph.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
                    }
                    else
                    {
                        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Hairline");
                        glyph.SetResourceReference(EditorIcon.StrokeProperty, "Secondary");
                    }
                }
                Refresh();
                RoutedEventHandler update = (_, _) => Refresh();
                state.Checked += update;
                state.Unchecked += update;
                window.Closed += (_, _) => { state.Checked -= update; state.Unchecked -= update; };
                grid.Children.Add(dot);
            }
            var button = new Button { Content = grid, Width = 52, Height = 56, Style = (Style)FindResource("PlainButton"), Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = label };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => click(button);
            panel.Children.Add(button);
            return button;
        }

        async void OpenDeviceMenu(Button button, CheckBox enabled, ComboBox devices, string title, bool withSystemSettings)
        {
            // Mac refreshes the device list every time the popover opens; the current selection is kept.
            button.IsEnabled = false;
            try { await Guard(RefreshInputDevices); }
            finally { button.IsEnabled = true; }
            var menu = new ContextMenu();
            var off = new MenuItem { Header = "关闭" + title, IsCheckable = true, IsChecked = enabled.IsChecked != true };
            off.Click += (_, _) => { enabled.IsChecked = false; PersistRecorderPreferences(); };
            menu.Items.Add(off);
            foreach (var item in devices.Items)
            {
                var name = item switch { AudioDeviceInfo audio => audio.Name, CameraInfo camera => camera.Name, _ => item.ToString() };
                var entry = new MenuItem { Header = name, IsCheckable = true, IsChecked = enabled.IsChecked == true && Equals(devices.SelectedItem, item) };
                entry.Click += (_, _) => { devices.SelectedItem = item; enabled.IsChecked = true; PersistRecorderPreferences(); };
                menu.Items.Add(entry);
            }
            if (devices.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "未检测到设备", IsEnabled = false });
            if (withSystemSettings)
            {
                // The old dead "设备" button is gone; its only useful part lives at the bottom of the camera menu (D-26).
                menu.Items.Add(new Separator());
                var settings = new MenuItem { Header = "系统摄像头设置…" };
                settings.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:camera") { UseShellExecute = true });
                menu.Items.Add(settings);
            }
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        Round("mic", "麦克风", Microphone, b => OpenDeviceMenu(b, Microphone, MicDevices, "麦克风", false));
        Round("camera", "摄像头", RecordCamera, b => OpenDeviceMenu(b, RecordCamera, CameraDevices, "摄像头", true));
        Round("audio", "系统声音", SystemSound, _ => { SystemSound.IsChecked = SystemSound.IsChecked != true; PersistRecorderPreferences(); });
        Divider(8);

        // -- save location & recording configuration
        var saveTextBlock = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 70 };
        void RefreshSaveLabel() => saveTextBlock.Text = preferredRecordingRoot == null ? "项目" : Path.GetFileName(preferredRecordingRoot);
        RefreshSaveLabel();
        var savePanel = new DockPanel { Width = 128 };
        var folderIcon = new EditorIcon { Kind = "folder", Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        folderIcon.SetResourceReference(EditorIcon.StrokeProperty, "Secondary");
        DockPanel.SetDock(folderIcon, Dock.Left);
        savePanel.Children.Add(folderIcon);
        var chevron = new EditorIcon { Kind = "chevron", Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(EditorIcon.StrokeProperty, "Secondary");
        DockPanel.SetDock(chevron, Dock.Right);
        savePanel.Children.Add(chevron);
        var saveText = new StackPanel();
        var saveLabel = new TextBlock { Text = "保存到", FontSize = 10 };
        saveLabel.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        saveText.Children.Add(saveLabel);
        saveText.Children.Add(saveTextBlock);
        savePanel.Children.Add(saveText);
        var save = new Button { Content = savePanel, Width = 154, Height = 46, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(5, 0, 7, 0), BorderThickness = new Thickness(.75), ToolTip = "保存位置与录制配置" };
        save.SetResourceReference(Control.BackgroundProperty, "Chrome");
        save.SetResourceReference(Control.BorderBrushProperty, "Hairline");
        save.Click += (_, _) => ShowRecorderConfig(save, window, RefreshSaveLabel);
        panel.Children.Add(save);

        var memoIcon = new EditorIcon { Kind = "memo", Width = 21, Height = 21 };
        memoIcon.SetResourceReference(EditorIcon.StrokeProperty, "Ink");
        var memo = new Button { Content = memoIcon, Width = 36, Height = 36, Margin = new Thickness(3, 0, 7, 0), Padding = new Thickness(0), ToolTip = "口播提词器" };
        System.Windows.Automation.AutomationProperties.SetName(memo, "口播提词器");
        memo.Click += (_, _) => MemoClick(this, new RoutedEventArgs());
        panel.Children.Add(memo);
        Round("window", "打开编辑器", null, _ => ShowEditorForDesktop());
        Round("settings", "DogSC 菜单", null, ShowDesktopMenu);
        Round("close", "收起录制工具", null, _ => window.Close());
        Divider(6);

        // -- the red start button: the only control that enters the selection overlays
        var startGrid = new Grid { Width = 50, Height = 50 };
        startGrid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(PickerStyle.Red) });
        startGrid.Children.Add(new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var start = new Button { Content = startGrid, Width = 58, Height = 56, Style = (Style)FindResource("PlainButton"), Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = "开始录制：进入所选范围的选择层" };
        System.Windows.Automation.AutomationProperties.SetName(start, "开始录制");
        start.Click += async (_, _) => await StartCaptureFlow(window, SetPreparing);
        panel.Children.Add(start);

        window.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel) window.DragMove(); };
        return window;
    }

    // ---- Selection → recording ---------------------------------------------------------------------------

    /// <summary>
    /// Red button: hide the bar and memo, run the display / window / region selection overlay, then start recording
    /// with the chosen target. Cancel restores everything; failures restore the bar (D-03, D-23).
    /// </summary>
    private async Task StartCaptureFlow(Window setup, Action<bool> setPreparing)
    {
        if (recorderSelecting || capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping) return;
        recorderSelecting = true;
        var kind = Math.Clamp(CaptureKind.SelectedIndex, 0, 2);
        var memoWasVisible = desktopMemo is { IsVisible: true };
        TargetItem? item = null;
        PixelRect? pickedRegion = null;
        Exception? failure = null;
        try
        {
            PersistRecorderPreferences();
            setup.Hide();
            if (memoWasVisible) desktopMemo?.Hide();
            try { (item, pickedRegion) = await PickCaptureTargetAsync(kind); }
            catch (Exception error) { failure = error; }
            finally
            {
                setup.Show();
                if (memoWasVisible && desktopMemo is { IsVisible: false } memoWindow) memoWindow.Show();
            }
            if (failure is not null) { await Guard(() => Task.FromException(failure)); return; }
            if (item is null) { setup.Activate(); return; }

            setPreparing(true);
            var started = false;
            try
            {
                Targets.ItemsSource = new List<TargetItem> { item };
                Targets.SelectedIndex = 0;
                region = pickedRegion;
                if (pickedRegion is not null) RegionLabel.Text = $"{pickedRegion.X},{pickedRegion.Y} · {pickedRegion.Width}×{pickedRegion.Height} px";
                await Guard(ToggleRecording);
                if (capture.State is CaptureState.Recording or CaptureState.Paused)
                {
                    started = true;
                    setup.Close();
                }
            }
            finally { if (!started) setPreparing(false); }
        }
        finally { recorderSelecting = false; }
    }

    private async Task<(TargetItem? Item, PixelRect? Region)> PickCaptureTargetAsync(int kind)
    {
        Task<IReadOnlyList<DisplayInfo>> LoadDisplays() => capture.EnumerateDisplaysAsync();
        switch (kind)
        {
            case 1:
            {
                var editor = excludeEditorDuringRecording ? IntPtr.Zero : new WindowInteropHelper(this).Handle;
                var picked = await WindowPickerSession.PickAsync(LoadDisplays, editor);
                if (picked is null) return (null, null);
                var target = new CaptureTarget(CaptureTargetKind.Window, picked.Handle.ToInt64().ToString("X"), picked.Bounds, picked.Handle);
                return (new TargetItem(picked.Title, target), null);
            }
            case 2:
            {
                var displays = await LoadDisplays();
                var rect = await RegionPickerSession.PickAsync(displays);
                if (rect is null) return (null, null);
                var host = displays.FirstOrDefault(d => PickerOverlayWindow.Contains(d.Bounds, rect))
                    ?? throw new InvalidOperationException("所选区域需要完整位于同一块显示器内。");
                return (new TargetItem(host.Name, new CaptureTarget(CaptureTargetKind.Display, host.Id, host.Bounds)), rect);
            }
            default:
            {
                var display = await DisplayPickerSession.PickAsync(LoadDisplays, message => ShowNotice(message, true));
                if (display is null) return (null, null);
                return (new TargetItem(display.Name, new CaptureTarget(CaptureTargetKind.Display, display.Id, display.Bounds)), null);
            }
        }
    }

    // ---- "保存与录制配置" panel (G-11, D-19) --------------------------------------------------------------

    private void ShowRecorderConfig(Button anchor, Window recorder, Action refreshSaveLabel)
    {
        Window? panelWindow = null;
        void Close() { try { panelWindow?.Close(); } catch (InvalidOperationException) { } }

        Button Row(string text, string? detail, Action click)
        {
            var dock = new DockPanel { LastChildFill = true };
            if (detail is not null)
            {
                var side = new TextBlock { Text = detail, FontSize = 12, Margin = new Thickness(16, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, VerticalAlignment = VerticalAlignment.Center };
                side.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
                DockPanel.SetDock(side, Dock.Right);
                dock.Children.Add(side);
            }
            dock.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var row = new Button { Content = dock, Style = (Style)FindResource("PlainButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0), MinHeight = 34 };
            row.Click += (_, _) => click();
            return row;
        }

        UIElement Segmented(string title, string[] labels, Func<int> get, Action<int> set)
        {
            var line = new DockPanel { Margin = new Thickness(10, 6, 6, 6) };
            var caption = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 56 };
            DockPanel.SetDock(caption, Dock.Left);
            line.Children.Add(caption);
            var group = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var buttons = new List<Button>();
            void Paint()
            {
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (i == get()) buttons[i].SetResourceReference(Control.BackgroundProperty, "Selection");
                    else { buttons[i].ClearValue(Control.BackgroundProperty); buttons[i].Background = Brushes.Transparent; }
                }
            }
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var button = new Button { Content = labels[i], Style = (Style)FindResource("PlainButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2, 0, 0, 0), FontSize = 12.5 };
                button.Click += (_, _) => { set(index); Paint(); PersistRecorderPreferences(); };
                buttons.Add(button);
                group.Children.Add(button);
            }
            Paint();
            line.Children.Add(group);
            return line;
        }

        Border Rule()
        {
            var rule = new Border { Height = 1, Margin = new Thickness(6, 5, 6, 5) };
            rule.SetResourceReference(Border.BackgroundProperty, "Hairline");
            return rule;
        }

        var stack = new StackPanel { Width = 330 };
        stack.Children.Add(Row("保存位置", preferredRecordingRoot == null ? "默认（视频\\FrameTrail）" : Path.GetFileName(preferredRecordingRoot), () =>
        {
            Close();
            RecordingFolderClick(this, new RoutedEventArgs());
            PersistRecorderPreferences();
            refreshSaveLabel();
        }));

        // 自动命名: used by the next recording as project name and folder-name prefix (Mac recordingTitleDraft).
        var nameBox = new TextBox { Text = nextRecordingTitle ?? string.Empty, Height = 34, Margin = new Thickness(10, 4, 10, 4), Padding = new Thickness(10, 0, 10, 0), MaxLength = 60, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "下次录制的项目名与文件夹名前缀；留空则使用时间戳" };
        var placeholder = new TextBlock { Text = "自动命名", IsHitTestVisible = false, Margin = new Thickness(21, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        placeholder.Visibility = nameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        nameBox.TextChanged += (_, _) =>
        {
            var text = nameBox.Text.Trim();
            nextRecordingTitle = text.Length == 0 ? null : text;
            placeholder.Visibility = nameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var nameGrid = new Grid();
        nameGrid.Children.Add(nameBox);
        nameGrid.Children.Add(placeholder);
        stack.Children.Add(nameGrid);

        stack.Children.Add(Segmented("帧率", ["30 FPS", "60 FPS"], () => CaptureFps.SelectedIndex == 0 ? 0 : 1, index => CaptureFps.SelectedIndex = index));
        stack.Children.Add(Segmented("画质", ["最高 1080p", "原始分辨率"], () => CaptureResolution.SelectedIndex == 1 ? 1 : 0, index => CaptureResolution.SelectedIndex = index));
        stack.Children.Add(Rule());

        stack.Children.Add(Row("打开项目…", null, () =>
        {
            Close();
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "打开 DogSC 项目", Filter = "DogSC 项目|*.frametrail|项目备份|*.frametrail.bak", CheckFileExists = true };
            if (dialog.ShowDialog(recorder) != true) return;
            var file = dialog.FileName;
            _ = Guard(async () => { ShowEditorForDesktop(); await OpenProject(file); RecentProjects.Add(file); });
        }));
        var last = RecentProjects.Read().FirstOrDefault();
        if (last is not null)
        {
            stack.Children.Add(Row("继续上次项目", Path.GetFileNameWithoutExtension(last), () =>
            {
                Close();
                _ = Guard(async () => { ShowEditorForDesktop(); await OpenProject(last); RecentProjects.Add(last); });
            }));
        }
        stack.Children.Add(Row("恢复未完成录制…", null, () => { Close(); RecoverRecordingClick(this, new RoutedEventArgs()); }));
        stack.Children.Add(Row("口播提词器", null, () => { Close(); ShowMemoForDesktop(); }));

        var surface = new Border { Style = (Style)FindResource("FloatingSurface"), Margin = new Thickness(9, 7, 9, 11), Child = stack };
        panelWindow = new Window
        {
            Title = "DogSC · 保存与录制配置",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = surface
        };
        panelWindow.SourceInitialized += (_, _) => NativeWindows.Exclude(panelWindow);
        panelWindow.Deactivated += (_, _) => Close();
        panelWindow.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };

        try
        {
            var anchorPoint = anchor.PointToScreen(new Point(0, anchor.ActualHeight + 6));
            panelWindow.Left = anchorPoint.X;
            panelWindow.Top = anchorPoint.Y;
            panelWindow.Show();
            TrayMenuPlacement.PlaceWindow(panelWindow, anchorPoint);
            panelWindow.Activate();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentOutOfRangeException or OverflowException)
        {
            App.Log(error);
        }
    }
}
