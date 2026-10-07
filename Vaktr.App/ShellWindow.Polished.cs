using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using Vaktr.App.Controls;
using Vaktr.App.Services;
using Vaktr.App.ViewModels;
using Vaktr.Collector;
using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;
using Vaktr.Overlay;

namespace Vaktr.App;

public sealed partial class ShellWindow : Window
{
    private static readonly int[] ScrapeIntervalPresets = [1, 2, 5, 10, 15, 30, 60];
    private static readonly string[] OverlayColorPresets = ["#66E7FF", "#7CE38B", "#F5B54A", "#FF6B6B", "#FFFFFF"];
    private static readonly int[] RetentionHourPresets = [1, 6, 12, 24, 48, 72, 168, 336, 720, 2160, 8760];
    private static BitmapImage? s_brandBitmap;
    private static string? s_brandBitmapPath;
    private DeckEditorMode _activeDeckEditor = DeckEditorMode.Scrape;

    private readonly MainViewModel _viewModel;
    private readonly IMetricStore _metricStore;
    private readonly IConfigStore _configStore;
    private readonly AutoLaunchService _autoLaunchService;
    private readonly Dictionary<string, TelemetryPanelCard> _panelCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SummaryCardVisual> _summaryCardVisuals = [];

    private Grid _rootLayout;
    private readonly Grid _titleBarDragHost;
    private readonly ScrollViewer _scrollHost;
    private Border? _loadingOverlay;
    private readonly Border _controlsBodyHost;
    private Border? _controlsSurfaceRef;
    private readonly Grid _brandHost;
    private readonly Grid _summaryHost;
    private readonly Grid _dashboardGrid;
    private readonly TextBlock _statusText;
    private readonly Ellipse _statusIndicator;
    private readonly ActionChip _globalRangeButton;
    private readonly Border _globalRangeEditorHost;
    private readonly ActionChip _globalOneMinuteButton;
    private readonly ActionChip _globalFiveMinuteButton;
    private readonly ActionChip _globalFifteenMinuteButton;
    private readonly ActionChip _globalThirtyMinuteButton;
    private readonly ActionChip _globalOneHourButton;
    private readonly ActionChip _globalTwelveHourButton;
    private readonly ActionChip _globalTwentyFourHourButton;
    private readonly ActionChip _globalTwoDayButton;
    private readonly ActionChip _globalFiveDayButton;
    private readonly ActionChip _globalSevenDayButton;
    private readonly ActionChip _globalThirtyDayButton;
    private readonly ActionChip _globalNinetyDayButton;
    private readonly ActionChip _globalOneYearButton;
    private readonly ActionChip _globalResetZoomButton;

    private CollectorService? _collectorService;
    private Task? _pendingHistoryLoad;
    private OverlayController? _overlayController;
    private nint _windowIconHandle;
    private bool _controlDeckEditableActive;
    private bool _dashboardRefreshQueued;
    private bool _hasReceivedFirstSnapshot;
    private bool _initialized;
    private int _lastDashboardColumnCount;
    private int _lastSummaryColumnCount;
    private bool _windowIconApplied;
    private bool _summaryCardsBound;
    private bool _globalRangeEditorVisible;
    private bool _snapshotApplyQueued;
    private bool _controlDeckRenderQueued;
    private bool _panelRenderingSuspended;
    private string _lastRenderedStatusText = string.Empty;
    private string _draftScrapeIntervalInput = string.Empty;
    private string _draftRetentionInput = string.Empty;
    private string _draftStorageDirectory = string.Empty;
    private ThemeMode _draftThemeMode;
    private bool _draftOverlayEnabled;
    private Vaktr.Core.Models.OverlayMode _draftOverlayMode = Vaktr.Core.Models.OverlayMode.Advanced;
    private Vaktr.Core.Models.OverlayCorner _draftOverlayCorner = Vaktr.Core.Models.OverlayCorner.TopRight;
    private string _draftOverlayColor = "#66E7FF";
    private string _draftOverlayFontSize = "14";
    private string _draftOverlayOpacity = "90";
    private string _draftOverlayHotkey = "Ctrl+Shift+F11";
    private List<string> _draftOverlayCustomMetrics = ["fps", "cpu", "gpu", "gpu-temp", "memory"];
    private DateTimeOffset? _globalAbsoluteStartUtc;
    private DateTimeOffset? _globalAbsoluteEndUtc;
    private MetricSnapshot? _pendingSnapshot;
    private int _lastLoadedHistoryMinutes;
    public ShellWindow(
        MainViewModel viewModel,
        IMetricStore metricStore,
        IConfigStore configStore,
        AutoLaunchService autoLaunchService)
    {
        StartupTrace.Write("ShellWindow ctor start // polished-v19");
        _viewModel = viewModel;
        _metricStore = metricStore;
        _configStore = configStore;
        _autoLaunchService = autoLaunchService;

        Title = "Vaktr";

        _statusText = CreateSecondaryText(string.Empty, 11.5);
        _statusText.MaxWidth = 280;
        _statusText.TextWrapping = TextWrapping.NoWrap;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusIndicator = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = ResolveBrush("AccentBrush", "#79B8FF"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _controlsBodyHost = new Border();
        _brandHost = CreateBrandPlaceholder();
        _summaryHost = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12,
        };
        _dashboardGrid = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 16,
        };
        _globalRangeButton = CreateActionChip("15m", OnToggleGlobalRangeEditor, true);
        _globalRangeButton.MinWidth = 88;
        ToolTipService.SetToolTip(_globalRangeButton, "Choose the time range for all charts");
        _globalRangeEditorHost = new Border
        {
            Visibility = Visibility.Collapsed,
        };
        _globalOneMinuteButton = CreateGlobalRangeChip("1m", 1);
        _globalFiveMinuteButton = CreateGlobalRangeChip("5m", 5);
        _globalFifteenMinuteButton = CreateGlobalRangeChip("15m", 15);
        _globalThirtyMinuteButton = CreateGlobalRangeChip("30m", 30);
        _globalOneHourButton = CreateGlobalRangeChip("1h", 60);
        _globalTwelveHourButton = CreateGlobalRangeChip("12h", 720);
        _globalTwentyFourHourButton = CreateGlobalRangeChip("24h", 1440);
        _globalTwoDayButton = CreateGlobalRangeChip("2d", 2880);
        _globalFiveDayButton = CreateGlobalRangeChip("5d", 7200);
        _globalSevenDayButton = CreateGlobalRangeChip("7d", 10080);
        _globalThirtyDayButton = CreateGlobalRangeChip("30d", 43200);
        _globalNinetyDayButton = CreateGlobalRangeChip("90d", 129600);
        _globalOneYearButton = CreateGlobalRangeChip("1y", 525600);
        _globalResetZoomButton = CreateActionChip("Reset zoom", OnResetAllZoomClick);
        ToolTipService.SetToolTip(_globalResetZoomButton, "Return all charts to the live window");
        _titleBarDragHost = new Grid
        {
            Height = 34,
            Background = ResolveBrush("AppBackdropBrush", "#111315"),
        };
        _scrollHost = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _scrollHost.ViewChanged += OnScrollHostViewChanged;

        // Show loading screen immediately — ultra-lightweight, renders on first frame
        _rootLayout = BuildLoadingScreen();
        Content = _rootLayout;
        ExtendsContentIntoTitleBar = true;

        Closed += OnWindowClosed;
        Activated += OnWindowActivated;

        StartupTrace.Write("ShellWindow ctor complete // polished-v19");
    }

    private bool _fullUiBuilt;

    private void BuildFullUi()
    {
        if (_fullUiBuilt)
        {
            return;
        }

        _fullUiBuilt = true;
        StartupTrace.Write("BuildFullUi start");

        var fullLayout = BuildRootLayout();

        // Transfer loading overlay to the full layout
        if (_loadingOverlay is not null)
        {
            if (_loadingOverlay.Parent is Grid oldParent)
            {
                oldParent.Children.Remove(_loadingOverlay);
            }

            _loadingOverlay.Background = ResolveBrush("AppBackdropBrush", "#111315");
            fullLayout.Children.Add(_loadingOverlay);
            Grid.SetRow(_loadingOverlay, 1);
            Canvas.SetZIndex(_loadingOverlay, 100);
        }

        _rootLayout = fullLayout;
        Content = _rootLayout;
        SetTitleBar(_titleBarDragHost);
        ApplyInitialTheme(_viewModel.SelectedTheme);
        InstallResizeHook();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DashboardPanels.CollectionChanged += OnDashboardPanelsChanged;
        _viewModel.PanelToggles.CollectionChanged += OnPanelTogglesChanged;

        try
        {
            TryApplyWindowIcon();
            TryLoadBrandImage();
            SyncControlDeckDraftsFromViewModel();
            RenderControlDeckSummary();
            BuildSummaryCards();
            RefreshDashboardPanels();
            RefreshGlobalRangeControls();
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("BuildFullUi", ex);
        }

        StartupTrace.Write("BuildFullUi complete");
        // The dashboard has its own waiting states; telemetry must not keep the
        // entire app hidden if the first sample is slow or unavailable.
        DismissLoadingOverlay();
    }

    public void ApplyInitialTheme(ThemeMode mode)
    {
        var requestedTheme = mode == ThemeMode.Dark ? ElementTheme.Dark : ElementTheme.Light;
        if (_rootLayout.RequestedTheme != requestedTheme)
        {
            _rootLayout.RequestedTheme = requestedTheme;
        }

        RefreshWindowChrome();
    }

    public void ApplyTheme(ThemeMode mode)
    {
        var requestedTheme = mode == ThemeMode.Dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyInitialTheme(mode);
        _draftThemeMode = mode;
        RefreshThemeVisuals();
    }

    public void PreviewTheme(ThemeMode mode)
    {
        ApplyInitialTheme(mode);
        _draftThemeMode = mode;
        RefreshThemeVisuals();
    }

    private void RefreshThemeVisuals()
    {
        ThemeVisuals.InvalidateGradientCache();

        // All brush updates run synchronously so the user never sees a half-themed state.
        // Chart redraws are already deferred inside each card's RefreshThemeResources.
        RefreshWindowChrome();
        RefreshShellSurfaceThemeResources();
        RefreshSummaryCardThemeResources();
        RefreshGlobalRangeControls();
        UpdateStatusText();

        foreach (var chip in new[]
        {
            _globalRangeButton, _globalOneMinuteButton, _globalFiveMinuteButton,
            _globalFifteenMinuteButton, _globalThirtyMinuteButton, _globalOneHourButton,
            _globalTwelveHourButton, _globalTwentyFourHourButton, _globalTwoDayButton,
            _globalFiveDayButton, _globalSevenDayButton, _globalThirtyDayButton,
            _globalNinetyDayButton, _globalOneYearButton, _globalResetZoomButton,
        })
        {
            chip.RefreshThemeResources();
        }

        // Panel cards — update brushes only, chart redraws are deferred inside each card
        foreach (var card in _panelCards.Values)
        {
            card.RefreshThemeResources();
        }

        // Rebuild control deck on next frame — it's less critical than panel visuals
        // and rebuilding synchronously during theme switches causes visible jank.
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (_controlDeckEditableActive)
            {
                RenderEditableControlDeck();
            }
            else
            {
                RenderControlDeckSummary();
            }

            if (_globalRangeEditorVisible)
            {
                RenderGlobalRangeEditor();
            }
        });
    }

    private void BuildSummaryCards()
    {
        if (_summaryCardsBound)
        {
            return;
        }

        StartupTrace.Write("BuildSummaryCards // polished-v19");
        _summaryHost.Children.Clear();
        _summaryHost.RowDefinitions.Clear();
        _summaryHost.ColumnDefinitions.Clear();
        _summaryCardVisuals.Clear();
        var summaryColumns = DetermineSummaryColumns();
        _lastSummaryColumnCount = summaryColumns;
        for (var column = 0; column < summaryColumns; column++)
        {
            _summaryHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var card in _viewModel.SummaryCards)
        {
            var badgeHost = (Border)IconFactory.CreateTile(card.Title, card.AccentBrush, 38, 15);
            badgeHost.VerticalAlignment = VerticalAlignment.Center;

            var titleText = CreateMutedText(string.Empty, 9.5);
            titleText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Title)) });

            var valueText = CreatePrimaryText(string.Empty, 24, true);
            valueText.FontFamily = new FontFamily("Bahnschrift");
            valueText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Value)) });

            var captionText = CreateSecondaryText(string.Empty, 11.5);
            captionText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Caption)) });

            // Mini utilization bar — thin horizontal bar below the value
            var gaugeTrack = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
                Opacity = 0.38,
                Margin = new Thickness(0, 7, 0, 6),
            };
            var gaugeFill = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = card.AccentBrush,
                Margin = new Thickness(0, 7, 0, 6),
            };
            var gaugeHost = new Grid
            {
                Children = { gaugeTrack, gaugeFill },
            };

            // Bind fill width and color to utilization
            card.PropertyChanged += (_, args) =>
            {
                if (_isWindowResizing) return; // skip during resize
                if (!string.Equals(args.PropertyName, nameof(SummaryCardViewModel.Utilization)))
                {
                    return;
                }

                var util = card.Utilization;
                var trackWidth = gaugeTrack.ActualWidth > 0 ? gaugeTrack.ActualWidth : 120;
                gaugeFill.Width = trackWidth * Math.Clamp(util / 100d, 0d, 1d);
                gaugeFill.Background = ResolveSummaryUtilizationBrush(util, card.AccentBrush);
            };
            // Also update on track size change
            gaugeTrack.SizeChanged += (_, _) =>
            {
                var util = card.Utilization;
                gaugeFill.Width = gaugeTrack.ActualWidth * Math.Clamp(util / 100d, 0d, 1d);
                gaugeFill.Background = ResolveSummaryUtilizationBrush(util, card.AccentBrush);
            };

            var details = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    titleText,
                    valueText,
                    gaugeHost,
                    captionText,
                },
            };

            var contentGrid = new Grid
            {
                ColumnSpacing = 14,
            };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.Children.Add(badgeHost);
            contentGrid.Children.Add(details);
            Grid.SetColumn(details, 1);

            var summaryCard = new Border
            {
                DataContext = card,
                Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
                BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
                BorderThickness = new Thickness(0.9),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                MinHeight = 84,
                Child = contentGrid,
            };
            _summaryHost.Children.Add(summaryCard);
            _summaryCardVisuals.Add(new SummaryCardVisual(summaryCard, titleText, valueText, captionText, badgeHost, gaugeTrack, gaugeFill, card));
            var index = _summaryHost.Children.Count - 1;
            while (_summaryHost.RowDefinitions.Count <= index / summaryColumns)
            {
                _summaryHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetColumn(summaryCard, index % summaryColumns);
            Grid.SetRow(summaryCard, index / summaryColumns);
        }

        _summaryCardsBound = true;
    }

    private void RefreshSummaryCardThemeResources()
    {
        if (!_summaryCardsBound)
        {
            return;
        }

        for (var i = 0; i < _summaryCardVisuals.Count; i++)
        {
            var visual = _summaryCardVisuals[i];
            visual.Surface.Background = ResolveBrush("SurfaceBrush", "#1C1F22");
            visual.Surface.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            visual.TitleText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            visual.ValueText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
            visual.CaptionText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
            visual.GaugeTrack.Background = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            visual.GaugeFill.Background = ResolveSummaryUtilizationBrush(visual.ViewModel.Utilization, visual.ViewModel.AccentBrush);
            // Skip badge recreation — just update its background/border colors
            if (visual.BadgeHost is Border badge)
            {
                badge.Background = ResolveBrush("SurfaceInsetBrush", "#141619");
                badge.BorderBrush = visual.ViewModel.AccentBrush;
            }
        }
    }

    private Brush ResolveSummaryUtilizationBrush(double utilization, Brush accentBrush)
    {
        if (utilization > 90)
        {
            return ResolveBrush("CriticalBrush", "#FF9761");
        }

        if (utilization > 75)
        {
            return ResolveBrush("WarningBrush", "#F0C968");
        }

        return accentBrush;
    }

    private void RefreshPanelToggles()
    {
    }

    private void RefreshDashboardPanels()
    {
        StartupTrace.Write("RefreshDashboardPanels // polished-v19");
        var panels = _viewModel.DashboardPanels.ToArray();
        var activeKeys = new HashSet<string>(panels.Select(panel => panel.PanelKey), StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _panelCards.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            if (_panelCards.TryGetValue(staleKey, out var staleCard))
            {
                staleCard.RangePresetRequested -= OnPanelRangePresetRequested;
                staleCard.PanelZoomSelectionRequested -= OnPanelZoomSelectionRequested;
                staleCard.PanelZoomResetRequested -= OnPanelZoomResetRequested;
                staleCard.PanelReorderRequested -= OnPanelReorderRequested;
                staleCard.PanelDragEnded -= OnPanelDragEnded;
            }

            _panelCards.Remove(staleKey);
        }

        foreach (var panel in panels)
        {
            if (!_panelCards.TryGetValue(panel.PanelKey, out var card))
            {
                card = new TelemetryPanelCard { Panel = panel };
                card.RangePresetRequested += OnPanelRangePresetRequested;
                card.PanelZoomSelectionRequested += OnPanelZoomSelectionRequested;
                card.PanelZoomResetRequested += OnPanelZoomResetRequested;
                card.PanelReorderRequested += OnPanelReorderRequested;
                card.PanelDragEnded += OnPanelDragEnded;
                _panelCards.Add(panel.PanelKey, card);
            }
            else
            {
                card.Panel = panel;
            }

            card.SetRenderingSuspended(_panelRenderingSuspended);
        }

        _dashboardGrid.Children.Clear();
        _dashboardGrid.RowDefinitions.Clear();
        _dashboardGrid.ColumnDefinitions.Clear();

        if (panels.Length == 0)
        {
            _dashboardGrid.Children.Add(CreatePlaceholderCard("Live board", "Telemetry wakes up after the first local sample arrives."));
            return;
        }

        var columns = DetermineDashboardColumns();
        _lastDashboardColumnCount = columns;
        for (var column = 0; column < columns; column++)
        {
            _dashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = (int)Math.Ceiling(panels.Length / (double)columns);
        for (var row = 0; row < rows; row++)
        {
            _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var index = 0; index < panels.Length; index++)
        {
            var card = _panelCards[panels[index].PanelKey];
            _dashboardGrid.Children.Add(card);
            Grid.SetColumn(card, index % columns);
            Grid.SetRow(card, index / columns);
        }
    }

    private int DetermineDashboardColumns()
    {
        var width = _rootLayout.ActualWidth > 0 ? _rootLayout.ActualWidth : 1280;

        // Hysteresis: use slightly different thresholds for expanding vs contracting
        // to prevent flickering at boundary widths
        var columns = _lastDashboardColumnCount switch
        {
            3 => width >= 1180 ? 3 : width >= 840 ? 2 : 1,  // shrinking: hold 3-col longer
            1 => width >= 960 ? (width >= 1300 ? 3 : 2) : 1, // growing: require more width
            _ => width >= 1260 ? 3 : width >= 900 ? 2 : 1,
        };

        _lastDashboardColumnCount = columns;
        return columns;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        StartupTrace.Write("OnRootLoaded start // polished-v19");

        try
        {
            var config = _viewModel.BuildConfig();
            StartupTrace.Write("OnRootLoaded resumed after first paint // polished-v19");
            _autoLaunchService.SetEnabled(config.LaunchOnStartup);
            App.CurrentApp.MarkStartupSettled();
            _ = StartTelemetryAsync(config);
            StartOverlay(config);
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                TryApplyWindowIcon();
            });
            StartupTrace.Write("OnRootLoaded scheduled background startup // polished-v19");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("OnRootLoaded", ex);
            _viewModel.StatusText = $"Startup issue: {ex.Message}";
            UpdateStatusText();
            App.CurrentApp.MarkStartupSettled();
        }
    }

    private async Task StartTelemetryAsync(VaktrConfig config)
    {
        try
        {
            await EnsureCollectorRunningAsync(config);
            _pendingHistoryLoad = TryLoadHistoryAsync(config);

            // Run startup prune in the background — don't block telemetry startup.
            // This catches up on missed maintenance without delaying the first snapshot.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _metricStore.PruneAsync(config, CancellationToken.None);
                    StartupTrace.Write("Startup prune complete");
                }
                catch (Exception pruneEx)
                {
                    StartupTrace.WriteException("Startup prune", pruneEx);
                }
            });

            StartupTrace.Write("Background startup complete // polished-v19");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("StartTelemetryAsync", ex);
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _viewModel.StatusText = $"Startup issue: {ex.Message}";
                UpdateStatusText();
            });
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _overlayController?.Dispose();
        _overlayController = null;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.DashboardPanels.CollectionChanged -= OnDashboardPanelsChanged;
        _viewModel.PanelToggles.CollectionChanged -= OnPanelTogglesChanged;
        Activated -= OnWindowActivated;
        _scrollHost.ViewChanged -= OnScrollHostViewChanged;

        if (_windowIconHandle != 0)
        {
            NativeWindowMethods.DestroyIcon(_windowIconHandle);
            _windowIconHandle = 0;
        }

        // Wait for any in-flight history load to complete so its SQLite
        // read connection is closed before the process exits
        if (_pendingHistoryLoad is not null)
        {
            try
            {
                _pendingHistoryLoad.GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort — history load may have failed
            }

            _pendingHistoryLoad = null;
        }

        if (_collectorService is not null)
        {
            var collectorService = _collectorService;
            _collectorService = null;
            collectorService.SnapshotCollected -= OnSnapshotCollected;
            collectorService.CollectionFailed -= OnCollectionFailed;
            // Wait for disposal to complete so SQLite connections are properly closed
            // and WAL is checkpointed before the process exits
            try
            {
                Task.Run(async () => await collectorService.DisposeAsync())
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup — process is exiting
            }
        }

        // Ensure the metric store is fully disposed and WAL is checkpointed
        if (_metricStore is IAsyncDisposable disposableStore)
        {
            try
            {
                Task.Run(async () => await disposableStore.DisposeAsync())
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup — process is exiting
            }
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_fullUiBuilt)
        {
            return;
        }

        RefreshWindowChrome();

        if (_windowIconApplied)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(TryApplyWindowIcon);
    }


    private async void OnSaveSettingsClick(object? sender, EventArgs e)
    {
        try
        {
            var previousConfig = _viewModel.BuildConfig();
            if (!TryNormalizeScrapeInput(_draftScrapeIntervalInput, out var normalizedScrapeInput))
            {
                _viewModel.StatusText = "Scrape interval must be a whole number of seconds between 1 and 60.";
                UpdateStatusText();
                RenderEditableControlDeck();
                return;
            }

            if (!MainViewModel.TryParseRetentionInput(_draftRetentionInput, out _, out var normalizedRetentionInput))
            {
                _viewModel.StatusText = "Retention must use m, h, or d. Try 30m, 24h, or 7d.";
                UpdateStatusText();
                RenderEditableControlDeck();
                return;
            }

            _viewModel.ScrapeIntervalInput = normalizedScrapeInput;
            _viewModel.RetentionHoursInput = normalizedRetentionInput;
            _viewModel.StorageDirectory = string.IsNullOrWhiteSpace(_draftStorageDirectory)
                ? string.Empty
                : _draftStorageDirectory.Trim();
            _viewModel.SelectedTheme = _draftThemeMode;

            // Build and validate overlay config from drafts
            _viewModel.OverlayConfig = BuildOverlayConfigFromDrafts();

            _viewModel.ApplyPanelVisibility();
            var config = _viewModel.BuildConfig();
            var scrapeChanged = config.ScrapeIntervalSeconds != previousConfig.ScrapeIntervalSeconds;
            var storageChanged = !string.Equals(config.StorageDirectory, previousConfig.StorageDirectory, StringComparison.OrdinalIgnoreCase);
            var previousRetentionWindow = previousConfig.GetRetentionWindow();
            var nextRetentionWindow = config.GetRetentionWindow();
            var retentionChanged =
                nextRetentionWindow != previousRetentionWindow ||
                !string.Equals(config.RetentionInputText, previousConfig.RetentionInputText, StringComparison.OrdinalIgnoreCase);
            var retentionLowered = nextRetentionWindow < previousRetentionWindow;
            var themeChanged = config.Theme != previousConfig.Theme;
            var collectorRestartRequired = scrapeChanged || storageChanged;

            _viewModel.StatusText = collectorRestartRequired
                ? "Applying telemetry settings"
                : retentionLowered
                    ? "Saving settings and pruning older history"
                    : themeChanged
                        ? "Applying theme"
                        : "Saving settings";
            UpdateStatusText();
            _viewModel.ApplyConfig(config);
            SyncControlDeckDraftsFromViewModel();
            _controlDeckEditableActive = false;
            _autoLaunchService.SetEnabled(config.LaunchOnStartup);
            await _configStore.SaveAsync(config, CancellationToken.None);
            if (themeChanged)
            {
                App.CurrentApp.ApplyTheme(config.Theme);
            }
            RenderControlDeckSummary();
            _ = ApplyRuntimeSettingsAsync(config, collectorRestartRequired, retentionChanged, retentionLowered);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Settings issue: {ex.Message}";
            UpdateStatusText();
        }
    }

    private async void OnThemeQuickToggle(object? sender, EventArgs e)
    {
        _draftThemeMode = _draftThemeMode == ThemeMode.Dark
            ? ThemeMode.Light
            : ThemeMode.Dark;
        _viewModel.SelectedTheme = _draftThemeMode;
        App.CurrentApp.ApplyTheme(_draftThemeMode);

        // Persist immediately so the theme is remembered on next launch
        try
        {
            var config = _viewModel.BuildConfig();
            await _configStore.SaveAsync(config, CancellationToken.None);
        }
        catch
        {
            // Best effort — theme will still be active for this session
        }
    }

    private void OnToggleGlobalRangeEditor(object? sender, EventArgs e)
    {
        if (_globalRangeEditorVisible)
        {
            HideGlobalRangeEditor();
            return;
        }

        RenderGlobalRangeEditor();
    }

    private void OnGlobalWindowRangeClick(object? sender, EventArgs e)
    {
        if (sender is ActionChip { Tag: int minutes })
        {
            RunWithDeferredPanelRendering(() => ApplyGlobalWindowRange(minutes));
        }
    }

    private void OnPanelRangePresetRequested(object? sender, TimeRangePresetRequestedEventArgs e)
    {
        RunWithDeferredPanelRendering(() => ApplyGlobalWindowRange((int)e.Preset));
    }

    private void OnPanelZoomSelectionRequested(object? sender, ChartZoomSelectionEventArgs e)
    {
        ApplyAbsoluteGlobalRange(e.StartUtc, e.EndUtc);
    }

    private void OnPanelZoomResetRequested(object? sender, EventArgs e)
    {
        OnResetAllZoomClick(sender, e);
    }

    private void OnPanelDragEnded(object? sender, EventArgs e)
    {
        // Place the dragged card at its correct grid position
        var panels = _viewModel.DashboardPanels.ToArray();
        var columns = DetermineDashboardColumns();

        for (var index = 0; index < panels.Length; index++)
        {
            if (_panelCards.TryGetValue(panels[index].PanelKey, out var card) && ReferenceEquals(card, sender))
            {
                Grid.SetColumn(card, index % columns);
                Grid.SetRow(card, index / columns);
                break;
            }
        }
    }

    private void OnPanelReorderRequested(object? sender, PanelReorderRequestedEventArgs e)
    {
        if (!_viewModel.MovePanel(e.SourcePanelKey, e.TargetPanelKey))
        {
            return;
        }

        // Play a subtle settle animation on the displaced card
        if (_panelCards.TryGetValue(e.TargetPanelKey, out var displacedCard) && !displacedCard.IsDragging)
        {
            displacedCard.PlaySwapSettleAnimation();
        }

        ReorderDashboardPanelsInPlace();
        DebouncePersistLayout();
    }

    private void ReorderDashboardPanelsInPlace()
    {
        var panels = _viewModel.DashboardPanels.ToArray();
        var columns = DetermineDashboardColumns();

        for (var index = 0; index < panels.Length; index++)
        {
            if (_panelCards.TryGetValue(panels[index].PanelKey, out var card))
            {
                if (card.IsDragging)
                {
                    continue;
                }

                var targetCol = index % columns;
                var targetRow = index / columns;
                if (Grid.GetColumn(card) != targetCol)
                {
                    Grid.SetColumn(card, targetCol);
                }

                if (Grid.GetRow(card) != targetRow)
                {
                    Grid.SetRow(card, targetRow);
                }
            }
        }
    }

    private bool _persistLayoutQueued;

    private void DebouncePersistLayout()
    {
        if (_persistLayoutQueued)
        {
            return;
        }

        _persistLayoutQueued = true;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _persistLayoutQueued = false;
            _ = PersistLayoutAsync();
        });
    }

    private void OnResetAllZoomClick(object? sender, EventArgs e)
    {
        RunWithDeferredPanelRendering(() =>
        {
            _globalAbsoluteStartUtc = null;
            _globalAbsoluteEndUtc = null;
            _viewModel.ResetGlobalZoom();
        });

        HideGlobalRangeEditor();
        RefreshGlobalRangeControls();
    }

    private void OnSnapshotCollected(object? sender, MetricSnapshot snapshot)
    {
        _overlayController?.OnSnapshot(snapshot);

        _pendingSnapshot = snapshot;
        if (_snapshotApplyQueued)
        {
            return;
        }

        _snapshotApplyQueued = true;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyQueuedSnapshot);
    }

    private void OnCollectionFailed(object? sender, Exception ex)
    {
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            StartupTrace.WriteException("Collection failed", ex);
            if (!_hasReceivedFirstSnapshot)
            {
                _viewModel.StatusText = $"Telemetry retrying: {ex.Message}";
                UpdateStatusText();
            }
        });
    }

    private void ApplyQueuedSnapshot()
    {
        _snapshotApplyQueued = false;

        // During window resize, don't apply snapshots at all — keep them pending.
        // Text/value updates cause WinUI layout invalidation which is the main
        // source of resize stutter. The snapshot will be applied when resize ends.
        if (_isWindowResizing)
        {
            if (_pendingSnapshot is not null)
            {
                _snapshotApplyQueued = true;
                _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyQueuedSnapshot);
            }
            return;
        }

        var snapshot = _pendingSnapshot;
        _pendingSnapshot = null;
        if (snapshot is null)
        {
            return;
        }

        if (!_hasReceivedFirstSnapshot)
        {
            _hasReceivedFirstSnapshot = true;
            StartupTrace.Write($"First snapshot collected // samples={snapshot.Samples.Count}");
        }

        // Suspend rendering during the snapshot application pass so that
        // individual panel property changes don't trigger separate chart redraws.
        // Everything renders once at the end when we resume.
        SetPanelRenderingSuspended(true);
        _viewModel.ApplySnapshot(snapshot);
        if (_globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue)
        {
            foreach (var panel in _viewModel.DashboardPanels)
            {
                if (!panel.IsZoomed)
                {
                    panel.ZoomToWindow(_globalAbsoluteStartUtc.Value, _globalAbsoluteEndUtc.Value);
                }
            }
        }

        SetPanelRenderingSuspended(false);
        UpdateStatusText();

        if (_pendingSnapshot is not null && !_snapshotApplyQueued)
        {
            _snapshotApplyQueued = true;
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyQueuedSnapshot);
        }
    }

    private void DismissLoadingOverlay()
    {
        if (_loadingOverlay is null)
        {
            return;
        }

        var overlay = _loadingOverlay;
        _loadingOverlay = null;

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeOut);
        Storyboard.SetTarget(fadeOut, overlay);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        storyboard.Completed += (_, _) =>
        {
            if (overlay.Parent is Grid parentGrid)
            {
                parentGrid.Children.Remove(overlay);
            }
        };
        storyboard.Begin();
    }

    private async Task PersistLayoutAsync()
    {
        try
        {
            await _configStore.SaveAsync(_viewModel.BuildConfig(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("PersistLayout", ex);
        }
    }

    private void QueueDashboardRefresh()
    {
        if (_dashboardRefreshQueued)
        {
            return;
        }

        _dashboardRefreshQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _dashboardRefreshQueued = false;
            if (!_initialized)
            {
                return;
            }

            StartupTrace.Write("Queued dashboard refresh after resize // polished-v19");
            RefreshDashboardPanels();
        });
    }

    private void OnScrollHostViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isWindowResizing) return; // no scroll handling during resize

        if (e.IsIntermediate)
        {
            SetPanelRenderingSuspended(true);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => SetPanelRenderingSuspended(false));
    }

    private void RunWithDeferredPanelRendering(Action action)
    {
        SetPanelRenderingSuspended(true);
        try
        {
            action();
        }
        finally
        {
            SetPanelRenderingSuspended(false);
        }
    }

    private void SetPanelRenderingSuspended(bool suspended)
    {
        if (_panelRenderingSuspended == suspended)
        {
            return;
        }

        _panelRenderingSuspended = suspended;
        foreach (var card in _panelCards.Values)
        {
            card.SetRenderingSuspended(suspended);
        }
    }

    private bool _isWindowResizing;
    private Border? _shellBorderRef;

    private void StartOverlay(VaktrConfig config)
    {
        try
        {
            var controller = new OverlayController();
            controller.StatusMessage += msg =>
            {
                if (msg == "__reregister_hotkey__")
                {
                    var hwnd = WindowNative.GetWindowHandle(this);
                    controller.RegisterHotkey(hwnd);
                    return;
                }
                _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    _viewModel.StatusText = msg;
                    UpdateStatusText();
                });
            };

            var shellHwnd = WindowNative.GetWindowHandle(this);
            HotkeyInterop.Install(shellHwnd, id => controller.OnWmHotkey(id));

            _overlayController = controller;
            _overlayController.Start();

            if (config.Overlay != null)
            {
                _overlayController.ApplyConfig(config.Overlay);
                _overlayController.RegisterHotkey(shellHwnd);
            }
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("StartOverlay", ex);
        }
    }

    private void InstallResizeHook()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ResizeInterop.Install(hwnd,
                onEnterResize: () => DispatcherQueue.TryEnqueue(() =>
                {
                    _isWindowResizing = true;

                    if (_scrollHost.Content is UIElement scrollContent)
                    {
                        scrollContent.CacheMode = new BitmapCache();
                    }

                    if (_shellBorderRef is not null)
                    {
                        _shellBorderRef.CornerRadius = new CornerRadius(0);
                    }

                    foreach (var card in _panelCards.Values)
                    {
                        card.SetRenderingSuspended(true);
                        card.SetResizeMode(true);
                    }

                    // Safety: auto-clear resize flag after 5 seconds in case WM_EXITSIZEMOVE is missed
                    _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isWindowResizing)
                            {
                                _isWindowResizing = false;
                                if (_scrollHost.Content is UIElement sc) sc.CacheMode = null;
                                if (_shellBorderRef is not null) _shellBorderRef.CornerRadius = new CornerRadius(0);
                                foreach (var card in _panelCards.Values)
                                {
                                    card.SetResizeMode(false);
                                    card.SetRenderingSuspended(false);
                                }
                            }
                        });
                    }, TaskScheduler.Default);
                }),
                onExitResize: () => DispatcherQueue.TryEnqueue(() =>
                {
                    _isWindowResizing = false;

                    // Remove bitmap cache — back to live rendering
                    if (_scrollHost.Content is UIElement scrollContent)
                    {
                        scrollContent.CacheMode = null;
                    }

                    if (_shellBorderRef is not null)
                    {
                        _shellBorderRef.CornerRadius = new CornerRadius(0);
                    }

                    var nextColumnCount = DetermineDashboardColumns();
                    if (nextColumnCount != _lastDashboardColumnCount)
                    {
                        QueueDashboardRefresh();
                    }
                    else if (_summaryCardsBound && DetermineSummaryColumns() != _lastSummaryColumnCount)
                    {
                        BuildSummaryCards();
                    }

                    foreach (var card in _panelCards.Values)
                    {
                        card.SetResizeMode(false);
                        card.SetRenderingSuspended(false);
                    }
                }));
        }
        catch
        {
            // Subclassing may fail in some hosts — fall back to SizeChanged
        }
    }

    private void OnRootLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // When the Win32 resize hook is active, skip all work here — the hook handles it
        if (!_initialized || _isWindowResizing)
        {
            return;
        }

        // Handle programmatic resizes (not drag) that don't trigger WM_ENTERSIZEMOVE
        var nextColumnCount = DetermineDashboardColumns();
        if (nextColumnCount != _lastDashboardColumnCount)
        {
            QueueDashboardRefresh();
        }
        else if (_summaryCardsBound && DetermineSummaryColumns() != _lastSummaryColumnCount)
        {
            BuildSummaryCards();
        }
    }

    private void OnDashboardPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            RefreshDashboardPanels();
            return;
        }

        // For move/reorder events, just update grid positions (fast)
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            ReorderDashboardPanelsInPlace();
            return;
        }

        // For add/remove/reset, do a full rebuild
        QueueDashboardRefresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isWindowResizing) return; // no UI updates during resize

        if (string.Equals(e.PropertyName, nameof(MainViewModel.StatusText), StringComparison.Ordinal))
        {
            UpdateStatusText();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainViewModel.SelectedWindowMinutes), StringComparison.Ordinal))
        {
            RefreshGlobalRangeControls();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainViewModel.StorageDirectory), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainViewModel.ScrapeIntervalInput), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainViewModel.RetentionHoursInput), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainViewModel.LaunchOnStartup), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainViewModel.MinimizeToTray), StringComparison.Ordinal))
        {
            if (!_controlDeckEditableActive)
            {
                RenderControlDeckSummary();
            }
        }
    }

    private void OnPanelTogglesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
    }

    private void OnScrapeFieldClick(object? sender, EventArgs e)
    {
        BeginControlDeckEdit(DeckEditorMode.Scrape);
    }

    private void OnRetentionFieldClick(object? sender, EventArgs e)
    {
        BeginControlDeckEdit(DeckEditorMode.Retention);
    }

    private void OnStorageFieldClick(object? sender, EventArgs e)
    {
        BeginControlDeckEdit(DeckEditorMode.Storage);
    }

    private void OnRetentionInputChanged(object? sender, EventArgs e)
    {
        if (sender is InlineTextEntry entry)
        {
            _draftRetentionInput = entry.Text;
        }
    }

    private void OnScrapeInputChanged(object? sender, EventArgs e)
    {
        if (sender is InlineTextEntry entry)
        {
            _draftScrapeIntervalInput = entry.Text;
        }
    }

    private void OnStorageInputChanged(object? sender, EventArgs e)
    {
        if (sender is InlineTextEntry entry)
        {
            _draftStorageDirectory = entry.Text;
        }
    }

    private void OnEditSettingsClick(object? sender, EventArgs e)
    {
        BeginControlDeckEdit();
    }

    private void OnCancelSettingsClick(object? sender, EventArgs e)
    {
        var persistedTheme = _viewModel.SelectedTheme;
        var restoreThemePreview = _draftThemeMode != persistedTheme;
        SyncControlDeckDraftsFromViewModel();
        _controlDeckEditableActive = false;
        if (restoreThemePreview)
        {
            App.CurrentApp.ApplyTheme(persistedTheme);
        }
        else
        {
            RenderControlDeckSummary();
        }

        _viewModel.StatusText = "Control deck unchanged";
        UpdateStatusText();
    }

    private void BeginControlDeckEdit(DeckEditorMode mode = DeckEditorMode.Scrape)
    {
        SyncControlDeckDraftsFromViewModel();
        _activeDeckEditor = mode;
        RenderEditableControlDeck();
    }

    private void QueueEditableControlDeckRender()
    {
        if (!_controlDeckEditableActive || _controlDeckRenderQueued)
        {
            return;
        }

        _controlDeckRenderQueued = true;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _controlDeckRenderQueued = false;
            if (_controlDeckEditableActive)
            {
                RenderEditableControlDeck();
            }
        });
    }

    private void SyncControlDeckDraftsFromViewModel()
    {
        _draftScrapeIntervalInput = _viewModel.ScrapeIntervalInput;
        _draftRetentionInput = _viewModel.RetentionHoursInput;
        _draftStorageDirectory = _viewModel.StorageDirectory;
        _draftThemeMode = _viewModel.SelectedTheme;

        var ov = _viewModel.OverlayConfig;
        _draftOverlayEnabled = ov.Enabled;
        _draftOverlayMode = ov.Mode;
        _draftOverlayCorner = ov.Corner;
        _draftOverlayColor = ov.TextColor;
        _draftOverlayFontSize = ov.FontSizePx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _draftOverlayOpacity = ((int)Math.Round(ov.Opacity * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _draftOverlayHotkey = ov.HotkeyGesture;
        _draftOverlayCustomMetrics = [.. ov.CustomMetrics];
    }

    private Vaktr.Core.Models.OverlayConfig BuildOverlayConfigFromDrafts()
    {
        int.TryParse(_draftOverlayFontSize?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var fontSize);
        int.TryParse(_draftOverlayOpacity?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var opacityPct);

        return new Vaktr.Core.Models.OverlayConfig
        {
            Enabled = _draftOverlayEnabled,
            Mode = _draftOverlayMode,
            Corner = _draftOverlayCorner,
            TextColor = _draftOverlayColor,
            FontSizePx = fontSize,
            Opacity = opacityPct / 100.0,
            HotkeyGesture = _draftOverlayHotkey,
            CustomMetrics = [.. _draftOverlayCustomMetrics],
        }.Normalize();
    }

    private int GetDraftScrapeIntervalSeconds() =>
        int.TryParse(_draftScrapeIntervalInput?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 1 and <= 60
            ? seconds
            : VaktrConfig.DefaultScrapeIntervalSeconds;

    private int GetDraftRetentionHours() =>
        MainViewModel.TryParseRetentionInput(_draftRetentionInput, out var hours, out _)
            ? hours
            : VaktrConfig.DefaultMaxRetentionHours;

    private void StepScrapeInterval(int direction)
    {
        var next = MoveThroughPresets(GetDraftScrapeIntervalSeconds(), ScrapeIntervalPresets, direction);
        _draftScrapeIntervalInput = next == VaktrConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : next.ToString(CultureInfo.InvariantCulture);
        QueueEditableControlDeckRender();
    }

    private void SetScrapeInterval(int seconds)
    {
        _draftScrapeIntervalInput = seconds == VaktrConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : seconds.ToString(CultureInfo.InvariantCulture);
        QueueEditableControlDeckRender();
    }

    private void NudgeScrapeInterval(int delta)
    {
        var next = Math.Clamp(_viewModel.EffectiveScrapeIntervalSeconds + delta, 1, 60);
        SetScrapeInterval(next);
    }

    private void ResetScrapeInterval()
    {
        _draftScrapeIntervalInput = string.Empty;
        QueueEditableControlDeckRender();
    }

    private void SetRetentionInput(string value)
    {
        _draftRetentionInput = value;
        QueueEditableControlDeckRender();
    }

    private void StepRetentionHours(int direction)
    {
        var next = MoveThroughPresets(GetDraftRetentionHours(), RetentionHourPresets, direction);
        _draftRetentionInput = FormatRetentionDraftInput(next);
        QueueEditableControlDeckRender();
    }

    private void SetRetentionHours(int hours)
    {
        _draftRetentionInput = FormatRetentionDraftInput(hours);
        QueueEditableControlDeckRender();
    }

    private void NudgeRetentionHours(int delta)
    {
        var next = Math.Clamp(GetDraftRetentionHours() + delta, 1, 24 * 3650);
        SetRetentionHours(next);
    }

    private void ResetRetentionHours()
    {
        _draftRetentionInput = string.Empty;
        QueueEditableControlDeckRender();
    }

    private static string FormatRetentionDraftInput(int hours) =>
        hours <= 0 ? string.Empty : MainViewModel.FormatRetentionInput(hours);

    private void ResetStorageDirectory()
    {
        _draftStorageDirectory = string.Empty;
        QueueEditableControlDeckRender();
    }

    private void SetStorageDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _draftStorageDirectory = value.Trim();
        QueueEditableControlDeckRender();
    }

    private static bool TryNormalizeScrapeInput(string? text, out string normalizedText)
    {
        normalizedText = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (seconds is < 1 or > 60)
        {
            return false;
        }

        normalizedText = seconds == VaktrConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : seconds.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private async Task ApplyRuntimeSettingsAsync(
        VaktrConfig config,
        bool collectorRestartRequired,
        bool retentionChanged,
        bool retentionLowered)
    {
        try
        {
            await Task.Run(async () =>
            {
                if (collectorRestartRequired)
                {
                    if (_collectorService is null)
                    {
                        _collectorService = new CollectorService(new WindowsMetricCollector(), _metricStore);
                        _collectorService.SnapshotCollected += OnSnapshotCollected;
                        _collectorService.CollectionFailed += OnCollectionFailed;
                    }

                    await _collectorService.StartAsync(config, CancellationToken.None);
                }
                else if (retentionChanged)
                {
                    await _metricStore.InitializeAsync(config, CancellationToken.None);
                    if (retentionLowered)
                    {
                        await _metricStore.PruneAsync(config, CancellationToken.None);
                    }
                }
            });

            _viewModel.StatusText = _viewModel.DashboardPanels.Count > 0
                ? "Streaming local telemetry"
                : "Waiting for first sample";
            UpdateStatusText();

            if (_overlayController != null && config.Overlay != null)
            {
                _overlayController.ApplyConfig(config.Overlay);
                // Re-register hotkey if gesture changed (controller signals via StatusMessage)
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Settings issue: {ex.Message}";
            UpdateStatusText();
        }
    }

    private async void OnBrowseStorageClick(object? sender, EventArgs e)
    {
        try
        {
            _activeDeckEditor = DeckEditorMode.Storage;
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            SetStorageDirectory(folder.Path);
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("BrowseStorageClick", ex);
            _viewModel.StatusText = $"Storage picker issue: {ex.Message}";
            UpdateStatusText();
        }
    }

    private static int MoveThroughPresets(int currentValue, IReadOnlyList<int> presets, int direction)
    {
        if (presets.Count == 0)
        {
            return currentValue;
        }

        if (direction > 0)
        {
            foreach (var preset in presets)
            {
                if (preset > currentValue)
                {
                    return preset;
                }
            }

            return presets[^1];
        }

        for (var index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index] < currentValue)
            {
                return presets[index];
            }
        }

        return presets[0];
    }

    private void TryApplyWindowIcon()
    {
        if (_windowIconApplied)
        {
            return;
        }

        try
        {
            // Try ico first, then fall back to png
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Vaktr.ico");
            var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "vaktr.png");
            var iconPath = File.Exists(icoPath) ? icoPath : File.Exists(pngPath) ? pngPath : null;

            if (iconPath is not null)
            {
                AppWindow.SetIcon(iconPath);
                var windowHandle = WindowNative.GetWindowHandle(this);
                if (windowHandle != 0)
                {
                    if (_windowIconHandle != 0)
                    {
                        NativeWindowMethods.DestroyIcon(_windowIconHandle);
                        _windowIconHandle = 0;
                    }

                    _windowIconHandle = NativeWindowMethods.LoadWindowIcon(iconPath);
                    if (_windowIconHandle != 0)
                    {
                        NativeWindowMethods.ApplyWindowIcon(windowHandle, _windowIconHandle);
                    }
                }

                _windowIconApplied = true;
                StartupTrace.Write($"Window icon applied from {System.IO.Path.GetFileName(iconPath)}");
            }
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("ApplyWindowIcon", ex);
        }
    }

    private void TryLoadBrandImage()
    {
        try
        {
            var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "vaktr.png");
            if (!File.Exists(imagePath))
            {
                return;
            }

            StartupTrace.Write("TryLoadBrandImage start // polished-v19");
            if (s_brandBitmap is null || !string.Equals(s_brandBitmapPath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(imagePath);
                s_brandBitmap = bitmap;
                s_brandBitmapPath = imagePath;
            }

            _brandHost.Width = 44;
            _brandHost.Height = 44;
            _brandHost.Children.Clear();
            _brandHost.Children.Add(new Microsoft.UI.Xaml.Controls.Image
            {
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                Source = s_brandBitmap,
            });
            StartupTrace.Write("TryLoadBrandImage complete // polished-v19");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("TryLoadBrandImage", ex);
        }
    }

    private void RenderSummaryPlaceholders()
    {
        _summaryCardsBound = false;
        _summaryHost.Children.Clear();
        _summaryHost.RowDefinitions.Clear();
        _summaryHost.ColumnDefinitions.Clear();
        var summaryColumns = DetermineSummaryColumns();
        _lastSummaryColumnCount = summaryColumns;
        for (var column = 0; column < summaryColumns; column++)
        {
            _summaryHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var placeholders = new[]
        {
            CreatePlaceholderCard("CPU", "Waiting for first sample"),
            CreatePlaceholderCard("Memory", "Loading local history"),
            CreatePlaceholderCard("Disk", "Preparing local boards"),
            CreatePlaceholderCard("Network", "Warming link metrics"),
        };

        for (var index = 0; index < placeholders.Length; index++)
        {
            while (_summaryHost.RowDefinitions.Count <= index / summaryColumns)
            {
                _summaryHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            _summaryHost.Children.Add(placeholders[index]);
            Grid.SetColumn(placeholders[index], index % summaryColumns);
            Grid.SetRow(placeholders[index], index / summaryColumns);
        }
    }

    private int DetermineSummaryColumns()
    {
        var width = _rootLayout.ActualWidth > 0 ? _rootLayout.ActualWidth : 1280;
        return width >= 1120 ? 4 : width >= 820 ? 2 : 1;
    }

    private async Task TryLoadHistoryAsync(VaktrConfig config)
    {
        try
        {
            StartupTrace.Write("TryLoadHistoryAsync start");
            // Load the full retention window so users can zoom out to see all retained data
            var retentionWindow = config.GetRetentionWindow();
            var historyWindow = retentionWindow;
            var history = await _metricStore.LoadHistoryAsync(DateTimeOffset.UtcNow.Subtract(historyWindow), CancellationToken.None);
            _viewModel.LoadHistory(history);
            _lastLoadedHistoryMinutes = (int)historyWindow.TotalMinutes;
            StartupTrace.Write($"TryLoadHistoryAsync complete // panels={history.Count}");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("History load", ex);
            if (!_hasReceivedFirstSnapshot)
            {
                _viewModel.StatusText = "History unavailable, starting live telemetry";
                UpdateStatusText();
            }
        }
    }

    private async Task EnsureCollectorRunningAsync(VaktrConfig config)
    {
        StartupTrace.Write("EnsureCollectorRunningAsync start");
        _collectorService ??= new CollectorService(new WindowsMetricCollector(), _metricStore);
        StartupTrace.Write("CollectorService created");
        _collectorService.SnapshotCollected -= OnSnapshotCollected;
        _collectorService.CollectionFailed -= OnCollectionFailed;
        _collectorService.SnapshotCollected += OnSnapshotCollected;
        _collectorService.CollectionFailed += OnCollectionFailed;

        _viewModel.StatusText = "Starting telemetry";
        UpdateStatusText();
        StartupTrace.Write("Calling CollectorService.StartAsync");

        // StartAsync initializes the SQLite store before returning. Awaiting it
        // here keeps history loading from racing that initialization; sampling
        // itself continues on the collector's background loop.
        await _collectorService.StartAsync(config, CancellationToken.None);
        StartupTrace.Write("CollectorService.StartAsync complete");

        // The shell is already visible; show a waiting status until the first sample.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_summaryCardsBound)
            {
                BuildSummaryCards();
            }
            _viewModel.StatusText = "Waiting for first sample";
            UpdateStatusText();
            StartupTrace.Write($"EnsureCollectorRunningAsync complete // dashboardPanels={_viewModel.DashboardPanels.Count}");
        });
    }

    private void UpdateStatusText()
    {
        var text = _viewModel.StatusText;
        _statusText.Text = text;
        var isIssue = text.Contains("issue", StringComparison.OrdinalIgnoreCase)
            || text.Contains("retrying", StringComparison.OrdinalIgnoreCase);
        var isWaiting = text.Contains("waiting", StringComparison.OrdinalIgnoreCase)
            || text.Contains("arming", StringComparison.OrdinalIgnoreCase);
        _statusIndicator.Fill = isIssue
            ? ResolveBrush("CriticalBrush", "#FF897D")
            : isWaiting
                ? ResolveBrush("TextMutedBrush", "#858B93")
                : ResolveBrush("AccentBrush", "#79B8FF");

        if (!string.Equals(_lastRenderedStatusText, text, StringComparison.Ordinal))
        {
            _lastRenderedStatusText = text;
            StartupTrace.Write($"StatusText -> {text}");
        }
    }

    private void ApplyGlobalWindowRange(int minutes)
    {
        _globalAbsoluteStartUtc = null;
        _globalAbsoluteEndUtc = null;
        _viewModel.ApplyGlobalWindowRange(minutes);
        HideGlobalRangeEditor();
        RefreshGlobalRangeControls();

        // If the user selects a wider range than what we last loaded from the database,
        // re-query to ensure the data covers the full requested window.
        if (minutes > _lastLoadedHistoryMinutes)
        {
            _ = ReloadHistoryForRangeAsync(minutes);
        }
    }

    private async Task ReloadHistoryForRangeAsync(int minutes)
    {
        try
        {
            var fromUtc = DateTimeOffset.UtcNow.AddMinutes(-minutes);
            var history = await _metricStore.LoadHistoryAsync(fromUtc, CancellationToken.None);
            _viewModel.LoadHistory(history);
            _lastLoadedHistoryMinutes = minutes;
        }
        catch
        {
            // Best-effort — live data will still flow
        }
    }

    private void RefreshGlobalRangeControls()
    {
        var noAbsolute = !_globalAbsoluteStartUtc.HasValue;
        var minutes = _viewModel.SelectedWindowMinutes;
        ApplyGlobalRangeState(_globalOneMinuteButton, noAbsolute && minutes == 1);
        ApplyGlobalRangeState(_globalFiveMinuteButton, noAbsolute && minutes == 5);
        ApplyGlobalRangeState(_globalFifteenMinuteButton, noAbsolute && minutes == 15);
        ApplyGlobalRangeState(_globalThirtyMinuteButton, noAbsolute && minutes == 30);
        ApplyGlobalRangeState(_globalOneHourButton, noAbsolute && minutes == 60);
        ApplyGlobalRangeState(_globalTwelveHourButton, noAbsolute && minutes == 720);
        ApplyGlobalRangeState(_globalTwentyFourHourButton, noAbsolute && minutes == 1440);
        ApplyGlobalRangeState(_globalTwoDayButton, noAbsolute && minutes == 2880);
        ApplyGlobalRangeState(_globalFiveDayButton, noAbsolute && minutes == 7200);
        ApplyGlobalRangeState(_globalSevenDayButton, noAbsolute && minutes == 10080);
        ApplyGlobalRangeState(_globalThirtyDayButton, noAbsolute && minutes == 43200);
        ApplyGlobalRangeState(_globalNinetyDayButton, noAbsolute && minutes == 129600);
        ApplyGlobalRangeState(_globalOneYearButton, noAbsolute && minutes == 525600);

        _globalRangeButton.Text = BuildGlobalRangeButtonText();
        _globalRangeButton.IsActive = _globalRangeEditorVisible;
        _globalRangeButton.IsFilled = true;
        _globalResetZoomButton.Opacity = _viewModel.DashboardPanels.Any(panel => panel.IsZoomed) ? 1d : 0.82d;
    }

    private void RefreshWindowChrome()
    {
        try
        {
            if (_shellBorderRef is not null)
            {
                _shellBorderRef.Background = BrushFactory.CreateBrush("#00FFFFFF");
                _shellBorderRef.BorderBrush = BrushFactory.CreateBrush("#00FFFFFF");
            }

            var titleBar = AppWindow.TitleBar;
            if (titleBar is null)
            {
                return;
            }

            var transparent = Color.FromArgb(0, 0, 0, 0);
            var foreground = App.CurrentApp.ResolveThemeColor("TextSecondaryBrush", _rootLayout.RequestedTheme == ElementTheme.Light ? "#4A6078" : "#C6D7EA");
            var hoverBackground = App.CurrentApp.ResolveThemeColor("SurfaceElevatedBrush", _rootLayout.RequestedTheme == ElementTheme.Light ? "#F4F8FC" : "#112033");
            var pressedBackground = App.CurrentApp.ResolveThemeColor("SurfaceStrongBrush", _rootLayout.RequestedTheme == ElementTheme.Light ? "#EDF4FB" : "#18314A");
            var inactiveForeground = App.CurrentApp.ResolveThemeColor("TextMutedBrush", _rootLayout.RequestedTheme == ElementTheme.Light ? "#6F8399" : "#8098B2");

            titleBar.BackgroundColor = transparent;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = transparent;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = transparent;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("RefreshWindowChrome", ex);
        }
    }

    private void RefreshShellSurfaceThemeResources()
    {
        if (_controlsSurfaceRef is not null)
        {
            _controlsSurfaceRef.Background = BrushFactory.CreateBrush("#00FFFFFF");
            _controlsSurfaceRef.BorderBrush = BrushFactory.CreateBrush("#00FFFFFF");
        }
    }

    private void RenderGlobalRangeEditor()
    {
        _globalRangeEditorVisible = true;
        var startText = (_globalAbsoluteStartUtc ?? DateTimeOffset.Now.AddMinutes(-_viewModel.SelectedWindowMinutes)).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var endText = (_globalAbsoluteEndUtc ?? DateTimeOffset.Now).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var currentLabel = _globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue
            ? "Pinned window"
            : $"Last {FormatGlobalRangeLabel(_viewModel.SelectedWindowMinutes)}";
        var currentMode = _globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue
            ? "Absolute"
            : "Rolling";

        var fromEntry = CreateDeckInlineEntry(startText, "2026-04-02 09:00");
        var toEntry = CreateDeckInlineEntry(endText, "2026-04-02 09:30");
        var quickRangeRowPrimary = CreateChipWrapRow(
            _globalOneMinuteButton,
            _globalFiveMinuteButton,
            _globalFifteenMinuteButton,
            _globalThirtyMinuteButton,
            _globalOneHourButton);
        var quickRangeRowSecondary = CreateChipWrapRow(
            _globalTwelveHourButton,
            _globalTwentyFourHourButton,
            _globalTwoDayButton,
            _globalFiveDayButton,
            _globalSevenDayButton);
        var quickRangeRowTertiary = CreateChipWrapRow(
            _globalThirtyDayButton,
            _globalNinetyDayButton,
            _globalOneYearButton);

        var quickRangeCard = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#132235"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    CreateSectionHeader("QUICK RANGES", "Jump the whole board to a preset replay window."),
                    quickRangeRowPrimary,
                    quickRangeRowSecondary,
                    quickRangeRowTertiary,
                },
            },
        };

        var fromColumn = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("From"),
                fromEntry,
            },
        };
        var toColumn = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("To"),
                toEntry,
            },
        };
        var absoluteGrid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        absoluteGrid.Children.Add(fromColumn);
        absoluteGrid.Children.Add(toColumn);
        Grid.SetColumn(toColumn, 1);

        var absoluteRangeCard = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#132235"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    CreateSectionHeader("ABSOLUTE RANGE", "Pin every panel to one precise local time slice."),
                    absoluteGrid,
                    CreateSecondaryText("Use local time like 2026-04-02 21:30.", 12),
                },
            },
        };

        var infoRow = new Grid
        {
            ColumnSpacing = 12,
        };
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        infoRow.Children.Add(CreateInfoPanel("Current", currentLabel));
        var modePanel = CreateInfoPanel("Mode", currentMode);
        infoRow.Children.Add(modePanel);
        Grid.SetColumn(modePanel, 1);

        var actionButtons = CreateChipRow(
            CreateActionChip("Close", (_, _) => HideGlobalRangeEditor()),
            CreateActionChip("Now", (_, _) =>
            {
                var next = DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                toEntry.Text = next;
            }),
            CreateActionChip("Apply range", (_, _) => ApplyAbsoluteGlobalRange(fromEntry.Text, toEntry.Text), true));
        var actionGrid = new Grid
        {
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        actionGrid.Children.Add(CreateMutedText("Applies to every panel.", 12));
        actionGrid.Children.Add(actionButtons);
        Grid.SetColumn(actionButtons, 1);

        var shell = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    CreateSectionHeader("TIME RANGE", "Keep every panel in sync with one shared timeline."),
                    infoRow,
                    quickRangeCard,
                    absoluteRangeCard,
                    actionGrid,
                },
            },
        };

        _globalRangeEditorHost.Child = shell;
        _globalRangeEditorHost.Visibility = Visibility.Visible;
        RefreshGlobalRangeControls();
    }

    private void HideGlobalRangeEditor()
    {
        _globalRangeEditorVisible = false;
        _globalRangeEditorHost.Child = null;
        _globalRangeEditorHost.Visibility = Visibility.Collapsed;
        RefreshGlobalRangeControls();
    }

    private void ApplyAbsoluteGlobalRange(string fromText, string toText)
    {
        if (!TryParseGlobalDateTime(fromText, out var start) || !TryParseGlobalDateTime(toText, out var end) || end <= start)
        {
            _viewModel.StatusText = "Time range must use local date/time like 2026-04-02 21:30, and End must be after From.";
            UpdateStatusText();
            return;
        }

        ApplyAbsoluteGlobalRange(start, end);
    }

    private void ApplyAbsoluteGlobalRange(DateTimeOffset start, DateTimeOffset end)
    {
        RunWithDeferredPanelRendering(() =>
        {
            _globalAbsoluteStartUtc = start;
            _globalAbsoluteEndUtc = end;
            _viewModel.ApplyGlobalAbsoluteRange(start, end);
        });

        HideGlobalRangeEditor();
        _viewModel.StatusText = $"Pinned {start.LocalDateTime:g} to {end.LocalDateTime:g}";
        UpdateStatusText();
    }

    private string BuildGlobalRangeButtonText()
    {
        if (_globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue)
        {
            return $"{_globalAbsoluteStartUtc.Value.LocalDateTime:MM-dd HH:mm} -> {_globalAbsoluteEndUtc.Value.LocalDateTime:MM-dd HH:mm}";
        }

        return FormatGlobalRangeLabel(_viewModel.SelectedWindowMinutes);
    }

    private string GetGlobalRangeButtonText()
    {
        if (_globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue)
        {
            return $"{_globalAbsoluteStartUtc.Value.LocalDateTime:MM-dd HH:mm} -> {_globalAbsoluteEndUtc.Value.LocalDateTime:MM-dd HH:mm} ▾";
        }

        return $"{FormatGlobalRangeLabel(_viewModel.SelectedWindowMinutes)} ▾";
    }

    private static string FormatGlobalRangeLabel(int minutes) => minutes switch
    {
        <= 1 => "1m",
        <= 5 => "5m",
        <= 15 => "15m",
        <= 30 => "30m",
        <= 60 => "1h",
        <= 720 => "12h",
        <= 1440 => "24h",
        <= 2880 => "2d",
        <= 7200 => "5d",
        <= 10080 => "7d",
        <= 43200 => "30d",
        <= 129600 => "90d",
        _ => "1y",
    };

    private static bool TryParseGlobalDateTime(string text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateTime.TryParseExact(
                text.Trim(),
                ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var exact))
        {
            value = new DateTimeOffset(exact);
            return true;
        }

        if (DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            value = new DateTimeOffset(parsed);
            return true;
        }

        return false;
    }

    private static void ApplyGlobalRangeState(ActionChip chip, bool isActive)
    {
        chip.IsActive = isActive;
        chip.IsFilled = isActive;
        chip.Opacity = isActive ? 1d : 0.82d;
    }
}

internal enum DeckEditorMode
{
    Scrape,
    Retention,
    Storage,
}

internal sealed record SummaryCardVisual(
    Border Surface,
    TextBlock TitleText,
    TextBlock ValueText,
    TextBlock CaptionText,
    Border BadgeHost,
    Border GaugeTrack,
    Border GaugeFill,
    SummaryCardViewModel ViewModel);

internal static class HotkeyInterop
{
    private const uint WM_HOTKEY = 0x0312;
    private const nint SubclassId = 1002;

    private static Action<int>? s_onHotkey;
    private static SubclassProc? s_procDelegate;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    public static void Install(nint hwnd, Action<int> onHotkey)
    {
        s_onHotkey = onHotkey;
        s_procDelegate = SubclassCallback;
        SetWindowSubclass(hwnd, s_procDelegate, SubclassId, 0);
    }

    private static nint SubclassCallback(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_HOTKEY)
            s_onHotkey?.Invoke((int)wParam);

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}

internal static class ResizeInterop
{
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;
    private const nint SubclassId = 1001;

    private static Action? s_onEnterResize;
    private static Action? s_onExitResize;
    private static SubclassProc? s_procDelegate; // prevent GC collection

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    public static void Install(nint hwnd, Action onEnterResize, Action onExitResize)
    {
        s_onEnterResize = onEnterResize;
        s_onExitResize = onExitResize;
        s_procDelegate = SubclassCallback;
        SetWindowSubclass(hwnd, s_procDelegate, SubclassId, 0);
    }

    private static nint SubclassCallback(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_ENTERSIZEMOVE)
        {
            s_onEnterResize?.Invoke();
        }
        else if (uMsg == WM_EXITSIZEMOVE)
        {
            s_onExitResize?.Invoke();
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}

internal static class NativeWindowMethods
{
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint DefaultSize = 0x0040;
    private const uint WindowSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    public static nint LoadWindowIcon(string iconPath) =>
        LoadImageW(0, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);

    public static void ApplyWindowIcon(nint windowHandle, nint iconHandle)
    {
        SendMessageW(windowHandle, WindowSetIcon, (nint)IconSmall, iconHandle);
        SendMessageW(windowHandle, WindowSetIcon, (nint)IconBig, iconHandle);
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
        nint instanceHandle,
        string name,
        uint imageType,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint iconHandle);
}
