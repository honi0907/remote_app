using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RemoteDesktop.App.Helpers;
using RemoteDesktop.App.Services;
using RemoteDesktop.App.Views;

namespace RemoteDesktop.App.Views;

public sealed partial class HomePage : Page
{
    private readonly IUpdateService _updateService = new GitHubUpdateService();
    private bool _updateChecked;

    public HomePage()
    {
        InitializeComponent();
        VersionText.Text = UpdateConfigLoader.GetCurrentVersionLabel();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_updateChecked)
        {
            _updateChecked = true;
            await CheckForUpdatesSilentlyAsync();
        }
    }

    private async void HostButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HostPage));
    }

    private async void ViewerButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ViewerPage));
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(showNoUpdateMessage: true);
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        await CheckForUpdatesAsync(showNoUpdateMessage: false);
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateMessage)
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (!result.UpdateAvailable)
            {
                if (showNoUpdateMessage)
                {
                    await ShowDialogAsync("更新確認", "最新バージョンです。");
                }

                return;
            }

            var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? "リリースノートはありません。"
                : result.ReleaseNotes;
            var dialog = new ContentDialog
            {
                Title = $"更新 v{result.LatestVersion} が利用可能",
                Content = new ScrollViewer
                {
                    MaxHeight = 240,
                    Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.WrapWholeWords },
                },
                PrimaryButtonText = "ダウンロードしてインストール",
                CloseButtonText = "後で",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await DownloadAndInstallAsync(result);
        }
        catch (Exception ex)
        {
            if (showNoUpdateMessage)
            {
                await ShowDialogAsync("更新確認", $"更新の確認に失敗しました: {ex.Message}");
            }
        }
    }

    private async Task DownloadAndInstallAsync(UpdateCheckResult update)
    {
        var progressBar = new ProgressBar { IsIndeterminate = false, Minimum = 0, Maximum = 1, Value = 0 };
        var dialog = new ContentDialog
        {
            Title = "更新をダウンロード中",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = update.AssetName ?? "インストーラー" },
                    progressBar,
                },
            },
            XamlRoot = XamlRoot,
        };

        var showTask = dialog.ShowAsync();
        var progress = new Progress<double>(value => progressBar.Value = value);
        var download = await _updateService.DownloadUpdateAsync(update, progress);
        dialog.Hide();
        await showTask;

        if (!download.Succeeded || string.IsNullOrWhiteSpace(download.InstallerPath))
        {
            await ShowDialogAsync("更新失敗", download.ErrorMessage ?? "ダウンロードに失敗しました。");
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "インストール",
            Content = "ダウンロードが完了しました。インストーラーを起動してアプリを終了します。",
            PrimaryButtonText = "インストール",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _updateService.LaunchInstaller(download.InstallerPath);
        Application.Current.Exit();
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
