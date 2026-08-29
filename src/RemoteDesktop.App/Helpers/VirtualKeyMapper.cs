using System.Runtime.InteropServices;
using RemoteDesktop.App.Protocol;
using RemoteDesktop.App.Services;

namespace RemoteDesktop.App.Helpers;

internal static class VirtualKeyMapper
{
    private const uint MapVkToVsc = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static InputInjector.KeyboardInputSpec ToKeyboardInput(int virtualKey, KeyAction action)
    {
        var vk = (ushort)(virtualKey & 0xFF);
        var scan = (ushort)(MapVirtualKey(vk, MapVkToVsc) & 0xFF);
        var flags = InputInjector.KeyboardEventFlags.None;
        if (action == KeyAction.Up)
        {
            flags |= InputInjector.KeyboardEventFlags.KeyUp;
        }

        if (IsExtendedKey(vk))
        {
            flags |= InputInjector.KeyboardEventFlags.ExtendedKey;
        }

        return new InputInjector.KeyboardInputSpec(vk, scan, flags);
    }

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true,
        0x2D or 0x2E => true,
        0x5B or 0x5C or 0x5D => true,
        0x6F or 0x90 => true,
        0xA2 or 0xA3 => true,
        _ => false,
    };
}
