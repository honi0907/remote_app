using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using RemoteDesktop.App.Helpers;
using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Streams;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace RemoteDesktop.App.Views;

public sealed partial class ViewerPage : Page
{
    private readonly LanDiscoveryService _discovery = new();
    private readonly SessionClient _sessionClient = new();
    private readonly DispatcherQueueTimer _pingTimer;
    private readonly DispatcherQueueTimer _fpsTimer;
    private CancellationTokenSource? _cts;
    private double _sourceWidth = 1;
    private double _sourceHeight = 1;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private bool _isConnected;
    private bool _isPointerCaptured;
    private (FrameMetadata Metadata, byte[] Jpeg)? _latestFrame;
    private int _renderInProgress;

    public ViewerPage()
    {
        InitializeComponent();
        _pingTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pingTimer.Interval = TimeSpan.FromSeconds(2);
        _pingTimer.Tick += PingTimer_Tick;

        _fpsTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _fpsTimer.Interval = TimeSpan.FromSeconds(1);
        _fpsTimer.Tick += FpsTimer_Tick;

        _discovery.HostsChanged += OnHostsChanged;
        _sessionClient.FrameReceived += OnFrameReceived;
        _sessionClient.LatencyMeasured += OnLatencyMeasured;
        _sessionClient.Disconnected += OnDisconnected;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _cts = new CancellationTokenSource();
        await _discovery.StartListeningAsync(_cts.Token);
        RefreshHostList();
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pingTimer.Stop();
        _fpsTimer.Stop();
        _cts?.Cancel();
        await _sessionClient.DisposeAsync();
        await _discovery.DisposeAsync();
        _cts?.Dispose();
        _cts = null;
        base.OnNavigatedFrom(e);
    }

    private void OnHostsChanged(object? sender, IReadOnlyList<DiscoveredHost> hosts)
    {
        _ = DispatcherQueue.EnqueueAsync(() =>
        {
            HostListView.ItemsSource = hosts;
            StatusText.Text = hosts.Count > 0
                ? "近傍ホストを選択して接続してください。"
                : "近傍ホストを探索中…";
        });
    }

    private void RefreshHostList()
    {
        HostListView.ItemsSource = _discovery.GetHosts();
    }

    private void HostListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiscoveredHost host)
        {
            _ = ConnectToHostAsync(host.Address, host.SessionPort);
        }
    }

    private async void ManualConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var address = ManualAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            address = "127.0.0.1";
        }

        await ConnectToHostAsync(address, RemoteConstants.SessionPort);
    }

    private async Task ConnectToHostAsync(string address, int port)
    {
        var pinDialog = new ContentDialog
        {
            Title = "PIN入力",
            Content = new TextBox
            {
                PlaceholderText = "6桁PIN",
                MaxLength = 6,
            },
            PrimaryButtonText = "接続",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await pinDialog.ShowAsync();
        if (result != ContentDialogResult.Primary || pinDialog.Content is not TextBox pinBox)
        {
            return;
        }

        var pin = pinBox.Text.Trim();
        if (pin.Length != 6)
        {
            await ShowErrorAsync("PINは6桁で入力してください。");
            return;
        }

        StatusText.Text = $"{address} に接続中…";
        try
        {
            await _sessionClient.ConnectAsync(address, port, NetworkHelper.GetLocalHostName(), pin, _cts?.Token ?? CancellationToken.None);
            _isConnected = true;
            ConnectionPanel.Visibility = Visibility.Collapsed;
            RemotePanel.Visibility = Visibility.Visible;
            StatusText.Text = "接続済み";
            _pingTimer.Start();
            _fpsTimer.Start();
            RemoteCanvas.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            await ShowErrorAsync(ex.Message);
        }
    }

    private void OnFrameReceived(object? sender, (FrameMetadata Metadata, byte[] Jpeg) frame)
    {
        _frameCount++;
        _latestFrame = frame;
        TryRenderLatestFrame();
    }

    private void TryRenderLatestFrame()
    {
        if (Interlocked.CompareExchange(ref _renderInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            try
            {
                while (_latestFrame is { } pending)
                {
                    _latestFrame = null;
                    _sourceWidth = pending.Metadata.Width;
                    _sourceHeight = pending.Metadata.Height;

                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(pending.Jpeg.AsBuffer());
                    stream.Seek(0);

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    RemoteImage.Source = bitmap;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderInProgress, 0);
                if (_latestFrame is not null)
                {
                    TryRenderLatestFrame();
                }
            }
        });
    }

    private void OnLatencyMeasured(object? sender, long latencyMs)
    {
        _ = DispatcherQueue.EnqueueAsync(() => LatencyText.Text = $"遅延: {latencyMs} ms");
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            _isConnected = false;
            _pingTimer.Stop();
            _fpsTimer.Stop();
            ConnectionPanel.Visibility = Visibility.Visible;
            RemotePanel.Visibility = Visibility.Collapsed;
            RemoteImage.Source = null;
            StatusText.Text = "切断されました。";
            LatencyText.Text = "遅延: --";
            FpsText.Text = "FPS: --";
            await Task.CompletedTask;
        });
    }

    private void FpsTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = DateTime.UtcNow - _fpsWindowStart;
        if (elapsed.TotalSeconds <= 0)
        {
            return;
        }

        var fps = _frameCount / elapsed.TotalSeconds;
        FpsText.Text = $"FPS: {fps:0}";
        _frameCount = 0;
        _fpsWindowStart = DateTime.UtcNow;
    }

    private async void PingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isConnected)
        {
            await _sessionClient.SendPingAsync();
        }
    }

    private void RemoteCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isConnected || !TryGetNormalized(e.GetCurrentPoint(RemoteCanvas).Position, out var nx, out var ny))
        {
            return;
        }

        _sessionClient.QueueMouseMove(nx, ny);
    }

    private async void RemoteCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        RemoteCanvas.CapturePointer(e.Pointer);
        _isPointerCaptured = true;
        Focus(FocusState.Programmatic);

        if (!TryGetNormalized(e.GetCurrentPoint(RemoteCanvas).Position, out var nx, out var ny))
        {
            return;
        }

        var button = e.GetCurrentPoint(RemoteCanvas).Properties.IsRightButtonPressed
            ? MouseButtonKind.Right
            : MouseButtonKind.Left;
        await _sessionClient.SendMouseButtonAsync(button, true, nx, ny);
    }

    private async void RemoteCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        if (_isPointerCaptured)
        {
            RemoteCanvas.ReleasePointerCapture(e.Pointer);
            _isPointerCaptured = false;
        }

        if (!TryGetNormalized(e.GetCurrentPoint(RemoteCanvas).Position, out var nx, out var ny))
        {
            return;
        }

        var button = e.GetCurrentPoint(RemoteCanvas).Properties.IsRightButtonPressed
            ? MouseButtonKind.Right
            : MouseButtonKind.Left;
        await _sessionClient.SendMouseButtonAsync(button, false, nx, ny);
    }

    private async void RemoteCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_isConnected || !TryGetNormalized(e.GetCurrentPoint(RemoteCanvas).Position, out var nx, out var ny))
        {
            return;
        }

        var delta = e.GetCurrentPoint(RemoteCanvas).Properties.MouseWheelDelta;
        await _sessionClient.SendMouseWheelAsync(delta, nx, ny);
    }

    private async void RemoteCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        await _sessionClient.SendKeyAsync((int)e.Key, KeyAction.Down);
        e.Handled = true;
    }

    private async void RemoteCanvas_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        await _sessionClient.SendKeyAsync((int)e.Key, KeyAction.Up);
        e.Handled = true;
    }

    private bool TryGetNormalized(Point position, out double normalizedX, out double normalizedY)
    {
        return CoordinateMapper.TryMapPointerToNormalized(
            position.X,
            position.Y,
            RemoteCanvas.ActualWidth,
            RemoteCanvas.ActualHeight,
            _sourceWidth,
            _sourceHeight,
            out normalizedX,
            out normalizedY);
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _sessionClient.DisconnectAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "エラー",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
