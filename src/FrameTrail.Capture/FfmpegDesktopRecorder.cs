using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FrameTrail.Capture;

/// <summary>Desktop Duplication capture through the installed FFmpeg ddagrab filter.</summary>
internal sealed class FfmpegDesktopRecorder : IAsyncDisposable
{
    private readonly string _outputPath;
    private readonly int _outputIndex;
    private readonly PixelRect _displayBounds;
    private readonly PixelRect _captureBounds;
    private readonly VideoOptions _video;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly Action<long> _firstProgress;
    private readonly Action<long> _captureStarted;
    private readonly string _ffmpeg;
    private readonly string _workingDirectory;
    private readonly string _manifestPath;
    private readonly List<string> _segments = [];
    private readonly object _diagnosticsGate = new();
    private readonly StringBuilder _stderrTail = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _activeSegmentPath;
    private TaskCompletionSource<bool>? _firstFrame;
    private string? _activePartialSegment;
    private int _segmentNumber;
    private bool _disposed;
    private bool _firstFrameProduced;
    private bool _unexpectedExit;
    private long _progressEstimatedFirstFrameTicks;
    private long _latestSegmentFirstFrameStopwatchTicks;
    private long _latestSegmentEndStopwatchTicks;
    private long _activeSegmentFrameCount;

    private const int MaximumStderrCharacters = 64 * 1024;

    internal EncoderMode EncoderMode { get; private set; } = EncoderMode.Unknown;
    internal string EncoderName { get; private set; } = "FFmpeg";
    internal IReadOnlyList<string> EncoderFailures => _encoderFailures;
    private readonly List<string> _encoderFailures = [];
    internal event Action<Exception>? UnexpectedExit;
    internal long? CurrentCapturePrivateBytes
    {
        get
        {
            try
            {
                var process = _process;
                if (process is null || process.HasExited) return null;
                process.Refresh();
                return process.PrivateMemorySize64;
            }
            catch { return null; }
        }
    }
    internal int? CurrentCaptureProcessId
    {
        get { try { return _process is { HasExited: false } process ? process.Id : null; } catch { return null; } }
    }
    internal string RecoveryManifestPath => _manifestPath;
    internal long? ProgressEstimatedFirstFrameStopwatchTicks => Interlocked.Read(ref _progressEstimatedFirstFrameTicks) is var ticks && ticks != 0 ? ticks : null;
    internal long? LatestSegmentFirstFrameStopwatchTicks => Interlocked.Read(ref _latestSegmentFirstFrameStopwatchTicks) is var ticks && ticks != 0 ? ticks : null;
    internal long? LatestSegmentEndStopwatchTicks => Interlocked.Read(ref _latestSegmentEndStopwatchTicks) is var ticks && ticks != 0 ? ticks : null;

    internal FfmpegDesktopRecorder(string outputPath, int outputIndex, PixelRect displayBounds,
        PixelRect? region, VideoOptions video, Action<long> firstProgress, Action<long> captureStarted)
    {
        _outputPath = outputPath;
        _outputIndex = outputIndex;
        _displayBounds = displayBounds;
        _captureBounds = region ?? displayBounds;
        _video = video;
        _outputWidth = Even(video.Width ?? (video.Height is int h ? (int)Math.Round((double)h * _captureBounds.Width / _captureBounds.Height) : _captureBounds.Width));
        _outputHeight = Even(video.Height ?? (video.Width is int w ? (int)Math.Round((double)w * _captureBounds.Height / _captureBounds.Width) : _captureBounds.Height));
        _firstProgress = firstProgress;
        _captureStarted = captureStarted;
        _ffmpeg = LocateFfmpeg();
        _workingDirectory = Path.Combine(outputPath + ".parts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
        _manifestPath = Path.Combine(_workingDirectory, "manifest.json");
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        EncoderName = await SelectEncoderAsync(cancellationToken).ConfigureAwait(false);
        EncoderMode = EncoderName is "h264_nvenc" or "h264_amf" or "h264_qsv" ? EncoderMode.Hardware
            : EncoderName == "libx264" ? EncoderMode.Software : EncoderMode.Unknown;
        _captureStarted(Stopwatch.GetTimestamp());
        await WriteManifestAsync("recording", cancellationToken).ConfigureAwait(false);
        await StartSegmentAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await FinishSegmentAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartSegmentAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null) await FinishSegmentAsync(cancellationToken).ConfigureAwait(false);
            if (_segments.Count == 0) throw new InvalidOperationException("FFmpeg produced no completed video segment. " + GetStderrTail() + $" Recovery manifest: {_manifestPath}");
            if (_segments.Count == 1 && !_unexpectedExit)
            {
                File.Move(_segments[0], _outputPath, true);
            }
            else
            {
                var listPath = Path.Combine(_workingDirectory, "concat.txt");
                var list = string.Join(Environment.NewLine, _segments.Select(path => "file '" + path.Replace("'", "'\\''") + "'"));
                await File.WriteAllTextAsync(listPath, list, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "warning", "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", "-y", _outputPath }) psi.ArgumentList.Add(arg);
                Process? concat = null;
                try
                {
                    concat = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg concat process.");
                    var stderr = ReadBoundedTextAsync(concat.StandardError, MaximumStderrCharacters, cancellationToken);
                    await concat.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    var error = await stderr.ConfigureAwait(false);
                    if (concat.ExitCode != 0) throw new InvalidOperationException($"FFmpeg could not join the paused recording segments (exit {concat.ExitCode}): {error}");
                }
                catch
                {
                    if (concat is not null) await TerminateAsync(concat).ConfigureAwait(false);
                    throw;
                }
                finally { concat?.Dispose(); }
            }
            if (_unexpectedExit)
            {
                await WriteManifestAsync("recovered-readable-segments", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteManifestAsync("completed", cancellationToken).ConfigureAwait(false);
                Directory.Delete(_workingDirectory, true);
            }
            return _outputPath;
        }
        finally { _gate.Release(); }
    }

    private async Task StartSegmentAsync(CancellationToken cancellationToken)
    {
        if (_process is not null) throw new InvalidOperationException("A desktop capture segment is already running.");
        var segment = Path.Combine(_workingDirectory, $"segment-{_segmentNumber++:D4}.mp4");
        var offsetX = _captureBounds.X - _displayBounds.X;
        var offsetY = _captureBounds.Y - _displayBounds.Y;
        // `time(0)` is evaluated at the first frame as it enters setpts. The zero multiplier preserves native PTS;
        // print emits a wall-clock microsecond anchor that is paired to Stopwatch at process launch.
        var filter = FormattableString.Invariant($"ddagrab=output_idx={_outputIndex}:framerate={_video.FrameRate}:video_size={_captureBounds.Width}x{_captureBounds.Height}:offset_x={offsetX}:offset_y={offsetY}:draw_mouse=0:dup_frames=1,setpts=PTS+0*print(time(0)*1000000*eq(N\\,0))");
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-stats_period", "0.05", "-progress", "pipe:2", "-f", "lavfi", "-i", filter,
            "-an", "-vf", FormattableString.Invariant($"hwdownload,format=bgra,scale={_outputWidth}:{_outputHeight}:flags=bilinear:in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p"),
            "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "iec61966-2-1", "-color_primaries", "bt709",
            "-r", _video.FrameRate.ToString(CultureInfo.InvariantCulture), "-fps_mode", "cfr" };
        args.AddRange(CaptureVideoQuality.EncoderArguments(EncoderName, _video, _outputWidth, _outputHeight));
        args.AddRange(["-movflags", "+faststart", "-y", segment]);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long? progressFrame = null;
        long? progressTimeUs = null;
        var pairedStopwatchBefore = Stopwatch.GetTimestamp();
        var pairedUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var pairedStopwatchAfter = Stopwatch.GetTimestamp();
        var pairedStopwatchTicks = pairedStopwatchBefore + (pairedStopwatchAfter - pairedStopwatchBefore) / 2;
        var firstFrameAnchorCaptured = false;
        Interlocked.Exchange(ref _latestSegmentFirstFrameStopwatchTicks, 0);
        Interlocked.Exchange(ref _activeSegmentFrameCount, 0);
        lock (_diagnosticsGate) _stderrTail.Clear();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            AppendStderr(e.Data);
            if (!firstFrameAnchorCaptured && TryParsePrintedUnixMicroseconds(e.Data, out var unixMicroseconds))
            {
                var unixEpochTicks = DateTimeOffset.UnixEpoch.UtcDateTime.Ticks;
                var pairUnixSeconds = (pairedUtcTicks - unixEpochTicks) / (double)TimeSpan.TicksPerSecond;
                var frameUnixSeconds = unixMicroseconds / 1_000_000d;
                var frameStopwatchTicks = pairedStopwatchTicks + (long)((frameUnixSeconds - pairUnixSeconds) * Stopwatch.Frequency);
                firstFrameAnchorCaptured = true;
                Interlocked.Exchange(ref _latestSegmentFirstFrameStopwatchTicks, frameStopwatchTicks);
                _firstProgress(frameStopwatchTicks);
                _firstFrameProduced = true;
                _firstFrame?.TrySetResult(true);
            }
            if (e.Data.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(e.Data.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                if (frame > 0) { progressFrame = frame; Interlocked.Exchange(ref _activeSegmentFrameCount, frame); }
            }
            else if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(e.Data.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeUs)) progressTimeUs = timeUs;
            else if (e.Data.StartsWith("progress=", StringComparison.Ordinal) && progressFrame is > 0 && progressTimeUs is long outputUs)
            {
                var receivedTicks = Stopwatch.GetTimestamp();
                var mediaZeroEstimate = receivedTicks - (long)(outputUs * (double)Stopwatch.Frequency / 1_000_000d);
                Interlocked.CompareExchange(ref _progressEstimatedFirstFrameTicks, mediaZeroEstimate, 0);
                if (!firstFrameAnchorCaptured)
                {
                    _firstProgress(mediaZeroEstimate);
                    Interlocked.Exchange(ref _latestSegmentFirstFrameStopwatchTicks, mediaZeroEstimate);
                    firstFrameAnchorCaptured = true;
                    _firstFrame?.TrySetResult(true);
                }
                _firstFrameProduced = true;
                progressFrame = null;
                progressTimeUs = null;
            }
        };
        process.Exited += (_, _) => _ = HandleUnexpectedExitAsync(process);
        if (!process.Start()) throw new InvalidOperationException("Could not start FFmpeg desktop capture.");
        _process = process;
        _activeSegmentPath = segment;
        process.BeginErrorReadLine();
        try { await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
        catch
        {
            var failedProcess = _process;
            _process = null;
            if (failedProcess is not null) await TerminateAsync(failedProcess).ConfigureAwait(false);
            throw new InvalidOperationException("FFmpeg ddagrab did not produce its first progress frame within 12 seconds. " + GetStderrTail());
        }
    }

    private static bool TryParsePrintedUnixMicroseconds(string line, out double unixMicroseconds)
    {
        unixMicroseconds = 0;
        var marker = line.LastIndexOf("] ", StringComparison.Ordinal);
        if (marker < 0) return false;
        var value = line.AsSpan(marker + 2).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out unixMicroseconds) &&
            unixMicroseconds is > 1_000_000_000_000_000d and < 4_000_000_000_000_000d;
    }

    private async Task FinishSegmentAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null) return;
        _process = null;
        var segment = _activeSegmentPath;
        _activeSegmentPath = null;
        try
        {
            await process.StandardInput.WriteLineAsync("q".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg capture segment failed with exit code {process.ExitCode}. {GetStderrTail()}");
            if (segment is null || !File.Exists(segment) || new FileInfo(segment).Length == 0)
                throw new InvalidOperationException("FFmpeg exited successfully but its MP4 segment is missing or empty.");
            var firstFrameTicks = Interlocked.Read(ref _latestSegmentFirstFrameStopwatchTicks);
            var frameCount = Interlocked.Read(ref _activeSegmentFrameCount);
            if (firstFrameTicks != 0 && frameCount > 0)
                Interlocked.Exchange(ref _latestSegmentEndStopwatchTicks,
                    firstFrameTicks + (long)Math.Round(frameCount * (double)Stopwatch.Frequency / _video.FrameRate));
            _segments.Add(segment);
            await WriteManifestAsync("paused-or-segment-complete", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw new InvalidOperationException("FFmpeg could not finalize a capture segment; original segments are retained in " + _workingDirectory, ex);
        }
        finally { process.Dispose(); }
    }

    private static async Task TerminateAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        process.Dispose();
    }

    private async Task HandleUnexpectedExitAsync(Process process)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            var exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            var activeSegment = _activeSegmentPath;
            _activeSegmentPath = null;
            _activePartialSegment = activeSegment is not null && File.Exists(activeSegment) && new FileInfo(activeSegment).Length > 0
                ? activeSegment : null;
            _unexpectedExit = true;
            var detail = GetStderrTail();
            failure = new InvalidOperationException($"FFmpeg capture child exited unexpectedly with code {exitCode}. {detail} Recovery manifest: {_manifestPath}");
            await WriteManifestAsync("unexpected-exit", CancellationToken.None).ConfigureAwait(false);
            try { process.Dispose(); } catch { }
        }
        catch (Exception ex) { failure = ex; }
        finally { _gate.Release(); }

        if (failure is not null && _firstFrameProduced)
        {
            try { UnexpectedExit?.Invoke(failure); } catch { }
        }
        else if (failure is not null)
        {
            _firstFrame?.TrySetException(failure);
        }
    }

    private void AppendStderr(string line)
    {
        lock (_diagnosticsGate)
        {
            _stderrTail.AppendLine(line);
            if (_stderrTail.Length > MaximumStderrCharacters)
                _stderrTail.Remove(0, _stderrTail.Length - MaximumStderrCharacters);
        }
    }

    private void SetStderrTail(string text)
    {
        lock (_diagnosticsGate)
        {
            _stderrTail.Clear();
            if (text.Length > MaximumStderrCharacters) text = text[^MaximumStderrCharacters..];
            _stderrTail.Append(text);
        }
    }

    private string GetStderrTail()
    {
        lock (_diagnosticsGate) return _stderrTail.ToString();
    }

    private async Task WriteManifestAsync(string status, CancellationToken cancellationToken)
    {
        var manifest = new CaptureRecoveryManifest(1, _outputPath, status, DateTimeOffset.UtcNow,
            _segments.Select(path => Path.GetRelativePath(_workingDirectory, path)).ToArray(),
            _activePartialSegment is null ? null : Path.GetRelativePath(_workingDirectory, _activePartialSegment),
            GetStderrTail());
        var temporary = _manifestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _manifestPath, true);
    }

    internal static IReadOnlyList<RecoverableRecordingInfo> FindRecoverableRecordings(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) return [];
        var found = new List<RecoverableRecordingInfo>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var manifestPath = Path.Combine(current, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) == 0)
                        {
                            var manifest = JsonSerializer.Deserialize<CaptureRecoveryManifest>(File.ReadAllText(manifestPath));
                            if (manifest is { Version: 1, Segments.Length: > 0 } && !string.IsNullOrWhiteSpace(manifest.OriginalOutputPath)
                                && Path.IsPathFullyQualified(manifest.OriginalOutputPath))
                            {
                                var segments = manifest.Segments.Where(relative => !string.IsNullOrWhiteSpace(relative)
                                        && string.Equals(Path.GetFileName(relative), relative, StringComparison.OrdinalIgnoreCase)
                                        && relative.StartsWith("segment-", StringComparison.OrdinalIgnoreCase)
                                        && relative.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                                    .Select(relative => Path.GetFullPath(Path.Combine(current, relative)))
                                    .Where(path => IsChildPath(current, path) && IsDirectChildPath(current, path) && File.Exists(path)
                                        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length > 0).ToArray();
                                if (segments.Length > 0) found.Add(new RecoverableRecordingInfo(manifestPath, manifest.OriginalOutputPath, segments));
                            }
                        }
                    }
                    catch { }
                }
                foreach (var subdirectory in Directory.EnumerateDirectories(current))
                {
                    try { if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) == 0) pending.Push(subdirectory); }
                    catch { }
                }
            }
            catch { }
        }
        return found;
    }

    internal static async Task<string> RecoverReadableRecordingAsync(RecoverableRecordingInfo recovery, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var validated = FindRecoverableRecordings(Path.GetDirectoryName(Path.GetFullPath(recovery.ManifestPath))!)
            .FirstOrDefault(candidate => string.Equals(candidate.ManifestPath, Path.GetFullPath(recovery.ManifestPath), StringComparison.OrdinalIgnoreCase));
        if (validated is null || validated.Segments.Count == 0) throw new ArgumentException("Recovery manifest has no finalized readable segments.", nameof(recovery));
        var destination = Path.GetFullPath(outputPath);
        if (!destination.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Recovery output must be an MP4 path.", nameof(outputPath));
        if (validated.Segments.Any(path => string.Equals(Path.GetFullPath(path), destination, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Recovery output cannot replace a source segment.", nameof(outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var workDirectory = Path.GetDirectoryName(Path.GetFullPath(validated.ManifestPath))!;
        var listPath = Path.Combine(workDirectory, $"recovery-{Guid.NewGuid():N}.txt");
        var list = string.Join(Environment.NewLine, validated.Segments.Select(path => "file '" + Path.GetFullPath(path).Replace("'", "'\\''") + "'"));
        await File.WriteAllTextAsync(listPath, list, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(LocateFfmpeg()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "warning", "-f", "concat", "-safe", "0", "-i", listPath,
                "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", "-y", destination }) psi.ArgumentList.Add(arg);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg recovery concat process.");
            var stderr = ReadBoundedTextAsync(process.StandardError, MaximumStderrCharacters, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            var detail = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg could not recover readable segments (exit {process.ExitCode}): {detail}");
            return destination;
        }
        catch
        {
            if (process is not null) await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            process?.Dispose();
            try { File.Delete(listPath); } catch { }
        }
    }

    private static bool IsChildPath(string directory, string path)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildPath(string directory, string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBoundedTextAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var tail = new StringBuilder(maximumCharacters);
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > maximumCharacters) tail.Remove(0, tail.Length - maximumCharacters);
        }
        return tail.ToString();
    }

    private sealed record CaptureRecoveryManifest(int Version, string OriginalOutputPath, string Status, DateTimeOffset UpdatedUtc,
        string[] Segments, string? ActivePartialSegment, string Diagnostics);

    private async Task<string> SelectEncoderAsync(CancellationToken cancellationToken)
    {
        var candidates = _video.EncoderPreference switch
        {
            EncoderPreference.Software => new[] { "libx264" },
            EncoderPreference.Hardware => new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264" },
            _ => new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264" }
        };
        // Probing an encoder whose GPU vendor runtime is absent always fails, and FFmpeg then writes
        // a large "Cannot load nvcuda.dll"-style block into the diagnostic tail. That tail is shown to
        // the user as a recording warning even though the recording succeeded on the next encoder, so
        // a machine without NVIDIA hardware reported a scary error on every recording. Skip the
        // vendor outright when its driver runtime is not installed; the failure is still reported when
        // the runtime exists but the encoder genuinely does not work.
        var present = new List<string>(candidates.Length);
        foreach (var encoder in candidates)
            if (AvailableEncoders.MayBeUsable(encoder)) present.Add(encoder);
            else _encoderFailures.Add($"{encoder}: skipped because {AvailableEncoders.MissingRuntimeDescription(encoder)} is not installed on this computer.");
        foreach (var encoder in present)
        {
            if (await TestEncoderAsync(encoder, cancellationToken).ConfigureAwait(false)) return encoder;
            _encoderFailures.Add(GetStderrTail() is { Length: > 0 } detail ? $"{encoder}: {detail}" : $"{encoder}: initialization failed");
        }
        throw new InvalidOperationException("No usable FFmpeg H.264 encoder was found (tested NVENC, AMF, QSV, Media Foundation and libx264).");
    }

    /// <summary>Vendor runtime presence checks that keep absent-GPU probes out of the diagnostics.</summary>
    private static class AvailableEncoders
    {
        /// <summary>True unless the encoder's GPU runtime is known to be absent.</summary>
        internal static bool MayBeUsable(string encoder) => MissingRuntimeDescription(encoder) is null;

        /// <summary>Returns a human-readable reason when the encoder cannot work here, otherwise null.</summary>
        internal static string? MissingRuntimeDescription(string encoder)
        {
            // Any vendor DLL from this list proves the vendor stack is installed.
            (string[] Probe, string Driver) requirement;
            switch (encoder)
            {
                case "h264_nvenc": requirement = (["nvapi64.dll", "nvcuda.dll", "nvml.dll"], "the NVIDIA driver"); break;
                case "h264_amf": requirement = (["amdhip64.dll"], "the AMD driver"); break;
                case "h264_qsv": requirement = (["mfx_dispatch.dll", "mfx_hw.dll", "igdrcl64.dll"], "the Intel driver"); break;
                default: return null; // h264_mf rides on Media Foundation and libx264 is pure software.
            }

            foreach (var file in requirement.Probe)
            {
                // Only a definite "missing" counts. File.Exists never throws on an unreadable
                // directory, whereas a recursive search does throw on protected subfolders such as
                // C:\Windows\System32\Com\dmp, and treating that failure as "driver absent" would
                // silently disable hardware encoding on every machine.
                if (FileExistsInSystemDirectories(file)) return null;
            }
            return requirement.Driver;
        }

        private static bool FileExistsInSystemDirectories(string fileName)
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var directory in new[] { system, windows })
            {
                if (directory.Length == 0) continue;
                if (File.Exists(Path.Combine(directory, fileName))) return true;
                // Vendor runtimes may also sit next to the driver store; only probe the shallow,
                // readable subfolders rather than recursing through protected system directories.
                foreach (var child in SafeDirectories(directory))
                    if (File.Exists(Path.Combine(child, fileName))) return true;
            }
            return false;
        }

        private static string[] SafeDirectories(string parent)
        {
            try { return Directory.GetDirectories(parent, "*", SearchOption.TopDirectoryOnly); }
            catch { return []; }
        }
    }

    private async Task<bool> TestEncoderAsync(string encoder, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=1:d=1" };
        args.AddRange(CaptureVideoQuality.EncoderArguments(encoder, _video, _outputWidth, _outputHeight));
        args.AddRange(["-frames:v", "1", "-f", "null", "-"]);
        var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return false;
            var error = ReadBoundedTextAsync(process.StandardError, MaximumStderrCharacters, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            var detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) { SetStderrTail($"{encoder}: {detail.Trim()}"); return false; }
            return true;
        }
        catch (OperationCanceledException) { if (process is not null) await TerminateAsync(process).ConfigureAwait(false); throw; }
        catch (TimeoutException) { if (process is not null) await TerminateAsync(process).ConfigureAwait(false); SetStderrTail($"{encoder}: probe timed out after 8 seconds."); return false; }
        catch (Exception ex) { if (process is not null) await TerminateAsync(process).ConfigureAwait(false); SetStderrTail($"{encoder}: {ex.Message}"); return false; }
    }

    private static int Even(int value) => Math.Max(2, value - value % 2);

    private static string LocateFfmpeg()
    {
        var custom = Environment.GetEnvironmentVariable("FRAMETRAIL_FFMPEG_DIR");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(Path.Combine(custom, "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, "ffmpeg.exe")));
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var package in Directory.GetDirectories(winget, "Gyan.FFmpeg_*"))
                candidates.AddRange(Directory.GetDirectories(package, "ffmpeg-*-full_build").Select(p => Path.Combine(p, "bin", "ffmpeg.exe")));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("缺少 ffmpeg.exe。请将 FFmpeg 放到应用 tools/ffmpeg 目录、设置 FRAMETRAIL_FFMPEG_DIR，或加入 PATH。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process is not null) await TerminateAsync(_process).ConfigureAwait(false);
        _process = null;
        _gate.Dispose();
    }
}
