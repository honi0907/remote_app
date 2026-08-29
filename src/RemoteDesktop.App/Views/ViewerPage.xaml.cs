using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
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
using Windows.System;
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
    private bool _isFullscreen;
    private bool _isPointerCaptured;
    private readonly ConcurrentQueue<EncodedStreamFrame> _h264Queue = new();
    private readonly SemaphoreSlim _decodeSignal = new(0);
    private readonly object _decodeSync = new();
    private WriteableBitmap? _h264Bitmap;
    private int _lockedWidth;
    private int _lockedHeight;
    private (byte[] Bgra, int Width, int Height)? _pendingPresent;
    private int _presentQueued;
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
        _sessionClient.HostCommandReceived += OnHostCommandReceived;

        RemoteCanvas.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RemoteCanvas_KeyDown), true);
        RemoteCanvas.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(RemoteCanvas_KeyUp), true);
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Page_KeyDown), true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _cts = new CancellationTokenSource();
        LogPathText.Text = $"ログ: {SessionLog.TodayPath("viewer")}";
        SessionLog.Write("viewer", "接続画面を開きました");
        _ = Task.Run(() => DecodeLoopAsync(_cts.Token));
        await _discovery.StartListeningAsync(_cts.Token);
        RefreshHostList();
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        RemoteCursorHelper.ForceVisible();
        UnhookMainWindowActivation();
        _pingTimer.Stop();
        _fpsTimer.Stop();
        _cts?.Cancel();
        try
        {
            _decodeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        await _sessionClient.DisposeAsync();
        lock (_decodeSync)
        {
            _h264Decoder.Dispose();
        }
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
            SessionLog.Write("viewer", $"接続済み {address}:{port}");
            HookMainWindowActivation();
            EnterFullscreen();
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
        _h264DecodeFailures = 0;
        _h264Received = 0;
        _h264Decoded = 0;
        _h264Keyframes = 0;
        _lastPayloadBytes = 0;
        _lastPixelNonZero = 0;
        _lockedWidth = 0;
        _lockedHeight = 0;
        _h264Bitmap = null;
        _lastDetail = $"StreamConfig codec={config.Codec}";
        lock (_decodeSync)
        {
            _h264Decoder.Reset();
        }
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
            try
            {
                _decodeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
        else
        {
            _latestJpegFrame = (frame.Metadata, frame.Payload, frame.Codec, frame.IsKeyframe);
            TryRenderLatestFrame();
        }
    }

    private async Task DecodeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _decodeSignal.WaitAsync(cancellationToken);
                DecodedVideoFrame? lastDecoded = null;
                while (_h264Queue.TryDequeue(out var pending))
                {
                    _sourceWidth = pending.Metadata.Width;
                    _sourceHeight = pending.Metadata.Height;
                    if (pending.IsKeyframe &&
                        pending.Metadata.Width > 0 &&
                        _lockedWidth > 0 &&
                        (pending.Metadata.Width != _lockedWidth || pending.Metadata.Height != _lockedHeight))
                    {
                        lock (_decodeSync)
                        {
                            _h264Decoder.Reset();
                        }

                        _lockedWidth = 0;
                        _lockedHeight = 0;
                    }

                    try
                    {
                        DecodedVideoFrame decoded;
                        lock (_decodeSync)
                        {
                            decoded = _h264Decoder.Decode(pending);
                        }

                        if (decoded.IsEmpty)
                        {
                            _h264DecodeFailures++;
                            _lastDetail = $"デコード失敗 key={pending.IsKeyframe} {_h264Decoder.LastError ?? "出力なし"}";
                            continue;
                        }

                        _h264DecodeFailures = 0;
                        _h264Decoded++;
                        var present = FitToLockedSize(decoded, pending.Metadata.Width, pending.Metadata.Height);
                        _sourceWidth = present.Width;
                        _sourceHeight = present.Height;
                        lastDecoded = present;
                    }
                    catch (Exception ex)
                    {
                        _h264DecodeFailures++;
                        _lastDetail = $"デコード例外 {ex.Message}";
                    }
                }

                if (lastDecoded is { } decodedFrame)
                {
                    _lastPixelNonZero = CountNonZeroRgb(decodedFrame.Bgra);
                    _lastDetail = $"描画 {decodedFrame.Width}x{decodedFrame.Height}";
                    RequestPresent(decodedFrame.Bgra, decodedFrame.Width, decodedFrame.Height);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RequestPresent(byte[] bgra, int width, int height)
    {
        _pendingPresent = (bgra, width, height);
        if (Interlocked.CompareExchange(ref _presentQueued, 1, 0) != 0)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var pending = _pendingPresent;
                _pendingPresent = null;
                if (pending is { } frame)
                {
                    ShowH264Pixels(frame.Bgra, frame.Width, frame.Height);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _presentQueued, 0);
                if (_pendingPresent is { } leftover)
                {
                    RequestPresent(leftover.Bgra, leftover.Width, leftover.Height);
                }
            }
        });
    }

    private void ShowH264Pixels(byte[] bgra, int width, int height)
    {
        if (_h264Bitmap is null || _h264Bitmap.PixelWidth != width || _h264Bitmap.PixelHeight != height)
        {
            _h264Bitmap = new WriteableBitmap(width, height);
            RemoteImage.Source = _h264Bitmap;
        }

        bgra.CopyTo(_h264Bitmap.PixelBuffer);
        _h264Bitmap.Invalidate();
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
                    _lastDetail = $"JPEG表示 {jpeg.Metadata.Width}x{jpeg.Metadata.Height} {jpeg.Payload.Length}B";
                }
            }
            catch (Exception ex)
            {
                _lastDetail = $"描画エラー {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _renderInProgress, 0);
                if (_latestJpegFrame is not null)
                {
                    TryRenderLatestFrame();
                }
            }
        });
    }

    private void SetDiagnostic(string detail)
    {
        _lastDetail = detail;
        var text =
            $"診断: codec={_activeCodec} 受信={_h264Received} key={_h264Keyframes} " +
            $"デコード={_h264Decoded} 失敗={_h264DecodeFailures} 最終={_lastPayloadBytes}B 非ゼロ={_lastPixelNonZero} / {detail}";
        DiagnosticText.Text = text;
    }

    private DecodedVideoFrame FitToLockedSize(DecodedVideoFrame decoded, int metadataWidth, int metadataHeight)
    {
        var width = decoded.Width;
        var height = decoded.Height;
        if (metadataWidth > 0 &&
            metadataHeight > 0 &&
            decoded.Bgra.Length == metadataWidth * metadataHeight * 4)
        {
            width = metadataWidth;
            height = metadataHeight;
        }

        if (_lockedWidth <= 0 || _lockedHeight <= 0)
        {
            _lockedWidth = width;
            _lockedHeight = height;
            return new DecodedVideoFrame(decoded.Bgra, width, height);
        }

        if (width == _lockedWidth && height == _lockedHeight)
        {
            return new DecodedVideoFrame(decoded.Bgra, width, height);
        }

        return new DecodedVideoFrame(
            ScaleBgra(decoded.Bgra, width, height, _lockedWidth, _lockedHeight),
            _lockedWidth,
            _lockedHeight);
    }

    private static byte[] ScaleBgra(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var destination = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / width);
                var sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                var destIndex = (y * width + x) * 4;
                destination[destIndex] = source[sourceIndex];
                destination[destIndex + 1] = source[sourceIndex + 1];
                destination[destIndex + 2] = source[sourceIndex + 2];
                destination[destIndex + 3] = source[sourceIndex + 3];
            }
        }

        return destination;
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
            RemoteCursorHelper.ForceVisible();
            UnhookMainWindowActivation();
            ExitFullscreen();
            ConnectionPanel.Visibility = Visibility.Visible;
            RemotePanel.Visibility = Visibility.Collapsed;
            RemoteImage.Source = null;
            StatusText.Text = "切断されました。";
            LatencyText.Text = "遅延: --";
            FpsText.Text = "FPS: --";
            SetDiagnostic("切断");
            SessionLog.Write("viewer", "切断されました");
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
        var line =
            $"fps={fps:0} codec={_activeCodec} recv={_h264Received} key={_h264Keyframes} " +
            $"dec={_h264Decoded} fail={_h264DecodeFailures} last={_lastPayloadBytes}B nonzero={_lastPixelNonZero} {_lastDetail}";
        SessionLog.Write("viewer", line);
        if (_isConnected)
        {
            _ = _sessionClient.SendViewerStatusAsync(line);
        }

        _frameCount = 0;
        _fpsWindowStart = DateTime.UtcNow;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        SessionLog.OpenDirectory();
    }

    private async void PingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isConnected)
        {
            await _sessionClient.SendPingAsync();
        }
    }

    private void OnHostCommandReceived(object? sender, HostCommandKind command)
    {
        if (command != HostCommandKind.ExitViewerFullscreen)
        {
            return;
        }

        _ = DispatcherQueue.EnqueueAsync(() => ExitFullscreen(showControls: true));
    }

    private void RemoteCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isConnected)
        {
            RemoteCursorHelper.SetHidden(true);
            RemoteCanvas.Focus(FocusState.Pointer);
        }
    }

    private void RemoteCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RemoteCursorHelper.SetHidden(false);
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
        RemoteCanvas.Focus(FocusState.Pointer);

        if (!TryGetNormalized(e.GetCurrentPoint(RemoteCanvas).Position, out var nx, out var ny))
        {
            return;
        }

        if (!TryGetMouseButton(e, RemoteCanvas, out var button))
        {
            return;
        }

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

        if (!TryGetReleasedMouseButton(e, RemoteCanvas, out var button))
        {
            return;
        }

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

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && _isFullscreen)
        {
            ExitFullscreen(showControls: true);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.D &&
            IsCtrlShiftPressed() &&
            _isConnected)
        {
            _ = _sessionClient.DisconnectAsync();
            e.Handled = true;
        }
    }

    private async void RemoteCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && _isFullscreen)
        {
            ExitFullscreen(showControls: true);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.D && IsCtrlShiftPressed())
        {
            await _sessionClient.DisconnectAsync();
            e.Handled = true;
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

    private static bool TryGetMouseButton(PointerRoutedEventArgs e, Grid canvas, out MouseButtonKind button)
    {
        var props = e.GetCurrentPoint(canvas).Properties;
        return TryMapPointerUpdateKind(props.PointerUpdateKind, out button)
            || TryMapPressedButtons(props, out button);
    }

    private static bool TryGetReleasedMouseButton(PointerRoutedEventArgs e, Grid canvas, out MouseButtonKind button)
    {
        var props = e.GetCurrentPoint(canvas).Properties;
        if (TryMapPointerUpdateKind(props.PointerUpdateKind, out button))
        {
            return true;
        }

        if (props.IsLeftButtonPressed)
        {
            button = MouseButtonKind.Left;
            return true;
        }

        if (props.IsRightButtonPressed)
        {
            button = MouseButtonKind.Right;
            return true;
        }

        if (props.IsMiddleButtonPressed)
        {
            button = MouseButtonKind.Middle;
            return true;
        }

        button = MouseButtonKind.Left;
        return true;
    }

    private static bool TryMapPointerUpdateKind(PointerUpdateKind kind, out MouseButtonKind button)
    {
        switch (kind)
        {
            case PointerUpdateKind.LeftButtonPressed:
            case PointerUpdateKind.LeftButtonReleased:
                button = MouseButtonKind.Left;
                return true;
            case PointerUpdateKind.RightButtonPressed:
            case PointerUpdateKind.RightButtonReleased:
                button = MouseButtonKind.Right;
                return true;
            case PointerUpdateKind.MiddleButtonPressed:
            case PointerUpdateKind.MiddleButtonReleased:
                button = MouseButtonKind.Middle;
                return true;
            default:
                button = MouseButtonKind.Left;
                return false;
        }
    }

    private static bool TryMapPressedButtons(PointerPointProperties props, out MouseButtonKind button)
    {
        if (props.IsRightButtonPressed)
        {
            button = MouseButtonKind.Right;
            return true;
        }

        if (props.IsMiddleButtonPressed)
        {
            button = MouseButtonKind.Middle;
            return true;
        }

        if (props.IsLeftButtonPressed)
        {
            button = MouseButtonKind.Left;
            return true;
        }

        button = MouseButtonKind.Left;
        return false;
    }

    private void EnterFullscreen()
    {
        var appWindow = AppWindowHelper.GetAppWindow(App.MainWindowInstance);
        if (appWindow is null)
        {
            return;
        }

        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        HeaderBar.Visibility = Visibility.Collapsed;
        DiagnosticBar.Visibility = Visibility.Collapsed;
        RemoteOverlayBar.Visibility = Visibility.Collapsed;
        HeaderRow.Height = new GridLength(0);
        DiagnosticRow.Height = new GridLength(0);
        _isFullscreen = true;
    }

    private void ExitFullscreen(bool showControls = true)
    {
        if (!_isFullscreen)
        {
            return;
        }

        var appWindow = AppWindowHelper.GetAppWindow(App.MainWindowInstance);
        if (appWindow is not null)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Default);
        }

        if (showControls && _isConnected)
        {
            HeaderBar.Visibility = Visibility.Visible;
            DiagnosticBar.Visibility = Visibility.Visible;
            RemoteOverlayBar.Visibility = Visibility.Visible;
            HeaderRow.Height = GridLength.Auto;
            DiagnosticRow.Height = GridLength.Auto;
        }

        _isFullscreen = false;
    }

    private void EnterFullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        EnterFullscreen();
        RemoteCanvas.Focus(FocusState.Programmatic);
    }

    private void HookMainWindowActivation()
    {
        if (App.MainWindowInstance is null)
        {
            return;
        }

        App.MainWindowInstance.Activated += MainWindow_Activated;
    }

    private void UnhookMainWindowActivation()
    {
        if (App.MainWindowInstance is null)
        {
            return;
        }

        App.MainWindowInstance.Activated -= MainWindow_Activated;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            RemoteCursorHelper.SetHidden(false);
        }
    }

    private static bool IsCtrlShiftPressed()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            && shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
