using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using FrameTrail.Capture;
using FrameTrail.Media;
using PointerEvent = FrameTrail.PointerEvent;

namespace FrameTrail.App;

public partial class MainWindow : Window
{
    private readonly FrameCaptureService capture = new();
    private FrameTrailProject? project;
    private string? projectFile;
    private UndoRedoHistory<FrameTrailProject>? history;
    private CancellationTokenSource? seekCancellation, operationCancellation;
    private readonly SemaphoreSlim previewGate = new(1,1);
    private GpuFrameRenderer? previewRenderer;
    private SceneAssetLoader? previewAssets;
    private string? previewAssetsProject;
    private (int SW,int SH,int OW,int OH) previewSize;
    private FrameTrailProject? evaluatedProject;
    private FrameScene? displayedPreviewScene;
    private FrameTrailProject? displayedPreviewProject;
    private PreparedFrameSceneEvaluator? previewEvaluator;
    private TimelineAssetData? timelineAssets;
    private CancellationTokenSource? assetsCancellation;
    private bool refreshing, playing, closeReady, closingInProgress;
    private string? proxyPath, recordingFolder;
    private string? diagnosticRecordingRoot;
    private PixelRect? region;
    private RecordingBar? recordingBar;
    private RegionRecordingGuide? regionRecordingGuide;
    private readonly RecordingMarkerHotKey markerHotKey = new();
    private readonly List<(long Tick, string Label)> recordingMarkers = [];
    private readonly DispatcherTimer recordingGuard = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool protectiveStop;
    private bool recordingFinalizing;
    private Task? recordingStopTask;
    private bool recordingFaultHandled;
    private long recordingStart;
    private record TargetItem(string Name, CaptureTarget Target);
    private record ClipItem(Guid Id, string Title, string Detail, BitmapSource? Thumbnail);
    private record MarkerItem(Guid Id, double SourceTime, string Label) { public override string ToString() => $"{SourceTime:0.00}s  {Label}"; }

    public MainWindow()
    {
        _ = WarmUpPreviewAsync();
        InitializeComponent(); InitializeEditorShell(); InitializeLifecycle(); SurfaceWindow.AttachNativeEdges(this);
        MainWindowPlacement.Attach(this);
        SurfaceWindow.FitToWorkArea(this);
        AutomationProperties.SetItemStatus(PreviewImage, "idle");
        AutomationProperties.SetItemStatus(TimelineView, "idle");
        Closed += (_, _) => { markerHotKey.Dispose();seekCancellation?.Cancel(); assetsCancellation?.Cancel(); operationCancellation?.Cancel(); recordingBar?.CloseAfterRecording(); recordingBar=null; CloseRegionRecordingGuide(); recordingGuard.Stop(); completionWindow?.Dismiss(); completionWindow=null; recoveryOffer?.Close(); };
        InitializeRecordingFlow();
        recordingGuard.Tick += async (_, _) =>
        {
            if(capture.State is not (CaptureState.Recording or CaptureState.Paused) || protectiveStop||recordingFinalizing)return;
            using var process=Process.GetCurrentProcess(); process.Refresh();
            string? reason=process.PrivateMemorySize64>3L*1024*1024*1024||capture.CurrentCapturePrivateBytes>2L*1024*1024*1024 ? "录制进程达到内存保护阈值，请降低录制分辨率后重试。" : null;
            if(recordingFolder!=null && RecordingDisk.TryGetFreeSpace(recordingFolder,out var freeBytes) && freeBytes<300L*1024*1024)reason="录制盘剩余空间低于 300MiB。";
            if(reason==null)return; protectiveStop=true;recordingGuard.Stop();
            completionNotice="已保护性结束并保留素材："+reason;ReportRecorderNotice(completionNotice,true);
            await Guard(async()=>{await StopRecording();Status.Text="已保护性结束并保留素材："+reason;});protectiveStop=false;
        };
        // A single independent track failing no longer ends the recording; see HandleTrackStateChangedAsync (D-11). Its error is only logged and kept in the warnings.
        trackCapture.ErrorOccurred += (_,error)=>Dispatcher.BeginInvoke(()=>{App.Log(error.Exception??new Exception(error.Message));if(capture.State is CaptureState.Recording or CaptureState.Paused&&!recordingFinalizing&&!recordingStartRollback)recordingTrackWarnings.Add("独立轨道提示："+error.Message);});
        capture.StateChanged += (_, state) => Dispatcher.BeginInvoke(async () =>
        {
            UpdateDesktopRecordingState();
            TrackRecordingClock(state);
            if (state.State == CaptureState.Faulted)
            {
                recordingGuard.Stop();
                if (recordingFolder != null && recordingStart > 0 && !recordingFaultHandled && !recordingStartRollback)
                {
                    recordingFaultHandled = true;
                    if (!recordingFinalizing) await Guard(StopRecording);
                }
            }
        });
    }
    private static async Task WarmUpPreviewAsync()
    {
        try { await GpuFrameRenderer.WarmUpShadersAsync().ConfigureAwait(false); }
        catch (Exception error) { App.Log(error); }
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshEmptyRecents();
        if (desktopResident) return;
        await Guard(RefreshDevices);
        ApplyUserPreferencesOnStartup(showFirstRunHelp: !Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--")));
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        var args = Environment.GetCommandLineArgs();
        var open = Array.IndexOf(args, "--open");
        if (open >= 0 && args.Length > open + 1) await Guard(() => OpenProject(args[open + 1]));
        var persistenceSmoke = Array.IndexOf(args, "--persistence-smoke");
        if (persistenceSmoke >= 0 && args.Length > persistenceSmoke + 1)
        {
            await VerifyPersistenceFailureRecovery(args[persistenceSmoke + 1]);
            closeReady = true; Close(); return;
        }
        var recordingIsolationSmoke = Array.IndexOf(args, "--recording-isolation-smoke");
        if (recordingIsolationSmoke >= 0 && args.Length > recordingIsolationSmoke + 1)
        {
            try { await RunRecordingIsolationSmoke(args[recordingIsolationSmoke + 1]); }
            catch (Exception error) { Environment.ExitCode = 1; App.Log(error); }
            closeReady = true; Close(); return;
        }

        var recordingBarSmoke=Array.IndexOf(args,"--recordingbar-lifecycle-smoke");if(recordingBarSmoke>=0&&recordingBarSmoke+1<args.Length){await VerifyRecordingBarLifecycle(args[recordingBarSmoke+1]);closeReady=true;Close();return;}
        var editorSmoke=Array.IndexOf(args,"--editor-smoke");if(editorSmoke>=0&&args.Length>editorSmoke+1){await VerifyEditorRegression(args[editorSmoke+1]);closeReady=true;Close();return;}
        var recoverySmoke=Array.IndexOf(args,"--recovery-smoke");if(recoverySmoke>=0&&args.Length>recoverySmoke+2){try{var recovered=await RecoverReadableRecording(args[recoverySmoke+1]);var p=FrameTrailProjectStore.Load(recovered);await File.WriteAllTextAsync(args[recoverySmoke+2],JsonSerializer.Serialize(new{Passed=true,Project=recovered,AudioTracks=p.AudioTracks.Count,Markers=p.Markers.Count,PointerEvents=p.PointerEvents.Count}));}catch(Exception ex){await File.WriteAllTextAsync(args[recoverySmoke+2],JsonSerializer.Serialize(new{Passed=false,Error=ex.ToString()}));}closeReady=true;Close();return;}
        var playbackSmoke=Array.IndexOf(args,"--playback-smoke");if(playbackSmoke>=0&&args.Length>playbackSmoke+1){await VerifyRealtimePlayback(args[playbackSmoke+1]);closeReady=true;Close();return;}
        var exportSmoke=Array.IndexOf(args,"--export-panel-smoke");if(exportSmoke>=0&&args.Length>exportSmoke+1){await VerifyExportPanel(args[exportSmoke+1]);closeReady=true;Close();return;}
        var visuals=Array.IndexOf(args,"--visual-states");
        if(visuals>=0&&args.Length>visuals+1){await Guard(()=>CaptureVisualStates(args[visuals+1]));closeReady=true;Close();return;}
        var workflow = Array.IndexOf(args, "--workflow-smoke");
        if (workflow >= 0 && args.Length > workflow + 1)
        {
            diagnosticRecordingRoot = Path.GetFullPath(args[workflow + 1]); Directory.CreateDirectory(diagnosticRecordingRoot);
            var checkDesktopIcons = args.Contains("--with-hidden-desktop-icons");
            var originalIconsHidden = checkDesktopIcons && new DesktopIconShell().Hidden;
            if (checkDesktopIcons)
            {
                hideDesktopIconsDuringRecording = true;
                Microphone.IsChecked = false;
                RecordCamera.IsChecked = false;
                CaptureResolution.SelectedIndex = 0;
                CaptureFps.SelectedIndex = 0;
            }
            try
            {
                using var inputStimulus=args.Contains("--with-input-stimulus")?new WorkflowInputStimulus():null;SystemSound.IsChecked=true; await ToggleRecording();
                if (checkDesktopIcons && !new DesktopIconShell().Hidden) throw new InvalidOperationException("录制开始后桌面图标仍可见。");
                await Task.Delay(1200); AddMarker("diagnostic");
                if(args.Contains("--with-pause")){await ToggleRecordingPause();await Task.Delay(1100);await ToggleRecordingPause();}
                if(inputStimulus!=null)await inputStimulus.ExerciseAsync();var secondsIndex=Array.IndexOf(args,"--record-seconds");var recordSeconds=secondsIndex>=0&&secondsIndex+1<args.Length?double.Parse(args[secondsIndex+1],CultureInfo.InvariantCulture):3.5;if(!double.IsFinite(recordSeconds)||recordSeconds<1.2||recordSeconds>3600)throw new ArgumentOutOfRangeException("--record-seconds");await Task.Delay(TimeSpan.FromSeconds(recordSeconds-1.2));await StopRecording();
                if (checkDesktopIcons && new DesktopIconShell().Hidden != originalIconsHidden) throw new InvalidOperationException("录制结束后桌面图标状态未恢复。");
                var originalDuration=project!.Timeline.Duration;if(inputStimulus!=null&&(project.PointerEvents.Count(x=>x.Kind==PointerEventKind.LeftClick)<2||project.ZoomClips.Count==0))throw new InvalidOperationException("真实点击未保存到指针轨道或未生成自动运镜。");
                Edit(p => { var split=p.Timeline.Split(p.Timeline.Segments[0].Id,originalDuration/2); p.Timeline.SetPlaybackRate(split.Right.Id,2); p.Timeline.Reorder(split.Right.Id,0); });
                await OpenProject(projectFile!);
                await RunExport(Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4"),new(1280,720,30,true),false);
                await RenderPreview(project!.Timeline.Duration/3);
                await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=true, DesktopIconsVerified=checkDesktopIcons, Project=projectFile, OriginalDuration=originalDuration, EditedDuration=project.Timeline.Duration, PointerEvents=project.PointerEvents.Count, Markers=project.Markers.Count, Clicks=project.PointerEvents.Count(x=>x.Kind==PointerEventKind.LeftClick), AutoZoomClips=project.ZoomClips.Count(x=>x.Automatic), StimulusClicks=inputStimulus?.ReceivedClicks, Export=Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4") },new JsonSerializerOptions { WriteIndented=true }));
                SaveSnapshot(Path.Combine(diagnosticRecordingRoot,"editor-project.png"));
            }
            catch(Exception ex) { App.Log(ex); await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=false,Error=ex.ToString() })); }
            await capture.DisposeAsync(); await RestoreDesktopIconsAsync(); closeReady=true; Close(); return;
        }
        var smoke = Array.IndexOf(args, "--smoke");
        if (smoke >= 0 && args.Length > smoke + 1)
        {
            await Task.Delay(1600); // Only the diagnostic snapshot waits for the app to lay out.
            SaveSnapshot(args[smoke + 1]);
            closeReady = true; Close();
        }
    }
    private void SaveSnapshot(string path)
    {
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); png.Save(stream);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    { if (msg == 0x0312 && wp.ToInt32() == RecordingMarkerHotKey.Id) { AddMarker("hotkey"); handled = true; } return IntPtr.Zero; }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status.Text = "处理已取消，原始素材和项目已保留。"; }
        catch (MicrophoneAccessDeniedException ex)
        {
            App.Log(ex);
            Status.Text = "Windows 拒绝访问麦克风。检查权限后可重新开始录制。";
            MicrophoneAccessRecovery.Show(DesktopDialogOwner());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException && IsVisible && WindowState != WindowState.Minimized)
        {
            // Expected, user-correctable conditions ("请先选择…", "此处已有同轨道动画") are shown as a
            // transient notice like Mac EditorTransientNotice instead of a blocking modal dialog.
            App.Log(ex); Status.Text = ex.Message; ShowNotice(ex.Message, warning: true);
        }
        catch (Exception ex) { App.Log(ex); Status.Text = ex.Message; SurfaceDialog.Show(DesktopDialogOwner(), ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task RefreshDevices()
    {
        if (!IsLoaded && !desktopResident) return;
        var oldMicrophone=(MicDevices.SelectedItem as AudioDeviceInfo)?.Id;var oldCamera=(CameraDevices.SelectedItem as CameraInfo)?.Id;
        var list = new List<TargetItem>();
        if (CaptureKind.SelectedIndex == 1)
        {
            var windows = await capture.EnumerateWindowsAsync();
            var visibleWindows = WindowCaptureSourceFilter.Filter(
                windows,
                Environment.ProcessId,
                new WindowInteropHelper(this).Handle,
                excludeEditorDuringRecording);
            list.AddRange(visibleWindows.Select(x => new TargetItem(x.Title, new(CaptureTargetKind.Window, x.Id, x.Bounds, x.Handle))));
        }
        else list.AddRange((await capture.EnumerateDisplaysAsync()).Select(x => new TargetItem($"{x.Name} · {x.Bounds.Width}×{x.Bounds.Height}", new(CaptureTargetKind.Display, x.Id, x.Bounds))));
        Targets.ItemsSource = list; Targets.SelectedIndex = 0;
        MicDevices.ItemsSource = (await trackCapture.EnumerateAudioDevicesAsync()).Where(x => x.Kind == AudioDeviceKind.Microphone).ToArray(); MicDevices.SelectedItem=MicDevices.Items.Cast<AudioDeviceInfo>().FirstOrDefault(x=>x.Id==oldMicrophone)??MicDevices.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        CameraDevices.ItemsSource = await trackCapture.EnumerateCamerasAsync(); CameraDevices.SelectedItem=CameraDevices.Items.Cast<CameraInfo>().FirstOrDefault(x=>x.Id==oldCamera)??CameraDevices.Items.Cast<CameraInfo>().FirstOrDefault();
        RegionButton.Visibility = CaptureKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CaptureKindChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded&&!changingRecorderMode) await Guard(RefreshDevices); }
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e) => await Guard(RefreshDevices);
    private void SelectRegionClick(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not TargetItem target || target.Target.Bounds is null) return;
        var selector = new RegionSelector(target.Target.Bounds); if (selector.ShowDialog() == true) { region = selector.Region; RegionLabel.Text = $"{region!.X},{region.Y} · {region.Width}×{region.Height} px"; }
    }
    private async void RecordClick(object sender, RoutedEventArgs e) => await Guard(ToggleRecording);
    private static FrameTrailProject MakeProject(string media, MediaInfo info) => new()
    {
        MediaPath = media, Media = new() { Width = info.Width, Height = info.Height, Duration = info.Duration,FrameRate=info.FrameRate>0?info.FrameRate:30 },
        Timeline = new() { Segments = [new() { SourceDuration = info.Duration }] }, Settings = EffectDefaults.NewProjectSettings(info.Width, info.Height)
    };
    private async Task OpenProject(string path)
    {
        // Mac requestOpenProject / beginProjectOpen: refuse while exporting, processing media or recording,
        // and write the current project to disk before replacing it.
        CommitPendingInput();
        EnsureCanSwitchProject();
        SaveBeforeSwitchingProject();
        StopPlayback(); seekCancellation?.Cancel();
        FrameTrailProject opened;
        try { opened = FrameTrailProjectStore.Load(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException && File.Exists(path + ".bak"))
        {
            if (SurfaceDialog.Show(this, "项目文件未能读取。是否保留损坏文件并恢复最近备份？\n" + ex.Message, "恢复项目", MessageBoxButton.YesNo) != MessageBoxResult.Yes) throw;
            File.Copy(path, path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); opened = FrameTrailProjectStore.RestoreBackup(path);
        }
        using var relocation = new ProjectAssetRelocationTransaction();
        Dictionary<string,double> openedAuxiliaryDurations = new(StringComparer.OrdinalIgnoreCase);
        string? auxiliaryWarning = null;
        var mainRelocated = false;
        string media;
        try
        {
            media = FrameTrailProjectStore.ResolveMediaPath(path, opened);
            MediaInfo? sourceInfo = null;
            Exception? sourceError = null;
            if (File.Exists(media))
            {
                try
                {
                    var existingSourceInfo = await MediaTools.ProbeAsync(media);
                    if (!MainMediaCoversTimeline(existingSourceInfo, opened)) throw new InvalidDataException("项目的主素材无法覆盖当前时间线范围，或没有可用视频帧。");
                    await MediaTools.DecodeFrameAsync(media, 0, 16, 16, CancellationToken.None);
                    sourceInfo = existingSourceInfo;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { sourceError = ex; App.Log(ex); }
            }
            if (sourceInfo is null)
            {
                if (sourceError is not null) SurfaceDialog.Show(this, "项目的主素材无法读取，请重新定位可解码的视频。\n\n" + sourceError.Message, "主素材无法读取", MessageBoxButton.OK, MessageBoxImage.Warning);
                while (true)
                {
                    var dialog = new OpenFileDialog { Title = "素材缺失或无法读取：请选择原视频，副本将保存到项目目录", Filter = "视频|*.mp4;*.mov;*.mkv;*.avi" };
                    if (dialog.ShowDialog(this) != true) throw new OperationCanceledException("主素材重定位已取消，项目未修改。");
                    MediaInfo candidateInfo;
                    try
                    {
                        candidateInfo = await MediaTools.ProbeAsync(dialog.FileName);
                        if (!MainMediaCoversTimeline(candidateInfo, opened)) throw new InvalidDataException("重新定位的文件缺少视频或时长过短，无法匹配时间线。");
                        await MediaTools.DecodeFrameAsync(dialog.FileName, 0, 16, 16, CancellationToken.None);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        App.Log(ex); SurfaceDialog.Show(this, "所选文件无法用于主视频，请重新选择。\n\n" + ex.Message, "素材验证失败", MessageBoxButton.OK, MessageBoxImage.Warning); continue;
                    }
                    var newRelative = "media/relocated-" + Guid.NewGuid().ToString("N") + Path.GetExtension(dialog.FileName);
                    media = FrameTrailProjectStore.ResolveAssetPath(path, newRelative);
                    relocation.Copy(dialog.FileName, media);
                    opened.MediaPath = newRelative; opened.Media = new() { Width = candidateInfo.Width, Height = candidateInfo.Height, Duration = candidateInfo.Duration };
                    mainRelocated = true;
                    sourceInfo = candidateInfo;
                    break;
                }
            }
            var validatedSourceInfo = sourceInfo ?? throw new InvalidDataException("项目的主素材没有通过验证。");
            opened.Media.FrameRate = validatedSourceInfo.FrameRate > 0 ? validatedSourceInfo.FrameRate : 30;
            var auxiliaryRelocated = await RelocateMissingAuxiliaryAssets(opened, path, relocation);
            var auxiliaryLoad = await LoadAuxiliaryDurations(opened, path);
            openedAuxiliaryDurations = auxiliaryLoad.Durations;
            auxiliaryWarning = auxiliaryLoad.Warning;
            relocation.Commit(() =>
            {
                if (mainRelocated || auxiliaryRelocated || auxiliaryLoad.Changed)
                    FrameTrailProjectStore.Save(path, opened);
            });
        }
        catch
        {
            foreach (var cleanupError in relocation.Rollback()) App.Log(cleanupError);
            throw;
        }
        // F-01: older projects relied on the evaluator re-planning zooms (AutoZoom with no stored clips); store the plan once (not an undo step).
        if (ZoomClipRules.MaterializeLegacyAutoZoom(opened)) { try { FrameTrailProjectStore.Save(path, opened); } catch (Exception ex) { App.Log(ex); } }
        auxiliaryDurations.Clear();
        foreach (var entry in openedAuxiliaryDurations) auxiliaryDurations[entry.Key] = entry.Value;
        freshRecordingProject = IsFirstOpenOfRecordedProject(path) ? Path.GetFullPath(path) : null;
        project = opened; projectFile = path; selectedEffect=null;TimelineView.MultiSelection.Clear();TimelineView.RangeStart=null;TimelineView.RangeEnd=null; history = new(project, Clone); timelineAssets=null; InvalidateProxy(); RefreshProject(); Playhead.Value = 0; RecentProjects.Add(path); Status.Text = ProjectOpenedStatus(path, auxiliaryWarning); _=LoadTimelineAssets(media,opened); await RenderPreview(0);
    }
    private static bool MainMediaCoversTimeline(MediaInfo info, FrameTrailProject candidate)
    {
        if (info.Width < 1 || !double.IsFinite(info.Duration) || info.Duration <= 0 || candidate.Timeline.Segments.Count == 0) return false;
        var requiredEnd = candidate.Timeline.Segments.Max(segment => segment.SourceEnd);
        return double.IsFinite(requiredEnd) && info.Duration + .05 >= requiredEnd;
    }
    private async void ImportClick(object sender, RoutedEventArgs e) => await ImportVideoFromMenu();
    private static FrameTrailProject Clone(FrameTrailProject p) => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(p))!;
    private void SaveProject() { if (project != null && projectFile != null) { try { FrameTrailProjectStore.Save(projectFile, project); } catch (Exception ex) { SetSaveState(false, ex.Message); throw; } Status.Text = "已保存"; SetSaveState(true); } }
    private void RefreshProject()
    {
        if (project is null) return; refreshing = true;
        var selected = (Clips.SelectedItem as ClipItem)?.Id;
        Clips.ItemsSource = project.Timeline.Segments.Select((s,i) => new ClipItem(s.Id, $"片段 {i+1} · {s.OutputDuration:0.00}s", $"{s.SourceStart:0.00}–{s.SourceEnd:0.00}s    ×{s.PlaybackRate:0.##}",timelineAssets?.Thumbnails.OrderBy(x=>Math.Abs(x.SourceTime-s.SourceStart)).FirstOrDefault()?.Image)).ToArray();
        Clips.SelectedItem = Clips.Items.Cast<ClipItem>().FirstOrDefault(x => x.Id == selected) ?? Clips.Items.Cast<ClipItem>().FirstOrDefault();
        Playhead.Maximum = project.Timeline.Duration; Playhead.Value = Math.Min(Playhead.Value, Playhead.Maximum);
        Markers.ItemsSource = project.Markers.Select(x => new MarkerItem(x.Id, x.SourceTime, x.Label)).ToArray();
        PaddingSlider.Value = project.Settings.Padding; RadiusSlider.Value = project.Settings.CornerRadius; ShadowSlider.Value = project.Settings.Shadow; BackgroundColor.Text = project.Settings.Background; ShowCursor.IsChecked = project.Settings.CursorVisible; MouseOffset.Text = (project.MouseClock.OffsetSeconds*1000).ToString("0.###", CultureInfo.CurrentCulture);
        EmptyState.Visibility = Visibility.Collapsed; ProjectTitle.Text = project.DisplayName ?? Path.GetFileName(Path.GetDirectoryName(projectFile));
        TimelineInfo.Text = $"{project.Timeline.Segments.Count} 个片段 · {project.ZoomClips.Count} 段运镜 · {project.PointerEvents.Count} 条鼠标事件 · 编辑后自动保存";
        refreshing = false; UpdateTime(); DrawWaveform(); RefreshEditorShell();
    }
    private async Task LoadTimelineAssets(string path,FrameTrailProject opened)
    {
        assetsCancellation?.Cancel(); assetsCancellation=new(); var token=assetsCancellation.Token;
        AutomationProperties.SetItemStatus(TimelineView, "loading");
        try
        {
            var data=await TimelineAssets.BuildAsync(path,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","thumbnail-cache"),token);
            if(token.IsCancellationRequested || project?.ProjectId!=opened.ProjectId)return;
            timelineAssets=data; RefreshProject();
            AutomationProperties.SetItemStatus(TimelineView, "ready");
        }
        catch(Exception ex) when(ex is OperationCanceledException || token.IsCancellationRequested) { }
        catch(Exception ex) { AutomationProperties.SetItemStatus(TimelineView, "error"); App.Log(ex); Status.Text="缩略图或波形生成失败："+ex.Message; }
    }
    private void DrawWaveform()
    {
        if(Waveform==null)return; Waveform.Children.Clear();
        if(project==null||timelineAssets==null||timelineAssets.Peaks.Count==0||Waveform.ActualWidth<1)return;
        var points=new PointCollection(); var width=Waveform.ActualWidth; var bins=Math.Min(1000,(int)width);
        for(var i=0;i<bins;i++)
        {
            var sourceTime=project.Timeline.MapOutputToSource(project.Timeline.Duration*i/bins);
            var index=Math.Clamp((int)(sourceTime/timelineAssets.Duration*timelineAssets.Peaks.Count),0,timelineAssets.Peaks.Count-1);
            var peak=timelineAssets.Peaks[index]*11; points.Add(new(i*width/bins,12-peak)); points.Add(new(i*width/bins,12+peak));
        }
        Waveform.Children.Add(new System.Windows.Shapes.Polyline { Points=points,Stroke=new SolidColorBrush(Color.FromRgb(126,175,162)),StrokeThickness=1 });
    }
    /// <summary>
    /// Commits one undoable edit. Matches Mac EditorStore.apply: a change that leaves the project
    /// identical is dropped (no empty undo step, redo stack kept), and the optional label names the
    /// step for undo/redo feedback. Any uncommitted live preview is discarded.
    /// </summary>
    private void Edit(Action<FrameTrailProject> edit, string? label = null)
    {
        if (project is null || history is null) return;
        // A gesture that is still held must not commit over this edit when it is released (P1-2).
        AbortActiveGestures();
        var hadLivePreview = liveProject is not null;
        ClearLiveEdit();
        // Serialize once: the same text is the clone source and the "nothing changed" baseline.
        var baseline = JsonSerializer.Serialize(project);
        FrameTrailProject next;
        try { next = JsonSerializer.Deserialize<FrameTrailProject>(baseline)!; edit(next); NormalizeEdit(project, next); next.Timeline.Validate(); }
        catch
        {
            // A rejected edit leaves the committed project; do not keep showing the abandoned live frame (P2-1).
            if (hadLivePreview) _ = Guard(RenderCommittedPreview);
            throw;
        }
        if (JsonSerializer.Serialize(next) == baseline)
        {
            if (hadLivePreview) _ = Guard(RenderCommittedPreview);
            return;
        }
        StopPlayback();
        try { FrameTrailProjectStore.Save(projectFile!, next); }
        catch (Exception ex)
        {
            RefreshProject(); SetSaveState(false, ex.Message);
            if (hadLivePreview) _ = Guard(RenderCommittedPreview);
            throw new IOException("保存失败，本次编辑未应用。解除文件占用后请重新执行该操作。", ex);
        }
        var before = project; history.Commit(next, label); project = next; InvalidateProxyAfterEdit(before, next);
        // F-07: a deliberate scale change on a zoom becomes the creation default (it is part of the same undo step via Settings.DefaultZoomScale).
        if (next.Settings.DefaultZoomScale is { } newDefaultZoom && before.Settings.DefaultZoomScale != next.Settings.DefaultZoomScale) RememberZoomCreationScale(newDefaultZoom);
        RefreshProject(); Status.Text = "已保存"; SetSaveState(true); _ = Guard(RenderCommittedPreview);
    }
    private static void NormalizeEdit(FrameTrailProject before, FrameTrailProject next)
    {
        // F-01: emptying the zoom lane by any path (inspector, live-edit commit, timeline) also turns auto zoom off.
        ZoomClipRules.NormalizeAfterEdit(before.ZoomClips.Count, next);
        foreach (var motion in next.ScreenMotions) motion.EnterDuration = Math.Min(motion.EnterDuration, motion.Duration);
        foreach (var motion in next.CameraMotions) motion.EnterDuration = Math.Min(motion.EnterDuration, motion.Duration);
        ApplyAdaptiveCanvas(next);
    }
    private TimelineSegment Selected(FrameTrailProject p) => p.Timeline.Segments.FirstOrDefault(x => x.Id == (Clips.SelectedItem as ClipItem)?.Id) ?? throw new InvalidOperationException("请选择一个片段。");
    private double SelectedSourceAtPlayhead(FrameTrailProject p,TimelineSegment selected)
    {
        var start=0d; foreach(var s in p.Timeline.Segments) { if(s.Id==selected.Id)break; start+=s.OutputDuration; }
        if(Playhead.Value<start-1e-8||Playhead.Value>start+selected.OutputDuration+1e-8) throw new InvalidOperationException("请把播放头放在选中片段内，再执行分割或裁剪。");
        return selected.SourceStart+Math.Clamp(Playhead.Value-start,0,selected.OutputDuration)*selected.PlaybackRate;
    }
    private void ClipSelected(object sender, SelectionChangedEventArgs e) { if (refreshing || project == null || Clips.SelectedItem is not ClipItem item) return; var time = 0d; foreach (var s in project.Timeline.Segments) { if (s.Id == item.Id) break; time += s.OutputDuration; } Playhead.Value = time; }
    private void UndoClick(object sender, RoutedEventArgs e) => _ = Guard(() => { ApplyHistoryChange(false); return Task.CompletedTask; });
    private void RedoClick(object sender, RoutedEventArgs e) => _ = Guard(() => { ApplyHistoryChange(true); return Task.CompletedTask; });
    private void ApplyHistoryChange(bool redo)
    {
        if (history is null || (redo ? !history.CanRedo : !history.CanUndo)) return;
        // A held gesture must not re-commit its old result after the undo (P1-2).
        AbortActiveGestures(); ClearLiveEdit(); StopPlayback();
        var selection = CaptureEffectSelectionForHistory();
        var label = redo ? history.RedoLabel : history.UndoLabel;
        void Persist(FrameTrailProject next) => FrameTrailProjectStore.Save(projectFile!, next);
        var projectBefore = project;
        project = redo ? history.Redo(Persist) : history.Undo(Persist);
        Status.Text = (redo ? "已重做" : "已撤销") + (label is null ? "" : "：" + label);
        ShowNotice(Status.Text);
        // One normalisation so selectedEffect, the timeline selection, the selected screen segment and the inspector tab agree (P2-8).
        InvalidateProxyAfterEdit(projectBefore!, project); RestoreEffectSelectionAfterHistory(selection); NormalizeSelectionAfterHistory(); RefreshProject();
        // T-07 (Mac revealPlayheadIfOutsideViewport): undo/redo can move the content under the playhead out of the viewport.
        Dispatcher.BeginInvoke(RevealTimelinePlayhead, System.Windows.Threading.DispatcherPriority.Background);
        _ = Guard(RenderCommittedPreview);
    }
    private void PlayheadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || project == null) return;
        // Timeline snapping lives in EditorTimelineControl; this slider is not part of the visual tree.
        StopPlayback();  UpdateTime(); _ = Guard(() => RenderTimelinePreview(Playhead.Value));
    }
    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}" : t.ToString("mm\\:ss\\.ff");
    }
    private void UpdateTime() { TimeLabel.Text = $"{FormatTime(Playhead.Value)} / {FormatTime(Playhead.Maximum)}"; TimelineView.Position=Playhead.Value; UpdatePlaybackAvailability(); }
    private void PreviousFrame(object sender, RoutedEventArgs e) => StepFrames(-1);
    private void NextFrame(object sender, RoutedEventArgs e) => StepFrames(1);
    private void InvalidateProxy() { _ = DisposePlaybackResourcesAsync(); ClosePlaybackAudio(); if (proxyPath != null && File.Exists(proxyPath)) { try { File.Delete(proxyPath); } catch (IOException) { } } proxyPath=null; playbackProxyProject=null; }
    private void StopPlayback() { var old=playbackCancellation; playbackCancellation=null; playing=false; playbackStarting=false; Player.Pause(); Player.Visibility=Visibility.Collapsed; PreviewImage.Visibility=Visibility.Visible; PlayButton.Content=new EditorIcon{Kind="play",Width=18,Height=18}; if(old is not null) _=old.CancelAsync(); RefreshPreviewSelection(); }
    private async void PlayClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (cropMode) return; if (playing || playbackStarting) { StopPlayback(); return; } if (project == null || projectFile == null || operationCancellation != null) return;
        await StartRealtimePlayback();
    });
    private void PlaybackEnded(object sender,RoutedEventArgs e)
    {
        // The audio proxy can end before the timeline does; only finish when the video clock has reached the end too.
        if (!playing || project == null || Playhead.Value < project.Timeline.Duration - .25) return;
        StopPlayback(); SetTimelinePosition(0, coalesce: false);
    }
    private ExportOptions Options(bool audio) { var w=Resolution.SelectedIndex switch {0=>(int)(project?.Media.Width??1920),2=>2560,3=>3840,_=>1920}; var h=Resolution.SelectedIndex==0?(int)(project?.Media.Height??1080):(int)Math.Round(w*(double)(project?.Settings.CanvasHeight??1080)/(project?.Settings.CanvasWidth??1920)); return new(w/2*2,Math.Max(2,h/2*2),ExportFps.SelectedIndex==1?60:30,HardwareEncoder.IsChecked==true,audio); }
    private async void ExportClick(object sender,RoutedEventArgs e) => await Guard(() => ChooseExport(false));
    private Task ChooseExport(bool audio) => ShowExportWindowAsync(audio);
    private async Task RunExport(string path,ExportOptions options,bool preview,CancellationToken cancellation=default)
    {
        if (project==null || projectFile==null) return; if (operationCancellation!=null) throw new InvalidOperationException("已有媒体处理任务运行。");
        if(!preview)StopPlayback(); seekCancellation?.Cancel(); operationCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation); CancelButton.Visibility=Visibility.Visible; Progress.Visibility=Visibility.Visible; ExportButton.IsEnabled=false; PlayButton.IsEnabled=false;
        var file=projectFile;var snapshot=Clone(project); var source=FrameTrailProjectStore.ResolveMediaPath(file,snapshot);
        try
        {
            var report=new Progress<ExportProgress>(p=>{ Progress.Value=p.Fraction; Status.Text=$"{(preview?"准备有声预览 · ":"")}{p.Stage} · 已用 {p.Elapsed:mm\\:ss}"+(p.Remaining.HasValue?$" · 预计剩余 {p.Remaining:mm\\:ss}":""); });
            var result=await Task.Run(()=>new ExportEngine().ExportAsync(snapshot,source,path,options with{ProjectFilePath=file},report,operationCancellation.Token)); Status.Text=$"完成 · {result.Encoder} · {result.Renderer} · {result.Elapsed.TotalSeconds:0.0}s";
        }
        finally { operationCancellation.Dispose(); operationCancellation=null; CancelButton.Visibility=Visibility.Collapsed; Progress.Visibility=Visibility.Collapsed; ExportButton.IsEnabled=true; PlayButton.IsEnabled=true; }
    }
    private void CancelClick(object sender,RoutedEventArgs e) => operationCancellation?.Cancel();
    private void MemoClick(object sender,RoutedEventArgs e) => ShowMemoForDesktop();
    private async void RecoverRecordingClick(object sender,RoutedEventArgs e) => await Guard(async()=>
    {
        // Start in the folder the user chose for recordings (not a fixed Videos\FrameTrail). Recovered material goes through the same result card as a new recording.
        var dialog=new OpenFileDialog { Title="选择未完成录制目录里的 recording-recovery.json",Filter="未完成录制记录|recording-recovery.json",InitialDirectory=RecordingRootDirectory };
        if(dialog.ShowDialog(DesktopDialogOwner())!=true)return;
        await RecoverAndPresentAsync(Path.GetDirectoryName(dialog.FileName)!);
    });
    private void WindowKeyDown(object sender,KeyEventArgs e)
    {
        if (HandleCanvasKey(e)) return;
        HandleTimelineShortcut(e);
    }
    private async void WindowClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady) return; e.Cancel=true;
        CommitPendingInput();
        if(desktopResident)
        {
            if(!desktopExiting)
            {
                // Mac requestCloseProject: ask about a running export / an unedited fresh recording first, then let go of playback resources.
                // Closing the editor brings the recorder start bar back (Mac closeProject -> phase .setup).
                if(desktopTrayAvailable) await Guard(async () =>
                {
                    if(!await ConfirmCloseEditorAsync()) return;
                    await HideEditorForDesktopAsync();
                    if(!IsVisible && !desktopExiting && capture.State is not (CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping) && !recordingFinalizing && completionWindow==null) _ = Guard(ActivateDesktopRecorderAsync);
                });
                else await RequestDesktopExitAsync();
            }
            return;
        }
        if(closingInProgress) return;
        if(capture.State is CaptureState.Recording or CaptureState.Starting or CaptureState.Paused) { SurfaceDialog.Show(this,"请先结束录制，确保素材完成封装。","录制进行中"); return; }
        closingInProgress=true;
        try
        {
            await Guard(async()=>
            {
                // A running media task is cancelled and awaited so a single close click is enough.
                if(operationCancellation!=null) { operationCancellation.Cancel(); Status.Text="正在取消处理，完成后将关闭。"; await WaitForOperationEndAsync(); }
                if(!await ConfirmCloseEditorAsync()) return;
                await ReleaseApplicationAsync();
            });
        }
        finally { if(!closeReady) closingInProgress=false; }
    }
}
