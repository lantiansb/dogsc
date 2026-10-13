using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.Capture;
using FrameTrail.Media;

namespace FrameTrail.App;

// Recording lifecycle: start (with rollback), stop, the completion card, abandon/restart, notices.
// Behaviour follows the Mac app v0.1.4 (AppModel.startRecording / stopRecording / persistCompletedRecording /
// discardCompletedRecording / abandonCurrentRecording and RecordingCompletionWindow).
public partial class MainWindow
{
    /// <summary>
    /// The project name typed on the recorder start surface. Consumed by the next recording: it becomes the project's
    /// DisplayName and the prefix of the recording folder (Mac AppModel.recordingTitleDraft). Cleared once a recording started.
    /// </summary>
    internal string? nextRecordingTitle;

    // True from just before the screen capture starts until the screen capture, the independent tracks and the bar are all up,
    // and again while a failed start is being rolled back. A recording that is aborted this way must never reach the
    // "recording finished" path (Mac: RecordingTrackFinalizer .rollbackFailedPreparation).
    private volatile bool recordingStartRollback;
    private int recordingSession;
    private bool recordingStarting;
    private bool recordingAbandoning;
    private string? recordingTitle;
    private string? completionNotice;
    // Recording clock: first-frame time and pause intervals, so the bar shows the real elapsed recording time.
    private long recordingStartRequestedTick;
    private long recordingPauseStartTick;
    private long recordingPausedTicks;
    private bool windowMinimizeAutoPaused;
    private readonly HashSet<Guid> faultedTrackIds = [];
    private RecordingCompletionWindow? completionWindow;
    private RecoveryOfferWindow? recoveryOffer;
    private bool recoveryOffered;

    /// <summary>Wires the recording lifecycle events that need more than one file's state. Called once from the constructor.</summary>
    private void InitializeRecordingFlow()
    {
        // Dialogs, settings and export windows opened while recording must not show up in the video (Mac CaptureSurfaceFilter).
        SurfaceWindow.ShouldExcludeFromCapture = () => capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping;
        // D-24: a minimized window target pauses the recording instead of ending it.
        capture.WindowTargetMinimizedChanged += minimized => Dispatcher.BeginInvoke(() => { _ = Guard(() => HandleWindowTargetMinimizedAsync(minimized)); });
        trackCapture.StateChanged += (_, change) => Dispatcher.BeginInvoke(() => { _ = Guard(() => HandleTrackStateChangedAsync(change)); });
        capture.ErrorOccurred += (_, error) =>
        {
            // Judge "being rolled back" when the error is raised, not when the UI thread gets to it.
            var duringStartRollback = recordingStartRollback;
            Dispatcher.BeginInvoke(() => HandleCaptureError(error, duringStartRollback));
        };
        capture.RecordingCompleted += result =>
        {
            // A recording that is being rolled back after a failed start is not a finished recording (D-01).
            if (recordingStartRollback) return;
            var session = Volatile.Read(ref recordingSession);
            Dispatcher.BeginInvoke(async () =>
            {
                if (session != recordingSession || recordingFinalizing || recordingFolder == null) return;
                recordingFinalizing = true;
                try { await Guard(() => CompleteRecording(result)); }
                finally { recordingFinalizing = false; }
            });
        };
    }

    private string RecordingRootDirectory => diagnosticRecordingRoot ?? preferredRecordingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");

    private async Task ToggleRecording()
    {
        if (capture.State is CaptureState.Recording or CaptureState.Paused) { await StopRecording(); return; }
        // A second click while a start, stop or abandon is still running must not start another recording.
        if (recordingStarting || recordingFinalizing || recordingAbandoning || capture.State is CaptureState.Starting or CaptureState.Stopping) return;
        recordingStarting = true;
        try { await StartRecordingAsync(); }
        finally { recordingStarting = false; }
    }

    private async Task StartRecordingAsync()
    {
        if (Targets.SelectedItem is not TargetItem item) throw new InvalidOperationException("请选择录制来源。");
        var target = item.Target;
        if (excludeEditorDuringRecording && target.WindowHandle != IntPtr.Zero && target.WindowHandle == new WindowInteropHelper(this).Handle)
            throw new InvalidOperationException("编辑器正设置为从录制中排除；请在设置中关闭此选项后录制它。");
        if (CaptureKind.SelectedIndex == 2) target = new(CaptureTargetKind.Region, item.Target.Id, region ?? throw new InvalidOperationException("请先框选录制区域。"));
        var bounds = target.Bounds; int? captureWidth = null, captureHeight = null;
        if (CaptureResolution.SelectedIndex == 0 && bounds != null)
        {
            var scale = Math.Min(1, Math.Min(1920d / bounds.Width, 1080d / bounds.Height));
            captureWidth = Math.Max(2, (int)(bounds.Width * scale) / 2 * 2);
            captureHeight = Math.Max(2, (int)(bounds.Height * scale) / 2 * 2);
        }
        var frameRate = CaptureFps.SelectedIndex == 1 ? 60 : 30;

        // The disk check comes first so a refused start leaves no empty recording folder behind (Mac CaptureReadiness).
        var root = RecordingRootDirectory;
        EnsureRecordingDiskSpace(root, captureWidth ?? bounds?.Width ?? 1920, captureHeight ?? bounds?.Height ?? 1080, frameRate);

        var title = RecordingNaming.CleanTitle(nextRecordingTitle);
        var folder = RecordingNaming.UniqueFolder(root, title);
        Interlocked.Increment(ref recordingSession);
        recordingStartRollback = true;
        recordingFolder = folder; recordingTitle = title; completionNotice = null;
        recordingMarkers.Clear(); faultedTrackIds.Clear(); windowMinimizeAutoPaused = false;
        recordingStart = 0; recordingPauseStartTick = 0; recordingPausedTicks = 0; recordingFaultHandled = false;
        RecordButton.IsEnabled = false; Status.Text = "正在启动原生采集…";
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "media"));
            var output = Path.Combine(folder, "media", "screen.mp4");
            var request = new RecordingRequest(output, target, new VideoOptions(FrameRate: frameRate, Width: captureWidth, Height: captureHeight), new AudioOptions(), new InputTrackingOptions(OutputPath: Path.Combine(folder, "pointer.jsonl")));
            await File.WriteAllTextAsync(Path.Combine(folder, "recording-recovery.json"), JsonSerializer.Serialize(new { request.OutputPath, TargetKind = request.Target.Kind.ToString(), request.Target.Id, request.Target.Bounds, WindowHandle = request.Target.WindowHandle.ToInt64(), request.Video, request.Audio }, new JsonSerializerOptions { WriteIndented = true }));
            if (excludeEditorDuringRecording) NativeWindows.Exclude(this);
            // The marker hot key exists only while recording (Mac RecordingMarkerHotKey).
            markerHotKey.Register(new WindowInteropHelper(this).EnsureHandle());
            await File.WriteAllTextAsync(Path.Combine(folder, "recording-controls.json"), JsonSerializer.Serialize(new { MarkerShortcut = markerHotKey.ShortcutText, MarkerShortcutIsFallback = markerHotKey.IsFallback, MarkerShortcutRegistrationError = markerHotKey.RegistrationError }, new JsonSerializerOptions { WriteIndented = true }));
            await HideDesktopIconsForRecordingAsync(target.Kind);
            recordingStartRequestedTick = Stopwatch.GetTimestamp();
            await capture.StartAsync(request);
            await StartAdditionalTracks();
            desktopIconVisibility?.EnsureActive();
            if (capture.State is not (CaptureState.Recording or CaptureState.Paused)) throw new InvalidOperationException("录制在启动过程中被中断，请重试。");
            recordingStartRollback = false;
            recordingGuard.Start();
            CreateRecordingBar();
            if (target.Kind == CaptureTargetKind.Region && target.Bounds is { } guideBounds) ShowRegionRecordingGuide(guideBounds);
            RecorderOverlay.Visibility = Visibility.Collapsed;
            RecordButton.Content = "■  结束录制"; Status.Text = "正在录制，素材与鼠标事件写入本机。";
            nextRecordingTitle = null;
        }
        catch
        {
            // Set before anything is awaited, so a RecordingCompleted raised by the rollback stop is ignored.
            recordingStartRollback = true;
            try { await RollbackFailedStartAsync(folder); }
            catch (Exception rollbackError) { App.Log(rollbackError); }
            finally { recordingStartRollback = false; }
            throw;
        }
        finally
        {
            RecordButton.IsEnabled = true;
            if (capture.State is not (CaptureState.Recording or CaptureState.Paused)) NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
        }
    }

    /// <summary>
    /// Undoes a start that failed half way (microphone denied, camera busy, disk error ...): stops what already runs,
    /// releases the hot key and deletes the folder and recovery journal of this attempt. The caller reports the error once.
    /// </summary>
    private async Task RollbackFailedStartAsync(string folder)
    {
        try { if (capture.State is CaptureState.Recording or CaptureState.Paused) await capture.StopAsync(); }
        catch (Exception error) { App.Log(error); }
        try { await StopAdditionalTracks(); }
        catch (Exception error) { App.Log(error); }
        await RestoreDesktopIconsAsync();
        markerHotKey.Unregister();
        recordingGuard.Stop();
        CloseRecordingBar();
        await RecordingNaming.DeleteFolderQuietlyAsync(folder);
        if (string.Equals(recordingFolder, folder, StringComparison.OrdinalIgnoreCase)) recordingFolder = null;
        recordingTitle = null;
        RecordButton.Content = "开始录制";
        Status.Text = "录制未能开始。";
    }

    private void EnsureRecordingDiskSpace(string root, int width, int height, int frameRate)
    {
        var need = RecordingDisk.EstimateOneMinuteBytes(width, height, frameRate);
        if (!RecordingDisk.TryGetFreeSpace(root, out var free) || free >= need) return;
        throw new IOException($"开始录制需预留约 1 分钟的空间（{RecordingDisk.FormatBytes(need)}），当前可用 {RecordingDisk.FormatBytes(free)}。请再释放约 {RecordingDisk.FormatBytes(need - free)} 后重试。");
    }

    private void CreateRecordingBar()
    {
        var bar = new RecordingBar(
            () => AddMarker("button"), StopRecording, async () => await Guard(ToggleRecordingPause),
            error =>
            {
                App.Log(error); Status.Text = "录制未能正常完成，原素材已保留。";
                SurfaceDialog.Show(DesktopDialogOwner(), error.Message, "录制未能完成", MessageBoxButton.OK, MessageBoxImage.Error);
            },
            markerHotKey.ShortcutText, markerHotKey.IsFallback, ShowRecordingMoreMenu, () => ShowMemoForDesktop(), RecordingElapsed);
        recordingBar = bar;
        bar.Show();
        if (markerHotKey.IsFallback) ReportRecorderNotice("Ctrl+Alt+M 已被占用，录制标记使用 " + markerHotKey.ShortcutText + "。", true);
        else if (markerHotKey.ShortcutText == null) ReportRecorderNotice("标记快捷键暂不可用，请使用录制条上的标记按钮。", true);
    }

    /// <summary>D-04: frame around the recorded region for the whole recording; closed together with the recording bar.</summary>
    private void ShowRegionRecordingGuide(PixelRect bounds)
    {
        try
        {
            regionRecordingGuide?.Close();
            regionRecordingGuide = new RegionRecordingGuide(bounds);
            regionRecordingGuide.Show();
        }
        catch (Exception error) { App.Log(error); regionRecordingGuide = null; }
    }

    private void CloseRegionRecordingGuide()
    {
        var guide = regionRecordingGuide;
        regionRecordingGuide = null;
        try { guide?.Close(); }
        catch (Exception error) { App.Log(error); }
    }

    private void CloseRecordingBar()
    {
        CloseRegionRecordingGuide();
        var bar = recordingBar;
        recordingBar = null;
        if (bar is null) return;
        try { bar.CloseAfterRecording(); }
        catch (Exception error) { App.Log(error); }
    }

    /// <summary>
    /// Real recording time: from the first-frame time (or the start request until that is known) minus the paused intervals.
    /// Mac AppModel.elapsedRecordingTime uses startedAt and accumulatedPausedDuration the same way.
    /// </summary>
    private TimeSpan RecordingElapsed()
    {
        var start = recordingStart != 0 ? recordingStart : recordingStartRequestedTick;
        if (start == 0) return TimeSpan.Zero;
        var end = recordingPauseStartTick != 0 ? recordingPauseStartTick : Stopwatch.GetTimestamp();
        var ticks = end - start - recordingPausedTicks;
        return ticks <= 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, ticks);
    }

    /// <summary>Called for every capture state change (UI thread) to keep the recording clock in step with the real pauses.</summary>
    private void TrackRecordingClock(CaptureStateChanged state)
    {
        if (state.State == CaptureState.Recording)
        {
            if (recordingStart == 0) recordingStart = state.StopwatchTicks;
            else if (recordingPauseStartTick != 0) { recordingPausedTicks += Math.Max(0, state.StopwatchTicks - recordingPauseStartTick); recordingPauseStartTick = 0; }
        }
        else if (state.State == CaptureState.Paused && recordingPauseStartTick == 0 && recordingStart != 0) recordingPauseStartTick = state.StopwatchTicks;
    }

    private void AddMarker(string input)
    {
        // Markers can only be added while actually recording (not paused), and a double press is one marker (Mac addRecordingMarker).
        if (capture.State != CaptureState.Recording) return;
        var now = Stopwatch.GetTimestamp();
        if (recordingMarkers.Count > 0 && now - recordingMarkers[^1].Tick < Stopwatch.Frequency * 3 / 10) return;
        recordingMarkers.Add((now, $"标记 {recordingMarkers.Count + 1}"));
        if (recordingFolder is not null)
        {
            try { File.AppendAllText(Path.Combine(recordingFolder, "markers.jsonl"), JsonSerializer.Serialize(new { Tick = recordingMarkers[^1].Tick, Label = recordingMarkers[^1].Label, Input = input }) + Environment.NewLine); }
            catch (Exception error) { App.Log(error); }
        }
        recordingBar?.SetMarkerCount(recordingMarkers.Count);
    }

    private Task StopRecording()
    {
        if (recordingStopTask is { IsCompleted: false }) return recordingStopTask;
        return recordingStopTask = StopRecordingCore();
    }

    private async Task StopRecordingCore()
    {
        if (recordingFinalizing) { while (recordingFinalizing) await Task.Delay(25); return; }
        recordingFinalizing = true;
        recordingGuard.Stop();
        markerHotKey.Unregister();
        RecordButton.IsEnabled = false;
        var captureStopped = false;
        try
        {
            recordingBar?.SetBusy("正在结束录制…");
            var result = await capture.StopAsync();
            captureStopped = true;
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
            throw new InvalidOperationException("录制意外结束，已停止独立轨道并保留原素材。请在录制起始条的“保存与录制配置”菜单中使用“恢复中断录制”，选择此目录：\n" + recordingFolder, error);
        }
        finally
        {
            NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
            // StopAsync disposes its backend before reporting Faulted. There is no
            // live recording to retry in that state; recovery uses the saved files.
            captureStopped |= capture.State is not (CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping);
            if (captureStopped) await RestoreDesktopIconsAsync();
            if (captureStopped) { CloseRecordingBar(); RecordButton.Content = "开始录制"; }
            else RecordButton.Content = "■  结束录制";
            RecordButton.IsEnabled = true;
            recordingFinalizing = false;
        }
    }

    private async Task CompleteRecording(RecordingResult result)
    {
        await RestoreDesktopIconsAsync();
        recordingGuard.Stop();
        markerHotKey.Unregister();
        NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
        RecordButton.IsEnabled = false;
        // The bar stays up with this phrase until the project file is on disk (Mac: finishing stage on the recorder panel).
        recordingBar?.SetBusy("正在整理素材…");
        var folder = recordingFolder!;
        try
        {
            var extraTracks = await StopAdditionalTracks(); recordingTrackWarnings.AddRange(result.Warnings); RecordButton.Content = "开始录制";
            // Keep pause/timing records even if validating the main video fails below.
            await File.WriteAllTextAsync(Path.Combine(folder, "independent-tracks-result.json"), JsonSerializer.Serialize(extraTracks, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(folder, "capture-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            var info = await MediaTools.ProbeAsync(result.OutputPath);
            if (info.Width < 1 || info.Height < 1 || !double.IsFinite(info.Duration) || info.Duration <= 0)
                throw new InvalidDataException("录制文件没有可用的视频时长。");
            // The first frame doubles as the decode check and as the picture on the completion card.
            // The thumbnail is decoration only: a failed decode must not stop the project from being created (the card shows a placeholder).
            BitmapSource thumbnail;
            try { thumbnail = await DecodeCompletionThumbnailAsync(result.OutputPath, info.Width, info.Height); }
            catch (Exception thumbnailError)
            {
                App.Log(thumbnailError);
                thumbnail = PlaceholderCompletionThumbnail(info.Width, info.Height);
            }
            var created = MakeProject("media/screen.mp4", info);
            var displayName = (recordingTitle ?? $"录屏 {DateTime.Now:yyyy-MM-dd HH:mm}").Trim();
            created.DisplayName = displayName.Length > 120 ? displayName[..120] : displayName;
            if (result.InputEventsPath is not null && File.Exists(result.InputEventsPath))
            {
                foreach (var line in File.ReadLines(result.InputEventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var ev = JsonSerializer.Deserialize<InputEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (ev == null) continue;
                    if (result.PausedIntervals?.Any(x => ev.StopwatchTicks >= x.PauseStartStopwatchTicks && ev.StopwatchTicks < x.ResumeStopwatchTicks) == true) continue;
                    var time = MediaTime(ev.StopwatchTicks, result);
                    if (time < 0 || time > info.Duration || ev.Kind is not (InputEventKind.Move or InputEventKind.MouseDown)) continue;
                    var kind = ev.Kind == InputEventKind.MouseDown ? ev.MouseButton == FrameTrail.Capture.MouseButton.Right ? PointerEventKind.RightClick : PointerEventKind.LeftClick : PointerEventKind.Move;
                    created.PointerEvents.Add(new(time, new(ev.NormalizedX, ev.NormalizedY), kind, ev.CursorAsset));
                }
            }
            created.Markers = recordingMarkers.Select(x => new RecordingMarker(Guid.NewGuid(), Math.Clamp(MediaTime(x.Tick, result), 0, info.Duration), x.Label)).ToList();
            await AttachRecordedTracks(created, result, extraTracks);
            // Generated only when the preference is on (Mac: automatic zoom after recording is a setting).
            if (ReadUserPreferences().AutoZoomAfterRecording) created.ZoomClips = AutoZoomPlanner.Plan(created.PointerEvents);
            // F-01: zoom lives only as stored clips; an empty lane must not be re-planned by the evaluator fallback or on open.
            created.Settings.AutoZoom = false;
            var path = Path.Combine(folder, "project.frametrail"); FrameTrailProjectStore.Save(path, created);
            var journal = Path.Combine(folder, "recording-recovery.json"); if (File.Exists(journal)) File.Delete(journal);
            Status.Text = $"录制完成 · {info.Duration:0.0} 秒" +
                (recordingTrackWarnings.Count > 0 ? $" · 有 {recordingTrackWarnings.Count} 项素材提醒，详情见 recording-warnings.txt。" : "");

            var notice = completionNotice;
            if (recordingTrackWarnings.Count > 0) notice = (notice is null ? "" : notice + " ") + $"有 {recordingTrackWarnings.Count} 项素材提醒，详情见 recording-warnings.txt。";
            var workArea = CompletionWorkArea();
            CloseRecordingBar();
            if (diagnosticRecordingRoot != null)
            {
                // Smoke runs have nobody to press a button: open the editor as before.
                if (desktopResident && !desktopExiting) ShowEditorForDesktop();
                await OpenProject(path);
                return;
            }
            ShowCompletionCard(new RecordingCompletionInfo(path, folder, created.DisplayName ?? "录屏", info.Duration, info.Width, info.Height, thumbnail, notice), workArea);
        }
        catch (Exception error)
        {
            CloseRecordingBar();
            recordingTrackWarnings.Add("录制素材未能完成项目验证：" + error.Message);
            try { await File.WriteAllLinesAsync(Path.Combine(folder, "recording-warnings.txt"), recordingTrackWarnings); }
            catch (Exception journalError) { App.Log(journalError); }
            throw new InvalidOperationException("录制素材未通过完整验证，原文件与恢复记录保留在：\n" + folder + "\n可在录制起始条的“保存与录制配置”菜单中选择“恢复中断录制”。", error);
        }
        finally { RecordButton.IsEnabled = true; }
    }

    /// <summary>Flat dark picture with the recording's aspect ratio, shown on the completion card when the first frame cannot be decoded.</summary>
    private static BitmapSource PlaceholderCompletionThumbnail(int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min(1d, Math.Min(640d / Math.Max(1, sourceWidth), 640d / Math.Max(1, sourceHeight)));
        var width = Math.Max(2, (int)(sourceWidth * scale) / 2 * 2);
        var height = Math.Max(2, (int)(sourceHeight * scale) / 2 * 2);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0x22; pixels[i + 1] = 0x20; pixels[i + 2] = 0x20; pixels[i + 3] = 0xFF; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task<BitmapSource> DecodeCompletionThumbnailAsync(string mediaPath, int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min(1d, Math.Min(640d / Math.Max(1, sourceWidth), 640d / Math.Max(1, sourceHeight)));
        var width = Math.Max(2, (int)(sourceWidth * scale) / 2 * 2);
        var height = Math.Max(2, (int)(sourceHeight * scale) / 2 * 2);
        var pixels = await MediaTools.DecodeFrameAsync(mediaPath, 0, width, height, CancellationToken.None);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- completion card -------------------------------------------------------------------------------------------

    private Rect CompletionWorkArea()
    {
        Window? anchor = recordingBar;
        anchor ??= FloatingWindowPlacement.FindLive("recorder");
        anchor ??= IsVisible ? this : null;
        try { if (anchor is { IsLoaded: true }) return SurfaceWindow.WorkArea(anchor); }
        catch (Exception error) { App.Log(error); }
        return SystemParameters.WorkArea;
    }

    private void CloseCompletionCard()
    {
        var card = completionWindow;
        completionWindow = null;
        card?.Dismiss();
    }

    private void ShowCompletionCard(RecordingCompletionInfo info, Rect workArea)
    {
        CloseCompletionCard();
        var card = new RecordingCompletionWindow(info, workArea, choice => ResolveCompletionAsync(info, choice));
        completionWindow = card;
        card.Closed += (_, _) =>
        {
            if (ReferenceEquals(completionWindow, card)) completionWindow = null;
            // Saving or discarding returns to the recorder start bar; editing hands over to the editor.
            if (card.ResolvedChoice is RecordingCompletionChoice.Save or RecordingCompletionChoice.Trash && !desktopExiting && !closeReady)
                _ = Guard(ActivateDesktopRecorderAsync);
        };
        card.Show();
    }

    private async Task ResolveCompletionAsync(RecordingCompletionInfo info, RecordingCompletionChoice choice)
    {
        switch (choice)
        {
            case RecordingCompletionChoice.Save:
                // The project file was written before the card appeared (Mac: media and project already crossed the save boundary).
                if (!File.Exists(info.ProjectPath)) throw new FileNotFoundException("项目文件不存在，可能已被移动或删除。", info.ProjectPath);
                RecentProjects.Add(info.ProjectPath);
                Status.Text = "已保存项目：" + info.Title;
                break;
            case RecordingCompletionChoice.Edit:
                await OpenCompletedProjectAsync(info);
                break;
            case RecordingCompletionChoice.Trash:
                await MoveRecordingToRecycleBinAsync(info.RecordingFolder);
                Status.Text = "已将这次录制移到回收站。";
                break;
        }
    }

    /// <summary>"进入编辑": saves whatever the editor has open, then opens the recorded project there (Mac editCompletedRecording).</summary>
    private async Task OpenCompletedProjectAsync(RecordingCompletionInfo info)
    {
        if (operationCancellation != null)
        {
            var owner = (Window?)completionWindow ?? this;
            if (SurfaceDialog.Show(owner, "编辑器正在导出或处理媒体，打开新项目会取消它。继续吗？", "正在处理", MessageBoxButton.OKCancel, MessageBoxImage.Warning, acceptLabel: "取消处理并打开", cancelLabel: "返回", preferCancel: true) != MessageBoxResult.OK)
                throw new OperationCanceledException("已取消打开，录制项目已保存。");
            operationCancellation?.Cancel();
            for (var wait = 0; wait < 60 && operationCancellation != null; wait++) await Task.Delay(50);
        }
        if (project != null && projectFile != null) SaveProject();
        ShowEditorForDesktop();
        await OpenProject(info.ProjectPath);
    }

    private static async Task MoveRecordingToRecycleBinAsync(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // The whole recording folder (project, media, logs) goes to the recycle bin, so it can be restored from there.
        await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
    }

    // ---- restart / discard while recording --------------------------------------------------------------------------

    private void ShowRecordingMoreMenu(Button button)
    {
        var menu = NewMenu();
        AddMenu(menu, "重新录制…", () => ConfirmAndAbandonRecording(restart: true));
        AddMenu(menu, "丢弃这次录制…", () => ConfirmAndAbandonRecording(restart: false));
        menu.Items.Add(new Separator());
        AddMenu(menu, "录制与界面设置…", ShowUserPreferences);
        AddMenu(menu, "退出 DogSC", RequestDesktopExitAsync);
        ShowTrayMenu(menu, button.PointToScreen(new Point(0, button.ActualHeight + 6)));
    }

    private Task ConfirmAndAbandonRecording(bool restart) => Guard(async () =>
    {
        if (capture.State is not (CaptureState.Recording or CaptureState.Paused) || recordingFinalizing) return;
        var confirmed = SurfaceDialog.Show(DesktopDialogOwner(),
            restart ? "当前录制将移到回收站，随后使用相同设置重新开始。" : "当前录制和相关素材将移到回收站，随后返回录制起始条。",
            restart ? "停止当前录制并重新开始？" : "丢弃这次录制？", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            acceptLabel: restart ? "重新录制" : "移到回收站", cancelLabel: "取消", preferCancel: true) == MessageBoxResult.OK;
        if (confirmed) await AbandonRecording(restart);
    });

    /// <summary>Mac abandonCurrentRecording: stop, move the recording to the recycle bin, then restart with the same settings or return to the start bar.</summary>
    private async Task AbandonRecording(bool restart)
    {
        if (capture.State is not (CaptureState.Recording or CaptureState.Paused) || recordingFinalizing || recordingAbandoning) return;
        var task = recordingStopTask = AbandonRecordingCore();
        await task;
        if (restart) await ToggleRecording();
        else await ActivateDesktopRecorderAsync();
    }

    private async Task AbandonRecordingCore()
    {
        recordingAbandoning = true; recordingFinalizing = true;
        var folder = recordingFolder;
        try
        {
            recordingGuard.Stop(); markerHotKey.Unregister();
            recordingBar?.SetBusy("正在丢弃…");
            try { if (capture.State is CaptureState.Recording or CaptureState.Paused) await capture.StopAsync(); }
            catch (Exception error) { App.Log(error); }
            try { await StopAdditionalTracks(); }
            catch (Exception error) { App.Log(error); }
            NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
            CloseRecordingBar();
            if (folder != null)
            {
                try { await MoveRecordingToRecycleBinAsync(folder); }
                catch (Exception error) { App.Log(error); ReportRecorderNotice("录制文件夹未能移到回收站，可手动删除：" + folder, true); }
            }
            RecordButton.Content = "开始录制"; Status.Text = "已丢弃这次录制。";
        }
        finally { await RestoreDesktopIconsAsync(); recordingAbandoning = false; recordingFinalizing = false; RecordButton.IsEnabled = true; }
    }

    // ---- notices and independent track events -----------------------------------------------------------------------

    /// <summary>
    /// Shows a recording-time message where the user can actually see it: on the recording bar (warning icon + short text,
    /// folded after 8 s but kept as tooltip), otherwise as an editor notice and in the status line.
    /// </summary>
    internal void ReportRecorderNotice(string message, bool warning = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (recordingBar is { } bar) { bar.ShowNotice(message, warning); return; }
        Status.Text = message;
        ShowNotice(message, warning);
    }

    private static string LocalizeCaptureMessage(string message)
    {
        if (message.Contains("was closed", StringComparison.OrdinalIgnoreCase)) return "录制的窗口已关闭，正在结束录制并保存素材。";
        if (message.Contains("minimized", StringComparison.OrdinalIgnoreCase)) return "录制的窗口已最小化，正在结束录制并保存素材。";
        if (message.Contains("bounds are unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("no visible bounds", StringComparison.OrdinalIgnoreCase)) return "录制的窗口当前不可见，正在结束录制并保存素材。";
        if (message.Contains("tracking failed", StringComparison.OrdinalIgnoreCase)) return "窗口跟踪失败，正在结束录制并保存素材。";
        return message;
    }

    private void HandleCaptureError(CaptureError error, bool duringStartRollback)
    {
        App.Log(error.Exception ?? new Exception(error.Message));
        // A start that is being rolled back reports its one error through the exception, not as a second notice.
        if (duringStartRollback) return;
        var message = LocalizeCaptureMessage(error.Message);
        if (error.Message.Contains("being finalized", StringComparison.OrdinalIgnoreCase)) completionNotice = message;
        ReportRecorderNotice(message == error.Message ? "录制提示：" + message : message, true);
    }

    /// <summary>
    /// A microphone or system-sound track that fails while recording is dropped on its own with a warning, and the recording goes on
    /// (Mac AppModel: a lost microphone only loses that track). A failing camera ends the recording, keeping the material.
    /// </summary>
    private async Task HandleTrackStateChangedAsync(CaptureTrackStateChanged change)
    {
        if (change.State != CaptureTrackState.Faulted || recordingStartRollback || recordingFinalizing) return;
        if (capture.State is not (CaptureState.Recording or CaptureState.Paused)) return;
        if (!recordingTracks.Any(track => track.TrackId == change.TrackId) || !faultedTrackIds.Add(change.TrackId)) return;
        var name = change.Kind switch { CaptureTrackKind.Microphone => "麦克风", CaptureTrackKind.SystemAudio => "系统声音", _ => "摄像头" };
        recordingTrackWarnings.Add(name + "轨道中途出错：" + (change.Detail ?? "未知原因"));
        if (change.Kind == CaptureTrackKind.Camera)
        {
            completionNotice = "摄像头已中断，已结束录制并保留素材。";
            ReportRecorderNotice("摄像头已中断，正在结束录制并保留素材", true);
            await StopRecording();
            return;
        }
        ReportRecorderNotice(name + "已断开或出错，该轨道已丢弃，其余轨道继续录制", true);
    }

    /// <summary>D-24: the captured window being minimized pauses the recording; restoring it continues. Closing the window still ends it.</summary>
    private async Task HandleWindowTargetMinimizedAsync(bool minimized)
    {
        if (recordingFinalizing || recordingStartRollback) return;
        if (minimized)
        {
            if (capture.State != CaptureState.Recording) return;
            await ToggleRecordingPauseCore(automatic: true);
            ReportRecorderNotice("窗口已最小化，恢复后自动继续", true);
        }
        else if (windowMinimizeAutoPaused)
        {
            windowMinimizeAutoPaused = false;
            if (capture.State == CaptureState.Paused) await ToggleRecordingPauseCore(automatic: false);
        }
    }

    private void PersistCaptureToggles()
    {
        try
        {
            var settings = ReadUserPreferences();
            settings.RecordSystemSound = SystemSound.IsChecked == true;
            settings.RecordMicrophone = Microphone.IsChecked == true;
            SaveUserPreferences(settings);
        }
        catch (Exception error) { App.Log(error); }
    }
}

/// <summary>Free-space lookup that also works for network (UNC) paths, and the "about one minute" recording reserve.</summary>
internal static class RecordingDisk
{
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    /// <summary>Free bytes for the volume holding <paramref name="path"/> (or its nearest existing parent). False when it cannot be determined; never throws.</summary>
    internal static bool TryGetFreeSpace(string path, out long freeBytes)
    {
        freeBytes = 0;
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent)) return false;
                probe = parent;
            }
            // GetDiskFreeSpaceEx accepts local and UNC directories; UNC names need the trailing backslash. DriveInfo throws for UNC.
            if (!GetDiskFreeSpaceEx(probe.TrimEnd('\\') + "\\", out var available, out _, out _)) return false;
            freeBytes = available > long.MaxValue ? long.MaxValue : (long)available;
            return true;
        }
        catch (Exception error)
        {
            App.Log(error);
            return false;
        }
    }

    /// <summary>
    /// Bytes needed for about one minute of recording: (video bitrate + 192 kbit/s audio) * 60 / 8 (Mac CaptureReadiness.estimatedOneMinuteBytes).
    /// The video bitrate mirrors FrameTrail.Capture.CaptureVideoQuality.ResolveBitrate: 16 Mbit/s at 1920x1080x60, scaled by pixels and frame rate, at least 2 Mbit/s.
    /// </summary>
    internal static long EstimateOneMinuteBytes(int width, int height, int frameRate)
    {
        var scale = (double)Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, frameRate) / (1920d * 1080d * 60d);
        var bitrate = Math.Clamp(Math.Round(16_000_000d * scale, MidpointRounding.AwayFromZero), 2_000_000d, int.MaxValue);
        return (long)((bitrate + 192_000d) * 60 / 8);
    }

    internal static string FormatBytes(long bytes)
    {
        const double mebi = 1024d * 1024d;
        return bytes >= 1024L * 1024 * 1024 ? $"{bytes / (mebi * 1024):0.0} GB" : $"{Math.Max(1, Math.Ceiling(bytes / mebi)):0} MB";
    }
}

/// <summary>Project titles and recording folder names.</summary>
internal static class RecordingNaming
{
    /// <summary>Trimmed title with characters Windows does not allow in file names removed; null when nothing usable is left.</summary>
    internal static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Trim().Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd('.', ' ');
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// A folder under <paramref name="root"/> that does not exist yet: "&lt;title&gt;-yyyyMMdd-HHmmss", or the timestamp plus a short id
    /// when there is no title. A name that is taken gets -2, -3 ... (never overwrites).
    /// </summary>
    internal static string UniqueFolder(string root, string? title)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = title is null ? stamp + "-" + Guid.NewGuid().ToString("N")[..6] : title + "-" + stamp;
        var candidate = Path.Combine(root, name);
        for (var number = 2; Directory.Exists(candidate) || File.Exists(candidate); number++) candidate = Path.Combine(root, name + "-" + number);
        return candidate;
    }

    /// <summary>Deletes a folder this app created a moment ago, retrying briefly while a just-stopped process releases its files. Failure is only logged.</summary>
    internal static async Task DeleteFolderQuietlyAsync(string folder)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) await Task.Run(() => Directory.Delete(folder, true));
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) { App.Log(error); return; }
                await Task.Delay(250);
            }
        }
    }
}
