using RemoteDesktop.App.Helpers;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Tests;

public static class ProtocolTests
{
    public static void RunAll()
    {
        TestAuthRoundTrip();
        TestConnectionRequestRoundTrip();
        TestMouseMoveRoundTrip();
        TestFrameRoundTrip();
        TestMessageReader();
        TestCoordinateMapper();
        Console.WriteLine("All protocol tests passed.");
    }

    private static void TestAuthRoundTrip()
    {
        const string pin = "362704";
        var bytes = MessageSerializer.BuildAuthRequest(pin);
        var parsed = MessageSerializer.ParseAuthRequest(bytes);
        if (!string.Equals(parsed, pin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Auth round-trip failed. Expected '{pin}', got '{parsed}'.");
        }
    }

    private static void TestConnectionRequestRoundTrip()
    {
        const string viewerName = "DESKTOP-1EK40GL";
        var bytes = MessageSerializer.BuildConnectionRequest(viewerName);
        var parsed = MessageSerializer.ParseConnectionRequest(bytes);
        if (!string.Equals(parsed, viewerName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connection request round-trip failed. Expected '{viewerName}', got '{parsed}'.");
        }
    }

    private static void TestMouseMoveRoundTrip()
    {
        var original = new MouseMoveMessage(0.25, 0.75);
        var bytes = MessageSerializer.BuildMouseMove(original);
        var parsed = MessageSerializer.ParseMouseMove(bytes);
        if (Math.Abs(parsed.NormalizedX - original.NormalizedX) > 0.0001 ||
            Math.Abs(parsed.NormalizedY - original.NormalizedY) > 0.0001)
        {
            throw new InvalidOperationException("Mouse move round-trip failed.");
        }
    }

    private static void TestFrameRoundTrip()
    {
        var metadata = new FrameMetadata(1920, 1080, DateTime.UtcNow.Ticks);
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var bytes = MessageSerializer.BuildFrame(metadata, jpeg);
        var (parsedMetadata, parsedJpeg) = MessageSerializer.ParseFrame(bytes);
        if (parsedMetadata.Width != metadata.Width ||
            parsedMetadata.Height != metadata.Height ||
            !parsedJpeg.SequenceEqual(jpeg))
        {
            throw new InvalidOperationException("Frame round-trip failed.");
        }
    }

    private static void TestMessageReader()
    {
        var reader = new MessageReader();
        var first = MessageSerializer.BuildPing(123);
        var second = MessageSerializer.BuildPong(456);
        var combined = first.Concat(second).ToArray();
        var messages = reader.Append(combined).ToList();
        if (messages.Count != 2)
        {
            throw new InvalidOperationException("Message reader failed to split messages.");
        }
    }

    private static void TestCoordinateMapper()
    {
        if (!CoordinateMapper.TryMapPointerToNormalized(400, 300, 800, 600, 1920, 1080, out var nx, out var ny))
        {
            throw new InvalidOperationException("Coordinate mapper failed for center point.");
        }

        if (Math.Abs(nx - 0.5) > 0.01 || Math.Abs(ny - 0.5) > 0.01)
        {
            throw new InvalidOperationException("Coordinate mapper center normalization failed.");
        }
    }
}
