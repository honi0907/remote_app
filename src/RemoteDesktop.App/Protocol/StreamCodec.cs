namespace RemoteDesktop.App.Protocol;

public enum StreamCodec : byte
{
    Jpeg = 0,
    H264 = 1,
    H265 = 2,
}

public enum StreamDeliveryMode
{
    Auto = 0,
    Jpeg = 1,
    H264 = 2,
    H265 = 3,
}

public readonly record struct StreamConfigMessage(
    StreamCodec Codec,
    int TargetFps,
    int MaxCaptureWidth,
    int JpegQuality);

public readonly record struct EncodedStreamFrame(
    StreamCodec Codec,
    FrameMetadata Metadata,
    byte[] Payload,
    bool IsKeyframe);
