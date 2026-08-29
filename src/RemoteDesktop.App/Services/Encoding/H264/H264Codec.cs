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
                if (output is null)
                {
                    break;
                }

                return (output, ContainsIdr(output));
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
            fps);
        transform.SetInputType(0, inputType, 0);
    }

    private static bool ContainsIdr(byte[] annexB)
    {
        for (var i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                var nalType = annexB[i + 3] & 0x1F;
                if (nalType == 5)
                {
                    return true;
                }
            }
        }

        return false;
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

    public byte[] Decode(byte[] annexB, int width, int height)
    {
        EnsureInitialized(width, height);

        using var sample = MediaFoundationSampleFactory.CreateSampleFromBuffer(annexB, 0, 0);
        _transform!.ProcessInput(0, sample, 0);

        while (true)
        {
            try
            {
                var output = MediaFoundationTransformHelper.TryProcessOutput(_transform!);
                if (output is null)
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

        using var inputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.H264,
            width,
            height,
            30);
        _transform.SetInputType(0, inputType, 0);

        using var outputType = MediaFoundationMediaTypeBuilder.CreateVideoType(
            H264MediaFoundationGuids.Nv12,
            width,
            height,
            30);
        _transform.SetOutputType(0, outputType, 0);

        MediaFoundationTransformHelper.SendStreamMessages(_transform);
        _width = width;
        _height = height;
    }
}
