using System.Runtime.InteropServices;
using RemoteDesktop.App.Protocol;

namespace RemoteDesktop.App.Services;

public static class InputInjector
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseMoveAbsolute = 0x8001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;

    private static int _captureWidth = 1;
    private static int _captureHeight = 1;

    public static void SetCaptureSize(int width, int height)
    {
        _captureWidth = Math.Max(1, width);
        _captureHeight = Math.Max(1, height);
    }

    public static void MoveMouse(double normalizedX, double normalizedY)
    {
        var x = ClampToScreenX(normalizedX);
        var y = ClampToScreenY(normalizedY);
        SendMouseInput(MouseMoveAbsolute, x, y, 0);
    }

    public static void SetMouseButton(MouseButtonKind button, bool isDown, double normalizedX, double normalizedY)
    {
        MoveMouse(normalizedX, normalizedY);
        var flags = button switch
        {
            MouseButtonKind.Left => isDown ? MouseLeftDown : MouseLeftUp,
            MouseButtonKind.Right => isDown ? MouseRightDown : MouseRightUp,
            MouseButtonKind.Middle => isDown ? MouseMiddleDown : MouseMiddleUp,
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
        SendMouseInput(flags, 0, 0, 0);
    }

    public static void Wheel(int delta, double normalizedX, double normalizedY)
    {
        MoveMouse(normalizedX, normalizedY);
        SendMouseInput(MouseWheel, 0, 0, delta);
    }

    public static void SendKey(int virtualKey, KeyAction action)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Ki = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Scan = 0,
                    Flags = action == KeyAction.Up ? KeyboardEventFlags.KeyUp : 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static int ClampToScreenX(double normalizedX)
    {
        var pixelX = (int)Math.Round(Math.Clamp(normalizedX, 0, 1) * (_captureWidth - 1));
        return NormalizeAbsoluteCoordinate(pixelX, _captureWidth);
    }

    private static int ClampToScreenY(double normalizedY)
    {
        var pixelY = (int)Math.Round(Math.Clamp(normalizedY, 0, 1) * (_captureHeight - 1));
        return NormalizeAbsoluteCoordinate(pixelY, _captureHeight);
    }

    private static int NormalizeAbsoluteCoordinate(int pixel, int dimension)
    {
        return (int)Math.Round(pixel * 65535.0 / Math.Max(1, dimension - 1));
    }

    private static void SendMouseInput(uint flags, int dx, int dy, int mouseData)
    {
        var input = new Input
        {
            Type = InputMouse,
            U = new InputUnion
            {
                Mi = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = (uint)mouseData,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public KeyboardEventFlags Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [Flags]
    private enum KeyboardEventFlags : uint
    {
        KeyUp = 0x0002,
    }
}
