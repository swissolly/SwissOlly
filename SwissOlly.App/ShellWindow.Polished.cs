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
using SwissOlly.App.Controls;
using SwissOlly.App.Services;
using SwissOlly.App.ViewModels;
using SwissOlly.Collector;
using SwissOlly.Core.Interfaces;
using SwissOlly.Core.Models;
using SwissOlly.Overlay;

namespace SwissOlly.App;

public sealed partial class ShellWindow : Window
{
    private const double PageGutter = 24;
    private const double SectionSpacing = 20;
    private const double PanelSpacing = 14;
    private static BitmapImage? s_brandBitmap;
    private static string? s_brandBitmapPath;

    private readonly MainViewModel _viewModel;

    /// <summary>
    /// The UI thread's queue, captured on the UI thread. Background threads must post through
    /// this, never through <c>Window.DispatcherQueue</c>: the Window is a UI-thread COM object, so
    /// reading its property from a worker is a cross-apartment call into the UI thread - one COM
    /// can stall or reject depending on what the UI thread is doing at that instant. That call sat
    /// on both the startup continuation and every snapshot, and losing that race left the app
    /// stuck on "Starting telemetry" with no data. DispatcherQueue itself is agile.
    /// </summary>
    private readonly DispatcherQueue _uiQueue;
    private readonly IMetricStore _metricStore;
    private readonly IConfigStore _configStore;
    private readonly AutoLaunchService _autoLaunchService;
    private readonly Dictionary<string, TelemetryPanelCard> _panelCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PanelLayout> _displayPanelLayouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SummaryCardVisual> _summaryCardVisuals = [];
    private readonly StackPanel _overviewView;
    private readonly StackPanel _settingsView;
    private readonly ActionChip _overviewNavigationButton;
    private readonly ActionChip _settingsNavigationButton;

    private Grid _rootLayout;
    private readonly Grid _titleBarDragHost;
    private readonly ScrollViewer _scrollHost;
    private Border? _loadingOverlay;
    private readonly Border _controlsBodyHost;
    private readonly Grid _brandHost;
    private readonly Grid _summaryHost;
    private readonly Grid _dashboardGrid;
    private readonly ActionChip _editLayoutButton;
    private readonly ActionChip _cancelLayoutButton;
    private bool _isLayoutEditMode;
    private Dictionary<string, PanelLayout>? _layoutSnapshot;
    private FrameworkElement? _appHeader;
    private readonly TextBlock _statusText;
    private readonly Ellipse _statusIndicator;
    private readonly ActionChip _globalRangeButton;
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
    private Task? _pendingStartupPrune;
    private OverlayController? _overlayController;
    private nint _windowIconHandle;
    private bool _dashboardRefreshQueued;
    private bool _responsiveLayoutUpdateQueued;
    private bool _hasReceivedFirstSnapshot;
    private bool _initialized;
    private bool _launchIntroPlayed;
    private bool _settingsIntroPlayed;
    private int _lastDashboardColumnCount;
    private int _lastResponsiveMinimumWidth = -1;
    private int _lastSummaryColumnCount;
    private int _lastSettingsColumnCount;
    private bool _windowIconApplied;
    private bool _summaryCardsBound;
    private bool _globalRangeEditorVisible;
    private bool _settingsViewActive;
    private bool _settingsViewBuilt;
    private int _snapshotApplyQueued; // 0/1 via Interlocked: set by the collector thread, cleared by the UI thread
    private bool _panelRenderingSuspended;
    private string _lastRenderedStatusText = string.Empty;
    private string _draftScrapeIntervalInput = string.Empty;
    private string _draftRetentionInput = string.Empty;
    private string _draftStorageDirectory = string.Empty;
    private ThemeMode _draftThemeMode;
    private bool _draftOverlayEnabled;
    private SwissOlly.Core.Models.OverlayMode _draftOverlayMode = SwissOlly.Core.Models.OverlayMode.Advanced;
    private SwissOlly.Core.Models.OverlayCorner _draftOverlayCorner = SwissOlly.Core.Models.OverlayCorner.TopRight;
    private string _draftOverlayColor = "#66E7FF";
    private string _draftOverlayFontSize = "14";
    private string _draftOverlayOpacity = "90";
    private string _draftOverlayHotkey = "Ctrl+Shift+F11";
    private List<string> _draftOverlayCustomMetrics = ["fps", "cpu", "gpu", "gpu-temp", "memory"];
    private DateTimeOffset? _globalAbsoluteStartUtc;
    private DateTimeOffset? _globalAbsoluteEndUtc;
    private MetricSnapshot? _pendingSnapshot; // written by the collector thread, taken atomically by the UI thread
    public ShellWindow(
        MainViewModel viewModel,
        IMetricStore metricStore,
        IConfigStore configStore,
        AutoLaunchService autoLaunchService)
    {
        StartupTrace.Write("ShellWindow ctor start // polished-v19");
        _uiQueue = DispatcherQueue;
        _viewModel = viewModel;
        _metricStore = metricStore;
        _configStore = configStore;
        _autoLaunchService = autoLaunchService;

        _overviewView = new StackPanel { Spacing = SectionSpacing };
        _settingsView = new StackPanel { Spacing = SectionSpacing, Visibility = Visibility.Collapsed };
        _overviewNavigationButton = CreateActionChip("Overview", OnOverviewNavigationClick);
        _settingsNavigationButton = CreateActionChip("Settings", OnSettingsNavigationClick);
        _overviewNavigationButton.MinHeight = _settingsNavigationButton.MinHeight = 32;
        _overviewNavigationButton.IsActive = true;
        _overviewNavigationButton.MinWidth = _settingsNavigationButton.MinWidth = 88;

        Title = "SwissOlly";

        _statusText = CreateSecondaryText(string.Empty, 11.5);
        _statusText.MaxWidth = 300;
        _statusText.TextWrapping = TextWrapping.NoWrap;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        // The status column is fixed-width, so long messages (errors especially) are cut off;
        // the full text is one hover away. Only while trimmed, so short status has no tooltip.
        _statusText.IsTextTrimmedChanged += (sender, _) =>
            ToolTipService.SetToolTip(sender, sender.IsTextTrimmed ? sender.Text : null);
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
            ColumnSpacing = 0,
            RowSpacing = 0,
        };
        _dashboardGrid = new Grid
        {
            ColumnSpacing = PanelSpacing,
            RowSpacing = PanelSpacing,
        };
        _globalRangeButton = CreateActionChip("15m", OnToggleGlobalRangeEditor, true);
        _globalRangeButton.MinWidth = 88;
        ToolTipService.SetToolTip(_globalRangeButton, "Choose the time range for all charts (Ctrl+[ and Ctrl+] to step)");
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
        _editLayoutButton = CreateActionChip("Edit layout", OnToggleLayoutEditClick);
        ToolTipService.SetToolTip(_editLayoutButton, "Resize and rearrange panels (Ctrl+E)");
        _cancelLayoutButton = CreateActionChip("Cancel", OnCancelLayoutEditClick);
        _cancelLayoutButton.Visibility = Visibility.Collapsed;
        _globalResetZoomButton.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_globalResetZoomButton, "Return all charts to the live window (Esc)");
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
        InstallWindowBehavior();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DashboardPanels.CollectionChanged += OnDashboardPanelsChanged;
        _viewModel.PanelToggles.CollectionChanged += OnPanelTogglesChanged;

        try
        {
            TryApplyWindowIcon();
            TryLoadBrandImage();
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
        InstallLayoutKeyboardShortcuts();
    }

    public void ApplyInitialTheme(ThemeMode mode)
    {
        ApplyRequestedTheme(mode);
        RefreshWindowChrome();
    }

    public void ApplyTheme(ThemeMode mode)
    {
        ApplyRequestedTheme(mode);
        _draftThemeMode = mode;
        RefreshThemeVisuals();
        // The caption buttons follow on a later turn, keeping their cost out of the click: setting
        // the title bar colours took 12-20 ms per theme change in a Release build. They are drawn
        // outside XAML, so they never changed in step with the page anyway.
        _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshWindowChrome);
    }

    public void PreviewTheme(ThemeMode mode) => ApplyTheme(mode);

    private void ApplyRequestedTheme(ThemeMode mode)
    {
        var requestedTheme = IsLightTheme(mode) ? ElementTheme.Light : ElementTheme.Dark;
        if (_rootLayout.RequestedTheme != requestedTheme)
        {
            _rootLayout.RequestedTheme = requestedTheme;
        }
    }

    /// <summary>
    /// The rest of a theme change. App.ApplyThemeResources has already recoloured the palette
    /// brushes in place, and both pages draw only with those or with shared brushes worked out
    /// from them, so this recolours the worked-out ones and stops: no page is rebuilt and no card,
    /// chart or chip is visited, and the hidden page is as current as the visible one. Walking
    /// every card and rebuilding Settings was most of what a theme click cost.
    /// </summary>
    private void RefreshThemeVisuals()
    {
        // Series first: icon shades are worked out from the accents it recolours.
        _viewModel.RefreshSeriesColors();
        ThemeVisuals.RecolorDerivedBrushes();
    }

    // As tall as the label and number beside it, with a stroke that still reads at a glance.
    private const double SummaryRingDiameter = 44;
    private const double SummaryRingThickness = 5;

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
            var badgeHost = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = card.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleText = CreateMutedText(string.Empty, 11);
            titleText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Title)) });

            var valueText = CreatePrimaryText(string.Empty, 24, true);
            valueText.FontFamily = new FontFamily("Segoe UI Variable Display");
            // Tabular figures keep the live number from shifting width every sample.
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(valueText, FontNumeralAlignment.Tabular);
            valueText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Value)) });

            var captionText = CreateSecondaryText(string.Empty, 12);
            captionText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Caption)) });

            // A ring gauge beside the number, where a thin line used to run under it.
            // The ring only moves the end of an arc, so a new reading costs no layout pass.
            var meter = new RingMeter(
                SummaryRingDiameter,
                SummaryRingThickness,
                ResolveBrush("SurfaceStrokeBrush", "#27425E"),
                ResolveSummaryUtilizationBrush(card.Utilization, card.AccentBrush),
                card.Utilization);
            meter.Element.VerticalAlignment = VerticalAlignment.Center;

            card.PropertyChanged += (_, args) =>
            {
                if (_isWindowResizing) return; // skip during resize
                if (!string.Equals(args.PropertyName, nameof(SummaryCardViewModel.Utilization)))
                {
                    return;
                }

                var util = card.Utilization;
                meter.ValueBrush = ResolveSummaryUtilizationBrush(util, card.AccentBrush);
                meter.AnimateTo(util);
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    badgeHost,
                    titleText,
                },
            };

            var heading = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    titleRow,
                    valueText,
                },
            };
            var reading = new Grid { ColumnSpacing = 12 };
            reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            reading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            reading.Children.Add(meter.Element);
            reading.Children.Add(heading);
            Grid.SetColumn(heading, 1);

            // The caption keeps the cell's full width under the ring and number: beside the ring
            // it would wrap in a four-column strip on a narrow window.
            var details = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    reading,
                    captionText,
                },
            };

            var column = _summaryHost.Children.Count % summaryColumns;
            var row = _summaryHost.Children.Count / summaryColumns;
            var summaryCard = new Border
            {
                DataContext = card,
                Background = BrushFactory.CreateBrush("#00FFFFFF"),
                BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
                BorderThickness = new Thickness(column > 0 ? 1 : 0, row > 0 ? 1 : 0, 0, 0),
                Padding = new Thickness(14, 12, 14, 12),
                MinHeight = 84,
                Child = details,
            };
            _summaryHost.Children.Add(summaryCard);
            _summaryCardVisuals.Add(new SummaryCardVisual(summaryCard, titleText, valueText, captionText, badgeHost, meter, card));
            var index = _summaryHost.Children.Count - 1;
            while (_summaryHost.RowDefinitions.Count <= index / summaryColumns)
            {
                _summaryHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetColumn(summaryCard, column);
            Grid.SetRow(summaryCard, row);
        }

        _summaryCardsBound = true;
    }

    /// <summary>
    /// Moves the built summary cards into the column count for the current width. Rebuilding
    /// would subscribe each card's meter a second time, and BuildSummaryCards refuses once bound,
    /// so the strip used to keep its launch-time column count: four squeezed columns after
    /// shrinking the window, or one stretched column after maximizing a narrow one.
    /// </summary>
    private void ReflowSummaryCards()
    {
        var summaryColumns = DetermineSummaryColumns();
        if (!_summaryCardsBound || summaryColumns == _lastSummaryColumnCount)
        {
            return;
        }

        _lastSummaryColumnCount = summaryColumns;
        _summaryHost.ColumnDefinitions.Clear();
        _summaryHost.RowDefinitions.Clear();
        for (var column = 0; column < summaryColumns; column++)
        {
            _summaryHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < _summaryCardVisuals.Count; index++)
        {
            var surface = _summaryCardVisuals[index].Surface;
            var column = index % summaryColumns;
            var row = index / summaryColumns;
            while (_summaryHost.RowDefinitions.Count <= row)
            {
                _summaryHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            surface.BorderThickness = new Thickness(column > 0 ? 1 : 0, row > 0 ? 1 : 0, 0, 0);
            Grid.SetColumn(surface, column);
            Grid.SetRow(surface, row);
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

    private void RefreshDashboardPanels()
    {
        StartupTrace.Write("RefreshDashboardPanels // polished-v19");
        var panels = _viewModel.DashboardPanels.ToArray();
        var activeKeys = new HashSet<string>(panels.Select(panel => panel.PanelKey), StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _panelCards.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            if (_panelCards.TryGetValue(staleKey, out var staleCard))
            {
                staleCard.PanelZoomSelectionRequested -= OnPanelZoomSelectionRequested;
                staleCard.PanelZoomResetRequested -= OnPanelZoomResetRequested;
                staleCard.LayoutGestureStarted -= OnCardLayoutGestureStarted;
                staleCard.LayoutGestureMoved -= OnCardLayoutGestureMoved;
                staleCard.LayoutGestureEnded -= OnCardLayoutGestureEnded;
                staleCard.LayoutNudgeRequested -= OnCardLayoutNudgeRequested;
            }

            _panelCards.Remove(staleKey);
            _reflowSprings.Remove(staleKey);
        }

        List<TelemetryPanelCard>? arrivals = null;
        foreach (var panel in panels)
        {
            if (!_panelCards.TryGetValue(panel.PanelKey, out var card))
            {
                card = new TelemetryPanelCard { Panel = panel };
                card.PanelZoomSelectionRequested += OnPanelZoomSelectionRequested;
                card.PanelZoomResetRequested += OnPanelZoomResetRequested;
                card.LayoutGestureStarted += OnCardLayoutGestureStarted;
                card.LayoutGestureMoved += OnCardLayoutGestureMoved;
                card.LayoutGestureEnded += OnCardLayoutGestureEnded;
                card.LayoutNudgeRequested += OnCardLayoutNudgeRequested;
                card.SetLayoutEditMode(_isLayoutEditMode);
                _panelCards.Add(panel.PanelKey, card);
                (arrivals ??= []).Add(card);
            }
            else
            {
                card.Panel = panel;
            }

            card.SetRenderingSuspended(_panelRenderingSuspended);
        }

        CancelActiveLayoutGesture();
        CompleteFloatSettle();
        foreach (var (key, springs) in _reflowSprings)
        {
            springs.X.Snap(0);
            springs.Y.Snap(0);
            if (_panelCards.TryGetValue(key, out var resetCard))
            {
                resetCard.SetReflowOffset(0, 0);
            }
        }

        _dashboardGrid.Children.Clear();
        _dashboardGrid.RowDefinitions.Clear();
        _dashboardGrid.ColumnDefinitions.Clear();

        if (panels.Length == 0)
        {
            _dashboardGrid.Children.Add(CreatePlaceholderCard("Live board", "Telemetry wakes up after the first local sample arrives."));
            return;
        }

        // The board is always 12 columns wide. Responsive behaviour is applied per panel by
        // widening narrow panels at small windows, not by changing the column count — the saved
        // geometry has to mean the same thing at every width or a layout cannot be "locked in".
        _lastDashboardColumnCount = DetermineDashboardColumns();
        for (var column = 0; column < PanelLayout.Columns; column++)
        {
            _dashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var panel in panels)
        {
            _dashboardGrid.Children.Add(_panelCards[panel.PanelKey]);
        }

        PlaceDashboardPanels(panels, PanelLayout.Columns, resetRows: true);
        if (_dropPlaceholder is not null)
        {
            EnsureDropPlaceholder();
        }

        // Panels reaching the board in the first moments (from history, or the first sample)
        // join the launch entrance as their own small wave; later arrivals just appear.
        if (arrivals is not null && IsLaunchEntranceOpen)
        {
            PlayCardEntrances(arrivals, 0);
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

        return columns;
    }

    private int DetermineSettingsColumns()
    {
        var width = _rootLayout.ActualWidth > 0 ? _rootLayout.ActualWidth : 1280;
        return width >= 1180 ? 3 : width >= 820 ? 2 : 1;
    }

    private void RefreshSettingsLayoutIfNeeded()
    {
        if (!_settingsViewActive)
        {
            return;
        }

        var columns = DetermineSettingsColumns();
        if (columns == _lastSettingsColumnCount)
        {
            return;
        }

        _lastSettingsColumnCount = columns;
        RenderEditableControlDeck();
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
            if (DetermineDashboardColumns() != _lastDashboardColumnCount)
            {
                RefreshDashboardPanels();
            }
            ReflowSummaryCards();
            UpdateHeaderStatusWidth();
            ApplyMinimumWindowSize();

            var config = _viewModel.BuildConfig();
            StartupTrace.Write("OnRootLoaded resumed after first paint // polished-v19");

            // The Run key decides, not the saved setting: the installer may have created it and
            // Task Manager may have disabled it. Writing the setting back used to delete the
            // installer's entry on every launch.
            _autoLaunchService.RefreshIfStale();
            _viewModel.LaunchOnStartup = _autoLaunchService.IsEnabled();
            if (Program.StartedInTray && _viewModel.MinimizeToTray)
            {
                _ = _uiQueue.TryEnqueue(() => HideToTray(showNotice: false));
            }

            App.CurrentApp.MarkStartupSettled();
            _ = StartTelemetryAsync(config);
            StartOverlay(config);
            _ = _uiQueue.TryEnqueue(() =>
            {
                TryApplyWindowIcon();
            });

            // In this turn, before the board's first frame, so nothing is drawn whole and then
            // hidden for its entrance (a Low-priority turn, as it was, can come after a frame).
            // Last in the handler, so the work above is done before the animations start.
            PlayLaunchEntrance();
            StartupTrace.Write("OnRootLoaded scheduled background startup // polished-v19");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("OnRootLoaded", ex);
            _viewModel.SetStatus($"Startup issue: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
            App.CurrentApp.MarkStartupSettled();
        }
    }

    /// <summary>How long after the first frame a panel reaching the board still joins the entrance.</summary>
    private const int LaunchEntranceOpenMs = 1500;
    private const int LaunchSectionStaggerMs = 20;
    private const int LaunchCardStaggerMs = 14;
    private const int LaunchCardStaggerSteps = 5;

    /// <summary>When the launch entrance stops taking arrivals (Environment.TickCount64); 0 when it's over.</summary>
    private long _launchEntranceOpenUntil;

    private bool IsLaunchEntranceOpen => _launchEntranceOpenUntil != 0 && Environment.TickCount64 < _launchEntranceOpenUntil;

    /// <summary>
    /// The launch entrance, once per launch: the header, the summary strip and the panels fade
    /// and rise a few pixels into place with a slight stagger, and charts draw their history in
    /// from the left. It runs on the compositor (Motion.PlayEntrance and PlayRevealFromLeft) and
    /// nothing waits for it, so it holds up neither the window nor the first sample.
    ///
    /// The board fills in waves: a baseline panel now, most of the rest when history loads (about
    /// half a second later), a few more with the first live sample. So for a short while, panels
    /// that arrive join in (RefreshDashboardPanels); after that they just appear. Skipped with
    /// Windows' animation effects off, and for a --tray launch, whose window is hidden: nobody
    /// would see it, and coming back from the tray never replays it.
    /// </summary>
    private void PlayLaunchEntrance()
    {
        if (_launchIntroPlayed)
        {
            return;
        }

        _launchIntroPlayed = true;
        if (!Motion.AnimationsEnabled || (Program.StartedInTray && _viewModel.MinimizeToTray))
        {
            return;
        }

        _launchEntranceOpenUntil = Environment.TickCount64 + LaunchEntranceOpenMs;
        if (_appHeader is not null)
        {
            Motion.PlayEntrance(_appHeader);
        }

        // The summary strip and the board's title band, a beat apart, then the panels.
        var delay = 0;
        foreach (var child in _overviewView.Children)
        {
            if (!ReferenceEquals(child, _dashboardGrid))
            {
                delay += LaunchSectionStaggerMs;
                Motion.PlayEntrance(child, TimeSpan.FromMilliseconds(delay));
            }
        }

        PlayCardEntrances(_panelCards.Values, delay + LaunchSectionStaggerMs);
    }

    /// <summary>
    /// Cards rise in reading order, top row first, each arming its chart to draw in. The stagger
    /// is capped so a whole board still settles in about 300 ms instead of trailing down the page.
    /// </summary>
    private void PlayCardEntrances(IEnumerable<TelemetryPanelCard> cards, int firstDelayMs)
    {
        var rank = 0;
        foreach (var card in cards.OrderBy(Grid.GetRow).ThenBy(Grid.GetColumn))
        {
            var delay = TimeSpan.FromMilliseconds(firstDelayMs + (Math.Min(rank++, LaunchCardStaggerSteps) * LaunchCardStaggerMs));
            Motion.PlayEntrance(card, delay);
            card.ArmLaunchDrawIn(delay, _launchEntranceOpenUntil);
        }
    }

    private void PlaySettingsEntrance()
    {
        if (!Motion.AnimationsEnabled)
        {
            return;
        }

        for (var index = 0; index < _settingsView.Children.Count; index++)
        {
            Motion.PlayEntrance(_settingsView.Children[index], TimeSpan.FromMilliseconds(index * 45));
        }
    }

    private async Task StartTelemetryAsync(SwissOllyConfig config)
    {
        try
        {
            _viewModel.SetStatus("Starting telemetry", StatusSeverity.Pending);
            UpdateStatusText();

            // Open the store first, on a worker. Initialization is idempotent, so the collector's
            // own StartAsync reusing it later costs nothing.
            var store = _metricStore;
            var storeReady = true;
            try
            {
                await Task.Run(() => store.InitializeAsync(config, CancellationToken.None));
                StartupTrace.Write("Metric store ready");
            }
            catch (Exception storeEx)
            {
                // Storage folder unreachable (an unplugged drive, no permission). The live board
                // still runs; the collector reports the history problem and keeps retrying.
                storeReady = false;
                StartupTrace.WriteException("Metric store", storeEx);
            }

            if (storeReady)
            {
                // Fill the board from history straight away, in parallel with the collector's PDH
                // setup. It used to wait behind that setup, so panels appeared ~0.5s later than needed.
                _viewModel.SetHistoryLoading(true);
                _pendingHistoryLoad = Task.Run(() => TryLoadHistoryAsync(config));
            }

            await EnsureCollectorRunningAsync(config);
            if (!storeReady)
            {
                return;
            }

            // Retention catches up in the background; pruning is transactional.
            _pendingStartupPrune = Task.Run(async () =>
            {
                try
                {
                    await _metricStore.PruneAsync(config, CancellationToken.None).ConfigureAwait(false);
                    StartupTrace.Write("Startup prune complete");
                }
                catch (Exception pruneEx)
                {
                    StartupTrace.WriteException("Startup prune", pruneEx);
                }
            });
            StartupTrace.Write("Background startup complete // polished-v21");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("StartTelemetryAsync", ex);
            _ = _uiQueue.TryEnqueue(() =>
            {
                _viewModel.SetStatus($"Startup issue: {ex.Message}", StatusSeverity.Issue);
                UpdateStatusText();
            });
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var config = _viewModel.BuildConfig();
            Task.Run(async () => await _configStore.SaveAsync(config, CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("Save config on exit", ex);
        }

        _overlayController?.Dispose();
        _overlayController = null;
        RemoveTrayIcon();

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

        // Wait briefly for startup retention work too; the store also serializes disposal
        // behind any operation that is already using its write connection.
        if (_pendingStartupPrune is not null)
        {
            try
            {
                _pendingStartupPrune.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort cleanup - the store coordinates any active write before closing.
            }
        }

        // Wait for any in-flight history load to complete so its SQLite
        // read connection is closed before the process exits
        if (_pendingHistoryLoad is not null)
        {
            try
            {
                // Bounded. TryLoadHistoryAsync awaits without ConfigureAwait(false), so its
                // continuation is posted back to this DispatcherQueue - the one this thread is
                // about to block. An unbounded wait here deadlocks the process on close while a
                // long-retention history load is in flight: no window, no exit.
                _pendingHistoryLoad.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort - history load may have failed
            }

            _pendingHistoryLoad = null;
        }

        if (_collectorService is not null)
        {
            var collectorService = _collectorService;
            _collectorService = null;
            collectorService.SnapshotCollected -= OnSnapshotCollected;
            collectorService.CollectionFailed -= OnCollectionFailed;
            collectorService.HistoryWriteStateChanged -= OnHistoryWriteStateChanged;
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

        // Everything is flushed: config saved, collector stopped, store checkpointed. End the
        // process explicitly. Left to WinUI, the dispatcher's shutdown drain was observed spinning a
        // full CPU core indefinitely after the window closed - an invisible zombie, on a machine
        // that is usually running a game.
        StartupTrace.Write("Window closed; exiting");
        Environment.Exit(0);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_fullUiBuilt)
        {
            return;
        }

        // Every colour again on activation, as before; only a theme change skips unchanged ones.
        _appliedTitleBarColors = null;
        RefreshWindowChrome();

        if (_windowIconApplied)
        {
            return;
        }

        _ = _uiQueue.TryEnqueue(TryApplyWindowIcon);
    }


    private async void OnSaveSettingsClick(object? sender, EventArgs e)
    {
        try
        {
            var previousConfig = _viewModel.BuildConfig();
            if (!TryNormalizeScrapeInput(_draftScrapeIntervalInput, out var normalizedScrapeInput))
            {
                RejectSettings("Scrape interval must be a whole number of seconds between 1 and 60.");
                return;
            }

            if (!MainViewModel.TryParseRetentionInput(_draftRetentionInput, out _, out var normalizedRetentionInput))
            {
                RejectSettings("Retention must be a number with m, h or d, up to 3650d. Try 30m, 24h or 7d.");
                return;
            }

            // An unusable folder used to be swapped for the default without a word.
            if (!string.IsNullOrWhiteSpace(_draftStorageDirectory) &&
                !SwissOllyConfig.TryNormalizeStorageDirectory(_draftStorageDirectory, out _))
            {
                RejectSettings(@"Storage needs a full path to a folder on a local drive, like D:\SwissOlly.");
                return;
            }

            // The overlay's own Normalize swaps anything out of range for a default, which would
            // apply something other than what was typed without a word.
            if (GetOverlayDraftProblem() is { } overlayProblem)
            {
                RejectSettings(overlayProblem);
                return;
            }

            _settingsProblem = null;

            // Lowering retention deletes history as soon as it applies. Say what will go and
            // wait for a yes; the confirmation calls back in here.
            var currentRetention = previousConfig.GetRetentionWindow();
            var draftRetention = SwissOllyConfig.TryParseRetentionWindow(normalizedRetentionInput, out var parsedRetention, out _)
                ? parsedRetention
                : TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours);
            if (draftRetention < currentRetention && !_retentionDeletionConfirmed)
            {
                ShowRetentionConfirmation(currentRetention, draftRetention);
                return;
            }

            _retentionDeletionConfirmed = false;

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

            _viewModel.SetStatus(collectorRestartRequired
                ? "Applying telemetry settings"
                : retentionLowered
                    ? "Saving settings and pruning older history"
                    : themeChanged
                        ? "Applying theme"
                        : "Saving settings", StatusSeverity.Healthy);
            UpdateStatusText();
            _viewModel.ApplyConfig(config);
            SyncControlDeckDraftsFromViewModel();
            // Start with Windows is not touched here: it applies on its own click. Re-writing it
            // from the flag read at launch re-enabled a startup entry the user had since turned
            // off in Task Manager.
            await _configStore.SaveAsync(config, CancellationToken.None);
            if (themeChanged)
            {
                App.CurrentApp.ApplyTheme(config.Theme);
            }
            _settingsSavedAt = DateTimeOffset.Now;
            RefreshSettingsDeck();
            _ = ApplyRuntimeSettingsAsync(config, collectorRestartRequired, storageChanged, retentionChanged, retentionLowered);
        }
        catch (Exception ex)
        {
            _retentionDeletionConfirmed = false;
            _viewModel.SetStatus($"Settings issue: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
        }
    }

    /// <summary>Keeps the draft and says why it was not applied, beside Apply and in the header.</summary>
    private void RejectSettings(string problem)
    {
        _settingsProblem = problem;
        _viewModel.SetStatus(problem, StatusSeverity.Issue);
        UpdateStatusText();
        RefreshSettingsDeck();
    }

    /// <summary>
    /// A whole number from a field, allowing the unit its chips are labelled with ("90%",
    /// "14px"), which used to be rejected as typed.
    /// </summary>
    private static bool TryParseDraftNumber(string? text, string unit, out int value)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^unit.Length].TrimEnd();
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Why the overlay drafts can't be applied as typed, or null when they can.</summary>
    private string? GetOverlayDraftProblem()
    {
        if (!TryParseDraftNumber(_draftOverlayFontSize, "px", out var fontSize) || fontSize is < 10 or > 32)
        {
            return "Overlay text size must be a whole number from 10 to 32.";
        }

        if (!TryParseDraftNumber(_draftOverlayOpacity, "%", out var opacity) || opacity is < 30 or > 100)
        {
            return "Overlay opacity must be a whole number from 30 to 100.";
        }

        if (SwissOlly.Core.Models.OverlayConfig.NormalizeHexColor(_draftOverlayColor) is null)
        {
            return "Overlay color must be a hex value like #66E7FF.";
        }

        if (_draftOverlayMode == SwissOlly.Core.Models.OverlayMode.Custom && _draftOverlayCustomMetrics.Count == 0)
        {
            return "Pick at least one metric for the custom overlay.";
        }

        return SwissOlly.Core.Models.OverlayConfig.DescribeHotkeyProblem(_draftOverlayHotkey);
    }

    private bool _retentionDeletionConfirmed;
    private Flyout? _retentionConfirmFlyout;

    private void ShowRetentionConfirmation(TimeSpan current, TimeSpan next)
    {
        if (_settingsApplyChip is null)
        {
            return;
        }

        var cancel = CreateActionChip("Cancel", (_, _) => _retentionConfirmFlyout?.Hide());
        var confirm = CreateActionChip("Delete and apply", (_, _) =>
        {
            _retentionConfirmFlyout?.Hide();
            _retentionDeletionConfirmed = true;
            OnSaveSettingsClick(this, EventArgs.Empty);
        }, filled: true);

        var body = CreateSecondaryText(
            $"Retention goes from {FormatDuration(current)} to {FormatDuration(next)}. History older than {FormatDuration(next)} is deleted as soon as you apply, and can't be recovered.",
            12.5);
        body.TextWrapping = TextWrapping.WrapWholeWords;

        _retentionConfirmFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            FlyoutPresenterStyle = CreateFlyoutPresenterStyle(),
            Content = new StackPanel
            {
                Width = 340,
                Spacing = 12,
                Children =
                {
                    CreatePrimaryText("Delete older history?", 14, true),
                    body,
                    CreateChipRow(cancel, confirm),
                },
            },
        };
        _retentionConfirmFlyout.ShowAt(_settingsApplyChip);
        // Keyboard users land on the safe choice.
        _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () => cancel.Focus(FocusState.Programmatic));
    }

    /// <summary>A flyout surface in the app's palette rather than the system's.</summary>
    private static Style CreateFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("SurfaceElevatedBrush", "#222529")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ResolveBrush("SurfaceStrokeBrush", "#2C3035")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(UiMetrics.Hairline)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(UiMetrics.OverlayRadius)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        return style;
    }

    /// <summary>"30 minutes", "24 hours", "7 days". Days from two up, so 24h reads as the chip does.</summary>
    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 2 && span.TotalDays == Math.Floor(span.TotalDays))
        {
            var days = (int)span.TotalDays;
            return days == 1 ? "1 day" : $"{days:N0} days";
        }

        if (span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours))
        {
            var hours = (int)span.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
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
            StartupTrace.Write($"Global time range selected // minutes={minutes}");
            RunWithDeferredPanelRendering(() => ApplyGlobalWindowRange(minutes));
        }
    }

    private void OnPanelZoomSelectionRequested(object? sender, ChartZoomSelectionEventArgs e)
    {
        ApplyAbsoluteGlobalRange(e.StartUtc, e.EndUtc);
    }

    private void OnPanelZoomResetRequested(object? sender, EventArgs e)
    {
        OnResetAllZoomClick(sender, e);
    }

    private void ReorderDashboardPanelsInPlace()
    {
        var panels = _viewModel.DashboardPanels.ToArray();
        // The grid is always PanelLayout.Columns wide now. Comparing against the responsive
        // breakpoint count (1-3) meant this never matched, so every reorder queued a full
        // rebuild — which re-made the 12-column grid and queued another.
        if (_dashboardGrid.ColumnDefinitions.Count != PanelLayout.Columns)
        {
            QueueDashboardRefresh();
            return;
        }

        PlaceDashboardPanels(panels, PanelLayout.Columns);
    }

    private void PlaceDashboardPanels(IReadOnlyList<MetricPanelViewModel> panels, int columns, bool resetRows = false)
    {
        if (resetRows)
        {
            _dashboardGrid.RowDefinitions.Clear();
        }

        _viewModel.EnsureLayoutsFor(panels.Select(p => p.PanelKey).ToList());

        var minWidth = ResponsiveMinimumWidth();
        var savedPlacements = panels
            .Select(panel => _viewModel.GetPanelLayout(panel.PanelKey) is { } layout
                ? new PanelPlacement(panel.PanelKey, layout)
                : null)
            .Where(placement => placement is not null)
            .Select(placement => placement!)
            .ToList();
        var displayPlacements = GridLayoutEngine.ResolveResponsive(savedPlacements, minWidth);
        _displayPanelLayouts.Clear();
        foreach (var placement in displayPlacements)
        {
            _displayPanelLayouts[placement.Key] = placement.Layout;
        }
        _lastResponsiveMinimumWidth = minWidth;

        var maxRow = displayPlacements.Count == 0 ? 0 : displayPlacements.Max(placement => placement.Layout.Bottom);
        foreach (var panel in panels)
        {
            if (!_displayPanelLayouts.TryGetValue(panel.PanelKey, out var layout))
            {
                continue;
            }

            if (_panelCards.TryGetValue(panel.PanelKey, out var card) && !card.IsFloating)
            {
                Grid.SetColumn(card, layout.X);
                Grid.SetRow(card, layout.Y);
                Grid.SetColumnSpan(card, layout.W);
                Grid.SetRowSpan(card, layout.H);
            }
        }

        while (_dashboardGrid.RowDefinitions.Count < maxRow)
        {
            _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DashboardRowUnitPx) });
        }
        // Rows a floating panel still occupies are trimmed when it docks, not here, or the
        // board would shrink out from under a panel that is still settling.
        while (!IsLayoutMotionActive && _dashboardGrid.RowDefinitions.Count > maxRow)
        {
            _dashboardGrid.RowDefinitions.RemoveAt(_dashboardGrid.RowDefinitions.Count - 1);
        }

        SpanBoardOverlays();
    }

    /// <summary>RowDefinition height; its grid pitch also includes the configured row spacing.</summary>
    private const double DashboardRowUnitPx = 48;

    /// <summary>
    /// Smallest width a panel may display at, by window size. At the narrowest breakpoint every
    /// panel goes full width, which is the only readable option on a small window.
    /// </summary>
    private int ResponsiveMinimumWidth()
    {
        // Measured width, not the legacy column count: that count is 0 on the very first
        // placement and would fall through to the narrow case, forcing every panel full width.
        var width = _rootLayout.ActualWidth > 0 ? _rootLayout.ActualWidth : 1280;
        return width switch
        {
            >= 1180 => PanelLayout.MinWidth,
            >= 820 => 6,
            _ => PanelLayout.Columns,
        };
    }

    private bool _persistLayoutQueued;

    private void DebouncePersistLayout()
    {
        if (_persistLayoutQueued)
        {
            return;
        }

        _persistLayoutQueued = true;
        if (!_uiQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _persistLayoutQueued = false;
            _ = PersistLayoutAsync();
        }))
        {
            _persistLayoutQueued = false;
            _ = PersistLayoutAsync();
        }
    }

    private void OnResetAllZoomClick(object? sender, EventArgs e)
    {
        RunWithDeferredPanelRendering(() =>
        {
            _globalAbsoluteStartUtc = null;
            _globalAbsoluteEndUtc = null;
            _viewModel.ResetGlobalZoom();
            _viewModel.ReleaseStatusHold();
        });

        HideGlobalRangeEditor();
        RefreshGlobalRangeControls();
    }

    private void OnSnapshotCollected(object? sender, MetricSnapshot snapshot)
    {
        // Runs on the collector's thread. Nothing here may throw: an exception used to escape
        // into the collector loop and stop sampling for the rest of the session.
        try
        {
            _overlayController?.OnSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("Overlay snapshot", ex);
        }

        Volatile.Write(ref _pendingSnapshot, snapshot);
        QueueSnapshotApply();
    }

    /// <summary>Posts one apply pass unless one is already queued. Safe from any thread.</summary>
    private void QueueSnapshotApply()
    {
        if (Interlocked.CompareExchange(ref _snapshotApplyQueued, 1, 0) != 0)
        {
            return;
        }

        if (!_uiQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyQueuedSnapshot))
        {
            Volatile.Write(ref _snapshotApplyQueued, 0);
        }
    }

    private void SubscribeCollector(CollectorService collectorService)
    {
        collectorService.SnapshotCollected -= OnSnapshotCollected;
        collectorService.CollectionFailed -= OnCollectionFailed;
        collectorService.HistoryWriteStateChanged -= OnHistoryWriteStateChanged;
        collectorService.SnapshotCollected += OnSnapshotCollected;
        collectorService.CollectionFailed += OnCollectionFailed;
        collectorService.HistoryWriteStateChanged += OnHistoryWriteStateChanged;
    }

    private void OnHistoryWriteStateChanged(object? sender, Exception? error)
    {
        if (error is not null)
        {
            StartupTrace.WriteException("History write", error);
        }
        else
        {
            StartupTrace.Write("History writes recovered");
        }

        var issue = error is null ? null : $"History isn't being saved: {DescribeStorageError(error)}. The live view keeps updating.";
        _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _viewModel.SetHistoryWriteIssue(issue);
            UpdateStatusText();
        });
    }

    /// <summary>A storage failure in words a user can act on, rather than a SQLite error code.</summary>
    internal static string DescribeStorageError(Exception error) => error switch
    {
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 13 } => "the disk is full",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } => "the database is locked by another program",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } or UnauthorizedAccessException => "SwissOlly can't write to the storage folder",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 10 or 14 } or DirectoryNotFoundException or DriveNotFoundException => "the storage folder can't be reached",
        IOException => "the storage folder can't be reached",
        _ => error.Message.TrimEnd('.'),
    };

    private void OnCollectionFailed(object? sender, Exception ex)
    {
        _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            StartupTrace.WriteException("Collection failed", ex);
            if (!_hasReceivedFirstSnapshot)
            {
                _viewModel.SetStatus($"Telemetry retrying: {ex.Message}", StatusSeverity.Issue);
                UpdateStatusText();
            }
        });
    }

    private void ApplyQueuedSnapshot()
    {
        Volatile.Write(ref _snapshotApplyQueued, 0);

        // While the window is being resized or a panel is being arranged, keep the snapshot
        // pending. Applying one re-renders every chart - a visible hitch mid-gesture. The old
        // version re-queued itself here at Low priority, which spun the dispatcher for the
        // whole resize; now it parks and ResumeDeferredSnapshot picks it up afterwards.
        if (_isWindowResizing || IsLayoutMotionActive)
        {
            _snapshotDeferred = true;
            return;
        }

        // Taken atomically: a non-atomic read-then-clear could discard a newer snapshot the
        // collector stored in between.
        var snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snapshot is null)
        {
            return;
        }

        if (!_hasReceivedFirstSnapshot)
        {
            _hasReceivedFirstSnapshot = true;
            StartupTrace.Write($"First snapshot collected // samples={snapshot.Samples.Count}");
            PrebuildSettingsWhenIdle();
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

        if (Volatile.Read(ref _pendingSnapshot) is not null)
        {
            QueueSnapshotApply();
        }
    }

    private bool _snapshotDeferred;

    private void ResumeDeferredSnapshot()
    {
        if (!_snapshotDeferred || _isWindowResizing || IsLayoutMotionActive)
        {
            return;
        }

        _snapshotDeferred = false;
        if (Volatile.Read(ref _pendingSnapshot) is not null)
        {
            QueueSnapshotApply();
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

        // Stop catching input the moment the fade starts. Removal waits for the storyboard's
        // Completed event on the UI thread; if that was ever late, a fully transparent overlay
        // sat over the window swallowing every click - the app looked ready but was dead.
        overlay.IsHitTestVisible = false;

        if (!Motion.AnimationsEnabled)
        {
            RemoveLoadingOverlay(overlay);
            return;
        }

        // Quick: the launch entrance plays underneath (PlayLaunchEntrance), and the 300 ms fade
        // this used to be covered most of it. The overlay is the backdrop colour, so to the eye
        // only the "Starting SwissOlly" text and dots fade.
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(MotionTokens.Quick)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeOut);
        Storyboard.SetTarget(fadeOut, overlay);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        storyboard.Completed += (_, _) => RemoveLoadingOverlay(overlay);
        storyboard.Begin();
    }

    /// <summary>
    /// Takes the loading overlay off the window. Collapsed first, so it is gone even if it can't
    /// be taken out of its panel: it sits over the header row, and with Windows' animation
    /// effects off it used to stay there for good, hiding the logo and the Overview/Settings
    /// switch (seen in startup frames, 2026-11-09).
    /// </summary>
    private void RemoveLoadingOverlay(Border overlay)
    {
        overlay.Visibility = Visibility.Collapsed;
        if (overlay.Parent is Panel parent)
        {
            parent.Children.Remove(overlay);
        }
        else
        {
            _rootLayout?.Children.Remove(overlay);
        }
    }

    /// <summary>
    /// Set by a Settings choice that applies on click (theme, Start with Windows, closing): the next
    /// save says "Saved" in the floating bar when it lands, or reports why it didn't.
    /// </summary>
    private bool _announceSettingsSave;

    private async Task PersistLayoutAsync()
    {
        var announce = _announceSettingsSave;
        _announceSettingsSave = false;
        try
        {
            await _configStore.SaveAsync(_viewModel.BuildConfig(), CancellationToken.None);
            if (announce)
            {
                _settingsSavedAt = DateTimeOffset.Now;
                UpdateSettingsActionState();
            }
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("PersistLayout", ex);
            if (announce)
            {
                _viewModel.SetStatus($"Couldn't save settings: {ex.Message}", StatusSeverity.Issue);
                UpdateStatusText();
            }
        }
    }

    private void QueueDashboardRefresh()
    {
        if (_dashboardRefreshQueued)
        {
            return;
        }

        _dashboardRefreshQueued = true;
        _ = _uiQueue.TryEnqueue(() =>
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

        _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => SetPanelRenderingSuspended(false));
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
        var shouldSuspend = suspended || _settingsViewActive || _isHiddenToTray;
        if (_panelRenderingSuspended == shouldSuspend)
        {
            return;
        }

        _panelRenderingSuspended = shouldSuspend;
        foreach (var card in _panelCards.Values)
        {
            card.SetRenderingSuspended(shouldSuspend);
        }
    }

    private void OnOverviewNavigationClick(object? sender, EventArgs e) => SetActiveShellView(settings: false);

    private void OnSettingsNavigationClick(object? sender, EventArgs e) => SetActiveShellView(settings: true);

    private DispatcherQueueTimer? _settingsPrebuildTimer;

    /// <summary>
    /// Builds the (hidden) Settings page a few seconds after the board is live, while nothing is
    /// happening, so the first click on Settings doesn't pay ~70-150ms to build it.
    /// </summary>
    private void PrebuildSettingsWhenIdle()
    {
        if (_settingsViewBuilt || _settingsPrebuildTimer is not null)
        {
            return;
        }

        _settingsPrebuildTimer = _uiQueue.CreateTimer();
        _settingsPrebuildTimer.IsRepeating = false;
        _settingsPrebuildTimer.Interval = TimeSpan.FromSeconds(3);
        _settingsPrebuildTimer.Tick += (_, _) =>
            _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_settingsViewBuilt && !_isLayoutEditMode)
                {
                    EnsureSettingsViewBuilt();
                }
            });
        _settingsPrebuildTimer.Start();
    }

    private void EnsureSettingsViewBuilt()
    {
        if (_settingsViewBuilt)
        {
            return;
        }

        _settingsView.Children.Add(CreatePrimaryText("Settings", 28, true));
        _settingsView.Children.Add(BuildControlsSurface());
        // Room to scroll the last card clear of the floating Apply bar.
        _settingsView.Children.Add(new Border { Height = 56 });
        SyncControlDeckDraftsFromViewModel();
        _settingsViewBuilt = true;
        RenderEditableControlDeck();
    }

    private void SetActiveShellView(bool settings)
    {
        if (settings)
        {
            // Leaving the board ends arranging it; the edits were deliberate, so they are kept.
            if (_isLayoutEditMode)
            {
                SetLayoutEditMode(false, commit: true);
            }

            EnsureSettingsViewBuilt();
        }

        if (_settingsViewActive == settings)
        {
            return;
        }

        // A click on Settings closes the board's open lists by itself (they are light-dismiss), but
        // Ctrl+2 and a screen reader's Invoke don't click: the process list stayed open over
        // Settings, still updating.
        if (settings)
        {
            if (_globalRangeEditorVisible)
            {
                HideGlobalRangeEditor();
            }

            foreach (var card in _panelCards.Values)
            {
                card.CloseProcessList();
            }
        }

        // Each view keeps its own scroll position: coming back to the board should land where
        // you left it, not at the top (HIG: preserve context across navigation).
        if (_settingsViewActive)
        {
            _settingsScrollOffset = _scrollHost.VerticalOffset;
        }
        else
        {
            _overviewScrollOffset = _scrollHost.VerticalOffset;
        }

        _settingsViewActive = settings;
        _overviewView.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        _settingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        _overviewNavigationButton.IsActive = !settings;
        _settingsNavigationButton.IsActive = settings;
        // ChangeView(horizontalOffset, verticalOffset, zoomFactor, disableAnimation). The
        // offset goes in the SECOND slot: a 0 in the third set zoomFactor to 0 (clamped to 0.1),
        // which once rendered a whole view at a tenth of its size.
        var restoreOffset = settings ? _settingsScrollOffset : _overviewScrollOffset;
        _scrollHost.UpdateLayout();
        _scrollHost.ChangeView(null, restoreOffset, null, true);
        RefreshSettingsLayoutIfNeeded();
        if (settings && !_settingsIntroPlayed)
        {
            _settingsIntroPlayed = true;
            _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, PlaySettingsEntrance);
        }
        else
        {
            // Overview sits left of Settings in the navigation, so each view arrives from its side.
            Motion.PlayDirectionalEntrance(settings ? _settingsView : _overviewView, settings ? 1 : -1);
        }
        SetPanelRenderingSuspended(false);
        // The Apply bar belongs to Settings: it leaves with it, and returns if edits are waiting.
        UpdateSettingsActionState();
    }

    private double _overviewScrollOffset;
    private double _settingsScrollOffset;
    private bool _isWindowResizing;
    private int _resizeSafetyGeneration;
    private Border? _shellBorderRef;

    private void StartOverlay(SwissOllyConfig config)
    {
        try
        {
            var controller = new OverlayController();
            // Captured here, on the UI thread. StatusMessage can be raised from the overlay's
            // timer or ETW threads, which must not call into the Window, and RegisterHotKey only
            // works from the thread that owns the window - so both go through the UI queue.
            var shellHwnd = WindowNative.GetWindowHandle(this);
            controller.StatusMessage += msg =>
            {
                if (msg == "__reregister_hotkey__")
                {
                    _ = _uiQueue.TryEnqueue(() => controller.RegisterHotkey(shellHwnd));
                    return;
                }

                _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    _viewModel.SetStatus(msg, StatusSeverity.Issue);
                    UpdateStatusText();
                });
            };

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
                onEnterResize: () => _uiQueue.TryEnqueue(() =>
                {
                    CancelActiveLayoutGesture();
                    CompleteFloatSettle();
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

                    // Safety: auto-clear after 5 seconds if WM_EXITSIZEMOVE is missed. A
                    // generation check makes an old timeout harmless after a normal or newer resize.
                    var safetyGeneration = System.Threading.Interlocked.Increment(ref _resizeSafetyGeneration);
                    _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                    {
                        _uiQueue.TryEnqueue(() =>
                        {
                            if (safetyGeneration != System.Threading.Volatile.Read(ref _resizeSafetyGeneration)
                                || !_isWindowResizing)
                            {
                                return;
                            }

                            _isWindowResizing = false;
                            ResumeDeferredSnapshot();
                            if (_scrollHost.Content is UIElement sc) sc.CacheMode = null;
                            if (_shellBorderRef is not null) _shellBorderRef.CornerRadius = new CornerRadius(0);
                            foreach (var card in _panelCards.Values)
                            {
                                card.SetResizeMode(false);
                                card.SetRenderingSuspended(false);
                            }
                        });
                    }, TaskScheduler.Default);
                }),
                onExitResize: () => _uiQueue.TryEnqueue(() =>
                {
                    System.Threading.Interlocked.Increment(ref _resizeSafetyGeneration);
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
                    else if (ResponsiveMinimumWidth() != _lastResponsiveMinimumWidth)
                    {
                        ReorderDashboardPanelsInPlace();
                    }

                    ReflowSummaryCards();
                    UpdateHeaderStatusWidth();
                    RefreshSettingsLayoutIfNeeded();

                    foreach (var card in _panelCards.Values)
                    {
                        card.SetResizeMode(false);
                        card.SetRenderingSuspended(false);
                    }

                    ResumeDeferredSnapshot();
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

        // SizeChanged can be raised while WinUI is still measuring/arranging the tree.
        // Mutating Grid rows or rebuilding card children in that callback can produce a
        // stowed XAML exception. Defer and coalesce all responsive tree changes to the next
        // dispatcher turn, using the latest measured width when the callback runs.
        var needsDashboard = DetermineDashboardColumns() != _lastDashboardColumnCount ||
            ResponsiveMinimumWidth() != _lastResponsiveMinimumWidth;
        var needsSummary = _summaryCardsBound && DetermineSummaryColumns() != _lastSummaryColumnCount;
        var needsSettings = _settingsViewActive && DetermineSettingsColumns() != _lastSettingsColumnCount;
        var needsHeader = _statusColumn is not null &&
            (_statusColumn.Width.Value < StatusColumnWidth || e.NewSize.Width < e.PreviousSize.Width);
        if (needsDashboard || needsSummary || needsSettings || needsHeader)
        {
            QueueResponsiveLayoutUpdate();
        }
    }

    private void QueueResponsiveLayoutUpdate()
    {
        if (_responsiveLayoutUpdateQueued)
        {
            return;
        }

        _responsiveLayoutUpdateQueued = true;
        if (!_uiQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _responsiveLayoutUpdateQueued = false;
            if (!_initialized || _isWindowResizing)
            {
                return;
            }

            StartupTrace.Write("Responsive layout update // deferred after SizeChanged");
            var nextColumnCount = DetermineDashboardColumns();
            if (nextColumnCount != _lastDashboardColumnCount)
            {
                RefreshDashboardPanels();
            }
            else if (ResponsiveMinimumWidth() != _lastResponsiveMinimumWidth)
            {
                ReorderDashboardPanelsInPlace();
            }

            ReflowSummaryCards();
            UpdateHeaderStatusWidth();
            RefreshSettingsLayoutIfNeeded();
        }))
        {
            _responsiveLayoutUpdateQueued = false;
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
            // Settings are always edited in place; drafts already reflect the applied values.
        }
    }

    private void OnPanelTogglesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
    }

    private void OnSettingsEntrySubmitted(object? sender, EventArgs e)
    {
        if (HasUnappliedSettings())
        {
            OnSaveSettingsClick(sender, e);
        }
    }

    private void OnCancelSettingsClick(object? sender, EventArgs e)
    {
        // Settings are edited in place; Revert throws away unapplied edits and stays on the page.
        var persistedTheme = _viewModel.SelectedTheme;
        var restoreThemePreview = _draftThemeMode != persistedTheme;
        SyncControlDeckDraftsFromViewModel();
        _settingsProblem = null;
        if (restoreThemePreview)
        {
            App.CurrentApp.ApplyTheme(persistedTheme);
        }

        RefreshSettingsDeck();
        _viewModel.SetStatus("Settings reverted", StatusSeverity.Healthy);
        UpdateStatusText();
    }

    private ActionChip? _settingsApplyChip;
    private ActionChip? _settingsRevertChip;
    private TextBlock? _settingsSavedLabel;
    private Border? _settingsApplyBar;
    private string? _settingsProblem;
    private DateTimeOffset _settingsSavedAt = DateTimeOffset.MinValue;
    private DispatcherQueueTimer? _settingsSavedTimer;
    private static readonly TimeSpan SettingsSavedVisibleFor = TimeSpan.FromSeconds(2.5);

    /// <summary>True when any draft differs from what is saved. Compares the raw draft text, so an
    /// invalid entry still counts as a change and Apply stays available to report it.</summary>
    private bool HasUnappliedSettings()
    {
        static bool SameText(string? a, string? b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        // The theme isn't compared: it applies on click and is never a draft. The custom metric
        // list counts only while Custom is (or was) chosen; an edit to it under "All metrics"
        // flagged unsaved changes the user couldn't see.
        var ov = _viewModel.OverlayConfig;
        var customMatters = _draftOverlayMode == SwissOlly.Core.Models.OverlayMode.Custom ||
            ov.Mode == SwissOlly.Core.Models.OverlayMode.Custom;
        return !SameText(_draftScrapeIntervalInput, _viewModel.ScrapeIntervalInput) ||
            !SameText(_draftRetentionInput, _viewModel.RetentionHoursInput) ||
            !SameText(_draftStorageDirectory, _viewModel.StorageDirectory) ||
            _draftOverlayEnabled != ov.Enabled ||
            _draftOverlayMode != ov.Mode ||
            _draftOverlayCorner != ov.Corner ||
            !SameText(_draftOverlayColor, ov.TextColor) ||
            !SameText(_draftOverlayFontSize, ov.FontSizePx.ToString(CultureInfo.InvariantCulture)) ||
            !SameText(_draftOverlayOpacity, ((int)Math.Round(ov.Opacity * 100)).ToString(CultureInfo.InvariantCulture)) ||
            !SameText(_draftOverlayHotkey, ov.HotkeyGesture) ||
            (customMatters && !new HashSet<string>(_draftOverlayCustomMetrics, StringComparer.OrdinalIgnoreCase).SetEquals(ov.CustomMetrics));
    }

    /// <summary>
    /// The floating bar over the bottom of Settings: it appears while something is unapplied,
    /// explains a rejected Apply, and says "Saved" for a moment after a good one. Apply used to
    /// sit below the overlay card, a long scroll from the retention or interval just changed.
    /// </summary>
    private void UpdateSettingsActionState()
    {
        if (_settingsApplyBar is null || _settingsApplyChip is null || _settingsRevertChip is null || _settingsSavedLabel is null)
        {
            return;
        }

        var dirty = HasUnappliedSettings();
        ToolTipService.SetToolTip(_settingsApplyChip, dirty ? "Save and apply your changes (Ctrl+S)" : "No changes to apply");

        bool show;
        if (_settingsProblem is not null && dirty)
        {
            // A rejected Apply explains itself beside the button, not only in the header.
            SetTextIfChanged(_settingsSavedLabel, _settingsProblem);
            _settingsSavedLabel.Foreground = ResolveBrush("CriticalBrush", "#FF897D");
            show = true;
        }
        else
        {
            _settingsProblem = null;
            var showSaved = !dirty && DateTimeOffset.Now - _settingsSavedAt < SettingsSavedVisibleFor;
            SetTextIfChanged(_settingsSavedLabel, dirty ? "You have changes that aren't applied yet." : "Saved");
            _settingsSavedLabel.Foreground = ResolveBrush("TextSecondaryBrush", "#C4C8CE");
            show = dirty || showSaved;
            if (showSaved)
            {
                _settingsSavedTimer ??= CreateSettingsSavedTimer();
                _settingsSavedTimer.Interval = SettingsSavedVisibleFor - (DateTimeOffset.Now - _settingsSavedAt);
                _settingsSavedTimer.Start();
            }
        }

        show &= _settingsViewActive;
        _settingsApplyChip.IsEnabled = show && dirty;
        _settingsRevertChip.IsEnabled = show && dirty;
        _settingsApplyBar.IsHitTestVisible = show;
        // A hidden bar is collapsed once it has faded, so it is gone for the keyboard and screen
        // readers too. Faded but present, Tab from the Overview could land on an invisible
        // Revert or Apply.
        if (show)
        {
            _applyBarCollapseTimer?.Stop();
            _settingsApplyBar.Visibility = Visibility.Visible;
            _settingsApplyBar.Opacity = 1;
        }
        else if (_settingsApplyBar.Visibility == Visibility.Visible && _settingsApplyBar.Opacity > 0)
        {
            _settingsApplyBar.Opacity = 0;
            _applyBarCollapseTimer ??= CreateApplyBarCollapseTimer();
            _applyBarCollapseTimer.Start();
        }
    }

    private DispatcherQueueTimer? _applyBarCollapseTimer;

    private DispatcherQueueTimer CreateApplyBarCollapseTimer()
    {
        var timer = _uiQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) =>
        {
            if (_settingsApplyBar is { Opacity: 0 })
            {
                _settingsApplyBar.Visibility = Visibility.Collapsed;
            }
        };
        return timer;
    }

    private static void SetTextIfChanged(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }

    /// <summary>Built once and kept: it floats over the scrolling page, so it stays in reach.</summary>
    private Border BuildSettingsApplyBar()
    {
        _settingsRevertChip = CreateActionChip("Revert", OnCancelSettingsClick);
        _settingsApplyChip = CreateActionChip("Apply", OnSaveSettingsClick, filled: true);
        _settingsSavedLabel = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ResolveBrush("TextSecondaryBrush", "#C4C8CE"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            _settingsSavedLabel, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(_settingsSavedLabel);
        var chips = CreateChipRow(_settingsRevertChip, _settingsApplyChip);
        layout.Children.Add(chips);
        Grid.SetColumn(chips, 1);

        _settingsApplyBar = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#222529"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(UiMetrics.OverlayRadius),
            Padding = new Thickness(18, 8, 8, 8),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(PageGutter, 0, PageGutter, 18),
            Opacity = 0,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = layout,
        };
        if (Motion.AnimationsEnabled)
        {
            _settingsApplyBar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        }

        return _settingsApplyBar;
    }

    private DispatcherQueueTimer CreateSettingsSavedTimer()
    {
        var timer = _uiQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UpdateSettingsActionState();
        return timer;
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

    private SwissOlly.Core.Models.OverlayConfig BuildOverlayConfigFromDrafts()
    {
        TryParseDraftNumber(_draftOverlayFontSize, "px", out var fontSize);
        TryParseDraftNumber(_draftOverlayOpacity, "%", out var opacityPct);

        return new SwissOlly.Core.Models.OverlayConfig
        {
            Enabled = _draftOverlayEnabled,
            Mode = _draftOverlayMode,
            Corner = _draftOverlayCorner,
            TextColor = SwissOlly.Core.Models.OverlayConfig.NormalizeHexColor(_draftOverlayColor) ?? _draftOverlayColor,
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
            : SwissOllyConfig.DefaultScrapeIntervalSeconds;

    private void SetScrapeInterval(int seconds)
    {
        _draftScrapeIntervalInput = seconds == SwissOllyConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : seconds.ToString(CultureInfo.InvariantCulture);
        RefreshSettingsDeck();
    }

    private void ResetScrapeInterval()
    {
        _draftScrapeIntervalInput = string.Empty;
        RefreshSettingsDeck();
    }

    private void SetRetentionInput(string value)
    {
        _draftRetentionInput = value;
        RefreshSettingsDeck();
    }

    private void ResetRetentionHours()
    {
        _draftRetentionInput = string.Empty;
        RefreshSettingsDeck();
    }

    private void ResetStorageDirectory()
    {
        _draftStorageDirectory = string.Empty;
        RefreshSettingsDeck();
    }

    private void SetStorageDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _draftStorageDirectory = value.Trim();
        RefreshSettingsDeck();
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

        normalizedText = seconds == SwissOllyConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : seconds.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private async Task ApplyRuntimeSettingsAsync(
        SwissOllyConfig config,
        bool collectorRestartRequired,
        bool storageChanged,
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
                        SubscribeCollector(_collectorService);
                    }

                    await _collectorService.StartAsync(config, CancellationToken.None);
                    // A storage-directory change can reopen an older database that has not
                    // been maintained under the current policy. Apply the configured cutoff
                    // before reporting settings as active.
                    await _metricStore.PruneAsync(config, CancellationToken.None);
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

            if (storageChanged)
            {
                // Nothing is copied: the new folder has its own database, and the old one stays
                // where it was. Show what the new folder holds.
                _viewModel.SetStatus("History now saves to the new folder. Earlier history stays in the old one.", StatusSeverity.Healthy, TimeSpan.FromSeconds(10));
                UpdateStatusText();
                _viewModel.SetHistoryLoading(true);
                _pendingHistoryLoad = Task.Run(() => TryLoadHistoryAsync(config));
            }
            else
            {
                _viewModel.SetStatus(_viewModel.DashboardPanels.Count > 0
                    ? "Streaming local telemetry"
                    : "Waiting for first sample", _viewModel.DashboardPanels.Count > 0 ? StatusSeverity.Healthy : StatusSeverity.Pending);
                UpdateStatusText();
            }

            if (_overlayController != null && config.Overlay != null)
            {
                _overlayController.ApplyConfig(config.Overlay);
                // Re-register hotkey if gesture changed (controller signals via StatusMessage)
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"Settings issue: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
        }
    }

    private async void OnBrowseStorageClick(object? sender, EventArgs e)
    {
        try
        {
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

            if (!SwissOllyConfig.TryNormalizeStorageDirectory(folder.Path, out var storageDirectory))
            {
                _viewModel.SetStatus("History must live on a local drive, not a network share.", StatusSeverity.Issue);
                UpdateStatusText();
                return;
            }

            SetStorageDirectory(storageDirectory);
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("BrowseStorageClick", ex);
            _viewModel.SetStatus($"Storage picker issue: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
        }
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
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SwissOlly.ico");
            var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "swissolly-256.png");
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
            var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "swissolly-256.png");
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

    private int DetermineSummaryColumns()
    {
        var width = _rootLayout.ActualWidth > 0 ? _rootLayout.ActualWidth : 1280;
        // Two from 600px: one column stacked the four cards about 430px tall at the 720px minimum
        // window, pushing every chart below the fold.
        return width >= 1120 ? 4 : width >= 600 ? 2 : 1;
    }

    /// <summary>History kept in memory at full resolution; longer ranges are read on demand.</summary>
    private static readonly TimeSpan BaseHistoryWindow = TimeSpan.FromHours(24);

    /// <summary>The most recent stretch loaded sample by sample; older base history is per minute.
    /// The same window a running session keeps raw, so a restart shows what was on screen.</summary>
    private static readonly TimeSpan RawHistoryWindow = MetricPanelViewModel.RawSampleWindow;

    private DateTimeOffset _baseHistoryStartUtc = DateTimeOffset.MaxValue;
    private int _wideHistoryMinutes;
    private int _wideHistoryRequest;

    /// <summary>
    /// Loads the last 24 hours (or less, with shorter retention): raw samples for the last hour,
    /// minute averages before that. Loading the whole retention window, as before, put every
    /// point of a 90-day or year-long history in memory at launch.
    /// </summary>
    private async Task TryLoadHistoryAsync(SwissOllyConfig config)
    {
        try
        {
            StartupTrace.Write("TryLoadHistoryAsync start");
            var now = DateTimeOffset.UtcNow;
            var retentionWindow = config.GetRetentionWindow();
            var baseWindow = retentionWindow < BaseHistoryWindow ? retentionWindow : BaseHistoryWindow;
            var baseStart = now - baseWindow;
            // Cut at a whole minute, where the running session folds its own samples (see
            // MetricPanelViewModel.RawSampleWindow): each average then covers a full minute, and
            // the reload is point for point what the last session showed.
            var rawStart = baseWindow > RawHistoryWindow ? MetricPanelViewModel.MinuteFloor(now - RawHistoryWindow) : baseStart;
            var store = _metricStore;
            // Run, not just await: SQLite is synchronous, so its "async" reads block whatever
            // thread calls them. ConfigureAwait(false): a close that waits on this task must not
            // deadlock against its own continuation on the UI thread.
            var history = await Task.Run(async () =>
            {
                var older = rawStart > baseStart
                    ? await store.LoadHistoryAsync(baseStart, rawStart, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)
                    : [];
                var recent = await store.LoadHistoryAsync(rawStart, now.AddMinutes(1), TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                return MetricHistory.Concat(older, recent);
            }).ConfigureAwait(false);

            // Back to the UI thread explicitly for the view-model update.
            var applied = new TaskCompletionSource();
            if (_uiQueue.TryEnqueue(() =>
            {
                try
                {
                    _viewModel.LoadHistory(history);
                    _baseHistoryStartUtc = baseStart;
                    _wideHistoryMinutes = 0;
                }
                finally
                {
                    _viewModel.SetHistoryLoading(false);
                    applied.TrySetResult();
                }

                // A range wider than the base window (restored from the last session) still
                // needs its older part.
                RequestWideHistory(_viewModel.SelectedWindowMinutes);
            }))
            {
                await applied.Task.ConfigureAwait(false);
            }
            StartupTrace.Write($"TryLoadHistoryAsync complete // panels={history.Count}");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("History load", ex);
            _ = _uiQueue.TryEnqueue(() =>
            {
                _viewModel.SetHistoryLoading(false);
                if (!_hasReceivedFirstSnapshot)
                {
                    _viewModel.SetStatus("History unavailable, starting live telemetry", StatusSeverity.Issue);
                    UpdateStatusText();
                }
            });
        }
    }

    private async Task EnsureCollectorRunningAsync(SwissOllyConfig config)
    {
        StartupTrace.Write("EnsureCollectorRunningAsync start");
        var collectorService = _collectorService;
        if (collectorService is null)
        {
            // The collector's constructor opens a PDH query and expands wildcard counters over
            // every processor, disk, NIC and GPU engine - ~0.45s that used to run on the UI
            // thread, freezing the window in the middle of the launch animation.
            var store = _metricStore;
            var created = await Task.Run(() => new CollectorService(new WindowsMetricCollector(), store));
            collectorService = _collectorService ??= created;
        }

        StartupTrace.Write("CollectorService created");
        SubscribeCollector(collectorService);

        StartupTrace.Write("Calling CollectorService.StartAsync");

        // StartAsync runs on a worker; the continuation resumes on the UI thread (no
        // ConfigureAwait(false)), so nothing after this point touches UI objects off-thread.
        await Task.Run(() => collectorService.StartAsync(config, CancellationToken.None));
        StartupTrace.Write("CollectorService.StartAsync complete");

        if (!_summaryCardsBound)
        {
            BuildSummaryCards();
        }

        // The first sample may already be in; don't regress its status line.
        if (!_hasReceivedFirstSnapshot)
        {
            _viewModel.SetStatus("Waiting for first sample", StatusSeverity.Pending);
            UpdateStatusText();
        }

        StartupTrace.Write($"EnsureCollectorRunningAsync complete // dashboardPanels={_viewModel.DashboardPanels.Count}");
    }

    private void UpdateStatusText()
    {
        var text = _viewModel.StatusText;
        _statusText.Text = text;
        _statusIndicator.Fill = _viewModel.StatusSeverity switch
        {
            StatusSeverity.Issue => ResolveBrush("CriticalBrush", "#FF897D"),
            StatusSeverity.Pending => ResolveBrush("TextMutedBrush", "#858B93"),
            _ => ResolveBrush("AccentBrush", "#79B8FF"),
        };

        if (!string.Equals(_lastRenderedStatusText, text, StringComparison.Ordinal))
        {
            _lastRenderedStatusText = text;
            StartupTrace.Write($"StatusText -> {text}");
            if (_statusText.IsTextTrimmed)
            {
                // Still trimmed after a text change: IsTextTrimmedChanged won't fire to refresh it.
                ToolTipService.SetToolTip(_statusText, text);
            }
        }
    }

    private void ApplyGlobalWindowRange(int minutes)
    {
        StartupTrace.Write($"ApplyGlobalWindowRange start // minutes={minutes}");
        _globalAbsoluteStartUtc = null;
        _globalAbsoluteEndUtc = null;
        _viewModel.ReleaseStatusHold(); // "Showing 10:55 PM - 11:25 PM" no longer applies
        _viewModel.ApplyGlobalWindowRange(minutes);
        HideGlobalRangeEditor();
        RefreshGlobalRangeControls();
        StartupTrace.Write($"ApplyGlobalWindowRange complete // minutes={minutes}");

        RequestWideHistory(minutes);
    }

    /// <summary>
    /// For a range longer than the base window, reads the older part in the background, averaged
    /// to about two thousand points per series, and puts it in front of the in-memory history.
    /// The read used to run on the UI thread (SQLite is synchronous) and froze the window for
    /// about 0.6s on 7d and 30d, then replaced the recent raw samples with minute averages.
    /// </summary>
    private void RequestWideHistory(int minutes)
    {
        var retentionMinutes = (int)Math.Min(int.MaxValue, _viewModel.BuildConfig().GetRetentionWindow().TotalMinutes);
        var wanted = Math.Min(minutes, retentionMinutes);
        var baseMinutes = _baseHistoryStartUtc == DateTimeOffset.MaxValue
            ? int.MaxValue
            : (int)Math.Ceiling((DateTimeOffset.UtcNow - _baseHistoryStartUtc).TotalMinutes);
        if (wanted <= baseMinutes || wanted == _wideHistoryMinutes)
        {
            return;
        }

        _ = LoadWideHistoryAsync(wanted, ++_wideHistoryRequest);
    }

    private async Task LoadWideHistoryAsync(int minutes, int request)
    {
        try
        {
            StartupTrace.Write($"Wide history load start // minutes={minutes}");
            _viewModel.SetHistoryLoading(true);
            var to = _baseHistoryStartUtc;
            var from = DateTimeOffset.UtcNow.AddMinutes(-minutes);
            var bucket = MetricHistory.BucketFor(to - from);
            var store = _metricStore;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var history = await Task.Run(() => store.LoadHistoryAsync(from, to, bucket, CancellationToken.None));
            if (request != _wideHistoryRequest)
            {
                return; // A newer range was picked while this one loaded.
            }

            _viewModel.MergeOlderHistory(history, to);
            _wideHistoryMinutes = minutes;
            StartupTrace.Write($"Wide history load applied // minutes={minutes} bucket={bucket.TotalMinutes}m read={stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("Wide history load", ex);
        }
        finally
        {
            if (request == _wideHistoryRequest)
            {
                _viewModel.SetHistoryLoading(false);
            }
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

        // With its ▾: a bare "5m" didn't read as something that opens.
        _globalRangeButton.Text = GetGlobalRangeButtonText();
        _globalRangeButton.IsActive = _globalRangeEditorVisible;
        _globalRangeButton.IsFilled = true;
        var hasZoomedPanels = _viewModel.DashboardPanels.Any(panel => panel.IsZoomed);
        _globalResetZoomButton.Visibility = hasZoomedPanels && !_isLayoutEditMode ? Visibility.Visible : Visibility.Collapsed;
        _globalResetZoomButton.Opacity = 1d;
    }

    private (Color Foreground, Color Inactive, Color Hover, Color Pressed)? _appliedTitleBarColors;

    /// <summary>
    /// The caption buttons in the palette's colours. A title bar is a window, not XAML, so these
    /// are copies and are set again on each theme change; colours already applied are skipped.
    /// </summary>
    private void RefreshWindowChrome()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            if (titleBar is null)
            {
                return;
            }

            var isLight = _rootLayout.RequestedTheme == ElementTheme.Light;
            var colors = (
                Foreground: App.CurrentApp.ResolveThemeColor("TextSecondaryBrush", isLight ? "#4A6078" : "#C6D7EA"),
                Inactive: App.CurrentApp.ResolveThemeColor("TextMutedBrush", isLight ? "#6F8399" : "#8098B2"),
                Hover: App.CurrentApp.ResolveThemeColor("SurfaceElevatedBrush", isLight ? "#F4F8FC" : "#112033"),
                Pressed: App.CurrentApp.ResolveThemeColor("SurfaceStrongBrush", isLight ? "#EDF4FB" : "#18314A"));
            if (_appliedTitleBarColors == colors)
            {
                return;
            }

            if (_appliedTitleBarColors is null)
            {
                var transparent = Color.FromArgb(0, 0, 0, 0);
                titleBar.BackgroundColor = transparent;
                titleBar.InactiveBackgroundColor = transparent;
                titleBar.ButtonBackgroundColor = transparent;
                titleBar.ButtonInactiveBackgroundColor = transparent;
            }

            titleBar.ForegroundColor = colors.Foreground;
            titleBar.InactiveForegroundColor = colors.Inactive;
            titleBar.ButtonForegroundColor = colors.Foreground;
            titleBar.ButtonInactiveForegroundColor = colors.Inactive;
            titleBar.ButtonHoverBackgroundColor = colors.Hover;
            titleBar.ButtonHoverForegroundColor = colors.Foreground;
            titleBar.ButtonPressedBackgroundColor = colors.Pressed;
            titleBar.ButtonPressedForegroundColor = colors.Foreground;
            _appliedTitleBarColors = colors;
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("RefreshWindowChrome", ex);
        }
    }

    private Flyout? _rangeFlyout;
    private InlineTextEntry? _rangeFromEntry;
    private InlineTextEntry? _rangeToEntry;

    /// <summary>
    /// The time range is a popover anchored to its chip. It used to be a full-width panel
    /// inserted into the page - three titled sections, five explanatory sentences and two info
    /// boxes restating what the chip already said - which shoved the board down a whole screen,
    /// burying the very panels the choice applies to. The board now stays put.
    /// </summary>
    private Flyout EnsureRangeFlyout()
    {
        if (_rangeFlyout is not null)
        {
            return _rangeFlyout;
        }

        ActionChip[] chips =
        [
            _globalOneMinuteButton, _globalFiveMinuteButton, _globalFifteenMinuteButton, _globalThirtyMinuteButton, _globalOneHourButton,
            _globalTwelveHourButton, _globalTwentyFourHourButton, _globalTwoDayButton, _globalFiveDayButton, _globalSevenDayButton,
            _globalThirtyDayButton, _globalNinetyDayButton, _globalOneYearButton,
        ];

        const int presetColumns = 5;
        var presets = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var column = 0; column < presetColumns; column++)
        {
            presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < chips.Length; index++)
        {
            var chip = DetachFromParent(chips[index]);
            chip.MinWidth = 0;
            chip.MinHeight = 32;
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            while (presets.RowDefinitions.Count <= index / presetColumns)
            {
                presets.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            presets.Children.Add(chip);
            Grid.SetRow(chip, index / presetColumns);
            Grid.SetColumn(chip, index % presetColumns);
        }

        _rangeFromEntry = CreateDeckInlineEntry(string.Empty, "2026-04-02 09:00");
        _rangeToEntry = CreateDeckInlineEntry(string.Empty, "2026-04-02 09:30");
        _rangeFromEntry.AccessibleLabel = "Custom range from";
        _rangeToEntry.AccessibleLabel = "Custom range to";
        EventHandler applyRangeOnEnter = (_, _) =>
            ApplyAbsoluteGlobalRange(_rangeFromEntry?.Text ?? string.Empty, _rangeToEntry?.Text ?? string.Empty);
        _rangeFromEntry.Submitted += applyRangeOnEnter;
        _rangeToEntry.Submitted += applyRangeOnEnter;
        var nowChip = CreateActionChip("Now", (_, _) =>
        {
            if (_rangeToEntry is not null)
            {
                _rangeToEntry.Text = FormatRangeEntry(DateTimeOffset.Now);
            }
        });
        nowChip.MinHeight = 40;
        ToolTipService.SetToolTip(nowChip, "Set the end of the range to now");

        var custom = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        custom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        custom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fromLabel = CreateInlineLabel("From");
        var toLabel = CreateInlineLabel("To");
        custom.Children.Add(fromLabel);
        custom.Children.Add(_rangeFromEntry);
        custom.Children.Add(toLabel);
        custom.Children.Add(_rangeToEntry);
        custom.Children.Add(nowChip);
        Grid.SetColumn(_rangeFromEntry, 1);
        Grid.SetColumnSpan(_rangeFromEntry, 2);
        Grid.SetRow(toLabel, 1);
        Grid.SetRow(_rangeToEntry, 1);
        Grid.SetColumn(_rangeToEntry, 1);
        Grid.SetRow(nowChip, 1);
        Grid.SetColumn(nowChip, 2);

        var apply = CreateActionChip("Apply range", (_, _) =>
            ApplyAbsoluteGlobalRange(_rangeFromEntry?.Text ?? string.Empty, _rangeToEntry?.Text ?? string.Empty), filled: true);
        var actions = CreateChipRow(apply);

        var content = new StackPanel
        {
            Width = 380,
            Spacing = 12,
            Children =
            {
                CreatePrimaryText("Time range", 13, true),
                presets,
                new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
                },
                CreatePrimaryText("Custom range", 13, true),
                custom,
                actions,
            },
        };

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("SurfaceElevatedBrush", "#222529")));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ResolveBrush("SurfaceStrokeBrush", "#2C3035")));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(UiMetrics.OverlayRadius)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 460d));

        _rangeFlyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = presenterStyle,
        };
        _rangeFlyout.Closed += (_, _) =>
        {
            _globalRangeEditorVisible = false;
            RefreshGlobalRangeControls();
        };

        return _rangeFlyout;
    }

    private void RenderGlobalRangeEditor()
    {
        var flyout = EnsureRangeFlyout();
        var start = _globalAbsoluteStartUtc ?? DateTimeOffset.Now.AddMinutes(-_viewModel.SelectedWindowMinutes);
        var end = _globalAbsoluteEndUtc ?? DateTimeOffset.Now;
        _rangeFromEntry!.Text = FormatRangeEntry(start);
        _rangeToEntry!.Text = FormatRangeEntry(end);
        _globalRangeEditorVisible = true;
        RefreshGlobalRangeControls();
        flyout.ShowAt(_globalRangeButton);
    }

    private void HideGlobalRangeEditor()
    {
        _globalRangeEditorVisible = false;
        _rangeFlyout?.Hide();
        RefreshGlobalRangeControls();
    }

    private static string FormatRangeEntry(DateTimeOffset value)
        => value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private void ApplyAbsoluteGlobalRange(string fromText, string toText)
    {
        string? problem = null;
        if (!TryParseGlobalDateTime(fromText, out var start))
        {
            problem = $"From isn't a date and time SwissOlly can read. Use the form {FormatRangeEntry(DateTimeOffset.Now)}.";
        }
        else if (!TryParseGlobalDateTime(toText, out var end))
        {
            problem = $"To isn't a date and time SwissOlly can read. Use the form {FormatRangeEntry(DateTimeOffset.Now)}.";
        }
        else if (end <= start)
        {
            problem = "To must be later than From.";
        }
        else if (start >= DateTimeOffset.Now)
        {
            problem = "From is in the future, so there's nothing to show yet.";
        }
        else
        {
            // An end in the future would leave the right of every chart empty.
            ApplyAbsoluteGlobalRange(start, end > DateTimeOffset.Now ? DateTimeOffset.Now : end);
            return;
        }

        _viewModel.SetStatus(problem, StatusSeverity.Issue);
        UpdateStatusText();
    }

    private void ApplyAbsoluteGlobalRange(DateTimeOffset start, DateTimeOffset end)
    {
        RunWithDeferredPanelRendering(() =>
        {
            _globalAbsoluteStartUtc = start;
            _globalAbsoluteEndUtc = end;
            _viewModel.ApplyGlobalAbsoluteRange(start, end);
        });

        // A pinned range can reach back past the in-memory window.
        RequestWideHistory((int)Math.Min(int.MaxValue, Math.Ceiling((DateTimeOffset.UtcNow - start).TotalMinutes)));

        HideGlobalRangeEditor();
        _viewModel.SetStatus($"Showing {TimeLabels.Range(start.LocalDateTime, end.LocalDateTime)}", StatusSeverity.Healthy, TimeSpan.FromSeconds(6));
        UpdateStatusText();
    }

    private string GetGlobalRangeButtonText()
    {
        if (_globalAbsoluteStartUtc.HasValue && _globalAbsoluteEndUtc.HasValue)
        {
            return $"{TimeLabels.Range(_globalAbsoluteStartUtc.Value.LocalDateTime, _globalAbsoluteEndUtc.Value.LocalDateTime)} ▾";
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
        // Selection is IsActive alone (dimming the other chips also dimmed their text); "filled"
        // now means a primary button.
        chip.IsActive = isActive;
    }
}

internal sealed record SummaryCardVisual(
    Border Surface,
    TextBlock TitleText,
    TextBlock ValueText,
    TextBlock CaptionText,
    Border BadgeHost,
    RingMeter Meter,
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

    /// <summary>Loads the icon image closest to <paramref name="size"/> pixels square.</summary>
    public static nint LoadWindowIcon(string iconPath, int size) =>
        LoadImageW(0, iconPath, ImageIcon, size, size, LoadFromFile);

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
