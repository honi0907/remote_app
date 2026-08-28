using Windows.Storage;

namespace RemoteDesktop.App.Services;

public static class HostSettingsStore
{
    private const string PresetKey = "HostStreamPreset";
    private const string FpsKey = "HostTargetFps";
    private const string MaxWidthKey = "HostMaxCaptureWidth";
    private const string JpegQualityKey = "HostJpegQuality";

    public static StreamSettings Load()
    {
        var local = ApplicationData.Current.LocalSettings;
        var settings = StreamSettings.CreateDefault();

        if (local.Values.TryGetValue(PresetKey, out var presetValue)
            && presetValue is int presetInt
            && Enum.IsDefined(typeof(StreamQualityPreset), presetInt))
        {
            settings.Preset = (StreamQualityPreset)presetInt;
        }

        if (local.Values.TryGetValue(FpsKey, out var fpsValue) && fpsValue is int fps)
        {
            settings.TargetFps = Clamp(fps, 10, 30);
        }

        if (local.Values.TryGetValue(MaxWidthKey, out var widthValue) && widthValue is int maxWidth)
        {
            settings.MaxCaptureWidth = Clamp(maxWidth, 0, 3840);
        }

        if (local.Values.TryGetValue(JpegQualityKey, out var qualityValue) && qualityValue is int quality)
        {
            settings.JpegQuality = Clamp(quality, 30, 95);
        }

        return settings;
    }

    public static void Save(StreamSettings settings)
    {
        var local = ApplicationData.Current.LocalSettings;
        local.Values[PresetKey] = (int)settings.Preset;
        local.Values[FpsKey] = Clamp(settings.TargetFps, 10, 30);
        local.Values[MaxWidthKey] = Clamp(settings.MaxCaptureWidth, 0, 3840);
        local.Values[JpegQualityKey] = Clamp(settings.JpegQuality, 30, 95);
    }

    public static StreamSettings GetEffectiveSettings() => Load().ResolveEffective();

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
