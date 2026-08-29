using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public enum StreamQualityPreset
{
    Responsive = 0,
    Quality = 1,
    Manual = 2,
}

public sealed class StreamSettings
{
    public const int DefaultResponsiveFps = 24;
    public const int DefaultResponsiveMaxWidth = 1280;
    public const int DefaultResponsiveJpegQuality = 55;

    public const int DefaultQualityFps = 15;
    public const int DefaultQualityMaxWidth = 0;
    public const int DefaultQualityJpegQuality = 80;

    public StreamQualityPreset Preset { get; set; } = StreamQualityPreset.Responsive;

    public StreamDeliveryMode DeliveryMode { get; set; } = StreamDeliveryMode.Auto;

    public int TargetFps { get; set; } = DefaultResponsiveFps;

    /// <summary>0 = native capture resolution.</summary>
    public int MaxCaptureWidth { get; set; } = DefaultResponsiveMaxWidth;

    public int JpegQuality { get; set; } = DefaultResponsiveJpegQuality;

    public StreamSettings Clone() => new()
    {
        Preset = Preset,
        DeliveryMode = DeliveryMode,
        TargetFps = TargetFps,
        MaxCaptureWidth = MaxCaptureWidth,
        JpegQuality = JpegQuality,
    };

    public StreamSettings ResolveEffective()
    {
        var resolved = Preset switch
        {
            StreamQualityPreset.Responsive => new StreamSettings
            {
                Preset = Preset,
                DeliveryMode = DeliveryMode,
                TargetFps = DefaultResponsiveFps,
                MaxCaptureWidth = DefaultResponsiveMaxWidth,
                JpegQuality = DefaultResponsiveJpegQuality,
            },
            StreamQualityPreset.Quality => new StreamSettings
            {
                Preset = Preset,
                DeliveryMode = DeliveryMode,
                TargetFps = DefaultQualityFps,
                MaxCaptureWidth = DefaultQualityMaxWidth,
                JpegQuality = DefaultQualityJpegQuality,
            },
            StreamQualityPreset.Manual => Clone(),
            _ => throw new ArgumentOutOfRangeException(nameof(Preset), Preset, null),
        };

        return resolved;
    }

    public static StreamSettings CreateDefault() => new();
}
