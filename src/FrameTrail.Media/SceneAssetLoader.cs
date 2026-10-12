using System.Diagnostics;
using System.Globalization;
using FrameTrail;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameTrail.Media;

/// <summary>Resolves project-relative scene assets into decoded layers shared by preview and export.</summary>
public sealed class SceneAssetLoader : IDisposable
{
    private readonly string projectFile;
    private readonly int outputWidth, outputHeight;
    internal const long DefaultImageCacheBudgetBytes = 256L * 1024 * 1024;
    private readonly ByteBudgetLruCache<AssetCacheKey, (int Width, int Height, byte[] Pixels)> imageCache;
    private readonly Dictionary<string, MediaInfo> mediaInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CameraFrameStream> cameraStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BackgroundVideoFrameStream> backgroundVideoStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public SceneAssetLoader(string projectFile, int outputWidth, int outputHeight)
        : this(projectFile, outputWidth, outputHeight, DefaultImageCacheBudgetBytes)
    {
    }

    internal SceneAssetLoader(string projectFile, int outputWidth, int outputHeight, long imageCacheBudgetBytes)
    {
        if (string.IsNullOrWhiteSpace(projectFile)) throw new ArgumentException("项目路径不能为空。", nameof(projectFile));
        if (outputWidth < 16 || outputHeight < 16) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (imageCacheBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(imageCacheBudgetBytes));
        this.projectFile = Path.GetFullPath(projectFile); this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        imageCache = new ByteBudgetLruCache<AssetCacheKey, (int Width, int Height, byte[] Pixels)>(imageCacheBudgetBytes, AssetCacheKeyComparer.Instance);
    }

    internal long CachedImageBytes => imageCache.CachedBytes;
    internal int CachedImageCount => imageCache.Count;
    internal long ImageCacheHitCount => imageCache.HitCount;
    internal long ImageCacheMissCount => imageCache.MissCount;
    internal long ImageCacheEvictionCount => imageCache.EvictionCount;

    /// <summary>Probes the camera asset for evaluator geometry without mutating the caller's project snapshot.</summary>
    public static async Task<double?> ProbeCameraAspectRatioAsync(FrameTrailProject project, string projectFile, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Camera is not { } camera) return null;
        var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, camera.MediaPath);
        var info = await MediaTools.ProbeAsync(path, cancellation);
        if (info.Width < 2 || info.Height < 2 || info.Duration <= 0)
            throw new InvalidDataException($"摄像头素材没有有效画面：{camera.MediaPath}");
        var aspect = info.Width / (double)info.Height;
        if (!double.IsFinite(aspect) || aspect <= 0) throw new InvalidDataException($"摄像头素材宽高比无效：{camera.MediaPath}");
        return aspect;
    }

    /// <summary>Loads active background, camera and image layers. Calls are serialized for a shared preview loader.</summary>
    public async Task<IReadOnlyList<FrameLayerInput>> LoadAsync(FrameScene scene, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        await gate.WaitAsync(cancellation);
        try
        {
            var result = new List<FrameLayerInput>(2 + (scene.Overlays?.Count ?? 0));
            if (scene.BackgroundStyle is { Kind: BackgroundKind.Image, ImagePath: { Length: > 0 } backgroundPath })
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, backgroundPath);
                var info = await Probe(path, cancellation);
                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("背景图片无法解码。");
                var scale = Math.Min(1, 4096d / Math.Max(info.Width, info.Height));
                var w = Math.Max(1, (int)Math.Round(info.Width * scale)); var h = Math.Max(1, (int)Math.Round(info.Height * scale));
                var (_, _, pixels) = await LoadImage(path, w, h, cancellation);
                var style = scene.BackgroundStyle!;
                result.Add(new(w, h, pixels, NormalizedRect.Full, Opacity: 1, Layer: int.MinValue, BehindScreen: true, Fit: style.ImageFit, Blur: style.Blur));
            }
            if (scene.BackgroundStyle is { Kind: BackgroundKind.Video, VideoPath: { Length: > 0 } videoPath } videoStyle)
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, videoPath);
                var info = await Probe(path, cancellation);
                if (info.Width < 2 || info.Height < 2 || info.Duration <= 0) throw new InvalidDataException("背景视频没有有效的视频帧。");
                var scale = Math.Min(1, 4096d / Math.Max(info.Width, info.Height));
                var width = Math.Max(2, (int)Math.Round(info.Width * scale / 2) * 2);
                var height = Math.Max(2, (int)Math.Round(info.Height * scale / 2) * 2);
                if (!backgroundVideoStreams.TryGetValue(path, out var stream))
                    backgroundVideoStreams.Add(path, stream = new BackgroundVideoFrameStream(path, width, height));
                var requested = Math.Max(0, videoStyle.VideoStartTime + scene.OutputTime * Math.Clamp(videoStyle.Speed, 0, 10));
                var localTime = videoStyle.VideoLoop ? requested % info.Duration : Math.Min(requested, Math.Max(0, info.Duration - 1d / 30));
                var pixels = await stream.ReadAsync(localTime, cancellation);
                result.Add(new(width, height, pixels, NormalizedRect.Full, Opacity: 1, Layer: int.MinValue,
                    BehindScreen: true, Fit: videoStyle.ImageFit, Blur: videoStyle.Blur));
            }
            if (scene.Canvas is { Frame: not ScreenFrameKind.None } frameCanvas && (!string.IsNullOrWhiteSpace(frameCanvas.Title) || !string.IsNullOrWhiteSpace(frameCanvas.Address)))
            {
                var rect = FrameHeaderRect(scene, frameCanvas);
                var tileWidth = 1024; var tileHeight = Math.Clamp((int)Math.Round(tileWidth * rect.Height * outputHeight / Math.Max(rect.Width * outputWidth, 1)), 24, 128);
                var textKey = new AssetCacheKey(AssetCacheKeyKind.Text, null, tileWidth, tileHeight,
                    frameCanvas.Frame, frameCanvas.Title, frameCanvas.Address);
                if (!imageCache.TryGetValue(textKey, out var textTile))
                {
                    textTile = BuildFrameText(frameCanvas, tileWidth, tileHeight, cancellation);
                    imageCache.Set(textKey, textTile, ByteSize(textTile));
                }
                result.Add(new(textTile.Width, textTile.Height, textTile.Pixels, rect, Layer: int.MinValue, FollowScreenTransform: true));
            }
            foreach (var overlay in scene.Overlays ?? [])
            {
                cancellation.ThrowIfCancellationRequested();
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, overlay.MediaPath);
                var info = await Probe(path, cancellation);
                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException($"贴图没有可解码的视频帧：{overlay.MediaPath}");
                var scale = Math.Min(1, 2048d / Math.Max(info.Width, info.Height));
                var width = Math.Max(1, (int)Math.Round(info.Width * scale)); var height = Math.Max(1, (int)Math.Round(info.Height * scale));
                var (_, _, pixels) = await LoadImage(path, width, height, cancellation);
                result.Add(new(width, height, pixels, overlay.Rect, overlay.Rotation, overlay.Opacity, overlay.Layer, overlay.CornerRadius, Blur: overlay.Blur,
                    BorderWidth: overlay.BorderWidth, BorderColor: overlay.BorderColor, ShadowOpacity: overlay.ShadowOpacity, ShadowRadius: overlay.ShadowRadius,
                    ShadowOffsetX: overlay.ShadowOffsetX, ShadowOffsetY: overlay.ShadowOffsetY, BorderOutside: true));
            }
            if (scene.Camera is { } camera)
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, camera.MediaPath);
                var cameraInfo = await Probe(path, cancellation);
                if (cameraInfo.Duration <= 0 || camera.SourceTime < 0 || camera.SourceTime >= cameraInfo.Duration)
                    throw new InvalidDataException($"摄像头素材时间越界：请求 {camera.SourceTime:0.###} 秒，素材时长 {cameraInfo.Duration:0.###} 秒。请缩短摄像头源区间或调整起点。");
                var edge = Math.Max(outputWidth, outputHeight);
                var (decodeWidth, decodeHeight) = CameraDecodeSize(cameraInfo.Width, cameraInfo.Height, edge);
                if (!cameraStreams.TryGetValue(path, out var stream)) cameraStreams.Add(path, stream = new CameraFrameStream(path, decodeWidth, decodeHeight));
                var pixels = await stream.ReadAsync(camera.SourceTime, cancellation);
                var targetAspect = camera.Rect.Width * outputWidth / Math.Max(camera.Rect.Height * outputHeight, 1e-9);
                var sourceAspect = decodeWidth / (double)decodeHeight;
                var sourceCrop = ComputeCameraFillCrop(camera.Crop == default ? NormalizedRect.Full : camera.Crop, sourceAspect, targetAspect);
                // Camera shadows are authored against a 1080px short edge. The GPU's
                // shared layer shadow constants use a 1920px output-width reference.
                var shadowUnits = 1920d * Math.Min(outputWidth, outputHeight) / (1080d * outputWidth);
                result.Add(new(decodeWidth, decodeHeight, pixels, camera.Rect, Rotation: camera.Rotation, Opacity: camera.Opacity, Layer: int.MinValue + 1,
                    CornerRadius: camera.CornerRadius, Mirrored: camera.Mirrored, Shape: camera.Shape, SourceCrop: sourceCrop,
                    BorderWidth: camera.BorderWidth, BorderColor: camera.BorderColor,
                    ShadowOpacity: camera.ShadowStrength, ShadowRadius: 14 * shadowUnits, ShadowOffsetY: 8 * shadowUnits));
            }
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<(int Width, int Height, byte[] Pixels)> LoadImage(string path, int width, int height, CancellationToken cancellation)
    {
        var key = new AssetCacheKey(AssetCacheKeyKind.Image, path, width, height, default, null, null);
        if (imageCache.TryGetValue(key, out var cached)) return cached;
        var pixels = await MediaTools.DecodeFrameAsync(path, 0, width, height, cancellation);
        var entry = (width, height, pixels); imageCache.Set(key, entry, ByteSize(entry)); return entry;
    }

    private static long ByteSize((int Width, int Height, byte[] Pixels) entry) => entry.Pixels.LongLength;

    private enum AssetCacheKeyKind { Image, Text }
    private readonly record struct AssetCacheKey(AssetCacheKeyKind Kind, string? Path, int Width, int Height,
        ScreenFrameKind Frame, string? Title, string? Address);
    private sealed class AssetCacheKeyComparer : IEqualityComparer<AssetCacheKey>
    {
        public static AssetCacheKeyComparer Instance { get; } = new();
        public bool Equals(AssetCacheKey x, AssetCacheKey y) => x.Kind == y.Kind && x.Width == y.Width && x.Height == y.Height
            && x.Frame == y.Frame && (x.Kind == AssetCacheKeyKind.Image
                ? StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path)
                : StringComparer.Ordinal.Equals(x.Title, y.Title) && StringComparer.Ordinal.Equals(x.Address, y.Address));
        public int GetHashCode(AssetCacheKey value)
        {
            var hash = new HashCode(); hash.Add(value.Kind); hash.Add(value.Width); hash.Add(value.Height); hash.Add(value.Frame);
            if (value.Kind == AssetCacheKeyKind.Image) hash.Add(value.Path, StringComparer.OrdinalIgnoreCase);
            else { hash.Add(value.Title, StringComparer.Ordinal); hash.Add(value.Address, StringComparer.Ordinal); }
            return hash.ToHashCode();
        }
    }

    /// <summary>Center-crops within the user's camera crop so the source fills the target geometry without stretching.</summary>
    public static NormalizedRect ComputeCameraFillCrop(NormalizedRect userCrop, double sourceAspect, double targetAspect)
    {
        var crop = userCrop.Clamp();
        if (!crop.IsFinite || crop.Width <= 0 || crop.Height <= 0
            || !double.IsFinite(sourceAspect) || sourceAspect <= 0
            || !double.IsFinite(targetAspect) || targetAspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(userCrop), "摄像头取景比例或裁切矩形无效。");
        var currentAspect = sourceAspect * crop.Width / crop.Height;
        if (currentAspect > targetAspect)
        {
            var width = crop.Width * targetAspect / currentAspect;
            crop = crop with { X = crop.X + (crop.Width - width) / 2, Width = width };
        }
        else if (currentAspect < targetAspect)
        {
            var height = crop.Height * currentAspect / targetAspect;
            crop = crop with { Y = crop.Y + (crop.Height - height) / 2, Height = height };
        }
        return crop;
    }

    private static (int Width, int Height) CameraDecodeSize(int sourceWidth, int sourceHeight, int maxEdge)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidDataException("摄像头素材宽高无效。");
        var scale = Math.Min(1, maxEdge / (double)Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2) * 2);
        var height = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2) * 2);
        return (width, height);
    }

    private async Task<MediaInfo> Probe(string path, CancellationToken cancellation)
    {
        if (mediaInfo.TryGetValue(path, out var cached)) return cached;
        var info = await MediaTools.ProbeAsync(path, cancellation);
        mediaInfo.Add(path, info);
        return info;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; imageCache.Clear(); mediaInfo.Clear();
        foreach (var stream in cameraStreams.Values) stream.Dispose();
        foreach (var stream in backgroundVideoStreams.Values) stream.Dispose();
        cameraStreams.Clear(); backgroundVideoStreams.Clear(); gate.Dispose();
    }

    private NormalizedRect FrameHeaderRect(FrameScene scene, CanvasSettings canvas)
    {
        var sw = scene.SourceWidth > 0 ? scene.SourceWidth : canvas.AspectWidth;
        var sh = scene.SourceHeight > 0 ? scene.SourceHeight : canvas.AspectHeight;
        // The renderer owns the card geometry (zoom viewport, screen-motion placement, opening); the header strip rides on top of it.
        return GpuFrameRenderer.GetScreenHeaderRect(scene, Math.Max(sw, 1), Math.Max(sh, 1), outputWidth, outputHeight).Clamp();
    }

    private (int Width, int Height, byte[] Pixels) BuildFrameText(CanvasSettings canvas, int textWidth, int textHeight, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var buffer = new byte[checked(textWidth * textHeight * 4)];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var light = canvas.Frame is ScreenFrameKind.WindowLight or ScreenFrameKind.BrowserLight;
                    var titleBrush = new SolidColorBrush(light ? Color.FromRgb(153, 153, 153) : Color.FromRgb(226, 229, 235));
                    var addressBrush = new SolidColorBrush(light ? Color.FromRgb(130, 136, 146) : Color.FromRgb(190, 195, 204));
                    var hasTitle = !string.IsNullOrWhiteSpace(canvas.Title);
                    var hasAddress = !string.IsNullOrWhiteSpace(canvas.Address);
                    if (hasTitle)
                    {
                        var titleTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                        var title = new FormattedText(canvas.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, titleTypeface, Math.Clamp(textHeight * (hasAddress ? .31 : .32), 9, 28), titleBrush, 1.0);
                        var x = (textWidth - title.Width) * .5;
                        var y = hasAddress ? textHeight * .015 : (textHeight - title.Height) * .5;
                        dc.DrawText(title, new Point(x, y));
                    }
                    if (hasAddress)
                    {
                        var address = new FormattedText(canvas.Address, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Clamp(textHeight * .23, 8, 20), addressBrush, 1.0);
                        var x = (textWidth - address.Width) * .5;
                        var y = hasTitle ? textHeight * .56 : (textHeight - address.Height) * .5;
                        dc.DrawText(address, new Point(x, y));
                    }
                }
                var bitmap = new RenderTargetBitmap(textWidth, textHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var premultiplied = new byte[buffer.Length]; bitmap.CopyPixels(premultiplied, textWidth * 4, 0);
                for (var i = 0; i < premultiplied.Length; i += 4)
                {
                    var a = premultiplied[i + 3]; buffer[i + 3] = a;
                    if (a == 0) continue;
                    buffer[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
                    buffer[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
                    buffer[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("无法生成窗口标题图层。", failure);
        return (textWidth, textHeight, buffer);
    }
}

/// <summary>Byte-bounded least-recently-used cache. Values larger than the budget are returned by callers but are not retained.</summary>
internal sealed class ByteBudgetLruCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Size);
    private readonly long budgetBytes;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> entries;
    private readonly LinkedList<Entry> recency = new();
    private long cachedBytes;

    public ByteBudgetLruCache(long budgetBytes, IEqualityComparer<TKey>? comparer = null)
    {
        if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        this.budgetBytes = budgetBytes;
        entries = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public long CachedBytes => cachedBytes;
    public int Count => entries.Count;
    public long HitCount { get; private set; }
    public long MissCount { get; private set; }
    public long EvictionCount { get; private set; }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!entries.TryGetValue(key, out var node)) { MissCount++; value = default!; return false; }
        HitCount++;
        recency.Remove(node);
        recency.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value, long sizeBytes)
    {
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        Remove(key);
        if (sizeBytes > budgetBytes || budgetBytes == 0) return;
        while (cachedBytes > budgetBytes - sizeBytes && recency.Last is { } leastRecent)
            RemoveNode(leastRecent, eviction: true);
        var node = recency.AddFirst(new Entry(key, value, sizeBytes));
        entries.Add(key, node);
        cachedBytes += sizeBytes;
    }

    public void Clear()
    {
        entries.Clear(); recency.Clear(); cachedBytes = 0;
    }

    private void Remove(TKey key)
    {
        if (entries.TryGetValue(key, out var node)) RemoveNode(node, eviction: false);
    }

    private void RemoveNode(LinkedListNode<Entry> node, bool eviction)
    {
        recency.Remove(node);
        entries.Remove(node.Value.Key);
        cachedBytes -= node.Value.Size;
        if (eviction) EvictionCount++;
    }
}

/// <summary>Sequential 30 fps decoder; backwards seeks and large jumps restart FFmpeg at the requested timestamp.</summary>
internal sealed class CameraFrameStream : IDisposable
{
    private const int FrameRate = 30;
    private readonly string path;
    private readonly int width, height;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task<string>? errorText;
    private long currentFrame = -1;
    private byte[]? lastFrame;
    private bool disposed;

    public CameraFrameStream(string path, int width, int height) { this.path = path; this.width = width; this.height = height; }

    public async Task<byte[]> ReadAsync(double sourceTime, CancellationToken cancellation)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellation);
        try
        {
            var target = (long)Math.Round(sourceTime * FrameRate);
            if (process is null || process.HasExited || target < currentFrame || target - currentFrame > FrameRate * 2L)
            {
                await Restart(target, cancellation);
            }
            if (target == currentFrame && lastFrame is not null) return lastFrame;
            while (currentFrame < target)
            {
                lastFrame = await ReadFrame(cancellation); currentFrame++;
            }
            return lastFrame!;
        }
        catch when (cancellation.IsCancellationRequested)
        {
            // A cancelled preview seek must not leave a killed FFmpeg process cached for the next seek.
            await StopProcess(); currentFrame = -1; lastFrame = null;
            throw new OperationCanceledException(cancellation);
        }
        finally { gate.Release(); }
    }

    private async Task Restart(long target, CancellationToken cancellation)
    {
        await StopProcess();
        currentFrame = target - 1; lastFrame = null;
        var time = target / (double)FrameRate;
        process = MediaTools.Start(MediaTools.Ffmpeg,
            ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-an", "-vf", $"fps={FrameRate},scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,format=bgra", "-f", "rawvideo", "pipe:1"], output: true);
        errorText = process.StandardError.ReadToEndAsync();
        lastFrame = await ReadFrame(cancellation); currentFrame = target;
    }

    private async Task<byte[]> ReadFrame(CancellationToken cancellation)
    {
        var p = process ?? throw new InvalidOperationException("相机解码进程尚未启动。");
        var frame = new byte[checked(width * height * 4)];
        try { await p.StandardOutput.BaseStream.ReadExactlyAsync(frame, cancellation); return frame; }
        catch (EndOfStreamException)
        {
            await p.WaitForExitAsync(cancellation); var errors = errorText is null ? "" : await errorText;
            throw new IOException("相机素材提前结束或无法解码：" + MediaTools.Tail(errors));
        }
    }

    private async Task StopProcess()
    {
        if (process is null) return;
        var old = process; process = null;
        await MediaTools.StopAsync(old); old.Dispose(); errorText = null;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        var old = process; process = null;
        if (old is not null) { MediaTools.Kill(old); old.Dispose(); }
        lastFrame = null; gate.Dispose();
    }
}

/// <summary>Sequential loopable decoder for project background video; it retains only one decoded frame.</summary>
internal sealed class BackgroundVideoFrameStream : IDisposable
{
    private const int FrameRate = 30;
    private readonly string path;
    private readonly int width, height;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task<string>? errorText;
    private long currentFrame = -1;
    private byte[]? lastFrame;
    private bool disposed;

    public BackgroundVideoFrameStream(string path, int width, int height)
    { this.path = path; this.width = width; this.height = height; }

    public async Task<byte[]> ReadAsync(double time, CancellationToken cancellation)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellation);
        try
        {
            var target = (long)Math.Floor(time * FrameRate + 1e-7);
            if (process is null || target < currentFrame || target - currentFrame > FrameRate * 2L)
                await Restart(target, cancellation);
            if (target == currentFrame && lastFrame is not null) return lastFrame;
            while (currentFrame < target)
            {
                lastFrame = await ReadFrame(cancellation);
                currentFrame++;
            }
            return lastFrame!;
        }
        catch when (cancellation.IsCancellationRequested)
        {
            await StopProcess();
            throw new OperationCanceledException(cancellation);
        }
        finally { gate.Release(); }
    }

    private async Task Restart(long target, CancellationToken cancellation)
    {
        await StopProcess();
        currentFrame = target - 1; lastFrame = null;
        var time = target / (double)FrameRate;
        process = MediaTools.Start(MediaTools.Ffmpeg,
            ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-an", "-vf",
             $"fps={FrameRate},scale={width}:{height}:force_original_aspect_ratio=decrease:flags=bilinear,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra",
             "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], output: true);
        errorText = process.StandardError.ReadToEndAsync();
        lastFrame = await ReadFrame(cancellation); currentFrame = target;
    }

    private async Task<byte[]> ReadFrame(CancellationToken cancellation)
    {
        var active = process ?? throw new InvalidOperationException("背景视频解码进程尚未启动。");
        using var registration = cancellation.Register(() => MediaTools.Kill(active));
        var frame = new byte[checked(width * height * 4)];
        try { await active.StandardOutput.BaseStream.ReadExactlyAsync(frame, cancellation); return frame; }
        catch (EndOfStreamException)
        {
            await active.WaitForExitAsync(cancellation);
            var errors = errorText is null ? "" : await errorText;
            throw new IOException("背景视频提前结束或无法解码：" + MediaTools.Tail(errors));
        }
    }

    private async Task StopProcess()
    {
        var old = process; process = null; errorText = null;
        if (old is null) return;
        await MediaTools.StopAsync(old); old.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var old = process; process = null;
        if (old is not null) { MediaTools.Kill(old); old.Dispose(); }
        lastFrame = null; gate.Dispose();
    }
}
