using System.Text.Json;

using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public static class HostSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesktopLAN",
            "host-settings.json");

    public static StreamSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return StreamSettings.CreateDefault();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<StreamSettings>(json);
            return settings ?? StreamSettings.CreateDefault();
        }
        catch (Exception)
        {
            return StreamSettings.CreateDefault();
        }
    }

    public static void Save(StreamSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception)
        {
            // Settings persistence should never block hosting.
        }
    }

    public static StreamSettings GetEffectiveSettings()
    {
        var settings = Load().ResolveEffective();
        settings.DeliveryMode = Load().DeliveryMode;
        return settings;
    }
}
