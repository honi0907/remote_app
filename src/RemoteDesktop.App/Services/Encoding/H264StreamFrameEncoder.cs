using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using RemoteDesktop.App.Services.StreamEncoding.H264;
using Windows.Graphics.DirectX.Direct3D11;

namespace RemoteDesktop.App.Services.StreamEncoding;

public sealed class H264StreamFrameEncoder : IStreamFrameEncoder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly H264Encoder _encoder = new();
    private DateTime _lastEncodedUtc = DateTime.MinValue;
    private int _configuredWidth;
    private int _configuredHeight;
    private bool _hasSentFrame;

    public StreamCodec Codec => StreamCodec.H264;

    public EncodedStreamFrame EncodeFrame(IDirect3DSurface surface, int sourceWidth, int sourceHeight)
    {
        var settings = HostSettingsStore.GetEffectiveSettings();
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / settings.TargetFps);
        if (_hasSentFrame && DateTime.UtcNow - _lastEncodedUtc < minInterval)
        {
            return Empty();
        }

        if (!_gate.Wait(0))
        {
            return Empty();
        }

        try
        {
            var (width, height) = SurfaceBitmapHelper.GetScaledDimensions(
                sourceWidth,
                sourceHeight,
                settings.MaxCaptureWidth);
            width = AlignEven(width);
            height = AlignEven(height);

            EnsureEncoder(width, height, settings.TargetFps);

            using var bitmap = SurfaceBitmapHelper.CopySurfaceToBitmap(surface);
            var bgra = ScaleBgra(SurfaceBitmapHelper.ExtractBgra(bitmap), bitmap.PixelWidth, bitmap.PixelHeight, width, height);
            var nv12 = Nv12Converter.BgraToNv12(bgra, width, height);
            var (payload, isKeyframe) = EncodeUntilOutput(nv12);
            if (payload.Length == 0)
            {
                return Empty();
            }

            _hasSentFrame = true;
            _lastEncodedUtc = DateTime.UtcNow;
            var metadata = new FrameMetadata(width, height, DateTime.UtcNow.Ticks);
            return new EncodedStreamFrame(StreamCodec.H264, metadata, payload, isKeyframe);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _gate.Dispose();
    }

    private void EnsureEncoder(int width, int height, int fps)
    {
        if (_configuredWidth == width && _configuredHeight == height)
        {
            return;
        }

        var bitrateKbps = EstimateBitrateKbps(width, height, fps);
        _encoder.Initialize(width, height, fps, bitrateKbps);
        _configuredWidth = width;
        _configuredHeight = height;
        _hasSentFrame = false;
    }

    private (byte[] Payload, bool IsKeyframe) EncodeUntilOutput(byte[] nv12)
    {
        var rounds = _hasSentFrame ? 1 : 24;
        for (var i = 0; i < rounds; i++)
        {
            var (payload, isKeyframe) = _encoder.EncodeNv12(nv12);
            if (payload.Length == 0)
            {
                continue;
            }

            if (_hasSentFrame || isKeyframe)
            {
                return (payload, isKeyframe);
            }
        }

        return ([], false);
    }

    private static int EstimateBitrateKbps(int width, int height, int fps)
    {
        var pixels = (long)width * height;
        return (int)Math.Clamp(pixels * fps / 120_000, 2_000, 12_000);
    }

    private static byte[] ScaleBgra(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            return source;
        }

        var destination = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / width);
                var sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                var destIndex = (y * width + x) * 4;
                destination[destIndex] = source[sourceIndex];
                destination[destIndex + 1] = source[sourceIndex + 1];
                destination[destIndex + 2] = source[sourceIndex + 2];
                destination[destIndex + 3] = source[sourceIndex + 3];
            }
        }

        return destination;
    }

    private static int AlignEven(int value) => Math.Max(2, value - (value & 1));

    private static EncodedStreamFrame Empty() =>
        new(StreamCodec.H264, new FrameMetadata(0, 0, 0), [], false);
}
