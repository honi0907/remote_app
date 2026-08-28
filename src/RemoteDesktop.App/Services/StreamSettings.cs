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

    public int TargetFps { get; set; } = DefaultResponsiveFps;

    /// <summary>0 = native capture resolution.</summary>
    public int MaxCaptureWidth { get; set; } = DefaultResponsiveMaxWidth;

    public int JpegQuality { get; set; } = DefaultResponsiveJpegQuality;

    public StreamSettings Clone() => new()
    {
        Preset = Preset,
        TargetFps = TargetFps,
        MaxCaptureWidth = MaxCaptureWidth,
        JpegQuality = JpegQuality,
    };

    public StreamSettings ResolveEffective()
    {
        return Preset switch
        {
            StreamQualityPreset.Responsive => new StreamSettings
            {
                Preset = Preset,
                TargetFps = DefaultResponsiveFps,
                MaxCaptureWidth = DefaultResponsiveMaxWidth,
                JpegQuality = DefaultResponsiveJpegQuality,
            },
            StreamQualityPreset.Quality => new StreamSettings
            {
                Preset = Preset,
                TargetFps = DefaultQualityFps,
                MaxCaptureWidth = DefaultQualityMaxWidth,
                JpegQuality = DefaultQualityJpegQuality,
            },
            StreamQualityPreset.Manual => Clone(),
            _ => throw new ArgumentOutOfRangeException(nameof(Preset), Preset, null),
        };
    }

    public static StreamSettings CreateDefault() => new();
}
