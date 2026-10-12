using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameTrail;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameTrail.Media;

/// <summary>A decoded BGRA image/video layer positioned on the output canvas. Input buffers are consumed synchronously.</summary>
public sealed record FrameLayerInput(int Width, int Height, byte[] Bgra, NormalizedRect Rect, double Rotation = 0, double Opacity = 1, int Layer = 0, double CornerRadius = 0, bool Mirrored = false, bool BehindScreen = false, BackgroundImageFit Fit = BackgroundImageFit.Stretch, double Blur = 0, CameraShape Shape = CameraShape.RoundedRectangle, NormalizedRect? SourceCrop = null, bool FollowScreenTransform = false, double BorderWidth = 0, string BorderColor = "#FFFFFF", double ShadowOpacity = 0, double ShadowRadius = 0, double ShadowOffsetX = 0, double ShadowOffsetY = 0, bool BorderOutside = false);

/// <summary>One-frame Direct3D 11 compositor. Calls may move between threads, but must remain sequential.</summary>
public sealed class GpuFrameRenderer : IDisposable
{
    private readonly int sourceWidth, sourceHeight, outputWidth, outputHeight;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11Texture2D? sourceTexture, targetTexture, stagingTexture;
    private ID3D11ShaderResourceView? sourceView;
    private ID3D11RenderTargetView? targetView;
    private readonly Dictionary<string, CursorSlot> cursorSlots = new(StringComparer.Ordinal);
    private ID3D11VertexShader? vertexShader;
    private ID3D11PixelShader? pixelShader;
    private ID3D11SamplerState? sampler;
    private ID3D11Buffer? constantsBuffer;
    private readonly LayerSlot?[] layerSlots = new LayerSlot?[9];
    private readonly CompositeTarget?[] compositeTargets = new CompositeTarget?[4];
    private bool disposed;

    public string Backend { get; }

    public GpuFrameRenderer(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        this.sourceWidth = sourceWidth; this.sourceHeight = sourceHeight;
        this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        try
        {
            try
            {
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device!, out _, out context!).CheckError();
                Backend = "Direct3D 11 GPU hardware";
            }
            catch
            {
                device?.Dispose(); context?.Dispose(); device = null; context = null;
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device!, out _, out context!).CheckError();
                Backend = "Direct3D 11 WARP software";
            }
            Initialize();
        }
        catch { Dispose(); throw; }
    }

    public void Render(byte[] bgraSource, FrameScene scene, byte[] bgraOutput) => Render(bgraSource, scene, bgraOutput, null);

    public void Render(byte[] bgraSource, FrameScene scene, byte[] bgraOutput, IReadOnlyList<FrameLayerInput>? layers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bgraSource); ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(bgraOutput);
        var sourceBytes = checked(sourceWidth * sourceHeight * 4); var outputBytes = checked(outputWidth * outputHeight * 4);
        if (bgraSource.Length != sourceBytes) throw new ArgumentException($"源 BGRA 缓冲区长度应为 {sourceBytes} 字节。", nameof(bgraSource));
        if (bgraOutput.Length != outputBytes) throw new ArgumentException($"输出 BGRA 缓冲区长度应为 {outputBytes} 字节。", nameof(bgraOutput));
        if ((scene.BlurRegions?.Count ?? 0) > 4) throw new ArgumentException("单帧最多支持 4 个独立模糊区域。", nameof(scene));
        if (layers is { Count: > 9 }) throw new ArgumentException("单帧最多支持 8 个前景和 1 个背景合成图层。", nameof(layers));
        var orderedLayers = layers?.OrderBy(x => x.Layer).ToArray() ?? [];
        var underlays = orderedLayers.Where(x => x.BehindScreen).ToArray();
        if (underlays.Length > 1) throw new ArgumentException("单帧最多支持 1 个屏幕后置背景图层。", nameof(layers));
        var underlay = underlays.FirstOrDefault();
        var foregroundLayers = orderedLayers.Where(x => !x.BehindScreen).ToArray();
        if (foregroundLayers.Length > 8) throw new ArgumentException("单帧最多支持 8 个前景合成图层。", nameof(layers));
        var constants = MakeConstants(scene, foregroundLayers, underlay, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var treatment = scene.OverlayBackdrop ?? new FrameOverlayBackdrop();
        unsafe
        {
            fixed (byte* sourcePtr = bgraSource)
                context!.UpdateSubresource(sourceTexture!, 0, null, (IntPtr)sourcePtr, (uint)(sourceWidth * 4), 0);
        }
        context!.UpdateSubresource(in constants, constantsBuffer!);
        context.OMSetRenderTargets(targetView!, null);
        context.RSSetViewports(new[] { new Viewport(0, 0, outputWidth, outputHeight) });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShader);
        context.PSSetConstantBuffer(0, constantsBuffer!);
        context.PSSetShaderResource(0, sourceView!);
        if (underlay is { } bg)
        {
            if (bg.Bgra.Length != checked(bg.Width * bg.Height * 4)) throw new ArgumentException("背景 BGRA 缓冲区长度与尺寸不匹配。", nameof(layers));
            var slot = EnsureLayerSlot(8, bg.Width, bg.Height);
            unsafe { fixed (byte* ptr = bg.Bgra) context.UpdateSubresource(slot.Texture, 0, null, (IntPtr)ptr, (uint)(bg.Width * 4), 0); }
            context.PSSetShaderResource(9, slot.View);
        }
        else context.PSSetShaderResource(9, null!);
        for (var i = 0; i < 8; i++)
        {
            if (i < foregroundLayers.Length)
            {
                var layer = foregroundLayers[i];
                if (layer.Bgra.Length != checked(layer.Width * layer.Height * 4)) throw new ArgumentException($"合成图层 {i} BGRA 缓冲区长度与尺寸不匹配。", nameof(layers));
                var slot = EnsureLayerSlot(i, layer.Width, layer.Height);
                unsafe
                {
                    fixed (byte* layerPtr = layer.Bgra)
                        context.UpdateSubresource(slot.Texture, 0, null, (IntPtr)layerPtr, (uint)(layer.Width * 4), 0);
                }
                context.PSSetShaderResource((uint)(i + 1), slot.View);
            }
            else context.PSSetShaderResource((uint)(i + 1), null!);
        }
        CursorSlot? cursorSlot = null;
        var cursorStyle = scene.CursorAppearance?.Style ?? CursorStyle.SystemArrow;
        if (cursorStyle is CursorStyle.FollowRecorded or CursorStyle.SystemHand or CursorStyle.SystemText)
        {
            var cursorAssetId = cursorStyle switch
            {
                CursorStyle.SystemHand => "system.hand",
                CursorStyle.SystemText => "system.ibeam",
                _ => SystemCursorAssetLibrary.Normalize(scene.CursorAsset)
            };
            cursorSlots.TryGetValue(cursorAssetId, out cursorSlot);
        }
        context.PSSetShaderResource(10, cursorSlot is null ? null! : cursorSlot.View);
        context.PSSetSampler(0, sampler!);
        var baseBlur = treatment.ScreenBlur * (1 - Math.Clamp(treatment.ScreenSuppression, 0, 1));
        var cameraBlur = treatment.CameraBlur;
        if (baseBlur > .01 || cameraBlur > .01)
        {
            // Keep screen/background and camera independent through the blur passes. All
            // intermediates stay on the GPU; only the final frame is read back for encoding.
            var headerCount = foregroundLayers.TakeWhile(l => l.FollowScreenTransform).Count();
            var cameraIndex = Array.FindIndex(foregroundLayers, l => l.Layer == int.MinValue + 1);
            var cameraCount = cameraIndex >= 0 ? 1 : 0;
            var baseTarget = EnsureCompositeTarget(0);
            var pass = constants;
            pass.LayerCount = new(headerCount, 0, 0, 0);
            pass.BackdropPass = new(0, 0, 0, 0);
            DrawPass(pass, baseTarget.Target);
            var baseView = baseTarget.View;
            if (baseBlur > .01)
            {
                var horizontal = EnsureCompositeTarget(1);
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
                context.PSSetShaderResource(11, baseView);
                pass.BackdropPass = new(1, (float)baseBlur, 0, 0);
                DrawPass(pass, horizontal.Target); baseView = horizontal.View;
                context.PSSetShaderResource(11, null!);
            }
            ID3D11ShaderResourceView? cameraView = null;
            if (cameraCount > 0)
            {
                var cameraTarget = EnsureCompositeTarget(2);
                pass = constants; pass.LayerCount = new(cameraIndex + 1, cameraIndex, 0, 0);
                pass.BackdropPass = new(3, 0, 0, 0);
                DrawPass(pass, cameraTarget.Target); cameraView = cameraTarget.View;
                if (cameraBlur > .01)
                {
                    var horizontal = EnsureCompositeTarget(3);
                    context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
                    context.PSSetShaderResource(11, cameraView);
                    pass.BackdropPass = new(1, (float)cameraBlur, 0, 0);
                    DrawPass(pass, horizontal.Target); cameraView = horizontal.View;
                    context.PSSetShaderResource(11, null!);
                }
            }
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            context.PSSetShaderResource(11, baseView);
            context.PSSetShaderResource(12, cameraView!);
            pass = constants; pass.LayerCount.Y = headerCount + cameraCount;
            pass.BackdropPass = new(2, (float)baseBlur, (float)cameraBlur, cameraCount);
            DrawPass(pass, targetView!);
        }
        else context.Draw(3, 0);
        for (var i = 0; i < 13; i++) context.PSSetShaderResource((uint)i, null!);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        context.CopyResource(stagingTexture!, targetTexture!);
        context.Map(stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dst = bgraOutput)
                    for (var row = 0; row < outputHeight; row++)
                        Buffer.MemoryCopy(src + row * mapped.RowPitch, dst + row * outputWidth * 4, outputWidth * 4, outputWidth * 4);
            }
        }
        finally { context.Unmap(stagingTexture!, 0); }
    }

    private void DrawPass(ShaderConstants constants, ID3D11RenderTargetView target)
    {
        context!.OMSetRenderTargets(target, null);
        context.UpdateSubresource(in constants, constantsBuffer!);
        context.Draw(3, 0);
    }

    private CompositeTarget EnsureCompositeTarget(int index)
    {
        if (compositeTargets[index] is { } existing) return existing;
        var texture = device!.CreateTexture2D(TextureDescription(outputWidth, outputHeight, Format.B8G8R8A8_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        return compositeTargets[index] = new(texture, device.CreateRenderTargetView(texture), device.CreateShaderResourceView(texture));
    }

    private void Initialize()
    {
        sourceTexture = device!.CreateTexture2D(TextureDescription(sourceWidth, sourceHeight, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        sourceView = device.CreateShaderResourceView(sourceTexture);
        targetTexture = device.CreateTexture2D(TextureDescription(outputWidth, outputHeight, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
        targetView = device.CreateRenderTargetView(targetTexture);
        stagingTexture = device.CreateTexture2D(TextureDescription(outputWidth, outputHeight, Format.B8G8R8A8_UNorm, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        vertexShader = device.CreateVertexShader(VertexBytecode.Value);
        pixelShader = device.CreatePixelShader(PixelBytecode.Value);
        constantsBuffer = device.CreateBuffer((uint)Marshal.SizeOf<ShaderConstants>(), BindFlags.ConstantBuffer);
        sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
            MaxAnisotropy = 1
        });
        foreach (var cursor in SystemCursorAssetLibrary.All.Values)
        {
            var texture = device.CreateTexture2D(TextureDescription(cursor.Width, cursor.Height, Format.B8G8R8A8_UNorm,
                BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
            unsafe
            {
                fixed (byte* ptr = cursor.Bgra)
                    context!.UpdateSubresource(texture, 0, null, (IntPtr)ptr, (uint)(cursor.Width * 4), 0);
            }
            cursorSlots.Add(cursor.Id, new(cursor, texture, device.CreateShaderResourceView(texture)));
        }
    }

    /// <summary>Maps a normalized canvas coordinate onto the recorded source image when it hits the visible screen body.</summary>
    public static bool TryMapCanvasPointToSource(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
        NormalizedPoint canvasPoint, out NormalizedPoint sourcePoint)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        sourcePoint = default;
        if (!canvasPoint.IsFinite || canvasPoint.X is < 0 or > 1 || canvasPoint.Y is < 0 or > 1) return false;

        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var centerX = constants.ScreenRect.X + constants.ScreenRect.Z * .5f;
        var centerY = constants.ScreenRect.Y + constants.ScreenRect.W * .5f;
        var pixelX = (float)canvasPoint.X * outputWidth;
        var pixelY = (float)canvasPoint.Y * outputHeight;
        var angle = -constants.Motion.X * (MathF.PI / 180);
        var dx = pixelX - centerX; var dy = pixelY - centerY;
        var contentX = centerX + MathF.Cos(angle) * dx - MathF.Sin(angle) * dy;
        var contentY = centerY + MathF.Sin(angle) * dx + MathF.Cos(angle) * dy;
        var halfWidth = Math.Max(constants.ScreenRect.Z * .5f, 1);
        var halfHeight = Math.Max(constants.ScreenRect.W * .5f, 1);
        var targetX = (contentX - centerX) / halfWidth;
        var targetY = (contentY - centerY) / halfHeight;
        var projected = TransformPoint(targetX, targetY, constants.Inverse0, constants.Inverse1, constants.Inverse2);
        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z) || Math.Abs(projected.Z) < 1e-6f) return false;
        var sign = MathF.Sign(projected.Z);
        var sourceX = projected.X / Math.Abs(projected.Z) * sign * .5f + .5f;
        var sourceY = projected.Y / Math.Abs(projected.Z) * sign * .5f + .5f;
        const float edgeTolerance = .001f;
        if (sourceX < -edgeTolerance || sourceX > 1 + edgeTolerance || sourceY < -edgeTolerance || sourceY > 1 + edgeTolerance) return false;
        sourceX = Math.Clamp(sourceX, 0, 1); sourceY = Math.Clamp(sourceY, 0, 1);

        var pX = (sourceX * 2 - 1) * halfWidth; var pY = (sourceY * 2 - 1) * halfHeight;
        var radius = constants.Style.X;
        var qX = Math.Abs(pX) - halfWidth + radius;
        var qY = Math.Abs(pY) - halfHeight + radius;
        var outsideX = Math.Max(qX, 0); var outsideY = Math.Max(qY, 0);
        var distance = MathF.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(qX, qY), 0) - radius;
        if (distance > .5f) return false;
        var header = constants.FrameStyle.X;
        var cardHalfHeight = halfHeight + header * .5f;
        var cardPointY = pY + header * .5f;
        var desktopChrome = constants.FrameStyle.Y is > .5f and < 4.5f;
        var cardRadius = desktopChrome ? constants.Style2.W : radius + constants.FrameStyle.W;
        var cardQx = Math.Abs(pX) - halfWidth + cardRadius;
        var cardQy = Math.Abs(cardPointY) - cardHalfHeight + cardRadius;
        var cardOutsideX = Math.Max(cardQx, 0); var cardOutsideY = Math.Max(cardQy, 0);
        var cardDistance = MathF.Sqrt(cardOutsideX * cardOutsideX + cardOutsideY * cardOutsideY)
            + Math.Min(Math.Max(cardQx, cardQy), 0) - cardRadius;
        if (cardDistance > .5f) return false;

        sourcePoint = new NormalizedPoint(
            constants.Crop.X + sourceX * constants.Crop.Z,
            constants.Crop.Y + sourceY * constants.Crop.W);
        return sourcePoint.IsFinite;
    }

    /// <summary>Projects a full-source normalized point through crop, screen motion, and opening into normalized output-canvas coordinates.</summary>
    public static bool TryMapSourcePointToCanvas(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
        NormalizedPoint sourcePoint, out NormalizedPoint canvasPoint)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        canvasPoint = default;
        if (!sourcePoint.IsFinite || sourcePoint.X is < 0 or > 1 || sourcePoint.Y is < 0 or > 1) return false;
        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var localX = (sourcePoint.X - constants.Crop.X) / constants.Crop.Z;
        var localY = (sourcePoint.Y - constants.Crop.Y) / constants.Crop.W;
        // Permit extrapolation beyond the crop so a partly cropped source-space
        // rectangle still displays its true projected edge at the crop boundary.
        var x = (float)(localX * 2 - 1);
        var y = (float)(localY * 2 - 1);
        var projected = TransformPoint(x, y, constants.Transform0, constants.Transform1, constants.Transform2);
        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z) || Math.Abs(projected.Z) < 1e-6f) return false;
        var centerX = constants.ScreenRect.X + constants.ScreenRect.Z * .5f;
        var centerY = constants.ScreenRect.Y + constants.ScreenRect.W * .5f;
        var pixelX = centerX + projected.X / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * constants.ScreenRect.Z * .5f;
        var pixelY = centerY + projected.Y / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * constants.ScreenRect.W * .5f;
        var angle = constants.Motion.X * (MathF.PI / 180);
        var dx = pixelX - centerX; var dy = pixelY - centerY;
        canvasPoint = new((centerX + MathF.Cos(angle) * dx - MathF.Sin(angle) * dy) / outputWidth,
            (centerY + MathF.Sin(angle) * dx + MathF.Cos(angle) * dy) / outputHeight);
        return canvasPoint.IsFinite;
    }

    /// <summary>Returns the projected screen quadrilateral in canvas-normalized TL, TR, BR, BL order.</summary>
    public static IReadOnlyList<NormalizedPoint> GetProjectedScreenCorners(FrameScene scene, int sourceWidth, int sourceHeight,
        int outputWidth, int outputHeight, bool includeHeader = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var halfWidth = Math.Max(constants.ScreenRect.Z * .5f, 1);
        var halfHeight = Math.Max(constants.ScreenRect.W * .5f, 1);
        var centerX = constants.ScreenRect.X + halfWidth;
        var centerY = constants.ScreenRect.Y + halfHeight;
        var headerExtension = includeHeader ? 2 * constants.FrameStyle.X / Math.Max(constants.ScreenRect.W, 1) : 0;
        var localCorners = new[] { (-1f, -1f - headerExtension), (1f, -1f - headerExtension), (1f, 1f), (-1f, 1f) };
        var angle = constants.Motion.X * (MathF.PI / 180);
        var result = new NormalizedPoint[4];
        for (var i = 0; i < localCorners.Length; i++)
        {
            var projected = TransformPoint(localCorners[i].Item1, localCorners[i].Item2,
                constants.Transform0, constants.Transform1, constants.Transform2);
            if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z) || Math.Abs(projected.Z) < 1e-6f)
                throw new InvalidOperationException("屏幕透视矩阵无法投影选区顶点。");
            var x = centerX + projected.X / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * halfWidth;
            var y = centerY + projected.Y / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * halfHeight;
            var dx = x - centerX; var dy = y - centerY;
            var rotatedX = centerX + MathF.Cos(angle) * dx - MathF.Sin(angle) * dy;
            var rotatedY = centerY + MathF.Sin(angle) * dx + MathF.Cos(angle) * dy;
            result[i] = new(rotatedX / outputWidth, rotatedY / outputHeight);
        }
        return result;
    }

    /// <summary>
    /// Normalized canvas rectangle of the window/browser toolbar strip that sits on top of the screen card (zoom, screen motion and
    /// opening included). Used to place the rendered header text. Empty (zero height) when the frame has no toolbar.
    /// </summary>
    public static NormalizedRect GetScreenHeaderRect(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var header = constants.FrameStyle.X;
        return new NormalizedRect(constants.ScreenRect.X / outputWidth, (constants.ScreenRect.Y - header) / outputHeight,
            constants.ScreenRect.Z / outputWidth, header / outputHeight);
    }

    private static Vector3 TransformPoint(float x, float y, Vector4 row0, Vector4 row1, Vector4 row2) => new(
        x * row0.X + y * row1.X + row2.X,
        x * row0.Y + y * row1.Y + row2.Y,
        x * row0.Z + y * row1.Z + row2.Z);

    private static ShaderConstants MakeConstants(FrameScene scene, FrameLayerInput[] layers, FrameLayerInput? underlay,
        int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var padding = Math.Clamp(double.IsFinite(scene.Padding) ? scene.Padding : 0, 0, .49);
        var availableWidth = outputWidth * (1 - 2 * padding); var availableHeight = outputHeight * (1 - 2 * padding);
        var frame = scene.Canvas?.Frame ?? ScreenFrameKind.None;
        var shortEdge = Math.Min(outputWidth, outputHeight);
        var frameScale = scene.Canvas?.FrameScale ?? 1;
        var isPhone = frame == ScreenFrameKind.Phone;
        var isTablet = frame == ScreenFrameKind.Tablet;
        var deviceKind = isPhone ? 1f : isTablet ? 2f : 0f;
        var bezelBase = shortEdge * (isPhone ? .026 : isTablet ? .035 : 0);
        var outerRadiusBase = shortEdge * (isPhone ? .11 : isTablet ? .065 : 0);
        // Match the source renderer's 46px toolbar at 1080p; browser chrome uses its 1.08 toolbar scale.
        var toolbarScale = Math.Clamp(scene.Canvas?.FrameToolbarScale ?? 1, .65, 1.6);
        var baseHeaderHeight = (frame switch { ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark => shortEdge * (46d / 1080), ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => shortEdge * (46d * 1.08 / 1080), _ => 0 }) * toolbarScale;
        var reservedHeader = baseHeaderHeight * frameScale;
        // Two layout models (FrameTrailSettings.LayoutModel): 1 = original Windows geometry, 2 = Mac card model. Both stay in one place so the
        // renderer and every canvas/source mapping helper that calls MakeConstants agree.
        var card = scene.LayoutModel < 2
            ? LegacyScreenCard(scene, sourceWidth, sourceHeight, outputWidth, outputHeight, availableWidth, availableHeight, bezelBase, baseHeaderHeight, reservedHeader, frameScale)
            : MacScreenCard(scene, sourceWidth, sourceHeight, outputWidth, outputHeight, availableWidth, availableHeight, bezelBase, baseHeaderHeight, reservedHeader, frameScale, frame, deviceKind);
        var crop = card.Crop; var cropSize = card.CropSize; var keepsCanvasCovered = card.KeepsCanvasCovered; var zoomScale = card.ZoomScale;
        var totalScale = card.TotalScale; var headerHeight = card.HeaderHeight; var screenWidth = card.Width; var screenHeight = card.Height;
        var left = card.Left; var top = card.Top;
        var opening = scene.Opening ?? new FrameOpeningState();
        var deviceBezel = bezelBase * totalScale;
        var authoredOuterRadius = scene.Canvas?.FrameOuterRadius;
        var deviceOuterRadius = authoredOuterRadius.HasValue
            ? shortEdge * Math.Clamp(authoredOuterRadius.Value, 0, 160) / 1080d * totalScale
            : outerRadiusBase * totalScale;
        var openingScaleForEdges = scene.Opening is null ? 1 : Math.Clamp(double.IsFinite(opening.Scale) ? opening.Scale : 1, .1, 4);
        var radius = deviceKind > 0 ? Math.Max(1, deviceOuterRadius - deviceBezel)
            : keepsCanvasCovered ? 0 : Math.Clamp(scene.CornerRadius, 0, .5) * shortEdge * openingScaleForEdges * zoomScale;
        var chromeOuterRadius = frame switch
        {
            ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark => shortEdge * totalScale * ((float)(authoredOuterRadius ?? 18) / 1080f),
            ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => shortEdge * totalScale * ((float)(authoredOuterRadius ?? 22) / 1080f),
            _ => 0
        };
        var shadowRadius = Math.Clamp(scene.Shadow, 0, 1) * shortEdge * .045 * zoomScale;
        // Projection pivot: the authored point inside the recorded content stays fixed while the plane tilts (Mac projectionAnchor).
        var pivotAnchor = card.PivotAnchor;
        var cursorVisible = scene.CursorAppearance?.Visible != false && scene.Cursor is { IsFinite: true } p
            && p.X >= crop.X && p.X <= crop.X + cropSize.X
            && p.Y >= crop.Y && p.Y <= crop.Y + cropSize.Y;
        var cursor = cursorVisible ? scene.Cursor!.Value : new NormalizedPoint(-1, -1);
        var cursorScale = shortEdge / 1080f * (float)Math.Clamp(scene.CursorAppearance?.Scale ?? 1, .1, 5);
        var cursorStyle = scene.CursorAppearance?.Style ?? CursorStyle.SystemArrow;
        var cursorAsset = cursorStyle switch
        {
            CursorStyle.SystemHand => SystemCursorAssetLibrary.Resolve("system.hand"),
            CursorStyle.SystemText => SystemCursorAssetLibrary.Resolve("system.ibeam"),
            _ => SystemCursorAssetLibrary.Resolve(scene.CursorAsset)
        };
        var cursorReferenceHeight = Math.Max(SystemCursorAssetLibrary.Resolve("system.arrow").Height, 1);
        var cursorAssetInfo = new Vector4(
            cursorAsset.Width * 44f / cursorReferenceHeight,
            cursorAsset.Height * 44f / cursorReferenceHeight,
            cursorAsset.HotspotX * 44f / cursorReferenceHeight,
            cursorAsset.HotspotY * 44f / cursorReferenceHeight);
        var cursorRotation = scene.CursorAppearance?.MotionTiltEnabled == false || !double.IsFinite(scene.CursorRotationRadians)
            ? 0 : Math.Clamp(scene.CursorRotationRadians, -Math.PI / 4, Math.PI / 4);
        var clickProgress = scene.ClickProgress is { } progress && double.IsFinite(progress)
            ? (float)Math.Clamp(progress, 0, 1) : -1;
        var clickScale = (float)Math.Clamp(scene.CursorAppearance?.ClickScale ?? 1, .4, 3);
        var clickOpacity = (float)Math.Clamp(scene.CursorAppearance?.ClickOpacity ?? .8, .05, 1);
        var style = cursorStyle;
        var clickStyle = scene.CursorAppearance is { ClickEffectsEnabled: false } ? ClickEffectStyle.None : scene.CursorAppearance?.ClickEffect ?? ClickEffectStyle.Pulse;
        var gradient = scene.BackgroundStyle ?? new BackgroundSettings { Color = scene.Background, EndColor = scene.Background };
        var background = ParseColor(gradient.Color);
        var endBackground = ParseColor(gradient.Kind == BackgroundKind.LinearGradient ? gradient.EndColor : gradient.Color);
        var angle = (float)((double.IsFinite(gradient.AngleDegrees) ? gradient.AngleDegrees : 90) * Math.PI / 180);
        var patternKind = gradient.Kind == BackgroundKind.Pattern ? gradient.Pattern == BackgroundPatternKind.Grid ? 1 : 2 : 0;
        var patternSpacing = (float)(Math.Clamp(gradient.PatternSpacing, 8, 512) * shortEdge / 1080d);
        var patternStrength = gradient.Kind == BackgroundKind.Pattern ? (float)Math.Clamp(gradient.PatternStrength, 0, 1) : 0;
        var frameLight = frame is ScreenFrameKind.WindowLight or ScreenFrameKind.BrowserLight;
        var motionState = scene.ScreenMotion ?? new ScreenMotionState();
        if (scene.Opening is { } screenOpening)
            motionState = motionState with
            {
                RotationX = motionState.RotationX + screenOpening.RotationX,
                RotationY = motionState.RotationY + screenOpening.RotationY,
                Perspective = Math.Clamp(motionState.Perspective + screenOpening.Perspective, 0, 2)
            };
        var transform = MakeHomography(motionState, pivotAnchor);
        var inverse = InvertHomography(transform);
        var result = new ShaderConstants
        {
            OutputSourceSize = new(outputWidth, outputHeight, sourceWidth, sourceHeight),
            Crop = new((float)crop.X, (float)crop.Y, (float)cropSize.X, (float)cropSize.Y),
            ScreenRect = new((float)left, (float)top, (float)screenWidth, (float)screenHeight),
            Style = new((float)radius, (float)shadowRadius, (float)Math.Clamp(scene.Shadow, 0, 1), cursorVisible ? (float)Math.Clamp(scene.ClickStrength, 0, 1) : 0),
            Cursor = new((float)cursor.X, (float)cursor.Y, cursorVisible ? 1 : 0, cursorScale),
            CursorDynamics = new((float)cursorRotation, clickProgress, clickScale, clickOpacity),
            Background = background,
            Style2 = new(angle, (float)style, (float)clickStyle, (float)chromeOuterRadius),
            BackgroundEnd = endBackground,
            BackgroundPattern = new(patternKind, patternSpacing, patternStrength, 0),
            PatternColor = ParseColor(gradient.PatternColor),
            BackgroundAnimation = new(gradient.Kind == BackgroundKind.Dynamic ? 1 : 0, (float)gradient.Preset,
                (float)((double.IsFinite(scene.OutputTime) ? scene.OutputTime : 0) * Math.Clamp(gradient.Speed, 0, 10)), 0),
            FrameStyle = new((float)headerHeight, (float)frame, frameLight ? 1 : 0, (float)Math.Clamp(scene.Canvas?.BorderWidth ?? 0, 0, 60) * outputHeight / Math.Max(scene.CanvasHeight, 1) * (float)openingScaleForEdges * (float)zoomScale),
            DeviceFrame = new((float)deviceBezel, (float)deviceOuterRadius, (float)(deviceBezel * .18), deviceKind),
            FrameColor = frameLight ? new(.92f, .93f, .95f, 1) : new(.12f, .13f, .15f, 1),
            Motion = new((float)((scene.ScreenMotion?.RotationZ ?? 0) + opening.Rotation), (float)Math.Clamp(opening.Opacity, 0, 1), 0, 0),
            Transform0 = transform.Row0, Transform1 = transform.Row1, Transform2 = transform.Row2,
            Inverse0 = inverse.Row0, Inverse1 = inverse.Row1, Inverse2 = inverse.Row2,
            ClickColor = ParseColor(scene.CursorAppearance?.ClickColor),
            Focus = scene.Focus is { } focus ? new((float)focus.Center.X, (float)focus.Center.Y, (float)focus.Size, (float)focus.Blur) : Vector4.Zero,
            FocusStyle = scene.Focus is { } focusStyle ? new((float)focusStyle.Shape, (float)focusStyle.AngleDegrees, (float)focusStyle.Softness, (float)focusStyle.Dimming) : Vector4.Zero,
            FocusProgress = new((float)Math.Clamp(scene.FocusProgress, 0, 1), 0, 0, 0),
            BlurRect0 = BlurRect(scene, 0), BlurRect1 = BlurRect(scene, 1), BlurRect2 = BlurRect(scene, 2), BlurRect3 = BlurRect(scene, 3),
            BlurStyle0 = BlurStyle(scene, 0), BlurStyle1 = BlurStyle(scene, 1), BlurStyle2 = BlurStyle(scene, 2), BlurStyle3 = BlurStyle(scene, 3),
            LayerCount = new(layers.Length, 0, 0, 0)
        };
        result.CursorAssetInfo = cursorAssetInfo;
        result.BackdropTreatment = new((float)Math.Clamp(scene.OverlayBackdrop?.ScreenSuppression ?? 0, 0, 1), 0, 0, 0);
        // Source area that can reach the canvas: zoom magnifies the card, so a spotlight is only active when it overlaps what is
        // actually on screen. A tilted or rotated plane keeps the whole crop as a conservative bound.
        var tilted = scene.LayoutModel < 2 || Math.Abs(motionState.RotationX) > 1e-6 || Math.Abs(motionState.RotationY) > 1e-6 || Math.Abs(result.Motion.X) > 1e-6;
        var visibleX0 = tilted ? crop.X : crop.X + Math.Clamp((0 - left) / Math.Max(screenWidth, 1e-6), 0, 1) * cropSize.X;
        var visibleX1 = tilted ? crop.X + cropSize.X : crop.X + Math.Clamp((outputWidth - left) / Math.Max(screenWidth, 1e-6), 0, 1) * cropSize.X;
        var visibleY0 = tilted ? crop.Y : crop.Y + Math.Clamp((0 - top) / Math.Max(screenHeight, 1e-6), 0, 1) * cropSize.Y;
        var visibleY1 = tilted ? crop.Y + cropSize.Y : crop.Y + Math.Clamp((outputHeight - top) / Math.Max(screenHeight, 1e-6), 0, 1) * cropSize.Y;
        result.SpotlightControl = new(scene.BlurRegions?.Any(b => b.Kind == BlurKind.Spotlight && b.Opacity > .001 &&
            (!b.SourceSpace || b.Rect.X < visibleX1 && b.Rect.X + b.Rect.Width > visibleX0
                && b.Rect.Y < visibleY1 && b.Rect.Y + b.Rect.Height > visibleY0)) == true ? 1 : 0,
            (float)(scene.BlurRegions?.Where(b => b.Kind == BlurKind.Spotlight).Select(b => b.SpotlightDimming * b.Opacity).DefaultIfEmpty(0).Max() ?? 0), 0, 0);
        var shadowReferenceScale = outputWidth / 1920f;
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i]; var rect = layer.Rect;
            if (!rect.IsFinite || rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("场景图层矩形必须是有限的正尺寸区域。", nameof(layers));
            var shadowAngle = (double.IsFinite(layer.Rotation) ? layer.Rotation : 0) * Math.PI / 180;
            var layerShadowX = double.IsFinite(layer.ShadowOffsetX) ? Math.Clamp(layer.ShadowOffsetX, -4096, 4096) : 0;
            var layerShadowY = double.IsFinite(layer.ShadowOffsetY) ? Math.Clamp(layer.ShadowOffsetY, -4096, 4096) : 0;
            var rotatedOffsetX = layerShadowX * Math.Cos(shadowAngle) - layerShadowY * Math.Sin(shadowAngle);
            var rotatedOffsetY = layerShadowX * Math.Sin(shadowAngle) + layerShadowY * Math.Cos(shadowAngle);
            SetLayer(ref result, i, new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height),
                new((float)Math.Clamp(layer.Opacity * (layer.FollowScreenTransform ? 1 - result.BackdropTreatment.X : layer.Layer == int.MinValue + 1 ? 1 - Math.Clamp(scene.OverlayBackdrop?.CameraSuppression ?? 0, 0, 1) : 1), 0, 1), layer.Shape == CameraShape.Circle ? -1 : (float)Math.Clamp(layer.CornerRadius, 0, .5), (float)layer.Rotation, (layer.Mirrored ? 1 : 0) + (layer.FollowScreenTransform ? 2 : 0) + (float)Math.Clamp(layer.Blur, 0, 1) * .25f),
                ValidCrop(layer.SourceCrop), new((float)Math.Clamp(layer.BorderWidth, 0, 60) * (layer.BorderOutside ? -outputWidth / 1920f : (float)outputHeight / Math.Max(scene.CanvasHeight, 1)), ParseColor(layer.BorderColor).X, ParseColor(layer.BorderColor).Y, ParseColor(layer.BorderColor).Z),
                new((float)Math.Clamp(double.IsFinite(layer.ShadowOpacity) ? layer.ShadowOpacity : 0, 0, 1),
                    (float)Math.Clamp(double.IsFinite(layer.ShadowRadius) ? layer.ShadowRadius : 0, 0, 160) * shadowReferenceScale,
                    (float)rotatedOffsetX * shadowReferenceScale, (float)rotatedOffsetY * shadowReferenceScale));
        }
        if (underlay is { } backgroundLayer)
            result.UnderlayStyle = new((float)Math.Clamp(backgroundLayer.Opacity, 0, 1), 1, (float)backgroundLayer.Fit, (float)Math.Clamp(backgroundLayer.Blur, 0, 1) * 100);
        return result;
    }

    /// <summary>Resolved screen card: visible source window, card rectangle (body, excluding the toolbar), scale and 3D pivot.</summary>
    private readonly record struct ScreenCardGeometry(NormalizedPoint Crop, NormalizedPoint CropSize, double Left, double Top, double Width, double Height,
        double TotalScale, double HeaderHeight, double ZoomScale, bool KeepsCanvasCovered, NormalizedPoint PivotAnchor);

    /// <summary>
    /// LayoutModel 1 (projects written before the Mac card model, b39f01f geometry): the card is fitted to the whole source, the
    /// ScreenMotion position is the card centre, zoom crops inside the card (the focus window stretches over the full card) and the
    /// 3D tilt pivots on the card centre.
    /// </summary>
    private static ScreenCardGeometry LegacyScreenCard(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
        double availableWidth, double availableHeight, double bezelBase, double baseHeaderHeight, double reservedHeader, double frameScale)
    {
        var fit = Math.Min(Math.Max(2, availableWidth - bezelBase * 2) / sourceWidth,
            Math.Max(2, availableHeight - reservedHeader - bezelBase * 2) / sourceHeight);
        var screenWidth = sourceWidth * fit; var screenHeight = sourceHeight * fit;
        var contentScale = scene.Canvas is { } canvasSettings ? Math.Clamp(canvasSettings.ContentScale, .1, 4) : 1;
        var position = scene.ScreenMotion?.Position ?? scene.Canvas?.ContentPosition ?? new NormalizedPoint(.5, .5);
        if (!position.IsFinite) position = new(.5, .5);
        var opening = scene.Opening ?? new FrameOpeningState();
        var offsetX = double.IsFinite(opening.OffsetX) ? Math.Clamp(opening.OffsetX, -2, 2) : 0;
        var offsetY = double.IsFinite(opening.OffsetY) ? Math.Clamp(opening.OffsetY, -2, 2) : 0;
        var screenCenterX = Math.Clamp(position.X, 0, 1) * outputWidth + offsetX * outputWidth;
        var screenCenterY = Math.Clamp(position.Y, 0, 1) * outputHeight + offsetY * outputHeight;
        var motionScale = scene.ScreenMotion?.Scale ?? 1;
        var totalScale = Math.Clamp(contentScale * frameScale * (double.IsFinite(motionScale) ? motionScale : 1) * Math.Clamp(opening.Scale, .1, 4), .1, 8);
        var headerHeight = baseHeaderHeight * totalScale;
        screenCenterY += headerHeight * .5;
        screenWidth *= totalScale; screenHeight *= totalScale;
        var left = screenCenterX - screenWidth * .5; var top = screenCenterY - screenHeight * .5;
        var crop = scene.CropOrigin.IsFinite && scene.CropSize.IsFinite ? scene.CropOrigin : new NormalizedPoint(0, 0);
        var cropSize = scene.CropSize.IsFinite ? scene.CropSize : new NormalizedPoint(1, 1);
        if (scene.Canvas is { } c)
        {
            crop = new(c.Crop.X + crop.X * c.Crop.Width, c.Crop.Y + crop.Y * c.Crop.Height);
            cropSize = new(cropSize.X * c.Crop.Width, cropSize.Y * c.Crop.Height);
        }
        cropSize = new(Math.Clamp(cropSize.X, .0001, 1), Math.Clamp(cropSize.Y, .0001, 1));
        var zoom = Math.Max(double.IsFinite(scene.Zoom) ? scene.Zoom : 1, 1);
        // Crop size is authoritative when supplied; zoom remains available to consumers and helps guard legacy scenes.
        if (cropSize.X >= .9999 && cropSize.Y >= .9999 && zoom > 1.0001) cropSize = new(1 / zoom, 1 / zoom);
        crop = new(Math.Clamp(crop.X, 0, 1 - cropSize.X), Math.Clamp(crop.Y, 0, 1 - cropSize.Y));
        return new(crop, cropSize, left, top, screenWidth, screenHeight, totalScale, headerHeight, 1, false, new(.5, .5));
    }

    /// <summary>
    /// LayoutModel 2 (Mac card model): the cropped picture keeps its own aspect, the position is resolved through placement/focus framing,
    /// zoom magnifies the whole card (ZoomViewportTransform) and the 3D tilt pivots on the authored point.
    /// </summary>
    private static ScreenCardGeometry MacScreenCard(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
        double availableWidth, double availableHeight, double bezelBase, double baseHeaderHeight, double reservedHeader, double frameScale,
        ScreenFrameKind frame, float deviceKind)
    {
        // The source crop is only the user's canvas crop; zoom magnifies the card instead of cropping inside it.
        var crop = new NormalizedPoint(0, 0);
        var cropSize = new NormalizedPoint(1, 1);
        if (scene.Canvas is { } c)
        {
            crop = new(c.Crop.X, c.Crop.Y);
            cropSize = new(c.Crop.Width, c.Crop.Height);
        }
        cropSize = new(Math.Clamp(cropSize.X, .0001, 1), Math.Clamp(cropSize.Y, .0001, 1));
        crop = new(Math.Clamp(crop.X, 0, 1 - cropSize.X), Math.Clamp(crop.Y, 0, 1 - cropSize.Y));
        // Mac: the cropped picture keeps its own aspect ratio, so the fit is based on the cropped extent, not the whole source.
        var croppedWidth = Math.Max(sourceWidth * cropSize.X, 1e-3); var croppedHeight = Math.Max(sourceHeight * cropSize.Y, 1e-3);
        var fit = Math.Min(Math.Max(2, availableWidth - bezelBase * 2) / croppedWidth,
            Math.Max(2, availableHeight - reservedHeader - bezelBase * 2) / croppedHeight);
        var fittedWidth = croppedWidth * fit; var fittedHeight = croppedHeight * fit;
        var contentScale = scene.Canvas is { } canvasSettings ? Math.Clamp(canvasSettings.ContentScale, .1, 4) : 1;
        var opening = scene.Opening ?? new FrameOpeningState();
        var offsetX = double.IsFinite(opening.OffsetX) ? Math.Clamp(opening.OffsetX, -2, 2) : 0;
        var offsetY = double.IsFinite(opening.OffsetY) ? Math.Clamp(opening.OffsetY, -2, 2) : 0;

        // Placement (Mac CompositionScene.evaluate): position addresses the card edge-to-edge (placement framing) or a point of
        // interest (focus framing) and is resolved to a pixel offset from the canvas centre. State scale is absolute and the
        // frame scale multiplies the whole card, as before.
        var contentPosition = scene.Canvas?.ContentPosition ?? new NormalizedPoint(.5, .5);
        var baseState = new ScreenMotionState { Position = contentPosition.IsFinite ? contentPosition : new(.5, .5), Scale = contentScale };
        var activeState = scene.ScreenMotion is { } motionSample && motionSample.Position.IsFinite ? motionSample : null;
        var blend = scene.ScreenBlend
            ?? (activeState is not null ? new ScreenMotionBlend(activeState, activeState, ScreenMotionFraming.Focus, ScreenMotionFraming.Focus, 0, activeState.Position)
                : new ScreenMotionBlend(baseState, baseState, ScreenMotionFraming.Placement, ScreenMotionFraming.Placement, 0, baseState.Position));
        var stateScale = activeState is not null && double.IsFinite(activeState.Scale) ? activeState.Scale : contentScale;
        var manualScale = Math.Clamp(stateScale * frameScale, .1, 8);
        var devicePadding = deviceKind > 0 ? bezelBase : 0;
        var anchorViewport = new ScreenAnchorViewport(outputWidth, outputHeight, fittedWidth, fittedHeight,
            baseHeaderHeight + devicePadding, devicePadding, devicePadding, devicePadding, frameScale);
        var (blendOffsetX, blendOffsetY) = ScreenMotionLayout.BlendOffset(blend, anchorViewport);
        var baseCenterX = outputWidth / 2d + blendOffsetX; var baseCenterY = outputHeight / 2d + blendOffsetY;
        var baseWidth = fittedWidth * manualScale; var baseHeight = fittedHeight * manualScale;

        // Zoom (Mac ZoomViewportTransform + CompositionScene.swift:278-325): the complete card is magnified about its own centre
        // and shifted by the framed viewport translation. Only a card that already covers the whole canvas is clamped to it.
        var zoomSample = scene.ZoomSample ?? LegacyZoomSample(scene);
        var tolerance = Math.Max(outputWidth, outputHeight) * 1e-9;
        var left0 = baseCenterX - baseWidth / 2; var top0 = baseCenterY - baseHeight / 2;
        var keepsCanvasCovered = frame == ScreenFrameKind.None && scene.ScreenMotion is null && deviceKind == 0
            && left0 <= tolerance && top0 <= tolerance && left0 + baseWidth >= outputWidth - tolerance && top0 + baseHeight >= outputHeight - tolerance;
        NormalizedRect? covering = keepsCanvasCovered ? new NormalizedRect(-left0 / baseWidth, -top0 / baseHeight, outputWidth / baseWidth, outputHeight / baseHeight) : null;
        var viewport = ZoomViewportTransform.Make(zoomSample, covering);
        var zoomScale = viewport.Scale;
        var cardCenterX = baseCenterX + viewport.Translation.X * baseWidth;
        // The toolbar hangs above the content rectangle; keep the complete card centred while it grows.
        var cardCenterY = baseCenterY + viewport.Translation.Y * baseHeight - baseHeaderHeight * manualScale / 2;
        cardCenterX += offsetX * outputWidth; cardCenterY += offsetY * outputHeight;
        var totalScale = Math.Clamp(manualScale * zoomScale * Math.Clamp(opening.Scale, .1, 4), .1, 8);
        var headerHeight = baseHeaderHeight * totalScale;
        var screenWidth = fittedWidth * totalScale; var screenHeight = fittedHeight * totalScale;
        var screenCenterX = cardCenterX; var screenCenterY = cardCenterY + headerHeight * .5;
        var left = screenCenterX - screenWidth * .5; var top = screenCenterY - screenHeight * .5;
        // Projection pivot: the authored point inside the recorded content stays fixed while the plane tilts (Mac projectionAnchor).
        var pivotAnchor = blend.ProjectionAnchor.IsFinite ? blend.ProjectionAnchor : new NormalizedPoint(.5, .5);
        return new(crop, cropSize, left, top, screenWidth, screenHeight, totalScale, headerHeight, zoomScale, keepsCanvasCovered, pivotAnchor);
    }

    /// <summary>Scenes built before ZoomSample existed expressed zoom as a focus crop window; frame the same focus at the same magnification.</summary>
    private static ZoomSampleState LegacyZoomSample(FrameScene scene)
    {
        if (scene.CropSize.IsFinite && scene.CropOrigin.IsFinite && scene.CropSize.X is > .0001 and < .9999)
            return new ZoomSampleState(1 / scene.CropSize.X, new(scene.CropOrigin.X + scene.CropSize.X / 2, scene.CropOrigin.Y + scene.CropSize.Y / 2), 1);
        return double.IsFinite(scene.Zoom) && scene.Zoom > 1.0001 ? new ZoomSampleState(scene.Zoom, new(.5, .5), 1) : ZoomSampleState.Identity;
    }

    /// <summary>
    /// Card-local homography for the 3D tilt. <paramref name="pivotAnchor"/> is the recorded-content point (0..1 inside the screen)
    /// that stays fixed while the plane tilts (Mac projectionAnchor): the tilt is conjugated by a translation to the pivot,
    /// M = T(-p) * H * T(p) in the row-vector convention used by the shader and the mapping helpers.
    /// </summary>
    private static (Vector4 Row0, Vector4 Row1, Vector4 Row2) MakeHomography(ScreenMotionState s, NormalizedPoint? pivotAnchor = null)
    {
        var rx = Math.Clamp(s.RotationX, -80, 80) * Math.PI / 180; var ry = Math.Clamp(s.RotationY, -80, 80) * Math.PI / 180;
        var cx = Math.Cos(rx); var sx = Math.Sin(rx); var cy = Math.Cos(ry); var sy = Math.Sin(ry); var p = Math.Clamp(s.Perspective, 0, 2);
        // Rows are stored for HLSL's row-vector mul(float3, float3x3). Perspective terms therefore
        // belong in the Z column so the projected denominator varies with X/Y.
        double a = cy, b = 0, c = -p * sy, d = p * sy * sx, e = cx, f = p * cy * sx;
        var pivot = pivotAnchor is { IsFinite: true } anchor ? anchor : new NormalizedPoint(.5, .5);
        var px = Math.Clamp(pivot.X, 0, 1) * 2 - 1; var py = Math.Clamp(pivot.Y, 0, 1) * 2 - 1;
        // H * T(p) shifts each row r to (r0 + r2*px, r1 + r2*py, r2); T(-p) then subtracts px/py times the first two rows from the translation row.
        double r00 = a + c * px, r01 = b + c * py, r02 = c, r10 = d + f * px, r11 = e + f * py, r12 = f;
        var g = px - px * r00 - py * r10; var h = py - px * r01 - py * r11; var i = 1 - px * r02 - py * r12;
        return (new((float)r00, (float)r01, (float)r02, 0), new((float)r10, (float)r11, (float)r12, 0), new((float)g, (float)h, (float)i, 0));
    }

    private static (Vector4 Row0, Vector4 Row1, Vector4 Row2) InvertHomography((Vector4 Row0, Vector4 Row1, Vector4 Row2) m)
    {
        var a=m.Row0.X; var b=m.Row0.Y; var c=m.Row0.Z; var d=m.Row1.X; var e=m.Row1.Y; var f=m.Row1.Z; var g=m.Row2.X; var h=m.Row2.Y; var i=m.Row2.Z;
        var A=e*i-f*h; var B=c*h-b*i; var C=b*f-c*e; var D=f*g-d*i; var E=a*i-c*g; var F=c*d-a*f; var G=d*h-e*g; var H=b*g-a*h; var I=a*e-b*d;
        var determinant=a*A+b*D+c*G;
        if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-6) return (new(1,0,0,0),new(0,1,0,0),new(0,0,1,0));
        var scale=1/determinant;
        return (new(A*scale,B*scale,C*scale,0),new(D*scale,E*scale,F*scale,0),new(G*scale,H*scale,I*scale,0));
    }

    private static void SetLayer(ref ShaderConstants c, int i, Vector4 rect, Vector4 style, Vector4 crop, Vector4 border, Vector4 shadow)
    {
        switch (i)
        {
            case 0: c.LayerRect0=rect; c.LayerStyle0=style; c.LayerCrop0=crop; c.LayerBorder0=border; c.LayerShadow0=shadow; break; case 1: c.LayerRect1=rect; c.LayerStyle1=style; c.LayerCrop1=crop; c.LayerBorder1=border; c.LayerShadow1=shadow; break;
            case 2: c.LayerRect2=rect; c.LayerStyle2=style; c.LayerCrop2=crop; c.LayerBorder2=border; c.LayerShadow2=shadow; break; case 3: c.LayerRect3=rect; c.LayerStyle3=style; c.LayerCrop3=crop; c.LayerBorder3=border; c.LayerShadow3=shadow; break;
            case 4: c.LayerRect4=rect; c.LayerStyle4=style; c.LayerCrop4=crop; c.LayerBorder4=border; c.LayerShadow4=shadow; break; case 5: c.LayerRect5=rect; c.LayerStyle5=style; c.LayerCrop5=crop; c.LayerBorder5=border; c.LayerShadow5=shadow; break;
            case 6: c.LayerRect6=rect; c.LayerStyle6=style; c.LayerCrop6=crop; c.LayerBorder6=border; c.LayerShadow6=shadow; break; case 7: c.LayerRect7=rect; c.LayerStyle7=style; c.LayerCrop7=crop; c.LayerBorder7=border; c.LayerShadow7=shadow; break;
        }
    }

    private static Vector4 ValidCrop(NormalizedRect? crop)
    {
        var r = crop ?? NormalizedRect.Full;
        if (!r.IsFinite || r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1 || r.Y + r.Height > 1)
            throw new ArgumentException("图层素材裁切范围无效。", nameof(crop));
        return new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    private LayerSlot EnsureLayerSlot(int index, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new ArgumentOutOfRangeException(nameof(width), "图层尺寸无效。");
        var existing = layerSlots[index];
        if (existing is { } slot && slot.Width == width && slot.Height == height) return slot;
        existing?.Dispose();
        var texture = device!.CreateTexture2D(TextureDescription(width, height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        var created = new LayerSlot(width, height, texture, device.CreateShaderResourceView(texture));
        layerSlots[index] = created;
        return created;
    }

    private static Vector4 BlurRect(FrameScene scene, int index)
    {
        var list = scene.BlurRegions;
        if (list is null || index >= list.Count) return Vector4.Zero;
        var item = list[index]; var rect = item.Rect;
        return new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
    }
    private static Vector4 BlurStyle(FrameScene scene, int index)
    {
        var list = scene.BlurRegions;
        if (list is null || index >= list.Count) return Vector4.Zero;
        var item = list[index];
        return new((float)(item.Kind == BlurKind.Spotlight ? item.SpotlightDimming : item.Strength), (float)item.Kind + (item.SourceSpace ? 4 : 0), (float)item.CornerRadius, (float)item.Opacity);
    }

    private static Texture2DDescription TextureDescription(int width, int height, Format format, BindFlags bind, ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
        Format = format, SampleDescription = new SampleDescription(1, 0),
        Usage = usage, BindFlags = bind, CPUAccessFlags = cpu, MiscFlags = ResourceOptionFlags.None
    };

    private static Vector4 ParseColor(string? color)
    {
        if (color is not null && Regex.Match(color, "^#([0-9A-Fa-f]{6})$") is { Success: true } m)
        {
            var value = Convert.ToUInt32(m.Groups[1].Value, 16);
            return new(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1);
        }
        return new(.125f, .129f, .141f, 1);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        context?.ClearState(); context?.Flush();
        foreach (var cursor in cursorSlots.Values) cursor.Dispose();
        cursorSlots.Clear();
        foreach (var slot in layerSlots) slot?.Dispose();
        foreach (var target in compositeTargets) target?.Dispose();
        constantsBuffer?.Dispose(); sampler?.Dispose(); pixelShader?.Dispose(); vertexShader?.Dispose();
        targetView?.Dispose(); sourceView?.Dispose(); stagingTexture?.Dispose(); targetTexture?.Dispose(); sourceTexture?.Dispose();
        context?.Dispose(); device?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Vector4 OutputSourceSize;
        public Vector4 Crop;
        public Vector4 ScreenRect;
        public Vector4 Style;
        public Vector4 Cursor; // normalized source position, visible flag, 1080-short-edge size scale
        public Vector4 CursorDynamics; // hotspot rotation radians, click progress (-1 uses legacy strength), click scale, click opacity
        public Vector4 Background;
        public Vector4 Style2; // gradient angle, cursor style, click effect style, reserved
        public Vector4 BackgroundEnd;
        public Vector4 FrameStyle;
        public Vector4 DeviceFrame; // bezel px, outer radius px, rim width px, kind (1 phone, 2 tablet)
        public Vector4 FrameColor;
        public Vector4 Motion;
        public Vector4 Transform0, Transform1, Transform2;
        public Vector4 Inverse0, Inverse1, Inverse2;
        public Vector4 ClickColor;
        public Vector4 Focus; // source normalized center, region size, blur amount
        public Vector4 FocusStyle; // shape, angle, softness, dimming
        public Vector4 FocusProgress;
        public Vector4 BlurRect0, BlurRect1, BlurRect2, BlurRect3;
        public Vector4 BlurStyle0, BlurStyle1, BlurStyle2, BlurStyle3;
        public Vector4 LayerCount;
        public Vector4 LayerRect0, LayerRect1, LayerRect2, LayerRect3, LayerRect4, LayerRect5, LayerRect6, LayerRect7;
        public Vector4 LayerStyle0, LayerStyle1, LayerStyle2, LayerStyle3, LayerStyle4, LayerStyle5, LayerStyle6, LayerStyle7;
        public Vector4 UnderlayStyle;
        public Vector4 BackgroundPattern;
        public Vector4 PatternColor;
        public Vector4 BackgroundAnimation;
        public Vector4 LayerCrop0, LayerCrop1, LayerCrop2, LayerCrop3, LayerCrop4, LayerCrop5, LayerCrop6, LayerCrop7;
        public Vector4 LayerBorder0, LayerBorder1, LayerBorder2, LayerBorder3, LayerBorder4, LayerBorder5, LayerBorder6, LayerBorder7;
        public Vector4 CursorAssetInfo; // width/height and hotspot in 44px arrow-height units
        public Vector4 LayerShadow0, LayerShadow1, LayerShadow2, LayerShadow3, LayerShadow4, LayerShadow5, LayerShadow6, LayerShadow7;
        public Vector4 BackdropTreatment, BackdropPass;
        public Vector4 SpotlightControl;
    }

    private sealed record LayerSlot(int Width, int Height, ID3D11Texture2D Texture, ID3D11ShaderResourceView View) : IDisposable
    { public void Dispose() { View.Dispose(); Texture.Dispose(); } }

    private sealed record CompositeTarget(ID3D11Texture2D Texture, ID3D11RenderTargetView Target, ID3D11ShaderResourceView View) : IDisposable
    { public void Dispose() { View.Dispose(); Target.Dispose(); Texture.Dispose(); } }

    private sealed record CursorSlot(SystemCursorRaster Raster, ID3D11Texture2D Texture, ID3D11ShaderResourceView View) : IDisposable
    { public void Dispose() { View.Dispose(); Texture.Dispose(); } }

    private static readonly string ShaderSource = ReadShaderSource();
    private static readonly Lazy<byte[]> VertexBytecode = new(() => ShaderBytecodeCache.GetOrCompile(
        ShaderSource, "VSMain", "vs_5_0",
        () => Compiler.Compile(ShaderSource, "VSMain", "FrameTrail.Scene.hlsl", "vs_5_0").ToArray()));
    private static readonly Lazy<byte[]> PixelBytecode = new(() => ShaderBytecodeCache.GetOrCompile(
        ShaderSource, "PSMain", "ps_5_0",
        () => Compiler.Compile(ShaderSource, "PSMain", "FrameTrail.Scene.hlsl", "ps_5_0").ToArray()));
    private static readonly Lazy<Task> ShaderWarmUp = new(() => Task.Run(() =>
    {
        _ = VertexBytecode.Value;
        _ = PixelBytecode.Value;
    }));

    public static Task WarmUpShadersAsync() => ShaderWarmUp.Value;

    private static string ReadShaderSource()
    {
        using var stream = typeof(GpuFrameRenderer).Assembly.GetManifestResourceStream("FrameTrail.Media.Scene.hlsl")
            ?? throw new InvalidOperationException("内嵌 GPU 场景 shader 缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
