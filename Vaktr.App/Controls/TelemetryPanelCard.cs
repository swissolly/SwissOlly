using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Vaktr.App.ViewModels;
using Vaktr.Core.Models;

namespace Vaktr.App.Controls;

public sealed class TelemetryPanelCard : UserControl
{
    public static readonly DependencyProperty PanelProperty =
        DependencyProperty.Register(
            nameof(Panel),
            typeof(MetricPanelViewModel),
            typeof(TelemetryPanelCard),
            new PropertyMetadata(null, OnPanelChanged));

    private readonly Border _cardBorder;
    private readonly Border _badgeBorder;
    private readonly Border _edgeGlow;
    private readonly Grid _badgeIconHost;
    private readonly TextBlock _footerText;
    private readonly TextBlock _scaleText;
    private readonly TextBlock _titleText;
    private readonly TextBlock _currentValueText;
    private readonly TextBlock _secondaryValueText;
    private readonly TelemetryChart _chart;
    private readonly UsageGauge _gauge;
    private readonly Grid _visualGrid;
    private readonly Border _chartFrame;
    private readonly Border _rangeShell;
    private readonly Border _scalePill;
    private readonly StackPanel _legendHost;
    private readonly ScrollViewer _legendScroller;
    private readonly Border _processSection;
    private readonly TextBlock _processSectionTitleText;
    private readonly TextBlock _processLabelText;
    private readonly TextBlock _activityLabelText;
    private readonly TextBlock _valueLabelText;
    private readonly StackPanel _processRowsHost;
    private readonly ScrollViewer _processScroller;
    private readonly ActionChip _oneMinuteButton;
    private readonly ActionChip _fiveMinuteButton;
    private readonly ActionChip _fifteenMinuteButton;
    private readonly ActionChip _oneHourButton;
    private readonly ActionChip _twoDayButton;
    private readonly ActionChip _fiveDayButton;
    private readonly ActionChip _sortHighestButton;
    private readonly ActionChip _sortLowestButton;
    private readonly ActionChip _sortNameButton;
    private readonly ActionChip _perProcessChartButton;
    private readonly Border _legendDivider;
    private readonly Dictionary<string, (ClickableBorder Row, Border InnerBorder, TextBlock NameText, TextBlock ValueText, Ellipse Dot)> _legendRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ClickableBorder Row, TextBlock NameText, TextBlock ValueText, TextBlock CaptionText, LinearGradientBrush FillBrush, Border RowBorder, Ellipse? ChartDot)> _processRowParts = new(StringComparer.OrdinalIgnoreCase);
    private bool _processScrollLoadQueued;
    private bool _processSectionRevealed;
    private MetricPanelViewModel? _observedPanel;
    private bool _refreshQueued;
    private bool _isDropTarget;
    private IReadOnlyList<ChartSeriesViewModel>? _lastRenderedSeries;
    private IReadOnlyList<ProcessListItemViewModel>? _lastRenderedProcessRows;
    private DateTimeOffset _lastRenderedWindowStartUtc;
    private DateTimeOffset _lastRenderedWindowEndUtc;
    private double _lastRenderedCeilingValue = double.NaN;
    private MetricUnit _lastRenderedUnit;
    private TimeRangePreset _lastRenderedRange;
    private ProcessSortMode _lastRenderedSortMode;
    private bool _lastRenderedZoomState;
    private bool _lastRenderedSupportsProcessTable;
    private bool _lastRenderedGaugeMode;
    private bool _isHeaderPointerDown;
    private bool _isHeaderDragActive;
    private FrameworkElement? _headerDragHandle;
    private readonly TranslateTransform _dragTranslate = new();
    private Windows.Foundation.Point _dragMouseStart;
    private TelemetryPanelCard? _activeDropTargetCard;
    private List<(TelemetryPanelCard Card, Windows.Foundation.Rect Bounds)>? _dragTargetCache;
    private long _lastSwapTicks;
    private bool _isEffectivelyVisible = true;
    private bool _isRenderingSuspended;
    private bool _deferredRefreshPending;

    public event EventHandler<TimeRangePresetRequestedEventArgs>? RangePresetRequested;

    public event EventHandler<ChartZoomSelectionEventArgs>? PanelZoomSelectionRequested;

    public event EventHandler? PanelZoomResetRequested;


    public event EventHandler<PanelReorderRequestedEventArgs>? PanelReorderRequested;

    public event EventHandler? PanelDragEnded;

    public bool IsDragging => _isHeaderDragActive;

    public TelemetryPanelCard()
    {
        MinHeight = 388;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        RenderTransform = _dragTranslate;

        _badgeIconHost = new Grid
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _footerText = CreateTextBlock(fontSize: 10);
        _footerText.CharacterSpacing = 55;
        _footerText.Opacity = 0.72;
        _scaleText = CreateTextBlock("Bahnschrift", 9.5, FontWeights.Normal);
        _scaleText.TextWrapping = TextWrapping.NoWrap;
        _scaleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _scaleText.MaxWidth = 200;
        _titleText = CreateTextBlock("Segoe UI Variable Display", 15.5, FontWeights.Medium);
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _titleText.TextWrapping = TextWrapping.WrapWholeWords;
        _titleText.MaxLines = 2;
        _titleText.Opacity = 0.94;
        _currentValueText = CreateTextBlock("Bahnschrift", 28, FontWeights.SemiBold);
        _currentValueText.CharacterSpacing = -12;
        _secondaryValueText = CreateTextBlock(fontSize: 11.5);
        _secondaryValueText.Opacity = 0.78;

        _badgeBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(0.9),
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            Child = _badgeIconHost,
        };

        _edgeGlow = new Border
        {
            Width = 0,
            Height = 184,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-2, 6, 0, 6),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _oneMinuteButton = CreateRangeButton("1m", TimeRangePreset.OneMinute);
        _fiveMinuteButton = CreateRangeButton("5m", TimeRangePreset.FiveMinutes);
        _fifteenMinuteButton = CreateRangeButton("15m", TimeRangePreset.FifteenMinutes);
        _oneHourButton = CreateRangeButton("1h", TimeRangePreset.OneHour);
        _twoDayButton = CreateRangeButton("2d", TimeRangePreset.TwoDays);
        _fiveDayButton = CreateRangeButton("5d", TimeRangePreset.FiveDays);

        _visualGrid = new Grid
        {
            ColumnSpacing = 18,
        };
        _visualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _visualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _gauge = new UsageGauge
        {
            Width = 156,
            Height = 156,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _chart = new TelemetryChart
        {
            Height = 188,
            MinHeight = 188,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _chart.ZoomSelectionRequested += OnChartZoomSelectionRequested;
        _chart.ZoomResetRequested += OnChartZoomResetRequested;

        _chartFrame = new Border
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4, 4, 4, 3),
            Child = _chart,
        };

        _visualGrid.Children.Add(_gauge);
        _visualGrid.Children.Add(_chartFrame);
        Grid.SetColumn(_chartFrame, 1);

        _legendHost = new StackPanel
        {
            Spacing = 4,
        };
        _legendDivider = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 0, 8, 0),
            Background = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            Opacity = 0.3,
            IsHitTestVisible = false,
        };
        _legendScroller = new ScrollViewer
        {
            MaxHeight = 400,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            Content = _legendHost,
        };
        _legendScroller.PointerWheelChanged += (sender, e) =>
        {
            if (sender is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                e.Handled = true;
            }
        };

        _sortHighestButton = CreateProcessSortButton("Highest", ProcessSortMode.Highest);
        _sortLowestButton = CreateProcessSortButton("Lowest", ProcessSortMode.Lowest);
        _sortNameButton = CreateProcessSortButton("Name", ProcessSortMode.Name);
        _perProcessChartButton = new ActionChip { Text = "Chart", MinHeight = 26, MinWidth = 36 };
        _perProcessChartButton.Click += OnPerProcessChartToggle;

        _processSectionTitleText = CreateTextBlock("Segoe UI Variable Text", 11, FontWeights.Medium);
        _processRowsHost = new StackPanel
        {
            Spacing = 0,
        };
        // Click handlers are attached per-row in the slow path of RefreshProcessRows
        _processScroller = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            Content = _processRowsHost,
        };

        var processHeader = new Grid
        {
            ColumnSpacing = 10,
        };
        processHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        processHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        processHeader.Children.Add(_processSectionTitleText);

        var sortHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                _sortHighestButton,
                _sortLowestButton,
                _sortNameButton,
            },
        };
        processHeader.Children.Add(sortHost);
        Grid.SetColumn(sortHost, 1);

        var processColumns = new Grid
        {
            ColumnSpacing = 6,
            Padding = new Thickness(6, 0, 6, 4),
        };
        processColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        processColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        processColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        processColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _processLabelText = CreateTextBlock(fontSize: 9);
        _processLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _processLabelText.CharacterSpacing = 100;
        _processLabelText.Opacity = 0.6;
        _processLabelText.Text = "PROCESS";
        processColumns.Children.Add(_processLabelText);

        _activityLabelText = CreateTextBlock(fontSize: 9);
        _activityLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _activityLabelText.CharacterSpacing = 100;
        _activityLabelText.Opacity = 0.6;
        _activityLabelText.Text = "DETAILS";
        processColumns.Children.Add(_activityLabelText);
        Grid.SetColumn(_activityLabelText, 1);

        _valueLabelText = CreateTextBlock(fontSize: 9);
        _valueLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _valueLabelText.CharacterSpacing = 100;
        _valueLabelText.Opacity = 0.6;
        _valueLabelText.Text = "VALUE";
        _valueLabelText.HorizontalAlignment = HorizontalAlignment.Right;
        processColumns.Children.Add(_valueLabelText);
        Grid.SetColumn(_valueLabelText, 3);

        // Capture scroll events — prevent bubbling to main shell scroll, but let through at edges
        _processScroller.PointerWheelChanged += (sender, e) =>
        {
            // Always capture scroll wheel when the process list has scrollable content.
            // This prevents the wheel event from bubbling to the parent ScrollViewer
            // and scrolling the entire page when the user is over the process list.
            if (sender is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                e.Handled = true;
            }
        };

        // Load more processes when user scrolls near the bottom
        _processScroller.ViewChanged += (_, _) =>
        {
            if (_processScrollLoadQueued || Panel is null)
            {
                return;
            }

            var scrollableHeight = _processScroller.ScrollableHeight;
            if (scrollableHeight <= 0)
            {
                return;
            }

            // When within 20px of the bottom, load more
            if (_processScroller.VerticalOffset >= scrollableHeight - 20)
            {
                _processScrollLoadQueued = true;
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    _processScrollLoadQueued = false;
                    if (Panel is { } panel && !panel.ProcessListExpanded)
                    {
                        panel.ProcessListExpanded = true;
                    }
                });
            }
        };

        _processSection = new Border
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 11, 13, 11),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    processHeader,
                    processColumns,
                    _processScroller,
                },
            },
        };

        var rangeHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                _oneMinuteButton,
                _fiveMinuteButton,
                _fifteenMinuteButton,
                _oneHourButton,
                _twoDayButton,
                _fiveDayButton,
            },
        };

        _rangeShell = new Border
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4, 4, 4, 4),
            Child = rangeHost,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // Top row: badge + title inline, scale pill on the right
        var titleRow = new Grid { ColumnSpacing = 10 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(_badgeBorder);
        titleRow.Children.Add(_titleText);
        Grid.SetColumn(_titleText, 1);
        _titleText.VerticalAlignment = VerticalAlignment.Center;

        _scalePill = new Border
        {
            Background = ResolveBrush("PanelOverlayBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.7),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 3, 10, 3),
            Child = _scaleText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78,
        };
        titleRow.Children.Add(_scalePill);
        Grid.SetColumn(_scalePill, 2);

        // Value row: large value + secondary on the same line
        var valueRow = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                _currentValueText,
                _secondaryValueText,
            },
        };
        _secondaryValueText.Padding = new Thickness(1, 0, 0, 0);

        var headerContent = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(0, 0, 0, 4),
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            Children =
            {
                titleRow,
                valueRow,
                _footerText,
            },
        };
        var headerGrid = new Grid
        {
            ColumnSpacing = 12,
        };
        headerGrid.Background = BrushFactory.CreateBrush("#00FFFFFF");
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ManipulationMode = ManipulationModes.None;
        headerGrid.Children.Add(headerContent);
        headerGrid.Children.Add(_rangeShell);
        Grid.SetColumn(_rangeShell, 1);
        headerGrid.PointerPressed += OnHeaderPointerPressed;
        headerGrid.PointerMoved += OnHeaderPointerMoved;
        headerGrid.PointerReleased += OnHeaderPointerReleased;
        headerGrid.PointerCaptureLost += OnHeaderPointerCaptureLost;
        _headerDragHandle = headerGrid;

        _cardBorder = new Border
        {
            Background = CreateSurfaceGradient("#101B2D", "#16273D"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.9),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 16, 18, 18),
            Child = new Grid
            {
                Children =
                {
                    _edgeGlow,
                    new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            headerGrid,
                            _visualGrid,
                            _legendDivider,
                            _legendScroller,
                            _processSection,
                        },
                    },
                },
            },
        };
        _cardBorder.PointerEntered += (_, _) => SetHoverState(true);
        _cardBorder.PointerExited += (_, _) => SetHoverState(false);
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        Content = _cardBorder;
        Opacity = 0;
        var entrancePlayed = false;
        Loaded += (_, _) =>
        {
            RefreshFromPanel();
            if (!entrancePlayed)
            {
                entrancePlayed = true;
                PlayEntranceAnimation();
            }
            else
            {
                Opacity = 1;
            }
        };
    }

    public MetricPanelViewModel? Panel
    {
        get => (MetricPanelViewModel?)GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    private static void OnPanelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (TelemetryPanelCard)dependencyObject;
        control.DetachPanel(args.OldValue as MetricPanelViewModel);
        control.AttachPanel(args.NewValue as MetricPanelViewModel);
        control.RefreshFromPanel();
    }

    private void AttachPanel(MetricPanelViewModel? panel)
    {
        _observedPanel = panel;
        if (panel is not null)
        {
            panel.PropertyChanged += OnPanelPropertyChanged;
        }
    }

    private void DetachPanel(MetricPanelViewModel? panel)
    {
        if (panel is null)
        {
            return;
        }

        panel.PropertyChanged -= OnPanelPropertyChanged;
        if (ReferenceEquals(_observedPanel, panel))
        {
            _observedPanel = null;
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRenderingSuspended || (!_isEffectivelyVisible && !IsPriorityPanelProperty(e.PropertyName)))
        {
            _deferredRefreshPending = true;
            return;
        }

        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _refreshQueued = false;
            RefreshFromPanel();
        });
    }

    private void RefreshFromPanel()
    {
        var panel = Panel;
        if (panel is null)
        {
            UpdateBadgeIcon(null);
            _footerText.Text = string.Empty;
            _scaleText.Text = string.Empty;
            _scalePill.Visibility = Visibility.Collapsed;
            _titleText.Text = "Telemetry";
            _currentValueText.Text = "Waiting";
            _secondaryValueText.Text = "Collecting hardware samples";
            _chart.Series = Array.Empty<ChartSeriesViewModel>();
            _chart.CeilingValue = 0d;
            _chart.EmptyStateText = "Waiting for samples";
            _legendHost.Children.Clear();
            _legendRows.Clear();
            _legendScroller.Visibility = Visibility.Collapsed;
            _processRowsHost.Children.Clear();
            _processRowParts.Clear();
            _processSection.Visibility = Visibility.Collapsed;
            RefreshRangeButtons(null);
            RefreshProcessSortButtons(null);
            RefreshVisualMode(null);
            _lastRenderedSeries = null;
            _lastRenderedProcessRows = null;
            _lastRenderedWindowStartUtc = default;
            _lastRenderedWindowEndUtc = default;
            _lastRenderedCeilingValue = double.NaN;
            return;
        }

        UpdateBadgeIcon(panel);
        _footerText.Text = panel.IsZoomed ? $"\u23F8 {panel.FooterText}" : panel.FooterText;
        _footerText.Opacity = panel.IsZoomed ? 0.85 : 0.6;
        _footerText.Foreground = panel.IsZoomed
            ? ResolveBrush("AccentBrush", "#66E7FF")
            : ResolveBrush("TextMutedBrush", "#7D9AB6");
        _titleText.Text = panel.Title;
        _currentValueText.Text = panel.CurrentValue;
        _secondaryValueText.Text = panel.SecondaryValue;
        _scaleText.Text = panel.ScaleLabel;
        _chart.EmptyStateText = panel.EmptyStateText;
        _scalePill.Visibility = string.IsNullOrWhiteSpace(panel.ScaleLabel) ? Visibility.Collapsed : Visibility.Visible;

        if (!_isEffectivelyVisible)
        {
            ApplyPalette(panel);
            return;
        }

        var chartChanged =
            !ReferenceEquals(_lastRenderedSeries, panel.VisibleSeries) ||
            _lastRenderedUnit != panel.Unit ||
            _lastRenderedWindowStartUtc != panel.WindowStartUtc ||
            _lastRenderedWindowEndUtc != panel.WindowEndUtc ||
            !double.Equals(_lastRenderedCeilingValue, panel.ChartCeilingValue);
        if (chartChanged)
        {
            _chart.Series = panel.VisibleSeries;
            _chart.Unit = panel.Unit;
            _chart.WindowStartUtc = panel.WindowStartUtc;
            _chart.WindowEndUtc = panel.WindowEndUtc;
            _chart.CeilingValue = panel.ChartCeilingValue;
            _lastRenderedSeries = panel.VisibleSeries;
            _lastRenderedUnit = panel.Unit;
            _lastRenderedWindowStartUtc = panel.WindowStartUtc;
            _lastRenderedWindowEndUtc = panel.WindowEndUtc;
            _lastRenderedCeilingValue = panel.ChartCeilingValue;
            RefreshLegend(panel);
        }

        _gauge.Value = panel.GaugeValue;
        _gauge.AccentBrush = ResolveUtilizationBrush(panel);
        _gauge.Caption = panel.PrefersGaugeVisual ? "Capacity" : "Live";

        if (!ReferenceEquals(_lastRenderedProcessRows, panel.ProcessRows))
        {
            RefreshProcessRows(panel);
            _lastRenderedProcessRows = panel.ProcessRows;
        }

        if (_lastRenderedRange != panel.SelectedRange || _lastRenderedZoomState != panel.IsZoomed)
        {
            RefreshRangeButtons(panel);
            _lastRenderedRange = panel.SelectedRange;
            _lastRenderedZoomState = panel.IsZoomed;
        }

        if (_lastRenderedSortMode != panel.ProcessSortMode)
        {
            RefreshProcessSortButtons(panel);
            _lastRenderedSortMode = panel.ProcessSortMode;
        }

        if (_lastRenderedSupportsProcessTable != panel.SupportsProcessTable || _lastRenderedGaugeMode != panel.PrefersGaugeVisual)
        {
            RefreshVisualMode(panel);
            _lastRenderedSupportsProcessTable = panel.SupportsProcessTable;
            _lastRenderedGaugeMode = panel.PrefersGaugeVisual;
        }

        ApplyPalette(panel);
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var viewport = args.EffectiveViewport;
        var isVisible = viewport.Width > 0 && viewport.Height > 0;
        if (_isEffectivelyVisible == isVisible)
        {
            return;
        }

        _isEffectivelyVisible = isVisible;
        if (_isEffectivelyVisible && _deferredRefreshPending)
        {
            _deferredRefreshPending = false;
            RefreshFromPanel();
        }
    }

    public void SetRenderingSuspended(bool suspended)
    {
        if (_isRenderingSuspended == suspended)
        {
            return;
        }

        _isRenderingSuspended = suspended;
        if (!_isRenderingSuspended && _deferredRefreshPending)
        {
            _deferredRefreshPending = false;
            RefreshFromPanel();
        }
    }

    /// <summary>Optimizes the panel for window resize — collapses heavy elements and flattens corners.</summary>
    public void SetResizeMode(bool resizing)
    {
        _cardBorder.CornerRadius = new CornerRadius(resizing ? 4 : 18);
        _chartFrame.CornerRadius = new CornerRadius(resizing ? 2 : 12);

        // Suspend chart/gauge rendering only during resize
        _chart.SetRenderingSuspended(resizing);
        _gauge.SetRenderingSuspended(resizing);

        // Collapse charts and legends during resize so WinUI's layout engine
        // skips measuring them entirely — this is the biggest layout cost saver
        _chart.Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;
        _legendScroller.Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;
        _processSection.Visibility = resizing ? Visibility.Collapsed
            : Panel?.SupportsProcessTable == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsPriorityPanelProperty(string? propertyName)
    {
        return string.Equals(propertyName, nameof(MetricPanelViewModel.CurrentValue), StringComparison.Ordinal) ||
               string.Equals(propertyName, nameof(MetricPanelViewModel.SecondaryValue), StringComparison.Ordinal) ||
               string.Equals(propertyName, nameof(MetricPanelViewModel.FooterText), StringComparison.Ordinal) ||
               string.Equals(propertyName, nameof(MetricPanelViewModel.ScaleLabel), StringComparison.Ordinal);
    }

    public void RefreshThemeResources()
    {
        // Use theme resource brushes where possible — they update in-place when theme changes.
        // Only use CreateSurfaceGradient for elements that need distinct gradients.
        var strokeBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
        var mutedBrush = ResolveBrush("TextMutedBrush", "#7D9AB6");
        var cardBg = CreateSurfaceGradient("#101B2D", "#16273D");

        _cardBorder.Background = cardBg;
        _cardBorder.BorderBrush = strokeBrush;
        _cardBorder.BorderThickness = new Thickness(0.9);
        _cardBorder.Opacity = 1.0;
        _cardBorder.Translation = new System.Numerics.Vector3(0f, 0f, 0f);
        _rangeShell.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
        _rangeShell.BorderBrush = strokeBrush;
        _scalePill.Background = ResolveBrush("PanelOverlayBrush", "#091321");
        _scalePill.BorderBrush = strokeBrush;
        _chartFrame.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
        _chartFrame.BorderBrush = strokeBrush;
        _processSection.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
        _processSection.BorderBrush = strokeBrush;
        _legendDivider.Background = strokeBrush;
        _legendScroller.Background = null;
        _processScroller.Background = null;
        _processLabelText.Foreground = mutedBrush;
        _activityLabelText.Foreground = mutedBrush;
        _valueLabelText.Foreground = mutedBrush;

        _badgeBorder.Background = ResolveBrush("SurfaceInsetBrush", "#091321");

        _oneMinuteButton.RefreshThemeResources();
        _fiveMinuteButton.RefreshThemeResources();
        _fifteenMinuteButton.RefreshThemeResources();
        _oneHourButton.RefreshThemeResources();
        _twoDayButton.RefreshThemeResources();
        _fiveDayButton.RefreshThemeResources();
        _sortHighestButton.RefreshThemeResources();
        _sortLowestButton.RefreshThemeResources();
        _sortNameButton.RefreshThemeResources();
        _perProcessChartButton.RefreshThemeResources();

        var legendBg = ResolveBrush("SurfaceElevatedBrush", "#112033");
        var secondaryBrush = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        var primaryBrush = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        foreach (var rowParts in _legendRows.Values)
        {
            rowParts.InnerBorder.Background = legendBg;
            rowParts.InnerBorder.BorderBrush = strokeBrush;
            rowParts.NameText.Foreground = secondaryBrush;
            rowParts.ValueText.Foreground = primaryBrush;
        }

        foreach (var rowParts in _processRowParts.Values)
        {
            rowParts.NameText.Foreground = primaryBrush;
            rowParts.ValueText.Foreground = primaryBrush;
            rowParts.CaptionText.Foreground = mutedBrush;
        }

        if (Panel is { } panel)
        {
            UpdateBadgeIcon(panel);
            ApplyPalette(panel);
            // Re-apply range/sort button states so active highlights use new theme colors
            RefreshRangeButtons(panel);
            RefreshProcessSortButtons(panel);
        }
        else
        {
            _footerText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            _scaleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
            _titleText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
            _currentValueText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
            _secondaryValueText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
            _processSectionTitleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        }

        _chart.RefreshThemeResources();
        _gauge.RefreshThemeResources();
    }

    private void RefreshLegend(MetricPanelViewModel panel)
    {
        if (panel.VisibleSeries.Count == 0)
        {
            _legendRows.Clear();
            _legendHost.Children.Clear();
            _legendScroller.Visibility = Visibility.Collapsed;
            return;
        }

        _legendScroller.Visibility = panel.SupportsProcessTable ? Visibility.Collapsed : Visibility.Visible;

        if (_legendRows.Count == 0 && _legendHost.Children.Count > 0)
        {
            _legendHost.Children.Clear();
        }

        var activeKeys = new HashSet<string>(panel.VisibleSeries.Select(series => series.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _legendRows.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _legendHost.Children.Remove(_legendRows[staleKey].Row);
            _legendRows.Remove(staleKey);
        }

        var orderedRows = new List<UIElement>(panel.VisibleSeries.Count);
        for (var index = 0; index < panel.VisibleSeries.Count; index++)
        {
            var series = panel.VisibleSeries[index];
            var value = series.Points.Count == 0
                ? "--"
                : FormatValue(series.Points[^1].Value, panel.Unit);

            if (!_legendRows.TryGetValue(series.Name, out var rowParts))
            {
                rowParts = CreateLegendRow(series.Name, series.StrokeBrush);
                _legendRows.Add(series.Name, rowParts);
                _legendHost.Children.Add(rowParts.Row);

                // Click legend row to isolate/focus that series on the chart
                var capturedKey = series.Key;
                rowParts.Row.Tapped += (_, _) => Panel?.ToggleSeriesFocus(capturedKey);
            }

            // Dim unfocused legend rows when a series is focused
            var isFocused = panel.FocusedSeriesKey is null ||
                string.Equals(series.Key, panel.FocusedSeriesKey, StringComparison.OrdinalIgnoreCase);
            rowParts.Row.Opacity = isFocused ? 1.0 : 0.35;

            rowParts.ValueText.Text = value;
            orderedRows.Add(rowParts.Row);
        }

        if (_legendHost.Children.Count != orderedRows.Count ||
            !_legendHost.Children.Cast<UIElement>().SequenceEqual(orderedRows))
        {
            _legendHost.Children.Clear();
            foreach (var row in orderedRows)
            {
                _legendHost.Children.Add(row);
            }
        }
    }

    private void RefreshProcessRows(MetricPanelViewModel panel)
    {
        if (!panel.SupportsProcessTable)
        {
            _processRowsHost.Children.Clear();
            _processRowParts.Clear();

            return;
        }

        _processSectionTitleText.Text = panel.ProcessTableTitle;
        if (panel.ProcessRows.Count == 0)
        {
            _processRowParts.Clear();
            _processRowsHost.Children.Clear();
            _processRowsHost.Children.Add(CreateEmptyText("Process table warming up"));

            return;
        }

        // Fast path: if all rows already exist as UI elements, just reorder + update in-place.
        // Skip fast path if a pin state changed (need full rebuild for bold/dot visuals).
        var rows = panel.ProcessRows;
        var forceRebuild = panel._forceProcessRowRebuild;
        if (forceRebuild) panel._forceProcessRowRebuild = false; // consume the flag
        var allExist = !forceRebuild && rows.Count <= _processRowParts.Count;
        if (allExist)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (!_processRowParts.ContainsKey(rows[i].Key))
                {
                    allExist = false;
                    break;
                }
            }
        }

        if (allExist && rows.Count > 0)
        {
            var requiresReorder = _processRowsHost.Children.Count != rows.Count;
            for (var i = 0; i < rows.Count; i++)
            {
                var item = rows[i];
                var rp = _processRowParts[item.Key];
                rp.NameText.Text = item.Name;
                rp.ValueText.Text = item.Value;
                rp.CaptionText.Text = item.Caption;
                rp.Row.Tag = item.Key;
                var intensity = Math.Clamp(item.Intensity, 0d, 1d);
                if (rp.FillBrush.GradientStops.Count >= 3)
                {
                    rp.FillBrush.GradientStops[1].Offset = intensity;
                    rp.FillBrush.GradientStops[2].Offset = Math.Min(intensity + 0.03, 1.0);
                }

                if (!requiresReorder)
                {
                    requiresReorder =
                        _processRowsHost.Children[i] is not FrameworkElement current ||
                        !string.Equals(current.Tag as string, item.Key, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (requiresReorder)
            {
                _processRowsHost.Children.Clear();
                for (var i = 0; i < rows.Count; i++)
                {
                    _processRowsHost.Children.Add(_processRowParts[rows[i].Key].Row);
                }
            }

            // Remove entries for rows no longer shown
            if (_processRowParts.Count > rows.Count)
            {
                var activeKeys = new HashSet<string>(rows.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows) activeKeys.Add(r.Key);
                foreach (var key in _processRowParts.Keys.Where(k => !activeKeys.Contains(k)).ToArray())
                    _processRowParts.Remove(key);
            }

            return;
        }

        // Slow path: rebuild the row list (sort change, new/removed processes)
        _processRowsHost.Children.Clear();
        _processRowParts.Clear();

        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            var rowParts = CreateProcessRow(item, panel.AccentBrush);
            rowParts.Row.Tag = item.Key;
            _processRowParts.Add(item.Key, rowParts);
            _processRowsHost.Children.Add(rowParts.Row);

            // Track press/release to distinguish taps from scroll drags.
            // Use AddHandler with handledEventsToo so events fire even when
            // the parent ScrollViewer has already marked them as Handled.
            var processName = item.Name;
            Windows.Foundation.Point pressPos = default;
            rowParts.Row.AddHandler(PointerPressedEvent, new PointerEventHandler((sender, args) =>
            {
                pressPos = args.GetCurrentPoint((UIElement)sender).Position;
            }), true);
            rowParts.Row.AddHandler(PointerReleasedEvent, new PointerEventHandler((sender, args) =>
            {
                if (Panel is not { } p) return;
                var releasePos = args.GetCurrentPoint((UIElement)sender).Position;
                // Only count as a click if the pointer didn't move far (not a scroll)
                var dx = Math.Abs(releasePos.X - pressPos.X);
                var dy = Math.Abs(releasePos.Y - pressPos.Y);
                if (dx < 12 && dy < 12)
                {
                    p.ToggleProcessPin(processName);
                }
                args.Handled = true;
            }), true);
        }

    }

    private void RefreshVisualMode(MetricPanelViewModel? panel)
    {
        var showGauge = panel?.PrefersGaugeVisual == true;
        _gauge.Visibility = showGauge ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_chartFrame, showGauge ? 1 : 0);
        Grid.SetColumnSpan(_chartFrame, showGauge ? 1 : 2);
        _legendScroller.Visibility = panel?.SupportsProcessTable == true ? Visibility.Collapsed : Visibility.Visible;

        if (panel?.SupportsProcessTable == true)
        {
            if (!_processSectionRevealed)
            {
                // First reveal — animate smoothly from zero height
                _processSectionRevealed = true;
                _processSection.Visibility = Visibility.Visible;
                _processSection.Opacity = 0;
                _processSection.MaxHeight = 0;
                _processSection.RenderTransform = new TranslateTransform();

                // Defer animation to after layout measures the section
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

                    var heightAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 500,
                        Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                        EasingFunction = ease,
                    };

                    var fadeAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                        EasingFunction = ease,
                    };

                    var slideAnim = new DoubleAnimation
                    {
                        From = 6,
                        To = 0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                        EasingFunction = ease,
                    };

                    Storyboard.SetTarget(heightAnim, _processSection);
                    Storyboard.SetTargetProperty(heightAnim, "MaxHeight");
                    Storyboard.SetTarget(fadeAnim, _processSection);
                    Storyboard.SetTargetProperty(fadeAnim, "Opacity");
                    Storyboard.SetTarget(slideAnim, _processSection.RenderTransform);
                    Storyboard.SetTargetProperty(slideAnim, "Y");

                    var sb = new Storyboard();
                    sb.Children.Add(heightAnim);
                    sb.Children.Add(fadeAnim);
                    sb.Children.Add(slideAnim);
                    sb.Completed += (_, _) => _processSection.MaxHeight = double.PositiveInfinity;
                    sb.Begin();
                });
            }
            else
            {
                _processSection.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _processSection.Visibility = Visibility.Collapsed;
            _processSectionRevealed = false;
        }
    }

    private void RefreshRangeButtons(MetricPanelViewModel? panel)
    {
        var allowPresetHighlight = panel?.IsZoomed != true;
        ApplyRangeState(_oneMinuteButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.OneMinute);
        ApplyRangeState(_fiveMinuteButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.FiveMinutes);
        ApplyRangeState(_fifteenMinuteButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.FifteenMinutes);
        ApplyRangeState(_oneHourButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.OneHour);
        ApplyRangeState(_twoDayButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.TwoDays);
        ApplyRangeState(_fiveDayButton, allowPresetHighlight && panel?.SelectedRange == TimeRangePreset.FiveDays);
    }

    private void RefreshProcessSortButtons(MetricPanelViewModel? panel)
    {
        ApplyRangeState(_sortHighestButton, panel?.ProcessSortMode == ProcessSortMode.Highest);
        ApplyRangeState(_sortLowestButton, panel?.ProcessSortMode == ProcessSortMode.Lowest);
        var isNameSort = panel?.ProcessSortMode is ProcessSortMode.Name or ProcessSortMode.NameReverse;
        ApplyRangeState(_sortNameButton, isNameSort);
        _sortNameButton.Text = panel?.ProcessSortMode == ProcessSortMode.NameReverse ? "Name \u2191" : "Name \u2193";
    }

    private void ApplyPalette(MetricPanelViewModel panel)
    {
        _badgeBorder.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
        _footerText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _scaleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        _titleText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _secondaryValueText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        _processSectionTitleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");

        // Color-code current value, badge, and edge glow based on utilization thresholds
        var thresholdBrush = ResolveUtilizationBrush(panel);
        _badgeBorder.BorderBrush = thresholdBrush;
        _edgeGlow.Background = thresholdBrush;

        var util = panel.UtilizationPercent;
        if (util > 90)
        {
            _currentValueText.Foreground = ResolveBrush("CriticalBrush", "#FF9761");
        }
        else if (util > 75)
        {
            _currentValueText.Foreground = ResolveBrush("WarningBrush", "#F0C968");
        }
        else
        {
            _currentValueText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"); // default
        }
    }

    // Cached threshold brushes — avoids allocating new brushes on every utilization update
    private static Brush ResolveUtilizationBrush(MetricPanelViewModel panel)
    {
        if (panel.UtilizationPercent <= 0 || panel.Unit != MetricUnit.Percent)
        {
            return panel.AccentBrush;
        }

        if (panel.UtilizationPercent > 90)
        {
            return ResolveBrush("CriticalBrush", "#FF9761");
        }

        if (panel.UtilizationPercent > 75)
        {
            return ResolveBrush("WarningBrush", "#F0C968");
        }

        return panel.AccentBrush;
    }

    private void SetHoverState(bool isHovered)
    {
        if (_isDropTarget || _isHeaderDragActive)
        {
            return;
        }

        _cardBorder.BorderBrush = isHovered
            ? ResolveBrush("AccentStrongBrush", "#9FEFFF")
            : ResolveBrush("SurfaceStrokeBrush", "#27425E");
        _cardBorder.BorderThickness = new Thickness(isHovered ? 1.15 : 0.9);
        _cardBorder.Opacity = isHovered ? 1.0 : 0.985;
        _cardBorder.Background = isHovered
            ? CreateSurfaceGradient("#122034", "#1A2C46")
            : CreateSurfaceGradient("#101B2D", "#16273D");
        _cardBorder.Translation = new System.Numerics.Vector3(0f, isHovered ? -1f : 0f, 0f);
    }

    private ActionChip CreateRangeButton(string text, TimeRangePreset preset)
    {
        var button = new ActionChip
        {
            Tag = preset,
            MinHeight = 26,
            MinWidth = 34,
            Text = text,
        };

        button.Click += OnRangeClick;
        return button;
    }

    private ActionChip CreateProcessSortButton(string text, ProcessSortMode sortMode)
    {
        var button = new ActionChip
        {
            Tag = sortMode,
            MinHeight = 28,
            MinWidth = 58,
            Text = text,
        };

        button.Click += OnProcessSortClick;
        return button;
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Panel is null || IsInteractiveHeaderElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isHeaderPointerDown = true;
        _isHeaderDragActive = false;

        if (XamlRoot?.Content is UIElement root)
        {
            _dragMouseStart = e.GetCurrentPoint(root).Position;
        }

        this.CancelDirectManipulations();
        FindAncestor<ScrollViewer>(this)?.CancelDirectManipulations();
        if (sender is UIElement senderElement)
        {
            senderElement.CapturePointer(e.Pointer);
        }

        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHeaderPointerDown || XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        var rootPoint = e.GetCurrentPoint(root).Position;

        if (!_isHeaderDragActive)
        {
            var dx = rootPoint.X - _dragMouseStart.X;
            var dy = rootPoint.Y - _dragMouseStart.Y;
            if ((dx * dx) + (dy * dy) < 64d)
            {
                return;
            }

            _isHeaderDragActive = true;

            _cardBorder.BorderBrush = ResolveBrush("AccentStrongBrush", "#9FEFFF");
            _cardBorder.BorderThickness = new Thickness(2);
            _cardBorder.Background = CreateSurfaceGradient("#122034", "#1A2C46");
            _cardBorder.Opacity = 0.92;
            _cardBorder.Translation = new System.Numerics.Vector3(0f, -2f, 0f);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            Canvas.SetZIndex(this, 50);
            CacheDragTargets();
        }

        // Pure mouse delta — card moves exactly with the mouse, no feedback loops
        _dragTranslate.X = rootPoint.X - _dragMouseStart.X;
        _dragTranslate.Y = rootPoint.Y - _dragMouseStart.Y;

        // When pointer enters a different panel, swap positions (with cooldown to prevent chain-swaps)
        var targetCard = ResolveDropTargetCard(e);
        var now = Environment.TickCount64;

        if (!ReferenceEquals(targetCard, _activeDropTargetCard))
        {
            _activeDropTargetCard?.SetDropTargetState(false);

            if (targetCard is not null && Panel is not null && (now - _lastSwapTicks) > 250)
            {
                _activeDropTargetCard = targetCard;
                targetCard.SetDropTargetState(true);

                var targetKey = targetCard.Panel?.PanelKey;
                if (!string.IsNullOrWhiteSpace(targetKey))
                {
                    _lastSwapTicks = now;
                    PanelReorderRequested?.Invoke(this, new PanelReorderRequestedEventArgs(Panel.PanelKey, targetKey));
                    // Recache after the grid repositions
                    _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        if (_isHeaderDragActive)
                        {
                            CacheDragTargets();
                        }
                    });
                }
            }
            else
            {
                _activeDropTargetCard = null;
            }
        }

        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHeaderPointerDown)
        {
            return;
        }

        EndHeaderDragInteraction();
        if (sender is UIElement senderElement)
        {
            senderElement.ReleasePointerCaptures();
        }

        e.Handled = true;
    }

    private void OnHeaderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isHeaderPointerDown)
        {
            EndHeaderDragInteraction();
        }
    }

    private void OnRangeClick(object? sender, EventArgs e)
    {
        if (sender is not ActionChip { Tag: TimeRangePreset preset })
        {
            return;
        }

        if (RangePresetRequested is not null)
        {
            RangePresetRequested.Invoke(this, new TimeRangePresetRequestedEventArgs(preset));
            return;
        }

        Panel?.ApplyRangePreset(preset);
    }

    private void OnPerProcessChartToggle(object? sender, EventArgs e)
    {
        if (Panel is { } panel)
        {
            panel.PerProcessChartsEnabled = !panel.PerProcessChartsEnabled;
            _perProcessChartButton.IsActive = panel.PerProcessChartsEnabled;
        }
    }

    private void OnProcessSortClick(object? sender, EventArgs e)
    {
        if (sender is not ActionChip { Tag: ProcessSortMode sortMode })
        {
            return;
        }

        if (Panel is { } panel)
        {
            // Toggle Name → NameReverse on second click
            if (sortMode == ProcessSortMode.Name && panel.ProcessSortMode == ProcessSortMode.Name)
            {
                sortMode = ProcessSortMode.NameReverse;
            }
            else if (sortMode == ProcessSortMode.Name && panel.ProcessSortMode == ProcessSortMode.NameReverse)
            {
                sortMode = ProcessSortMode.Name;
            }

            panel.ProcessSortMode = sortMode;
            RefreshProcessSortButtons(panel);
        }
    }

    private void OnChartZoomSelectionRequested(object? sender, ChartZoomSelectionEventArgs e)
    {
        if (PanelZoomSelectionRequested is not null)
        {
            PanelZoomSelectionRequested.Invoke(this, e);
            return;
        }

        Panel?.ZoomToWindow(e.StartUtc, e.EndUtc);
    }

    private void OnChartZoomResetRequested(object? sender, EventArgs e)
    {
        if (PanelZoomResetRequested is not null)
        {
            PanelZoomResetRequested.Invoke(this, e);
            return;
        }

        Panel?.ResetZoom();
    }

    public void PlaySwapSettleAnimation()
    {
        // Quick opacity pulse — card briefly dims then returns, indicating it just moved
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeOut = new DoubleAnimation
        {
            From = 0.6,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = ease,
        };

        Storyboard.SetTarget(fadeOut, _cardBorder);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeOut);
        sb.Begin();
    }

    private void SetDropTargetState(bool isActive)
    {
        _isDropTarget = isActive;
        _cardBorder.BorderBrush = isActive
            ? ResolveBrush("AccentStrongBrush", "#9FEFFF")
            : ResolveBrush("SurfaceStrokeBrush", "#27425E");
        _cardBorder.BorderThickness = new Thickness(isActive ? 1.25 : 0.9);
        _cardBorder.Background = isActive
            ? CreateSurfaceGradient("#122034", "#1A2C46")
            : CreateSurfaceGradient("#101B2D", "#16273D");
        _cardBorder.Opacity = isActive ? 0.96 : 1.0;
        _cardBorder.Translation = new System.Numerics.Vector3(0f, isActive ? -1f : 0f, 0f);
    }

    private void CacheDragTargets()
    {
        _dragTargetCache = [];
        if (XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        foreach (var card in EnumeratePanelCards(root))
        {
            if (ReferenceEquals(card, this) || card.Panel is null)
            {
                continue;
            }

            if (TryGetBounds(card, root, out var bounds))
            {
                _dragTargetCache.Add((card, bounds));
            }
        }
    }

    private void UpdateDropTargetState(PointerRoutedEventArgs e)
    {
        var targetCard = ResolveDropTargetCard(e);
        if (ReferenceEquals(_activeDropTargetCard, targetCard))
        {
            return;
        }

        _activeDropTargetCard?.SetDropTargetState(false);
        _activeDropTargetCard = targetCard;
        _activeDropTargetCard?.SetDropTargetState(true);
    }

    private TelemetryPanelCard? ResolveDropTargetCard(PointerRoutedEventArgs e)
    {
        if (_dragTargetCache is null || _dragTargetCache.Count == 0 || XamlRoot?.Content is not UIElement root)
        {
            return null;
        }

        var hostPoint = e.GetCurrentPoint(root).Position;

        // Direct hit — pointer must be inside or very close to a card to trigger swap
        TelemetryPanelCard? nearestCard = null;
        var nearestDistance = double.MaxValue;

        foreach (var (card, bounds) in _dragTargetCache)
        {
            // Direct hit
            if (bounds.Contains(hostPoint))
            {
                return card;
            }

            // Within a small margin of the card edge (20px)
            var dx = Math.Max(0, Math.Max(bounds.X - hostPoint.X, hostPoint.X - (bounds.X + bounds.Width)));
            var dy = Math.Max(0, Math.Max(bounds.Y - hostPoint.Y, hostPoint.Y - (bounds.Y + bounds.Height)));
            var dist = (dx * dx) + (dy * dy);

            if (dist <= 400 && dist < nearestDistance)
            {
                nearestDistance = dist;
                nearestCard = card;
            }
        }

        return nearestCard;
    }

    private void EndHeaderDragInteraction()
    {
        var wasDragging = _isHeaderDragActive;
        _isHeaderPointerDown = false;
        _isHeaderDragActive = false;
        _activeDropTargetCard?.SetDropTargetState(false);
        _activeDropTargetCard = null;
        _dragTargetCache = null;

        if (wasDragging)
        {
            ProtectedCursor = null;
            _cardBorder.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            _cardBorder.BorderThickness = new Thickness(0.9);
            _cardBorder.Background = CreateSurfaceGradient("#101B2D", "#16273D");
            _cardBorder.Opacity = 1.0;
            _cardBorder.Translation = new System.Numerics.Vector3(0f, 0f, 0f);

            // Finalize grid position and clear translate instantly — no swoop
            PanelDragEnded?.Invoke(this, EventArgs.Empty);
            _dragTranslate.X = 0;
            _dragTranslate.Y = 0;

            // Subtle opacity pulse to confirm placement
            var opacityAnim = new DoubleAnimation
            {
                From = 0.7,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(opacityAnim, _cardBorder);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(opacityAnim);
            storyboard.Completed += (_, _) => Canvas.SetZIndex(this, 0);
            storyboard.Begin();
        }
        else
        {
            _dragTranslate.X = 0;
            _dragTranslate.Y = 0;
        }
    }

    private static bool IsInteractiveHeaderElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ActionChip)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : class
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static IEnumerable<TelemetryPanelCard> EnumeratePanelCards(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TelemetryPanelCard panelCard)
            {
                yield return panelCard;
            }

            foreach (var nested in EnumeratePanelCards(child))
            {
                yield return nested;
            }
        }
    }

    private static bool TryGetBounds(FrameworkElement element, UIElement root, out Windows.Foundation.Rect bounds)
    {
        bounds = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var transform = element.TransformToVisual(root);
            var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            bounds = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (ClickableBorder Row, Border InnerBorder, TextBlock NameText, TextBlock ValueText, Ellipse Dot) CreateLegendRow(string name, Brush strokeBrush)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 2, 8, 0),
            Fill = strokeBrush,
        };
        row.Children.Add(dot);

        var nameText = new TextBlock
        {
            FontSize = 11,
            Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1"),
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(nameText);
        Grid.SetColumn(nameText, 1);

        var valueText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            Text = "--",
            MinWidth = 42,
            TextAlignment = TextAlignment.Right,
        };
        row.Children.Add(valueText);
        Grid.SetColumn(valueText, 2);

        var border = new Border
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 7, 11, 7),
            Child = row,
        };

        // Hover reads current theme each time — no stale captured brushes
        border.PointerEntered += (_, _) =>
        {
            border.Background = ResolveBrush("SurfaceStrongBrush", "#18314A");
            border.BorderBrush = ResolveBrush("AccentStrongBrush", "#9FEFFF");
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
            border.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
        };

        var wrapper = new ClickableBorder(border);
        return (wrapper, border, nameText, valueText, dot);
    }

    private static (ClickableBorder Row, TextBlock NameText, TextBlock ValueText, TextBlock CaptionText, LinearGradientBrush FillBrush, Border RowBorder, Ellipse? ChartDot) CreateProcessRow(ProcessListItemViewModel item, Brush accentBrush)
    {
        // Small color dot showing the process chart line color (if charted)
        Ellipse? chartDot = null;
        var nameContent = (FrameworkElement)new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11.5,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            Text = item.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameText = (TextBlock)nameContent;

        // Bold name + color dot when pinned
        if (item.IsPinned)
        {
            nameText.FontWeight = FontWeights.SemiBold;
        }

        if (item.ChartColor is not null)
        {
            chartDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = item.ChartColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var nameStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { chartDot, nameText },
            };
            nameContent = nameStack;
        }

        var captionText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 9.5,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            Text = item.Caption,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var valueText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            Text = item.Value,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 46,
            TextAlignment = TextAlignment.Right,
        };

        // Table row: Name | Caption | Value
        var rowGrid = new Grid
        {
            ColumnSpacing = 6,
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        rowGrid.Children.Add(nameContent);
        rowGrid.Children.Add(captionText);
        Grid.SetColumn(captionText, 1);
        rowGrid.Children.Add(valueText);
        Grid.SetColumn(valueText, 2);

        // Background gradient fill — row itself acts as the meter (Grafana-style)
        var intensity = Math.Clamp(item.Intensity, 0d, 1d);
        var accentColor = accentBrush is SolidColorBrush scb ? scb.Color : BrushFactory.ParseColor("#66E7FF");
        var baseAlpha = item.IsPinned ? (byte)70 : (byte)55;
        var edgeAlpha = item.IsPinned ? (byte)50 : (byte)35;

        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(baseAlpha, accentColor.R, accentColor.G, accentColor.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(edgeAlpha, accentColor.R, accentColor.G, accentColor.B), Offset = intensity },
                new GradientStop { Color = Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B), Offset = Math.Min(intensity + 0.03, 1.0) },
            },
        };

        var rowBorder = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0, 0, 0, 0.4),
            Background = fillBrush,
            // Transparent background on the grid ensures the entire row is hit-testable
            Child = new Grid { Background = BrushFactory.CreateBrush("#01000000"), Children = { rowGrid } },
        };

        // Hover feedback — accent border + subtle background brighten
        rowBorder.PointerEntered += (_, _) =>
        {
            rowBorder.BorderBrush = ResolveBrush("AccentBrush", "#66E7FF");
            rowBorder.BorderThickness = new Thickness(0, 0, 0, 1.2);
            // Boost fill opacity on hover for clear visual feedback
            if (fillBrush.GradientStops.Count >= 3)
            {
                var c0 = fillBrush.GradientStops[0].Color;
                var c1 = fillBrush.GradientStops[1].Color;
                fillBrush.GradientStops[0].Color = Color.FromArgb(Math.Min((byte)120, (byte)(c0.A + 40)), c0.R, c0.G, c0.B);
                fillBrush.GradientStops[1].Color = Color.FromArgb(Math.Min((byte)100, (byte)(c1.A + 40)), c1.R, c1.G, c1.B);
            }
        };
        rowBorder.PointerExited += (_, _) =>
        {
            rowBorder.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            rowBorder.BorderThickness = new Thickness(0, 0, 0, 0.4);
            // Restore original fill opacity
            if (fillBrush.GradientStops.Count >= 3)
            {
                var c0 = fillBrush.GradientStops[0].Color;
                var c1 = fillBrush.GradientStops[1].Color;
                fillBrush.GradientStops[0].Color = Color.FromArgb(baseAlpha, c0.R, c0.G, c0.B);
                fillBrush.GradientStops[1].Color = Color.FromArgb(edgeAlpha, c1.R, c1.G, c1.B);
            }
        };

        // Wrap in ClickableBorder for hand cursor + click handlers in slow path
        var clickableRow = new ClickableBorder(rowBorder);
        return (clickableRow, nameText, valueText, captionText, fillBrush, rowBorder, chartDot);
    }

    private void UpdateBadgeIcon(MetricPanelViewModel? panel)
    {
        var iconKey = panel switch
        {
            null => "collection",
            { PanelKey: var key } when key.Contains("temperature", StringComparison.OrdinalIgnoreCase) => "temperature",
            { Category: MetricCategory.Cpu } => "cpu",
            { Category: MetricCategory.Gpu } => "gpu",
            { Category: MetricCategory.Memory } => "memory",
            { Category: MetricCategory.Disk, PrefersGaugeVisual: true } => "drive",
            { Category: MetricCategory.Disk } => "disk",
            { Category: MetricCategory.Network } => "network",
            { Category: MetricCategory.System } => "system",
            _ => "collection",
        };

        var accentBrush = panel?.AccentBrush ?? ResolveBrush("AccentBrush", "#66E7FF");
        _badgeIconHost.Children.Clear();
        _badgeIconHost.Children.Add(IconFactory.CreateIcon(iconKey, accentBrush, 16));
    }

    private static void ApplyRangeState(ActionChip control, bool isActive)
    {
        control.IsActive = isActive;
        control.IsFilled = isActive;
        control.Opacity = isActive ? 1 : 0.76;
    }

    private static TextBlock CreateTextBlock(string? fontFamily = null, double fontSize = 12, Windows.UI.Text.FontWeight? fontWeight = null) =>
        new()
        {
            FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? new FontFamily("Segoe UI Variable Text") : new FontFamily(fontFamily),
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.WrapWholeWords,
        };

    private static TextBlock CreateEmptyText(string text) =>
        new()
        {
            FontSize = 11,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            Text = text,
        };

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static Brush CreateSurfaceGradient(string startHex, string endHex) => ThemeVisuals.CreateSurfaceLiftGradient(startHex, endHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);

    private static string FormatValue(double value, MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => $"{value:0.#}%",
        MetricUnit.Celsius => $"{value:0.#} C",
        MetricUnit.Gigabytes when value >= 1024d => $"{value / 1024d:0.0} TiB",
        MetricUnit.Gigabytes => $"{value:0.0} GiB",
        MetricUnit.MegabytesPerSecond => $"{value:0.0} MB/s",
        MetricUnit.MegabitsPerSecond => $"{value:0.0} Mbps",
        MetricUnit.Megahertz => $"{value / 1000d:0.00} GHz",
        MetricUnit.Count when value >= 1_000_000d => $"{value / 1_000_000d:0.#}M",
        MetricUnit.Count when value >= 1_000d => $"{value / 1_000d:0.#}k",
        MetricUnit.Count => $"{value:0}",
        _ => $"{value:0.##}",
    };

    private void PlayEntranceAnimation()
    {
        var col = Grid.GetColumn(this);
        var row = Grid.GetRow(this);
        var staggerMs = (row * 3 + col) * 70 + 60;

        // Slide from the direction the card is positioned:
        // Cards on the left side of the window slide from left, right side from right
        var slideX = 0d;
        if (XamlRoot?.Content is UIElement root && ActualWidth > 0)
        {
            var cardCenter = TransformToVisual(root).TransformPoint(
                new Windows.Foundation.Point(ActualWidth / 2d, 0)).X;
            var windowCenter = root.ActualSize.X / 2d;
            slideX = cardCenter < windowCenter ? 3d : -3d;
        }
        var slideY = 2d;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var translate = new TranslateTransform { X = slideX, Y = slideY };
        _cardBorder.RenderTransform = translate;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            BeginTime = TimeSpan.FromMilliseconds(staggerMs),
            EasingFunction = ease,
        };

        var moveX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            BeginTime = TimeSpan.FromMilliseconds(staggerMs),
            EasingFunction = ease,
        };

        var moveY = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            BeginTime = TimeSpan.FromMilliseconds(staggerMs),
            EasingFunction = ease,
        };

        Storyboard.SetTarget(fadeIn, this);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        Storyboard.SetTarget(moveX, translate);
        Storyboard.SetTargetProperty(moveX, "X");
        Storyboard.SetTarget(moveY, translate);
        Storyboard.SetTargetProperty(moveY, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(moveX);
        storyboard.Children.Add(moveY);
        storyboard.Completed += (_, _) =>
        {
            _cardBorder.RenderTransform = null;
        };
        storyboard.Begin();
    }
}

public sealed class TimeRangePresetRequestedEventArgs : EventArgs
{
    public TimeRangePresetRequestedEventArgs(TimeRangePreset preset)
    {
        Preset = preset;
    }

    public TimeRangePreset Preset { get; }
}

public sealed class PanelReorderRequestedEventArgs : EventArgs
{
    public PanelReorderRequestedEventArgs(string sourcePanelKey, string targetPanelKey)
    {
        SourcePanelKey = sourcePanelKey;
        TargetPanelKey = targetPanelKey;
    }

    public string SourcePanelKey { get; }

    public string TargetPanelKey { get; }
}
