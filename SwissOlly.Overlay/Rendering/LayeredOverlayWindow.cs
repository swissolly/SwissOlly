using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SwissOlly.Overlay.Rendering;

internal sealed class LayeredOverlayWindow : IDisposable
{
    private const string WindowClass = "SwissOllyOverlay";
    private const int TimerTopmost = 1;
    private const int TopmostIntervalMs = 100;

    private IntPtr _hwnd;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _visible;

    // Pending update posted cross-thread
    private volatile PendingBitmap? _pendingBitmap;
    private const uint WM_UPDATE_BITMAP = 0x0401; // WM_USER+1
    private const uint WM_SHOW_HIDE = 0x0402;
    private const uint WM_REPOSITION = 0x0403;
    private const uint WM_SHUTDOWN = 0x0404;

    private OverlayCorner _corner = OverlayCorner.TopRight;
    private IntPtr _gameMonitor = IntPtr.Zero;
    private IntPtr _gameHwnd = IntPtr.Zero;
    internal IntPtr Handle => _hwnd;

    internal void Start()
    {
        _running = true;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "SwissOllyOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    internal void Show() => PostToWindow(WM_SHOW_HIDE, 1);
    internal void Hide() => PostToWindow(WM_SHOW_HIDE, 0);

    internal void UpdateBitmap(Bitmap bmp)
    {
        // Clone so the renderer can recycle its bitmap immediately
        var clone = (Bitmap)bmp.Clone();
        var old = Interlocked.Exchange(ref _pendingBitmap, new PendingBitmap(clone));
        old?.Bmp.Dispose();
        PostToWindow(WM_UPDATE_BITMAP, 0);
    }

    internal void Reposition(OverlayCorner corner, IntPtr gameMonitor, IntPtr gameHwnd)
    {
        _corner = corner;
        _gameMonitor = gameMonitor;
        _gameHwnd = gameHwnd;
        PostToWindow(WM_REPOSITION, 0);
    }

    private void PostToWindow(uint msg, int wParam)
    {
        if (_hwnd != IntPtr.Zero)
            Win32Interop.PostMessage(_hwnd, msg, new IntPtr(wParam), IntPtr.Zero);
    }

    private void MessageLoop()
    {
        Win32Interop.SetThreadDpiAwarenessContext(
            Win32Interop.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        RegisterWindowClass();
        CreateOverlayWindow();

        var msg = new Win32Interop.MSG();
        while (_running && Win32Interop.GetMessage(ref msg, IntPtr.Zero, 0, 0))
        {
            Win32Interop.TranslateMessage(ref msg);
            Win32Interop.DispatchMessage(ref msg);
        }
    }

    private void RegisterWindowClass()
    {
        // Class is registered once per process; ignore ERROR_CLASS_ALREADY_EXISTS
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = WndProcDelegate,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = Win32Interop.GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = WindowClass,
            hIconSm = IntPtr.Zero,
        };
        RegisterClassEx(ref wc);
    }

    private void CreateOverlayWindow()
    {
        int exStyle = Win32Interop.WS_EX_LAYERED
                    | Win32Interop.WS_EX_TRANSPARENT
                    | Win32Interop.WS_EX_TOPMOST
                    | Win32Interop.WS_EX_NOACTIVATE
                    | Win32Interop.WS_EX_TOOLWINDOW;

        _hwnd = Win32Interop.CreateWindowEx(
            exStyle, WindowClass, "SwissOllyOverlay",
            Win32Interop.WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            Win32Interop.GetModuleHandle(null), IntPtr.Zero);

        OverlayLog.Write($"CreateWindowEx → 0x{_hwnd:X}  err={Marshal.GetLastWin32Error()}");
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_UPDATE_BITMAP:
                ApplyPendingBitmap();
                return IntPtr.Zero;

            case WM_SHOW_HIDE:
                SetVisible(wParam.ToInt32() != 0);
                return IntPtr.Zero;

            case WM_REPOSITION:
                RepositionNow();
                return IntPtr.Zero;

            case WM_SHUTDOWN:
                _running = false;
                Win32Interop.PostQuitMessage(0);
                return IntPtr.Zero;

            case Win32Interop.WM_DPICHANGED:
                ApplyPendingBitmap();
                return IntPtr.Zero;

            case Win32Interop.WM_DISPLAYCHANGE:
                RepositionNow();
                return IntPtr.Zero;

            case (uint)WM_TIMER:
                if (wParam.ToInt32() == TimerTopmost && _visible)
                    ReassertTopmost();
                return IntPtr.Zero;

            case Win32Interop.WM_DESTROY:
                StopTopmostTimer();
                Win32Interop.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void SetVisible(bool show)
    {
        _visible = show;
        if (show)
        {
            bool swOk = ShowWindow(_hwnd, SW_SHOWNA);
            OverlayLog.Write($"ShowWindow(SW_SHOWNA) = {swOk}");
            ApplyPendingBitmap();   // re-apply if bitmap arrived before Show (usually null here — ULW already called in WM_UPDATE_BITMAP)
            ReassertTopmost();      // after ShowWindow so we land on top of other TOPMOST windows
            StartTopmostTimer();
        }
        else
        {
            StopTopmostTimer();
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private unsafe void ApplyPendingBitmap()
    {
        var pending = Interlocked.Exchange(ref _pendingBitmap, null);
        if (pending == null) return;

        var bmp = pending.Bmp;
        int w = bmp.Width;
        int h = bmp.Height;

        // Every handle is released in finally: an exception from LockBits/CopyMemory used to leak a
        // screen DC, memory DC and DIB section per frame until GDI's per-process quota ran out.
        var screenDc = IntPtr.Zero;
        var memDc = IntPtr.Zero;
        var hDib = IntPtr.Zero;
        var oldObj = IntPtr.Zero;
        try
        {
            screenDc = Win32Interop.GetDC(IntPtr.Zero);
            memDc = Win32Interop.CreateCompatibleDC(screenDc);

            // CreateDIBSection produces a 32bpp DIB section, which is what UpdateLayeredWindow requires.
            // GetHbitmap produces a DDB, causing UpdateLayeredWindow to silently return FALSE.
            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // negative = top-down, matches GDI+ memory layout
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32Interop.BI_RGB,
                }
            };

            hDib = Win32Interop.CreateDIBSection(memDc, ref bmi, Win32Interop.DIB_RGB_COLORS, out IntPtr ppvBits, IntPtr.Zero, 0);
            if (hDib == IntPtr.Zero)
            {
                return;
            }

            // Copy premultiplied BGRA pixels directly — GDI+ Format32bppArgb is stored BGRA in memory
            var data = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                Buffer.MemoryCopy(data.Scan0.ToPointer(), ppvBits.ToPointer(), (long)data.Stride * h, (long)data.Stride * h);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            oldObj = Win32Interop.SelectObject(memDc, hDib);

            var size = new Win32Interop.SIZE { CX = w, CY = h };
            var srcPt = new Win32Interop.POINT { X = 0, Y = 0 };
            var dstPt = GetCornerPosition(w, h);

            var blend = new Win32Interop.BLENDFUNCTION
            {
                BlendOp = Win32Interop.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32Interop.AC_SRC_ALPHA,
            };

            bool ulwOk = Win32Interop.UpdateLayeredWindow(
                _hwnd, screenDc, ref dstPt, ref size,
                memDc, ref srcPt, 0, ref blend, Win32Interop.ULW_ALPHA);
            OverlayLog.Write($"UpdateLayeredWindow({w}x{h} @ {dstPt.X},{dstPt.Y}) = {ulwOk}  err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        finally
        {
            if (oldObj != IntPtr.Zero) Win32Interop.SelectObject(memDc, oldObj);
            if (hDib != IntPtr.Zero) Win32Interop.DeleteObject(hDib);
            if (memDc != IntPtr.Zero) Win32Interop.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
            bmp.Dispose();
        }
    }

    private Win32Interop.POINT GetCornerPosition(int w, int h)
    {
        const int Margin = 16;
        var monitor = _gameMonitor != IntPtr.Zero ? _gameMonitor
            : Win32Interop.MonitorFromWindow(_hwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);

        var info = new Win32Interop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32Interop.MONITORINFO>() };
        Win32Interop.GetMonitorInfo(monitor, ref info);
        // Use full monitor bounds (rcMonitor) rather than work area (rcWork) so the overlay
        // anchors to the physical screen edge in borderless-fullscreen and windowed-fullscreen modes.
        var r = info.rcMonitor;

        return _corner switch
        {
            OverlayCorner.TopLeft     => new Win32Interop.POINT { X = r.Left  + Margin, Y = r.Top    + Margin },
            OverlayCorner.TopRight    => new Win32Interop.POINT { X = r.Right - w - Margin, Y = r.Top    + Margin },
            OverlayCorner.BottomLeft  => new Win32Interop.POINT { X = r.Left  + Margin, Y = r.Bottom - h - Margin },
            OverlayCorner.BottomRight => new Win32Interop.POINT { X = r.Right - w - Margin, Y = r.Bottom - h - Margin },
            _ => new Win32Interop.POINT { X = r.Right - w - Margin, Y = r.Top + Margin },
        };
    }

    private void RepositionNow()
    {
        if (!_visible || _hwnd == IntPtr.Zero) return;
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        Win32Interop.SetWindowPos(
            _hwnd, Win32Interop.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }

    private void StartTopmostTimer() =>
        SetTimer(_hwnd, TimerTopmost, TopmostIntervalMs, IntPtr.Zero);

    private void StopTopmostTimer() =>
        KillTimer(_hwnd, TimerTopmost);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostToWindow(WM_SHUTDOWN, 0);
            _thread?.Join(2000);
            _hwnd = IntPtr.Zero;
        }
        _pendingBitmap?.Bmp.Dispose();
    }

    // WndProc delegate kept alive
    private delegate IntPtr WndProcDelegate_t(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private readonly WndProcDelegate_t WndProcDelegate;

    public LayeredOverlayWindow()
    {
        WndProcDelegate = WndProc;
    }

    // ── Additional P/Invoke ────────────────────────────────────────────────
    private const int SW_SHOWNA = 8; // show without activating — critical for overlays
    private const int SW_HIDE = 0;
    private const uint WM_TIMER = 0x0113;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, int nIDEvent);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate_t lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private sealed class PendingBitmap(Bitmap bmp)
    {
        public Bitmap Bmp { get; } = bmp;
    }
}
