using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwissOlly.App.Controls;
using SwissOlly.App.Services;
using SwissOlly.App.ViewModels;
using SwissOlly.Core.Models;
using SwissOlly.Store.Persistence;

namespace SwissOlly.App;

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

        // An exception nothing caught on a background thread (LibreHardwareMonitor, WMI) ends the
        // process; this can't stop that, only record it for a bug report. It used to log
        // COMExceptions alone, so every other crash left no trace.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                StartupTrace.WriteCrash("AppDomain.UnhandledException", ex);
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
        SwissOllyConfig config;
        try
        {
            config = configStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            StartupTrace.Write("Config loaded");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("Config load", ex);
            config = SwissOllyConfig.CreateDefault().Normalize();
        }

        // The palette first: the view model's series and summary colours adapt to it as they
        // are created (darker on light themes).
        ApplyThemeResources(config.Theme);
        StartupTrace.Write("Configured theme applied");
        var metricStore = new SqliteMetricStore();
        var viewModel = new MainViewModel(config);
        StartupTrace.Write("Stores and view model created // launch-cut-v19");

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
        var theme = ThemeCatalog.Get(mode);
        var palette = theme.Palette;

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
        // Text fields: white on light palettes, inset on dark ones.
        SetBrushResource("InputBackgroundBrush", theme.IsLight ? palette.Surface : palette.SurfaceInset);
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
            // Always handle COMExceptions — LibreHardwareMonitor and WMI can throw
            // on background threads that we can't catch locally
            if (e.Exception is System.Runtime.InteropServices.COMException)
            {
                StartupTrace.WriteException("App.UnhandledException", e.Exception);
                e.Handled = true;
                StartupTrace.Write("COMException handled to preserve shell");
            }
            else
            {
                // Left unhandled, so the app is about to close.
                StartupTrace.WriteCrash("App.UnhandledException", e.Exception);
            }
        }
    }
}
