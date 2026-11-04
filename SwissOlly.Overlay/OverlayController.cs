using System.Drawing;
using SwissOlly.Core.Models;
using SwissOlly.Overlay.Detection;
using SwissOlly.Overlay.Fps;
using SwissOlly.Overlay.Hotkeys;
using SwissOlly.Overlay.Rendering;

namespace SwissOlly.Overlay;

public sealed class OverlayController : IDisposable
{
    private readonly LayeredOverlayWindow _window;
    private readonly OverlayBitmapRenderer _renderer;
    private readonly GlobalHotkeyService _hotkey;
    private readonly object _renderLock = new();
    private System.Threading.Timer? _pollTimer;
    private readonly AdaptiveFpsSource _fps = new();

    // Polled on a timer thread; the hotkey and config changes arrive on the UI thread. Lock order
    // is visibility, then render; FPS start/stop has its own lock and never takes the others.
    private readonly OverlayVisibility _visibility = new();
    private readonly object _visibilityLock = new();
    private readonly object _fpsLock = new();
    private int _polling;
    private volatile OverlayConfig _config = new OverlayConfig().Normalize();

    private double _lastFps = double.NaN;
    private string? _fpsError;
    private FpsAccuracy _fpsAccuracy = FpsAccuracy.Unavailable;
    private bool _fpsUnavailableReported;
    private double _lastCpu = double.NaN;
    private double _lastGpu = double.NaN;
    private double _lastGpuTemp = double.NaN;
    private double _lastMemPct = double.NaN;

    public event Action<string>? StatusMessage;

    /// <summary>
    /// Whether the reading counts the game's present calls, is a compositor-derived estimate,
    /// or is unavailable. Present calls are not correlated with frames confirmed displayed.
    /// </summary>
    public FpsAccuracy FpsAccuracy => _fpsAccuracy;

    public OverlayController()
    {
        _window = new LayeredOverlayWindow();
        _renderer = new OverlayBitmapRenderer();
        _hotkey = new GlobalHotkeyService();
        _hotkey.HotkeyPressed += OnHotkeyPressed;

        _fps.FpsUpdated += fps =>
        {
            // A live reading clears any earlier failure. Without this the overlay latches to
            // "FPS: --" after the first transient error and never recovers for the session.
            _fpsError = null;
            _lastFps = fps;
            PushRender();
        };

        _fps.ErrorOccurred += msg =>
        {
            OverlayLog.Write($"FPS error: {msg}");
            _fpsError = msg switch
            {
                "exclusive-fullscreen" => "exclusive",
                "no-presents" => "idle",
                "etw-access-denied" => "access",
                _ when msg.StartsWith("etw-", StringComparison.Ordinal) => "unavailable",
                _ => "err",
            };
            _lastFps = double.NaN;
            PushRender();

            if (msg.StartsWith("etw-", StringComparison.Ordinal) && !_fpsUnavailableReported)
            {
                _fpsUnavailableReported = true;
                StatusMessage?.Invoke(msg == "etw-access-denied"
                    ? "FPS unavailable: sign out of Windows and back in so Performance Log Users access takes effect, then restart SwissOlly."
                    : "FPS unavailable: SwissOlly could not read this process's Windows present events. Restart SwissOlly and check the overlay log.");
            }
        };

        _fps.AccuracyChanged += accuracy =>
        {
            _fpsAccuracy = accuracy;

        };
    }

    public void Start()
    {
        _window.Start();
        _pollTimer = new System.Threading.Timer(PollForeground, null, 500, 500);
        OverlayLog.Write("OverlayController started  poll=500ms");
    }

    public void RegisterHotkey(IntPtr ownerHwnd)
    {
        // Only while the overlay is on: a registered hotkey is taken from every other app, which
        // is no trade for a key that does nothing.
        if (!_config.Enabled)
        {
            _hotkey.Unregister();
            return;
        }

        if (!_hotkey.Register(ownerHwnd, _config.HotkeyGesture))
            StatusMessage?.Invoke($"Could not register hotkey '{_config.HotkeyGesture}' — may already be in use.");
    }

    public void OnWmHotkey(int hotkeyId) => _hotkey.OnWmHotkey(hotkeyId);

    public void ApplyConfig(OverlayConfig config)
    {
        bool gestureChanged = config.HotkeyGesture != _config.HotkeyGesture;
        bool enabledChanged = config.Enabled != _config.Enabled;
        _config = config;
        _window.SetCorner(config.Corner);

        lock (_renderLock)
            _renderer.Invalidate();

        // Registered only while enabled, so turning the overlay on or off re-registers too.
        if (gestureChanged || enabledChanged)
            StatusMessage?.Invoke("__reregister_hotkey__");

        if (enabledChanged)
        {
            OverlayLog.Write($"Enabled changed → {config.Enabled}");
            // Either way the next poll judges the foreground window afresh.
            lock (_visibilityLock)
                _visibility.Reset();
            if (!config.Enabled)
            {
                StopFps();
                ApplyVisibility();
            }
        }

        PushRender();
    }

    public void OnSnapshot(MetricSnapshot snapshot)
    {
        if (!_config.Enabled) return;

        _lastCpu = ExtractSample(snapshot, "cpu-total", "usage") ?? _lastCpu;
        _lastGpu = ExtractSample(snapshot, "gpu-total", "usage") ?? _lastGpu;
        _lastGpuTemp = ExtractSample(snapshot, "gpu-temperature", "temperature") ?? _lastGpuTemp;

        double? usedGb = ExtractSample(snapshot, "memory", "used-gb");
        double? totalGb = ExtractSample(snapshot, "memory", "total-gb");
        if (usedGb.HasValue && totalGb.HasValue && totalGb.Value > 0)
            _lastMemPct = usedGb.Value / totalGb.Value * 100.0;

        PushRender();
    }

    private void PollForeground(object? state)
    {
        // Timer callbacks overlap when one runs long (stopping an ETW session can take seconds).
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            if (!_config.Enabled) return;

            var hwnd = Win32Interop.GetForegroundWindow();
            OverlayVisibilityChange change;
            lock (_visibilityLock)
                change = _visibility.OnPoll(hwnd, GameProcessClassifier.IsLikelyGame);

            if (change != OverlayVisibilityChange.None)
            {
                OverlayLog.Write($"Foreground 0x{hwnd:X} → {change}");
                Apply(change, hwnd);
            }
        }
        catch (Exception ex)
        {
            OverlayLog.Write($"PollForeground error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Apply(OverlayVisibilityChange change, IntPtr foreground)
    {
        switch (change)
        {
            case OverlayVisibilityChange.ShowForGame:
                ShowOverGame(foreground);
                break;
            case OverlayVisibilityChange.Hide:
                StopFps();
                ApplyVisibility();
                break;
            case OverlayVisibilityChange.Suppress:
            case OverlayVisibilityChange.ShowForHotkey:
                ApplyVisibility();
                break;
        }
    }

    private void ShowOverGame(IntPtr hwnd)
    {
        uint pid = GameProcessClassifier.GetProcessId(hwnd);
        string exeName = "?";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            exeName = process.ProcessName;
        }
        catch { }
        OverlayLog.Write($"Game detected pid={pid} exe={exeName} — showing");
        StartFps(pid);
        // Turned off, or hidden by a hotkey or another poll, while this one was starting up.
        if (!_config.Enabled || !_visibility.IsShown)
        {
            StopFps();
        }

        // Log game window position for debugging
        var gameRect = new Win32Interop.RECT();
        Win32Interop.GetWindowRect(hwnd, ref gameRect);
        OverlayLog.Write($"Game hwnd=0x{hwnd:X} rect=[{gameRect.Left},{gameRect.Top},{gameRect.Right},{gameRect.Bottom}]");

        var monitor = Win32Interop.MonitorFromWindow(hwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);

        // In exclusive fullscreen, GetWindowRect may report (0,0)-relative coordinates even
        // on a secondary monitor, causing MonitorFromWindow to return the wrong monitor.
        // Cross-check with the cursor: when a game is active, the cursor is captured to the
        // game's display — so a mismatch means we picked the wrong monitor.
        var cursorPt = new Win32Interop.POINT();
        Win32Interop.GetCursorPos(ref cursorPt);
        var cursorMonitor = Win32Interop.MonitorFromPoint(cursorPt, Win32Interop.MONITOR_DEFAULTTONEAREST);
        if (cursorMonitor != IntPtr.Zero && cursorMonitor != monitor)
        {
            OverlayLog.Write($"Monitor mismatch: window → 0x{monitor:X}, cursor @ ({cursorPt.X},{cursorPt.Y}) → 0x{cursorMonitor:X}. Using cursor monitor.");
            monitor = cursorMonitor;
        }

        var monInfo = new Win32Interop.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.MONITORINFO>() };
        Win32Interop.GetMonitorInfo(monitor, ref monInfo);
        OverlayLog.Write($"Overlay monitor rect=[{monInfo.rcMonitor.Left},{monInfo.rcMonitor.Top},{monInfo.rcMonitor.Right},{monInfo.rcMonitor.Bottom}]");

        _window.Reposition(_config.Corner, monitor, hwnd);
        ApplyVisibility();
    }

    private void OnHotkeyPressed()
    {
        if (!_config.Enabled) return;

        OverlayVisibilityChange change;
        lock (_visibilityLock)
            change = _visibility.OnHotkey();
        OverlayLog.Write($"Hotkey pressed → {change}");
        Apply(change, IntPtr.Zero);
    }

    private void ApplyVisibility()
    {
        // Under the lock, so a hotkey on the UI thread and a poll on the timer thread can't post
        // Show and Hide out of order and leave the window out of step with the state.
        lock (_visibilityLock)
        {
            // Off wins: a poll already past its Enabled check could otherwise show it after
            // the overlay was turned off.
            if (!_visibility.IsShown || !_config.Enabled)
            {
                _window.Hide();
                return;
            }

            // Force a fresh ULW call on every show — layered window content is lost on hide/re-show
            lock (_renderLock)
                _renderer.Invalidate();
            PushRender();
            _window.Show();
        }
    }

    // The FPS sources aren't thread-safe, and these are called from the poll timer and the UI thread.
    private void StartFps(uint pid)
    {
        lock (_fpsLock)
        {
            _lastFps = double.NaN;
            _fpsError = null;
            _fps.StartTracking(pid);
        }
    }

    private void StopFps()
    {
        lock (_fpsLock)
        {
            _fps.StopTracking();
            _lastFps = double.NaN;
            _fpsError = null;
        }
    }

    private void PushRender()
    {
        if (!_visibility.IsShown) return;

        lock (_renderLock)
        {
            var lines = BuildLines();
            var color = ParseColor(_config.TextColor);
            var (bmp, changed) = _renderer.Render(lines, color, _config.FontSizePx, (float)_config.Opacity);
            if (changed)
            {
                OverlayLog.Write($"PushRender: bitmap {bmp.Width}x{bmp.Height}, sending to window");
                _window.UpdateBitmap(bmp);
            }
        }
    }

    // Amber, then red: the same meaning as the dashboard's Warning and Critical. Critical was an
    // orange (#FF9761) that sat too close to the amber, and to the neon orange and yellow text
    // colours, to read as a second, more urgent step over a busy game scene.
    private static readonly Color WarningColor = Color.FromArgb(0xF0, 0xC9, 0x68);
    private static readonly Color CriticalColor = Color.FromArgb(0xFF, 0x5A, 0x4F);

    private OverlayLine[] BuildLines()
    {
        var metrics = _config.Mode switch
        {
            OverlayMode.Simple => ["fps"],
            OverlayMode.Advanced => ["fps", "cpu", "gpu", "gpu-temp", "memory"],
            OverlayMode.Custom => _config.CustomMetrics,
            _ => ["fps"],
        };

        var baseColor = ParseColor(_config.TextColor);
        var lines = new List<OverlayLine>(metrics.Count);

        foreach (var m in metrics)
        {
            // Values are right-aligned in a fixed-width field. Segoe UI uses tabular figures, so
            // a constant character count means a constant pixel width — without this the row
            // reflows every time a value crosses 9→10 or 99→100 and the HUD visibly twitches.
            switch (m)
            {
                case "fps":
                    string value = _fpsError != null || double.IsNaN(_lastFps)
                        ? "  --"
                        : _fpsAccuracy == FpsAccuracy.Approximate
                            ? Figures($"~{_lastFps,3:F0}")
                            : Figures($"{_lastFps,4:F0}");
                    lines.Add(new OverlayLine("FPS",
                        value,
                        baseColor));
                    break;
                case "cpu":
                    lines.Add(new OverlayLine("CPU", FormatPercent(_lastCpu), UsageColor(_lastCpu, baseColor)));
                    break;
                case "gpu":
                    lines.Add(new OverlayLine("GPU", FormatPercent(_lastGpu), UsageColor(_lastGpu, baseColor)));
                    break;
                case "gpu-temp":
                    lines.Add(new OverlayLine("GPU Temp",
                        double.IsNaN(_lastGpuTemp) ? " --" : Figures($"{_lastGpuTemp,3:F0}°C"),
                        TempColor(_lastGpuTemp, baseColor)));
                    break;
                case "memory":
                    lines.Add(new OverlayLine("MEM", FormatPercent(_lastMemPct), UsageColor(_lastMemPct, baseColor)));
                    break;
                default:
                    lines.Add(new OverlayLine(m, string.Empty, baseColor));
                    break;
            }
        }

        return [.. lines];
    }

    private static string FormatPercent(double value)
        => double.IsNaN(value) ? " --%" : Figures($"{value,3:F0}%");

    /// <summary>
    /// Pads with figure spaces (U+2007), which are as wide as a digit. The padding was plain
    /// spaces, about half a digit wide in Segoe UI, so a value still moved sideways crossing
    /// 9→10 or 99→100 despite the fixed character count.
    /// </summary>
    private static string Figures(string text) => text.Replace(' ', ' ');

    private static Color UsageColor(double percent, Color normal)
        => double.IsNaN(percent) ? normal
         : percent > 90 ? CriticalColor
         : percent > 75 ? WarningColor
         : normal;

    // GPU temperature bands rather than percentages: 85°C is the usual throttle neighbourhood.
    private static Color TempColor(double celsius, Color normal)
        => double.IsNaN(celsius) ? normal
         : celsius > 85 ? CriticalColor
         : celsius > 75 ? WarningColor
         : normal;

    private static double? ExtractSample(MetricSnapshot snapshot, string panelKey, string seriesKey)
    {
        foreach (var s in snapshot.Samples)
        {
            if (string.Equals(s.PanelKey, panelKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.SeriesKey, seriesKey, StringComparison.OrdinalIgnoreCase))
                return s.Value;
        }
        return null;
    }

    private static Color ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return Color.FromArgb(0x66, 0xE7, 0xFF); }
    }

    public void Dispose()
    {
        // Let a poll that's already running finish before the FPS source and window go.
        if (_pollTimer is not null)
        {
            using var pollsDone = new ManualResetEvent(false);
            if (_pollTimer.Dispose(pollsDone))
                pollsDone.WaitOne(TimeSpan.FromSeconds(6));
        }

        StopFps();
        _fps.Dispose();
        _hotkey.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
