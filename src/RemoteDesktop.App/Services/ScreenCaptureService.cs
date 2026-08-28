using System.Runtime.InteropServices;
using RemoteDesktop.App.Protocol;
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
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 2;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private static readonly Guid DxgiDeviceGuid = new("A2BFEA4A-771F-44DD-9819-99D0BE320319");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppvObject);

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", SetLastError = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    public static IDirect3DDevice CreateDevice()
    {
        Exception? lastError = null;
        foreach (var driverType in new[] { DriverTypeHardware, DriverTypeWarp })
        {
            try
            {
                return CreateDeviceInternal(driverType);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new COMException("Unable to create a Direct3D device for screen capture.");
    }

    private static IDirect3DDevice CreateDeviceInternal(int driverType)
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            7,
            out var d3dDevice,
            out _,
            out var immediateContext);

        if (hr < 0)
        {
            throw new COMException("D3D11CreateDevice failed.", hr);
        }

        try
        {
            var dxgiDevice = QueryDxgiDevice(d3dDevice);
            try
            {
                var createHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var winRtDevice);
                if (createHr != 0)
                {
                    throw new COMException("CreateDirect3D11DeviceFromDXGIDevice failed.", unchecked((int)createHr));
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
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (immediateContext != IntPtr.Zero)
            {
                Marshal.Release(immediateContext);
            }

            Marshal.Release(d3dDevice);
        }
    }

    private static IntPtr QueryDxgiDevice(IntPtr d3dDevice)
    {
        var vtable = Marshal.ReadIntPtr(d3dDevice);
        var queryInterfacePtr = Marshal.ReadIntPtr(vtable);
        var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryInterfacePtr);
        var iid = DxgiDeviceGuid;
        var hr = queryInterface(d3dDevice, ref iid, out var dxgiDevice);
        if (hr < 0)
        {
            throw new COMException("QueryInterface for IDXGIDevice failed.", hr);
        }

        return dxgiDevice;
    }
}
