namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal static class Nv12Converter
{
    public static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        var ySize = width * height;
        var uvSize = ySize / 2;
        var nv12 = new byte[ySize + uvSize];

        var yIndex = 0;
        var uvIndex = ySize;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var bgraIndex = (row * width + col) * 4;
                var b = bgra[bgraIndex];
                var g = bgra[bgraIndex + 1];
                var r = bgra[bgraIndex + 2];

                var y = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                nv12[yIndex++] = y;

                if ((row & 1) == 0 && (col & 1) == 0)
                {
                    var u = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    var v = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                    nv12[uvIndex++] = u;
                    nv12[uvIndex++] = v;
                }
            }
        }

        return nv12;
    }

    public static byte[] Nv12ToBgra(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var yPlaneSize = width * height;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var y = nv12[row * width + col];
                var uvIndex = yPlaneSize + ((row / 2) * width) + ((col / 2) * 2);
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
