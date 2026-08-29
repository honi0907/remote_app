using System.Net;
using System.Net.Sockets;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public sealed class SessionServer : IAsyncDisposable
{
    private readonly MessageReader _reader = new();
    private readonly object _clientSync = new();
    private readonly object _frameSendSync = new();
    private readonly Queue<EncodedStreamFrame> _frameQueue = new();
    private Task? _frameSendTask;
    private const int MaxQueuedH264Frames = 12;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _readTask;
    private string _pin = string.Empty;
    private bool _authenticated;

    public event EventHandler<string>? ClientConnectionRequested;
    public event EventHandler? ClientConnected;
    public event EventHandler? ClientDisconnected;

    public bool HasAuthenticatedClient
    {
        get
        {
            lock (_clientSync)
            {
                return _authenticated && _client?.Connected == true;
            }
        }
    }

    public async Task StartAsync(string pin, CancellationToken cancellationToken)
    {
        _pin = pin;
        await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, RemoteConstants.SessionPort);
        _listener.Start();

        _acceptTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    await HandleIncomingClientAsync(client, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    public async Task ApprovePendingClientAsync(bool approved)
    {
        NetworkStream? stream;
        lock (_clientSync)
        {
            stream = _stream;
        }

        if (stream is null)
        {
            return;
        }

        var response = MessageSerializer.BuildConnectionResponse(
            approved ? ConnectionResponseKind.Accepted : ConnectionResponseKind.Rejected);
        await stream.WriteAsync(response, CancellationToken.None);

        if (!approved)
        {
            await DisconnectClientAsync();
        }
    }

    public async Task SendStreamConfigAsync(StreamConfigMessage config)
    {
        await WriteAsync(MessageSerializer.BuildStreamConfig(config));
    }

    public async Task SendStreamStatusAsync(string text)
    {
        await WriteAsync(MessageSerializer.BuildStreamStatus(text));
    }

    public async Task SendFrameAsync(EncodedStreamFrame frame)
    {
        if (frame.Payload.Length == 0)
        {
            return;
        }

        NetworkStream? stream;
        lock (_clientSync)
        {
            if (!_authenticated)
            {
                return;
            }

            stream = _stream;
        }

        if (stream is null)
        {
            return;
        }

        var message = frame.Codec switch
        {
            StreamCodec.H264 => MessageSerializer.BuildVideoFrame(frame.Metadata, frame.Payload, frame.IsKeyframe),
            StreamCodec.Jpeg => MessageSerializer.BuildFrame(frame.Metadata, frame.Payload),
            StreamCodec.H265 => MessageSerializer.BuildVideoFrame(frame.Metadata, frame.Payload, frame.IsKeyframe),
            _ => MessageSerializer.BuildFrame(frame.Metadata, frame.Payload),
        };
        await stream.WriteAsync(message);
    }

    public void QueueFrame(EncodedStreamFrame frame)
    {
        if (frame.Payload.Length == 0)
        {
            return;
        }

        lock (_frameSendSync)
        {
            if (frame.Codec == StreamCodec.Jpeg)
            {
                _frameQueue.Clear();
                _frameQueue.Enqueue(frame);
            }
            else if (frame.IsKeyframe)
            {
                _frameQueue.Clear();
                _frameQueue.Enqueue(frame);
            }
            else if (_frameQueue.Count < MaxQueuedH264Frames)
            {
                _frameQueue.Enqueue(frame);
            }

            if (_frameSendTask is null || _frameSendTask.IsCompleted)
            {
                _frameSendTask = Task.Run(SendQueuedFrameLoopAsync);
            }
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await DisconnectClientAsync();

        _listener?.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener = null;
        _acceptTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await DisconnectClientAsync();

        lock (_clientSync)
        {
            _client = client;
            client.NoDelay = true;
            _stream = client.GetStream();
            _authenticated = false;
        }

        _readTask = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);
        await Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NetworkStream? stream;
                lock (_clientSync)
                {
                    stream = _stream;
                }

                if (stream is null)
                {
                    break;
                }

                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                foreach (var message in _reader.Append(buffer.AsSpan(0, read)))
                {
                    await HandleMessageAsync(message);
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
            await DisconnectClientAsync();
        }
    }

    private async Task HandleMessageAsync(byte[] message)
    {
        if (!MessageSerializer.TryParseType(message, out var type))
        {
            return;
        }

        switch (type)
        {
            case MessageType.ConnectionRequest:
                var viewerName = MessageSerializer.ParseConnectionRequest(message);
                ClientConnectionRequested?.Invoke(this, viewerName);
                break;

            case MessageType.AuthRequest:
                var pin = MessageSerializer.ParseAuthRequest(message);
                var result = string.Equals(pin, _pin, StringComparison.Ordinal) ? AuthResult.Ok : AuthResult.InvalidPin;
                await WriteAsync(MessageSerializer.BuildAuthResponse(result));
                if (result == AuthResult.Ok)
                {
                    lock (_clientSync)
                    {
                        _authenticated = true;
                    }

                    ClientConnected?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    await DisconnectClientAsync();
                }

                break;

            case MessageType.MouseMove when _authenticated:
                var move = MessageSerializer.ParseMouseMove(message);
                InputInjector.MoveMouse(move.NormalizedX, move.NormalizedY);
                break;

            case MessageType.MouseButton when _authenticated:
                var button = MessageSerializer.ParseMouseButton(message);
                InputInjector.SetMouseButton(button.Button, button.IsDown, button.NormalizedX, button.NormalizedY);
                break;

            case MessageType.MouseWheel when _authenticated:
                var wheel = MessageSerializer.ParseMouseWheel(message);
                InputInjector.Wheel(wheel.Delta, wheel.NormalizedX, wheel.NormalizedY);
                break;

            case MessageType.Key when _authenticated:
                var key = MessageSerializer.ParseKey(message);
                InputInjector.SendKey(key.VirtualKey, key.Action);
                break;

            case MessageType.Ping:
                var timestamp = MessageSerializer.ParseTimestamp(message);
                await WriteAsync(MessageSerializer.BuildPong(timestamp));
                break;

            case MessageType.Disconnect:
                await DisconnectClientAsync();
                break;
        }
    }

    private async Task WriteAsync(byte[] message)
    {
        NetworkStream? stream;
        lock (_clientSync)
        {
            stream = _stream;
        }

        if (stream is null)
        {
            return;
        }

        await stream.WriteAsync(message);
    }

    private async Task DisconnectClientAsync()
    {
        Task? readTask;
        lock (_clientSync)
        {
            readTask = _readTask;
            _readTask = null;
            _authenticated = false;
            _stream?.Dispose();
            _client?.Close();
            _stream = null;
            _client = null;
        }

        if (readTask is not null)
        {
            try
            {
                await readTask;
            }
            catch (Exception)
            {
            }
        }

        ClientDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendQueuedFrameLoopAsync()
    {
        try
        {
            while (true)
            {
                EncodedStreamFrame frame;
                lock (_frameSendSync)
                {
                    if (_frameQueue.Count == 0)
                    {
                        _frameSendTask = null;
                        return;
                    }

                    frame = _frameQueue.Dequeue();
                }

                await SendFrameAsync(frame);
            }
        }
        catch (Exception)
        {
            lock (_frameSendSync)
            {
                _frameQueue.Clear();
                _frameSendTask = null;
            }
        }
    }
}
