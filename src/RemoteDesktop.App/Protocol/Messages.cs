namespace RemoteDesktop.App.Protocol;

public static class RemoteConstants
{
    public const int DiscoveryPort = 9847;
    public const int SessionPort = 9848;
    public const string DiscoveryMagic = "RDESK1";
}

public enum MessageType : byte
{
    AuthRequest = 1,
    AuthResponse = 2,
    Frame = 3,
    MouseMove = 4,
    MouseButton = 5,
    MouseWheel = 6,
    Key = 7,
    Disconnect = 8,
    Ping = 9,
    Pong = 10,
    ConnectionRequest = 11,
    ConnectionResponse = 12,
    StreamConfig = 13,
    VideoFrame = 14,
    StreamStatus = 15,
}

public enum MouseButtonKind : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

public enum KeyAction : byte
{
    Down = 0,
    Up = 1,
}

public enum AuthResult : byte
{
    Ok = 0,
    InvalidPin = 1,
    Rejected = 2,
}

public enum ConnectionResponseKind : byte
{
    Accepted = 0,
    Rejected = 1,
}

public readonly record struct FrameMetadata(int Width, int Height, long TimestampUtcTicks);

public readonly record struct MouseMoveMessage(double NormalizedX, double NormalizedY);

public readonly record struct MouseButtonMessage(MouseButtonKind Button, bool IsDown, double NormalizedX, double NormalizedY);

public readonly record struct MouseWheelMessage(int Delta, double NormalizedX, double NormalizedY);

public readonly record struct KeyMessage(int VirtualKey, KeyAction Action);

public sealed class DiscoveredHost
{
    public DiscoveredHost(string hostName, string address, int sessionPort, DateTime lastSeenUtc)
    {
        HostName = hostName;
        Address = address;
        SessionPort = sessionPort;
        LastSeenUtc = lastSeenUtc;
    }

    public string HostName { get; }
    public string Address { get; }
    public int SessionPort { get; }
    public DateTime LastSeenUtc { get; }
}
