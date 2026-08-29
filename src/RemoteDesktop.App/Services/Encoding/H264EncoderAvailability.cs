using RemoteDesktop.App.Services.StreamEncoding.H264;

namespace RemoteDesktop.App.Services.StreamEncoding;

internal static class H264EncoderAvailability
{
    private static bool? _isSupported;

    public static bool IsSupported()
    {
        if (_isSupported.HasValue)
        {
            return _isSupported.Value;
        }

        try
        {
            MediaFoundationRuntime.EnsureStarted();
            using var encoder = MediaFoundationTransformFactory.CreateTransform(H264MediaFoundationGuids.H264Encoder);
            using var decoder = MediaFoundationTransformFactory.CreateTransform(H264MediaFoundationGuids.H264Decoder);
            _isSupported = true;
        }
        catch (Exception)
        {
            _isSupported = false;
        }

        return _isSupported.Value;
    }
}
