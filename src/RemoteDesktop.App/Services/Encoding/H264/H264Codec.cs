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
        CodecApi.ConfigureGop(_transform, 8);
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

        if (_frameIndex % 8 == 0)
        {
            CodecApi.RequestKeyframe(_transform);
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

    public string? LastError { get; private set; }

    public (byte[] Bgra, int Width, int Height) Decode(byte[] bitstream, int frameWidth, int frameHeight)
    {
        try
        {
            EnsureTransform();

            using var sample = MediaFoundationSampleFactory.CreateSampleFromBuffer(bitstream, 0, 0);
            _transform!.ProcessInput(0, sample, 0);

            var decoded = TryReadOutput(frameWidth, frameHeight);
            if (decoded.Bgra.Length > 0)
            {
                return decoded;
            }

            if (H264BitstreamHelper.IsDecodableKeyframe(bitstream))
            {
                _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                decoded = TryReadOutput(frameWidth, frameHeight);
                MediaFoundationTransformHelper.SendStreamMessages(_transform);
            }

            return decoded;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return ([], 0, 0);
        }
    }

    private (byte[] Bgra, int Width, int Height) TryReadOutput(int frameWidth, int frameHeight)
    {
        for (var outputTry = 0; outputTry < 8; outputTry++)
        {
            try
            {
                var output = MediaFoundationTransformHelper.ProcessOutput(
                    _transform!,
                    ref _outputConfigured,
                    out var outputWidth,
                    out var outputHeight);
                if (output is null || output.Length == 0)
                {
                    continue;
                }

                if (!Nv12Converter.TryResolveLayout(
                        output.Length,
                        frameWidth,
                        frameHeight,
                        outputWidth,
                        outputHeight,
                        out var width,
                        out var height,
                        out var stride))
                {
                    LastError = $"NV12サイズ不一致 {output.Length}B type={outputWidth}x{outputHeight} meta={frameWidth}x{frameHeight}";
                    continue;
                }

                var bgra = Nv12Converter.Nv12ToBgra(output, width, height, stride);
                LastError = null;
                return (bgra, width, height);
            }
            catch (SharpGenException ex) when (MediaFoundationTransformHelper.IsNeedMoreInput(ex.HResult))
            {
                LastError = "デコーダが追加入力待ち（キーフレーム未到達の可能性）";
                break;
            }
        }

        return ([], 0, 0);
    }

    public void Reset()
    {
        _transform?.Dispose();
        _transform = null;
        _outputConfigured = false;
        LastError = null;
    }

    public void Dispose() => Reset();

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
