using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    // Opt-in probe measures WPF assignment, not physical monitor presentation.
    internal Action<double>? PlaybackFramePresented;
    private sealed record PlaybackRequest(FrameTrailProject Project, string File, string Source, double Start,
        int Width, int Height, int FrameRate, bool NativeSource, string? AudioProxy, CancellationToken Lifetime);
    private sealed record PlaybackResources(PlaybackRequest Request, long SourceLength, DateTime SourceModified,
        PreviewPlaybackSession Session, bool HasAudio, string? AudioProxy)
    {
        public byte[] Pixels { get; } = GC.AllocateUninitializedArray<byte>(checked(Request.Width * Request.Height * 4));
        public PreparedPlaybackFrame? NextFrame { get; set; }
        public PreparedPlaybackFrame? HeldFrame { get; set; }
    }
    private sealed record PreparedPlaybackFrame(BitmapSource Bitmap, PreviewFrameInfo Info);
    private CancellationTokenSource? playbackCancellation;
    private CancellationTokenSource playbackResourcesLifetime = new();
    // Workers are serialized. Pausing stops presentation; the in-flight frame may
    // finish in the background so the next run can reuse the decoder and renderer.
    private Task playbackWork = Task.CompletedTask;
    private PlaybackResources? playbackResources;
    // Accessed only by the serialized playback worker. Proxies created while a
    // start is cancelled still belong to this project lifetime and are cleaned up.
    private readonly HashSet<string> playbackOwnedProxies = [];
    private int playbackRenderedFrames;
    private int playbackSessionGeneration;
    private int playbackAudioOpenCount;
    private bool playbackStarting;
    private FrameTrailProject? playbackProxyProject;
    private string? playbackAudioSource;
    private Task? playbackAudioReady;
    private TaskCompletionSource? playbackAudioOpened;

    private async Task StartRealtimePlayback()
    {
        if (project is null || projectFile is null || closingInProgress || closeReady) return;
        // Playback replaces whatever was being dragged: end every gesture and drop the live preview (P2-9).
        AbortActiveGestures(); ClearLiveEdit();
        StopPlayback();
        var owner = new CancellationTokenSource();
        playbackCancellation = owner;
        playing = true;
        playbackStarting = true;
        RefreshPreviewSelection(); // B-06: edit chrome is hidden while playing
        playbackRenderedFrames = 0;
        completedPreview = null;
        // CancelAsync marks cancellation immediately; process callbacks run away
        // from the input thread.
        if (seekCancellation is { } seek) _ = seek.CancelAsync();
        PlayButton.Content = new EditorIcon { Kind = "pause", Width = 18, Height = 18 };
        PreviewImage.Visibility = Visibility.Visible;
        Player.Visibility = Visibility.Collapsed;
        var size = PreviewOutputSize(project);
        var request = new PlaybackRequest(project, projectFile, FrameTrailProjectStore.ResolveMediaPath(projectFile, project),
            Playhead.Value >= Playhead.Maximum - .03 ? 0 : Playhead.Value, size.Width, size.Height,
            previewFrameRate, previewResolution == 0, ReferenceEquals(playbackProxyProject, project) ? proxyPath : null,
            playbackResourcesLifetime.Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = playbackWork;
        // B-18: the first play after an edit exports the audio proxy; say so instead of silently waiting.
        // (Reusing the proxy across edits that do not touch audio is not implemented: see the report.)
        var preparing = playbackResources is null || !ReferenceEquals(playbackResources.Request.Project, project);
        if (preparing) { Status.Text = PreparingPlaybackText; ShowNotice(PreparingPlaybackText); }
        playbackWork = Task.Run(() => RunPlaybackAsync(previous, request, owner, ready));
        try { await ready.Task; }
        catch (OperationCanceledException) { }
        finally { if (Status.Text == PreparingPlaybackText) Status.Text = ""; }
    }

    private const string PreparingPlaybackText = "正在准备播放…";

    /// <summary>Play stays disabled while there is nothing to play, during crop mode and while an export runs (B-29).</summary>
    private void UpdatePlaybackAvailability()
    {
        PlayButton.IsEnabled = !cropMode && operationCancellation == null && project is { } p && p.Timeline.Duration > .001;
    }

    /// <summary>Mac setPreviewActive: playback pauses when the window is minimised or hidden (B-28).</summary>
    private void WindowStateChangedPause(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && (playing || playbackStarting)) StopPlayback();
    }

    private void WindowVisibilityChangedPause(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && (playing || playbackStarting)) StopPlayback();
    }

    private async Task RunPlaybackAsync(Task previous, PlaybackRequest request, CancellationTokenSource owner,
        TaskCompletionSource ready)
    {
        var token = owner.Token;
        try
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            request.Lifetime.ThrowIfCancellationRequested();
            var resources = await GetPlaybackResourcesAsync(request).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Task audioReady = Task.CompletedTask;
            if (resources.HasAudio)
                audioReady = await Dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentPlayback(owner, request)) throw new OperationCanceledException(token);
                    proxyPath = resources.AudioProxy;
                    playbackProxyProject = request.Project;
                    return EnsurePlaybackAudioLoadedAsync(resources.AudioProxy!);
                });
            var pixels = resources.Pixels;
            // Reuse the held image and one prepared next frame when resuming.
            // Keep the original timestamp: advancing the video clock here would
            // silently skip an audio interval every time the user presses play.
            var held = resources.HeldFrame;
            var canResume = held is not null && Math.Abs(held.Info.OutputTime - request.Start) < 1e-6;
            var info = canResume ? held!.Info :
                await resources.Session.ReadFrameAsync(request.Start, pixels, request.Lifetime).ConfigureAwait(false);
            var firstFrame = canResume ? held!.Bitmap : CreatePlaybackBitmap(pixels, request);
            await audioReady.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var playbackStart = info.OutputTime;
            long clockOrigin = 0;
            using var frameClock = new PlaybackFrameClock();
            double ElapsedSeconds() => Stopwatch.GetElapsedTime(clockOrigin).TotalSeconds;
            long Deadline(double outputTime, double correction) => clockOrigin +
                (long)Math.Round((outputTime - playbackStart - correction) * Stopwatch.Frequency);
            var presented = await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentPlayback(owner, request)) return false;
                ApplyPlaybackFrame(firstFrame, info, request.Project);
                if (resources.HasAudio)
                {
                    Player.Position = TimeSpan.FromSeconds(playbackStart);
                    Player.Play();
                }
                clockOrigin = Stopwatch.GetTimestamp();
                playbackStarting = false;
                return true;
            });
            if (!presented) { ready.TrySetCanceled(); return; }
            resources.HeldFrame = new(firstFrame, info);
            if (!canResume) resources.NextFrame = null;
            ready.TrySetResult();
            var lastFrame = (long)Math.Floor(info.OutputTime * request.FrameRate + 1e-7);
            var audioCorrection = 0d;
            var nextAudioCheck = 1d;
            while (true)
            {
                var time = playbackStart + ElapsedSeconds() + audioCorrection;
                if (resources.HasAudio && ElapsedSeconds() >= nextAudioCheck && !token.IsCancellationRequested)
                {
                    var audioTime = await Dispatcher.InvokeAsync(() => Player.Position.TotalSeconds);
                    if (Math.Abs(audioTime - time) > .08) audioCorrection += audioTime - time;
                    nextAudioCheck = ElapsedSeconds() + 1;
                    time = playbackStart + ElapsedSeconds() + audioCorrection;
                }
                if (time >= request.Project.Timeline.Duration - .015) break;
                // Prepare one frame ahead while the displayed frame is held. Waiting
                // until its deadline to decode adds codec/render time to every frame.
                var frameIndex = Math.Max(lastFrame + 1, (long)Math.Floor(time * request.FrameRate));
                if (frameIndex / (double)request.FrameRate >= request.Project.Timeline.Duration)
                {
                    await frameClock.WaitUntilAsync(Deadline(request.Project.Timeline.Duration, audioCorrection), token).ConfigureAwait(false);
                    continue;
                }
                lastFrame = frameIndex;
                time = frameIndex / (double)request.FrameRate;
                var prepared = resources.NextFrame;
                var alreadyPrepared = prepared is not null && Math.Abs(prepared.Info.OutputTime - time) < 1e-6;
                info = alreadyPrepared ? prepared!.Info :
                    await resources.Session.ReadFrameAsync(time, pixels, request.Lifetime).ConfigureAwait(false);
                var frame = alreadyPrepared ? prepared!.Bitmap : CreatePlaybackBitmap(pixels, request);
                resources.NextFrame = new(frame, info);
                token.ThrowIfCancellationRequested();
                await frameClock.WaitUntilAsync(Deadline(time, audioCorrection), token).ConfigureAwait(false);
                presented = await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentPlayback(owner, request)) return false;
                    ApplyPlaybackFrame(frame, info, request.Project);
                    return true;
                });
                if (!presented) break;
                resources.HeldFrame = new(frame, info);
                resources.NextFrame = null;
            }
            if (!token.IsCancellationRequested)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentPlayback(owner, request)) return;
                    // B-23 (Mac AVPlayerItemDidPlayToEndTime): finish on the first frame, so Space plays from the start.
                    StopPlayback();
                    SetTimelinePosition(0, coalesce: false);
                });
        }
        catch (Exception) when (token.IsCancellationRequested || request.Lifetime.IsCancellationRequested)
        {
            ready.TrySetCanceled();
        }
        catch (Exception error)
        {
            App.Log(error);
            await ReleasePlaybackSessionAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentPlayback(owner, request)) return;
                StopPlayback();
                AutomationProperties.SetItemStatus(PreviewImage, "error");
                Status.Text = "预览播放未完成：" + error.Message;
            });
            ready.TrySetException(error);
        }
        finally
        {
            ready.TrySetCanceled();
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(playbackCancellation, owner)) StopPlayback();
            });
            owner.Dispose();
        }
    }

    private async Task<PlaybackResources> GetPlaybackResourcesAsync(PlaybackRequest request)
    {
        var source = new FileInfo(request.Source);
        var sourceChanged = playbackResources is { } previous && previous.Request.Source == request.Source &&
            (previous.SourceLength != source.Length || previous.SourceModified != source.LastWriteTimeUtc);
        if (playbackResources is { } existing && ReferenceEquals(existing.Request.Project, request.Project) &&
            existing.Request.File == request.File && existing.Request.Source == request.Source &&
            existing.Request.Width == request.Width && existing.Request.Height == request.Height &&
            existing.Request.FrameRate == request.FrameRate && existing.Request.NativeSource == request.NativeSource &&
            existing.SourceLength == source.Length && existing.SourceModified == source.LastWriteTimeUtc)
            return existing;
        await ReleasePlaybackSessionAsync().ConfigureAwait(false);
        var session = await PreviewPlaybackSession.CreateAsync(request.Project, request.Source, request.File,
            request.Width, request.Height, request.FrameRate, request.Lifetime,
            preserveSourceResolution: request.NativeSource, preferNativeDecoder: true).ConfigureAwait(false);
        string? newProxy = null;
        try
        {
            var snapshot = request.Project;
            var hasAudio = session.SourceHasAudio && MediaTools.HasAudibleMainAudio(snapshot.Timeline, snapshot.Settings.MainAudioVolume, snapshot.Settings.MainAudioMuted)
                || MediaTools.HasAudibleAudioTracks(snapshot.Timeline, snapshot.AudioTracks, snapshot.Settings.MainAudioVolume, snapshot.Settings.MainAudioMuted);
            if (snapshot.Camera is { Muted: false, Volume: > 0 } camera)
            {
                var cameraInfo = await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(request.File, camera.MediaPath), request.Lifetime).ConfigureAwait(false);
                if (cameraInfo.HasAudio)
                {
                    IReadOnlyList<CameraSyncSegment> mappings = camera.SyncSegments is { Count: > 0 } sync ? sync : [new CameraSyncSegment(camera.SourceStart, camera.TimelineStart, camera.Duration)];
                    var track = new AudioTrack { MediaPath = camera.MediaPath, SourceStart = camera.SourceStart, Duration = camera.Duration,
                        OffsetSeconds = camera.TimelineStart, Volume = camera.Volume, SourceRate = mappings[0].SourceRate, SyncSegments = mappings.ToList(), Source = AudioTrackSource.Camera };
                    hasAudio |= MediaTools.HasAudibleAudioTrack(snapshot.Timeline, track);
                }
            }
            var audioProxy = sourceChanged ? null : request.AudioProxy;
            if (hasAudio && (audioProxy is null || !File.Exists(audioProxy)))
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "cache");
                Directory.CreateDirectory(directory);
                newProxy = Path.Combine(directory, Guid.NewGuid() + ".m4a");
                await new ExportEngine().ExportAsync(snapshot, request.Source, newProxy,
                    new(960, 540, 30, true, true, request.File), cancellation: request.Lifetime).ConfigureAwait(false);
                audioProxy = newProxy;
            }
            request.Lifetime.ThrowIfCancellationRequested();
            playbackResources = new(request, source.Length, source.LastWriteTimeUtc, session, hasAudio, audioProxy);
            if (newProxy is not null) playbackOwnedProxies.Add(newProxy);
            Interlocked.Increment(ref playbackSessionGeneration);
            return playbackResources;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (newProxy is not null) try { File.Delete(newProxy); } catch (IOException) { }
            throw;
        }
    }

    private bool IsCurrentPlayback(CancellationTokenSource owner, PlaybackRequest request) =>
        ReferenceEquals(playbackCancellation, owner) && !owner.IsCancellationRequested && playing &&
        ReferenceEquals(project, request.Project) && !closingInProgress && !closeReady;

    private static BitmapSource CreatePlaybackBitmap(byte[] pixels, PlaybackRequest request)
    {
        var frame = BitmapSource.Create(request.Width, request.Height, 96, 96, PixelFormats.Bgra32, null, pixels, request.Width * 4);
        frame.Freeze();
        return frame;
    }

    private void ApplyPlaybackFrame(BitmapSource frame, PreviewFrameInfo info, FrameTrailProject snapshot)
    {
        var time = info.OutputTime;
        playbackRenderedFrames++;
        PreviewImage.Source = frame;
        if (info.Scene is { } scene) displayedPreviewScene = scene;
        else
        {
            if (!ReferenceEquals(evaluatedProject, snapshot))
            {
                previewEvaluator = new PreparedFrameSceneEvaluator(snapshot);
                evaluatedProject = snapshot;
            }
            displayedPreviewScene = previewEvaluator!.Evaluate(time);
        }
        displayedPreviewProject = snapshot;
        AutomationProperties.SetItemStatus(PreviewImage, "ready");
        var wasRefreshing = refreshing;
        refreshing = true;
        try { Playhead.Value = time; }
        finally { refreshing = wasRefreshing; }
        PlaybackFramePresented?.Invoke(time);
        UpdateTime();
    }

    private Task EnsurePlaybackAudioLoadedAsync(string path)
    {
        if (playbackAudioSource == path && playbackAudioReady is { IsFaulted: false, IsCanceled: false }) return playbackAudioReady;
        ClosePlaybackAudio();
        playbackAudioSource = path;
        return playbackAudioReady = LoadPlaybackAudioAsync(path);
    }

    private async Task LoadPlaybackAudioAsync(string path)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playbackAudioOpened = opened;
        RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
        EventHandler<ExceptionRoutedEventArgs> onFailed = (_, error) => opened.TrySetException(error.ErrorException);
        Player.MediaOpened += onOpened;
        Player.MediaFailed += onFailed;
        try
        {
            playbackAudioOpenCount++;
            Player.Source = new Uri(path);
            Player.Pause(); // Preroll silently; only the current playback owner starts audio.
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Player.MediaOpened -= onOpened;
            Player.MediaFailed -= onFailed;
            if (ReferenceEquals(playbackAudioOpened, opened)) playbackAudioOpened = null;
        }
    }

    private void ClosePlaybackAudio()
    {
        playbackAudioOpened?.TrySetCanceled();
        playbackAudioOpened = null;
        Player.Close();
        Player.Source = null;
        playbackAudioReady = null;
        playbackAudioSource = null;
    }

    private async Task ReleasePlaybackSessionAsync()
    {
        var old = playbackResources;
        playbackResources = null;
        if (old is not null) await old.Session.DisposeAsync().ConfigureAwait(false);
    }

    private Task DisposePlaybackResourcesAsync()
    {
        StopPlayback();
        var lifetime = playbackResourcesLifetime;
        playbackResourcesLifetime = new();
        var canceled = lifetime.CancelAsync();
        var previous = playbackWork;
        playbackWork = Task.Run(async () =>
        {
            await canceled.ConfigureAwait(false);
            await previous.ConfigureAwait(false);
            await ReleasePlaybackSessionAsync().ConfigureAwait(false);
            foreach (var path in playbackOwnedProxies)
            {
                try { File.Delete(path); }
                catch (IOException error) { App.Log(error); }
                catch (UnauthorizedAccessException error) { App.Log(error); }
            }
            playbackOwnedProxies.Clear();
            lifetime.Dispose();
        });
        return playbackWork;
    }
}
