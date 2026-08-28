using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using RemoteDesktop.App.Helpers;

namespace RemoteDesktop.App.Services;

public sealed class GitHubUpdateService : IUpdateService
{
    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "RemoteDesktopLAN-Updater" } },
    };

    public UpdateConfiguration Configuration { get; }

    public GitHubUpdateService()
    {
        Configuration = UpdateConfigLoader.Load();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();
        var url = $"https://api.github.com/repos/{Configuration.GitHubOwner}/{Configuration.GitHubRepo}/releases/latest";
        var release = await HttpClient.GetFromJsonAsync<GitHubRelease>(url, cancellationToken);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return new UpdateCheckResult(false, current, null, null, null, null);
        }

        var latest = ParseVersion(release.TagName);
        if (latest is null)
        {
            return new UpdateCheckResult(false, current, null, release.Body, null, null);
        }

        var asset = FindInstallerAsset(release.Assets);
        var updateAvailable = latest > current;
        return new UpdateCheckResult(
            updateAvailable,
            current,
            latest,
            release.Body,
            asset?.BrowserDownloadUrl,
            asset?.Name);
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.AssetName))
        {
            return new UpdateDownloadResult(false, null, "ダウンロード URL が見つかりません。");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "RemoteDesktopLAN", "Updates");
        Directory.CreateDirectory(downloadDir);
        var installerPath = Path.Combine(downloadDir, update.AssetName);

        try
        {
            using var response = await HttpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(installerPath);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0)
                {
                    progress?.Report(readTotal / (double)total);
                }
            }

            return new UpdateDownloadResult(true, installerPath, null);
        }
        catch (Exception ex)
        {
            return new UpdateDownloadResult(false, null, ex.Message);
        }
    }

    public void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("インストーラーが見つかりません。", installerPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        });
    }

    private GitHubReleaseAsset? FindInstallerAsset(IReadOnlyList<GitHubReleaseAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        var pattern = Configuration.InstallerAssetPattern.Replace("*", "", StringComparison.Ordinal);
        return assets.FirstOrDefault(a => a.Name.Contains("RemoteDesktopLAN-Setup", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private static Version GetCurrentVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseVersion(informational ?? "0.0.0") ?? new Version(0, 0, 0);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimStart('v', 'V');
        var plusIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            trimmed = trimmed[..plusIndex];
        }

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset>? Assets { get; set; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
