using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services.StreamEncoding;
using RemoteDesktop.App.Services.StreamEncoding.H264;

const int width = 640;
const int height = 360;

MediaFoundationRuntime.EnsureStarted();

var encoder = new H264Encoder();
encoder.Initialize(width, height, 24, 2000);

var bgra = CreateTestPattern(width, height);
var nv12 = Nv12Converter.BgraToNv12(bgra, width, height);

byte[]? encoded = null;
for (var i = 0; i < 60 && encoded is null; i++)
{
    var (data, isKeyframe) = encoder.EncodeNv12(nv12);
    if (data.Length > 0 && H264BitstreamHelper.IsDecodableKeyframe(data))
    {
        encoded = data;
        Console.WriteLine($"Encoded on attempt {i + 1}: {data.Length} bytes, keyframe={isKeyframe}");
    }
}

if (encoded is null)
{
    Console.Error.WriteLine("FAIL: encoder produced no decodable output.");
    return 1;
}

using var decoder = new H264StreamFrameDecoder();
var frame = new EncodedStreamFrame(StreamCodec.H264, new FrameMetadata(width, height, DateTime.UtcNow.Ticks), encoded, true);
var decoded = decoder.Decode(frame);
var nonZero = decoded.Bgra.Count(b => b != 0);
Console.WriteLine($"Decode: {decoded.Bgra.Length} bytes ({decoded.Width}x{decoded.Height}), nonZero={nonZero}");

if (decoded.Bgra.Length != width * height * 4 || nonZero < 1000)
{
    Console.Error.WriteLine("FAIL: H.264 roundtrip.");
    return 1;
}

Console.WriteLine("PASS: H.264 encode/decode roundtrip.");
return 0;

static byte[] CreateTestPattern(int width, int height)
{
    var bgra = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            bgra[i] = (byte)(x * 255 / Math.Max(1, width - 1));
            bgra[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
            bgra[i + 2] = 180;
            bgra[i + 3] = 255;
        }
    }

    return bgra;
}
