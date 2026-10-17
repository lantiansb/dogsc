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
    private readonly RecordingMarkerHotKey markerHotKey = new();
    private readonly List<(long Tick, string Label)> recordingMarkers = [];
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
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
        InitializeComponent(); InitializeEditorShell(); SurfaceWindow.AttachNativeEdges(this);
        MainWindowPlacement.Attach(this);
        SurfaceWindow.FitToWorkArea(this);
        AutomationProperties.SetItemStatus(PreviewImage, "idle");
        AutomationProperties.SetItemStatus(TimelineView, "idle");
        Closed += (_, _) => { markerHotKey.Dispose();seekCancellation?.Cancel(); assetsCancellation?.Cancel(); operationCancellation?.Cancel(); recordingBar?.CloseAfterRecording(); recordingBar=null; recordingGuard.Stop(); };
        recordingGuard.Tick += async (_, _) =>
        {
            if(capture.State is not (CaptureState.Recording or CaptureState.Paused) || protectiveStop||recordingFinalizing)return;
            using var process=Process.GetCurrentProcess(); process.Refresh();
            string? reason=process.PrivateMemorySize64>3L*1024*1024*1024||capture.CurrentCapturePrivateBytes>2L*1024*1024*1024 ? "录制进程达到内存保护阈值，请降低录制分辨率后重试。" : null;
            if(recordingFolder!=null && new DriveInfo(Path.GetPathRoot(recordingFolder)!).AvailableFreeSpace<300L*1024*1024)reason="录制盘剩余空间低于 300MiB。";
            if(reason==null)return; protectiveStop=true;recordingGuard.Stop();await Guard(async()=>{await StopRecording();Status.Text="已保护性结束并保留素材："+reason;});protectiveStop=false;
        };
        capture.ErrorOccurred += (_, error) => Dispatcher.BeginInvoke(() => { Status.Text = "录制提示：" + error.Message; App.Log(error.Exception ?? new Exception(error.Message)); });
        trackCapture.ErrorOccurred += (_,error)=>Dispatcher.BeginInvoke(async()=>{App.Log(error.Exception??new Exception(error.Message));if(!recordingFinalizing&&capture.State is CaptureState.Recording or CaptureState.Paused)await Guard(StopRecording);Status.Text="独立轨道录制中断，已保留可读素材："+error.Message;});
        capture.RecordingCompleted += result=>Dispatcher.BeginInvoke(async()=>{if(recordingFinalizing||recordingFolder==null)return;recordingFinalizing=true;try{await Guard(()=>CompleteRecording(result));}finally{recordingFinalizing=false;}});
        capture.StateChanged += (_, state) => Dispatcher.BeginInvoke(async () =>
        {
            UpdateDesktopRecordingState();
            if (state.State == CaptureState.Recording && recordingStart==0) recordingStart = state.StopwatchTicks;
            if (state.State == CaptureState.Faulted)
            {
                recordingGuard.Stop();
                if (recordingFolder != null && recordingStart > 0 && !recordingFaultHandled)
                {
                    recordingFaultHandled = true;
                    if (!recordingFinalizing) await Guard(StopRecording);
                }
            }
        });
        playbackTimer.Tick += (_, _) => { if (!playing) return; refreshing = true; Playhead.Value = Math.Min(Player.Position.TotalSeconds, Playhead.Maximum); refreshing = false; UpdateTime(); };
    }
    private static async Task WarmUpPreviewAsync()
    {
        try { await GpuFrameRenderer.WarmUpShadersAsync().ConfigureAwait(false); }
        catch (Exception error) { App.Log(error); }
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (desktopResident) return;
        await Guard(RefreshDevices);
        ApplyUserPreferencesOnStartup(showFirstRunHelp: !Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--")));
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        RegisterRecordingMarkerHotKey();
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
            try
            {
                using var inputStimulus=args.Contains("--with-input-stimulus")?new WorkflowInputStimulus():null;SystemSound.IsChecked=true; await ToggleRecording();
                await Task.Delay(1200); AddMarker("diagnostic");
                if(args.Contains("--with-pause")){await ToggleRecordingPause();await Task.Delay(1100);await ToggleRecordingPause();}
                if(inputStimulus!=null)await inputStimulus.ExerciseAsync();var secondsIndex=Array.IndexOf(args,"--record-seconds");var recordSeconds=secondsIndex>=0&&secondsIndex+1<args.Length?double.Parse(args[secondsIndex+1],CultureInfo.InvariantCulture):3.5;if(!double.IsFinite(recordSeconds)||recordSeconds<1.2||recordSeconds>3600)throw new ArgumentOutOfRangeException("--record-seconds");await Task.Delay(TimeSpan.FromSeconds(recordSeconds-1.2));await StopRecording();
                var originalDuration=project!.Timeline.Duration;if(inputStimulus!=null&&(project.PointerEvents.Count(x=>x.Kind==PointerEventKind.LeftClick)<2||project.ZoomClips.Count==0))throw new InvalidOperationException("真实点击未保存到指针轨道或未生成自动运镜。");
                Edit(p => { var split=p.Timeline.Split(p.Timeline.Segments[0].Id,originalDuration/2); p.Timeline.SetPlaybackRate(split.Right.Id,2); p.Timeline.Reorder(split.Right.Id,0); });
                await OpenProject(projectFile!);
                await RunExport(Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4"),new(1280,720,30,true),false);
                await RenderPreview(project!.Timeline.Duration/3);
                await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=true, Project=projectFile, OriginalDuration=originalDuration, EditedDuration=project.Timeline.Duration, PointerEvents=project.PointerEvents.Count, Markers=project.Markers.Count, Clicks=project.PointerEvents.Count(x=>x.Kind==PointerEventKind.LeftClick), AutoZoomClips=project.ZoomClips.Count(x=>x.Automatic), StimulusClicks=inputStimulus?.ReceivedClicks, Export=Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4") },new JsonSerializerOptions { WriteIndented=true }));
                SaveSnapshot(Path.Combine(diagnosticRecordingRoot,"editor-project.png"));
            }
            catch(Exception ex) { App.Log(ex); await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=false,Error=ex.ToString() })); }
            await capture.DisposeAsync(); closeReady=true; Close(); return;
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
        catch (Exception ex) { App.Log(ex); Status.Text = ex.Message; SurfaceDialog.Show(DesktopDialogOwner(), ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    /// <summary>
    /// 设备/窗口枚举的统一保护：异步查询加超时，超时或异常就跳过这一项。
    /// 异常设备（离线音频端点、虚拟显示器、MIDI 测试设备等）会让枚举无限期阻塞，
    /// 过去表现为整个程序 AppHang、界面永远出不来。
    /// </summary>
    private static async Task<T?> TryQueryAsync<T>(string what, Task<T> query, TimeSpan budget) where T : class
    {
        try
        {
            var result = await query.WaitAsync(budget);
            App.Trace($"devices: {what} ok");
            return result;
        }
        catch (TimeoutException)
        {
            App.Trace($"devices: {what} TIMEOUT after {budget.TotalSeconds:0}s, skipped");
            return null;
        }
        catch (Exception error)
        {
            App.Trace($"devices: {what} failed: {error.GetType().Name} {error.Message}");
            return null;
        }
    }

    private async Task RefreshDevices()
    {
        if (!IsLoaded && !desktopResident) return;
        App.Trace("RefreshDevices: begin");
        var budget = TimeSpan.FromSeconds(6);
        var oldMicrophone=(MicDevices.SelectedItem as AudioDeviceInfo)?.Id;var oldCamera=(CameraDevices.SelectedItem as CameraInfo)?.Id;
        var list = new List<TargetItem>();
        if (CaptureKind.SelectedIndex == 1)
        {
            var windows = await TryQueryAsync("windows", capture.EnumerateWindowsAsync(), budget);
            if (windows is not null)
            {
                var visibleWindows = WindowCaptureSourceFilter.Filter(
                    windows,
                    Environment.ProcessId,
                    new WindowInteropHelper(this).Handle,
                    excludeEditorDuringRecording);
                list.AddRange(visibleWindows.Select(x => new TargetItem(x.Title, new(CaptureTargetKind.Window, x.Id, x.Bounds, x.Handle))));
            }
        }
        else
        {
            var displays = await TryQueryAsync("displays", capture.EnumerateDisplaysAsync(), budget);
            if (displays is not null) list.AddRange(displays.Select(x => new TargetItem($"{x.Name} · {x.Bounds.Width}×{x.Bounds.Height}", new(CaptureTargetKind.Display, x.Id, x.Bounds))));
        }
        Targets.ItemsSource = list; Targets.SelectedIndex = list.Count > 0 ? 0 : -1;
        var microphones = await TryQueryAsync("microphones", trackCapture.EnumerateAudioDevicesAsync(), budget);
        if (microphones is not null)
        {
            MicDevices.ItemsSource = microphones.Where(x => x.Kind == AudioDeviceKind.Microphone).ToArray(); MicDevices.SelectedItem=MicDevices.Items.Cast<AudioDeviceInfo>().FirstOrDefault(x=>x.Id==oldMicrophone)??MicDevices.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        }
        var cameras = await TryQueryAsync("cameras", trackCapture.EnumerateCamerasAsync(), budget);
        if (cameras is not null)
        {
            CameraDevices.ItemsSource = cameras; CameraDevices.SelectedItem=CameraDevices.Items.Cast<CameraInfo>().FirstOrDefault(x=>x.Id==oldCamera)??CameraDevices.Items.Cast<CameraInfo>().FirstOrDefault();
        }
        RegionButton.Visibility = CaptureKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        App.Trace("RefreshDevices: end");
    }
    private async void CaptureKindChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded&&!changingRecorderMode) await Guard(RefreshDevices); }
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e) => await Guard(RefreshDevices);
    private void SelectRegionClick(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not TargetItem target || target.Target.Bounds is null) return;
        var selector = new RegionSelector(target.Target.Bounds); if (selector.ShowDialog() == true) { region = selector.Region; RegionLabel.Text = $"{region!.X},{region.Y} · {region.Width}×{region.Height} px"; }
    }
    private async void RecordClick(object sender, RoutedEventArgs e) => await Guard(ToggleRecording);
    private async Task ToggleRecording()
    {
        if (capture.State is CaptureState.Recording or CaptureState.Paused) { await StopRecording(); return; }
        if (Targets.SelectedItem is not TargetItem item) throw new InvalidOperationException("请选择录制来源。");
        var target = item.Target;
        if(excludeEditorDuringRecording&&target.WindowHandle==new WindowInteropHelper(this).Handle)throw new InvalidOperationException("编辑器正设置为从录制中排除；请在设置中关闭此选项后录制它。");
        if (CaptureKind.SelectedIndex == 2) target = new(CaptureTargetKind.Region, item.Target.Id, region ?? throw new InvalidOperationException("请先框选录制区域。"));
        recordingFolder = Path.Combine(diagnosticRecordingRoot ?? preferredRecordingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail"), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(recordingFolder, "media"));
        EnsureRecordingFolderWritable(recordingFolder);
        var output = Path.Combine(recordingFolder, "media", "screen.mp4");
        if(new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace<500L*1024*1024)throw new IOException("录制盘剩余空间低于 500MiB，请释放空间后重试。");
        var bounds=target.Bounds; int? captureWidth=null,captureHeight=null;
        if(CaptureResolution.SelectedIndex==0 && bounds!=null)
        { var scale=Math.Min(1,Math.Min(1920d/bounds.Width,1080d/bounds.Height));captureWidth=Math.Max(2,(int)(bounds.Width*scale)/2*2);captureHeight=Math.Max(2,(int)(bounds.Height*scale)/2*2); }
        var request = new RecordingRequest(output, target, new VideoOptions(FrameRate: CaptureFps.SelectedIndex == 1 ? 60 : 30, Width:captureWidth,Height:captureHeight), new AudioOptions(), new InputTrackingOptions(OutputPath: Path.Combine(recordingFolder, "pointer.jsonl")));
        await File.WriteAllTextAsync(Path.Combine(recordingFolder, "recording-recovery.json"), JsonSerializer.Serialize(new { request.OutputPath, TargetKind = request.Target.Kind.ToString(), request.Target.Id, request.Target.Bounds, WindowHandle = request.Target.WindowHandle.ToInt64(), request.Video, request.Audio }, new JsonSerializerOptions { WriteIndented = true }));
        recordingMarkers.Clear(); RecordButton.IsEnabled = false; Status.Text = "正在启动原生采集…";
        try
        {
            if(excludeEditorDuringRecording)NativeWindows.Exclude(this);
            RegisterRecordingMarkerHotKey();
            await File.WriteAllTextAsync(Path.Combine(recordingFolder,"recording-controls.json"),JsonSerializer.Serialize(new { MarkerShortcut=markerHotKey.ShortcutText, MarkerShortcutIsFallback=markerHotKey.IsFallback, MarkerShortcutRegistrationError=markerHotKey.RegistrationError },new JsonSerializerOptions { WriteIndented=true }));
            recordingStart=0; recordingFaultHandled=false; await capture.StartAsync(request); await StartAdditionalTracks(); recordingGuard.Start(); recordingBar = new RecordingBar(()=>AddMarker("button"), StopRecording, async()=>await Guard(ToggleRecordingPause), error=>{App.Log(error);Status.Text="录制未能正常完成，原素材已保留。";SurfaceDialog.Show(this,error.Message,"录制未能完成",MessageBoxButton.OK,MessageBoxImage.Error);},markerHotKey.ShortcutText,markerHotKey.IsFallback,ShowDesktopMenu); recordingBar.Show(); RecorderOverlay.Visibility=Visibility.Collapsed;
            RecordButton.Content = "■  结束录制"; Status.Text = "正在录制，素材与鼠标事件写入本机。";
        }
        finally { RecordButton.IsEnabled = true;if(capture.State is not (CaptureState.Recording or CaptureState.Paused))NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,0); }
    }
    private void AddMarker(string input)
    {
        if (capture.State != CaptureState.Recording) return;
        recordingMarkers.Add((Stopwatch.GetTimestamp(), $"标记 {recordingMarkers.Count + 1}"));
        if (recordingFolder is not null) File.AppendAllText(Path.Combine(recordingFolder, "markers.jsonl"), JsonSerializer.Serialize(new { Tick = recordingMarkers[^1].Tick, Label = recordingMarkers[^1].Label, Input = input }) + Environment.NewLine);
        recordingBar?.SetMarkerCount(recordingMarkers.Count);
    }
    private Task StopRecording()
    {
        if(recordingStopTask is { IsCompleted:false })return recordingStopTask;
        return recordingStopTask=StopRecordingCore();
    }
    private async Task StopRecordingCore()
    {
        if(recordingFinalizing){while(recordingFinalizing)await Task.Delay(25);return;}recordingFinalizing=true;
        recordingGuard.Stop();
        RecordButton.IsEnabled = false;
        var captureStopped=false;
        try
        {
            var result = await capture.StopAsync();
            captureStopped=true;
            await CompleteRecording(result);
        }
        catch (Exception error)
        {
            recordingFaultHandled = true;
            var tracks = await StopAdditionalTracks();
            if (recordingFolder != null)
            {
                try
                {
                    if (tracks.Count > 0)
                        await File.WriteAllTextAsync(Path.Combine(recordingFolder, "independent-tracks-result.json"), JsonSerializer.Serialize(tracks, new JsonSerializerOptions { WriteIndented = true }));
                    recordingTrackWarnings.Add("主录制未能完成：" + error.Message);
                    await File.WriteAllLinesAsync(Path.Combine(recordingFolder, "recording-warnings.txt"), recordingTrackWarnings);
                }
                catch (Exception journalError) { App.Log(journalError); }
            }
            throw new InvalidOperationException("录制意外结束，已停止独立轨道并保留原素材。请在录制条的保存位置菜单中使用“恢复未完成录制…”，选择此目录：\n" + recordingFolder, error);
        }
        finally
        {
            NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
            // StopAsync disposes its backend before reporting Faulted. There is no
            // live recording to retry in that state; recovery uses the saved files.
            captureStopped |= capture.State is not (CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping);
            if(captureStopped){recordingBar?.CloseAfterRecording();recordingBar=null;RecordButton.Content="开始录制";}
            else RecordButton.Content="■  结束录制";
            RecordButton.IsEnabled = true;
            recordingFinalizing = false;
        }
    }
    private async Task CompleteRecording(RecordingResult result)
    {
        recordingGuard.Stop();
        NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,0);
        RecordButton.IsEnabled = false;
        try
        {
            var extraTracks=await StopAdditionalTracks(); recordingTrackWarnings.AddRange(result.Warnings); recordingBar?.CloseAfterRecording(); recordingBar = null; RecordButton.Content = "开始录制";
            // Keep pause/timing records even if validating the main video fails below.
            await File.WriteAllTextAsync(Path.Combine(recordingFolder!, "independent-tracks-result.json"), JsonSerializer.Serialize(extraTracks, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(recordingFolder!, "capture-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            var info = await MediaTools.ProbeAsync(result.OutputPath);
            if (info.Width < 1 || info.Height < 1 || !double.IsFinite(info.Duration) || info.Duration <= 0)
                throw new InvalidDataException("录制文件没有可用的视频时长。");
            await MediaTools.DecodeFrameAsync(result.OutputPath, 0, 16, 16, CancellationToken.None);
            var created = MakeProject("media/screen.mp4", info);
            if (result.InputEventsPath is not null && File.Exists(result.InputEventsPath))
            {
                foreach (var line in File.ReadLines(result.InputEventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var ev = JsonSerializer.Deserialize<InputEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (ev == null) continue;
                    if(result.PausedIntervals?.Any(x=>ev.StopwatchTicks>=x.PauseStartStopwatchTicks&&ev.StopwatchTicks<x.ResumeStopwatchTicks)==true)continue;
                    var time = MediaTime(ev.StopwatchTicks,result);
                    if (time < 0 || time > info.Duration || ev.Kind is not (InputEventKind.Move or InputEventKind.MouseDown)) continue;
                    var kind = ev.Kind == InputEventKind.MouseDown ? ev.MouseButton == FrameTrail.Capture.MouseButton.Right ? PointerEventKind.RightClick : PointerEventKind.LeftClick : PointerEventKind.Move;
                    created.PointerEvents.Add(new(time, new(ev.NormalizedX, ev.NormalizedY), kind, ev.CursorAsset));
                }
            }
            created.Markers = recordingMarkers.Select(x => new RecordingMarker(Guid.NewGuid(), Math.Clamp(MediaTime(x.Tick,result), 0, info.Duration), x.Label)).ToList();
            await AttachRecordedTracks(created,result,extraTracks);
            created.ZoomClips = AutoZoomPlanner.Plan(created.PointerEvents);
            created.DisplayName ??= Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder!)));
            var path = Path.Combine(recordingFolder!, ProjectFileNameFor(recordingFolder!)); FrameTrailProjectStore.Save(path, created);
            if (desktopResident && !desktopExiting) ShowEditorForDesktop();
            await OpenProject(path);
            var journal = Path.Combine(recordingFolder!, "recording-recovery.json"); if (File.Exists(journal)) File.Delete(journal);
            Status.Text = $"录制完成 · {info.Duration:0.0} 秒" +
                (recordingTrackWarnings.Count > 0 ? $" · 有 {recordingTrackWarnings.Count} 项素材提醒，详情见 recording-warnings.txt。" : "");
        }
        catch (Exception error)
        {
            recordingTrackWarnings.Add("录制素材未能完成项目验证：" + error.Message);
            try { await File.WriteAllLinesAsync(Path.Combine(recordingFolder!, "recording-warnings.txt"), recordingTrackWarnings); }
            catch (Exception journalError) { App.Log(journalError); }
            throw new InvalidOperationException("录制素材未通过完整验证，原文件与恢复记录保留在：\n" + recordingFolder + "\n可从录制条保存位置菜单选择“恢复未完成录制…”。", error);
        }
        finally { RecordButton.IsEnabled = true; }
    }
    private static FrameTrailProject MakeProject(string media, MediaInfo info) => new()
    {
        MediaPath = media, Media = new() { Width = info.Width, Height = info.Height, Duration = info.Duration,FrameRate=info.FrameRate>0?info.FrameRate:30 },
        Timeline = new() { Segments = [new() { SourceDuration = info.Duration }] }, Settings = new() { Background = "#F7F7F5", CanvasWidth = info.Width, CanvasHeight = info.Height,Padding=.055,CornerRadius=.012 }
    };

    /// <summary>工程文件名取自它所在的项目目录，保证视频库内每个工程的名字都唯一可辨认。</summary>
    internal static string ProjectFileNameFor(string projectDirectory)
    {
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)));
        if (string.IsNullOrWhiteSpace(leaf)) return "project.frametrail";
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var stem = new string(leaf.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return (stem.Length == 0 ? "project" : stem) + ".frametrail";
    }

    /// <summary>录制目录在真正开始采集前必须可写，否则要等到第一帧超时才会暴露成笼统的“录制失败”。</summary>
    private static void EnsureRecordingFolderWritable(string folder)
    {
        var probe = Path.Combine(folder, ".frametrail-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex)
        {
            throw new IOException($"无法在录制目录写入文件，请检查该目录的权限或所在磁盘状态：\n{folder}", ex);
        }
    }
    private async void OpenProjectClick(object sender, RoutedEventArgs e) => await Guard(async () => { var dialog = new OpenFileDialog { Filter = "DogSC 项目|*.frametrail|项目备份|*.frametrail.bak" }; if (dialog.ShowDialog(this) == true) await OpenProject(dialog.FileName); });
    private async Task OpenProject(string path)
    {
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
        auxiliaryDurations.Clear();
        foreach (var entry in openedAuxiliaryDurations) auxiliaryDurations[entry.Key] = entry.Value;
        project = opened; projectFile = path; selectedEffect=null;TimelineView.MultiSelection.Clear();TimelineView.RangeStart=null;TimelineView.RangeEnd=null; history = new(project, Clone); timelineAssets=null; InvalidateProxy(); RefreshProject(); Playhead.Value = 0; RecentProjects.Add(path); Status.Text = ProjectOpenedStatus(path, auxiliaryWarning); _=LoadTimelineAssets(media,opened); await RenderPreview(0);
    }
    private static bool MainMediaCoversTimeline(MediaInfo info, FrameTrailProject candidate)
    {
        if (info.Width < 1 || !double.IsFinite(info.Duration) || info.Duration <= 0 || candidate.Timeline.Segments.Count == 0) return false;
        var requiredEnd = candidate.Timeline.Segments.Max(segment => segment.SourceEnd);
        return double.IsFinite(requiredEnd) && info.Duration + .05 >= requiredEnd;
    }
    private async void ImportClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var pick = new OpenFileDialog { Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm" }; if (pick.ShowDialog(this) != true) return;
        var info = await MediaTools.ProbeAsync(pick.FileName); if (info.Width == 0 || info.Duration <= 0) throw new InvalidDataException("文件没有可用视频。");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail", "Import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(Path.Combine(dir,"media"));
        var relative = "media/source" + Path.GetExtension(pick.FileName); File.Copy(pick.FileName, Path.Combine(dir, relative));
        var imported = MakeProject(relative, info); imported.DisplayName = Path.GetFileNameWithoutExtension(pick.FileName); var file = Path.Combine(dir, ProjectFileNameFor(dir)); FrameTrailProjectStore.Save(file, imported); await OpenProject(file);
        Status.Text = "已复制素材并建立项目：" + dir;
        RecentProjects.Add(file);
    });
    private static FrameTrailProject Clone(FrameTrailProject p) => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(p))!;
    private void SaveProject() { if (project != null && projectFile != null) { FrameTrailProjectStore.Save(projectFile, project); Status.Text = "已保存 · " + projectFile; } }
    private async void SaveClick(object sender, RoutedEventArgs e) => await Guard(() => { SaveProject(); return Task.CompletedTask; });
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
    private void WaveformSizeChanged(object sender,SizeChangedEventArgs e)=>DrawWaveform();
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
    private void Edit(Action<FrameTrailProject> edit)
    {
        if (project is null || history is null) return; StopPlayback(); var next = Clone(project); edit(next); foreach(var motion in next.ScreenMotions)motion.EnterDuration=Math.Min(motion.EnterDuration,motion.Duration);foreach(var motion in next.CameraMotions)motion.EnterDuration=Math.Min(motion.EnterDuration,motion.Duration); next.Timeline.Validate();
        try { FrameTrailProjectStore.Save(projectFile!, next); }
        catch (Exception ex)
        {
            RefreshProject();
            throw new IOException("保存失败，本次编辑未应用。解除文件占用后请重新执行该操作。", ex);
        }
        history.Commit(next); project = next; InvalidateProxy(); RefreshProject(); Status.Text = "已保存 · " + projectFile; _ = Guard(() => RenderPreview(Playhead.Value));
    }
    private TimelineSegment Selected(FrameTrailProject p) => p.Timeline.Segments.FirstOrDefault(x => x.Id == (Clips.SelectedItem as ClipItem)?.Id) ?? throw new InvalidOperationException("请选择一个片段。");
    private double SelectedSourceAtPlayhead(FrameTrailProject p,TimelineSegment selected)
    {
        var start=0d; foreach(var s in p.Timeline.Segments) { if(s.Id==selected.Id)break; start+=s.OutputDuration; }
        if(Playhead.Value<start-1e-8||Playhead.Value>start+selected.OutputDuration+1e-8) throw new InvalidOperationException("请把播放头放在选中片段内，再执行分割或裁剪。");
        return selected.SourceStart+Math.Clamp(Playhead.Value-start,0,selected.OutputDuration)*selected.PlaybackRate;
    }
    private async void MoveLeftClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Reorder(s.Id, Math.Max(0,p.Timeline.Segments.IndexOf(s)-1)); }); return Task.CompletedTask; });
    private async void MoveRightClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Reorder(s.Id, Math.Min(p.Timeline.Segments.Count-1,p.Timeline.Segments.IndexOf(s)+1)); }); return Task.CompletedTask; });
    private async void SpeedClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => p.Timeline.SetPlaybackRate(Selected(p).Id, new[] {.25,.5,.75,1d,1.5,2,4}[Speed.SelectedIndex])); return Task.CompletedTask; });
    private async void RestoreClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => p.Timeline.Segments = [new() { SourceDuration = p.Media.Duration }]); return Task.CompletedTask; });
    private void ClipSelected(object sender, SelectionChangedEventArgs e) { if (refreshing || project == null || Clips.SelectedItem is not ClipItem item) return; var time = 0d; foreach (var s in project.Timeline.Segments) { if (s.Id == item.Id) break; time += s.OutputDuration; } Playhead.Value = time; }
    private void UndoClick(object sender, RoutedEventArgs e) => _ = Guard(() => { ApplyHistoryChange(false); return Task.CompletedTask; });
    private void RedoClick(object sender, RoutedEventArgs e) => _ = Guard(() => { ApplyHistoryChange(true); return Task.CompletedTask; });
    private void ApplyHistoryChange(bool redo)
    {
        if (history is null || (redo ? !history.CanRedo : !history.CanUndo)) return;
        var selection = CaptureEffectSelectionForHistory();
        void Persist(FrameTrailProject next) => FrameTrailProjectStore.Save(projectFile!, next);
        project = redo ? history.Redo(Persist) : history.Undo(Persist);
        Status.Text = "已保存 · " + projectFile;
        InvalidateProxy(); RestoreEffectSelectionAfterHistory(selection); RefreshProject();
        _ = Guard(() => RenderPreview(Playhead.Value));
    }
    private void MarkerJump(object sender, MouseButtonEventArgs e) { if (Markers.SelectedItem is MarkerItem m && project != null) { var at = project.Timeline.MapSourceToOutput(m.SourceTime); if (at.HasValue) Playhead.Value = at.Value; else Status.Text = "此标记所在素材已从时间线裁去。"; } }
    private void DeleteMarkerClick(object sender, RoutedEventArgs e) { if (Markers.SelectedItem is MarkerItem m) Edit(p => p.Markers.RemoveAll(x => x.Id == m.Id)); }
    private async void ApplySettingsClick(object sender, RoutedEventArgs e) => await Guard(() => { if (!System.Text.RegularExpressions.Regex.IsMatch(BackgroundColor.Text,"^#[0-9a-fA-F]{6}$")) throw new ArgumentException("背景色请使用 #RRGGBB 格式。"); Edit(p => { p.Settings.Background = BackgroundColor.Text; p.Settings.Padding = PaddingSlider.Value; p.Settings.CornerRadius = RadiusSlider.Value; p.Settings.Shadow = ShadowSlider.Value; p.Settings.CursorVisible = ShowCursor.IsChecked == true; }); return Task.CompletedTask; });
    private void AutoZoomClick(object sender, RoutedEventArgs e) => Edit(p => { p.Settings.AutoZoom = true; p.ZoomClips = AutoZoomPlanner.Plan(p.PointerEvents.Select(x => x with { Time = p.MouseClock.ToSourceTime(x.Time) }), new AutoZoomOptions(Scale: new[] {1.3,1.6,2}[ZoomScale.SelectedIndex])); });
    private void ClearZoomClick(object sender, RoutedEventArgs e) => Edit(p => { p.Settings.AutoZoom = false; p.ZoomClips.Clear(); });
    private async void CalibrateClick(object sender, RoutedEventArgs e) => await Guard(() => { var value = double.Parse(MouseOffset.Text, CultureInfo.CurrentCulture); if (!double.IsFinite(value) || Math.Abs(value)>10000) throw new ArgumentException("时间校准范围为 ±10000 毫秒。"); Edit(p => { p.MouseClock = p.MouseClock with { OffsetSeconds = value/1000 }; if (p.ZoomClips.All(x => x.Automatic)) p.ZoomClips = AutoZoomPlanner.Plan(p.PointerEvents.Select(x=>x with { Time=p.MouseClock.ToSourceTime(x.Time) })); }); return Task.CompletedTask; });
    private void PreviewFocusClick(object sender, MouseButtonEventArgs e)
    {
        if (project == null || playing || inspectorTab!="zoom"||motionTab!="zoom"||ManualFocus.IsChecked != true || PreviewImage.Source is null) return;
        var point = e.GetPosition(PreviewImage); var imageAspect = PreviewImage.Source.Width/PreviewImage.Source.Height; var w = PreviewImage.ActualWidth; var h = w/imageAspect; if (h>PreviewImage.ActualHeight) { h=PreviewImage.ActualHeight; w=h*imageAspect; }
        var nx = (point.X-(PreviewImage.ActualWidth-w)/2)/w; var ny = (point.Y-(PreviewImage.ActualHeight-h)/2)/h;
        if(displayedPreviewScene is not {} scene||!ReferenceEquals(displayedPreviewProject,project)||!GpuFrameRenderer.TryMapCanvasPointToSource(scene,previewSize.SW,previewSize.SH,previewSize.OW,previewSize.OH,new(nx,ny),out var focus))return;
        var canvasCrop=project.Settings.Canvas?.Crop??NormalizedRect.Full;focus=new(Math.Clamp((focus.X-canvasCrop.X)/canvasCrop.Width,0,1),Math.Clamp((focus.Y-canvasCrop.Y)/canvasCrop.Height,0,1));
        Edit(p => { var selected=selectedEffect?.Kind=="zoom"?p.ZoomClips.FirstOrDefault(z=>z.Id==selectedEffect.Id):null;if(selected!=null){selected.Focus=focus;selected.Automatic=false;}else{var start = Math.Min(p.Timeline.MapOutputToSource(Playhead.Value),Math.Max(0,p.Media.Duration-.05));var zoom=new ZoomClip {Start=start,End=Math.Min(p.Media.Duration,start+1.5),Focus=focus,Scale=new[]{1.3,1.6,2}[ZoomScale.SelectedIndex],EnterDuration=.5,ExitDuration=.5};p.ZoomClips.Add(zoom);selectedEffect=new("zoom",zoom.Id);}});
        e.Handled=true;
    }
    private void PlayheadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || project == null) return;
        if(SnapBoundaries.IsChecked==true && Playhead.IsMouseOver && Mouse.LeftButton==MouseButtonState.Pressed)
        {
            var edges=new List<double>{0}; var total=0d; foreach(var segment in project.Timeline.Segments) { total+=segment.OutputDuration; edges.Add(total); }
            var nearest=edges.MinBy(x=>Math.Abs(x-e.NewValue)); var tolerance=Math.Min(.15,project.Timeline.Duration*8/Math.Max(Playhead.ActualWidth,1));
            if(Math.Abs(nearest-e.NewValue)<tolerance) { refreshing=true;Playhead.Value=nearest;refreshing=false; }
        }
        StopPlayback();  UpdateTime(); _ = Guard(() => RenderTimelinePreview(Playhead.Value));
    }
    private void UpdateTime() { TimeLabel.Text = $"{TimeSpan.FromSeconds(Playhead.Value):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(Playhead.Maximum):mm\\:ss\\.ff}"; TimelineView.Position=Playhead.Value; }
    private void PreviousFrame(object sender, RoutedEventArgs e) => StepFrames(-1);
    private void NextFrame(object sender, RoutedEventArgs e) => StepFrames(1);
    private void InvalidateProxy() { _ = DisposePlaybackResourcesAsync(); ClosePlaybackAudio(); if (proxyPath != null && File.Exists(proxyPath)) { try { File.Delete(proxyPath); } catch (IOException) { } } proxyPath=null; playbackProxyProject=null; }
    private void StopPlayback() { var old=playbackCancellation; playbackCancellation=null; playing=false; playbackStarting=false; playbackTimer.Stop(); Player.Pause(); Player.Visibility=Visibility.Collapsed; PreviewImage.Visibility=Visibility.Visible; PlayButton.Content=new EditorIcon{Kind="play",Width=18,Height=18}; if(old is not null) _=old.CancelAsync(); }
    private async void PlayClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (playing || playbackStarting) { StopPlayback(); return; } if (project == null || projectFile == null || operationCancellation != null) return;
        await StartRealtimePlayback();
    });
    private void PlaybackEnded(object sender,RoutedEventArgs e) { StopPlayback(); Playhead.Value=Playhead.Maximum; }
    private ExportOptions Options(bool audio) { var w=Resolution.SelectedIndex switch {0=>(int)(project?.Media.Width??1920),2=>2560,3=>3840,_=>1920}; var h=Resolution.SelectedIndex==0?(int)(project?.Media.Height??1080):(int)Math.Round(w*(double)(project?.Settings.CanvasHeight??1080)/(project?.Settings.CanvasWidth??1920)); return new(w/2*2,Math.Max(2,h/2*2),ExportFps.SelectedIndex==1?60:30,HardwareEncoder.IsChecked==true,audio); }
    private async void ExportClick(object sender,RoutedEventArgs e) => await Guard(() => ChooseExport(false));
    private async void ExportAudioClick(object sender,RoutedEventArgs e) => await Guard(() => ChooseExport(true));
    private async Task ChooseExport(bool audio)
    {
        if (project==null || projectFile==null || operationCancellation!=null) return; StopPlayback();var snapshot=ExportSnapshot();var file=projectFile;var source=FrameTrailProjectStore.ResolveMediaPath(file,snapshot);
        var dialog=new ExportWindow(snapshot,PreviewImage.Source,ProjectTitle.Text,(path,options,progress,token)=>Task.Run(()=>new ExportEngine().ExportAsync(snapshot,source,path,options with{ProjectFilePath=file},progress,token)),initialAudioOnly:audio){Owner=this};dialog.ShowDialog();await Task.CompletedTask;
    }
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
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"FrameTrail");
        var dialog=new OpenFileDialog { Title="选择未完成录制目录里的 recording-recovery.json",Filter="未完成录制记录|recording-recovery.json",InitialDirectory=root };
        if(dialog.ShowDialog(DesktopDialogOwner())!=true)return;var path=await RecoverReadableRecording(Path.GetDirectoryName(dialog.FileName)!);if(desktopResident)ShowEditorForDesktop();await OpenProject(path);Status.Text="已恢复可读素材，恢复说明保存在项目旁的 .recovery.txt 文件。";
    });
    private void WindowKeyDown(object sender,KeyEventArgs e)
    {
        HandleTimelineShortcut(e);
    }
    private async void WindowClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady) return; e.Cancel=true;
        if(desktopResident)
        {
            if(!desktopExiting)
            {
                if(desktopTrayAvailable) await Guard(() => { SaveProject(); StopPlayback(); Hide(); return Task.CompletedTask; });
                else await RequestDesktopExitAsync();
            }
            return;
        }
        if(closingInProgress) return;
        if(capture.State is CaptureState.Recording or CaptureState.Starting or CaptureState.Paused) { SurfaceDialog.Show(this,"请先结束录制，确保素材完成封装。","录制进行中"); return; }
        if(operationCancellation!=null) { operationCancellation.Cancel(); Status.Text="正在取消处理，请稍后关闭。"; return; }
        closingInProgress=true;
        try
        {
            await Guard(async()=>
            {
                await ReleaseApplicationAsync();
            });
        }
        finally { if(!closeReady) closingInProgress=false; }
    }
}
