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
    private readonly DispatcherQueueTimer _diagnosticsTimer;
    private CancellationTokenSource? _cts;
    private string _pin = string.Empty;
    private int _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private bool _isLoadingSettings;

    public HostPage()
    {
        InitializeComponent();
        _firewallTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _firewallTimer.Interval = TimeSpan.FromSeconds(2);
        _firewallTimer.Tick += FirewallTimer_Tick;

        _diagnosticsTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _diagnosticsTimer.Interval = TimeSpan.FromSeconds(1);
        _diagnosticsTimer.Tick += DiagnosticsTimer_Tick;

        _sessionServer.ClientConnectionRequested += OnClientConnectionRequested;
        _sessionServer.ClientConnected += OnClientConnected;
        _sessionServer.ClientDisconnected += OnClientDisconnected;
        _sessionServer.ViewerStatusReceived += OnViewerStatusReceived;
        _screenCapture.FrameCaptured += OnFrameCaptured;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadSettingsIntoUi();
        LogPathText.Text = $"ログ: {SessionLog.TodayPath("host")}";
        SessionLog.Write("host", "ホスト画面を開きました");

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
        _diagnosticsTimer.Stop();
        _cts?.Cancel();
        _screenCapture.FrameCaptured -= OnFrameCaptured;
        await _screenCapture.DisposeAsync();
        await _sessionServer.DisposeAsync();
        await _discovery.DisposeAsync();
        _cts?.Dispose();
        _cts = null;
        base.OnNavigatedFrom(e);
    }

    private void LoadSettingsIntoUi()
    {
        _isLoadingSettings = true;
        var settings = HostSettingsStore.Load();

        DeliveryModeComboBox.SelectedIndex = settings.DeliveryMode switch
        {
            StreamDeliveryMode.H264 => 1,
            StreamDeliveryMode.Jpeg => 2,
            _ => 0,
        };

        PresetComboBox.SelectedIndex = settings.Preset switch
        {
            StreamQualityPreset.Responsive => 0,
            StreamQualityPreset.Quality => 1,
            StreamQualityPreset.Manual => 2,
            _ => 0,
        };

        FpsNumberBox.Value = settings.TargetFps;
        MaxWidthNumberBox.Value = settings.MaxCaptureWidth;
        QualityNumberBox.Value = settings.JpegQuality;
        ManualSettingsPanel.Visibility = settings.Preset == StreamQualityPreset.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateEffectiveSettingsText();
        _isLoadingSettings = false;
    }

    private void DeliveryModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveCurrentSettings();
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || PresetComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var preset = item.Tag?.ToString() switch
        {
            "Quality" => StreamQualityPreset.Quality,
            "Manual" => StreamQualityPreset.Manual,
            _ => StreamQualityPreset.Responsive,
        };

        ManualSettingsPanel.Visibility = preset == StreamQualityPreset.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;

        SaveCurrentSettings();
    }

    private void SaveCurrentSettings()
    {
        var preset = PresetComboBox.SelectedItem is ComboBoxItem presetItem
            ? presetItem.Tag?.ToString() switch
            {
                "Quality" => StreamQualityPreset.Quality,
                "Manual" => StreamQualityPreset.Manual,
                _ => StreamQualityPreset.Responsive,
            }
            : StreamQualityPreset.Responsive;

        ManualSettingsPanel.Visibility = preset == StreamQualityPreset.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;

        var deliveryMode = DeliveryModeComboBox.SelectedItem is ComboBoxItem deliveryItem
            ? deliveryItem.Tag?.ToString() switch
            {
                "H264" => StreamDeliveryMode.H264,
                "Jpeg" => StreamDeliveryMode.Jpeg,
                _ => StreamDeliveryMode.Auto,
            }
            : StreamDeliveryMode.Auto;

        var settings = new StreamSettings
        {
            Preset = preset,
            DeliveryMode = deliveryMode,
            TargetFps = ReadNumberBox(FpsNumberBox, StreamSettings.DefaultResponsiveFps, 10, 30),
            MaxCaptureWidth = ReadNumberBox(MaxWidthNumberBox, StreamSettings.DefaultResponsiveMaxWidth, 0, 3840),
            JpegQuality = ReadNumberBox(QualityNumberBox, StreamSettings.DefaultResponsiveJpegQuality, 30, 95),
        };

        HostSettingsStore.Save(settings);
        UpdateEffectiveSettingsText();
    }

    private void ManualSetting_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingSettings || PresetComboBox.SelectedIndex != 2)
        {
            return;
        }

        SaveCurrentSettings();
    }

    private static int ReadNumberBox(NumberBox box, int fallback, int min, int max)
    {
        var value = box.Value;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(value), min, max);
    }

    private void UpdateEffectiveSettingsText()
    {
        var effective = HostSettingsStore.GetEffectiveSettings();
        var widthLabel = effective.MaxCaptureWidth <= 0 ? "フル解像度" : $"{effective.MaxCaptureWidth}px 幅";
        var settings = HostSettingsStore.Load();
        var codecLabel = settings.DeliveryMode switch
        {
            StreamDeliveryMode.H264 => "H.264",
            StreamDeliveryMode.Jpeg => "JPEG",
            StreamDeliveryMode.H265 => "H.265",
            _ => "自動(H.264優先)",
        };
        EffectiveSettingsText.Text =
            $"現在の配信: {codecLabel} / FPS {effective.TargetFps} / {widthLabel} / JPEG {effective.JpegQuality}";
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
            try
            {
                var settings = HostSettingsStore.Load();
                _screenCapture.ConfigureEncoder(settings.DeliveryMode);
                await _sessionServer.SendStreamConfigAsync(_screenCapture.CreateStreamConfig());
                await _screenCapture.StartAsync(_cts?.Token ?? CancellationToken.None);

                var codecLabel = _screenCapture.ActiveCodec == StreamCodec.H264 ? "H.264" : "JPEG";
                StatusText.Text = settings.DeliveryMode is StreamDeliveryMode.H264 or StreamDeliveryMode.H265
                    && _screenCapture.ActiveCodec == StreamCodec.Jpeg
                    ? $"クライアント接続済み - JPEG配信中（H.264はMedia Foundation未対応）"
                    : $"クライアント接続済み - 画面共有中 ({codecLabel})";
                ConnectedClientText.Text = "接続中のクライアント: 1";
                DiagnosticText.Text = $"診断: 配信開始 codec={_screenCapture.ActiveCodec} capture={_screenCapture.CaptureWidth}x{_screenCapture.CaptureHeight}";
                SessionLog.Write("host", DiagnosticText.Text);
                _diagnosticsTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = "画面共有の開始に失敗しました";
                ConnectedClientText.Text = "接続中のクライアント: なし";
                FpsText.Text = "FPS: --";

                var dialog = new ContentDialog
                {
                    Title = "画面共有エラー",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
            }
        });
    }

    private async void OnClientDisconnected(object? sender, EventArgs e)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            StatusText.Text = "接続待機中…";
            ConnectedClientText.Text = "接続中のクライアント: なし";
            FpsText.Text = "FPS: --";
            DiagnosticText.Text = "診断: 待機中";
            ViewerDiagnosticText.Text = "接続側: 未受信";
            SessionLog.Write("host", "クライアント切断");
            _diagnosticsTimer.Stop();
            await _screenCapture.StopAsync();
        });
    }

    private void OnFrameCaptured(object? sender, EncodedStreamFrame frame)
    {
        if (!_sessionServer.HasAuthenticatedClient)
        {
            return;
        }

        _sessionServer.QueueFrame(frame);

        _frameCount++;
        var elapsed = DateTime.UtcNow - _fpsWindowStart;
        if (elapsed.TotalSeconds >= 1)
        {
            var fps = _frameCount / elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsWindowStart = DateTime.UtcNow;
            var encodeError = _screenCapture.LastEncodeError;
            _ = DispatcherQueue.EnqueueAsync(() =>
            {
                FpsText.Text = $"FPS: {fps:0}";
                DiagnosticText.Text = encodeError is null
                    ? $"診断: 送信 {frame.Codec} {frame.Metadata.Width}x{frame.Metadata.Height} {frame.Payload.Length}B key={frame.IsKeyframe}"
                    : $"診断: エンコードエラー {encodeError}";
            });
        }
    }

    private async void DiagnosticsTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var status =
            $"host codec={_screenCapture.ActiveCodec} { _screenCapture.CaptureWidth}x{_screenCapture.CaptureHeight} " +
            $"try={_screenCapture.EncodeAttempts} ok={_screenCapture.EncodeSuccesses} empty={_screenCapture.EncodeEmpties} " +
            $"err={_screenCapture.LastEncodeError ?? "-"}";
        DiagnosticText.Text = $"診断: {status}";
        SessionLog.Write("host", status);

        if (_sessionServer.HasAuthenticatedClient)
        {
            try
            {
                await _sessionServer.SendStreamStatusAsync(status);
            }
            catch (Exception)
            {
            }
        }

        if (_screenCapture.ActiveCodec == StreamCodec.H264 &&
            _screenCapture.EncodeSuccesses == 0 &&
            _screenCapture.EncodeAttempts >= 8)
        {
            _screenCapture.FallbackToJpeg("H.264がフレームを出せないためJPEGへ切替");
            try
            {
                await _sessionServer.SendStreamConfigAsync(_screenCapture.CreateStreamConfig());
                StatusText.Text = "クライアント接続済み - JPEG配信中（H.264送信失敗のため切替）";
            }
            catch (Exception)
            {
            }
        }
    }

    private void OnViewerStatusReceived(object? sender, string status)
    {
        SessionLog.Write("host", $"viewer {status}");
        _ = DispatcherQueue.EnqueueAsync(() => ViewerDiagnosticText.Text = $"接続側: {status}");
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        SessionLog.OpenDirectory();
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
