using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using RemoteDesktop.App.Helpers;
using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;
using RemoteDesktop.App.Services.StreamEncoding;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace RemoteDesktop.App.Views;

public sealed partial class ViewerPage : Page
{
    private readonly LanDiscoveryService _discovery = new();
    private readonly SessionClient _sessionClient = new();
    private readonly H264StreamFrameDecoder _h264Decoder = new();
    private readonly DispatcherQueueTimer _pingTimer;
    private readonly DispatcherQueueTimer _fpsTimer;
    private CancellationTokenSource? _cts;
    private double _sourceWidth = 1;
    private double _sourceHeight = 1;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private bool _isConnected;
    private bool _isPointerCaptured;
    private readonly ConcurrentQueue<EncodedStreamFrame> _h264Queue = new();
    private (FrameMetadata Metadata, byte[] Payload, StreamCodec Codec, bool IsKeyframe)? _latestJpegFrame;
    private int _renderInProgress;
    private int _h264DecodeFailures;
    private int _h264Received;
    private int _h264Decoded;
    private int _h264Keyframes;
    private int _lastPayloadBytes;
    private int _lastPixelNonZero;
    private string _lastDetail = "未受信";
    private StreamCodec _activeCodec = StreamCodec.Jpeg;

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
        _sessionClient.StreamFrameReceived += OnStreamFrameReceived;
        _sessionClient.StreamConfigReceived += OnStreamConfigReceived;
        _sessionClient.StreamStatusReceived += OnStreamStatusReceived;
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
        _h264Decoder.Dispose();
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

    private void OnStreamStatusReceived(object? sender, string status)
    {
        _ = DispatcherQueue.EnqueueAsync(() => SetDiagnostic($"ホスト: {status}"));
    }

    private void OnStreamConfigReceived(object? sender, StreamConfigMessage config)
    {
        _activeCodec = config.Codec;
        _h264Decoder.Reset();
        _h264DecodeFailures = 0;
        _h264Received = 0;
        _h264Decoded = 0;
        _h264Keyframes = 0;
        _lastPayloadBytes = 0;
        _lastPixelNonZero = 0;
        _lastDetail = $"StreamConfig codec={config.Codec}";
        while (_h264Queue.TryDequeue(out _))
        {
        }

        _ = DispatcherQueue.EnqueueAsync(() => SetDiagnostic(
            $"StreamConfig codec={config.Codec} fps={config.TargetFps} width={config.MaxCaptureWidth}"));
    }

    private void OnStreamFrameReceived(object? sender, EncodedStreamFrame frame)
    {
        _frameCount++;
        if (frame.Codec == StreamCodec.H264)
        {
            _h264Received++;
            _lastPayloadBytes = frame.Payload.Length;
            if (frame.IsKeyframe)
            {
                _h264Keyframes++;
                while (_h264Queue.TryDequeue(out _))
                {
                }
            }
            else if (_h264Queue.Count > 24)
            {
                return;
            }

            _h264Queue.Enqueue(frame);
        }
        else
        {
            _latestJpegFrame = (frame.Metadata, frame.Payload, frame.Codec, frame.IsKeyframe);
        }

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
                DecodedVideoFrame? lastDecoded = null;
                while (_h264Queue.TryDequeue(out var pending))
                {
                    _sourceWidth = pending.Metadata.Width;
                    _sourceHeight = pending.Metadata.Height;
                    try
                    {
                        var decoded = _h264Decoder.Decode(pending);
                        if (decoded.IsEmpty)
                        {
                            _h264DecodeFailures++;
                            SetDiagnostic(
                                $"デコード失敗 in={pending.Payload.Length}B {pending.Metadata.Width}x{pending.Metadata.Height} key={pending.IsKeyframe} 原因={_h264Decoder.LastError ?? "出力なし（キーフレーム待ち）"}");
                            continue;
                        }

                        _h264DecodeFailures = 0;
                        _h264Decoded++;
                        _sourceWidth = decoded.Width;
                        _sourceHeight = decoded.Height;
                        lastDecoded = decoded;
                    }
                    catch (Exception ex)
                    {
                        _h264DecodeFailures++;
                        SetDiagnostic($"デコード例外 {ex.Message}");
                    }
                }

                if (lastDecoded is { } decodedFrame)
                {
                    _lastPixelNonZero = CountNonZeroRgb(decodedFrame.Bgra);
                    SetDiagnostic(
                        $"描画 {decodedFrame.Width}x{decodedFrame.Height} 非ゼロ画素={_lastPixelNonZero}");
                    await ShowH264FrameAsync(decodedFrame.Bgra, decodedFrame.Width, decodedFrame.Height);
                }

                if (_latestJpegFrame is { } jpeg)
                {
                    _latestJpegFrame = null;
                    _sourceWidth = jpeg.Metadata.Width;
                    _sourceHeight = jpeg.Metadata.Height;
                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(jpeg.Payload.AsBuffer());
                    stream.Seek(0);

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    RemoteImage.Source = bitmap;
                    SetDiagnostic($"JPEG表示 {jpeg.Metadata.Width}x{jpeg.Metadata.Height} {jpeg.Payload.Length}B");
                }
            }
            catch (Exception ex)
            {
                SetDiagnostic($"描画エラー {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _renderInProgress, 0);
                if (!_h264Queue.IsEmpty || _latestJpegFrame is not null)
                {
                    TryRenderLatestFrame();
                }
            }
        });
    }

    private async Task ShowH264FrameAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        RemoteImage.Source = bitmap;
    }

    private void SetDiagnostic(string detail)
    {
        _lastDetail = detail;
        var text =
            $"診断: codec={_activeCodec} 受信={_h264Received} key={_h264Keyframes} " +
            $"デコード={_h264Decoded} 失敗={_h264DecodeFailures} 最終={_lastPayloadBytes}B 非ゼロ={_lastPixelNonZero} / {detail}";
        DiagnosticText.Text = text;
        OverlayDiagnosticText.Text = text;
    }

    private static int CountNonZeroRgb(byte[] bgra)
    {
        var count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 16)
        {
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0)
            {
                count++;
            }
        }

        return count;
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
            SetDiagnostic("切断");
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
        SetDiagnostic(_lastDetail);
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
