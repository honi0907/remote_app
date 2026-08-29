using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services.StreamEncoding.H264;

namespace RemoteDesktop.App.Services.StreamEncoding;

public readonly record struct DecodedVideoFrame(byte[] Bgra, int Width, int Height)
{
    public bool IsEmpty => Bgra.Length == 0;
}

public sealed class H264StreamFrameDecoder : IDisposable
{
    private readonly H264Decoder _decoder = new();
    private int _pendingWarmupFrames;

    public DecodedVideoFrame Decode(EncodedStreamFrame frame)
    {
        if (frame.Codec != StreamCodec.H264 || frame.Payload.Length == 0)
        {
            return new DecodedVideoFrame([], 0, 0);
        }

        var (bgra, width, height) = _decoder.Decode(frame.Payload, frame.Metadata.Width, frame.Metadata.Height);
        if (bgra.Length > 0)
        {
            _pendingWarmupFrames = 0;
            return new DecodedVideoFrame(bgra, width, height);
        }

        if (_pendingWarmupFrames < 30)
        {
            _pendingWarmupFrames++;
        }

        return new DecodedVideoFrame([], 0, 0);
    }

    public void Reset() => _pendingWarmupFrames = 0;

    public void Dispose() => _decoder.Dispose();
}
