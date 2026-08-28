using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using RemoteDesktop.App.Protocol;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RemoteDesktop.App.Services;

public sealed class FrameEncoder : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastEncodedUtc = DateTime.MinValue;

    public byte[] EncodeFrame(IDirect3DSurface surface, int width, int height)
    {
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / RemoteConstants.TargetFps);
        if (DateTime.UtcNow - _lastEncodedUtc < minInterval)
        {
            return [];
        }

        if (!_gate.Wait(0))
        {
            return [];
        }

        try
        {
            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask().GetAwaiter().GetResult();
            using (bitmap)
            {
                var jpeg = EncodeJpeg(bitmap);
                _lastEncodedUtc = DateTime.UtcNow;
                return jpeg;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static byte[] EncodeJpeg(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.IsThumbnailGenerated = false;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        stream.Seek(0);
        var output = new byte[stream.Size];
        stream.ReadAsync(output.AsBuffer(), (uint)output.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return output;
    }
}
