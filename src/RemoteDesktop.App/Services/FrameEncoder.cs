using System.Runtime.InteropServices.WindowsRuntime;
using RemoteDesktop.App.Protocol;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RemoteDesktop.App.Services;

public readonly record struct EncodedFrame(byte[] Jpeg, int Width, int Height);

public sealed class FrameEncoder : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastEncodedUtc = DateTime.MinValue;

    public EncodedFrame EncodeFrame(IDirect3DSurface surface, int width, int height)
    {
        var settings = HostSettingsStore.GetEffectiveSettings();
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / settings.TargetFps);
        if (DateTime.UtcNow - _lastEncodedUtc < minInterval)
        {
            return new EncodedFrame([], 0, 0);
        }

        if (!_gate.Wait(0))
        {
            return new EncodedFrame([], 0, 0);
        }

        try
        {
            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask().GetAwaiter().GetResult();
            using (bitmap)
            {
                var (scaledWidth, scaledHeight) = GetScaledDimensions(width, height, settings.MaxCaptureWidth);
                var jpeg = EncodeJpeg(bitmap, scaledWidth, scaledHeight, settings);
                _lastEncodedUtc = DateTime.UtcNow;
                return new EncodedFrame(jpeg, (int)scaledWidth, (int)scaledHeight);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

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

    private static (uint Width, uint Height) GetScaledDimensions(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (maxWidth <= 0 || sourceWidth <= maxWidth)
        {
            return ((uint)sourceWidth, (uint)sourceHeight);
        }

        var scale = (double)maxWidth / sourceWidth;
        var scaledHeight = (uint)Math.Max(1, Math.Round(sourceHeight * scale));
        return ((uint)maxWidth, scaledHeight);
    }
}
