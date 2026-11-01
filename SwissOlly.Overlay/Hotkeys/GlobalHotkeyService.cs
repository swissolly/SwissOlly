using System.Runtime.InteropServices;
using SwissOlly.Core.Models;

namespace SwissOlly.Overlay.Hotkeys;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0xBEEF;

    private IntPtr _ownerHwnd;
    private bool _registered;

    internal event Action? HotkeyPressed;

    internal bool Register(IntPtr ownerHwnd, string gesture)
    {
        Unregister();
        _ownerHwnd = ownerHwnd;

        if (!OverlayConfig.TryParseHotkeyGesture(gesture, out uint mods, out uint vk))
            return false;

        _registered = RegisterHotKey(ownerHwnd, HotkeyId, mods, vk);
        return _registered;
    }

    internal void Unregister()
    {
        if (_registered && _ownerHwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_ownerHwnd, HotkeyId);
            _registered = false;
        }
    }

    // Called from WndProc when WM_HOTKEY arrives
    internal void OnWmHotkey(int id)
    {
        if (id == HotkeyId)
            HotkeyPressed?.Invoke();
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
