using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using SwissOlly.App.Services;
using SwissOlly.App.ViewModels;
using WinRT.Interop;

namespace SwissOlly.App;

/// <summary>
/// Window lifetime outside the board itself: the tray icon and close-to-tray, bringing the window
/// forward when a second copy is launched, and following time-zone and regional-format changes.
/// </summary>
public sealed partial class ShellWindow
{
    private const uint WmSettingChange = 0x001A;
    private const uint WmTimeChange = 0x001E;

    private TrayIcon? _trayIcon;
    private nint _trayIconHandle;
    private bool _exitRequested;
    private bool _isHiddenToTray;

    private void InstallWindowBehavior()
    {
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            ShellMessageInterop.Install(windowHandle, OnShellMessage);
            AppWindow.Closing += OnAppWindowClosing;
            SyncTrayIcon();
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("InstallWindowBehavior", ex);
        }
    }

    /// <summary>The tray icon exists exactly while "keep running in the tray" is on.</summary>
    private void SyncTrayIcon()
    {
        if (_viewModel.MinimizeToTray)
        {
            if (_trayIcon is null)
            {
                var windowHandle = WindowNative.GetWindowHandle(this);
                _trayIconHandle = LoadTrayIconHandle(windowHandle);
                _trayIcon = new TrayIcon(windowHandle, _trayIconHandle, "SwissOlly");
            }

            if (!_trayIcon.IsAdded)
            {
                _trayIcon.Add();
                if (!_trayIcon.IsAdded)
                {
                    StartupTrace.Write("Tray icon not added; retrying");
                    RetryTrayIconUntilAdded();
                }
            }

            return;
        }

        RemoveTrayIcon();
    }

    private DispatcherQueueTimer? _trayIconRetryTimer;
    private int _trayIconRetriesLeft;

    /// <summary>
    /// Adding the icon can fail while Explorer is up but still busy at sign-in, and a Start with
    /// Windows launch then sat hidden with no icon to click. TaskbarCreated only covers an
    /// Explorer that starts later, so keep trying for a minute.
    /// </summary>
    private void RetryTrayIconUntilAdded()
    {
        _trayIconRetriesLeft = 20;
        if (_trayIconRetryTimer is null)
        {
            _trayIconRetryTimer = _uiQueue.CreateTimer();
            _trayIconRetryTimer.Interval = TimeSpan.FromSeconds(3);
            _trayIconRetryTimer.IsRepeating = true;
            _trayIconRetryTimer.Tick += (timer, _) =>
            {
                // Gone (tray turned off) or already back (TaskbarCreated).
                if (_trayIcon is null || _trayIcon.IsAdded)
                {
                    timer.Stop();
                    return;
                }

                if (--_trayIconRetriesLeft < 0)
                {
                    timer.Stop();
                    ShowMinimizedWithoutTrayIcon();
                    return;
                }

                _trayIcon.Add();
                if (_trayIcon.IsAdded)
                {
                    StartupTrace.Write("Tray icon added on retry");
                    timer.Stop();
                }
            };
        }

        _trayIconRetryTimer.Start();
    }

    /// <summary>
    /// Out of retries with the window hidden: with no icon there is no way back to it, so put it
    /// on the taskbar instead, minimized and without taking focus (a game may be in front).
    /// </summary>
    private void ShowMinimizedWithoutTrayIcon()
    {
        if (!_isHiddenToTray)
        {
            return;
        }

        StartupTrace.Write("Tray icon unavailable; showing the window minimized");
        _isHiddenToTray = false;
        // One call, so the restored window never flashes up before minimizing.
        ShowWindow(WindowNative.GetWindowHandle(this), SwShowMinNoActive);

        // Ready for when it's restored from the taskbar; nothing draws while minimized.
        SetPanelRenderingSuspended(false);
    }

    private void RemoveTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_trayIconHandle != 0)
        {
            NativeWindowMethods.DestroyIcon(_trayIconHandle);
            _trayIconHandle = 0;
        }
    }

    private static nint LoadTrayIconHandle(nint windowHandle)
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SwissOlly.ico");
        if (!File.Exists(iconPath))
        {
            return 0;
        }

        // The notification area draws a small icon at the window's DPI; loading that size avoids
        // a blurry downscale of the 32px one.
        var dpi = windowHandle != 0 ? GetDpiForWindow(windowHandle) : 96u;
        var size = GetSystemMetricsForDpi(SmCxSmIcon, dpi == 0 ? 96u : dpi);
        return NativeWindowMethods.LoadWindowIcon(iconPath, size > 0 ? size : 16);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || !_viewModel.MinimizeToTray || _trayIcon is not { IsAdded: true })
        {
            return;
        }

        args.Cancel = true;
        HideToTray(showNotice: true);
    }

    private void HideToTray(bool showNotice)
    {
        CancelActiveLayoutGesture();
        HideGlobalRangeEditor();
        _isHiddenToTray = true;
        AppWindow.Hide();
        // Nothing to draw for while hidden; collection, history and the overlay carry on.
        SetPanelRenderingSuspended(true);
        StartupTrace.Write("Hidden to tray");

        if (showNotice && !_viewModel.HasShownTrayNotice)
        {
            _trayIcon?.ShowNotice(
                "SwissOlly is still running",
                "It keeps recording in the notification area. Right-click the icon to exit.");
            _viewModel.HasShownTrayNotice = true;
        }

        // Persist now: a sign-out ends a tray-resident process without a normal close.
        DebouncePersistLayout();
    }

    private void ShowFromTray()
    {
        var wasHidden = _isHiddenToTray;
        _isHiddenToTray = false;
        AppWindow.Show(true);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore(true);
        }

        Activate();
        var windowHandle = WindowNative.GetWindowHandle(this);
        if (windowHandle != 0)
        {
            SetForegroundWindow(windowHandle);
        }

        if (wasHidden)
        {
            SetPanelRenderingSuspended(false);
            StartupTrace.Write("Restored from tray");
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void OnShellMessage(uint message, nint wParam, nint lParam)
    {
        if (message == TrayIcon.CallbackMessage)
        {
            var trayEvent = (uint)(lParam.ToInt64() & 0xFFFF);
            if (trayEvent is TrayIcon.SelectEvent or TrayIcon.KeySelectEvent or TrayIcon.DoubleClickEvent)
            {
                _ = _uiQueue.TryEnqueue(ShowFromTray);
            }
            else if (trayEvent == TrayIcon.ContextMenuEvent && _trayIcon is not null)
            {
                // Anchor point in screen coordinates, packed into wParam (version 4 callbacks).
                var x = (short)(wParam.ToInt64() & 0xFFFF);
                var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                var command = _trayIcon.ShowMenu(x, y);
                if (command == TrayIcon.OpenCommand)
                {
                    _ = _uiQueue.TryEnqueue(ShowFromTray);
                }
                else if (command == TrayIcon.ExitCommand)
                {
                    _ = _uiQueue.TryEnqueue(ExitApplication);
                }
            }
        }
        else if (message == TrayIcon.TaskbarCreatedMessage)
        {
            // Explorer restarted and forgot every notification icon.
            _ = _uiQueue.TryEnqueue(() =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.Add();
                }
            });
        }
        else if (message == SingleInstance.ActivateMessage)
        {
            _ = _uiQueue.TryEnqueue(ShowFromTray);
        }
        else if (message == WmTimeChange ||
                 (message == WmSettingChange && lParam != 0 &&
                  string.Equals(Marshal.PtrToStringUni(lParam), "intl", StringComparison.OrdinalIgnoreCase)))
        {
            // .NET caches the time zone and the regional formats for the life of the process, so
            // a time-zone switch or a change to 12/24-hour time would not reach the labels.
            _ = _uiQueue.TryEnqueue(OnRegionalSettingsChanged);
        }
    }

    private void OnRegionalSettingsChanged()
    {
        CultureInfo.CurrentCulture.ClearCachedData();
        StartupTrace.Write($"Time or regional settings changed // tz={TimeZoneInfo.Local.Id}");
        foreach (var panel in _viewModel.DashboardPanels)
        {
            panel.RefreshPresentation();
        }

        RefreshGlobalRangeControls();
        UpdateStatusText();
    }

    /// <summary>Smallest window, in view pixels, that still fits the header and one board column.</summary>
    private const double MinimumWindowWidth = 720;
    private const double MinimumWindowHeight = 560;
    private bool _minimumSizeTracksDpi;
    private double _minimumSizeScale;

    /// <summary>
    /// There was no minimum size, so the window could be dragged down to a sliver where the
    /// header overflowed and the summary strip wrapped every caption. The presenter takes raw
    /// pixels, so this is reapplied when the window moves to a monitor with another scale.
    /// </summary>
    private void ApplyMinimumWindowSize()
    {
        try
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter || _rootLayout.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var scale = xamlRoot.RasterizationScale > 0 ? xamlRoot.RasterizationScale : 1d;
            if (scale == _minimumSizeScale)
            {
                return;
            }

            _minimumSizeScale = scale;
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWindowWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumWindowHeight * scale);
            if (!_minimumSizeTracksDpi)
            {
                _minimumSizeTracksDpi = true;
                xamlRoot.Changed += (_, _) => ApplyMinimumWindowSize();
            }
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("ApplyMinimumWindowSize", ex);
        }
    }

    /// <summary>Applies "Start with Windows" immediately: there is nothing to validate.</summary>
    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            _autoLaunchService.SetEnabled(enabled);
            _viewModel.LaunchOnStartup = _autoLaunchService.IsEnabled();
            // SetEnabled can decline quietly (no executable path, or no Run key); only a change
            // that took is announced as saved.
            _announceSettingsSave = _viewModel.LaunchOnStartup == enabled;
            DebouncePersistLayout();
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("SetStartWithWindows", ex);
            _viewModel.SetStatus($"Couldn't change Start with Windows: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
        }

        RefreshSettingsDeck();
    }

    private void SetKeepRunningInTray(bool enabled)
    {
        _viewModel.MinimizeToTray = enabled;
        SyncTrayIcon();
        _announceSettingsSave = true;
        DebouncePersistLayout();
        RefreshSettingsDeck();
    }

    private const int SmCxSmIcon = 49;

    private const int SwShowMinNoActive = 7;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}

/// <summary>Forwards the main window's shell-related messages to the shell window.</summary>
internal static class ShellMessageInterop
{
    private const nint SubclassId = 1003;

    private static Action<uint, nint, nint>? s_onMessage;
    private static SubclassProc? s_procDelegate; // prevent GC collection

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    public static void Install(nint hwnd, Action<uint, nint, nint> onMessage)
    {
        s_onMessage = onMessage;
        s_procDelegate = SubclassCallback;
        SetWindowSubclass(hwnd, s_procDelegate, SubclassId, 0);
    }

    private static nint SubclassCallback(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == TrayIcon.CallbackMessage ||
            uMsg == TrayIcon.TaskbarCreatedMessage ||
            uMsg == SingleInstance.ActivateMessage ||
            uMsg == 0x001A ||
            uMsg == 0x001E)
        {
            try
            {
                s_onMessage?.Invoke(uMsg, wParam, lParam);
            }
            catch (Exception ex)
            {
                StartupTrace.WriteException("ShellMessage", ex);
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
