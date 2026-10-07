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
                "#F2F3F5",     // AppBackdrop
                "#FFFFFF",     // ShellBackground
                "#E4E6E9",     // ShellStroke
                "#FFFFFF",     // Surface — primary white cards
                "#FAFAFB",     // SurfaceElevated
                "#F4F5F6",     // SurfaceStrong
                "#F1F2F4",     // SurfaceInset
                "#E7E8EA",     // SurfaceStroke
                "#C8FFFFFF",   // SurfaceHighlight — restrained highlight for glassy edges
                "#F4F5F6",     // PanelOverlay
                "#E5E7EA",     // SurfaceGrid
                "#1C1D1F",     // TextPrimary
                "#45484D",     // TextSecondary
                "#747980",     // TextMuted
                "#1769C2",     // Accent
                "#0D4F99",     // AccentStrong
                "#E5EFFB",     // AccentSoft
                "#1A1769C2",   // AccentHalo
                "#18C08326",   // WarningHalo
                "#A96500",     // Warning
                "#C7463B",     // Critical
                "#80E7E8EA")   // OverlayScrim
            : new ThemePalette(
                "#111315",
                "#17191C",
                "#202327",
                "#1C1F22",
                "#222529",
                "#292D31",
                "#141619",
                "#2C3035",
                "#2BFFFFFF",
                "#25282C",
                "#2B3036",
                "#F4F5F7",
                "#C4C8CE",
                "#858B93",
                "#79B8FF",
                "#B6D9FF",
                "#294A6B",
                "#2479B8FF",
                "#20E6A34B",
                "#F2BF66",
                "#FF897D",
                "#B0080A0C");

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
