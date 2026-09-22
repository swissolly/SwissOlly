using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vaktr.App.Services;
using Vaktr.App.ViewModels;
using Vaktr.Core.Models;
using Vaktr.Store.Persistence;

namespace Vaktr.App;

public sealed class App : Application
{
    private ShellWindow? _window;

    public static App CurrentApp => (App)Current;

    public App()
    {
        StartupTrace.Write("App ctor");
        UnhandledException += OnUnhandledException;

        // Catch exceptions from background threads (LibreHardwareMonitor, WMI)
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is System.Runtime.InteropServices.COMException comEx)
            {
                StartupTrace.WriteException("AppDomain COMException (suppressed)", comEx);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            StartupTrace.WriteException("UnobservedTaskException (suppressed)", args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        StartupTrace.Write("App.OnLaunched start");
        EnsureResourcesInitialized();
        StartupTrace.Write("Resources initialized // launch-cut-v19");

        var configStore = new JsonConfigStore();
        VaktrConfig config;
        try
        {
            config = configStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            StartupTrace.Write("Config loaded");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("Config load", ex);
            config = VaktrConfig.CreateDefault().Normalize();
        }

        var metricStore = new SqliteMetricStore();
        var viewModel = new MainViewModel(config);
        StartupTrace.Write("Stores and view model created // launch-cut-v19");
        ApplyThemeResources(config.Theme);
        StartupTrace.Write("Configured theme applied");

        _window = new ShellWindow(
            viewModel,
            metricStore,
            configStore,
            new AutoLaunchService());
        StartupTrace.Write("ShellWindow created // polished-v19");

        _window.ApplyInitialTheme(config.Theme);
        StartupTrace.Write("Window theme prepared");
        _window.Activate();
        StartupTrace.Write("Window activated");
    }

    public void ApplyTheme(ThemeMode mode)
    {
        ApplyThemeResources(mode);
        _window?.ApplyTheme(mode);
    }

    public void PreviewTheme(ThemeMode mode)
    {
        ApplyThemeResources(mode);
        _window?.PreviewTheme(mode);
    }

    public void MarkStartupSettled()
    {
        StartupTrace.Write("Startup guard disarmed");
    }

    private void ApplyThemeResources(ThemeMode mode)
    {
        var palette = mode == ThemeMode.Light
            ? new ThemePalette(
                "#EEF2F7",     // AppBackdrop — neutral light shell background
                "#F7F9FC",     // ShellBackground — brighter than the app backdrop
                "#DFE6EE",     // ShellStroke — hairline, matching the dark theme's restraint
                "#FFFFFF",     // Surface — primary white cards
                "#FBFCFE",     // SurfaceElevated — nearly-white raised surfaces
                "#EEF2F7",     // SurfaceStrong — compact controls and hover states
                "#F3F6FA",     // SurfaceInset — recessed chart/process beds
                "#E3E9F0",     // SurfaceStroke — hairline on light cards
                "#C8FFFFFF",   // SurfaceHighlight — restrained highlight for glassy edges
                "#E8EEF5",     // PanelOverlay — subtle supporting pill/background tint
                "#E7ECF2",     // SurfaceGrid — chart grid felt rather than read
                "#0D1A25",     // TextPrimary — strong dark text
                "#3A4F63",     // TextSecondary — calmer supporting copy
                "#66788A",     // TextMuted — readable metadata
                "#0C6B96",     // Accent — cooler cyan-blue without neon contrast
                "#0A4E6D",     // AccentStrong — stronger interactive state
                "#DDEAF6",     // AccentSoft — quiet accent tint
                "#18609020",   // AccentHalo
                "#15C09040",   // WarningHalo
                "#B8861F",     // Warning
                "#C96C36",     // Critical
                "#80D8E4F0")   // OverlayScrim
            : new ThemePalette(
                "#030812",
                "#07101B",
                // Strokes are hairlines, not wireframe. The previous values (#28445E shell,
                // #315274 surface, #426A8C grid) were saturated mid-blues at high enough contrast
                // that every card, chip and gridline drew a visible outline — the single biggest
                // reason the dashboard read as a diagram rather than a designed surface.
                // Separation now comes from surface elevation; the stroke only catches the edge.
                "#18212D",
                "#0C1726",
                "#112033",
                "#18314A",
                "#091321",
                "#1E2835",
                "#22FFFFFF",
                "#091321",
                // Gridlines should be felt, not read. They sit just above the plot bed.
                "#1A222D",
                "#F4F9FF",
                "#C6D7EA",
                "#7C8B9E",
                "#6FE2FF",
                "#C7F5FF",
                "#163B60",
                "#2B8FE6C4",
                "#1EFFA25C",
                "#F0C968",
                "#FF9761",
                "#B0060A12");

        SetBrushResource("AppBackdropBrush", palette.AppBackdrop);
        SetBrushResource("ShellBackgroundBrush", palette.ShellBackground);
        SetBrushResource("ShellStrokeBrush", palette.ShellStroke);
        SetBrushResource("SurfaceBrush", palette.Surface);
        SetBrushResource("SurfaceElevatedBrush", palette.SurfaceElevated);
        SetBrushResource("SurfaceStrongBrush", palette.SurfaceStrong);
        SetBrushResource("SurfaceInsetBrush", palette.SurfaceInset);
        SetBrushResource("SurfaceStrokeBrush", palette.SurfaceStroke);
        SetBrushResource("SurfaceHighlightBrush", palette.SurfaceHighlight);
        SetBrushResource("PanelOverlayBrush", palette.PanelOverlay);
        SetBrushResource("SurfaceGridBrush", palette.SurfaceGrid);
        SetBrushResource("TextPrimaryBrush", palette.TextPrimary);
        SetBrushResource("TextSecondaryBrush", palette.TextSecondary);
        SetBrushResource("TextMutedBrush", palette.TextMuted);
        SetBrushResource("AccentBrush", palette.Accent);
        SetBrushResource("AccentStrongBrush", palette.AccentStrong);
        SetBrushResource("AccentSoftBrush", palette.AccentSoft);
        SetBrushResource("AccentHaloBrush", palette.AccentHalo);
        SetBrushResource("WarningHaloBrush", palette.WarningHalo);
        SetBrushResource("WarningBrush", palette.Warning);
        SetBrushResource("CriticalBrush", palette.Critical);
        SetBrushResource("OverlayScrimBrush", palette.OverlayScrim);
    }

    public Windows.UI.Color ResolveThemeColor(string key, string fallbackHex)
    {
        if (Resources.TryGetValue(key, out var value) && value is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
        {
            return brush.Color;
        }

        return BrushFactory.ParseColor(fallbackHex);
    }

    private void SetBrushResource(string key, string hex)
    {
        if (Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = BrushFactory.ParseColor(hex);
            return;
        }

        Resources[key] = BrushFactory.CreateBrush(hex);
    }

    private void EnsureResourcesInitialized()
    {
        StartupTrace.Write("EnsureResourcesInitialized start");
        Resources ??= new ResourceDictionary();
        EnsureFrameworkFallbackResources();
        StartupTrace.Write("Skipping XamlControlsResources merge for custom shell");
        StartupTrace.Write("EnsureResourcesInitialized complete");
    }

    private void EnsureFrameworkFallbackResources()
    {
        SetBrushFallback("AcrylicBackgroundFillColorDefaultBrush", "#15283B");
        SetBrushFallback("AcrylicInAppFillColorDefaultBrush", "#183148");
        SetBrushFallback("LayerFillColorDefaultBrush", "#102131");
        SetBrushFallback("LayerFillColorAltBrush", "#15283B");
        SetBrushFallback("CardStrokeColorDefaultBrush", "#27425E");
        StartupTrace.Write("Framework fallback resources seeded");
    }

    private void SetBrushFallback(string key, string hex)
    {
        if (!Resources.TryGetValue(key, out _))
        {
            Resources[key] = BrushFactory.CreateBrush(hex);
        }

        EnsureThemeBrushFallback("Default", key, hex);
        EnsureThemeBrushFallback("Dark", key, hex);
        EnsureThemeBrushFallback("Light", key, hex);
    }

    private void EnsureThemeBrushFallback(string themeKey, string resourceKey, string hex)
    {
        if (Resources.ThemeDictionaries.TryGetValue(themeKey, out var existing) &&
            existing is ResourceDictionary existingDictionary)
        {
            if (!existingDictionary.TryGetValue(resourceKey, out _))
            {
                existingDictionary[resourceKey] = BrushFactory.CreateBrush(hex);
            }

            return;
        }

        var dictionary = new ResourceDictionary
        {
            [resourceKey] = BrushFactory.CreateBrush(hex),
        };
        Resources.ThemeDictionaries[themeKey] = dictionary;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (e.Exception is not null)
        {
            StartupTrace.WriteException("App.UnhandledException", e.Exception);

            // Always handle COMExceptions — LibreHardwareMonitor and WMI can throw
            // on background threads that we can't catch locally
            if (e.Exception is System.Runtime.InteropServices.COMException)
            {
                e.Handled = true;
                StartupTrace.Write("COMException handled to preserve shell");
            }
        }
    }

    private sealed record ThemePalette(
        string AppBackdrop,
        string ShellBackground,
        string ShellStroke,
        string Surface,
        string SurfaceElevated,
        string SurfaceStrong,
        string SurfaceInset,
        string SurfaceStroke,
        string SurfaceHighlight,
        string PanelOverlay,
        string SurfaceGrid,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string Accent,
        string AccentStrong,
        string AccentSoft,
        string AccentHalo,
        string WarningHalo,
        string Warning,
        string Critical,
        string OverlayScrim);
}
