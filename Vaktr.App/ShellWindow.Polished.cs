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
    private const double PageGutter = 24;
    private const double SectionSpacing = 20;
    private const double PanelSpacing = 14;
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
    private readonly StackPanel _overviewView;
    private readonly StackPanel _settingsView;
    private readonly ActionChip _overviewNavigationButton;
    private readonly ActionChip _settingsNavigationButton;

    private Grid _rootLayout;
    private readonly Grid _titleBarDragHost;
    private readonly ScrollViewer _scrollHost;
    private Border? _loadingOverlay;
    private readonly Border _controlsBodyHost;
    private Border? _controlsSurfaceRef;
    private Border? _summarySurfaceRef;
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
    private bool _launchIntroPlayed;
    private bool _settingsIntroPlayed;
    private int _lastDashboardColumnCount;
    private int _lastSummaryColumnCount;
    private int _lastSettingsColumnCount;
    private bool _windowIconApplied;
    private bool _summaryCardsBound;
    private bool _globalRangeEditorVisible;
    private bool _settingsViewActive;
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

        _overviewView = new StackPanel { Spacing = SectionSpacing };
        _settingsView = new StackPanel { Spacing = SectionSpacing, Visibility = Visibility.Collapsed };
        _overviewNavigationButton = CreateActionChip("Overview", OnOverviewNavigationClick, true);
        _settingsNavigationButton = CreateActionChip("Settings", OnSettingsNavigationClick);
        _overviewNavigationButton.MinHeight = _settingsNavigationButton.MinHeight = 32;
        _overviewNavigationButton.MinWidth = _settingsNavigationButton.MinWidth = 88;

        Title = "Vaktr";

        _statusText = CreateSecondaryText(string.Empty, 11.5);
        _statusText.MaxWidth = 300;
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
        _editLayoutButton = CreateActionChip("Edit layout", OnToggleLayoutEditClick);
        ToolTipService.SetToolTip(_editLayoutButton, "Resize and rearrange panels");
        _cancelLayoutButton = CreateActionChip("Cancel", OnCancelLayoutEditClick);
        _cancelLayoutButton.Visibility = Visibility.Collapsed;
        _globalResetZoomButton.Visibility = Visibility.Collapsed;
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
        MaybeStartGestureProbe();
    }

    public void ApplyInitialTheme(ThemeMode mode)
    {
        var requestedTheme = IsLightTheme(mode) ? ElementTheme.Light : ElementTheme.Dark;
        if (_rootLayout.RequestedTheme != requestedTheme)
        {
            _rootLayout.RequestedTheme = requestedTheme;
        }

        RefreshWindowChrome();
    }

    public void ApplyTheme(ThemeMode mode)
    {
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
            _overviewNavigationButton, _settingsNavigationButton,
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
            var badgeHost = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = card.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleText = CreateMutedText(string.Empty, 9.5);
            titleText.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(SummaryCardViewModel.Title)) });

            var valueText = CreatePrimaryText(string.Empty, 24, true);
            valueText.FontFamily = new FontFamily("Segoe UI Variable Display");
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

            var details = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    titleRow,
                    valueText,
                    gaugeHost,
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
            _summaryCardVisuals.Add(new SummaryCardVisual(summaryCard, titleText, valueText, captionText, badgeHost, gaugeTrack, gaugeFill, card));
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

    private void RefreshSummaryCardThemeResources()
    {
        if (!_summaryCardsBound)
        {
            return;
        }

        for (var i = 0; i < _summaryCardVisuals.Count; i++)
        {
            var visual = _summaryCardVisuals[i];
            visual.Surface.Background = BrushFactory.CreateBrush("#00FFFFFF");
            visual.Surface.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            visual.TitleText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            visual.ValueText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
            visual.CaptionText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
            visual.GaugeTrack.Background = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            visual.GaugeFill.Background = ResolveSummaryUtilizationBrush(visual.ViewModel.Utilization, visual.ViewModel.AccentBrush);
            visual.BadgeHost.Background = visual.ViewModel.AccentBrush;
            visual.BadgeHost.BorderBrush = visual.ViewModel.AccentBrush;
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
                staleCard.PanelSizePresetRequested -= OnPanelSizePresetRequested;
                staleCard.PanelDragMoved -= OnPanelDragMoved;
                staleCard.PanelResizeStarted -= OnPanelResizeStarted;
                staleCard.PanelResizeDelta -= OnPanelResizeDelta;
                staleCard.PanelResizeCompleted -= OnPanelResizeCompleted;
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
                card.PanelSizePresetRequested += OnPanelSizePresetRequested;
                card.PanelDragMoved += OnPanelDragMoved;
                card.PanelResizeStarted += OnPanelResizeStarted;
                card.PanelResizeDelta += OnPanelResizeDelta;
                card.PanelResizeCompleted += OnPanelResizeCompleted;
                card.SetLayoutEditMode(_isLayoutEditMode);
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
        if (_controlDeckEditableActive)
        {
            RenderEditableControlDeck();
        }
        else
        {
            RenderControlDeckSummary();
        }
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
            if (_summaryCardsBound && DetermineSummaryColumns() != _lastSummaryColumnCount)
            {
                BuildSummaryCards();
            }

            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PlayLaunchEntrance);

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

    private void PlayLaunchEntrance()
    {
        if (_launchIntroPlayed)
        {
            return;
        }

        _launchIntroPlayed = true;
        if (!Motion.AnimationsEnabled)
        {
            return;
        }

        if (_appHeader is not null)
        {
            Motion.PlayEntrance(_appHeader);
        }

        // Stagger is tight on purpose. The previous schedule ran the last panel out to ~292ms
        // before it even began a 260ms animation, so launch took over half a second to settle.
        // Capping the cascade at 10 panels keeps a 14-panel dashboard from trailing off.
        var sectionDelay = 25;
        foreach (var child in _overviewView.Children)
        {
            if (!ReferenceEquals(child, _dashboardGrid))
            {
                Motion.PlayEntrance(child, TimeSpan.FromMilliseconds(sectionDelay));
                sectionDelay += 35;
            }
        }

        var panelIndex = 0;
        foreach (var panel in _viewModel.DashboardPanels)
        {
            if (_panelCards.TryGetValue(panel.PanelKey, out var card))
            {
                var delay = 70 + Math.Min(panelIndex, 10) * 16;
                Motion.PlayEntrance(card, TimeSpan.FromMilliseconds(delay));
                panelIndex++;
            }
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

    private void PlayControlEditorEntrance()
    {
        if (!Motion.AnimationsEnabled || _controlsBodyHost.Child is not StackPanel body)
        {
            return;
        }

        var cardIndex = 0;
        if (body.Children.Count > 0 && body.Children[0] is Grid cards)
        {
            foreach (var card in cards.Children)
            {
                var delay = Math.Min(cardIndex, 6) * 28;
                Motion.PlayEntrance(card, TimeSpan.FromMilliseconds(delay));
                cardIndex++;
            }
        }

        if (body.Children.Count > 1)
        {
            Motion.PlayEntrance(body.Children[1], TimeSpan.FromMilliseconds(150));
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

    private void OnThemeChoiceClick(object? sender, EventArgs e)
    {
        if (sender is ActionChip { Tag: ThemeMode mode } && Enum.IsDefined(typeof(ThemeMode), mode))
        {
            _draftThemeMode = mode;
            App.CurrentApp.PreviewTheme(mode);
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
        // In edit mode a drag is a grid move: translate the pixel offset into cells and let the
        // engine resolve the board around the dropped panel. Outside edit mode this is just the
        // legacy reorder settling back into place.
        HidePlaceholder();

        if (_isLayoutEditMode
            && sender is TelemetryPanelCard card
            && card.Panel?.PanelKey is { Length: > 0 } key
            && _viewModel.GetPanelLayout(key) is { } layout)
        {
            var columnWidth = _dashboardGrid.ActualWidth / PanelLayout.Columns;
            if (columnWidth > 0)
            {
                var offset = card.DragOffset;
                var targetX = layout.X + (int)Math.Round(offset.X / columnWidth);
                var targetY = layout.Y + (int)Math.Round(offset.Y / DashboardRowUnitPx);

                if (targetX != layout.X || targetY != layout.Y)
                {
                    var placements = _viewModel.PanelLayouts
                        .Select(kv => new PanelPlacement(kv.Key, kv.Value))
                        .ToList();

                    _viewModel.ApplyPlacements(
                        GridLayoutEngine.Move(placements, key, targetX, targetY));
                }
            }
        }

        ReorderDashboardPanelsInPlace();
        DebouncePersistLayout();
    }

    private void OnPanelReorderRequested(object? sender, PanelReorderRequestedEventArgs e)
    {
        // Edit mode places by grid cell, so the drop-target swap must not also fire or the two
        // would produce different answers for the same gesture.
        if (_isLayoutEditMode)
        {
            return;
        }

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
        var maxRow = 0;

        foreach (var panel in panels)
        {
            var layout = _viewModel.GetPanelLayout(panel.PanelKey);
            if (layout is null)
            {
                continue;
            }

            // Widen narrow panels at small windows so a third-width card never becomes an
            // unreadable sliver. The saved geometry is untouched — this is display-only.
            var width = Math.Clamp(Math.Max(layout.W, minWidth), PanelLayout.MinWidth, PanelLayout.Columns);
            var x = Math.Clamp(layout.X, 0, PanelLayout.Columns - width);

            maxRow = Math.Max(maxRow, layout.Bottom);

            if (_panelCards.TryGetValue(panel.PanelKey, out var card) && !card.IsDragging)
            {
                Grid.SetColumn(card, x);
                Grid.SetRow(card, layout.Y);
                Grid.SetColumnSpan(card, width);
                Grid.SetRowSpan(card, layout.H);
            }
        }

        while (_dashboardGrid.RowDefinitions.Count < maxRow)
        {
            _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DashboardRowUnitPx) });
        }
    }

    /// <summary>Row unit in pixels. A default panel is H=6, so ~336px tall including spacing.</summary>
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

    private static int GetPanelColumnSpan(PanelSizePreset size) => size switch
    {
        PanelSizePreset.Wide or PanelSizePreset.Large => 2,
        _ => 1,
    };

    private void OnToggleLayoutEditClick(object? sender, EventArgs e)
        => SetLayoutEditMode(!_isLayoutEditMode, commit: true);

    private void OnCancelLayoutEditClick(object? sender, EventArgs e)
    {
        if (_layoutSnapshot is not null)
        {
            _viewModel.RestorePanelLayouts(_layoutSnapshot);
            ReorderDashboardPanelsInPlace();
        }

        SetLayoutEditMode(false, commit: false);
    }

    /// <summary>
    /// Entering edit mode snapshots geometry so Cancel is a true revert. Leaving with
    /// <paramref name="commit"/> persists through the existing debounced save path.
    /// </summary>
    private void SetLayoutEditMode(bool editing, bool commit)
    {
        if (_isLayoutEditMode == editing)
        {
            return;
        }

        _isLayoutEditMode = editing;

        if (editing)
        {
            _layoutSnapshot = _viewModel.SnapshotPanelLayouts();
        }
        else
        {
            _layoutSnapshot = null;
            if (commit)
            {
                DebouncePersistLayout();
            }
        }

        _editLayoutButton.Text = editing ? "Done" : "Edit layout";
        _editLayoutButton.IsActive = editing;
        _editLayoutButton.IsFilled = editing;
        _cancelLayoutButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

        // Leaving edit mode mid-gesture must not strand a card in its floating preview state
        // (explicit size, top-left aligned, raised z-index) with no release event coming.
        if (!editing)
        {
            _resizeCard?.EndResizePreview();
            _resizeCard = null;
            _resizeOriginKey = null;
            _resizeOriginLayout = null;
            HidePlaceholder();
        }

        foreach (var card in _panelCards.Values)
        {
            card.SetLayoutEditMode(editing);
        }

        if (editing)
        {
            _viewModel.StatusText = "Edit layout - drag a panel to move it, drag its edges to resize";
            UpdateStatusText();
        }
    }

    private PanelLayout? _resizeOriginLayout;
    private string? _resizeOriginKey;
    private TelemetryPanelCard? _resizeCard;
    private Windows.Foundation.Size _resizeOriginSize;
    private Border? _layoutPlaceholder;

    /// <summary>
    /// The drop target preview. A bare Border - no chart, no text - so moving it through the
    /// grid during a drag costs essentially nothing, which is the whole point: the expensive
    /// real panels stay put until the gesture ends.
    /// </summary>
    private Border EnsurePlaceholder()
    {
        if (_layoutPlaceholder is null)
        {
            _layoutPlaceholder = new Border
            {
                Background = ResolveBrush("AccentSoftBrush", "#294A6B"),
                BorderBrush = ResolveBrush("AccentBrush", "#79B8FF"),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Opacity = 0.35,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Canvas.SetZIndex(_layoutPlaceholder, 10);
            _dashboardGrid.Children.Add(_layoutPlaceholder);
        }

        return _layoutPlaceholder;
    }

    private void ShowPlaceholderAt(PanelLayout layout)
    {
        var placeholder = EnsurePlaceholder();
        Grid.SetColumn(placeholder, layout.X);
        Grid.SetRow(placeholder, layout.Y);
        Grid.SetColumnSpan(placeholder, layout.W);
        Grid.SetRowSpan(placeholder, layout.H);
        placeholder.Visibility = Visibility.Visible;
    }

    private void HidePlaceholder()
    {
        if (_layoutPlaceholder is not null)
        {
            _layoutPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Previews the cell a dragged panel will land in, without disturbing the board.</summary>
    private void OnPanelDragMoved(object? sender, PanelResizeEventArgs e)
    {
        if (!_isLayoutEditMode || string.IsNullOrEmpty(e.PanelKey))
        {
            return;
        }

        if (_viewModel.GetPanelLayout(e.PanelKey) is not { } layout)
        {
            return;
        }

        var columnWidth = _dashboardGrid.ActualWidth / PanelLayout.Columns;
        if (columnWidth <= 0)
        {
            return;
        }

        ShowPlaceholderAt(new PanelLayout(
            layout.X + (int)Math.Round(e.DeltaX / columnWidth),
            Math.Max(0, layout.Y + (int)Math.Round(e.DeltaY / DashboardRowUnitPx)),
            layout.W,
            layout.H).Normalize());
    }

    /// <summary>Captures the geometry and pixel size the drag starts from.</summary>
    private void OnPanelResizeStarted(object? sender, PanelResizeEventArgs e)
    {
        if (!_isLayoutEditMode || string.IsNullOrEmpty(e.PanelKey) || sender is not TelemetryPanelCard card)
        {
            return;
        }

        _resizeOriginLayout = _viewModel.GetPanelLayout(e.PanelKey)?.Clone();
        if (_resizeOriginLayout is null)
        {
            return;
        }

        _resizeOriginKey = e.PanelKey;
        _resizeCard = card;
        _resizeOriginSize = card.BeginResizePreview();
        ShowPlaceholderAt(_resizeOriginLayout);
    }

    /// <summary>
    /// Pixel feedback on the card, snapped preview on the placeholder. Nothing else in the grid
    /// is touched until the gesture completes.
    /// </summary>
    private void OnPanelResizeDelta(object? sender, PanelResizeEventArgs e)
    {
        if (!_isLayoutEditMode
            || _resizeOriginLayout is not { } origin
            || _resizeCard is not { } card
            || !string.Equals(_resizeOriginKey, e.PanelKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var columnWidth = _dashboardGrid.ActualWidth / PanelLayout.Columns;
        if (columnWidth <= 0)
        {
            return;
        }

        var width = _resizeOriginSize.Width;
        var height = _resizeOriginSize.Height;
        var offsetX = 0d;
        var offsetY = 0d;

        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Right)) width += e.DeltaX;
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Bottom)) height += e.DeltaY;
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Left))
        {
            width -= e.DeltaX;
            offsetX = e.DeltaX;
        }
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Top))
        {
            height -= e.DeltaY;
            offsetY = e.DeltaY;
        }

        card.UpdateResizePreview(width, height, offsetX, offsetY);

        // Snapped destination, derived from the same origin geometry every time so the result
        // depends only on where the pointer is now - never on the path it took.
        var dCols = (int)Math.Round(e.DeltaX / columnWidth);
        var dRows = (int)Math.Round(e.DeltaY / DashboardRowUnitPx);

        var x = origin.X;
        var y = origin.Y;
        var w = origin.W;
        var h = origin.H;

        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Right)) w = origin.W + dCols;
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Bottom)) h = origin.H + dRows;
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Left))
        {
            var clamped = Math.Min(dCols, origin.W - PanelLayout.MinWidth);
            x = origin.X + clamped;
            w = origin.W - clamped;
        }
        if (e.Edge.HasFlag(TelemetryPanelCard.ResizeEdge.Top))
        {
            var clamped = Math.Min(dRows, origin.H - PanelLayout.MinHeight);
            y = Math.Max(0, origin.Y + clamped);
            h = origin.H - (y - origin.Y);
        }

        ShowPlaceholderAt(new PanelLayout(x, y, w, h).Normalize());
    }

    /// <summary>Commits once, on release. This is the only layout pass of the whole gesture.</summary>
    private void OnPanelResizeCompleted(object? sender, EventArgs e)
    {
        var card = _resizeCard;
        var key = _resizeOriginKey;
        var placeholder = _layoutPlaceholder;

        _resizeCard = null;
        _resizeOriginKey = null;
        _resizeOriginLayout = null;

        card?.EndResizePreview();
        HidePlaceholder();

        if (!_isLayoutEditMode || key is null || placeholder is null)
        {
            return;
        }

        var target = new PanelLayout(
            Grid.GetColumn(placeholder),
            Grid.GetRow(placeholder),
            Grid.GetColumnSpan(placeholder),
            Grid.GetRowSpan(placeholder));

        var placements = _viewModel.PanelLayouts
            .Select(kv => new PanelPlacement(kv.Key, kv.Value))
            .ToList();

        _viewModel.ApplyPlacements(GridLayoutEngine.Apply(placements, key, target));
        ReorderDashboardPanelsInPlace();
        DebouncePersistLayout();
    }

    private void OnPanelSizePresetRequested(object? sender, PanelSizePresetRequestedEventArgs e)
    {
        if (!_viewModel.SetPanelSizePreset(e.PanelKey, e.Preset))
        {
            return;
        }

        ReorderDashboardPanelsInPlace();
        DebouncePersistLayout();
    }

    private bool _persistLayoutQueued;

    private void DebouncePersistLayout()
    {
        if (_persistLayoutQueued)
        {
            return;
        }

        _persistLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
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
        var shouldSuspend = suspended || _settingsViewActive;
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

    private void SetActiveShellView(bool settings)
    {
        if (_settingsViewActive == settings)
        {
            return;
        }

        _settingsViewActive = settings;
        _overviewView.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        _settingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        _overviewNavigationButton.IsActive = !settings;
        _overviewNavigationButton.IsFilled = !settings;
        _settingsNavigationButton.IsActive = settings;
        _settingsNavigationButton.IsFilled = settings;
        // ChangeView(horizontalOffset, verticalOffset, zoomFactor, disableAnimation).
        // This previously passed 0 in the THIRD slot, setting zoomFactor to 0 rather than
        // scrolling to the top. Zero clamps to MinZoomFactor (0.1), so every switch into a view
        // rendered the entire scroll content at one tenth scale — layout stayed correct, which is
        // why ActualWidth looked right while the pixels did not.
        _scrollHost.ChangeView(null, 0, null, true);
        RefreshSettingsLayoutIfNeeded();
        if (settings && !_settingsIntroPlayed)
        {
            _settingsIntroPlayed = true;
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PlaySettingsEntrance);
        }
        else
        {
            Motion.PlayEntrance(settings ? _settingsView : _overviewView);
        }
        SetPanelRenderingSuspended(false);
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

                    RefreshSettingsLayoutIfNeeded();

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

        RefreshSettingsLayoutIfNeeded();
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
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PlayControlEditorEntrance);
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
            // ConfigureAwait(false): the SQLite read must not need the UI thread to resume, or
            // a close that waits on this task deadlocks against its own continuation.
            var history = await _metricStore
                .LoadHistoryAsync(DateTimeOffset.UtcNow.Subtract(historyWindow), CancellationToken.None)
                .ConfigureAwait(false);

            // Back to the UI thread explicitly for the view-model update.
            var applied = new TaskCompletionSource();
            if (DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _viewModel.LoadHistory(history);
                    _lastLoadedHistoryMinutes = (int)historyWindow.TotalMinutes;
                }
                finally
                {
                    applied.TrySetResult();
                }
            }))
            {
                await applied.Task.ConfigureAwait(false);
            }
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
        var hasZoomedPanels = _viewModel.DashboardPanels.Any(panel => panel.IsZoomed);
        _globalResetZoomButton.Visibility = hasZoomedPanels ? Visibility.Visible : Visibility.Collapsed;
        _globalResetZoomButton.Opacity = 1d;
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
        if (_summarySurfaceRef is not null)
        {
            _summarySurfaceRef.Background = ResolveBrush("SurfaceBrush", "#1C1F22");
            _summarySurfaceRef.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035");
        }

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
            Padding = new Thickness(16),
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
