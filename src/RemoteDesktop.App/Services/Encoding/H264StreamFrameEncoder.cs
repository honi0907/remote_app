using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using RemoteDesktop.App.Services.StreamEncoding.H264;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RemoteDesktop.App.Services.StreamEncoding;

public sealed class H264StreamFrameEncoder : IStreamFrameEncoder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly H264Encoder _encoder = new();
    private DateTime _lastEncodedUtc = DateTime.MinValue;
    private int _configuredWidth;
    private int _configuredHeight;

    public StreamCodec Codec => StreamCodec.H264;

    public EncodedStreamFrame EncodeFrame(IDirect3DSurface surface, int sourceWidth, int sourceHeight)
    {
        var settings = HostSettingsStore.GetEffectiveSettings();
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / settings.TargetFps);
        if (DateTime.UtcNow - _lastEncodedUtc < minInterval)
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
            using var scaledBitmap = ScaleBitmap(bitmap, width, height);
            var bgra = SurfaceBitmapHelper.ExtractBgra(scaledBitmap);
            var nv12 = Nv12Converter.BgraToNv12(bgra, width, height);
            var (payload, isKeyframe) = _encoder.EncodeNv12(nv12);
            if (payload.Length == 0)
            {
                return Empty();
            }

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
    }

    private static int EstimateBitrateKbps(int width, int height, int fps)
    {
        var pixels = (long)width * height;
        return (int)Math.Clamp(pixels * fps / 120_000, 2_000, 12_000);
    }

    private static SoftwareBitmap ScaleBitmap(SoftwareBitmap bitmap, int width, int height)
    {
        if (bitmap.PixelWidth == width && bitmap.PixelHeight == height)
        {
            return SoftwareBitmap.Copy(bitmap);
        }

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.BitmapTransform.ScaledWidth = (uint)width;
        encoder.BitmapTransform.ScaledHeight = (uint)height;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        stream.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private static int AlignEven(int value) => Math.Max(2, value - (value & 1));

    private static EncodedStreamFrame Empty() =>
        new(StreamCodec.H264, new FrameMetadata(0, 0, 0), [], false);
}
