using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using ScreenRecorderLib;
using NativeAudioOptions = ScreenRecorderLib.AudioOptions;
using NativeVideoOptions = ScreenRecorderLib.VideoEncoderOptions;

namespace FrameTrail.Capture;

public sealed class FrameCaptureService : IFrameCaptureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Recorder? _recorder;
    private InputSampler? _inputSampler;
    private RecordingRequest? _request;
    private TaskCompletionSource<bool>? _recordingStarted;
    private TaskCompletionSource<string>? _recordingFinished;
    private TaskCompletionSource<CaptureState>? _requestedStateChanged;
    private readonly List<CapturePauseInterval> _pausedIntervals = [];
    private long _pauseStartTicks;
    private CaptureState _state = CaptureState.Idle;
    private long _startTicks, _stopTicks;
    private long _firstFrameObservedTicks, _firstFrameSystemTimestampUnixMilliseconds;
    private int _firstFrameNumber, _firstEncodedFrameSeen;
    private int _windowPreviewDiagnosticsLogged;
    private int _width, _height, _frameRate;
    private string _encoderName = "H.264 Media Foundation";
    private EncoderMode _encoderMode = EncoderMode.Unknown;
    private string? _activeOutputPath;
    private readonly List<string> _warnings = [];
    private readonly List<string> _diagnostics = [];
    private string? _windowPreviewFirstFrameDiagnostic;
    private WindowRecordingSource? _windowSource;
    private CancellationTokenSource? _windowMonitorCancellation;
    private Task? _windowMonitorTask;
    private RecordingResult? _windowAutoStopResult;
    private int _windowTargetLost;
    private RecordingResult? _latestCompletedResult;
    private FfmpegDesktopRecorder? _desktopRecorder;
    private Exception? _backendFailure;
    private TaskCompletionSource<Exception>? _backendFailureSignal;

    public CaptureState State => _state;
    public RecordingResult? LatestCompletedResult => _latestCompletedResult;
    public long? CurrentCapturePrivateBytes => _desktopRecorder?.CurrentCapturePrivateBytes;
    public int? CurrentCaptureProcessId => _desktopRecorder?.CurrentCaptureProcessId;
    public event EventHandler<CaptureStateChanged>? StateChanged;
    public event EventHandler<CaptureError>? ErrorOccurred;
    public event EventHandler<InputEvent>? InputEventRecorded;
    public event Action<RecordingResult>? RecordingCompleted;
    /// <summary>
    /// Raised on a worker thread when the captured window is minimized (true) or restored (false).
    /// With at least one subscriber, minimizing no longer ends the recording; the owner is expected
    /// to pause and resume it. Without subscribers the old behaviour (finalize) is kept.
    /// </summary>
    public event Action<bool>? WindowTargetMinimizedChanged;
    private PixelRect? _lastWindowBounds;

    public static IReadOnlyList<RecoverableRecordingInfo> FindRecoverableRecordings(string directory) =>
        FfmpegDesktopRecorder.FindRecoverableRecordings(directory);

    public static Task<string> RecoverReadableRecordingAsync(RecoverableRecordingInfo recovery, string outputPath,
        CancellationToken cancellationToken = default) =>
        FfmpegDesktopRecorder.RecoverReadableRecordingAsync(recovery, outputPath, cancellationToken);

    public Task<IReadOnlyList<DisplayInfo>> EnumerateDisplaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<DisplayInfo>>(() =>
        {
            using var dpi = WindowsNative.UsePhysicalCoordinates();
            var native = Recorder.GetDisplays().ToDictionary(d => d.DeviceName, StringComparer.OrdinalIgnoreCase);
            return WindowsNative.EnumerateDisplays().Select(d => native.TryGetValue(d.Id, out var item)
                ? d with { Name = string.IsNullOrWhiteSpace(item.FriendlyName) ? d.Name : item.FriendlyName }
                : d).ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WindowInfo>> EnumerateWindowsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            using var dpi = WindowsNative.UsePhysicalCoordinates();
            return Recorder.GetWindows().Where(w => w.Handle != IntPtr.Zero && WindowsNative.IsWindow(w.Handle))
                .Select(w =>
                {
                    WindowsNative.TryGetVisibleWindowRect(w.Handle, out var rect);
                    var title = w.Title ?? string.Empty;
                    var pid = w.Pid;
                    return new WindowInfo(w.Handle.ToInt64().ToString("X"), title, pid, w.Handle, WindowsNative.ToPixelRect(rect));
                })
                .Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0 && !string.IsNullOrWhiteSpace(w.Title))
                .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<AudioDeviceInfo>>(() => Recorder.GetSystemAudioLoopbackDevices().Select(d => new AudioDeviceInfo(d.DeviceName, d.FriendlyName, AudioDeviceKind.SystemLoopback, d.IsDefaultDevice))
            .Concat(Recorder.GetSystemAudioCaptureDevices().Select(d => new AudioDeviceInfo(d.DeviceName, d.FriendlyName, AudioDeviceKind.Microphone, d.IsDefaultDevice))).ToArray(), cancellationToken);
    }

    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is CaptureState.Starting or CaptureState.Recording or CaptureState.Stopping)
                throw new InvalidOperationException("A recording is already active.");
            EnsureWindows();
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            _request = request;
            _warnings.Clear();
            _diagnostics.Clear();
            _backendFailure = null;
            _backendFailureSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pausedIntervals.Clear(); _pauseStartTicks = 0;
            _firstFrameObservedTicks = 0; _firstFrameSystemTimestampUnixMilliseconds = 0;
            _firstFrameNumber = 0; _firstEncodedFrameSeen = 0;
            _windowPreviewDiagnosticsLogged = 0; _windowPreviewFirstFrameDiagnostic = null;
            Interlocked.Exchange(ref _desktopExitFinalizationScheduled, 0);
            _windowSource = null; _windowAutoStopResult = null; _windowTargetLost = 0; _lastWindowBounds = null;
            ValidateRequest(request);
            _activeOutputPath = Path.GetFullPath(request.OutputPath);
            SetState(CaptureState.Starting);
            if (request.Target.Kind != CaptureTargetKind.Window)
            {
                await StartDesktopRecorderAsync(request, cancellationToken).ConfigureAwait(false);
                return;
            }
            var options = CreateRecorderOptions(request);
            _recordingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tracking = request.InputTracking ?? new InputTrackingOptions();
            if (tracking.Enabled)
            {
                try
                {
                    _inputSampler = new InputSampler(tracking, () => ResolveTargetBounds(request.Target), evt => InputEventRecorded?.Invoke(this, evt), ex => OnWindowInputFailure(request.Target, ex));
                    _inputSampler.Start();
                }
                catch (Exception ex)
                {
                    _warnings.Add($"Input tracking unavailable: {ex.Message}");
                    ErrorOccurred?.Invoke(this, new CaptureError("Input tracking could not be started.", ex));
                }
            }
            var outputPath = _activeOutputPath ?? throw new InvalidOperationException("The active recording output path is unavailable.");
            await Task.Run(() =>
            {
                // ScreenRecorderLib's WindowRecordingSource is initialized by Record; keep Recorder
                // creation and that initialization on the same worker thread as the reference probe.
                var recorder = Recorder.CreateRecorder(options);
                _recorder = recorder;
                recorder.OnStatusChanged += OnStatusChanged;
                recorder.OnFrameRecorded += OnFrameRecorded;
                recorder.OnRecordingFailed += OnRecordingFailed;
                recorder.OnRecordingComplete += OnRecordingComplete;
                _startTicks = Stopwatch.GetTimestamp();
                recorder.Record(outputPath);
            }, cancellationToken).ConfigureAwait(false);
            await _recordingStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (request.Target.Kind == CaptureTargetKind.Window)
            {
                _windowMonitorCancellation = new CancellationTokenSource();
                _windowMonitorTask = Task.Run(() => MonitorWindowTargetAsync(request.Target, _windowMonitorCancellation.Token));
            }
        }
        catch (OperationCanceledException ex)
        {
            if (_recorder is not null)
            {
                try { await Task.Run(_recorder.Stop).ConfigureAwait(false); } catch { }
                await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            }
            DeleteIncompleteOutput();
            SetState(CaptureState.Cancelled, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            if (_recorder is not null)
            {
                try { await Task.Run(_recorder.Stop).ConfigureAwait(false); } catch { }
                await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            }
            DeleteIncompleteOutput();
            SetState(CaptureState.Faulted, ex.Message);
            ErrorOccurred?.Invoke(this, new CaptureError(ex.Message, ex));
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_windowAutoStopResult is not null && _state == CaptureState.Completed)
                return _windowAutoStopResult;
            if (_desktopRecorder is not null)
            {
                SetState(CaptureState.Stopping);
                var stopObservedTicks = Stopwatch.GetTimestamp();
                await _desktopRecorder.StopAsync(cancellationToken).ConfigureAwait(false);
                _stopTicks = _desktopRecorder.LatestSegmentEndStopwatchTicks ?? stopObservedTicks;
                // A trailing paused interval contains no media and is omitted; StopObserved
                // preserves when the user actually requested stop for diagnostics.
                _pauseStartTicks = 0;
                var desktopInputPath = _inputSampler?.OutputPath;
                var encoder = _desktopRecorder.EncoderName;
                var mode = _desktopRecorder.EncoderMode;
                var progressFrameEstimate = _desktopRecorder.ProgressEstimatedFirstFrameStopwatchTicks;
                await DisposeRecordingComponentsAsync().ConfigureAwait(false);
                var desktopResult = new RecordingResult(_activeOutputPath!, _startTicks, _stopTicks, Stopwatch.Frequency,
                    mode, encoder, _width, _height, _frameRate, desktopInputPath, _warnings.ToArray(), _pausedIntervals.ToArray(),
                    _firstFrameObservedTicks == 0 ? null : _firstFrameObservedTicks, null, null, progressFrameEstimate, stopObservedTicks,
                    _diagnostics.ToArray());
                _latestCompletedResult = desktopResult;
                _activeOutputPath = null;
                SetState(CaptureState.Completed);
                try { RecordingCompleted?.Invoke(desktopResult); } catch (Exception ex) { ErrorOccurred?.Invoke(this, new CaptureError("录制结果已封装，但完成事件处理程序发生异常。", ex)); }
                return desktopResult;
            }
            if (_state is not (CaptureState.Recording or CaptureState.Paused or CaptureState.Faulted) || _recorder is null || _recordingFinished is null)
                throw new InvalidOperationException("There is no active recording.");
            var failedBackend = _backendFailure;
            SetState(CaptureState.Stopping);
            _stopTicks = Stopwatch.GetTimestamp();
            if (_pauseStartTicks != 0)
            {
                _pausedIntervals.Add(new CapturePauseInterval(_pauseStartTicks, _stopTicks));
                _pauseStartTicks = 0;
            }
            try { await Task.Run(_recorder.Stop).ConfigureAwait(false); }
            catch (Exception stopException)
            {
                failedBackend ??= _backendFailure;
                if (failedBackend is null) throw;
                _warnings.Add("Recorder stop reported an error after backend failure: " + stopException.Message);
            }
            failedBackend ??= _backendFailure;
            var output = string.Empty;
            try
            {
                if (failedBackend is null)
                {
                    var completed = _recordingFinished.Task;
                    var failure = _backendFailureSignal?.Task;
                    var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    var winner = failure is null ? await Task.WhenAny(completed, timeout).ConfigureAwait(false)
                        : await Task.WhenAny(completed, failure, timeout).ConfigureAwait(false);
                    if (winner == timeout) throw new TimeoutException("The recorder did not report completion within 30 seconds.");
                    if (failure is not null && winner == failure) failedBackend = await failure.ConfigureAwait(false);
                    else output = await completed.ConfigureAwait(false);
                }
                if (failedBackend is not null)
                    output = await _recordingFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch when (failedBackend is not null && _activeOutputPath is { } failedPath && CaptureFailurePolicy.HasNonEmptyCandidate(failedPath))
            {
                output = failedPath;
                _warnings.Add("The capture backend failed; a non-empty partial output was retained for media validation.");
            }
            if (failedBackend is not null && !_warnings.Any(x => x.Contains(failedBackend.Message, StringComparison.Ordinal)))
                _warnings.Add("Capture backend failure: " + failedBackend.Message);
            var inputPath = _inputSampler?.OutputPath;
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _windowPreviewFirstFrameDiagnostic) is { } previewDiagnostic) _diagnostics.Add(previewDiagnostic);
            else if (_request?.Target.Kind == CaptureTargetKind.Window) _diagnostics.Add("Window source preview did not yield a diagnostic frame before capture finalized.");
            SetState(CaptureState.Completed);
            _activeOutputPath = null;
            var result = new RecordingResult(output, _startTicks, _stopTicks, Stopwatch.Frequency, _encoderMode,
                _encoderName, _width, _height, _frameRate, inputPath, _warnings.ToArray(), _pausedIntervals.ToArray(),
                Interlocked.Read(ref _firstFrameObservedTicks) is var firstFrameTicks && firstFrameTicks != 0 ? firstFrameTicks : null,
                Volatile.Read(ref _firstEncodedFrameSeen) == 2 ? Volatile.Read(ref _firstFrameNumber) : null,
                Volatile.Read(ref _firstEncodedFrameSeen) == 2 ? Interlocked.Read(ref _firstFrameSystemTimestampUnixMilliseconds) : null,
                Diagnostics: _diagnostics.ToArray());
            _latestCompletedResult = result;
            if (Volatile.Read(ref _windowTargetLost) != 0) _windowAutoStopResult = result;
            try { RecordingCompleted?.Invoke(result); }
            catch (Exception ex) { try { ErrorOccurred?.Invoke(this, new CaptureError("录制结果已封装，但完成事件处理程序发生异常。", ex)); } catch { } }
            return result;
        }
        catch (Exception ex)
        {
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            SetState(CaptureState.Faulted, ex.Message);
            ErrorOccurred?.Invoke(this, new CaptureError(ex.Message, ex));
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != CaptureState.Recording || _recorder is null)
            {
                if (_state == CaptureState.Recording && _desktopRecorder is not null)
                {
                    _requestedStateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _desktopRecorder.PauseAsync(cancellationToken).ConfigureAwait(false);
                    _pauseStartTicks = _desktopRecorder.LatestSegmentEndStopwatchTicks ?? Stopwatch.GetTimestamp();
                    SetStateAt(CaptureState.Paused, _pauseStartTicks);
                    _inputSampler?.Pause();
                    return;
                }
                throw new InvalidOperationException("Only an active recording can be paused.");
            }
            _requestedStateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(_recorder.Pause, cancellationToken).ConfigureAwait(false);
            await _requestedStateChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _inputSampler?.Pause();
        }
        finally { _requestedStateChanged = null; _gate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != CaptureState.Paused || _recorder is null)
            {
                if (_state == CaptureState.Paused && _desktopRecorder is not null)
                {
                    await _desktopRecorder.ResumeAsync(cancellationToken).ConfigureAwait(false);
                    // ResumeAsync waits for the new segment's first frame. Use its setpts
                    // observation anchor as the pause boundary, instead of the later point
                    // when FFmpeg progress output or async startup happens to return.
                    var now = _desktopRecorder.LatestSegmentFirstFrameStopwatchTicks ?? Stopwatch.GetTimestamp();
                    if (_pauseStartTicks != 0) _pausedIntervals.Add(new(_pauseStartTicks, now));
                    _pauseStartTicks = 0;
                    SetStateAt(CaptureState.Recording, now);
                    _inputSampler?.Resume();
                    return;
                }
                throw new InvalidOperationException("Only a paused recording can be resumed.");
            }
            _requestedStateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(_recorder.Resume, cancellationToken).ConfigureAwait(false);
            await _requestedStateChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _inputSampler?.Resume();
        }
        finally { _requestedStateChanged = null; _gate.Release(); }
    }

    private void ValidateRequest(RecordingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath)) throw new ArgumentException("OutputPath is required.", nameof(request));
        var video = request.Video ?? new VideoOptions();
        if (video.FrameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(request), "FrameRate must be between 1 and 120.");
        if (!Enum.IsDefined(request.Target.Kind)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Target.Kind == CaptureTargetKind.Region && request.Target.Bounds is null) throw new ArgumentException("A region target requires physical pixel bounds.", nameof(request));
        if (request.Target.Kind == CaptureTargetKind.Window && request.Target.WindowHandle == IntPtr.Zero) throw new ArgumentException("A window target requires a valid HWND.", nameof(request));
        var path = Path.GetFullPath(request.OutputPath);
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OutputPath must end in .mp4.", nameof(request));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private async Task StartDesktopRecorderAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the target and the monitor list under one per-monitor-v2 DPI context.
            // Display targets carry only a device Id, so use that monitor's bounds directly
            // instead of resolving bounds in one context and comparing them in another.
            using var dpi = WindowsNative.UsePhysicalCoordinates();
            var displays = WindowsNative.EnumerateDisplays();
            DisplayInfo? display;
            PixelRect bounds;
            if (request.Target.Kind == CaptureTargetKind.Display)
            {
                display = displays.FirstOrDefault(d => string.Equals(d.Id, request.Target.Id, StringComparison.OrdinalIgnoreCase));
                if (display is null) throw new ArgumentException("Display device was not found.", nameof(request));
                bounds = display.Bounds;
            }
            else
            {
                bounds = ResolveTargetBounds(request.Target);
                display = displays.FirstOrDefault(d => Contains(d.Bounds, bounds));
                if (display is null) throw new ArgumentException("The selected display or region must fit inside one display.", nameof(request));
            }
            var outputIndex = displays.ToList().FindIndex(d => d.Id.Equals(display.Id, StringComparison.OrdinalIgnoreCase));
            var video = request.Video ?? new VideoOptions();
            _width = Even(video.Width ?? (video.Height is int h ? (int)Math.Round((double)h * bounds.Width / bounds.Height) : bounds.Width));
            _height = Even(video.Height ?? (video.Width is int w ? (int)Math.Round((double)w * bounds.Height / bounds.Width) : bounds.Height));
            _frameRate = video.FrameRate;
            var tracking = request.InputTracking ?? new InputTrackingOptions();
            _desktopRecorder = new FfmpegDesktopRecorder(_activeOutputPath!, outputIndex, display.Bounds,
                request.Target.Kind == CaptureTargetKind.Region ? request.Target.Bounds : null,
                video, observed => Interlocked.CompareExchange(ref _firstFrameObservedTicks, observed, 0), started =>
                {
                    _startTicks = started;
                    if (!tracking.Enabled) return;
                    try
                    {
                        _inputSampler = new InputSampler(tracking, () => ResolveTargetBounds(request.Target), evt => InputEventRecorded?.Invoke(this, evt),
                            ex => ErrorOccurred?.Invoke(this, new CaptureError("鼠标事件记录已中断，已写入的数据保留。", ex)));
                        _inputSampler.Start();
                    }
                    catch (Exception ex) { _warnings.Add($"Input tracking unavailable: {ex.Message}"); }
                });
            _desktopRecorder.UnexpectedExit += OnDesktopCaptureUnexpectedExit;
            _diagnostics.Add("Display/region capture uses FFmpeg ddagrab Desktop Duplication; audio is recorded as separate WAV tracks through ICaptureTrackService.");
            _diagnostics.Add("FirstFrameObservedStopwatchTicks is observed when the first ddagrab frame enters setpts and converted from FFmpeg wall-clock time to Stopwatch using a launch-time pair; it is an observation estimate, not a native frame PTS.");
            _diagnostics.Add("FFmpeg ddagrab output index follows DXGI enumeration; this machine's output-index mapping is not verified on multi-monitor systems.");
            if ((request.Audio?.RecordSystemAudio ?? false) || (request.Audio?.RecordMicrophone ?? false))
                _warnings.Add("AudioOptions are not muxed by the ddagrab backend; start independent WAV capture through ICaptureTrackService.");
            await _desktopRecorder.StartAsync(cancellationToken).ConfigureAwait(false);
            _encoderMode = _desktopRecorder.EncoderMode;
            _encoderName = _desktopRecorder.EncoderName;
            if (_encoderMode == EncoderMode.Unknown)
                _diagnostics.Add("FFmpeg Media Foundation encoder was selected, but its active hardware/software mode is not exposed.");
            if (_desktopRecorder.EncoderFailures.Count > 0)
                _warnings.Add("Encoder fallback attempts failed: " + string.Join(" | ", _desktopRecorder.EncoderFailures));
            SetState(CaptureState.Recording);
        }
        catch (OperationCanceledException ex)
        {
            if (_desktopRecorder is not null) { await _desktopRecorder.DisposeAsync().ConfigureAwait(false); _desktopRecorder = null; }
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            DeleteIncompleteOutput();
            SetState(CaptureState.Cancelled, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            if (_desktopRecorder is not null) { await _desktopRecorder.DisposeAsync().ConfigureAwait(false); _desktopRecorder = null; }
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            DeleteIncompleteOutput();
            SetState(CaptureState.Faulted, ex.Message);
            ErrorOccurred?.Invoke(this, new CaptureError(ex.Message, ex));
            throw;
        }
    }

    private int _desktopExitFinalizationScheduled;
    private void OnDesktopCaptureUnexpectedExit(Exception exception)
    {
        if (Interlocked.Exchange(ref _desktopExitFinalizationScheduled, 1) != 0) return;
        var message = $"FFmpeg desktop-capture process exited unexpectedly. Readable completed segments are retained; recovery manifest: {_desktopRecorder?.RecoveryManifestPath}";
        _warnings.Add(message);
        ErrorOccurred?.Invoke(this, new CaptureError(message, exception));
        _ = Task.Run(async () =>
        {
            try
            {
                if (_state is CaptureState.Recording or CaptureState.Paused or CaptureState.Starting)
                    await StopAsync().ConfigureAwait(false);
            }
            catch { /* The recovery manifest remains available when finalization cannot produce an output MP4. */ }
        });
    }

    private RecorderOptions CreateRecorderOptions(RecordingRequest request)
    {
        var video = request.Video ?? new VideoOptions();
        var targetBounds = ResolveTargetBounds(request.Target);
        _width = Even(video.Width ?? (video.Height is int requestedHeight ? (int)Math.Round((double)requestedHeight * targetBounds.Width / targetBounds.Height) : targetBounds.Width));
        _height = Even(video.Height ?? (video.Width is int requestedWidth ? (int)Math.Round((double)requestedWidth * targetBounds.Height / targetBounds.Width) : targetBounds.Height));
        _frameRate = video.FrameRate;
        if (video.EncoderPreference == EncoderPreference.Software) _encoderMode = EncoderMode.Software;
        else _encoderMode = EncoderMode.Unknown;
        var srcOptions = new SourceOptions();
        RecordingSourceBase source;
        switch (request.Target.Kind)
        {
            case CaptureTargetKind.Display:
                source = new DisplayRecordingSource(request.Target.Id) { RecorderApi = RecorderApi.DesktopDuplication, IsCursorCaptureEnabled = false };
                break;
            case CaptureTargetKind.Window:
                _diagnostics.Add("Window capture uses ScreenRecorderLib WindowRecordingSource (Windows Graphics Capture). The first-image time anchor uses Stopwatch when the source preview callback arrives, with the recorder frame callback as fallback; neither callback exposes a native source-frame timestamp.");
                var windowSource = new WindowRecordingSource(request.Target.WindowHandle)
                {
                    IsBorderRequired = false,
                    IsCursorCaptureEnabled = false,
                    // Keep source dimensions while diagnosing delivery; the first callback anchors timing.
                    IsVideoFramePreviewEnabled = true,
                    VideoFramePreviewSize = new ScreenRecorderLib.ScreenSize(targetBounds.Width, targetBounds.Height)
                };
                _windowSource = windowSource;
                windowSource.OnFrameRecorded += OnWindowSourceFrameRecorded;
                source = windowSource;
                break;
            case CaptureTargetKind.Region:
                var bounds = request.Target.Bounds!;
                var display = WindowsNative.EnumerateDisplays().FirstOrDefault(d => Contains(d.Bounds, bounds));
                if (display is null) throw new ArgumentException("The selected region must fit inside one display.", nameof(request));
                source = new DisplayRecordingSource(display.Id) { RecorderApi = RecorderApi.DesktopDuplication, IsCursorCaptureEnabled = false,
                    SourceRect = new ScreenRecorderLib.ScreenRect(bounds.X - display.Bounds.X, bounds.Y - display.Bounds.Y, bounds.Width, bounds.Height) };
                break;
            default: throw new ArgumentOutOfRangeException();
        }
        srcOptions.RecordingSources = [source];
        var encoder = new H264VideoEncoder();
        var videoOptions = new NativeVideoOptions
        {
            Encoder = encoder, Framerate = video.FrameRate, Quality = Math.Clamp(video.Quality, 1, 100),
            IsHardwareEncodingEnabled = video.EncoderPreference != EncoderPreference.Software,
            IsFixedFramerate = video.FixedFrameRate, IsMp4FastStartEnabled = false, IsFragmentedMp4Enabled = false, IsLowLatencyEnabled = true
        };
        if (video.Bitrate is int bitrate) videoOptions.Bitrate = bitrate;
        if (video.EncoderPreference == EncoderPreference.Hardware)
            _diagnostics.Add("Hardware encoding was requested; ScreenRecorderLib does not expose which encoder Media Foundation ultimately selected.");
        else if (video.EncoderPreference == EncoderPreference.Automatic)
            _diagnostics.Add("Encoder mode is reported as Unknown because ScreenRecorderLib does not expose Media Foundation encoder selection.");
        var outputOptions = new OutputOptions { OutputFrameSize = new ScreenRecorderLib.ScreenSize(_width, _height), RecorderMode = RecorderMode.Video };
        var audioOptions = CreateAudioOptions(request.Audio ?? new AudioOptions());
        var mouse = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false };
        return new RecorderOptions { SourceOptions = srcOptions, VideoEncoderOptions = videoOptions, OutputOptions = outputOptions, AudioOptions = audioOptions, MouseOptions = mouse };
    }

    private static NativeAudioOptions CreateAudioOptions(AudioOptions requested)
    {
        var options = new NativeAudioOptions { IsAudioEnabled = requested.RecordSystemAudio || requested.RecordMicrophone };
        var sources = new List<AudioSourceBase>();
        if (requested.RecordSystemAudio) sources.Add(string.IsNullOrWhiteSpace(requested.SystemAudioDeviceId) ? LoopbackAudioSource.Default : new LoopbackAudioSource(requested.SystemAudioDeviceId));
        if (requested.RecordMicrophone) sources.Add(string.IsNullOrWhiteSpace(requested.MicrophoneDeviceId) ? CaptureAudioSource.Default : new CaptureAudioSource(requested.MicrophoneDeviceId));
        options.AudioSources = sources;
        return options;
    }

    private PixelRect ResolveTargetBounds(CaptureTarget target)
    {
        using var dpi = WindowsNative.UsePhysicalCoordinates();
        if (target.Kind == CaptureTargetKind.Region) return target.Bounds!;
        if (target.Kind == CaptureTargetKind.Window)
        {
            // While the owner handles minimize by pausing, keep pointer normalization on the last known bounds.
            if (WindowTargetMinimizedChanged is not null && _lastWindowBounds is { } lastBounds
                && WindowsNative.IsWindow(target.WindowHandle) && WindowsNative.IsIconic(target.WindowHandle)) return lastBounds;
            if (!WindowsNative.IsWindow(target.WindowHandle) || WindowsNative.IsIconic(target.WindowHandle)
                || !WindowsNative.TryGetVisibleWindowRect(target.WindowHandle, out var rect)) throw new ArgumentException("Window handle is no longer valid or is minimized.", nameof(target));
            var windowBounds = WindowsNative.ToPixelRect(rect);
            _lastWindowBounds = windowBounds;
            return windowBounds;
        }
        return WindowsNative.EnumerateDisplays().FirstOrDefault(d => string.Equals(d.Id, target.Id, StringComparison.OrdinalIgnoreCase))?.Bounds
            ?? throw new ArgumentException("Display device was not found.", nameof(target));
    }

    private void RaiseWindowMinimized(bool minimized)
    {
        try { WindowTargetMinimizedChanged?.Invoke(minimized); }
        catch (Exception ex) { try { ErrorOccurred?.Invoke(this, new CaptureError("窗口最小化状态通知失败。", ex)); } catch { } }
    }

    private async Task MonitorWindowTargetAsync(CaptureTarget target, CancellationToken cancellationToken)
    {
        var minimized = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                if (!WindowsNative.IsWindow(target.WindowHandle))
                {
                    FailWindowTarget("The captured window was closed; recording is being finalized.");
                    return;
                }
                if (WindowsNative.IsIconic(target.WindowHandle))
                {
                    if (WindowTargetMinimizedChanged is null)
                    {
                        FailWindowTarget("The captured window was minimized; recording is being finalized.");
                        return;
                    }
                    if (!minimized) { minimized = true; RaiseWindowMinimized(true); }
                    continue;
                }
                if (minimized) { minimized = false; RaiseWindowMinimized(false); }
                if (!WindowsNative.TryGetVisibleWindowRect(target.WindowHandle, out var nativeRect))
                {
                    FailWindowTarget("The captured window bounds are unavailable; recording is being finalized.");
                    return;
                }
                var bounds = WindowsNative.ToPixelRect(nativeRect);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    FailWindowTarget("The captured window was minimized or has no visible bounds; recording is being finalized.");
                    return;
                }
                // WindowRecordingSource follows the HWND through moves, resizes and display changes.
                // Keep validating its screen bounds for pointer normalization; do not recrop the source.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            FailWindowTarget("Window capture tracking failed; recording is being finalized.", ex);
        }
    }

    private void OnWindowInputFailure(CaptureTarget target, Exception exception)
    {
        // Input sampling can notice target loss before the 100 ms window monitor.
        // Route that normal end condition through the same one-shot finalization.
        if (exception is ArgumentException && !WindowsNative.IsWindow(target.WindowHandle))
            FailWindowTarget("The captured window was closed; recording is being finalized.");
        else if (exception is ArgumentException && WindowsNative.IsIconic(target.WindowHandle))
        {
            if (WindowTargetMinimizedChanged is null) FailWindowTarget("The captured window was minimized; recording is being finalized.");
            else ErrorOccurred?.Invoke(this, new CaptureError("鼠标事件记录已中断，已写入的数据保留。", exception));
        }
        else
            ErrorOccurred?.Invoke(this, new CaptureError("鼠标事件记录已中断，已写入的数据保留。", exception));
    }

    private void FailWindowTarget(string message, Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _windowTargetLost, 1) != 0) return;
        ErrorOccurred?.Invoke(this, new CaptureError(message, exception));
        _ = Task.Run(async () => { try { await StopAsync().ConfigureAwait(false); } catch { } });
    }
    private static bool Contains(PixelRect outer, PixelRect inner) => inner.Width > 0 && inner.Height > 0 && inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
    private static int Even(int value) => Math.Max(2, value - value % 2);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FrameTrail.Capture requires Windows."); }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        var ticks = Stopwatch.GetTimestamp();
        if (e.Status == ScreenRecorderLib.RecorderStatus.Recording)
        {
            if (_state == CaptureState.Paused && _pauseStartTicks != 0)
            {
                _pausedIntervals.Add(new CapturePauseInterval(_pauseStartTicks, ticks));
                _pauseStartTicks = 0;
            }
            SetStateAt(CaptureState.Recording, ticks);
            _recordingStarted?.TrySetResult(true);
        }
        else if (e.Status == ScreenRecorderLib.RecorderStatus.Paused)
        {
            _pauseStartTicks = ticks;
            SetStateAt(CaptureState.Paused, ticks);
        }
        else if (e.Status == ScreenRecorderLib.RecorderStatus.Finishing) SetStateAt(CaptureState.Stopping, ticks);
        _requestedStateChanged?.TrySetResult(_state);
    }
    private void OnFrameRecorded(object? sender, FrameRecordedEventArgs e)
    {
        MarkFirstImageObserved();
        if (Interlocked.CompareExchange(ref _firstEncodedFrameSeen, 1, 0) != 0) return;
        Volatile.Write(ref _firstFrameNumber, e.FrameNumber);
        Interlocked.Exchange(ref _firstFrameSystemTimestampUnixMilliseconds, e.Timestamp);
        Volatile.Write(ref _firstEncodedFrameSeen, 2);
    }

    private void OnWindowSourceFrameRecorded(object? sender, FrameDataRecordedEventArgs e)
    {
        // This preview callback reports the first source image but exposes no native frame PTS.
        // Record callback-observation time only; never retain the native bitmap buffer.
        try
        {
            var bitmap = e.BitmapData;
            if (bitmap is null || bitmap.Data == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
                return;
            MarkFirstImageObserved();
            if (Interlocked.CompareExchange(ref _windowPreviewDiagnosticsLogged, 1, 0) == 0)
                Volatile.Write(ref _windowPreviewFirstFrameDiagnostic, SummarizeWindowPreview(bitmap));
        }
        catch (Exception)
        {
            // Native callbacks must not leak managed exceptions into the capture thread.
        }
    }

    private void MarkFirstImageObserved()
    {
        Interlocked.CompareExchange(ref _firstFrameObservedTicks, Stopwatch.GetTimestamp(), 0);
    }

    private static string SummarizeWindowPreview(FrameBitmapData bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = bitmap.Stride;
        if (Math.Abs((long)stride) < (long)width * 4)
            return $"Window source preview diagnostic: invalid BGRA stride; width={width}, height={height}, stride={stride}.";
        double intensitySum = 0;
        var brightSamples = 0;
        var count = 0;
        foreach (var y in new[] { height / 4, height / 2, Math.Max(0, height * 3 / 4) })
        foreach (var x in new[] { width / 4, width / 2, Math.Max(0, width * 3 / 4) })
        {
            var offset = checked(y * stride + x * 4);
            var blue = Marshal.ReadByte(bitmap.Data, offset);
            var green = Marshal.ReadByte(bitmap.Data, offset + 1);
            var red = Marshal.ReadByte(bitmap.Data, offset + 2);
            intensitySum += (red + green + blue) / 3.0;
            if (Math.Max(red, Math.Max(green, blue)) >= 48) brightSamples++;
            count++;
        }
        return $"Window source preview first-frame sample: {width}x{height}, stride={stride}, gridMeanRgbIntensity={intensitySum / count:F2}, gridBrightSamples={brightSamples}/{count}.";
    }
    internal void InjectRecordingFailureForProbe(Exception failure) => HandleRecordingFailure(failure);
    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e) =>
        HandleRecordingFailure(new InvalidOperationException(e.Error));
    private void HandleRecordingFailure(Exception exception)
    {
        CaptureFailurePolicy.ApplyWindowFailure(exception, _state, _recordingStarted, _recordingFinished, failure => _backendFailure = failure);
        _backendFailureSignal?.TrySetResult(exception);
        if (_state != CaptureState.Starting)
        {
            _warnings.Add("Capture backend failure: " + exception.Message);
        }
        ErrorOccurred?.Invoke(this, new CaptureError(exception.Message, exception));
        SetState(CaptureState.Faulted, exception.Message);
    }
    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e) => _recordingFinished?.TrySetResult(e.FilePath);
    private void SetState(CaptureState state, string? detail = null)
    {
        _state = state;
        StateChanged?.Invoke(this, new CaptureStateChanged(state, Stopwatch.GetTimestamp(), detail));
    }
    private void SetStateAt(CaptureState state, long ticks, string? detail = null)
    {
        _state = state;
        StateChanged?.Invoke(this, new CaptureStateChanged(state, ticks, detail));
    }
    private void DeleteIncompleteOutput()
    {
        if (_activeOutputPath is null) return;
        try { if (File.Exists(_activeOutputPath)) File.Delete(_activeOutputPath); } catch { }
        _activeOutputPath = null;
    }
    private async Task DisposeRecordingComponentsAsync()
    {
        _windowMonitorCancellation?.Cancel();
        if (_windowMonitorTask is not null)
        {
            try { await _windowMonitorTask.ConfigureAwait(false); } catch { }
            _windowMonitorTask = null;
        }
        _windowMonitorCancellation?.Dispose(); _windowMonitorCancellation = null;
        if (_windowSource is not null)
        {
            _windowSource.OnFrameRecorded -= OnWindowSourceFrameRecorded;
            _windowSource = null;
        }
        if (_desktopRecorder is not null) { await _desktopRecorder.DisposeAsync().ConfigureAwait(false); _desktopRecorder = null; }
        _recorder?.Dispose(); _recorder = null;
        if (_inputSampler is not null) { await _inputSampler.DisposeAsync().ConfigureAwait(false); _inputSampler = null; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_state is CaptureState.Recording or CaptureState.Paused or CaptureState.Faulted) { try { await StopAsync().ConfigureAwait(false); } catch { } }
        await DisposeRecordingComponentsAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}



