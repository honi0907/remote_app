using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Protocol;

public static class MessageSerializer
{
    public static byte[] BuildAuthRequest(string pin) =>
        BuildStringPayload(MessageType.AuthRequest, pin);

    public static byte[] BuildAuthResponse(AuthResult result) =>
        Wrap(MessageType.AuthResponse, [(byte)result]);

    public static byte[] BuildConnectionRequest(string viewerName) =>
        BuildStringPayload(MessageType.ConnectionRequest, viewerName);

    public static byte[] BuildConnectionResponse(ConnectionResponseKind kind) =>
        Wrap(MessageType.ConnectionResponse, [(byte)kind]);

    public static byte[] BuildFrame(FrameMetadata metadata, byte[] jpegBytes)
    {
        var payload = new byte[20 + jpegBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), metadata.Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), metadata.Height);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), metadata.TimestampUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), jpegBytes.Length);
        jpegBytes.CopyTo(payload.AsSpan(20));
        return Wrap(MessageType.Frame, payload);
    }

    public static byte[] BuildStreamConfig(StreamConfigMessage config)
    {
        var payload = new byte[8];
        payload[0] = (byte)config.Codec;
        payload[1] = (byte)Math.Clamp(config.TargetFps, 1, 255);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2, 2), (short)Math.Clamp(config.MaxCaptureWidth, 0, short.MaxValue));
        payload[4] = (byte)Math.Clamp(config.JpegQuality, 1, 255);
        return Wrap(MessageType.StreamConfig, payload);
    }

    public static byte[] BuildVideoFrame(FrameMetadata metadata, byte[] h264Bytes, bool isKeyframe)
    {
        var payload = new byte[21 + h264Bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), metadata.Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), metadata.Height);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), metadata.TimestampUtcTicks);
        payload[16] = (byte)(isKeyframe ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(17, 4), h264Bytes.Length);
        h264Bytes.CopyTo(payload.AsSpan(21));
        return Wrap(MessageType.VideoFrame, payload);
    }

    public static byte[] BuildMouseMove(MouseMoveMessage message)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(0, 8), message.NormalizedX);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(8, 8), message.NormalizedY);
        return Wrap(MessageType.MouseMove, payload);
    }

    public static byte[] BuildMouseButton(MouseButtonMessage message)
    {
        var payload = new byte[18];
        payload[0] = (byte)message.Button;
        payload[1] = message.IsDown ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(2, 8), message.NormalizedX);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(10, 8), message.NormalizedY);
        return Wrap(MessageType.MouseButton, payload);
    }

    public static byte[] BuildMouseWheel(MouseWheelMessage message)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), message.Delta);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(4, 8), message.NormalizedX);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(12, 8), message.NormalizedY);
        return Wrap(MessageType.MouseWheel, payload);
    }

    public static byte[] BuildKey(KeyMessage message)
    {
        var payload = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), message.VirtualKey);
        payload[4] = (byte)message.Action;
        return Wrap(MessageType.Key, payload);
    }

    public static byte[] BuildPing(long timestampUtcTicks)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, timestampUtcTicks);
        return Wrap(MessageType.Ping, payload);
    }

    public static byte[] BuildPong(long timestampUtcTicks)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, timestampUtcTicks);
        return Wrap(MessageType.Pong, payload);
    }

    public static byte[] BuildDisconnect() =>
        Wrap(MessageType.Disconnect, ReadOnlySpan<byte>.Empty);

    public static bool TryParseType(ReadOnlySpan<byte> buffer, out MessageType type)
    {
        if (buffer.Length < 1)
        {
            type = default;
            return false;
        }

        type = (MessageType)buffer[0];
        return true;
    }

    public static string ParseAuthRequest(ReadOnlySpan<byte> payload) =>
        ParseStringPayload(payload);

    public static AuthResult ParseAuthResponse(ReadOnlySpan<byte> payload) =>
        Payload(payload).Length > 0 ? (AuthResult)Payload(payload)[0] : AuthResult.InvalidPin;

    public static string ParseConnectionRequest(ReadOnlySpan<byte> payload) =>
        ParseStringPayload(payload);

    public static ConnectionResponseKind ParseConnectionResponse(ReadOnlySpan<byte> payload) =>
        Payload(payload).Length > 0 ? (ConnectionResponseKind)Payload(payload)[0] : ConnectionResponseKind.Rejected;

    public static (FrameMetadata Metadata, byte[] Jpeg) ParseFrame(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        var width = BinaryPrimitives.ReadInt32LittleEndian(body[..4]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(body[4..8]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(body[8..16]);
        var jpegLength = BinaryPrimitives.ReadInt32LittleEndian(body[16..20]);
        var jpeg = body[20..(20 + jpegLength)].ToArray();
        return (new FrameMetadata(width, height, timestamp), jpeg);
    }

    public static StreamConfigMessage ParseStreamConfig(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        if (body.Length < 5)
        {
            return new StreamConfigMessage(StreamCodec.Jpeg, 24, 1280, 55);
        }

        var codec = Enum.IsDefined(typeof(StreamCodec), body[0])
            ? (StreamCodec)body[0]
            : StreamCodec.Jpeg;
        var fps = body[1];
        var maxWidth = BinaryPrimitives.ReadInt16LittleEndian(body[2..4]);
        var quality = body[4];
        return new StreamConfigMessage(codec, fps, maxWidth, quality);
    }

    public static (FrameMetadata Metadata, byte[] H264, bool IsKeyframe) ParseVideoFrame(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        var width = BinaryPrimitives.ReadInt32LittleEndian(body[..4]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(body[4..8]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(body[8..16]);
        var isKeyframe = body[16] == 1;
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(body[17..21]);
        var h264 = body[21..(21 + dataLength)].ToArray();
        return (new FrameMetadata(width, height, timestamp), h264, isKeyframe);
    }

    public static MouseMoveMessage ParseMouseMove(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        return new MouseMoveMessage(
            BinaryPrimitives.ReadDoubleLittleEndian(body[..8]),
            BinaryPrimitives.ReadDoubleLittleEndian(body[8..16]));
    }

    public static MouseButtonMessage ParseMouseButton(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        return new MouseButtonMessage(
            (MouseButtonKind)body[0],
            body[1] == 1,
            BinaryPrimitives.ReadDoubleLittleEndian(body[2..10]),
            BinaryPrimitives.ReadDoubleLittleEndian(body[10..18]));
    }

    public static MouseWheelMessage ParseMouseWheel(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        return new MouseWheelMessage(
            BinaryPrimitives.ReadInt32LittleEndian(body[..4]),
            BinaryPrimitives.ReadDoubleLittleEndian(body[4..12]),
            BinaryPrimitives.ReadDoubleLittleEndian(body[12..20]));
    }

    public static KeyMessage ParseKey(ReadOnlySpan<byte> payload)
    {
        var body = Payload(payload);
        return new KeyMessage(
            BinaryPrimitives.ReadInt32LittleEndian(body[..4]),
            (KeyAction)body[4]);
    }

    public static long ParseTimestamp(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadInt64LittleEndian(Payload(payload));

    private static string ParseStringPayload(ReadOnlySpan<byte> buffer)
    {
        var body = Payload(buffer);
        if (body.Length < 4)
        {
            return string.Empty;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(body[..4]);
        if (length < 0 || body.Length < 4 + length)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(body.Slice(4, length));
    }

    private static byte[] BuildStringPayload(MessageType type, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var payload = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), bytes.Length);
        bytes.CopyTo(payload.AsSpan(4));
        return Wrap(type, payload);
    }

    private static byte[] Wrap(MessageType type, ReadOnlySpan<byte> payload)
    {
        var message = new byte[5 + payload.Length];
        message[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1, 4), payload.Length);
        payload.CopyTo(message.AsSpan(5));
        return message;
    }

    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> buffer) =>
        buffer.Length <= 5 ? ReadOnlySpan<byte>.Empty : buffer[5..];
}

public sealed class MessageReader
{
    private readonly List<byte> _buffer = [];
    private readonly object _sync = new();

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> chunk)
    {
        var messages = new List<byte[]>();
        lock (_sync)
        {
            _buffer.AddRange(chunk.ToArray());

            while (_buffer.Count >= 5)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(CollectionsMarshal.AsSpan(_buffer)[1..5]);
                if (_buffer.Count < 5 + length)
                {
                    break;
                }

                messages.Add(_buffer.GetRange(0, 5 + length).ToArray());
                _buffer.RemoveRange(0, 5 + length);
            }
        }

        return messages;
    }
}
