using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace RemoteDesktop.App.Services.StreamEncoding.H264;

internal static class CodecApi
{
    private static readonly Guid Iid = new("901db4c7-133b-4bbd-88d6-4df5b1b3c4c8");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid GopSize = new("95d9d8aa-0d08-4862-ab6c-3e4d83dd71dd");
    private const ushort VtUi4 = 19;

    public static void ConfigureGop(IMFTransform transform, int gopFrames)
    {
        TrySetUint(transform, GopSize, (uint)Math.Max(1, gopFrames));
    }

    public static void RequestKeyframe(IMFTransform transform)
    {
        TrySetUint(transform, ForceKeyFrame, 1);
    }

    private static void TrySetUint(IMFTransform transform, Guid property, uint value)
    {
        var iid = Iid;
        var hr = Marshal.QueryInterface(transform.NativePointer, ref iid, out var pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var codecApi = Marshal.GetObjectForIUnknown(pointer) as ICodecApi;
            if (codecApi is null)
            {
                return;
            }

            var variant = new VariantUint { Vt = VtUi4, Value = value };
            codecApi.SetValue(property, variant);
        }
        catch (Exception)
        {
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

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

    [ComImport]
    [Guid("901db4c7-133b-4bbd-88d6-4df5b1b3c4c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig] int IsSupported(in Guid api);
        [PreserveSig] int IsModifiable(in Guid api);
        [PreserveSig] int GetParameterRange(in Guid api, IntPtr min, IntPtr max, IntPtr step);
        [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(in Guid api, IntPtr value);
        [PreserveSig] int GetValue(in Guid api, IntPtr value);
        [PreserveSig] int SetValue(in Guid api, in VariantUint value);
        [PreserveSig] int RegisterForEvent(in Guid api, IntPtr userData);
        [PreserveSig] int UnregisterForEvent(in Guid api);
        [PreserveSig] int SetAllDefaults();
        [PreserveSig] int SetValueWithNotify(in Guid api, IntPtr value, out IntPtr changed, out uint count);
        [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr changed, out uint count);
        [PreserveSig] int GetAllSettings(IntPtr stream);
        [PreserveSig] int SetAllSettings(IntPtr stream);
        [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changed, out uint count);
    }
}
