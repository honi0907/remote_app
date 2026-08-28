namespace RemoteDesktop.App.Services;

public sealed class UpdateConfiguration
{
    public string GitHubOwner { get; init; } = "honi0907";
    public string GitHubRepo { get; init; } = "remote_app";
    public string InstallerAssetPattern { get; init; } = "RemoteDesktopLAN-Setup-*.exe";
}

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    Version? CurrentVersion,
    Version? LatestVersion,
    string? ReleaseNotes,
    string? DownloadUrl,
    string? AssetName);

public sealed record UpdateDownloadResult(
    bool Succeeded,
    string? InstallerPath,
    string? ErrorMessage);

public interface IUpdateService
{
    UpdateConfiguration Configuration { get; }
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<UpdateDownloadResult> DownloadUpdateAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    void LaunchInstaller(string installerPath);
}
