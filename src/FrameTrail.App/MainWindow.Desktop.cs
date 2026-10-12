using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using FrameTrail.Capture;

namespace FrameTrail.App;

public partial class MainWindow
{
    private DesktopTrayIcon? desktopTray;
    private MemoWindow? desktopMemo;
    private TrayMenuWindow? desktopTrayMenu;
    private bool desktopResident;
    private bool desktopExiting;
    private bool desktopTrayAvailable;
    private Task desktopLaunchTask = Task.CompletedTask;
    private bool showingRecorder;

    internal Task StartDesktopAsync()
    {
        desktopResident = true;
        desktopLaunchTask = Guard(InitializeDesktopAsync);
        return desktopLaunchTask;
    }

    private async Task InitializeDesktopAsync()
    {
        // A message target is needed for hotkeys, but the editor need not be shown.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        // The marker hot key is registered only while a recording runs (see StartRecordingAsync).
        try
        {
            desktopTray = new DesktopTrayIcon(
                () => _ = Guard(ActivateDesktopRecorderAsync), ShowDesktopTrayMenu, ReportDesktopFailure);
            desktopTray.AvailabilityRestored += () => desktopTrayAvailable = true;
            desktopTrayAvailable = true;
        }
        catch (Exception error) { ReportDesktopFailure(error); }
        await RefreshDevices();
        ApplyUserPreferencesOnStartup(showFirstRunHelp: false);
        // After a crash or power loss the interrupted recording is offered once (D-13).
        _ = Guard(OfferRecoveryAtStartupAsync);
    }

    internal void HandleDesktopLaunch(string[] arguments)
    {
        // Process successive open requests in order; one import must not overtake another.
        desktopLaunchTask = HandleDesktopLaunchAsync(desktopLaunchTask, arguments);
    }

    private async Task HandleDesktopLaunchAsync(Task previous, string[] arguments)
    {
        // Acknowledge receipt before any project recovery dialog can enter its
        // nested message loop; opening a file is a queued UI action.
        await Task.Yield();
        await previous;
        if (desktopExiting) return;
        await Guard(async () =>
        {
            var index = Array.IndexOf(arguments, "--open");
            if (index >= 0 && index + 1 < arguments.Length)
            {
                ShowEditorForDesktop();
                await OpenProject(arguments[index + 1]);
            }
            else
            {
                await ActivateDesktopRecorderAsync();
                if (arguments.Length == 0 && !ReadUserPreferences().FirstRunHelpShown)
                {
                    ShowPermissionHelp(true);
                    var preferences = ReadUserPreferences();
                    preferences.FirstRunHelpShown = true;
                    SaveUserPreferences(preferences);
                }
            }
        });
    }

    internal void ShowEditorForDesktop()
    {
        if (project is null) RefreshEmptyRecents();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (Window dialog in OwnedWindows)
            if (dialog.IsVisible) dialog.Activate();
    }

    private async Task ActivateDesktopRecorderAsync()
    {
        if (desktopExiting) return;
        if (completionWindow is { } card)
        {
            // A finished recording is waiting for its decision; the start bar comes back after it.
            if (!card.IsVisible) card.Show();
            card.Activate();
            return;
        }
        if (recordingBar is { } bar)
        {
            if (!bar.IsVisible) bar.Show();
            if (bar.WindowState == WindowState.Minimized) bar.WindowState = WindowState.Normal;
            bar.Activate();
            return;
        }
        if (capture.State == CaptureState.Starting || recordingFinalizing) return;
        if (showingRecorder) return;
        showingRecorder = true;
        try { await ShowFloatingRecorder(); }
        finally { showingRecorder = false; }
    }

    internal void ShowMemoForDesktop(bool? editing = null)
    {
        if (desktopMemo is null)
        {
            desktopMemo = new MemoWindow();
            desktopMemo.Closed += (_, _) => desktopMemo = null;
        }
        if (editing.HasValue) desktopMemo.SetEditingMode(editing.Value);
        if (!desktopMemo.IsVisible) desktopMemo.Show();
        if (desktopMemo.WindowState == WindowState.Minimized) desktopMemo.WindowState = WindowState.Normal;
        desktopMemo.Activate();
    }

    private Window DesktopDialogOwner()
    {
        var activeDialog = Application.Current.Windows.OfType<Window>().LastOrDefault(window => window.IsVisible && window.IsActive && window.Owner is not null);
        if (activeDialog is not null) return activeDialog;
        if (IsVisible && IsActive) return this;
        if (recordingBar?.IsVisible == true) return recordingBar;
        if (recorderSetup?.IsVisible == true) return recorderSetup;
        if (desktopMemo?.IsVisible == true) return desktopMemo;
        if (IsVisible) return this;
        ShowEditorForDesktop();
        return this;
    }

    internal void ReportDesktopFailure(Exception error)
    {
        App.Log(error);
        desktopTrayAvailable = false;
        ShowEditorForDesktop();
        Status.Text = "后台入口暂不可用，编辑器保持打开：" + error.Message;
    }

    private void UpdateDesktopRecordingState()
    {
        if (desktopTray is null) return;
        try { desktopTray.SetRecording(capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused); }
        catch (Exception error) { ReportDesktopFailure(error); }
    }

    private ContextMenu CreateDesktopMenu()
    {
        var menu = NewMenu();
        AddMenu(menu, recordingBar is null ? "显示录制工具" : "显示录制条", () => Guard(ActivateDesktopRecorderAsync));
        AddMenu(menu, "打开编辑器", ShowEditorForDesktop);
        AddMenu(menu, "口播提词器", () => ShowMemoForDesktop());
        var recent = RecentProjects.Read().Take(5).ToArray();
        if (recent.Length > 0)
        {
            var projects = new MenuItem { Header = "最近项目" };
            foreach (var path in recent)
            {
                // Mac recent list: project title and date instead of the shared "project" file name.
                var summary = RecentProjects.Describe(path);
                AddMenu(projects, summary.Title + "  ·  " + summary.DateText, () => Guard(async () => { ShowEditorForDesktop(); await OpenProject(path); }));
            }
            menu.Items.Add(projects);
        }
        menu.Items.Add(new Separator());
        var recoverable = FindRecoverableRecordings();
        if (recoverable.Count > 0)
            AddMenu(menu, $"恢复中断录制（{recoverable.Count} 条，最近 {File.GetLastWriteTime(Path.Combine(recoverable[0], "recording-recovery.json")):MM-dd HH:mm}）", () => Guard(RecoverLatestRecordingAsync));
        AddNextRecordingToggles(menu);
        AddMenu(menu, "设置…", ShowUserPreferences);
        if (capture.State is CaptureState.Recording or CaptureState.Paused)
        {
            AddMenu(menu, capture.State == CaptureState.Paused ? "继续录制" : "暂停录制", () => Guard(ToggleRecordingPause));
            AddMenu(menu, "结束录制", () => Guard(StopRecording));
        }
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出 DogSC", RequestDesktopExitAsync);
        return menu;
    }

    /// <summary>
    /// "下次录制系统声音 / 麦克风" check items (Mac RecorderPanelController status menu). They only change what the next recording
    /// captures, so they are disabled while a recording is starting, running or finishing; each change is saved.
    /// </summary>
    private void AddNextRecordingToggles(ContextMenu menu)
    {
        var idle = capture.State is not (CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping) && !recordingFinalizing && !recordingStarting;
        var systemSound = new MenuItem { Header = "下次录制系统声音", IsCheckable = true, IsChecked = SystemSound.IsChecked == true, IsEnabled = idle };
        systemSound.Click += (_, _) => { SystemSound.IsChecked = systemSound.IsChecked; PersistCaptureToggles(); };
        menu.Items.Add(systemSound);
        var microphoneName = (MicDevices.SelectedItem as AudioDeviceInfo)?.Name;
        var microphone = new MenuItem
        {
            Header = microphoneName is null ? "下次录制麦克风（未检测到设备）" : $"下次录制麦克风（{microphoneName}）",
            IsCheckable = true, IsChecked = Microphone.IsChecked == true && microphoneName is not null, IsEnabled = idle && microphoneName is not null
        };
        microphone.Click += (_, _) => { Microphone.IsChecked = microphone.IsChecked; PersistCaptureToggles(); };
        menu.Items.Add(microphone);
    }

    private void ShowDesktopTrayMenu(Point screenPoint)
    {
        if (desktopTrayMenu is { IsVisible: true } current)
        {
            current.CloseMenu();
            return;
        }
        ShowTrayMenu(CreateDesktopMenu(), screenPoint);
    }

    /// <summary>Shows <paramref name="entries"/> in the activatable, capture-excluded menu window at a physical screen point.</summary>
    private void ShowTrayMenu(ContextMenu entries, Point screenPoint)
    {
        if (desktopTrayMenu is { IsVisible: true } current)
        {
            current.CloseMenu();
            return;
        }

        var window = new TrayMenuWindow(entries);
        desktopTrayMenu = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(desktopTrayMenu, window)) desktopTrayMenu = null;
        };
        window.ShowAt(screenPoint);
    }

    private void ShowDesktopMenu(Button button)
    {
        // Both recorder surfaces use the same activatable, capture-excluded menu
        // as the notification icon, with an anchor just below the pressed button.
        ShowDesktopTrayMenu(button.PointToScreen(new Point(0, button.ActualHeight + 6)));
    }

    private async Task RequestDesktopExitAsync()
    {
        if (desktopExiting || closingInProgress) return;
        // A running export belongs to a modal panel: ask, cancel it and wait for the engine to clean up its temp files.
        if (exportWindow is { IsExporting: true } exporting)
        {
            var answer = SurfaceDialog.Show(exporting, "正在导出。退出 DogSC 会取消导出并清理未完成的文件，是否继续？", "退出 DogSC", MessageBoxButton.YesNo, MessageBoxImage.Warning, acceptLabel: "取消导出并退出", noLabel: "继续导出");
            if (answer != MessageBoxResult.Yes) return;
            await exporting.CancelAndCloseAsync();
        }
        // A tray command can arrive while ShowDialog disables the editor. Do not
        // bypass that dialog's cancellation/save path (especially an active export).
        if (ComponentDispatcher.IsThreadModal)
        {
            var dialog = Application.Current.Windows.OfType<Window>().LastOrDefault(window => window.IsVisible && window.Owner is not null);
            if (dialog is not null)
            {
                dialog.Activate();
                SurfaceDialog.Show(dialog, "请先完成或关闭当前窗口，再退出 DogSC。正在导出时，可先等待完成或点击取消。", "完成当前操作");
                return;
            }
        }
        if (capture.State == CaptureState.Starting || recordingFinalizing || operationCancellation is not null)
        {
            SurfaceDialog.Show(DesktopDialogOwner(), "正在完成当前处理，请稍后再退出。", "DogSC 正在处理");
            return;
        }
        if (capture.State is CaptureState.Recording or CaptureState.Paused &&
            SurfaceDialog.Show(DesktopDialogOwner(), "当前正在录制。结束录制并保存项目后退出？", "退出 DogSC", MessageBoxButton.OKCancel,
                acceptLabel: "结束并退出", cancelLabel: "继续录制", preferCancel: true) != MessageBoxResult.OK) return;
        var keepGoing = true;
        await Guard(async () => keepGoing = await ConfirmCloseEditorAsync());
        if (!keepGoing) return;
        desktopExiting = true;
        try
        {
            await Guard(async () =>
            {
                if (desktopMemo is { } memo)
                {
                    memo.Close();
                    if (ReferenceEquals(desktopMemo, memo)) return; // Saving failed; Closing was cancelled.
                }
                if (capture.State is CaptureState.Recording or CaptureState.Paused) await StopRecording();
                await ReleaseApplicationAsync();
            });
        }
        finally { if (!closeReady) desktopExiting = false; }
    }

    private async Task ReleaseApplicationAsync()
    {
        // Explicit exit must never bypass a memo save failure, including tray fallback.
        if (desktopMemo is { } memo)
        {
            memo.Close();
            if (ReferenceEquals(desktopMemo, memo)) return;
        }
        SaveProject();
        completionWindow?.Dismiss(); completionWindow = null;
        recoveryOffer?.Close();
        previewResizeTimer.Stop();
        seekCancellation?.Cancel();
        assetsCancellation?.Cancel();
        ClosePlaybackAudio();
        await DisposePlaybackResourcesAsync();
        markerHotKey.Dispose();
        await capture.DisposeAsync();
        await trackCapture.DisposeAsync();
        await previewGate.WaitAsync();
        try
        {
            if (scrubDecoder is not null) await scrubDecoder.DisposeAsync();
            scrubDecoder = null; scrubDecoderKey = null; completedPreview = null;
            previewAssets?.Dispose(); previewAssets = null;
            previewRenderer?.Dispose(); previewRenderer = null;
        }
        finally { previewGate.Release(); }
        closeReady = true;
        Application.Current.Shutdown();
    }

    internal void DisposeDesktop()
    {
        desktopTrayMenu?.CloseMenu();
        desktopTray?.Dispose();
        desktopTray = null;
    }
}
