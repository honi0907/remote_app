using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services.StreamEncoding;
using RemoteDesktop.App.Services.StreamEncoding.H264;

const int width = 1280;
const int height = 720;
const int frameCount = 80;

MediaFoundationRuntime.EnsureStarted();

var encoder = new H264Encoder();
encoder.Initialize(width, height, 24, 4000);

var encodedFrames = new List<(byte[] Data, bool IsKeyframe)>();
for (var i = 0; i < frameCount; i++)
{
    var bgra = CreateTestPattern(width, height, i);
    var nv12 = Nv12Converter.BgraToNv12(bgra, width, height);
    var encoded = encoder.EncodeNv12(nv12);
    if (encoded.Data.Length > 0)
    {
        encodedFrames.Add(encoded);
        Console.WriteLine($"Encode[{i}]: {encoded.Data.Length} bytes key={encoded.IsKeyframe}");
    }
}

if (encodedFrames.Count == 0 || !encodedFrames.Any(f => f.IsKeyframe))
{
    Console.Error.WriteLine("FAIL: encoder produced no keyframe.");
    return 1;
}

using var sequentialDecoder = new H264StreamFrameDecoder();
var sequentialHits = 0;
DecodedVideoFrame last = new([], 0, 0);
foreach (var (data, isKeyframe) in encodedFrames)
{
    var frame = new EncodedStreamFrame(
        StreamCodec.H264,
        new FrameMetadata(width, height, DateTime.UtcNow.Ticks),
        data,
        isKeyframe);
    var decoded = sequentialDecoder.Decode(frame);
    Console.WriteLine($"Decode key={isKeyframe} empty={decoded.IsEmpty} size={decoded.Width}x{decoded.Height} err={sequentialDecoder.LastError}");
    if (!decoded.IsEmpty)
    {
        sequentialHits++;
        last = decoded;
    }
}

var sequentialNonZero = last.Bgra.Count(b => b != 0);
Console.WriteLine($"Sequential decode: hits={sequentialHits}/{encodedFrames.Count} last={last.Width}x{last.Height} nonZero={sequentialNonZero} err={sequentialDecoder.LastError}");

if (sequentialHits == 0 || last.Bgra.Length != width * height * 4 || sequentialNonZero < 1000)
{
    Console.Error.WriteLine("FAIL: sequential H.264 decode.");
    return 1;
}

using var latestOnlyDecoder = new H264StreamFrameDecoder();
var latest = encodedFrames[^1];
var latestDecoded = latestOnlyDecoder.Decode(new EncodedStreamFrame(
    StreamCodec.H264,
    new FrameMetadata(width, height, DateTime.UtcNow.Ticks),
    latest.Data,
    latest.IsKeyframe));
Console.WriteLine(
    $"Latest-only decode: key={latest.IsKeyframe} empty={latestDecoded.IsEmpty} err={latestOnlyDecoder.LastError}");

Console.WriteLine("PASS: H.264 sequential encode/decode at 1280x720.");
return 0;

static byte[] CreateTestPattern(int width, int height, int offset)
{
    var bgra = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            bgra[i] = (byte)((x + offset) * 255 / Math.Max(1, width - 1));
            bgra[i + 1] = (byte)((y + offset) * 255 / Math.Max(1, height - 1));
            bgra[i + 2] = 180;
            bgra[i + 3] = 255;
        }
    }

    return bgra;
}
