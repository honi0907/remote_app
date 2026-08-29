using System.Runtime.InteropServices;

namespace RemoteDesktop.App.Helpers;

public static class RemoteCursorHelper
{
    private static int _hideDepth;
    private static bool _remoteInputActive;
    private static IntPtr _blankCursor;

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(
        IntPtr hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvANDPlane,
        byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    public static void SetRemoteInputActive(bool active)
    {
        _remoteInputActive = active;
        if (active)
        {
            SetHidden(true);
            ApplyBlankCursor();
            return;
        }

        SetHidden(false);
    }

    public static void ApplyBlankCursor()
    {
        if (!_remoteInputActive && _hideDepth <= 0)
        {
            return;
        }

        _ = SetCursor(BlankCursorHandle);
    }

    public static void SetHidden(bool hidden)
    {
        if (hidden)
        {
            _hideDepth++;
            if (_hideDepth == 1)
            {
                while (ShowCursor(false) >= 0)
                {
                }
            }

            ApplyBlankCursor();
            return;
        }

        if (_hideDepth <= 0)
        {
            return;
        }

        _hideDepth--;
        if (_hideDepth == 0 && !_remoteInputActive)
        {
            while (ShowCursor(true) < 0)
            {
            }
        }
    }

    public static void ForceVisible()
    {
        _remoteInputActive = false;
        _hideDepth = 0;
        while (ShowCursor(true) < 0)
        {
        }
    }

    private static IntPtr BlankCursorHandle
    {
        get
        {
            if (_blankCursor != IntPtr.Zero)
            {
                return _blankCursor;
            }

            var andMask = new byte[128];
            var xorMask = new byte[128];
            for (var i = 0; i < andMask.Length; i++)
            {
                andMask[i] = 0xFF;
            }

            _blankCursor = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
            return _blankCursor;
        }
    }
}
