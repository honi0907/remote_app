using System.Reflection;
using System.Text.Json;
using RemoteDesktop.App.Services;

namespace RemoteDesktop.App.Helpers;

public static class UpdateConfigLoader
{
    public static UpdateConfiguration Load()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var configPath = Path.Combine(baseDir, "Assets", "update-config.json");
            if (!File.Exists(configPath))
            {
                return new UpdateConfiguration();
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<UpdateConfiguration>(json);
            return config ?? new UpdateConfiguration();
        }
        catch (Exception)
        {
            return new UpdateConfiguration();
        }
    }

    public static string GetCurrentVersionLabel()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        return $"v{version.TrimStart('v', 'V')}";
    }
}
