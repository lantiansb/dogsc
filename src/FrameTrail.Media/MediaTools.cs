using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FrameTrail.Media;

public sealed record MediaInfo(int Width, int Height, double Duration, double FrameRate, bool HasAudio);
public static class MediaTools
{
    // SourceRate values inside this band are treated as device-clock drift and
    // corrected by resampling; larger rates describe media/timeline mapping and
    // retain pitch through atempo. PlaybackRate is always user-controlled tempo.
    private const double SourceClockCorrectionLimitPpm = 1_000;
    private const int SourceClockIntermediateRate = 192_000;

    public static string Ffmpeg => Locate("ffmpeg.exe");
    public static string Ffprobe => Locate("ffprobe.exe");
    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Locate(string name)
    {
        var custom = Environment.GetEnvironmentVariable("FRAMETRAIL_FFMPEG_DIR");
        var roots = new List<string> { custom ?? "", Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"), AppContext.BaseDirectory };
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var d in Directory.GetDirectories(winget, "Gyan.FFmpeg_*")) roots.AddRange(Directory.GetDirectories(d).Select(x => Path.Combine(x, "bin")));
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x))) { var candidate = Path.Combine(root, name); if (File.Exists(candidate)) return candidate; }
        throw new FileNotFoundException($"缺少 {name}。请将 FFmpeg 放到应用 tools/ffmpeg 目录，或设置 FRAMETRAIL_FFMPEG_DIR。");
    }
    public static Process Start(string executable, IEnumerable<string> arguments, bool input = false, bool output = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardOutput = output, RedirectStandardError = true };
        // ffmpeg/ffprobe 始终输出 UTF-8。不显式指定时 .NET 按系统 ANSI 代码页解码，
        // 中文路径会被误解码：GBK 会把中文最后一个字节和紧随的 '\' 并成一个双字节字符，
        // 吃掉 JSON 转义里的反斜杠，使 ffprobe 的合法 JSON 变成非法转义（如 \2）而解析失败。
        if (output) info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new IOException($"无法启动 {executable}");
    }
    public static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    public static async Task StopAsync(Process process)
    {
        Kill(process);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch(InvalidOperationException) { }
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        using var p = Start(executable, arguments, output: true);
        using var registration = cancellation.Register(() => Kill(p));
        var error = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(cancellation); cancellation.ThrowIfCancellationRequested();
            var stderr = await error; var stdout = await output;
            if (p.ExitCode != 0) throw new IOException($"媒体处理失败 ({p.ExitCode})：{Tail(stderr)}");
            return stdout;
        }
        finally { await StopAsync(p); }
    }
    public static string Tail(string s) => s.Length <= 6000 ? s : s[^6000..];
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        var json = await RunAsync(Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellation);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "video");
        var duration = doc.RootElement.GetProperty("format").TryGetProperty("duration", out var dur) ? double.Parse(dur.GetString()!, CultureInfo.InvariantCulture) : 0;
        var rate = video.ValueKind != JsonValueKind.Undefined && video.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString()!.Split('/') : ["0", "1"];
        var denominator = double.Parse(rate[1], CultureInfo.InvariantCulture);
        return new(video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("width").GetInt32(), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("height").GetInt32(), duration, denominator == 0 ? 0 : double.Parse(rate[0], CultureInfo.InvariantCulture) / denominator, streams.Any(x => x.GetProperty("codec_type").GetString() == "audio"));
    }
    public static string VideoFilter(ProjectTimeline timeline, int width, int height, int fps)
    {
        timeline.Validate();
        var filters = new List<string>(); var n = timeline.Segments.Count;
        if (n > 1) filters.Add($"[0:v]split={n}" + string.Concat(Enumerable.Range(0, n).Select(i => $"[s{i}]")));
        for (var i = 0; i < n; i++)
        {
            var s = timeline.Segments[i];
            filters.Add($"[{(n == 1 ? "0:v" : $"s{i}")}]trim=start={Number(s.SourceStart)}:end={Number(s.SourceEnd)},setpts=(PTS-STARTPTS)/{Number(s.PlaybackRate)}[v{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0,n).Select(i => $"[v{i}]")) + (n > 1 ? $"concat=n={n}:v=1:a=0," : "") + $"fps={fps},scale={width}:{height}:flags=bilinear,format=bgra[out]");
        return string.Join(';', filters);
    }
    public static string AudioFilter(ProjectTimeline timeline, double volume = 1, bool muted = false)
    {
        timeline.Validate(); if (!double.IsFinite(volume) || volume is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(volume));
        var filters = new List<string>(); var n = timeline.Segments.Count;
        if (n > 1) filters.Add($"[0:a]asplit={n}" + string.Concat(Enumerable.Range(0,n).Select(i => $"[s{i}]")));
        for (var i=0;i<n;i++)
        {
            var s = timeline.Segments[i]; var gain = s.AudioOverrides?.SystemVolume ?? volume; var isMuted = s.AudioOverrides?.SystemMuted ?? muted; var rate = s.PlaybackRate; var tempo = new List<string>();
            while (rate > 2) { tempo.Add("atempo=2"); rate /= 2; }
            while (rate < .5) { tempo.Add("atempo=0.5"); rate *= 2; }
            if (Math.Abs(rate - 1) > 1e-12) tempo.Add("atempo=" + Number(rate));
            var tempoFilter = tempo.Count == 0 ? "" : string.Join(',', tempo) + ",";
            filters.Add($"[{(n == 1 ? "0:a" : $"s{i}")}]atrim=start={Number(s.SourceStart)}:end={Number(s.SourceEnd)},asetpts=PTS-STARTPTS,{tempoFilter}volume={Number(isMuted ? 0 : gain)},apad,atrim=duration={Number(s.OutputDuration)}[a{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0,n).Select(i => $"[a{i}]")) + (n > 1 ? $"concat=n={n}:v=0:a=1" : "anull") + "[out]");
        return string.Join(';', filters);
    }
    public static bool HasAudibleAudioTracks(ProjectTimeline timeline, IReadOnlyList<AudioTrack> tracks, double systemVolume = 1, bool systemMuted = false)
    {
        timeline.Validate();
        return tracks.Any(track => HasAudibleAudioTrack(timeline, track, systemVolume, systemMuted));
    }

    public static bool HasAudibleMainAudio(ProjectTimeline timeline, double volume = 1, bool muted = false)
    {
        timeline.Validate();
        return timeline.Segments.Any(segment => !(segment.AudioOverrides?.SystemMuted ?? muted)
            && (segment.AudioOverrides?.SystemVolume ?? volume) > 0);
    }

    public static bool HasAudibleAudioTrack(ProjectTimeline timeline, AudioTrack track, double systemVolume = 1, bool systemMuted = false)
    {
        timeline.Validate();
        var mappings = track.SyncSegments is { Count: > 0 } syncSegments
            ? syncSegments
            : [new CameraSyncSegment(track.SourceStart, track.OffsetSeconds, track.Duration, track.SourceRate)];
        return timeline.Segments.Any(segment => EffectiveTrackGain(segment, track, systemVolume, systemMuted) > 0
            && mappings.Any(mapping => mapping.SourceRate > 0
                && Math.Min(segment.SourceEnd, mapping.TimelineStart + mapping.Duration) > Math.Max(segment.SourceStart, mapping.TimelineStart)));
    }

    public static string AudioTrackFilter(ProjectTimeline timeline, IReadOnlyList<AudioTrack> tracks, double systemVolume = 1, bool systemMuted = false)
    {
        timeline.Validate();
        var filters = new List<string>(); var outputStarts = new double[timeline.Segments.Count]; var elapsed = 0d;
        for (var i = 0; i < timeline.Segments.Count; i++) { outputStarts[i] = elapsed; elapsed += timeline.Segments[i].OutputDuration; }
        var outputLabels = new List<string>(); var labelNumber = 0;
        for (var trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            var track = tracks[trackIndex];
            var mappings = track.SyncSegments is { Count: > 0 } syncSegments
                ? syncSegments
                : [new CameraSyncSegment(track.SourceStart, track.OffsetSeconds, track.Duration, track.SourceRate)];
            var samples = new List<(double AssetStart, double AssetEnd, double OutputStart, double PlaybackRate, double SourceRate, double Gain)>();
            for (var segmentIndex = 0; segmentIndex < timeline.Segments.Count; segmentIndex++)
            {
                var segment = timeline.Segments[segmentIndex];
                var gain = EffectiveTrackGain(segment, track, systemVolume, systemMuted);
                if (gain <= 0) continue;
                foreach (var mapping in mappings)
                {
                    var sourceStart = Math.Max(segment.SourceStart, mapping.TimelineStart);
                    var sourceEnd = Math.Min(segment.SourceEnd, mapping.TimelineStart + mapping.Duration);
                    if (sourceEnd <= sourceStart || mapping.SourceRate <= 0) continue;
                    var assetStart = mapping.SourceStart + (sourceStart - mapping.TimelineStart) * mapping.SourceRate;
                    var assetEnd = mapping.SourceStart + (sourceEnd - mapping.TimelineStart) * mapping.SourceRate;
                    var outputStart = outputStarts[segmentIndex] + (sourceStart - segment.SourceStart) / segment.PlaybackRate;
                    samples.Add((assetStart, assetEnd, outputStart, segment.PlaybackRate, mapping.SourceRate, gain));
                }
            }
            if (samples.Count == 0) continue;
            var input = trackIndex;
            var prefixes = new string[samples.Count];
            if (samples.Count > 1)
            {
                filters.Add($"[{input}:a]asplit={samples.Count}" + string.Concat(Enumerable.Range(0, samples.Count).Select(i => $"[t{trackIndex}s{i}]")));
                for (var i = 0; i < prefixes.Length; i++) prefixes[i] = $"t{trackIndex}s{i}";
            }
            for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
            {
                var sample = samples[sampleIndex];
                var delayMs = Math.Max(0, (int)Math.Round(sample.OutputStart * 1000));
                var clockRate = sample.SourceRate;
                var playbackRate = sample.PlaybackRate;
                var clockCorrection = "";
                var isDeviceClockDrift = Math.Abs(clockRate - 1) * 1_000_000 <= SourceClockCorrectionLimitPpm + 1e-6;
                var rate = playbackRate;
                if (isDeviceClockDrift)
                {
                    var correctedRate = checked((int)Math.Round(SourceClockIntermediateRate * clockRate, MidpointRounding.AwayFromZero));
                    if (correctedRate != SourceClockIntermediateRate)
                        clockCorrection = $"aresample={SourceClockIntermediateRate},asetrate={correctedRate},aresample=48000,";
                }
                else
                {
                    // Non-clock mapping rates can encode meaningful content speed;
                    // preserve its pitch instead of shifting it with asetrate.
                    rate *= clockRate;
                }
                var tempo = new List<string>();
                while (rate > 2) { tempo.Add("atempo=2"); rate /= 2; }
                while (rate < .5) { tempo.Add("atempo=0.5"); rate *= 2; }
                if (Math.Abs(rate - 1) > 1e-12) tempo.Add("atempo=" + Number(rate));
                var tempoFilter = tempo.Count == 0 ? "" : string.Join(',', tempo) + ",";
                var source = samples.Count == 1 ? $"{input}:a" : prefixes[sampleIndex];
                var label = $"track{labelNumber++}";
                filters.Add($"[{source}]atrim=start={Number(sample.AssetStart)}:end={Number(sample.AssetEnd)},asetpts=PTS-STARTPTS,{clockCorrection}{tempoFilter}volume={Number(sample.Gain)},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,adelay={delayMs}:all=1[{label}]");
                outputLabels.Add(label);
            }
        }
        if (outputLabels.Count == 0) throw new InvalidOperationException("没有可用的独立音轨片段。");
        filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={Number(elapsed)}[base]");
        var inputs = "[base]" + string.Concat(outputLabels.Select(x => $"[{x}]"));
        filters.Add(inputs + $"amix=inputs={outputLabels.Count + 1}:duration=first:normalize=0,atrim=duration={Number(elapsed)},asetpts=N/SR/TB[out]");
        return string.Join(';', filters);
    }

    private static double EffectiveTrackGain(TimelineSegment segment, AudioTrack track, double systemVolume, bool systemMuted)
    {
        var overrides = segment.AudioOverrides;
        return track.Source switch
        {
            AudioTrackSource.System => (overrides?.SystemMuted ?? (systemMuted || track.Muted)) ? 0 : (overrides?.SystemVolume ?? systemVolume),
            AudioTrackSource.Microphone => (overrides?.MicrophoneMuted ?? track.Muted) ? 0 : (overrides?.MicrophoneVolume ?? track.Volume),
            _ => track.Muted ? 0 : track.Volume
        };
    }
    public static async Task<byte[]> DecodeFrameAsync(string path, double sourceTime, int width, int height, CancellationToken cancellation)
    {
        using var p = Start(Ffmpeg, ["-v", "error", "-ss", Number(sourceTime), "-i", path, "-an", "-frames:v", "1", "-vf", $"scale={width}:{height}", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], output: true);
        using var reg = cancellation.Register(() => Kill(p)); var errors = p.StandardError.ReadToEndAsync();
        var buffer = new byte[checked(width * height * 4)];
        try { await p.StandardOutput.BaseStream.ReadExactlyAsync(buffer, cancellation); await p.WaitForExitAsync(cancellation); if (p.ExitCode != 0) throw new IOException(await errors); }
        finally { await StopAsync(p); }
        return buffer;
    }
}
