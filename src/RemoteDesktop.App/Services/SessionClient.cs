using System.Net.Sockets;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public sealed class SessionClient : IAsyncDisposable
{
    private readonly MessageReader _reader = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private TaskCompletionSource<ConnectionResponseKind>? _connectionTcs;
    private TaskCompletionSource<AuthResult>? _authTcs;

    public event EventHandler<(FrameMetadata Metadata, byte[] Jpeg)>? FrameReceived;
    public event EventHandler<long>? LatencyMeasured;
    public event EventHandler? Disconnected;
    public event EventHandler<ConnectionResponseKind>? ConnectionResponseReceived;
    public event EventHandler<AuthResult>? AuthResponseReceived;

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(string address, int port, string viewerName, string pin, CancellationToken cancellationToken)
    {
        await DisconnectAsync();

        _connectionTcs = new TaskCompletionSource<ConnectionResponseKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authTcs = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _client = new TcpClient();
        await _client.ConnectAsync(address, port, cancellationToken);
        _stream = _client.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(30));

        await WriteAsync(MessageSerializer.BuildConnectionRequest(viewerName));

        var connectionResponse = await _connectionTcs.Task.WaitAsync(handshakeTimeout.Token);
        ConnectionResponseReceived?.Invoke(this, connectionResponse);
        if (connectionResponse != ConnectionResponseKind.Accepted)
        {
            throw new InvalidOperationException("ホストが接続を拒否しました。");
        }

        await WriteAsync(MessageSerializer.BuildAuthRequest(pin));

        var authResult = await _authTcs.Task.WaitAsync(handshakeTimeout.Token);
        AuthResponseReceived?.Invoke(this, authResult);
        if (authResult != AuthResult.Ok)
        {
            throw new InvalidOperationException(authResult == AuthResult.InvalidPin
                ? "PINが正しくありません。"
                : "認証に失敗しました。");
        }
    }

    public async Task SendMouseMoveAsync(double normalizedX, double normalizedY)
    {
        await WriteAsync(MessageSerializer.BuildMouseMove(new MouseMoveMessage(normalizedX, normalizedY)));
    }

    public async Task SendMouseButtonAsync(MouseButtonKind button, bool isDown, double normalizedX, double normalizedY)
    {
        await WriteAsync(MessageSerializer.BuildMouseButton(new MouseButtonMessage(button, isDown, normalizedX, normalizedY)));
    }

    public async Task SendMouseWheelAsync(int delta, double normalizedX, double normalizedY)
    {
        await WriteAsync(MessageSerializer.BuildMouseWheel(new MouseWheelMessage(delta, normalizedX, normalizedY)));
    }

    public async Task SendKeyAsync(int virtualKey, KeyAction action)
    {
        await WriteAsync(MessageSerializer.BuildKey(new KeyMessage(virtualKey, action)));
    }

    public async Task SendPingAsync()
    {
        await WriteAsync(MessageSerializer.BuildPing(DateTime.UtcNow.Ticks));
    }

    public async Task DisconnectAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await WriteAsync(MessageSerializer.BuildDisconnect());
            }
            catch (Exception)
            {
            }
        }

        _cts?.Cancel();

        if (_readTask is not null)
        {
            try
            {
                await _readTask;
            }
            catch (Exception)
            {
            }
        }

        _stream?.Dispose();
        _client?.Close();
        _stream = null;
        _client = null;
        _readTask = null;
        _connectionTcs = null;
        _authTcs = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_stream is null)
                {
                    break;
                }

                var read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                foreach (var message in _reader.Append(buffer.AsSpan(0, read)))
                {
                    HandleMessage(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleMessage(byte[] message)
    {
        if (!MessageSerializer.TryParseType(message, out var type))
        {
            return;
        }

        switch (type)
        {
            case MessageType.ConnectionResponse:
                _connectionTcs?.TrySetResult(MessageSerializer.ParseConnectionResponse(message));
                break;

            case MessageType.AuthResponse:
                _authTcs?.TrySetResult(MessageSerializer.ParseAuthResponse(message));
                break;

            case MessageType.Frame:
                var (metadata, jpeg) = MessageSerializer.ParseFrame(message);
                FrameReceived?.Invoke(this, (metadata, jpeg));
                break;

            case MessageType.Pong:
                var sentAt = MessageSerializer.ParseTimestamp(message);
                var latencyMs = (DateTime.UtcNow.Ticks - sentAt) / TimeSpan.TicksPerMillisecond;
                LatencyMeasured?.Invoke(this, latencyMs);
                break;

            case MessageType.Disconnect:
                _ = DisconnectAsync();
                break;
        }
    }

    private async Task WriteAsync(byte[] message)
    {
        if (_stream is null)
        {
            return;
        }

        await _stream.WriteAsync(message);
    }
}
