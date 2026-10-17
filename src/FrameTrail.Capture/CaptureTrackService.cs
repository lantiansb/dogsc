using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;
using NativeAudioOptions = ScreenRecorderLib.AudioOptions;
using NativeVideoOptions = ScreenRecorderLib.VideoEncoderOptions;

namespace FrameTrail.Capture;

/// <summary>Records cameras as MP4 assets and individual audio inputs as PCM WAV assets.</summary>
public sealed class CaptureTrackService : ICaptureTrackService
{
    private readonly ConcurrentDictionary<Guid, TrackSession> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _disposeSync = new();
    private volatile bool _disposeRequested;
    private Task? _disposeTask;

    public event EventHandler<CaptureTrackStateChanged>? StateChanged;
    public event EventHandler<CaptureError>? ErrorOccurred;

    public Task<IReadOnlyList<CameraInfo>> EnumerateCamerasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<CameraInfo>>(() => Recorder.GetSystemVideoCaptureDevices()
            .Select(device => new CameraInfo(device.DeviceName, device.FriendlyName,
                Recorder.GetSupportedVideoCaptureFormatsForDevice(device.DeviceName).Select(format => new CameraFormatInfo(
                    format.Index, format.VideoFormatName, (int)Math.Round(format.FrameSize.Width), (int)Math.Round(format.FrameSize.Height),
                    format.Framerate, format.AverageBitrate)).ToArray()))
            .ToArray(), cancellationToken);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<AudioDeviceInfo>>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var renderDefault = TryGetDefault(enumerator, DataFlow.Render);
            using var captureDefault = TryGetDefault(enumerator, DataFlow.Capture);
            var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, AudioDeviceKind.SystemLoopback,
                    string.Equals(d.ID, renderDefault?.ID, StringComparison.OrdinalIgnoreCase)));
            var capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, AudioDeviceKind.Microphone,
                    string.Equals(d.ID, captureDefault?.ID, StringComparison.OrdinalIgnoreCase)));
            return render.Concat(capture).ToArray();
        }, cancellationToken);
    }

    public async Task<CaptureTrackHandle> StartCameraAsync(CameraRecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterStartGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var camera = Recorder.GetSystemVideoCaptureDevices().FirstOrDefault(x =>
                string.Equals(x.DeviceName, request.CameraDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Camera device was not found.", nameof(request));
            var formats = Recorder.GetSupportedVideoCaptureFormatsForDevice(camera.DeviceName);
            var format = request.FormatIndex is int index
                ? formats.FirstOrDefault(x => x.Index == index) ?? throw new ArgumentException("Camera format index was not found.", nameof(request))
                : formats.OrderByDescending(x => x.FrameSize.Width * (long)x.FrameSize.Height)
                    .ThenByDescending(x => x.Framerate).FirstOrDefault();
            if (format is null) throw new InvalidOperationException("The camera did not report a supported capture format.");
            var source = new VideoCaptureRecordingSource(camera.DeviceName, format);
            var video = request.Video is null
                ? new VideoOptions(FrameRate: Math.Max(1, (int)Math.Round(format.Framerate)),
                    Width: (int)Math.Round(format.FrameSize.Width), Height: (int)Math.Round(format.FrameSize.Height))
                : request.Video with
                {
                    Width = request.Video.Width ?? (int)Math.Round(format.FrameSize.Width),
                    Height = request.Video.Height ?? (int)Math.Round(format.FrameSize.Height)
                };
            var options = CreateOptions(CaptureTrackKind.Camera, source, null, video);
            return await StartCoreAsync(CaptureTrackKind.Camera, request.OutputPath, options, cancellationToken).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }
    }

    public async Task<CaptureTrackHandle> StartAudioTrackAsync(AudioTrackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceKind is not (AudioDeviceKind.SystemLoopback or AudioDeviceKind.Microphone))
            throw new ArgumentOutOfRangeException(nameof(request));
        await EnterStartGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var devices = await EnumerateAudioDevicesAsync(cancellationToken).ConfigureAwait(false);
            var device = string.IsNullOrWhiteSpace(request.DeviceId)
                ? devices.FirstOrDefault(x => x.Kind == request.DeviceKind && x.IsDefault)
                : devices.FirstOrDefault(x => x.Kind == request.DeviceKind && string.Equals(x.Id, request.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) throw new ArgumentException("Audio device was not found.", nameof(request));
            var kind = request.DeviceKind == AudioDeviceKind.SystemLoopback ? CaptureTrackKind.SystemAudio : CaptureTrackKind.Microphone;
            ValidateAudioOutput(request.OutputPath);
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDevice(device.Id);
            TimestampedWasapiCapture? audioCapture = null;
            WaveFileWriter? waveWriter = null;
            FileStream? outputStream = null;
            var ownsOutputFile = false;
            TrackSession? session = null;
            var registered = false;
            try
            {
                audioCapture = new TimestampedWasapiCapture(endpoint, request.DeviceKind == AudioDeviceKind.SystemLoopback);
                outputStream = new FileStream(Path.GetFullPath(request.OutputPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ownsOutputFile = true;
                waveWriter = new WaveFileWriter(outputStream, audioCapture.WaveFormat);
                outputStream = null; // The WaveFileWriter now owns the stream.
                session = new TrackSession(kind, request.OutputPath)
                {
                    AudioCapture = audioCapture,
                    AudioSampleRate = audioCapture.WaveFormat.SampleRate,
                    WaveFileWriter = waveWriter
                };
                session.Diagnostics.Add($"WASAPI audio format: {audioCapture.WaveFormat}; standalone WAV contains no video.");
                session.Diagnostics.Add("WASAPI device positions place continuous audio packets; QPC anchors the source clock and measures drift. Timestamp-error packets retain valid contiguous device positions and use arrival time only when an anchor/reset is needed.");
                audioCapture.PacketAvailable += (packet, frames, devicePosition, qpcPosition, arrivalTicks, flags) =>
                {
                    try
                    {
                        var packetDuration = frames * 1000d / Math.Max(1, audioCapture.WaveFormat.SampleRate);
                        if (packetDuration > session.MaxAudioPacketMilliseconds) session.MaxAudioPacketMilliseconds = packetDuration;
                        lock (session.AudioWriteGate)
                        {
                            if (session.AudioStartTicks == 0) return;
                            var placement = session.AudioPositionMapper!.Map(devicePosition, frames, qpcPosition,
                                arrivalTicks, flags);
                            var timing = placement.Timing;
                            if (placement.ClockReanchored && !session.AudioPaused)
                            {
                                // Flush all old-epoch packets before switching the active
                                // source clock segment. The current packet is enqueued below.
                                DrainAudioPackets(session, placement.StartFrame == long.MinValue
                                    ? long.MinValue : placement.StartFrame - 1);
                                var boundaryOutputFrame = AudioPacketTimeline.SourceFrameToActiveOutputFrame(
                                    placement.StartFrame, session.AudioPauseFrameIntervals);
                                if (boundaryOutputFrame < session.AudioFramesWritten)
                                {
                                    session.Warnings.Add("A WASAPI clock reanchor mapped behind the already-written WAV cursor; the new source-clock segment was clamped to the written boundary.");
                                    boundaryOutputFrame = session.AudioFramesWritten;
                                }
                                WriteAudioSilence(session, boundaryOutputFrame - session.AudioFramesWritten,
                                    audioCapture.WaveFormat.BlockAlign, arrivalTicks, AudioSilenceSource.PacketGap);
                                AddAudioClockSegment(session, placement.ClockBoundaryStopwatchTicks, boundaryOutputFrame);
                                session.AudioSegmentStartTicks = placement.ClockBoundaryStopwatchTicks;
                                session.AudioSegmentStartOutputFrame = boundaryOutputFrame;
                            }
                            session.AudioDiagnostics ??= new AudioPacketDiagnostics(session.AudioStartTicks, Stopwatch.Frequency);
                            session.AudioDiagnostics.PositionSample(session.AudioPacketCount + 1, devicePosition, frames,
                                qpcPosition, arrivalTicks, flags, placement.DevicePositionMode);
                            session.AudioDiagnostics.Packet(arrivalTicks, frames, (flags & AudioClientBufferFlags.Silent) != 0,
                                timing.ArrivalLatencyMilliseconds, timing.UsedArrivalFallback, timing.TimestampError,
                                timing.DataDiscontinuity, placement.DevicePositionMismatch, placement.DevicePositionReset,
                                placement.QpcResidualMilliseconds, placement.DeviceClockDriftPpm);
                            session.AudioPackets.Enqueue(new AudioPacket(placement.StartFrame, packet, timing, arrivalTicks,
                                (flags & AudioClientBufferFlags.Silent) != 0));
                            session.AudioPacketCount++;
                            if (session.FirstDevicePosition is null)
                            {
                                session.FirstDevicePosition = devicePosition;
                                session.FirstPacketOffsetMilliseconds = placement.StartFrame * 1000d / audioCapture.WaveFormat.SampleRate;
                            }
                            if (placement.DevicePositionMismatch)
                                session.DevicePositionMismatchCount++;
                            if (placement.DevicePositionReset)
                                session.DevicePositionResetCount++;
                            session.ExpectedDevicePosition = checked(devicePosition + frames);
                            if (timing.UsedArrivalFallback) session.TimestampFallbackCount++;
                            if (timing.TimestampError) session.TimestampErrorCount++;
                            if (timing.DataDiscontinuity) session.DataDiscontinuityCount++;
                            if (double.IsFinite(timing.ArrivalLatencyMilliseconds)) AddLatencySample(session, timing.ArrivalLatencyMilliseconds);
                        }
                    }
                    catch (Exception ex) { NotifyError(new CaptureError($"WASAPI audio write failed: {ex.Message}", ex)); }
                };
                audioCapture.RecordingStopped += exception =>
                {
                    session.AudioRecordingStopped = true;
                    if (exception is not null)
                    {
                        session.AudioFailure = exception;
                        session.AudioStopped.TrySetException(exception);
                        try { SetState(session, CaptureTrackState.Faulted, Stopwatch.GetTimestamp(), exception.Message); } catch { }
                        NotifyError(new CaptureError($"Audio capture stopped unexpectedly: {exception.Message}", exception));
                    }
                    else session.AudioStopped.TrySetResult(true);
                };
                if (!_sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not register audio track.");
                registered = true;
                SetState(session, CaptureTrackState.Starting);
                session.AudioStartTicks = Stopwatch.GetTimestamp();
                session.AudioSegmentStartTicks = session.AudioStartTicks;
                session.AudioSegmentStartOutputFrame = 0;
                session.AudioPositionMapper = new AudioPacketPositionMapper(session.AudioStartTicks,
                    audioCapture.WaveFormat.SampleRate, Stopwatch.Frequency);
                session.AudioDiagnostics = new AudioPacketDiagnostics(session.AudioStartTicks, Stopwatch.Frequency);
                session.AudioDiagnostics.SetAudioClockFrequency(audioCapture.AudioClockFrequency,
                    audioCapture.AudioClockFrequencyError);
                session.AudioDiagnostics.SetStreamFormat(audioCapture.WaveFormat.SampleRate,
                    audioCapture.WaveFormat.BlockAlign, audioCapture.WaveFormat.Channels,
                    audioCapture.WaveFormat.BitsPerSample);
                audioCapture.StartRecording();
                var startedTicks = session.AudioStartTicks;
                session.Handle = new CaptureTrackHandle(session.Id, kind, session.OutputPath, startedTicks, Stopwatch.Frequency);
                session.AudioPumpCancellation = new CancellationTokenSource();
                session.AudioPumpTask = Task.Run(() => PumpAudioTimelineAsync(session, session.AudioPumpCancellation.Token));
                SetState(session, CaptureTrackState.Recording, startedTicks);
                return session.Handle;
            }
            catch
            {
                if (registered && session is not null) _sessions.TryRemove(session.Id, out _);
                if (session?.AudioPumpCancellation is { } pumpCancellation)
                {
                    pumpCancellation.Cancel();
                    if (session.AudioPumpTask is not null)
                    {
                        try { await session.AudioPumpTask.ConfigureAwait(false); } catch { }
                    }
                    pumpCancellation.Dispose();
                    session.AudioPumpCancellation = null;
                    session.AudioPumpTask = null;
                }
                try { audioCapture?.StopRecording(); } catch { }
                try { audioCapture?.Dispose(); } catch { }
                try { outputStream?.Dispose(); } catch { }
                try { waveWriter?.Dispose(); } catch { }
                if (ownsOutputFile)
                {
                    try { if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath); } catch { }
                }
                if (session is not null) try { SetState(session, CaptureTrackState.Faulted, "Audio track startup failed."); } catch { }
                throw;
            }
        }
        finally { _startGate.Release(); }
    }

    public Task PauseTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => SetTrackPausedAsync(trackId, true, cancellationToken);
    public Task ResumeTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => SetTrackPausedAsync(trackId, false, cancellationToken);

    public async Task<CaptureTrackResult> StopTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(trackId, out var session)) throw new KeyNotFoundException($"Capture track {trackId} was not found.");
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CaptureFailurePolicy.CanStopTrack(session.State))
                throw new InvalidOperationException("The capture track is not active.");
            var backendFailure = session.FailureException;
            var stopTicks = Stopwatch.GetTimestamp();
            var audioStoppedWhilePaused = session.AudioCapture is not null && session.PauseStartTicks != 0;
            lock (session.AudioWriteGate)
            {
                if (session.PauseStartTicks != 0)
                {
                    var pauseStartFrame = session.PauseStartSourceFrame ?? SourceFrameAtTicks(session, session.PauseStartTicks);
                    var pauseEndFrame = SourceFrameAtTicks(session, stopTicks);
                    var priorPausedFrames = session.AudioPauseFrameIntervals.Sum(interval => Math.Max(0, interval.EndFrame - interval.StartFrame));
                    var pauseOutputFrame = Math.Max(0, pauseStartFrame - priorPausedFrames);
                    var boundaryOffset = Math.Max(
                        RecordPauseBoundaryNominalOffset(session, session.PauseStartTicks, pauseStartFrame),
                        RecordPauseBoundaryNominalOffset(session, stopTicks, pauseEndFrame));
                    AddAudioClockSegment(session, session.PauseStartTicks, pauseOutputFrame);
                    session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, stopTicks,
                        pauseStartFrame, pauseEndFrame, session.AudioSampleRate, boundaryOffset, pauseOutputFrame));
                    session.AudioPauseFrameIntervals.Add((pauseStartFrame, pauseEndFrame));
                    session.PauseStartTicks = 0;
                    session.PauseStartSourceFrame = null;
                }
                if (session.AudioCapture is not null) session.AudioPaused = true;
            }
            SetState(session, CaptureTrackState.Stopping);
            if (session.AudioCapture is not null)
            {
                session.AudioPumpCancellation?.Cancel();
                if (session.AudioPumpTask is not null)
                {
                    try { await session.AudioPumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                if (!session.AudioRecordingStopped) session.AudioCapture.StopRecording();
                try { await session.AudioStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Including the timeout case: a stuck audio device must not abort StopTrackAsync.
                    session.Warnings.Add($"Audio capture ended unexpectedly or timed out; WAV was finalized with clock-filled silence where needed: {(session.AudioFailure?.Message ?? ex.Message)}");
                }
                // Finalizing the audio track must never abort StopTrackAsync. If the WAV writer
                // fails here, the exception used to propagate out of StopTrackAsync and take the
                // already-recorded screen.mp4 down with it, discarding a good screen recording
                // because of a broken audio track. Keep the failure as a warning and still finalize.
                Exception? finalizeFailure = null;
                lock (session.AudioWriteGate)
                {
                    try
                    {
                        var blockAlign = Math.Max(1, session.AudioCapture.WaveFormat.BlockAlign);
                        var sourceStopFrame = SourceFrameAtTicks(session, stopTicks);
                        var targetFrames = GetAudioActiveOutputFrame(session, stopTicks);
                        DrainAudioPackets(session, long.MaxValue, sourceStopFrame, targetFrames);
                        WriteAudioSilence(session, Math.Max(0, targetFrames - session.AudioFramesWritten), blockAlign,
                            stopTicks, AudioSilenceSource.Finalization);
                        if (!audioStoppedWhilePaused) AddAudioClockSegment(session, stopTicks, session.AudioFramesWritten);
                    }
                    catch (Exception ex)
                    {
                        finalizeFailure = ex;
                        session.Warnings.Add($"Audio WAV finalization failed after {session.AudioFramesWritten} frames were written; the audio track was truncated and the recording continues: {ex.Message}");
                        Trace.TraceError("Audio finalization failed: {0}", ex);
                    }
                }
                session.AudioPumpCancellation?.Dispose(); session.AudioPumpCancellation = null; session.AudioPumpTask = null;
                try { session.WaveFileWriter?.Dispose(); }
                catch (Exception writerException)
                {
                    Trace.TraceError("Wave writer disposal after audio failure failed: {0}", writerException);
                    session.Warnings.Add($"Audio WAV writer could not be finalized cleanly: {writerException.Message}; the audio track may end before its last packet.");
                }
                try { session.AudioCapture.Dispose(); } catch { }
                var activeDurationSeconds = session.AudioClockSegments.Sum(segment =>
                    Math.Max(0, segment.EndStopwatchTicks - segment.StartStopwatchTicks) / (double)Stopwatch.Frequency);
                if (session.AudioPositionMapper?.DeviceClockDriftPpm is double measuredDrift)
                {
                    var predictedDifferenceMs = Math.Abs(measuredDrift) * activeDurationSeconds / 1_000d;
                    session.AudioDiagnostics?.SetClockEstimate(measuredDrift, predictedDifferenceMs);
                    if (predictedDifferenceMs >= 100d)
                        session.Warnings.Add($"Estimated audio device clock drift may accumulate about {predictedDifferenceMs:F1} ms over {activeDurationSeconds:F1} active seconds ({measuredDrift:F1} ppm); per-segment source rates are recorded for timeline mapping.");
                }
                if (session.AudioDiagnostics is { } diagnostics)
                {
                    diagnostics.SetDevicePositionMode(session.AudioPositionMapper?.DevicePositionMode);
                    var diagnosticsPath = session.OutputPath + ".diagnostics.json";
                    var diagnosticTicks = Stopwatch.GetTimestamp();
                    var totals = diagnostics.Snapshot(diagnosticTicks);
                    try
                    {
                        await diagnostics.WriteJsonAsync(diagnosticsPath, diagnosticTicks, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception diagnosticsException)
                    {
                        session.Warnings.Add($"Audio packet diagnostics file could not be written: {diagnosticsException.Message}; totals packets={totals.PacketCount}, packetFrames={totals.PacketFrames}, silentFlagFrames={totals.SilentFlagFrames}, writtenFrames={totals.ActuallyWrittenFrames}, overlapDroppedFrames={totals.OverlapDroppedFrames}, pumpSilenceFrames={totals.PumpGeneratedSilenceFrames}.");
                    }
                }
                if (session.PausedIntervals.Count > 0)
                    session.Diagnostics.Add($"Audio pause boundaries were mapped using {session.AudioPositionMapper?.DevicePositionMode ?? "unknown"} timing; largest packet was {session.MaxAudioPacketMilliseconds:F1} ms.");
                session.Diagnostics.Add($"WASAPI packet timing: packets={session.AudioPacketCount}, firstPacketOffset={session.FirstPacketOffsetMilliseconds:F3} ms, firstDevicePosition={session.FirstDevicePosition?.ToString() ?? "unknown"}, positionMode={session.AudioPositionMapper?.DevicePositionMode ?? "unknown"}, timestampFallbacks={session.TimestampFallbackCount}, timestampErrors={session.TimestampErrorCount}, discontinuities={session.DataDiscontinuityCount}, devicePositionMismatches={session.DevicePositionMismatchCount}, devicePositionResets={session.DevicePositionResetCount}, qpcResidualAbsMax={session.AudioPositionMapper?.MaxQpcResidualMilliseconds:F3} ms, deviceClockDrift={session.AudioPositionMapper?.DeviceClockDriftPpm?.ToString("F1") ?? "unmeasured"} ppm, sampledArrivalLatencyP95={Percentile(session.ArrivalLatencySamples, 0.95):F3} ms (reservoir {session.ArrivalLatencySamples.Count}/{session.ArrivalLatencySampleCount}).");
                if (finalizeFailure is not null)
                    session.Diagnostics.Add($"Audio finalization ended early: {finalizeFailure.Message}; writtenFrames={session.AudioFramesWritten}.");
            }
            else
            {
                try { await Task.Run(session.Recorder.Stop, cancellationToken).ConfigureAwait(false); }
                catch (Exception stopException)
                {
                    backendFailure ??= session.FailureException;
                    if (backendFailure is null) throw;
                    session.Warnings.Add("Camera recorder stop reported an error after backend failure: " + stopException.Message);
                }
                backendFailure ??= session.FailureException;
                try
                {
                    if (backendFailure is null)
                    {
                        var finished = session.Finished.Task;
                        var failure = session.FailureSignal.Task;
                        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
                        var winner = await Task.WhenAny(finished, failure, timeout).ConfigureAwait(false);
                        if (winner == timeout) throw new TimeoutException("The camera recorder did not report completion within 30 seconds.");
                        if (winner == failure) backendFailure = await failure.ConfigureAwait(false);
                        else await finished.ConfigureAwait(false);
                    }
                    if (backendFailure is not null)
                        await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch when (backendFailure is not null && CaptureFailurePolicy.HasNonEmptyCandidate(session.OutputPath))
                {
                    session.Warnings.Add("The capture backend failed; a non-empty partial output was retained for media validation.");
                }
                if (backendFailure is not null && !CaptureFailurePolicy.HasNonEmptyCandidate(session.OutputPath))
                    throw new InvalidOperationException("Camera capture failed before producing a non-empty output: " + backendFailure.Message, backendFailure);
                if (backendFailure is not null) session.Warnings.Add("Camera capture failure: " + backendFailure.Message);
                session.Recorder.Dispose();
            }
            _sessions.TryRemove(trackId, out _);
            SetState(session, CaptureTrackState.Completed);
            var audioSourceRate = session.AudioClockSegments.Count == 0 ? (double?)null :
                session.AudioClockSegments.Sum(segment => segment.SourceEndFrame - segment.SourceStartFrame) /
                (double)session.AudioSampleRate!.Value /
                (session.AudioClockSegments.Sum(segment => segment.EndStopwatchTicks - segment.StartStopwatchTicks) / (double)Stopwatch.Frequency);
            return new CaptureTrackResult(session.Handle!, stopTicks, session.Width, session.Height,
                session.FrameRate, session.EncoderMode, session.Warnings.ToArray(), session.PausedIntervals.ToArray(),
                session.AudioSampleRate, session.AudioPositionMapper?.DeviceClockDriftPpm,
                session.AudioCapture is null ? null : session.AudioFramesWritten, session.AudioClockSegments.ToArray(), audioSourceRate,
                session.Diagnostics.ToArray());
        }
        catch (Exception ex)
        {
            NotifyError(new CaptureError($"Capture track failed: {ex.Message}", ex));
            try { session.AudioCapture?.StopRecording(); } catch { }
            if (session.AudioPumpCancellation is { } pumpCancellation)
            {
                try { pumpCancellation.Cancel(); } catch (Exception cleanupException) { Trace.TraceError("Audio pump cancellation failed: {0}", cleanupException); }
                if (session.AudioPumpTask is { } pumpTask)
                {
                    try { await pumpTask.ConfigureAwait(false); }
                    catch (Exception cleanupException) { Trace.TraceError("Audio pump completion after track failure failed: {0}", cleanupException); }
                }
                try { pumpCancellation.Dispose(); } catch (Exception cleanupException) { Trace.TraceError("Audio pump cancellation disposal failed: {0}", cleanupException); }
                session.AudioPumpCancellation = null;
                session.AudioPumpTask = null;
            }
            try { session.WaveFileWriter?.Dispose(); } catch { }
            try { session.AudioCapture?.Dispose(); } catch { }
            try { session.Recorder?.Dispose(); } catch { }
            _sessions.TryRemove(trackId, out _);
            SetState(session, CaptureTrackState.Faulted, ex.Message);
            throw;
        }
        finally { session.Gate.Release(); }
    }

    private async Task<CaptureTrackHandle> StartCoreAsync(CaptureTrackKind kind, string outputPath,
        RecorderOptions options, CancellationToken cancellationToken)
    {
        ValidateOutput(outputPath);
        var session = new TrackSession(kind, outputPath);
        var registered = false;
        try
        {
            session.Recorder = Recorder.CreateRecorder(options);
            session.Recorder.OnStatusChanged += (_, args) => OnStatusChanged(session, args);
            session.Recorder.OnRecordingFailed += (_, args) =>
            {
                var ex = new InvalidOperationException(args.Error);
                session.FailureException = ex;
                session.FailureSignal.TrySetResult(ex);
                if (session.State == CaptureTrackState.Starting)
                {
                    session.Started.TrySetException(ex);
                    session.Finished.TrySetException(ex);
                }
                else session.Warnings.Add("Capture backend failure: " + args.Error);
                NotifyError(new CaptureError(args.Error, ex));
                SetState(session, CaptureTrackState.Faulted, args.Error);
            };
            session.Recorder.OnRecordingComplete += (_, args) => session.Finished.TrySetResult(args.FilePath);
            ConfigureOutputMetadata(session, options);
            if (!_sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not register capture track.");
            registered = true;
            SetState(session, CaptureTrackState.Starting);
            await Task.Run(() => session.Recorder.Record(session.OutputPath), cancellationToken).ConfigureAwait(false);
            var startedTicks = await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            session.Handle = new CaptureTrackHandle(session.Id, kind, session.OutputPath, startedTicks, Stopwatch.Frequency);
            return session.Handle;
        }
        catch (Exception ex)
        {
            try { await Task.Run(session.Recorder.Stop).ConfigureAwait(false); } catch { }
            try { session.Recorder?.Dispose(); } catch (Exception cleanupException) { Trace.TraceError("Recorder disposal after startup failure failed: {0}", cleanupException); }
            if (registered) _sessions.TryRemove(session.Id, out _);
            try { if (File.Exists(session.OutputPath)) File.Delete(session.OutputPath); } catch { }
            SetState(session, CaptureTrackState.Faulted, ex.Message);
            throw;
        }
    }

    private async Task SetTrackPausedAsync(Guid trackId, bool pause, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(trackId, out var session)) throw new KeyNotFoundException($"Capture track {trackId} was not found.");
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expected = pause ? CaptureTrackState.Recording : CaptureTrackState.Paused;
            if (session.State != expected) throw new InvalidOperationException(pause ? "The track is not recording." : "The track is not paused.");
            if (session.AudioCapture is not null)
            {
                var now = Stopwatch.GetTimestamp();
                if (pause)
                {
                    lock (session.AudioWriteGate)
                    {
                        session.AudioPaused = true;
                        session.PauseStartTicks = now;
                        session.PauseStartSourceFrame = SourceFrameAtTicks(session, now);
                    }
                    SetState(session, CaptureTrackState.Paused, now);
                }
                else
                {
                    lock (session.AudioWriteGate)
                    {
                        session.AudioPaused = false;
                        var pauseStartFrame = session.PauseStartSourceFrame ?? SourceFrameAtTicks(session, session.PauseStartTicks);
                        var pauseEndFrame = SourceFrameAtTicks(session, now);
                        var priorPausedFrames = session.AudioPauseFrameIntervals.Sum(interval => Math.Max(0, interval.EndFrame - interval.StartFrame));
                        var pauseOutputFrame = Math.Max(0, pauseStartFrame - priorPausedFrames);
                        var boundaryOffset = Math.Max(
                            RecordPauseBoundaryNominalOffset(session, session.PauseStartTicks, pauseStartFrame),
                            RecordPauseBoundaryNominalOffset(session, now, pauseEndFrame));
                        session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, now,
                            pauseStartFrame, pauseEndFrame, session.AudioSampleRate, boundaryOffset, pauseOutputFrame));
                        AddAudioClockSegment(session, session.PauseStartTicks, pauseOutputFrame);
                        session.AudioPauseFrameIntervals.Add((pauseStartFrame, pauseEndFrame));
                        session.PauseStartTicks = 0;
                        session.PauseStartSourceFrame = null;
                        session.AudioSegmentStartTicks = now;
                        session.AudioSegmentStartOutputFrame = pauseOutputFrame;
                    }
                    SetState(session, CaptureTrackState.Recording, now);
                }
                return;
            }
            session.ExpectedStatus = pause ? CaptureTrackState.Paused : CaptureTrackState.Recording;
            session.StatusChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(pause ? session.Recorder.Pause : session.Recorder.Resume, cancellationToken).ConfigureAwait(false);
            await session.StatusChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        finally { session.StatusChanged = null; session.Gate.Release(); }
    }

    private RecorderOptions CreateOptions(CaptureTrackKind kind, RecordingSourceBase source,
        NativeAudioOptions? audio, VideoOptions video)
    {
        var camera = kind == CaptureTrackKind.Camera;
        var sourceOptions = new SourceOptions { RecordingSources = [source] };
        var options = new NativeVideoOptions
        {
            Encoder = new H264VideoEncoder(), Framerate = Math.Clamp(video.FrameRate, 1, 120),
            Quality = Math.Clamp(video.Quality, 1, 100), Bitrate = video.Bitrate ?? (camera ? 4_000_000 : 128_000),
            IsHardwareEncodingEnabled = video.EncoderPreference != EncoderPreference.Software,
            IsFixedFramerate = video.FixedFrameRate, IsMp4FastStartEnabled = false, IsFragmentedMp4Enabled = false,
            IsLowLatencyEnabled = true
        };
        var cameraFormat = camera && source is VideoCaptureRecordingSource cameraSource
            ? Recorder.GetSupportedVideoCaptureFormatsForDevice(cameraSource.DeviceName).FirstOrDefault() : null;
        var width = Even(video.Width ?? (cameraFormat is null ? 640 : (int)Math.Round(cameraFormat.FrameSize.Width)));
        var height = Even(video.Height ?? (cameraFormat is null ? 480 : (int)Math.Round(cameraFormat.FrameSize.Height)));
        var output = new OutputOptions
        {
            OutputFrameSize = new ScreenRecorderLib.ScreenSize(width, height),
            RecorderMode = RecorderMode.Video,
            IsVideoCaptureEnabled = camera
        };
        var mouse = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false };
        return new RecorderOptions
        {
            SourceOptions = sourceOptions, VideoEncoderOptions = options, OutputOptions = output,
            AudioOptions = audio ?? new NativeAudioOptions(), MouseOptions = mouse
        };
    }

    private void ConfigureOutputMetadata(TrackSession session, RecorderOptions options)
    {
        session.Width = (int)Math.Round(options.OutputOptions.OutputFrameSize.Width);
        session.Height = (int)Math.Round(options.OutputOptions.OutputFrameSize.Height);
        session.FrameRate = options.VideoEncoderOptions.Framerate;
        session.EncoderMode = options.VideoEncoderOptions.IsHardwareEncodingEnabled ? EncoderMode.Unknown : EncoderMode.Software;
        if (session.EncoderMode == EncoderMode.Unknown)
            session.Diagnostics.Add("Media Foundation does not expose which encoder was selected.");
    }

    private void OnStatusChanged(TrackSession session, RecordingStatusEventArgs args)
    {
        var ticks = Stopwatch.GetTimestamp();
        switch (args.Status)
        {
            case RecorderStatus.Recording:
                if (session.State == CaptureTrackState.Paused && session.PauseStartTicks != 0)
                {
                    session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, ticks));
                    session.PauseStartTicks = 0;
                }
                if (session.State == CaptureTrackState.Starting) session.Started.TrySetResult(ticks);
                SetState(session, CaptureTrackState.Recording, ticks);
                break;
            case RecorderStatus.Paused:
                session.PauseStartTicks = ticks;
                SetState(session, CaptureTrackState.Paused, ticks);
                break;
            case RecorderStatus.Finishing:
                SetState(session, CaptureTrackState.Stopping, ticks);
                break;
        }
        if (session.State == session.ExpectedStatus) session.StatusChanged?.TrySetResult(session.State);
    }

    private void SetState(TrackSession session, CaptureTrackState state, long? ticks = null, string? detail = null)
    {
        session.State = state;
        NotifySubscribersSafely(StateChanged, this,
            new CaptureTrackStateChanged(session.Id, session.Kind, state, ticks ?? Stopwatch.GetTimestamp(), detail), nameof(StateChanged));
    }
    private void SetState(TrackSession session, CaptureTrackState state, string? detail) =>
        SetState(session, state, Stopwatch.GetTimestamp(), detail);

    private void NotifyError(CaptureError error) =>
        NotifySubscribersSafely(ErrorOccurred, this, error, nameof(ErrorOccurred));

    internal static void NotifySubscribersSafely<T>(EventHandler<T>? subscribers, object sender, T args, string eventName)
    {
        if (subscribers is null) return;
        foreach (EventHandler<T> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(sender, args); }
            catch (Exception ex) { Trace.TraceError("CaptureTrackService {0} subscriber failed: {1}", eventName, ex); }
        }
    }

    private static void ValidateOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("OutputPath is required.", nameof(outputPath));
        var path = Path.GetFullPath(outputPath);
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OutputPath must end in .mp4.", nameof(outputPath));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private static void ValidateAudioOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("OutputPath is required.", nameof(outputPath));
        var path = Path.GetFullPath(outputPath);
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Audio track OutputPath must end in .wav.", nameof(outputPath));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private static MMDevice? TryGetDefault(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    private static async Task PumpAudioTimelineAsync(TrackSession session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var format = session.AudioCapture!.WaveFormat;
        var blockAlign = Math.Max(1, format.BlockAlign);
        var reorderFrames = Math.Max(1, format.SampleRate / 10); // 100 ms
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (session.AudioWriteGate)
                {
                    if (session.AudioStartTicks == 0) continue;
                    var nowTicks = Stopwatch.GetTimestamp();
                    var targetSourceFrame = Math.Max(0, SourceFrameAtTicks(session, nowTicks) - reorderFrames);
                    DrainAudioPackets(session, targetSourceFrame);
                    var targetOutputFrame = Math.Max(0, GetAudioActiveOutputFrame(session, nowTicks) - reorderFrames);
                    WriteAudioSilence(session, Math.Max(0, targetOutputFrame - session.AudioFramesWritten), blockAlign,
                        nowTicks, AudioSilenceSource.Pump);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        // The pump runs in the background for the whole recording. If the WAV writer ever fails
        // here, the fault must stay inside this task: letting it escape makes StopTrackAsync throw
        // and takes the finished screen recording down with it.
        catch (Exception ex)
        {
            session.Warnings.Add("Audio timeline pump stopped after a WAV write failure; audio stops here and the recording continues: " + ex.Message);
            Trace.TraceError("Audio timeline pump failed: {0}", ex);
        }
    }

    private static void DrainAudioPackets(TrackSession session, long dueThroughFrame, long? sourceStopFrame = null, long? outputStopFrame = null)
    {
        var writer = session.WaveFileWriter;
        var blockAlign = Math.Max(1, session.AudioCapture!.WaveFormat.BlockAlign);
        while (session.AudioPackets.TryPeek(out var packet) && packet.StartFrame <= dueThroughFrame)
        {
            session.AudioPackets.Dequeue();
            var packetFrames = packet.Data.Length / blockAlign;
            var pauseRanges = MapAudioPauseRanges(session);
            if (session.AudioPaused && session.PauseStartTicks > 0)
            {
                var start = session.PauseStartSourceFrame ?? SourceFrameAtTicks(session, session.PauseStartTicks);
                pauseRanges.Add((start, long.MaxValue));
            }
            foreach (var segment in AudioPacketTimeline.MapPacket(packet.StartFrame, packetFrames, pauseRanges,
                         sourceStopFrame ?? long.MaxValue, outputStopFrame ?? long.MaxValue))
            {
                var segmentFrameCount = segment.FrameCount;
                WriteAudioSilence(session, Math.Max(0, segment.OutputStartFrame - session.AudioFramesWritten), blockAlign,
                    packet.ArrivalStopwatchTicks, AudioSilenceSource.PacketGap);
                var overlapFrames = Math.Max(0, session.AudioFramesWritten - segment.OutputStartFrame);
                var remainingFrames = Math.Max(0, segmentFrameCount - overlapFrames);
                if (overlapFrames > 0) session.AudioDiagnostics?.OverlapDropped(packet.ArrivalStopwatchTicks, Math.Min(overlapFrames, segmentFrameCount));
                if (remainingFrames == 0 || writer is null) continue;
                var offset = checked((int)((segment.InputFrameOffset + overlapFrames) * blockAlign));
                var count = checked((int)(remainingFrames * blockAlign));
                count = ClampAudioWriteBytes(session, writer, count);
                // The WAV has no room left. Stop consuming packets instead of dequeuing the rest of
                // the queue for nothing; finalization records why the audio track stops here.
                if (count <= 0) { session.AudioPackets.Clear(); return; }
                writer.Write(packet.Data, offset, count);
                session.AudioFramesWritten += count / blockAlign;
                session.AudioDiagnostics?.Written(packet.ArrivalStopwatchTicks, count / blockAlign, packet.EndpointSilent);
            }
        }
    }

    // A WAV payload lives in a 32-bit RIFF size field, so NAudio refuses writes past ~4 GiB and
    // throws "WAV file too large" from WaveFileWriter.Write. WaveFileWriter.Length counts data
    // bytes only; the 44-byte RIFF/WAVE header sits outside it.
    private const long MaxWaveDataBytes = 4294967295L - 44L;

    /// <summary>
    /// Clamps one audio write to the remaining RIFF payload. A runaway silence fill must degrade
    /// into a warning, never into an exception that discards an otherwise good screen recording.
    /// </summary>
    private static int ClampAudioWriteBytes(TrackSession session, WaveFileWriter writer, int count)
    {
        if (count <= 0) return 0;
        var blockAlign = Math.Max(1, session.AudioCapture!.WaveFormat.BlockAlign);
        var available = (MaxWaveDataBytes - writer.Length) / blockAlign * blockAlign;
        if (available >= count) return count;
        if (!session.AudioWaveSizeLimitReported)
        {
            session.AudioWaveSizeLimitReported = true;
            session.Warnings.Add($"Audio WAV reached the 4 GiB RIFF limit after {writer.Length} bytes; further audio was not written and the audio track was kept up to this point.");
        }
        return available > 0 ? (int)available : 0;
    }

    private static void WriteAudioSilence(TrackSession session, long frameCount, int blockAlign, long eventTicks, AudioSilenceSource source)
    {
        var writer = session.WaveFileWriter;
        if (frameCount <= 0 || writer is null) return;
        var format = session.AudioCapture!.WaveFormat;
        const int maximumChunkFrames = 8192;
        var silenceFrame = new byte[blockAlign];
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 8) Array.Fill(silenceFrame, (byte)128);
        // The requested gap comes from the audio device position clock projected onto QPC time. When
        // the two disagree — or when a capture epoch is anchored at frame 0 — the target frame can be
        // far beyond real elapsed time, and filling it literally wrote gigabytes of silence until the
        // WAV ceiling aborted the whole recording. No single fill may represent more than a couple of
        // seconds of wall clock; the surplus is reported instead of written.
        var maximumFrames = Math.Max(1L, (long)Math.Max(1, format.SampleRate) * 2);
        if (frameCount > maximumFrames)
        {
            if (!session.AudioSilenceTruncationReported)
            {
                session.AudioSilenceTruncationReported = true;
                session.Warnings.Add($"Audio {source} silence fill of {frameCount} frames was truncated to {maximumFrames} frames ({maximumFrames / (double)Math.Max(1, format.SampleRate):F2}s): the audio device clock disagreed with the system clock, so the requested timeline gap was not real.");
            }
            frameCount = maximumFrames;
        }
        while (frameCount > 0)
        {
            var frames = (int)Math.Min(frameCount, maximumChunkFrames);
            var bytes = new byte[checked(frames * blockAlign)];
            for (var i = 0; i < bytes.Length; i += blockAlign) Buffer.BlockCopy(silenceFrame, 0, bytes, i, blockAlign);
            var writable = ClampAudioWriteBytes(session, writer, bytes.Length);
            if (writable < bytes.Length)
            {
                if (writable <= 0) return;
                frames = writable / blockAlign;
                bytes = new byte[checked(frames * blockAlign)];
                for (var i = 0; i < bytes.Length; i += blockAlign) Buffer.BlockCopy(silenceFrame, 0, bytes, i, blockAlign);
            }
            writer.Write(bytes, 0, bytes.Length);
            session.AudioFramesWritten += frames;
            session.AudioDiagnostics?.SilenceWritten(eventTicks, frames, source);
            frameCount -= frames;
        }
    }

    private static long SourceFrameAtTicks(TrackSession session, long ticks)
    {
        if (session.AudioStartTicks == 0) return 0;
        return Math.Max(0, session.AudioPositionMapper?.StopwatchTicksToSourceFrame(ticks) ??
            (long)((decimal)(ticks - session.AudioStartTicks) * session.AudioCapture!.WaveFormat.SampleRate / Stopwatch.Frequency));
    }

    private static long GetAudioActiveOutputFrame(TrackSession session, long ticks)
    {
        var sourceFrame = SourceFrameAtTicks(session, ticks);
        var removed = MapAudioPauseRanges(session)
            .Sum(interval => Math.Max(0, Math.Min(sourceFrame, interval.EndFrame) - Math.Min(sourceFrame, interval.StartFrame)));
        if (session.AudioPaused && session.PauseStartTicks != 0)
        {
            var pauseStart = session.PauseStartSourceFrame ?? SourceFrameAtTicks(session, session.PauseStartTicks);
            removed += Math.Max(0, sourceFrame - Math.Min(sourceFrame, pauseStart));
        }
        return Math.Max(0, sourceFrame - removed);
    }

    private static List<(long StartFrame, long EndFrame)> MapAudioPauseRanges(TrackSession session) =>
        session.AudioPauseFrameIntervals.ToList();

    private static long RecordPauseBoundaryNominalOffset(TrackSession session, long stopwatchTicks, long sourceFrame)
    {
        var nominalFrame = (long)((decimal)(stopwatchTicks - session.AudioStartTicks) *
                                  session.AudioCapture!.WaveFormat.SampleRate / Stopwatch.Frequency);
        var offsetFrames = Math.Abs(sourceFrame - nominalFrame);
        session.AudioDiagnostics?.PauseBoundaryMapped(stopwatchTicks, offsetFrames);
        return offsetFrames;
    }

    private static void AddAudioClockSegment(TrackSession session, long endStopwatchTicks, long sourceEndOutputFrame)
    {
        if (session.AudioSampleRate is not int sampleRate || sampleRate <= 0) return;
        var segment = AudioClockSegmentMath.Create(session.AudioSegmentStartTicks, endStopwatchTicks,
            session.AudioSegmentStartOutputFrame, Math.Max(session.AudioSegmentStartOutputFrame, sourceEndOutputFrame),
            sampleRate, Stopwatch.Frequency);
        if (segment is not null) session.AudioClockSegments.Add(segment);
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[(int)Math.Clamp(Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static void AddLatencySample(TrackSession session, double value)
    {
        session.ArrivalLatencySampleCount++;
        const int reservoirSize = 4096;
        if (session.ArrivalLatencySamples.Count < reservoirSize)
        {
            session.ArrivalLatencySamples.Add(value);
            return;
        }
        var replacement = Random.Shared.NextInt64(session.ArrivalLatencySampleCount);
        if (replacement < reservoirSize) session.ArrivalLatencySamples[(int)replacement] = value;
    }

    private static int Even(int value) => Math.Max(2, value - value % 2);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FrameTrail.Capture requires Windows."); }
    private async Task EnterStartGateAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); }
        catch { _startGate.Release(); throw; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposeRequested) throw new ObjectDisposedException(nameof(CaptureTrackService));
    }

    internal async Task RunStartGateProbeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await EnterStartGateAsync(CancellationToken.None).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposeRequested = true;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _ = DisposeCoreAsync(completion);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            TrackSession[] sessions;
            try
            {
                sessions = _sessions.Values.ToArray();
            }
            finally { _startGate.Release(); }
            foreach (var session in sessions)
            {
                try { await StopTrackAsync(session.Id).ConfigureAwait(false); } catch { }
            }
            completion.TrySetResult(true);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private sealed class TrackSession(CaptureTrackKind kind, string outputPath)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal CaptureTrackKind Kind { get; } = kind;
        internal string OutputPath { get; } = Path.GetFullPath(outputPath);
        internal Recorder Recorder { get; set; } = null!;
        internal TimestampedWasapiCapture? AudioCapture { get; set; }
        internal AudioPacketPositionMapper? AudioPositionMapper { get; set; }
        internal int? AudioSampleRate { get; set; }
        internal WaveFileWriter? WaveFileWriter { get; set; }
        internal object AudioWriteGate { get; } = new();
        internal Queue<AudioPacket> AudioPackets { get; } = new();
        internal long AudioStartTicks { get; set; }
        internal long AudioFramesWritten { get; set; }
        internal bool AudioWaveSizeLimitReported { get; set; }
        internal bool AudioSilenceTruncationReported { get; set; }
        internal CancellationTokenSource? AudioPumpCancellation { get; set; }
        internal Task? AudioPumpTask { get; set; }
        internal TaskCompletionSource<bool> AudioStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception> FailureSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool AudioRecordingStopped;
        internal Exception? AudioFailure { get; set; }
        internal Exception? FailureException { get; set; }
        internal volatile bool AudioPaused;
        internal CaptureTrackHandle? Handle { get; set; }
        internal CaptureTrackState State { get; set; }
        internal CaptureTrackState ExpectedStatus { get; set; }
        internal TaskCompletionSource<long> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CaptureTrackState>? StatusChanged { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal List<CapturePauseInterval> PausedIntervals { get; } = [];
        internal List<(long StartFrame, long EndFrame)> AudioPauseFrameIntervals { get; } = [];
        internal List<CaptureAudioClockSegment> AudioClockSegments { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal List<string> Diagnostics { get; } = [];
        internal long PauseStartTicks { get; set; }
        internal long? PauseStartSourceFrame { get; set; }
        internal long AudioSegmentStartTicks { get; set; }
        internal long AudioSegmentStartOutputFrame { get; set; }
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal int FrameRate { get; set; }
        internal EncoderMode EncoderMode { get; set; }
        internal double MaxAudioPacketMilliseconds { get; set; }
        internal long AudioPacketCount { get; set; }
        internal long TimestampFallbackCount { get; set; }
        internal long TimestampErrorCount { get; set; }
        internal long DataDiscontinuityCount { get; set; }
        internal long DevicePositionMismatchCount { get; set; }
        internal long DevicePositionResetCount { get; set; }
        internal long? FirstDevicePosition { get; set; }
        internal long? ExpectedDevicePosition { get; set; }
        internal double FirstPacketOffsetMilliseconds { get; set; } = double.NaN;
        internal long ArrivalLatencySampleCount { get; set; }
        internal List<double> ArrivalLatencySamples { get; } = [];
        internal AudioPacketDiagnostics? AudioDiagnostics { get; set; }
    }
    private sealed record AudioPacket(long StartFrame, byte[] Data, AudioPacketTiming Timing, long ArrivalStopwatchTicks, bool EndpointSilent);
}
