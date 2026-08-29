using System.Runtime.InteropServices.WindowsRuntime;
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

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
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
