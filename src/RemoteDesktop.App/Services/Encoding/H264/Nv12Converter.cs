namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal static class Nv12Converter
{
    public static int GetStride(int width) => (width + 15) & ~15;

    public static int GetBufferSize(int width, int height) => GetStride(width) * height * 3 / 2;

    public static bool TryResolveLayout(
        int bufferLength,
        int preferredWidth,
        int preferredHeight,
        int fallbackWidth,
        int fallbackHeight,
        out int width,
        out int height,
        out int stride)
    {
        foreach (var (candidateWidth, candidateHeight) in new[]
                 {
                     (preferredWidth, preferredHeight),
                     (fallbackWidth, fallbackHeight),
                 })
        {
            if (TryMatch(bufferLength, candidateWidth, candidateHeight, out stride))
            {
                width = candidateWidth;
                height = candidateHeight;
                return true;
            }
        }

        width = 0;
        height = 0;
        stride = 0;
        return false;
    }

    private static bool TryMatch(int bufferLength, int width, int height, out int stride)
    {
        stride = 0;
        if (width <= 0 || height <= 0 || bufferLength <= 0)
        {
            return false;
        }

        var packed = width * height * 3 / 2;
        if (bufferLength == packed)
        {
            stride = width;
            return true;
        }

        var alignedStride = GetStride(width);
        if (bufferLength == alignedStride * height * 3 / 2)
        {
            stride = alignedStride;
            return true;
        }

        var alignedHeight = (height + 15) & ~15;
        if (alignedHeight != height && bufferLength == alignedStride * alignedHeight * 3 / 2)
        {
            stride = alignedStride;
            return true;
        }

        return false;
    }

    public static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        var stride = GetStride(width);
        var nv12 = new byte[GetBufferSize(width, height)];

        var yIndex = 0;
        var uvIndex = stride * height;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var bgraIndex = (row * width + col) * 4;
                var b = bgra[bgraIndex];
                var g = bgra[bgraIndex + 1];
                var r = bgra[bgraIndex + 2];

                var y = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                nv12[yIndex + col] = y;

                if ((row & 1) == 0 && (col & 1) == 0)
                {
                    var u = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    var v = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                    var uvOffset = ((row / 2) * stride) + col;
                    nv12[uvIndex + uvOffset] = u;
                    nv12[uvIndex + uvOffset + 1] = v;
                }
            }

            yIndex += stride;
        }

        return nv12;
    }

    public static byte[] Nv12ToBgra(byte[] nv12, int width, int height, int stride = 0)
    {
        if (stride <= 0)
        {
            stride = GetStride(width);
        }

        var required = stride * height * 3 / 2;
        if (nv12.Length < required)
        {
            throw new ArgumentException(
                $"NV12 buffer is {nv12.Length} bytes, expected at least {required} for {width}x{height} stride {stride}.");
        }

        var bgra = new byte[width * height * 4];
        var yPlaneSize = stride * height;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var y = nv12[(row * stride) + col];
                var uvIndex = yPlaneSize + ((row / 2) * stride) + ((col / 2) * 2);
                var u = nv12[uvIndex];
                var v = nv12[uvIndex + 1];

                var c = y - 16;
                var d = u - 128;
                var e = v - 128;

                var r = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                var g = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                var b = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);

                var bgraIndex = (row * width + col) * 4;
                bgra[bgraIndex] = b;
                bgra[bgraIndex + 1] = g;
                bgra[bgraIndex + 2] = r;
                bgra[bgraIndex + 3] = 255;
            }
        }

        return bgra;
    }
}
