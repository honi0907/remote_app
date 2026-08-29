using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services.StreamEncoding.H264;

namespace RemoteDesktop.App.Services.StreamEncoding;

public sealed class H264StreamFrameDecoder : IDisposable
{
    private readonly H264Decoder _decoder = new();

    public byte[] Decode(EncodedStreamFrame frame)
    {
        if (frame.Codec != StreamCodec.H264 || frame.Payload.Length == 0)
        {
            return [];
        }

        return _decoder.Decode(frame.Payload, frame.Metadata.Width, frame.Metadata.Height);
    }

    public void Dispose() => _decoder.Dispose();
}
