using System.Runtime.InteropServices;

namespace SwissOlly.App.Services;

/// <summary>
/// The notification-area icon. Its messages arrive through the main window's subclass (see
/// <c>ShellMessageInterop</c>), so no extra window exists. Call <see cref="Add"/> again when
/// Explorer restarts, which drops every icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>WM_APP-range message the shell sends to the main window for icon events.</summary>
    public const uint CallbackMessage = 0x8000 + 0x5A;

    public const int OpenCommand = 1;
    public const int ExitCommand = 2;

    // With NOTIFYICON_VERSION_4 the event is the low word of lParam.
    public const uint SelectEvent = 0x0400;        // NIN_SELECT: click
    public const uint KeySelectEvent = 0x0401;     // NIN_KEYSELECT: Enter or Space
    public const uint ContextMenuEvent = 0x007B;   // WM_CONTEXTMENU: right-click or Shift+F10
    public const uint DoubleClickEvent = 0x0203;   // WM_LBUTTONDBLCLK

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NiifInfo = 0x01;
    private const uint NiifRespectQuietTime = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint IconId = 1;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNoNotify = 0x0080;
    private const uint WmNull = 0x0000;

    public static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

    private readonly nint _windowHandle;
    private readonly nint _iconHandle;
    private readonly string _tooltip;
    private bool _added;

    public TrayIcon(nint windowHandle, nint iconHandle, string tooltip)
    {
        _windowHandle = windowHandle;
        _iconHandle = iconHandle;
        _tooltip = tooltip;
    }

    public bool IsAdded => _added;

    public void Add()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (!_added)
        {
            // Already present (Explorer did not restart after all): refresh it instead.
            _added = Shell_NotifyIconW(NimModify, ref data);
        }

        data.uVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref data);
    }

    /// <summary>A one-line notification next to the icon; Windows shows it as a toast.</summary>
    public void ShowNotice(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NifInfo);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NiifInfo | NiifRespectQuietTime;
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <summary>Shows Open / Exit at the cursor and returns the chosen command, or 0.</summary>
    public int ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            AppendMenuW(menu, MfString, OpenCommand, "Open SwissOlly");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, ExitCommand, "Exit SwissOlly");

            // Without this the menu does not close when the user clicks elsewhere.
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, x, y, _windowHandle, 0);
            PostMessageW(_windowHandle, WmNull, 0, 0);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(0);
        Shell_NotifyIconW(NimDelete, ref data);
        _added = false;
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _windowHandle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _iconHandle,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nint itemId, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint windowHandle, nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint windowHandle, uint message, nint wParam, nint lParam);
}
