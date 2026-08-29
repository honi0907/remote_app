using System.Runtime.InteropServices.WindowsRuntime;
using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RemoteDesktop.App.Services.StreamEncoding;

public sealed class JpegStreamFrameEncoder : IStreamFrameEncoder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastEncodedUtc = DateTime.MinValue;

    public StreamCodec Codec => StreamCodec.Jpeg;

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
            using var bitmap = SurfaceBitmapHelper.CopySurfaceToBitmap(surface);
            var (scaledWidth, scaledHeight) = SurfaceBitmapHelper.GetScaledDimensions(
                sourceWidth,
                sourceHeight,
                settings.MaxCaptureWidth);
            var jpeg = EncodeJpeg(bitmap, (uint)scaledWidth, (uint)scaledHeight, settings);
            _lastEncodedUtc = DateTime.UtcNow;
            var metadata = new FrameMetadata(scaledWidth, scaledHeight, DateTime.UtcNow.Ticks);
            return new EncodedStreamFrame(StreamCodec.Jpeg, metadata, jpeg, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static EncodedStreamFrame Empty() =>
        new(StreamCodec.Jpeg, new FrameMetadata(0, 0, 0), [], false);

    private static byte[] EncodeJpeg(SoftwareBitmap bitmap, uint scaledWidth, uint scaledHeight, StreamSettings settings)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.IsThumbnailGenerated = false;

        if (scaledWidth != bitmap.PixelWidth || scaledHeight != bitmap.PixelHeight)
        {
            encoder.BitmapTransform.ScaledWidth = scaledWidth;
            encoder.BitmapTransform.ScaledHeight = scaledHeight;
        }

        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;

        var quality = Math.Clamp(settings.JpegQuality, 30, 95) / 100.0;
        var properties = new BitmapPropertySet
        {
            {
                "ImageQuality",
                new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Double)
            },
        };
        encoder.BitmapProperties.SetPropertiesAsync(properties).AsTask().GetAwaiter().GetResult();
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        stream.Seek(0);
        var output = new byte[stream.Size];
        stream.ReadAsync(output.AsBuffer(), (uint)output.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return output;
    }
}
