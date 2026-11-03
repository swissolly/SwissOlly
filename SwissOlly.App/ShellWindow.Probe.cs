#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using SwissOlly.App.Controls;
using SwissOlly.Core.Models;

namespace SwissOlly.App;

// TEMPORARY QA PROBE — delete before committing. Runs only with SWISSOLLY_PROBE=1.
public sealed partial class ShellWindow
{
    private static readonly string ProbeDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swissolly-probe");

    private async void StartUiProbe()
    {
        if (Environment.GetEnvironmentVariable("SWISSOLLY_PROBE") != "1")
        {
            return;
        }

        Directory.CreateDirectory(ProbeDir);
        var steps = (Environment.GetEnvironmentVariable("SWISSOLLY_PROBE_STEPS") ?? "themes").Split(',');
        try
        {
            var sw = Stopwatch.StartNew();
            while (!_hasReceivedFirstSnapshot && sw.ElapsedMilliseconds < 30000)
            {
                await Task.Delay(200);
            }

            await Task.Delay(9000);
            ProbeLog($"start steps={string.Join('+', steps)} dpi={_rootLayout.XamlRoot?.RasterizationScale}");
            var originalTheme = _viewModel.SelectedTheme;
            foreach (var step in steps)
            {
                switch (step)
                {
                    case "themes": await ProbeThemes(); break;
                    case "sizes": await ProbeSizes(); break;
                    case "settings": await ProbeSettingsFlows(); break;
                    case "range": await ProbeRange(); break;
                    case "locale": await ProbeLocale(); break;
                    case "leak": await ProbeLeak(); break;
                    case "window": await ProbeWindowCard(); break;
                    case "states": await ProbeStates(); break;
                    case "perf": await ProbePerf(); break;
                    case "hover": await ProbeHoverTargets(); break;
                    case "cursor-old": await ProbeCursorChurn(oldPattern: true); break;
                    case "cursor-new": await ProbeCursorChurn(oldPattern: false); break;
                    case "cursor-popup-old": await ProbePopupCursorChurn(oldPattern: true); break;
                    case "cursor-popup-new": await ProbePopupCursorChurn(oldPattern: false); break;
                    case "settings2": await ProbeSettingsRedesign(); break;
                    case "pins": await ProbePins(); break;
                    case "restart-before":
                        await Task.Delay(TimeSpan.FromSeconds(120));
                        await ProbeCaptureForRestart("before");
                        ProbeLog("exiting through the tray Exit path");
                        File.WriteAllText(System.IO.Path.Combine(ProbeDir, "done.txt"), "done");
                        ExitApplication();
                        return;
                    case "restart-after":
                        await ProbeCaptureForRestart("after");
                        break;
                    case "menu": await ProbeContextMenu(); break;
                    case "narrow":
                        {
                            var size = AppWindow.Size;
                            AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));
                            await Task.Delay(1200);
                            OnSettingsNavigationClick(null, EventArgs.Empty);
                            await Task.Delay(800);
                            _draftOverlayMode = SwissOlly.Core.Models.OverlayMode.Custom;
                            RefreshSettingsDeck();
                            await SnapSettingsPages("narrow");
                            OnCancelSettingsClick(null, EventArgs.Empty);
                            OnOverviewNavigationClick(null, EventArgs.Empty);
                            AppWindow.Resize(size);
                            await Task.Delay(1000);
                            break;
                        }
                    case "idle": await Task.Delay(TimeSpan.FromMinutes(10)); break;
                }
            }

            App.CurrentApp.ApplyTheme(originalTheme);
            ProbeLog("done");
        }
        catch (Exception ex)
        {
            ProbeLog("FAILED " + ex);
        }
        finally
        {
            File.WriteAllText(System.IO.Path.Combine(ProbeDir, "done.txt"), "done");
        }
    }

    private static void ProbeLog(string text)
    {
        StartupTrace.Write("PROBE " + text);
        File.AppendAllText(System.IO.Path.Combine(ProbeDir, "probe.log"), $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
    }

    private static async Task Snap(string name, int settleMs = 450)
    {
        await Task.Delay(settleMs);
        var request = System.IO.Path.Combine(ProbeDir, "request.txt");
        File.WriteAllText(request, name);
        var sw = Stopwatch.StartNew();
        while (File.Exists(request) && sw.ElapsedMilliseconds < 15000)
        {
            await Task.Delay(50);
        }

        ProbeLog($"snap {name}{(File.Exists(request) ? " (timed out)" : string.Empty)}");
    }

    private TelemetryPanelCard? ProbeCard(string key) => _panelCards.TryGetValue(key, out var card) ? card : null;

    private static void ProbeInvoke(object target, string method, params object?[] args)
    {
        var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        info.Invoke(target, args);
    }

    private async Task ProbeThemes()
    {
        foreach (var theme in Enum.GetValues<ThemeMode>())
        {
            var name = theme.ToString().ToLowerInvariant();
            App.CurrentApp.ApplyTheme(theme);
            _scrollHost.ChangeView(null, 0, null, true);
            await Snap($"theme-{name}-overview", 900);

            if (ProbeCard("cpu-total") is { } cpu)
            {
                cpu.StartBringIntoView();
                await Task.Delay(300);
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Snap($"theme-{name}-popover", 700);
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                _scrollHost.ChangeView(null, 0, null, true);
            }

            RenderGlobalRangeEditor();
            await Snap($"theme-{name}-range", 600);
            HideGlobalRangeEditor();

            OnToggleLayoutEditClick(null, EventArgs.Empty);
            await Snap($"theme-{name}-edit", 700);
            OnCancelLayoutEditClick(null, EventArgs.Empty);
            await Task.Delay(400);

            OnSettingsNavigationClick(null, EventArgs.Empty);
            await Snap($"theme-{name}-settings", 900);
            _scrollHost.ChangeView(null, 100000, null, true);
            await Snap($"theme-{name}-settings-bottom", 500);
            OnOverviewNavigationClick(null, EventArgs.Empty);
            await Task.Delay(500);
        }
    }

    private async Task ProbeSizes()
    {
        var original = AppWindow.Size;
        foreach (var (w, h) in new[] { (500, 600), (720, 700), (900, 800), (1100, 800), (1366, 900), (2560, 1440) })
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            await Task.Delay(1200);
            _scrollHost.ChangeView(null, 0, null, true);
            await Snap($"size-{w}x{h}", 900);
            if (w <= 1000)
            {
                OnSettingsNavigationClick(null, EventArgs.Empty);
                await Snap($"size-{w}x{h}-settings", 900);
                OnOverviewNavigationClick(null, EventArgs.Empty);
                await Task.Delay(500);
            }
        }

        AppWindow.Resize(original);
        await Task.Delay(1000);
    }

    private async Task ProbeSettingsFlows()
    {
        OnSettingsNavigationClick(null, EventArgs.Empty);
        await Task.Delay(800);
        ProbeLog($"apply enabled before edit: {_settingsApplyChip?.IsEnabled}");

        _draftScrapeIntervalInput = "abc";
        RenderEditableControlDeck();
        ProbeLog($"apply enabled after edit: {_settingsApplyChip?.IsEnabled}");
        OnSaveSettingsClick(null, EventArgs.Empty);
        await Snap("settings-invalid-scrape", 700);
        ProbeLog($"status after invalid scrape: {_viewModel.StatusText} / draft kept: {_draftScrapeIntervalInput}");
        OnCancelSettingsClick(null, EventArgs.Empty);
        await Task.Delay(500);

        _draftRetentionInput = "99999999d";
        RenderEditableControlDeck();
        OnSaveSettingsClick(null, EventArgs.Empty);
        await Snap("settings-invalid-retention", 700);
        ProbeLog($"status after invalid retention: {_viewModel.StatusText}");
        OnCancelSettingsClick(null, EventArgs.Empty);
        await Task.Delay(500);

        // Lowering retention must ask first. Never confirm here: this is the real history.
        _draftRetentionInput = "6h";
        RenderEditableControlDeck();
        _scrollHost.ChangeView(null, 100000, null, true);
        await Task.Delay(500);
        OnSaveSettingsClick(null, EventArgs.Empty);
        await Snap("settings-retention-confirm", 900);
        ProbeLog($"confirm flyout open: {_retentionConfirmFlyout?.IsOpen} retention still {_viewModel.RetentionHoursInput}");
        _retentionConfirmFlyout?.Hide();
        await Task.Delay(300);
        OnCancelSettingsClick(null, EventArgs.Empty);
        await Snap("settings-after-revert", 600);
        ProbeLog($"apply enabled after revert: {_settingsApplyChip?.IsEnabled}");
        OnOverviewNavigationClick(null, EventArgs.Empty);
        await Task.Delay(500);
    }

    private async Task ProbeRange()
    {
        foreach (var (label, from, to) in new[]
        {
            ("malformed", "yesterday-ish", "now"),
            ("reversed", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("future", DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTime.Now.AddDays(2).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("valid", DateTime.Now.AddMinutes(-50).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTime.Now.AddMinutes(-20).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        })
        {
            ApplyAbsoluteGlobalRange(from, to);
            await Snap($"range-{label}", 900);
            ProbeLog($"range {label}: status='{_viewModel.StatusText}' button='{GetGlobalRangeButtonText()}'");
        }

        OnResetAllZoomClick(null, EventArgs.Empty);
        foreach (var minutes in new[] { 1, 5, 60, 1440, 10080 })
        {
            ApplyGlobalWindowRange(minutes);
            await Snap($"range-preset-{minutes}m", 1500);
        }

        ApplyGlobalWindowRange(720);
        await Task.Delay(800);
    }

    private async Task ProbeLocale()
    {
        var original = CultureInfo.CurrentCulture;
        foreach (var name in new[] { "de-DE", "en-GB", "en-US" })
        {
            var culture = new CultureInfo(name);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
            foreach (var panel in _viewModel.DashboardPanels)
            {
                panel.RefreshPresentation();
            }

            RefreshGlobalRangeControls();
            await Snap($"locale-{name}-overview", 2500);
            if (ProbeCard("memory") is { } memory)
            {
                memory.StartBringIntoView();
                await Task.Delay(300);
                ProbeInvoke(memory, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Snap($"locale-{name}-memory-popover", 900);
                ProbeInvoke(memory, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                _scrollHost.ChangeView(null, 0, null, true);
            }
        }

        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.CurrentCulture = original;
    }

    private async Task ProbeLeak()
    {
        if (ProbeCard("cpu-total") is not { } cpu)
        {
            return;
        }

        File.WriteAllText(System.IO.Path.Combine(ProbeDir, $"leak-start-{Environment.TickCount64}.txt"), "start");
        await Task.Delay(3000);
        for (var i = 0; i < 300; i++)
        {
            ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
            await Task.Delay(60);
            ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
            await Task.Delay(40);
            if (i % 3 == 0)
            {
                RenderGlobalRangeEditor();
                await Task.Delay(30);
                HideGlobalRangeEditor();
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(3000);
        File.WriteAllText(System.IO.Path.Combine(ProbeDir, $"leak-end-{Environment.TickCount64}.txt"), "end");
        ProbeLog("leak loop complete: 300 popover cycles, 100 range-editor cycles");
    }

    private async Task ProbeWindowCard()
    {
        OnSettingsNavigationClick(null, EventArgs.Empty);
        await Task.Delay(800);
        _scrollHost.ChangeView(null, 100000, null, true);
        SetStartWithWindows(true);
        await Snap("window-startup-on", 900);
        ProbeLog($"start with windows on: vm={_viewModel.LaunchOnStartup} registry={_autoLaunchService.IsEnabled()}");
        SetStartWithWindows(false);
        await Snap("window-startup-off", 900);
        ProbeLog($"start with windows off: vm={_viewModel.LaunchOnStartup} registry={_autoLaunchService.IsEnabled()}");
        SetKeepRunningInTray(false);
        await Snap("window-close-exits", 900);
        ProbeLog($"tray icon after off: {_trayIcon is not null}");
        SetKeepRunningInTray(true);
        await Snap("window-close-tray", 900);
        ProbeLog($"tray icon after on: {_trayIcon?.IsAdded}");
        OnOverviewNavigationClick(null, EventArgs.Empty);
        await Task.Delay(500);
    }

    /// <summary>Measures how long the UI thread is blocked: a 1ms ticker records how late it runs.</summary>
    private sealed class StallMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _running;
        public double Longest;
        public double BusyMs;
        public int Stalls;

        public async void Start()
        {
            _running = true;
            var last = _clock.Elapsed.TotalMilliseconds;
            while (_running)
            {
                await Task.Delay(1);
                var now = _clock.Elapsed.TotalMilliseconds;
                var gap = now - last;
                last = now;
                if (gap > 20)
                {
                    Stalls++;
                    BusyMs += gap;
                    Longest = Math.Max(Longest, gap);
                }
            }
        }

        public void Stop() => _running = false;

        public void Reset()
        {
            Longest = 0;
            BusyMs = 0;
            Stalls = 0;
        }

        public string Report() => $"longest stall {Longest:0}ms, {Stalls} stalls >20ms, {BusyMs:0}ms blocked";
    }

    private async Task ProbePerf()
    {
        var meter = new StallMeter();
        meter.Start();

        // Steady state: snapshots every 2s with every panel drawing.
        await Task.Delay(2000);
        meter.Reset();
        await Task.Delay(20000);
        ProbeLog($"perf idle 20s: {meter.Report()}");

        foreach (var minutes in new[] { 15, 60, 720, 1440, 10080, 43200, 15, 1 })
        {
            meter.Reset();
            var sw = Stopwatch.StartNew();
            ApplyGlobalWindowRange(minutes);
            var sync = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(2500);
            ProbeLog($"perf range {minutes}m: click handler {sync:0}ms; next 2.5s {meter.Report()}");
        }

        ApplyGlobalWindowRange(720);
        await Task.Delay(2500);

        if (ProbeCard("cpu-total") is { } cpu)
        {
            for (var i = 0; i < 3; i++)
            {
                meter.Reset();
                var sw = Stopwatch.StartNew();
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                var sync = sw.Elapsed.TotalMilliseconds;
                await Task.Delay(1500);
                ProbeLog($"perf popover open: handler {sync:0}ms; next 1.5s {meter.Report()}");
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Task.Delay(800);
            }
        }

        for (var i = 0; i < 2; i++)
        {
            meter.Reset();
            var sw = Stopwatch.StartNew();
            OnSettingsNavigationClick(null, EventArgs.Empty);
            var toSettings = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(1500);
            var settingsReport = meter.Report();
            meter.Reset();
            sw.Restart();
            OnOverviewNavigationClick(null, EventArgs.Empty);
            var toOverview = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(1500);
            ProbeLog($"perf settings: handler {toSettings:0}ms ({settingsReport}); back to overview handler {toOverview:0}ms ({meter.Report()})");
        }

        meter.Stop();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref ProbePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProbePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Opens the CPU popover and publishes screen points for an external real-mouse driver.</summary>
    private async Task ProbeHoverTargets()
    {
        if (ProbeCard("cpu-total") is not { } cpu)
        {
            return;
        }

        _scrollHost.ChangeView(null, 0, null, true);
        await Task.Delay(500);
        ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
        await Task.Delay(1500);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = _rootLayout.XamlRoot?.RasterizationScale ?? 1d;
        var lines = new List<string>();
        void Add(string label, FrameworkElement element)
        {
            var center = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(element.ActualWidth / 2, element.ActualHeight / 2));
            var point = new ProbePoint { X = (int)(center.X * scale), Y = (int)(center.Y * scale) };
            ClientToScreen(hwnd, ref point);
            lines.Add($"{label}|{point.X}|{point.Y}");
        }

        var slots = (System.Collections.IList)typeof(TelemetryPanelCard).GetField("_processRowSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cpu)!;
        foreach (var slot in slots)
        {
            var row = (FrameworkElement)slot!.GetType().GetProperty("Row")!.GetValue(slot)!;
            var key = slot.GetType().GetProperty("Key")!.GetValue(slot) as string;
            if (key is not null && row.XamlRoot is not null && row.ActualHeight > 0)
            {
                Add($"row:{key}", row);
            }
        }

        foreach (var name in new[] { "_sortHighestButton", "_sortLowestButton", "_sortNameButton", "_processScroller" })
        {
            if (typeof(TelemetryPanelCard).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cpu) is FrameworkElement element)
            {
                Add(name, element);
            }
        }

        File.WriteAllLines(System.IO.Path.Combine(ProbeDir, "coords.txt"), lines);
        ProbeLog($"hover: published {lines.Count} targets");
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(2000);
            ProbeLog($"hover: alive {(i + 1) * 2}s popover open={(bool)(typeof(TelemetryPanelCard).GetField("_processSectionExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cpu)!)}");
        }
    }

    /// <summary>The pre-fix pattern: a fresh cursor assigned from Loaded.</summary>
    private sealed class LoadedCursorElement : UserControl
    {
        public LoadedCursorElement()
        {
            Content = new Border { Width = 40, Height = 10, Background = SwissOlly.App.ViewModels.BrushFactory.CreateBrush("#01000000") };
            Loaded += (_, _) =>
            {
                try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); }
                catch { }
            };
        }
    }

    private async Task ProbeCursorChurn(bool oldPattern)
    {
        // Same churn as the old process list: elements added, then removed before or as they load.
        var host = new StackPanel();
        _overviewView.Children.Add(host);
        await Task.Delay(300);
        for (var round = 0; round < 300; round++)
        {
            var batch = new List<UIElement>();
            for (var i = 0; i < 15; i++)
            {
                UIElement element = oldPattern
                    ? new LoadedCursorElement()
                    : new ClickableBorder(new Border { Width = 40, Height = 10 }, invokable: true);
                batch.Add(element);
                host.Children.Add(element);
            }

            if (round % 2 == 0)
            {
                host.Children.Clear(); // removed in the same tick they were added
            }
            else
            {
                await Task.Yield();
                host.Children.Clear();
                foreach (var element in batch)
                {
                    host.Children.Add(element); // re-added: Loaded again
                }

                await Task.Yield();
                host.Children.Clear();
            }

            if (round % 50 == 0)
            {
                ProbeLog($"cursor churn ({(oldPattern ? "old" : "new")}): round {round} alive");
            }
        }

        _overviewView.Children.Remove(host);
        ProbeLog($"cursor churn ({(oldPattern ? "old" : "new")}): survived 300 rounds");
    }

    private async Task ProbePopupCursorChurn(bool oldPattern)
    {
        if (ProbeCard("cpu-total") is not { } cpu)
        {
            return;
        }

        var host = (StackPanel)typeof(TelemetryPanelCard).GetField("_processRowsHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cpu)!;
        var flyout = (Flyout)typeof(TelemetryPanelCard).GetField("_processFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cpu)!;
        var random = new Random(7);
        for (var round = 0; round < 120; round++)
        {
            ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
            await Task.Delay(random.Next(0, 40));
            var added = new List<UIElement>();
            for (var i = 0; i < 15; i++)
            {
                UIElement element = oldPattern
                    ? new LoadedCursorElement()
                    : new ClickableBorder(new Border { Width = 40, Height = 10 }, invokable: true);
                added.Add(element);
                host.Children.Add(element);
            }

            // Close while the new rows are loading, as a light-dismiss during a refresh would.
            await Task.Delay(random.Next(0, 25));
            flyout.Hide();
            await Task.Delay(random.Next(5, 60));
            foreach (var element in added)
            {
                host.Children.Remove(element);
            }

            if (round % 20 == 0)
            {
                ProbeLog($"popup cursor churn ({(oldPattern ? "old" : "new")}): round {round} alive");
            }
        }

        ProbeLog($"popup cursor churn ({(oldPattern ? "old" : "new")}): survived 120 rounds");
    }

    private async Task SnapSettingsPages(string prefix)
    {
        _scrollHost.ChangeView(null, 0, null, true);
        await Snap($"{prefix}-p0", 600);
        var step = Math.Max(200, _scrollHost.ViewportHeight * 0.8);
        for (var i = 1; i <= 6 && _scrollHost.VerticalOffset + 2 < _scrollHost.ScrollableHeight; i++)
        {
            _scrollHost.ChangeView(null, step * i, null, true);
            await Snap($"{prefix}-p{i}", 450);
        }
    }

    private async Task ProbeSettingsRedesign()
    {
        var originalTheme = _viewModel.SelectedTheme;
        OnSettingsNavigationClick(null, EventArgs.Empty);
        await Task.Delay(900);
        await SnapSettingsPages("s2-clean");
        ProbeLog($"bar when clean: opacity={_settingsApplyBar?.Opacity} hit={_settingsApplyBar?.IsHitTestVisible}");

        // Chip clicks now update in place: time 40 of them.
        var meter = new StallMeter();
        meter.Start();
        await Task.Delay(300);
        meter.Reset();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            SetScrapeInterval(i % 2 == 0 ? 5 : 10);
        }

        ProbeLog($"settings chip x40: {sw.Elapsed.TotalMilliseconds:0.0}ms total, {sw.Elapsed.TotalMilliseconds / 40:0.00}ms each; {meter.Report()}");
        sw.Restart();
        RenderEditableControlDeck();
        ProbeLog($"full settings rebuild (for comparison): {sw.Elapsed.TotalMilliseconds:0.0}ms");
        meter.Stop();

        // Dirty: the floating bar appears, and the chips/headline/field follow the draft.
        SetRetentionInput("7d");
        await Snap("s2-dirty", 600);
        ProbeLog($"dirty: bar opacity={_settingsApplyBar?.Opacity} apply={_settingsApplyChip?.IsEnabled} text='{_settingsSavedLabel?.Text}'");
        OnCancelSettingsClick(null, EventArgs.Empty);
        await Task.Delay(400);
        ProbeLog($"after revert: bar opacity={_settingsApplyBar?.Opacity} retention draft='{_draftRetentionInput}'");

        // Overlay drafts drive the preview. Never applied: only rejected or reverted.
        _draftOverlayEnabled = true;
        _draftOverlayMode = SwissOlly.Core.Models.OverlayMode.Custom;
        _draftOverlayCustomMetrics = ["memory", "fps"];
        ToggleOverlayMetric("gpu");
        _draftOverlayColor = "#39FF14";
        _draftOverlayCorner = SwissOlly.Core.Models.OverlayCorner.BottomLeft;
        _draftOverlayFontSize = "20";
        RefreshSettingsDeck();
        ProbeLog($"custom metrics after toggle: {string.Join(',', _draftOverlayCustomMetrics)}");
        await SnapSettingsPages("s2-overlay");

        _draftOverlayFontSize = "99";
        RefreshSettingsDeck();
        OnSaveSettingsClick(null, EventArgs.Empty);
        await Snap("s2-overlay-invalid", 700);
        ProbeLog($"invalid overlay: status='{_viewModel.StatusText}' bar='{_settingsSavedLabel?.Text}' applied size={_viewModel.OverlayConfig.FontSizePx}");
        OnCancelSettingsClick(null, EventArgs.Empty);
        await Task.Delay(500);

        foreach (var mode in new[] { ThemeMode.Obsidian, ThemeMode.Synthwave, ThemeMode.Frost, ThemeMode.Blossom })
        {
            sw.Restart();
            _themeCards[mode].RaiseClick();
            var handler = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(800);
            ProbeLog($"theme {mode}: click handler {handler:0}ms, selected card ok={_themeCards[mode].IsSelected}");
            await SnapSettingsPages($"s2-theme-{mode.ToString().ToLowerInvariant()}");
            OnOverviewNavigationClick(null, EventArgs.Empty);
            _scrollHost.ChangeView(null, 0, null, true);
            await Snap($"s2-theme-{mode.ToString().ToLowerInvariant()}-overview", 900);
            if (ProbeCard("cpu-total") is { } cpu)
            {
                cpu.StartBringIntoView();
                await Task.Delay(300);
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Snap($"s2-theme-{mode.ToString().ToLowerInvariant()}-popover", 700);
                ProbeInvoke(cpu, "OnProcessSectionToggleClick", null, EventArgs.Empty);
            }

            OnSettingsNavigationClick(null, EventArgs.Empty);
            await Task.Delay(500);
        }

        // Back to the user's theme through the same path, so the saved setting is theirs again.
        _themeCards[originalTheme].RaiseClick();
        await Task.Delay(800);
        ProbeLog($"theme restored: vm={_viewModel.SelectedTheme} original={originalTheme}");
        OnOverviewNavigationClick(null, EventArgs.Empty);
        await Task.Delay(500);
    }

    /// <summary>Pins two processes, keeps the popover open across many refreshes, at full and narrow width.</summary>
    private async Task ProbePins()
    {
        var size = AppWindow.Size;
        foreach (var (label, width) in new[] { ("wide", 0), ("narrow", 760) })
        {
            if (width > 0)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900));
                await Task.Delay(1200);
            }

            foreach (var key in new[] { "cpu-total", "memory" })
            {
                if (ProbeCard(key) is not { } card || card.Panel is not { } panel)
                {
                    continue;
                }

                card.StartBringIntoView();
                await Task.Delay(500);
                ProbeInvoke(card, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Task.Delay(1500);
                var names = panel.ProcessRows.Take(2).Select(row => row.Name).ToList();
                foreach (var name in names)
                {
                    panel.ToggleProcessPin(name);
                }

                // Several refreshes with the list open and pins in place: the reported freeze and crash.
                var meter = new StallMeter();
                meter.Start();
                await Task.Delay(8000);
                meter.Stop();
                ProbeLog($"pins {label} {key}: pinned {string.Join(", ", names)}; 8s open: {meter.Report()}");
                await Snap($"pins-{label}-{key}-popover", 100);
                ProbeInvoke(card, "OnProcessSectionToggleClick", null, EventArgs.Empty);
                await Snap($"pins-{label}-{key}-chart", 2500);
                foreach (var name in names)
                {
                    panel.ToggleProcessPin(name);
                }
            }
        }

        AppWindow.Resize(size);
        await Task.Delay(1000);
    }

    private static IEnumerable<DependencyObject> ProbeDescendants(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in ProbeDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Screenshots the board at four ranges and writes every series' points from the last 40
    /// minutes, so a run before a restart and one after it can be compared point for point.
    /// </summary>
    private async Task ProbeCaptureForRestart(string label)
    {
        var originalMinutes = _viewModel.SelectedWindowMinutes;
        OnOverviewNavigationClick(null, EventArgs.Empty);
        _scrollHost.ChangeView(null, 0, null, true);
        foreach (var minutes in new[] { 5, 15, 60, 1440 })
        {
            ApplyGlobalWindowRange(minutes);
            await Snap($"restart-{label}-{minutes}m", 1800);
        }

        ApplyGlobalWindowRange(originalMinutes);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-40);
        var lines = new List<string>();
        var buffersField = typeof(SwissOlly.App.ViewModels.MetricPanelViewModel).GetField("_buffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var panel in _viewModel.DashboardPanels)
        {
            var buffers = (System.Collections.IDictionary)buffersField.GetValue(panel)!;
            foreach (System.Collections.DictionaryEntry entry in buffers)
            {
                var points = (List<MetricPoint>)entry.Value!.GetType().GetProperty("Points")!.GetValue(entry.Value)!;
                foreach (var point in points.Where(p => p.Timestamp >= cutoff))
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"{panel.PanelKey}|{entry.Key}|{point.Timestamp.ToUnixTimeMilliseconds()}|{point.Value:R}"));
                }
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"#headline|{panel.PanelKey}|{panel.CurrentValue}|{panel.SecondaryValue}"));
        }

        File.WriteAllLines(System.IO.Path.Combine(ProbeDir, $"dump-{label}.txt"), lines);
        ProbeLog($"restart {label}: dumped {lines.Count} lines; status '{_viewModel.StatusText}'");
    }

    /// <summary>Opens a text field's right-click menu: the one framework flyout control left.</summary>
    private async Task ProbeContextMenu()
    {
        OnSettingsNavigationClick(null, EventArgs.Empty);
        await Task.Delay(900);
        var entry = ProbeDescendants(_settingsView).OfType<InlineTextEntry>().FirstOrDefault();
        if (entry?.ContextFlyout is not Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase menu)
        {
            ProbeLog("menu: no field with a context menu");
            return;
        }

        entry.StartBringIntoView();
        await Task.Delay(500);
        menu.ShowAt(entry);
        await Snap("menu-open", 800);
        var popups = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(_rootLayout.XamlRoot);
        ProbeLog($"menu: open popups={popups.Count} sizes={string.Join(",", popups.Select(p => p.Child is FrameworkElement c ? $"{c.ActualWidth:0}x{c.ActualHeight:0}" : "?"))}");
        menu.Hide();
        await Task.Delay(300);
        OnOverviewNavigationClick(null, EventArgs.Empty);
        await Task.Delay(300);
    }

    private async Task ProbeStates()
    {
        _viewModel.SetHistoryWriteIssue($"History isn't being saved: {DescribeStorageError(new DirectoryNotFoundException("x"))}. The live view keeps updating.");
        UpdateStatusText();
        await Snap("state-history-write-failing", 2500);
        _viewModel.SetHistoryWriteIssue(null);
        await Task.Delay(2500);
        UpdateStatusText();
        await Snap("state-history-write-recovered", 600);
    }
}
#endif
