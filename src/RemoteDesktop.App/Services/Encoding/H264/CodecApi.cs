using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal static class CodecApi
{
    private static readonly Guid Iid = new("901db4c7-133b-4bbd-88d6-4df5b1b3c4c8");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid GopSize = new("95d9d8aa-0d08-4862-ab6c-3e4d83dd71dd");
    private static readonly Guid LowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid BPictureCount = new("8D390AAC-DC7C-458C-9B71-C36C7B16BA0F");
    private static readonly Guid QualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid RateControlMode = new("1C2074D0-6CF9-4910-8BA4-0C5BC756260A");
    private static readonly Guid CommonQuality = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");
    private static readonly Guid CabacEnable = new("EE6CAD62-D305-4248-A50E-6D867DC1738C");
    private const ushort VtUi4 = 19;
    private const ushort VtBool = 11;

    public static string LastStatus { get; private set; } = string.Empty;

    public static void ConfigureRealtime(IMFTransform transform, int gopFrames)
    {
        var gop = (uint)Math.Max(1, gopFrames);
        var lowLatencyAttr = SetAttributeUint(transform, LowLatency, 1);
        var gopAttr = SetAttributeUint(transform, GopSize, gop);
        var bAttr = SetAttributeUint(transform, BPictureCount, 0);
        var lowLatencyCodec = SetCodecUint(transform, LowLatency, 1);
        var lowLatencyBool = SetCodecBool(transform, LowLatency, true);
        var gopCodec = SetCodecUint(transform, GopSize, gop);
        var bCodec = SetCodecUint(transform, BPictureCount, 0);
        LastStatus =
            $"attr low={lowLatencyAttr:X8} gop={gopAttr:X8} b={bAttr:X8}; " +
            $"codec low={lowLatencyCodec:X8} lowBool={lowLatencyBool:X8} gop={gopCodec:X8} b={bCodec:X8}";
    }

    public static void ConfigureQuality(IMFTransform transform, bool preferQuality)
    {
        SetAttributeUint(transform, QualityVsSpeed, preferQuality ? 12u : 40u);
        SetAttributeUint(transform, RateControlMode, preferQuality ? 3u : 2u);
        SetAttributeUint(transform, CommonQuality, preferQuality ? 90u : 74u);
        SetAttributeUint(transform, CabacEnable, 1);
    }

    public static void ConfigureGop(IMFTransform transform, int gopFrames) =>
        ConfigureRealtime(transform, gopFrames);

    public static void RequestKeyframe(IMFTransform transform)
    {
        SetAttributeUint(transform, ForceKeyFrame, 1);
        SetCodecUint(transform, ForceKeyFrame, 1);
    }

    private static int SetAttributeUint(IMFTransform transform, Guid key, uint value)
    {
        var getAttributes = GetVtableDelegate<GetAttributesDelegate>(transform.NativePointer, 8);
        if (getAttributes is null)
        {
            return unchecked((int)0x80004001);
        }

        var hr = getAttributes(transform.NativePointer, out var attributes);
        if (hr != 0 || attributes == IntPtr.Zero)
        {
            return hr == 0 ? unchecked((int)0x80004003) : hr;
        }

        try
        {
            var setUint = GetVtableDelegate<SetUint32Delegate>(attributes, 21);
            return setUint is null
                ? unchecked((int)0x80004001)
                : setUint(attributes, key, value);
        }
        finally
        {
            Marshal.Release(attributes);
        }
    }

    private static int SetCodecUint(IMFTransform transform, Guid property, uint value)
    {
        var variant = new VariantUint { Vt = VtUi4, Value = value };
        return InvokeCodecSetValue(transform, property, variant);
    }

    private static int SetCodecBool(IMFTransform transform, Guid property, bool value)
    {
        var variant = new VariantUint { Vt = VtBool, Value = value ? 0xFFFFu : 0 };
        return InvokeCodecSetValue(transform, property, variant);
    }

    private static int InvokeCodecSetValue(IMFTransform transform, Guid property, VariantUint variant)
    {
        var iid = Iid;
        var hr = Marshal.QueryInterface(transform.NativePointer, ref iid, out var pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return hr == 0 ? unchecked((int)0x80004003) : hr;
        }

        try
        {
            var setValue = GetVtableDelegate<SetValueDelegate>(pointer, 9);
            return setValue is null
                ? unchecked((int)0x80004001)
                : setValue(pointer, property, variant);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static T? GetVtableDelegate<T>(IntPtr instance, int index)
        where T : class
    {
        if (instance == IntPtr.Zero)
        {
            return null;
        }

        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return function == IntPtr.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAttributesDelegate(IntPtr self, out IntPtr attributes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUint32Delegate(IntPtr self, in Guid key, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValueDelegate(IntPtr self, in Guid api, in VariantUint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct VariantUint
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Value;
        public uint Padding;
        public ulong Padding2;
    }
}
