using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal static class MediaFoundationRuntime
{
    private static int _startupCount;
    private static readonly object Sync = new();

    public static void EnsureStarted()
    {
        lock (Sync)
        {
            if (_startupCount == 0)
            {
                MediaFactory.MFStartup(true).CheckError();
            }

            _startupCount++;
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_startupCount <= 0)
            {
                return;
            }

            _startupCount--;
            if (_startupCount == 0)
            {
                MediaFactory.MFShutdown().CheckError();
            }
        }
    }
}

internal static class MediaFoundationTransformFactory
{
    private const uint ClsctxInprocServer = 1;

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid clsid,
        IntPtr outer,
        uint context,
        in Guid iid,
        out IntPtr instance);

    public static IMFTransform CreateTransform(Guid clsid)
    {
        var iid = typeof(IMFTransform).GUID;
        var hr = CoCreateInstance(clsid, IntPtr.Zero, ClsctxInprocServer, iid, out var pointer);
        Marshal.ThrowExceptionForHR(hr);
        return new IMFTransform(pointer);
    }
}

internal static class MediaFoundationMediaTypeBuilder
{
    public static IMFMediaType CreatePartialVideoType(Guid subtype) =>
        CreateVideoType(subtype, 0, 0, 0);

    public static IMFMediaType CreateVideoType(
        Guid subtype,
        int width,
        int height,
        int fps,
        int bitrate = 0,
        int stride = 0,
        int mpeg2Profile = 0)
    {
        var mediaType = MediaFactory.MFCreateMediaType();
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype);
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);

        if (width > 0 && height > 0)
        {
            mediaType.Set(
                MediaTypeAttributeKeys.FrameSize,
                PackSize(width, height));
        }

        if (fps > 0)
        {
            mediaType.Set(
                MediaTypeAttributeKeys.FrameRate,
                PackRatio(fps, 1));
        }

        if (stride != 0)
        {
            mediaType.Set(MediaTypeAttributeKeys.DefaultStride, stride);
        }

        if (bitrate > 0)
        {
            mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            mediaType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, 8u);
        }

        if (mpeg2Profile > 0)
        {
            mediaType.Set(new Guid("AD76A80B-2D5C-4E0B-B375-64E520137036"), (uint)mpeg2Profile);
        }

        return mediaType;
    }

    public static (int Width, int Height) ReadFrameSize(IMFMediaType mediaType)
    {
        var packed = mediaType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        return ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
    }

    private static ulong PackSize(int width, int height) =>
        ((ulong)(uint)width << 32) | (uint)height;

    private static ulong PackRatio(int numerator, int denominator) =>
        ((ulong)(uint)numerator << 32) | (uint)denominator;
}

internal static class MediaFoundationSampleFactory
{
    public static IMFSample CreateSampleFromBuffer(byte[] buffer, long sampleTime100ns, long duration100ns)
    {
        var sample = MediaFactory.MFCreateSample();
        var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(buffer.Length);
        mediaBuffer.Lock(out var pointer, out _, out _);
        try
        {
            Marshal.Copy(buffer, 0, pointer, buffer.Length);
            mediaBuffer.CurrentLength = buffer.Length;
        }
        finally
        {
            mediaBuffer.Unlock();
        }

        sample.AddBuffer(mediaBuffer);
        sample.SampleTime = sampleTime100ns;
        sample.SampleDuration = duration100ns;
        return sample;
    }

    public static byte[] CopySampleBuffer(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var currentLength);
        try
        {
            var bytes = new byte[currentLength];
            Marshal.Copy(pointer, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }
}

internal static class MediaFoundationTransformHelper
{
    private const int TransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int TransformStreamChange = unchecked((int)0xC00D6D61);

    public static void SendStreamMessages(IMFTransform transform)
    {
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public static byte[]? ProcessOutput(
        IMFTransform transform,
        ref bool outputConfigured,
        out int outputWidth,
        out int outputHeight,
        int minBufferSize = 0)
    {
        outputWidth = 0;
        outputHeight = 0;

        while (true)
        {
            try
            {
                var bytes = TryProcessOutputOnce(transform, minBufferSize);
                if (bytes is null || bytes.Length == 0)
                {
                    return null;
                }

                if (outputConfigured)
                {
                    using var currentType = transform.GetOutputCurrentType(0);
                    (outputWidth, outputHeight) = MediaFoundationMediaTypeBuilder.ReadFrameSize(currentType);
                }

                return bytes;
            }
            catch (SharpGenException ex) when (IsStreamChange(ex.HResult))
            {
                using var availableType = transform.GetOutputAvailableType(0, 0);
                (outputWidth, outputHeight) = MediaFoundationMediaTypeBuilder.ReadFrameSize(availableType);
                transform.SetOutputType(0, availableType, 0);
                outputConfigured = true;
            }
            catch (SharpGenException ex) when (IsNeedMoreInput(ex.HResult))
            {
                return null;
            }
        }
    }

    private static byte[]? TryProcessOutputOnce(IMFTransform transform, int minBufferSize)
    {
        var streamInfo = transform.GetOutputStreamInfo(0);
        var outputBuffer = new OutputDataBuffer
        {
            StreamID = 0,
            Status = 0,
        };

        IMFSample? callerSample = null;
        if (((OutputStreamInfoFlags)streamInfo.Flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
        {
            callerSample = MediaFactory.MFCreateSample();
            var bufferSize = Math.Max(Math.Max(streamInfo.Size, minBufferSize), 1);
            var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
            callerSample.AddBuffer(mediaBuffer);
            outputBuffer.Sample = callerSample;
        }

        try
        {
            transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputBuffer,
                out _);
        }
        finally
        {
            if (outputBuffer.Sample != callerSample)
            {
                callerSample?.Dispose();
            }
        }

        if (outputBuffer.Sample is null)
        {
            return null;
        }

        using (outputBuffer.Sample)
        {
            return MediaFoundationSampleFactory.CopySampleBuffer(outputBuffer.Sample);
        }
    }

    public static bool IsNeedMoreInput(int hresult) => hresult == TransformNeedMoreInput;

    private static bool IsStreamChange(int hresult) => hresult == TransformStreamChange;
}
