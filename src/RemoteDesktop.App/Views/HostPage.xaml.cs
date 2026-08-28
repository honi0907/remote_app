using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RemoteDesktop.App.Helpers;
using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace RemoteDesktop.App.Views;

public sealed partial class HostPage : Page
{
    private readonly LanDiscoveryService _discovery = new();
    private readonly SessionServer _sessionServer = new();
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly DispatcherQueueTimer _firewallTimer;
    private CancellationTokenSource? _cts;
    private string _pin = string.Empty;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    public HostPage()
    {
        InitializeComponent();
        _firewallTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _firewallTimer.Interval = TimeSpan.FromSeconds(2);
        _firewallTimer.Tick += FirewallTimer_Tick;

        _sessionServer.ClientConnectionRequested += OnClientConnectionRequested;
        _sessionServer.ClientConnected += OnClientConnected;
        _sessionServer.ClientDisconnected += OnClientDisconnected;
        _screenCapture.FrameCaptured += OnFrameCaptured;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pin = PinGenerator.CreatePin();
        PinText.Text = _pin;
        IpText.Text = NetworkHelper.GetPrimaryIPv4Address();
        HostNameText.Text = NetworkHelper.GetLocalHostName();
        StatusText.Text = "接続待機中…";
        ConnectedClientText.Text = "接続中のクライアント: なし";

        _cts = new CancellationTokenSource();
        _firewallTimer.Start();

        await _discovery.StartBroadcastingAsync(NetworkHelper.GetLocalHostName(), RemoteConstants.SessionPort, _cts.Token);
        await _sessionServer.StartAsync(_pin, _cts.Token);
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        _firewallTimer.Stop();
        _cts?.Cancel();
        _screenCapture.FrameCaptured -= OnFrameCaptured;
        await _screenCapture.DisposeAsync();
        await _sessionServer.DisposeAsync();
        await _discovery.DisposeAsync();
        _cts?.Dispose();
        _cts = null;
        base.OnNavigatedFrom(e);
    }

    private void OnClientConnectionRequested(object? sender, string viewerName)
    {
        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "接続リクエスト",
                Content = $"{viewerName} から接続要求があります。許可しますか？",
                PrimaryButtonText = "許可",
                CloseButtonText = "拒否",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var result = await dialog.ShowAsync();
            await _sessionServer.ApprovePendingClientAsync(result == ContentDialogResult.Primary);
        });
    }

    private async void OnClientConnected(object? sender, EventArgs e)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            StatusText.Text = "クライアント接続済み - 画面共有中";
            ConnectedClientText.Text = "接続中のクライアント: 1";
            await _screenCapture.StartAsync(_cts?.Token ?? CancellationToken.None);
        });
    }

    private async void OnClientDisconnected(object? sender, EventArgs e)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            StatusText.Text = "接続待機中…";
            ConnectedClientText.Text = "接続中のクライアント: なし";
            FpsText.Text = "FPS: --";
            await _screenCapture.StopAsync();
        });
    }

    private async void OnFrameCaptured(object? sender, (FrameMetadata Metadata, byte[] Jpeg) frame)
    {
        if (!_sessionServer.HasAuthenticatedClient)
        {
            return;
        }

        await _sessionServer.SendFrameAsync(frame.Metadata, frame.Jpeg);

        _frameCount++;
        var elapsed = DateTime.UtcNow - _fpsWindowStart;
        if (elapsed.TotalSeconds >= 1)
        {
            var fps = _frameCount / elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsWindowStart = DateTime.UtcNow;
            _ = DispatcherQueue.EnqueueAsync(() => FpsText.Text = $"FPS: {fps:0}");
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void FirewallTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        FirewallInfoBar.IsOpen = true;
        _firewallTimer.Stop();
    }
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue dispatcher action."));
        }

        return tcs.Task;
    }

    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue dispatcher action."));
        }

        return tcs.Task;
    }
}
