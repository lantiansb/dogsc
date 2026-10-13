using System.Windows;
using System.Windows.Controls;
using FrameTrail.Capture;

namespace FrameTrail.App;

public partial class MainWindow
{
    private bool hideDesktopIconsDuringRecording;
    private DesktopIconVisibility? desktopIconVisibility;
    private Task desktopIconRestore = Task.CompletedTask;

    private async Task HideDesktopIconsForRecordingAsync(CaptureTargetKind kind)
    {
        await desktopIconRestore;
        if (!hideDesktopIconsDuringRecording || kind == CaptureTargetKind.Window) return;
        var session = await DesktopIconVisibility.StartAsync();
        desktopIconVisibility = session;
        _ = session.ObserveExitAsync(error => Dispatcher.BeginInvoke(async () =>
        {
            if (!ReferenceEquals(desktopIconVisibility, session)) return;
            App.Log(error);
            ReportRecorderNotice("桌面图标隐藏已中断，正在结束录制并保存素材。", true);
            if (capture.State is CaptureState.Recording or CaptureState.Paused) await Guard(StopRecording);
        }));
    }

    private Task RestoreDesktopIconsAsync()
    {
        var session = desktopIconVisibility;
        if (session is null) return desktopIconRestore;
        desktopIconVisibility = null;
        return desktopIconRestore = RestoreAsync(session);
        async Task RestoreAsync(DesktopIconVisibility value)
        {
            try { await value.DisposeAsync(); }
            catch (Exception error)
            {
                App.Log(error);
                try
                {
                    await DesktopIconVisibility.RecoverAsync();
                    if (System.IO.File.Exists(DesktopIconVisibility.JournalPath))
                        throw new System.IO.IOException("桌面图标恢复仍在等待 Windows 响应。");
                }
                catch (Exception recoveryError)
                {
                    App.Log(recoveryError);
                    ReportRecorderNotice("桌面图标暂未恢复，可在录制配置中点击“恢复桌面图标”。", true);
                }
            }
        }
    }

    private CheckBox CreateHideDesktopIconsToggle(Thickness margin)
    {
        var toggle = new CheckBox
        {
            Content = "录制时隐藏桌面图标", IsChecked = hideDesktopIconsDuringRecording,
            Style = TryFindResource("InspectorToggle") as Style, Margin = margin, MinHeight = 30,
            ToolTip = "显示器和区域录制期间，本机桌面图标暂时隐藏，结束后恢复原状态。"
        };
        toggle.IsEnabled = capture.State is not (CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping)
            && !recordingStarting && !recordingFinalizing;
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, "HideDesktopIconsDuringRecording");
        void Save()
        {
            hideDesktopIconsDuringRecording = toggle.IsChecked == true;
            var preferences = ReadUserPreferences();
            preferences.HideDesktopIconsDuringRecording = hideDesktopIconsDuringRecording;
            SaveUserPreferences(preferences);
        }
        toggle.Checked += (_, _) => { _ = Guard(() => { Save(); return Task.CompletedTask; }); };
        toggle.Unchecked += (_, _) => { _ = Guard(() => { Save(); return Task.CompletedTask; }); };
        return toggle;
    }
}
