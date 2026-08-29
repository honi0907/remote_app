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
    private bool _outputConfigured;

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
        _outputConfigured = true;
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
                var output = MediaFoundationTransformHelper.ProcessOutput(
                    _transform,
                    ref _outputConfigured,
                    out _,
                    out _);
                if (output is null || output.Length == 0)
                {
                    break;
                }

                return (output, H264BitstreamHelper.IsDecodableKeyframe(output));
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

        using var outputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.H264,
            width,
            height,
            fps,
            bitrate);
        transform.SetOutputType(0, outputType, 0);

        using var inputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.Nv12,
            width,
            height,
            fps,
            stride: stride);
        transform.SetInputType(0, inputType, 0);
    }

    private void DisposeTransform()
    {
        _transform?.Dispose();
        _transform = null;
        _initialized = false;
        _outputConfigured = false;
    }
}

internal sealed class H264Decoder : IDisposable
{
    private IMFTransform? _transform;
    private bool _outputConfigured;

    public byte[] Decode(byte[] bitstream, int width, int height)
    {
        EnsureTransform();

        using var sample = MediaFoundationSampleFactory.CreateSampleFromBuffer(bitstream, 0, 0);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _transform!.ProcessInput(0, sample, 0);
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

            for (var outputTry = 0; outputTry < 6; outputTry++)
            {
                try
                {
                    var output = MediaFoundationTransformHelper.ProcessOutput(
                        _transform!,
                        ref _outputConfigured,
                        out _,
                        out _);
                    if (output is null || output.Length == 0)
                    {
                        continue;
                    }

                    return Nv12Converter.Nv12ToBgra(output, width, height);
                }
                catch (SharpGenException ex) when (MediaFoundationTransformHelper.IsNeedMoreInput(ex.HResult))
                {
                    break;
                }
            }
        }

        return [];
    }

    public void Dispose()
    {
        _transform?.Dispose();
        _transform = null;
        _outputConfigured = false;
    }

    private void EnsureTransform()
    {
        if (_transform is not null)
        {
            return;
        }

        MediaFoundationRuntime.EnsureStarted();
        _transform = MediaFoundationTransformFactory.CreateTransform(H264MediaFoundationGuids.H264Decoder);

        using var inputType = MediaFoundationMediaTypeBuilder.CreatePartialVideoType(H264MediaFoundationGuids.H264);
        _transform.SetInputType(0, inputType, 0);

        using var outputType = _transform.GetOutputAvailableType(0, 0);
        _transform.SetOutputType(0, outputType, 0);

        MediaFoundationTransformHelper.SendStreamMessages(_transform);
        _outputConfigured = true;
    }
}

internal static class H264BitstreamHelper
{
    public static bool IsDecodableKeyframe(ReadOnlySpan<byte> data) =>
        ContainsNalType(data, 5) || (ContainsNalType(data, 7) && ContainsNalType(data, 8));

    public static bool ContainsIdr(ReadOnlySpan<byte> data) => ContainsNalType(data, 5);

    private static bool ContainsNalType(ReadOnlySpan<byte> data, int nalType)
    {
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if ((data[i + 3] & 0x1F) == nalType)
                {
                    return true;
                }
            }
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

            if (nalLength <= 0 || offset + nalLength > data.Length)
            {
                break;
            }

            if ((data[offset] & 0x1F) == nalType)
            {
                return true;
            }

            offset += (int)nalLength;
        }

        return false;
    }
}
