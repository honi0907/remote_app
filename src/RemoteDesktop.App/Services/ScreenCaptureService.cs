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
    private const int MonitorDefaultTopPrimary = 1;

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
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            primary = hMonitor;
            return false;
        }, IntPtr.Zero);
        return primary;
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        var interop = (IGraphicsCaptureItemInterop)ActivationFactory.Get(typeof(GraphicsCaptureItem));
        var itemGuid = typeof(GraphicsCaptureItem).GUID;
        var itemPointer = interop.CreateForMonitor(monitorHandle, ref itemGuid);
        return GraphicsCaptureItem.FromAbi(itemPointer);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23A0E92C655E")]
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
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private static readonly Guid DxgiDeviceGuid = new("A2BFEA4A-771F-44DD-9819-99D0BE320319");

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
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            DriverTypeHardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            7,
            out var d3dDevice,
            out _,
            out _);

        if (hr < 0)
        {
            throw new COMException("D3D11CreateDevice failed.", hr);
        }

        try
        {
            Marshal.QueryInterface(d3dDevice, in DxgiDeviceGuid, out var dxgiDevice).ThrowIfFailed();
            try
            {
                var createHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var winRtDevice);
                if (createHr != 0)
                {
                    throw new COMException("CreateDirect3D11DeviceFromDXGIDevice failed.", unchecked((int)createHr));
                }

                return MarshalInterface<IDirect3DDevice>.FromAbi(winRtDevice);
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
        }
    }
}
