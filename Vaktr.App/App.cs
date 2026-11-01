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

#if DEBUG
        // WinUI can surface XAML failures as stowed COMExceptions before app-level handlers
        // run. Mirror the HRESULT, source, message, and managed stack into Visual Studio Output.
        // Debug only: it writes a stack trace to disk for every first-chance COMException,
        // including the many the app catches on purpose.
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception is System.Runtime.InteropServices.COMException comEx)
            {
                var message = comEx.Message ?? string.Empty;
                var source = comEx.Source ?? string.Empty;
                var isWinUiException =
                    comEx.HResult == unchecked((int)0x800F1000) ||
                    message.Contains("No installed components were detected", System.StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("WinRT.Runtime", System.StringComparison.OrdinalIgnoreCase);

                if (isWinUiException)
                {
                    var label = "First-chance WinRT COMException HRESULT=0x" +
                        comEx.HResult.ToString("X8") + " Source=" + source + " Message=" + message;
                    StartupTrace.WriteException(label, comEx);
                }
            }
        };
#endif

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
        // Palette field order (matches the ThemePalette record):
        //   AppBackdrop, ShellBackground, ShellStroke, Surface, SurfaceElevated, SurfaceStrong,
        //   SurfaceInset, SurfaceStroke, SurfaceHighlight, PanelOverlay, SurfaceGrid,
        //   TextPrimary, TextSecondary, TextMuted, Accent, AccentStrong, AccentSoft,
        //   AccentHalo, WarningHalo, Warning, Critical, OverlayScrim
        //
        // Every theme keeps the same structural relationships — backdrop darkest, cards lifted
        // one step, strokes as hairlines, muted text well clear of the AA contrast floor — so
        // adding a palette is a recolour, never a re-layout. Warning/Critical stay in the
        // amber/red family across all of them because those encode threshold meaning, not style.
        var palette = mode switch
        {
            ThemeMode.Light => new ThemePalette(
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
                "#80E7E8EA"),  // OverlayScrim

            ThemeMode.Sandstone => new ThemePalette(
                "#EDE9E3", "#FAF8F5", "#E0DAD1", "#FAF8F5", "#F5F2EC", "#EDE8E0",
                "#F0ECE5", "#E2DCD3", "#C8FFFFFF", "#EFEBE4", "#E4DED5",
                "#241F1A", "#4A443C", "#7B7369",
                "#3F6B5C", "#2C5347", "#DDE9E4",
                "#1A3F6B5C", "#18C08326", "#9A6212", "#BE4A3C", "#80E2DCD3"),

            ThemeMode.Midnight => new ThemePalette(
                "#070B14", "#0C1220", "#182033", "#101728", "#17203A", "#1E294A",
                "#0A0F1C", "#1F2A42", "#2BFFFFFF", "#141C30", "#1E2840",
                "#EEF2FA", "#BCC7DD", "#7F8AA3",
                "#5CC8F5", "#A5E2FA", "#1B3A58",
                "#245CC8F5", "#20E6A34B", "#F2BF66", "#FF897D", "#B0060A12"),

            ThemeMode.Ember => new ThemePalette(
                "#14100E", "#1A1512", "#2A221D", "#1F1915", "#261E19", "#2E251F",
                "#17120F", "#312822", "#2BFFFFFF", "#241C17", "#2F2620",
                "#F7F2EC", "#D2C6BA", "#95877A",
                "#F0A04B", "#FFC888", "#4A3321",
                "#24F0A04B", "#20E6A34B", "#F2BF66", "#FF897D", "#B00F0C0A"),

            ThemeMode.Forest => new ThemePalette(
                "#0D120F", "#121815", "#1E2A23", "#161D19", "#1C2520", "#232E27",
                "#0F1411", "#26322B", "#2BFFFFFF", "#1A231E", "#243029",
                "#EFF5F1", "#C2D0C7", "#828F87",
                "#6FD8A4", "#A8EFC9", "#1E4433",
                "#246FD8A4", "#20E6A34B", "#F2BF66", "#FF897D", "#B00A0F0C"),

            ThemeMode.Violet => new ThemePalette(
                "#110E16", "#17131E", "#271F33", "#1C1726", "#231D2F", "#2B2439",
                "#13101A", "#2E2640", "#2BFFFFFF", "#211B2C", "#2C2440",
                "#F3EFF8", "#CBC2DA", "#8B819C",
                "#B18AF0", "#D4BDFF", "#38295C",
                "#24B18AF0", "#20E6A34B", "#F2BF66", "#FF897D", "#B00C0A12"),

            ThemeMode.Nord => new ThemePalette(
                "#2E3440", "#333B48", "#434C5E", "#3B4252", "#434C5E", "#4C566A",
                "#2B313C", "#4A5467", "#2BFFFFFF", "#3E4657", "#49536A",
                "#ECEFF4", "#D8DEE9", "#9BA6B8",
                "#88C0D0", "#B4DAE5", "#3B5A68",
                "#2488C0D0", "#20E6A34B", "#EBCB8B", "#E8909C", "#B0242933"),

            _ => new ThemePalette(
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
                "#B0080A0C"),
        };

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
