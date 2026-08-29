using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services.StreamEncoding;
using RemoteDesktop.App.Services.StreamEncoding.H264;

const int width = 1280;
const int height = 720;
const int frameCount = 80;

MediaFoundationRuntime.EnsureStarted();

var encoder = new H264Encoder();
encoder.Initialize(width, height, 24, 4000);
Console.WriteLine($"Encoder codecapi: {CodecApi.LastStatus}");

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
var predictedHits = 0;
var predictedCount = 0;
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
    if (!isKeyframe)
    {
        predictedCount++;
    }

    if (!decoded.IsEmpty)
    {
        sequentialHits++;
        last = decoded;
        if (!isKeyframe)
        {
            predictedHits++;
        }
    }
}

var sequentialNonZero = last.Bgra.Count(b => b != 0);
Console.WriteLine($"Sequential decode: hits={sequentialHits}/{encodedFrames.Count} p={predictedHits}/{predictedCount} last={last.Width}x{last.Height} nonZero={sequentialNonZero} err={sequentialDecoder.LastError}");

if (sequentialHits == 0 || last.Bgra.Length != width * height * 4 || sequentialNonZero < 1000)
{
    Console.Error.WriteLine("FAIL: sequential H.264 decode.");
    return 1;
}

if (predictedCount == 0 || predictedHits * 2 < predictedCount)
{
    Console.Error.WriteLine($"FAIL: P-frames did not decode ({predictedHits}/{predictedCount}).");
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

const int fullWidth = 1920;
const int fullHeight = 1080;
const int fullCount = 32;
var fullEncoder = new H264Encoder();
fullEncoder.Initialize(fullWidth, fullHeight, 15, 8000);
var fullFrames = new List<(byte[] Data, bool IsKeyframe)>();
for (var i = 0; i < fullCount; i++)
{
    var bgra = CreateTestPattern(fullWidth, fullHeight, i);
    var nv12 = Nv12Converter.BgraToNv12(bgra, fullWidth, fullHeight);
    var encoded = fullEncoder.EncodeNv12(nv12);
    if (encoded.Data.Length > 0)
    {
        fullFrames.Add(encoded);
        Console.WriteLine($"FullEncode[{i}]: {encoded.Data.Length} bytes key={encoded.IsKeyframe}");
    }
}

using var fullDecoder = new H264StreamFrameDecoder();
var fullHits = 0;
var fullPredictedHits = 0;
var fullPredictedCount = 0;
DecodedVideoFrame fullLast = new([], 0, 0);
foreach (var (data, isKeyframe) in fullFrames)
{
    var decoded = fullDecoder.Decode(new EncodedStreamFrame(
        StreamCodec.H264,
        new FrameMetadata(fullWidth, fullHeight, DateTime.UtcNow.Ticks),
        data,
        isKeyframe));
    Console.WriteLine($"FullDecode key={isKeyframe} empty={decoded.IsEmpty} size={decoded.Width}x{decoded.Height} err={fullDecoder.LastError}");
    if (!isKeyframe)
    {
        fullPredictedCount++;
    }

    if (!decoded.IsEmpty)
    {
        fullHits++;
        fullLast = decoded;
        if (!isKeyframe)
        {
            fullPredictedHits++;
        }
    }
}

Console.WriteLine($"Full sequential: hits={fullHits}/{fullFrames.Count} p={fullPredictedHits}/{fullPredictedCount} last={fullLast.Width}x{fullLast.Height}");
if (fullHits == 0 || fullLast.Bgra.Length != fullWidth * fullHeight * 4 || fullPredictedHits * 2 < Math.Max(1, fullPredictedCount))
{
    Console.Error.WriteLine("FAIL: 1920x1080 H.264 decode.");
    return 1;
}

Console.WriteLine("PASS: H.264 sequential encode/decode at 1920x1080.");
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
