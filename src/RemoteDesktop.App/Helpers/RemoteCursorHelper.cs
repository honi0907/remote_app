using System.Runtime.InteropServices;

namespace RemoteDesktop.App.Helpers;

public static class RemoteCursorHelper
{
    private static int _hideDepth;

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

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

            return;
        }

        if (_hideDepth <= 0)
        {
            return;
        }

        _hideDepth--;
        if (_hideDepth == 0)
        {
            while (ShowCursor(true) < 0)
            {
            }
        }
    }

    public static void ForceVisible()
    {
        _hideDepth = 0;
        while (ShowCursor(true) < 0)
        {
        }
    }
}
