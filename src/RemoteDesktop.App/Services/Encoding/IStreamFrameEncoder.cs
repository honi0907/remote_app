using System.Runtime.InteropServices;
using RemoteDesktop.App.Protocol;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace RemoteDesktop.App.Services.StreamEncoding;

public interface IStreamFrameEncoder : IDisposable
{
    StreamCodec Codec { get; }

    EncodedStreamFrame EncodeFrame(IDirect3DSurface surface, int sourceWidth, int sourceHeight);
}

internal static class SurfaceBitmapHelper
{
    public static SoftwareBitmap CopySurfaceToBitmap(IDirect3DSurface surface)
    {
        return SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask().GetAwaiter().GetResult();
    }

    public static byte[] ExtractBgra(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return ExtractBgra(converted);
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        var stride = plane.Stride;

        using var reference = buffer.CreateReference();
        unsafe
        {
            var byteAccess = (IMemoryBufferByteAccess)reference;
            byteAccess.GetBuffer(out var data, out _);

            if (stride == width * 4)
            {
                Marshal.Copy((IntPtr)data, pixels, 0, pixels.Length);
                return pixels;
            }

            for (var row = 0; row < height; row++)
            {
                Marshal.Copy((IntPtr)(data + (row * stride)), pixels, row * width * 4, width * 4);
            }
        }

        return pixels;
    }

    public static (int Width, int Height) GetScaledDimensions(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (maxWidth <= 0 || sourceWidth <= maxWidth)
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = (double)maxWidth / sourceWidth;
        var scaledHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (maxWidth, scaledHeight);
    }
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
