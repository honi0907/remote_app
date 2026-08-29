using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services.StreamEncoding;

public static class StreamFrameEncoderFactory
{
    public static IStreamFrameEncoder Create(StreamDeliveryMode mode, out StreamCodec activeCodec)
    {
        if (mode == StreamDeliveryMode.Jpeg)
        {
            activeCodec = StreamCodec.Jpeg;
            return new JpegStreamFrameEncoder();
        }

        if (mode is StreamDeliveryMode.Auto or StreamDeliveryMode.H264 or StreamDeliveryMode.H265)
        {
            try
            {
                var encoder = new H264StreamFrameEncoder();
                activeCodec = StreamCodec.H264;
                return encoder;
            }
            catch (Exception)
            {
                activeCodec = StreamCodec.Jpeg;
                return new JpegStreamFrameEncoder();
            }
        }

        activeCodec = StreamCodec.Jpeg;
        return new JpegStreamFrameEncoder();
    }
}
