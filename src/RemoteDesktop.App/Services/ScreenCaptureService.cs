using System.Runtime.InteropServices;
using RemoteDesktop.App.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace RemoteDesktop.App.Services;

public sealed class ScreenCaptureService : IAsyncDisposable
{
    private readonly FrameEncoder _frameEncoder = new();
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _device;
    private SizeInt32 _captureSize;
    private bool _isCapturing;

    public int CaptureWidth => _captureSize.Width;
    public int CaptureHeight => _captureSize.Height;

    public event EventHandler<(FrameMetadata Metadata, byte[] Jpeg)>? FrameCaptured;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isCapturing)
        {
            return;
        }

        var monitorHandle = MonitorHelper.GetPrimaryMonitorHandle();
        _captureItem = MonitorHelper.CreateItemForMonitor(monitorHandle);
        _captureSize = _captureItem.Size;

        _device = Direct3DHelper.CreateDevice();
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureSize);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = true;
        _session.StartCapture();
        _isCapturing = true;

        InputInjector.SetCaptureSize(_captureSize.Width, _captureSize.Height);
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isCapturing)
        {
            return Task.CompletedTask;
        }

        _framePool!.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _framePool?.Dispose();
        _captureItem = null;
        _session = null;
        _framePool = null;
        _device = null;
        _isCapturing = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _frameEncoder.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        try
        {
            var metadata = new FrameMetadata(_captureSize.Width, _captureSize.Height, DateTime.UtcNow.Ticks);
            var jpeg = _frameEncoder.EncodeFrame(frame.Surface, _captureSize.Width, _captureSize.Height);
            if (jpeg.Length > 0)
            {
                FrameCaptured?.Invoke(this, (metadata, jpeg));
            }
        }
        catch (Exception)
        {
            // Skip frames that cannot be encoded (protected content, transient GPU errors).
        }
    }
}

internal static class MonitorHelper
{
    private static readonly Guid GraphicsCaptureItemClassGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static IntPtr GetPrimaryMonitorHandle()
    {
        IntPtr primary = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);
        return primary;

        bool MonitorEnumCallback(IntPtr hMonitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            primary = hMonitor;
            return false;
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemGuid = GraphicsCaptureItemClassGuid;
        var itemPointer = interop.CreateForMonitor(monitorHandle, ref itemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != IntPtr.Zero)
            {
                Marshal.Release(itemPointer);
            }
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }
}

internal static class Direct3DHelper
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDevice()
    {
        var errors = new List<string>();

        foreach (var (label, factory) in DeviceFactories)
        {
            try
            {
                var d3dDevice = factory();
                try
                {
                    return CreateWinRtDevice(d3dDevice);
                }
                catch
                {
                    d3dDevice.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {FormatException(ex)}");
            }
        }

        throw new InvalidOperationException(
            "Direct3D デバイスを作成できませんでした。\n" + string.Join('\n', errors));
    }

    private static IEnumerable<(string Label, Func<ID3D11Device> Factory)> DeviceFactories =>
    [
        ("Hardware + BGRA", () => CreateWithDriver(DriverType.Hardware, DeviceCreationFlags.BgraSupport)),
        ("WARP + BGRA", () => CreateWithDriver(DriverType.Warp, DeviceCreationFlags.BgraSupport)),
        ("Hardware", () => CreateWithDriver(DriverType.Hardware, DeviceCreationFlags.None)),
        ("WARP", () => CreateWithDriver(DriverType.Warp, DeviceCreationFlags.None)),
    ];

    private static ID3D11Device CreateWithDriver(DriverType driverType, DeviceCreationFlags flags)
    {
        return D3D11.D3D11CreateDevice(driverType, flags, FeatureLevels);
    }

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var createHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winRtDevice);
        if (createHr != 0)
        {
            throw new COMException(
                $"CreateDirect3D11DeviceFromDXGIDevice failed (0x{createHr:X8}).",
                unchecked((int)createHr));
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(winRtDevice);
        }
        finally
        {
            Marshal.Release(winRtDevice);
        }
    }

    private static string FormatException(Exception ex)
    {
        if (ex is COMException comEx)
        {
            return $"{comEx.Message} (HRESULT 0x{comEx.HResult & 0xFFFFFFFF:X8})";
        }

        return ex.Message;
    }
}
