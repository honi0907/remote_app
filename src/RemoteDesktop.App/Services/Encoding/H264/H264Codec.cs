using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal sealed class H264Encoder : IDisposable
{
    private IMFTransform? _transform;
    private int _width;
    private int _height;
    private int _fps;
    private long _frameDuration100ns;
    private long _frameIndex;
    private bool _initialized;

    public void Initialize(int width, int height, int fps, int bitrateKbps)
    {
        if (_initialized && _width == width && _height == height && _fps == fps)
        {
            return;
        }

        DisposeTransform();
        MediaFoundationRuntime.EnsureStarted();

        _transform = MediaFoundationTransformFactory.CreateTransform(H264MediaFoundationGuids.H264Encoder);
        ConfigureTransform(_transform, width, height, fps, bitrateKbps * 1000);
        MediaFoundationTransformHelper.SendStreamMessages(_transform);

        _width = width;
        _height = height;
        _fps = fps;
        _frameDuration100ns = 10_000_000 / Math.Max(1, fps);
        _frameIndex = 0;
        _initialized = true;
    }

    public (byte[] Data, bool IsKeyframe) EncodeNv12(byte[] nv12)
    {
        if (_transform is null)
        {
            throw new InvalidOperationException("H.264 encoder is not initialized.");
        }

        var sampleTime = _frameIndex * _frameDuration100ns;
        using var sample = MediaFoundationSampleFactory.CreateSampleFromBuffer(nv12, sampleTime, _frameDuration100ns);
        _transform.ProcessInput(0, sample, 0);
        _frameIndex++;

        while (true)
        {
            try
            {
                var output = MediaFoundationTransformHelper.TryProcessOutput(_transform);
                if (output is null || output.Length == 0)
                {
                    break;
                }

                return (output, H264BitstreamHelper.ContainsIdr(output));
            }
            catch (SharpGenException ex) when (MediaFoundationTransformHelper.IsNeedMoreInput(ex.HResult))
            {
                break;
            }
        }

        return ([], false);
    }

    public void Dispose()
    {
        DisposeTransform();
    }

    private static void ConfigureTransform(IMFTransform transform, int width, int height, int fps, int bitrate)
    {
        var stride = Nv12Converter.GetStride(width);

        using var inputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.Nv12,
            width,
            height,
            fps,
            stride: stride);
        transform.SetInputType(0, inputType, 0);

        using var outputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.H264,
            width,
            height,
            fps,
            bitrate);
        transform.SetOutputType(0, outputType, 0);
    }

    private void DisposeTransform()
    {
        _transform?.Dispose();
        _transform = null;
        _initialized = false;
    }
}

internal sealed class H264Decoder : IDisposable
{
    private IMFTransform? _transform;
    private int _width;
    private int _height;

    public byte[] Decode(byte[] bitstream, int width, int height)
    {
        EnsureInitialized(width, height);

        using var sample = MediaFoundationSampleFactory.CreateSampleFromBuffer(bitstream, 0, 0);
        _transform!.ProcessInput(0, sample, 0);

        while (true)
        {
            try
            {
                var output = MediaFoundationTransformHelper.TryProcessOutput(_transform!);
                if (output is null || output.Length == 0)
                {
                    return [];
                }

                return Nv12Converter.Nv12ToBgra(output, _width, _height);
            }
            catch (SharpGenException ex) when (MediaFoundationTransformHelper.IsNeedMoreInput(ex.HResult))
            {
                return [];
            }
        }
    }

    public void Dispose()
    {
        _transform?.Dispose();
        _transform = null;
    }

    private void EnsureInitialized(int width, int height)
    {
        if (_transform is not null && _width == width && _height == height)
        {
            return;
        }

        _transform?.Dispose();
        MediaFoundationRuntime.EnsureStarted();
        _transform = MediaFoundationTransformFactory.CreateTransform(H264MediaFoundationGuids.H264Decoder);

        using var inputType = MediaFoundationMediaTypeBuilder.CreatePartialVideoType(H264MediaFoundationGuids.H264);
        _transform.SetInputType(0, inputType, 0);

        using var outputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.Nv12,
            width,
            height,
            30,
            stride: Nv12Converter.GetStride(width));
        _transform.SetOutputType(0, outputType, 0);

        MediaFoundationTransformHelper.SendStreamMessages(_transform);
        _width = width;
        _height = height;
    }
}

internal static class H264BitstreamHelper
{
    public static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        if (TryGetNalTypeAnnexB(data, out var annexType) && annexType == 5)
        {
            return true;
        }

        return TryGetNalTypeAvcc(data, out var avccType) && avccType == 5;
    }

    private static bool TryGetNalTypeAnnexB(ReadOnlySpan<byte> data, out int nalType)
    {
        nalType = 0;
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                nalType = data[i + 3] & 0x1F;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetNalTypeAvcc(ReadOnlySpan<byte> data, out int nalType)
    {
        nalType = 0;
        if (data.Length < 5)
        {
            return false;
        }

        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            var nalLength =
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];
            offset += 4;

            if (nalLength == 0 || offset + nalLength > data.Length)
            {
                return false;
            }

            nalType = data[offset] & 0x1F;
            if (nalType == 5)
            {
                return true;
            }

            offset += (int)nalLength;
        }

        return false;
    }
}
