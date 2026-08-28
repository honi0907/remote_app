using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public sealed class LanDiscoveryService : IAsyncDisposable
{
    private readonly object _hostsSync = new();
    private readonly Dictionary<string, DiscoveredHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _broadcastTask;
    private Task? _listenTask;
    private UdpClient? _broadcastClient;
    private UdpClient? _listenClient;

    public event EventHandler<IReadOnlyList<DiscoveredHost>>? HostsChanged;

    public IReadOnlyList<DiscoveredHost> GetHosts()
    {
        lock (_hostsSync)
        {
            PruneExpiredHosts();
            return _hosts.Values.OrderBy(h => h.HostName).ToList();
        }
    }

    public async Task StartBroadcastingAsync(string hostName, int sessionPort, CancellationToken cancellationToken)
    {
        await StopAsync();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _broadcastClient = new UdpClient { EnableBroadcast = true };

        _broadcastTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var payload = BuildAnnouncement(hostName, sessionPort);
                    await _broadcastClient.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, RemoteConstants.DiscoveryPort));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Ignore transient network errors during discovery.
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken)
    {
        if (_listenTask is not null)
        {
            return;
        }

        _cts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenClient = new UdpClient(RemoteConstants.DiscoveryPort) { EnableBroadcast = true };

        _listenTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _listenClient.ReceiveAsync(_cts.Token);
                    if (!TryParseAnnouncement(result.Buffer, out var hostName, out var sessionPort))
                    {
                        continue;
                    }

                    var address = result.RemoteEndPoint.Address.ToString();
                    if (IPAddress.IsLoopback(result.RemoteEndPoint.Address))
                    {
                        continue;
                    }

                    UpsertHost(new DiscoveredHost(hostName, address, sessionPort, DateTime.UtcNow));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Ignore malformed packets.
                }
            }
        }, _cts.Token);

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_broadcastTask is not null)
        {
            try
            {
                await _broadcastTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _broadcastClient?.Dispose();
        _listenClient?.Dispose();
        _broadcastClient = null;
        _listenClient = null;
        _broadcastTask = null;
        _listenTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void UpsertHost(DiscoveredHost host)
    {
        lock (_hostsSync)
        {
            _hosts[host.Address] = host;
            PruneExpiredHosts();
            HostsChanged?.Invoke(this, _hosts.Values.OrderBy(h => h.HostName).ToList());
        }
    }

    private void PruneExpiredHosts()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-8);
        var expired = _hosts.Where(pair => pair.Value.LastSeenUtc < cutoff).Select(pair => pair.Key).ToList();
        foreach (var key in expired)
        {
            _hosts.Remove(key);
        }
    }

    private static byte[] BuildAnnouncement(string hostName, int sessionPort)
    {
        var nameBytes = Encoding.UTF8.GetBytes(hostName);
        var payload = new byte[RemoteConstants.DiscoveryMagic.Length + 2 + nameBytes.Length];
        Encoding.ASCII.GetBytes(RemoteConstants.DiscoveryMagic).CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(RemoteConstants.DiscoveryMagic.Length, 2), (ushort)sessionPort);
        nameBytes.CopyTo(payload.AsSpan(RemoteConstants.DiscoveryMagic.Length + 2));
        return payload;
    }

    private static bool TryParseAnnouncement(ReadOnlySpan<byte> buffer, out string hostName, out int sessionPort)
    {
        hostName = string.Empty;
        sessionPort = RemoteConstants.SessionPort;

        if (buffer.Length < RemoteConstants.DiscoveryMagic.Length + 2)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(buffer[..RemoteConstants.DiscoveryMagic.Length]);
        if (!string.Equals(magic, RemoteConstants.DiscoveryMagic, StringComparison.Ordinal))
        {
            return false;
        }

        sessionPort = BinaryPrimitives.ReadUInt16LittleEndian(buffer[RemoteConstants.DiscoveryMagic.Length..]);
        hostName = Encoding.UTF8.GetString(buffer[(RemoteConstants.DiscoveryMagic.Length + 2)..]);
        return !string.IsNullOrWhiteSpace(hostName);
    }
}
