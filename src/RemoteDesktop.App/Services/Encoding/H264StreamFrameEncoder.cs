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
    private int _configuredFps;
    private bool _configuredPreferQuality;
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

            EnsureEncoder(width, height, settings.TargetFps, PreferQuality(settings));

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

    private void EnsureEncoder(int width, int height, int fps, bool preferQuality)
    {
        if (_configuredWidth == width &&
            _configuredHeight == height &&
            _configuredFps == fps &&
            _configuredPreferQuality == preferQuality)
        {
            return;
        }

        var bitrateKbps = EstimateBitrateKbps(width, height, fps, preferQuality);
        _encoder.Initialize(width, height, fps, bitrateKbps, preferQuality);
        _configuredWidth = width;
        _configuredHeight = height;
        _configuredFps = fps;
        _configuredPreferQuality = preferQuality;
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

    private static bool PreferQuality(StreamSettings settings) =>
        settings.Preset == StreamQualityPreset.Quality || settings.MaxCaptureWidth <= 0;

    private static int EstimateBitrateKbps(int width, int height, int fps, bool preferQuality)
    {
        var pixels = (long)width * height;
        var divisor = preferQuality ? 2_400 : 2_200;
        var min = preferQuality ? 12_000 : 8_000;
        var max = preferQuality ? 28_000 : 16_000;
        return (int)Math.Clamp(pixels * fps / divisor, min, max);
    }

    private static byte[] ScaleBgra(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            return source;
        }

        var destination = new byte[width * height * 4];
        var xRatio = (double)(sourceWidth - 1) / Math.Max(1, width - 1);
        var yRatio = (double)(sourceHeight - 1) / Math.Max(1, height - 1);
        for (var y = 0; y < height; y++)
        {
            var sourceY = y * yRatio;
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var fy = sourceY - y0;
            for (var x = 0; x < width; x++)
            {
                var sourceX = x * xRatio;
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var fx = sourceX - x0;
                var destIndex = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var c00 = source[(y0 * sourceWidth + x0) * 4 + channel];
                    var c10 = source[(y0 * sourceWidth + x1) * 4 + channel];
                    var c01 = source[(y1 * sourceWidth + x0) * 4 + channel];
                    var c11 = source[(y1 * sourceWidth + x1) * 4 + channel];
                    var top = c00 + ((c10 - c00) * fx);
                    var bottom = c01 + ((c11 - c01) * fx);
                    destination[destIndex + channel] = (byte)Math.Clamp(top + ((bottom - top) * fy), 0, 255);
                }
            }
        }

        return destination;
    }

    private static int AlignEven(int value) => Math.Max(2, value - (value & 1));

    private static EncodedStreamFrame Empty() =>
        new(StreamCodec.H264, new FrameMetadata(0, 0, 0), [], false);
}
