using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace RemoteDesktop.App.Helpers;

public static class AppWindowHelper
{
    public static AppWindow? GetAppWindow(Window? window)
    {
        if (window is null)
        {
            return null;
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }
}
