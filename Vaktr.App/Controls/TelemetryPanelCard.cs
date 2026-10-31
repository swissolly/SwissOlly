using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private static readonly (TimeRangePreset Preset, string ShortLabel, string MenuLabel)[] RangeChoices =
    [
        (TimeRangePreset.OneMinute, "1m", "1 minute"),
        (TimeRangePreset.FiveMinutes, "5m", "5 minutes"),
        (TimeRangePreset.FifteenMinutes, "15m", "15 minutes"),
        (TimeRangePreset.OneHour, "1h", "1 hour"),
        (TimeRangePreset.TwoDays, "2d", "2 days"),
        (TimeRangePreset.FiveDays, "5d", "5 days"),
    ];

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
    private readonly ActionChip _rangeButton;
    private readonly Dictionary<TimeRangePreset, SymbolIcon> _rangeMenuChecks = [];
    private readonly ActionChip _sortHighestButton;
    private readonly ActionChip _sortLowestButton;
    private readonly ActionChip _sortNameButton;
    private readonly ActionChip _perProcessChartButton;
    private readonly ActionChip _processToggleButton;
    private readonly ActionChip _sizeButton;
    private readonly Border _legendDivider;
    private readonly Dictionary<PanelSizePreset, SymbolIcon> _sizeMenuChecks = [];
    private readonly Dictionary<string, (ClickableBorder Row, Border InnerBorder, TextBlock NameText, TextBlock ValueText, Ellipse Dot)> _legendRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ClickableBorder Row, TextBlock NameText, TextBlock ValueText, TextBlock CaptionText, LinearGradientBrush FillBrush, Border RowBorder, Ellipse? ChartDot)> _processRowParts = new(StringComparer.OrdinalIgnoreCase);
    private bool _processScrollLoadQueued;
    private bool _processSectionExpanded;
    private MetricPanelViewModel? _observedPanel;
    private bool _refreshQueued;
    private string? _lastBadgeIconKey;
    private Brush? _lastBadgeAccentBrush;
    private IReadOnlyList<ChartSeriesViewModel>? _lastRenderedSeries;
    private IReadOnlyList<ProcessListItemViewModel>? _lastRenderedProcessRows;
    private PanelSizePreset _lastRenderedSizePreset = PanelSizePreset.Standard;
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
    private readonly ScaleTransform _dropSettleScale = new() { ScaleX = 1d, ScaleY = 1d };
    private Windows.Foundation.Point _dragMouseStart;
    private bool _isEffectivelyVisible = true;
    private bool _isRenderingSuspended;
    private bool _deferredRefreshPending;

    public event EventHandler<TimeRangePresetRequestedEventArgs>? RangePresetRequested;

    public event EventHandler<ChartZoomSelectionEventArgs>? PanelZoomSelectionRequested;

    public event EventHandler? PanelZoomResetRequested;



    public event EventHandler<PanelSizePresetRequestedEventArgs>? PanelSizePresetRequested;

    public event EventHandler? PanelDragEnded;

    /// <summary>Raised while a panel is being dragged, so the shell can preview the drop cell.</summary>
    public event EventHandler<PanelResizeEventArgs>? PanelDragMoved;

    /// <summary>Raised on pointer-down so the shell can capture the geometry to resize from.</summary>
    public event EventHandler<PanelResizeEventArgs>? PanelResizeStarted;

    /// <summary>Raised while a resize handle is dragged. Deltas are pixels, absolute from pointer-down.</summary>
    public event EventHandler<PanelResizeEventArgs>? PanelResizeDelta;

    /// <summary>Raised when a resize drag completes, so the shell can persist.</summary>
    public event EventHandler? PanelResizeCompleted;

    private Grid _resizeHandleLayer = null!;
    private bool _isLayoutEditMode;
    private ResizeEdge _activeResizeEdge = ResizeEdge.None;
    private Windows.Foundation.Point _resizeStart;

    /// <summary>Which edges a handle drags. Flags so a corner is just two edges combined.</summary>
    [Flags]
    public enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right,
    }

    public bool IsDragging => _isHeaderDragActive;

    /// <summary>
    /// Pixel offset the card has been dragged from its placed position. Valid inside the
    /// <see cref="PanelDragEnded"/> handler — the transform is cleared immediately after.
    /// </summary>
    public Windows.Foundation.Point DragOffset => new(_dragTranslate.X, _dragTranslate.Y);

    public TelemetryPanelCard()
    {
        // Height now comes from the panel's H in grid units, so the card stretches to fill the
        // rows it spans instead of sizing to its content. MinHeight only guards the smallest
        // allowed span (H=3) from collapsing below something usable.
        MinHeight = CardMinimumHeight;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        RenderTransform = _dragTranslate;

        _badgeIconHost = new Grid
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _footerText = CreateTextBlock(fontSize: 10);
        _footerText.Opacity = 0.72;
        _scaleText = CreateTextBlock("Segoe UI Variable Text", 10, FontWeights.Normal);
        _scaleText.TextWrapping = TextWrapping.NoWrap;
        _scaleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _scaleText.MaxWidth = 200;
        _titleText = CreateTextBlock("Segoe UI Variable Text", 13.5, FontWeights.SemiBold);
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _titleText.TextWrapping = TextWrapping.NoWrap;
        _titleText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");

        _currentValueText = CreateTextBlock("Segoe UI Variable Display", 30, FontWeights.SemiBold);
        _currentValueText.TextTrimming = TextTrimming.CharacterEllipsis;
        _currentValueText.TextWrapping = TextWrapping.NoWrap;
        _secondaryValueText = CreateTextBlock(fontSize: 11.5);
        _secondaryValueText.Opacity = 0.78;

        _badgeBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
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

        _rangeButton = new ActionChip
        {
            MinHeight = 30,
            MinWidth = 58,
            Text = "15m",
        };
        ToolTipService.SetToolTip(_rangeButton, "Choose this chart's time range");
        var rangeMenu = new MenuFlyout();
        foreach (var choice in RangeChoices)
        {
            var item = CreateCheckedMenuItem(choice.Preset, choice.MenuLabel, OnRangeClick, out var check);
            rangeMenu.Items.Add(item);
            _rangeMenuChecks.Add(choice.Preset, check);
        }
        FlyoutBase.SetAttachedFlyout(_rangeButton, rangeMenu);
        _rangeButton.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(_rangeButton);

        _sizeButton = new ActionChip { Text = "Size", MinHeight = 30, MinWidth = 54 };
        ToolTipService.SetToolTip(_sizeButton, "Choose this panel's size");
        var sizeMenu = new MenuFlyout();
        foreach (var (preset, label) in new[]
        {
            (PanelSizePreset.Compact, "Compact"),
            (PanelSizePreset.Standard, "Standard"),
            (PanelSizePreset.Wide, "Wide"),
            (PanelSizePreset.Large, "Large"),
        })
        {
            var item = CreateCheckedMenuItem(preset, label, OnPanelSizeClick, out var check);
            sizeMenu.Items.Add(item);
            _sizeMenuChecks.Add(preset, check);
        }
        FlyoutBase.SetAttachedFlyout(_sizeButton, sizeMenu);
        _sizeButton.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(_sizeButton);

        _visualGrid = new Grid
        {
            ColumnSpacing = 14,
        };
        _visualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _visualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _gauge = new UsageGauge
        {
            Width = 144,
            Height = 144,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _chart = new TelemetryChart
        {
            Height = 176,
            MinHeight = 176,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _chart.ZoomSelectionRequested += OnChartZoomSelectionRequested;
        _chart.ZoomResetRequested += OnChartZoomResetRequested;

        // The chart's own plot surface already paints an inset fill, so an outline here just
        // drew a second rectangle a few pixels outside the first one.
        _chartFrame = new Border
        {
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 4, 4, 3),
            Child = _chart,
        };
        ApplySizePreset(PanelSizePreset.Standard);

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
        _processToggleButton = new ActionChip { Text = "Processes", MinHeight = 30, MinWidth = 96 };
        ToolTipService.SetToolTip(_processToggleButton, "Show the top processes for this metric");
        _processToggleButton.Click += OnProcessSectionToggleClick;
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

        _processLabelText = CreateTextBlock(fontSize: 10);
        _processLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _processLabelText.Opacity = 0.6;
        _processLabelText.Text = "PROCESS";
        processColumns.Children.Add(_processLabelText);

        _activityLabelText = CreateTextBlock(fontSize: 10);
        _activityLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _activityLabelText.Opacity = 0.6;
        _activityLabelText.Text = "DETAILS";
        processColumns.Children.Add(_activityLabelText);
        Grid.SetColumn(_activityLabelText, 1);

        _valueLabelText = CreateTextBlock(fontSize: 10);
        _valueLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
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
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 8, 0, 0),
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

        var panelActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _processToggleButton,
                _sizeButton,
                _rangeButton,
            },
        };
        _rangeShell = new Border
        {
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Child = panelActions,
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

        // Caption, not a control. It states the chart's ceiling ("100% ceiling · 100.8k handles")
        // and was never clickable, so boxing it made it compete with the range chips beside it.
        _scalePill = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2, 3, 10, 3),
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
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            RenderTransform = _dropSettleScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            Child = new Grid
            {
                Children =
                {
                    _edgeGlow,
                    new StackPanel
                    {
                        Spacing = 12,
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

        // Handles live in an overlay above the card so they can be switched off entirely
        // outside edit mode. Visibility alone is not enough — an invisible-but-hit-testable
        // handle would still swallow the pointer events chart drag-zoom depends on.
        _resizeHandleLayer = BuildResizeHandleLayer();
        Content = new Grid
        {
            Children = { _cardBorder, _resizeHandleLayer },
        };
        Loaded += (_, _) => RefreshFromPanel();

        // Without this the card stays subscribed to its MetricPanelViewModel after the shell
        // drops it. The view models are permanent, so every toggled-off panel left an orphaned
        // card alive - still running RefreshFromPanel and rebuilding its chart on every scrape.
        Unloaded += (_, _) => DetachPanel(Panel);
    }

    /// <summary>
    /// Edit mode gates every layout affordance. Outside it the handle layer is collapsed *and*
    /// non-hit-testable, and the header drag is refused, so panels behave exactly as before this
    /// feature existed — which is what keeps chart drag-to-zoom working after a layout is saved.
    /// </summary>
    public void SetLayoutEditMode(bool editing)
    {
        _isLayoutEditMode = editing;
        _resizeHandleLayer.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _resizeHandleLayer.IsHitTestVisible = editing;

        _cardBorder.BorderBrush = editing
            ? ResolveBrush("AccentBrush", "#79B8FF")
            : ResolveBrush("SurfaceStrokeBrush", "#2C3035");

        // Charts must not react to the pointer while the board is being rearranged, or a drag
        // that starts inside a plot would zoom instead of move the panel.
        _chart.IsHitTestVisible = !editing;
    }

    // ---- Live resize preview -------------------------------------------------------------
    //
    // During a resize the card must not participate in grid layout. Re-spanning grid cells on
    // every pointer move re-measures this card, its chart, and every sibling - which is what
    // made resizing flicker and lurch. Instead the card is given an explicit pixel size and
    // pinned to the top-left of its cell, so it grows over its neighbours without moving them.
    // A cheap placeholder (owned by the shell) shows the snapped cell it will land in, and the
    // real layout is committed exactly once, on release.

    /// <summary>Smallest the card can render without clipping; must match PanelLayout.MinHeight * row unit.</summary>
    private const double CardMinimumHeight = 320;
    private const double CardMinimumWidth = 220;

    private bool _resizePreviewActive;
    private double _previewOriginWidth;
    private double _previewOriginHeight;

    /// <summary>Pixel size of the card as the drag begins, so the shell can offset from it.</summary>
    public Windows.Foundation.Size BeginResizePreview()
    {
        _previewOriginWidth = ActualWidth;
        _previewOriginHeight = ActualHeight;
        _resizePreviewActive = true;

        // Float above siblings and stop stretching, so an explicit size grows right/down from
        // this cell's origin rather than being overridden by the grid.
        Canvas.SetZIndex(this, 50);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Width = _previewOriginWidth;
        Height = _previewOriginHeight;

        // A chart redrawing mid-gesture is wasted work and the most visible source of flicker.
        _chart.SetRenderingSuspended(true);
        _gauge.SetRenderingSuspended(true);

        return new Windows.Foundation.Size(_previewOriginWidth, _previewOriginHeight);
    }

    /// <summary>
    /// Continuous, un-snapped pixel feedback. The panel follows the pointer exactly; the snapped
    /// destination is communicated by the placeholder instead. Snapping the card itself is what
    /// made the gesture feel steppy.
    /// </summary>
    public void UpdateResizePreview(double width, double height, double offsetX, double offsetY)
    {
        if (!_resizePreviewActive)
        {
            return;
        }

        // Width floor is a width, not half a height. The previous clamp used MinHeight / 2,
        // which is the wrong axis entirely and imposed a ~160px floor that varied with preset.
        Width = Math.Max(CardMinimumWidth, width);
        Height = Math.Max(CardMinimumHeight, height);

        // Left/top drags move the card as well as resize it; translation is composition-only.
        _dragTranslate.X = offsetX;
        _dragTranslate.Y = offsetY;
    }

    /// <summary>Hands the card back to the grid. The shell commits geometry immediately after.</summary>
    public void EndResizePreview()
    {
        if (!_resizePreviewActive)
        {
            return;
        }

        _resizePreviewActive = false;
        Width = double.NaN;
        Height = double.NaN;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        _dragTranslate.X = 0;
        _dragTranslate.Y = 0;
        Canvas.SetZIndex(this, 0);

        _chart.SetRenderingSuspended(false);
        _gauge.SetRenderingSuspended(false);
    }

    private Grid BuildResizeHandleLayer()
    {
        const double edge = 8;    // grab thickness along an edge
        const double corner = 16; // corner hotspot, larger because it is the common grab

        var layer = new Grid
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        // Edges are inset by the corner size so they never sit under a corner hotspot. Without
        // the inset the corner is unreachable along most of its length and the diagonal cursor
        // only shows up in a few stray pixels - which is why the corner arrows felt missing.
        void AddEdge(ResizeEdge e, HorizontalAlignment h, VerticalAlignment v, bool vertical)
        {
            var handle = CreateHandle(e);
            if (vertical)
            {
                handle.Width = edge;
                handle.HorizontalAlignment = h;
                handle.VerticalAlignment = VerticalAlignment.Stretch;
                handle.Margin = new Thickness(0, corner, 0, corner);
            }
            else
            {
                handle.Height = edge;
                handle.VerticalAlignment = v;
                handle.HorizontalAlignment = HorizontalAlignment.Stretch;
                handle.Margin = new Thickness(corner, 0, corner, 0);
            }
            layer.Children.Add(handle);
        }

        void AddCorner(ResizeEdge e, HorizontalAlignment h, VerticalAlignment v)
        {
            var handle = CreateHandle(e);
            handle.Width = corner;
            handle.Height = corner;
            handle.HorizontalAlignment = h;
            handle.VerticalAlignment = v;
            layer.Children.Add(handle);
        }

        AddEdge(ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, vertical: true);
        AddEdge(ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, vertical: true);
        AddEdge(ResizeEdge.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, vertical: false);
        AddEdge(ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, vertical: false);

        AddCorner(ResizeEdge.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top);
        AddCorner(ResizeEdge.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top);
        AddCorner(ResizeEdge.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        AddCorner(ResizeEdge.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom);

        return layer;
    }

    private const double CornerIdleOpacity = 0.32;
    private const double CornerHoverOpacity = 0.95;

    private static InputSystemCursorShape CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeEdge.Left or ResizeEdge.Right => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.SizeNorthSouth,
    };

    private Border CreateHandle(ResizeEdge edge)
    {
        var isCorner = edge is ResizeEdge.TopLeft or ResizeEdge.TopRight
            or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;
        var cursor = CursorFor(edge);

        var handle = new Border
        {
            Background = BrushFactory.CreateBrush("#01000000"), // hit-testable, visually absent
        };

        if (isCorner)
        {
            handle.Background = ResolveBrush("AccentBrush", "#79B8FF");
            handle.CornerRadius = new CornerRadius(2);
            // Visible but quiet in edit mode. Invisible-until-hover hides the affordance: you
            // have to already know the grip is there to discover it.
            handle.Opacity = CornerIdleOpacity;
            handle.Margin = new Thickness(3);
        }

        handle.PointerEntered += (_, _) =>
        {
            if (!_isLayoutEditMode) return;
            if (isCorner) handle.Opacity = CornerHoverOpacity;
            try { ProtectedCursor = InputSystemCursor.Create(cursor); } catch { }
        };

        handle.PointerExited += (_, _) =>
        {
            if (_activeResizeEdge != ResizeEdge.None) return;
            if (isCorner) handle.Opacity = CornerIdleOpacity;
            try { ProtectedCursor = null; } catch { }
        };

        handle.PointerPressed += (_, e) =>
        {
            if (!_isLayoutEditMode) return;
            _activeResizeEdge = edge;
            _resizeStart = e.GetCurrentPoint(this).Position;
            handle.CapturePointer(e.Pointer);
            PanelResizeStarted?.Invoke(this, new PanelResizeEventArgs(Panel?.PanelKey ?? string.Empty, edge, 0, 0));
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (_activeResizeEdge != edge) return;
            var point = e.GetCurrentPoint(this).Position;

            // Deltas are absolute from pointer-down. The shell applies them to the geometry
            // captured at drag start, never to the running value - adding an absolute delta to
            // an already-updated size is what made resizing compound and glitch.
            PanelResizeDelta?.Invoke(this, new PanelResizeEventArgs(
                Panel?.PanelKey ?? string.Empty, edge, point.X - _resizeStart.X, point.Y - _resizeStart.Y));
            e.Handled = true;
        };

        void EndResize(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_activeResizeEdge != edge) return;
            _activeResizeEdge = ResizeEdge.None;
            if (isCorner) handle.Opacity = CornerIdleOpacity;
            try { ProtectedCursor = null; } catch { }
            handle.ReleasePointerCaptures();
            PanelResizeCompleted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        handle.PointerReleased += (_, e) => EndResize(e);
        handle.PointerCaptureLost += (_, e) => EndResize(e);

        return handle;
    }

    public MetricPanelViewModel? Panel
    {
        get => (MetricPanelViewModel?)GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    private static void OnPanelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (TelemetryPanelCard)dependencyObject;
        if (!ReferenceEquals(args.OldValue, args.NewValue))
        {
            control._processSectionExpanded = false;
            control._lastRenderedProcessRows = null;
        }

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
            _footerText.Visibility = Visibility.Collapsed;
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
            _processSectionExpanded = false;
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

        // Unzoomed this line reads "1h replay", which is exactly what the highlighted range chip
        // a few inches to the right already says \u2014 a redundant caption on every card in the
        // default state. Zoomed it reports the actual span ("zoomed 3.2m"), which nothing else
        // shows, so it earns its place only then.
        _footerText.Visibility = panel.IsZoomed ? Visibility.Visible : Visibility.Collapsed;
        if (panel.IsZoomed)
        {
            _footerText.Text = $"\u23F8 {panel.FooterText}";
            _footerText.Opacity = 0.85;
            _footerText.Foreground = ResolveBrush("AccentBrush", "#66E7FF");
        }
        // Uppercased for the eyebrow treatment: "CPU Total" -> "CPU TOTAL".
        _titleText.Text = panel.Title;
        _currentValueText.Text = panel.CurrentValue;
        _secondaryValueText.Text = panel.SecondaryValue;
        _scaleText.Text = panel.ScaleLabel;
        _chart.EmptyStateText = panel.EmptyStateText;
        _scalePill.Visibility = string.IsNullOrWhiteSpace(panel.ScaleLabel) ? Visibility.Collapsed : Visibility.Visible;

        if (_lastRenderedSizePreset != panel.SizePreset)
        {
            ApplySizePreset(panel.SizePreset);
            _lastRenderedSizePreset = panel.SizePreset;
        }

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

        if (_processSectionExpanded && !ReferenceEquals(_lastRenderedProcessRows, panel.ProcessRows))
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
        // EffectiveViewport is expressed in THIS element's coordinate space, so for a panel
        // scrolled far out of view it is still a non-empty rect - it simply does not intersect
        // the element. Testing Width/Height > 0 was therefore true essentially always, the
        // off-screen gate never engaged, and all 14 panels kept redrawing their charts even
        // when four were visible. Intersect against the element's own bounds instead.
        var viewport = args.EffectiveViewport;
        var bounds = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight);
        var isVisible = ActualWidth <= 0 || ActualHeight <= 0
            || (viewport.Right > bounds.Left
                && viewport.Left < bounds.Right
                && viewport.Bottom > bounds.Top
                && viewport.Top < bounds.Bottom);
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
        // Corner radius intentionally left alone. Flattening 8 -> 4 -> 8 made every card visibly
        // pop at the start and end of a drag-resize, and rounded corners are not what makes a
        // resize expensive — collapsing the charts below is where the real saving is.

        // Suspend chart/gauge rendering only during resize
        _chart.SetRenderingSuspended(resizing);
        _gauge.SetRenderingSuspended(resizing);

        // Collapse charts and legends during resize so WinUI's layout engine
        // skips measuring them entirely — this is the biggest layout cost saver
        _chart.Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;
        _legendScroller.Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;
        _processSection.Visibility = !resizing && Panel?.SupportsProcessTable == true && _processSectionExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        var cardBg = ResolveBrush("SurfaceBrush", "#1C1F22");

        _cardBorder.Background = cardBg;
        _cardBorder.BorderBrush = strokeBrush;
        _cardBorder.BorderThickness = new Thickness(0.9);
        if (_isLayoutEditMode)
        {
            // Re-assert the edit affordance; a theme refresh would otherwise reset it.
            _cardBorder.BorderBrush = ResolveBrush("AccentBrush", "#79B8FF");
        }

        _cardBorder.Opacity = 1.0;
        _cardBorder.Translation = new System.Numerics.Vector3(0f, 0f, 0f);
        _rangeShell.Background = BrushFactory.CreateBrush("#00FFFFFF");
        // Both are intentionally borderless — re-applying a stroke here would restore the boxes.
        _scalePill.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _chartFrame.Background = BrushFactory.CreateBrush("#00FFFFFF");
        _processSection.Background = BrushFactory.CreateBrush("#00FFFFFF");
        _legendDivider.Background = strokeBrush;
        _legendScroller.Background = null;
        _processScroller.Background = null;
        _processLabelText.Foreground = mutedBrush;
        _activityLabelText.Foreground = mutedBrush;
        _valueLabelText.Foreground = mutedBrush;

        _badgeBorder.Background = ResolveBrush("SurfaceInsetBrush", "#091321");

        _rangeButton.RefreshThemeResources();
        _sizeButton.RefreshThemeResources();
        _processToggleButton.RefreshThemeResources();
        _sortHighestButton.RefreshThemeResources();
        _sortLowestButton.RefreshThemeResources();
        _sortNameButton.RefreshThemeResources();
        _perProcessChartButton.RefreshThemeResources();

        var secondaryBrush = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        var primaryBrush = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        foreach (var rowParts in _legendRows.Values)
        {
            // Legend rows are deliberately borderless and transparent at rest; re-applying a
            // background here on theme change would put the boxes back.
            rowParts.InnerBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
            _legendDivider.Visibility = Visibility.Collapsed;
            return;
        }

        // A single-series panel already states its value in the header (and again in the gauge,
        // where there is one), so a one-row legend repeating it a third time is noise — and with
        // nothing to compare against, click-to-isolate has nothing to isolate from.
        var legendEarnsItsSpace = !panel.SupportsProcessTable && panel.VisibleSeries.Count > 1;
        if (!legendEarnsItsSpace)
        {
            _legendRows.Clear();
            _legendHost.Children.Clear();
            _legendScroller.Visibility = Visibility.Collapsed;
            _legendDivider.Visibility = Visibility.Collapsed;
            return;
        }

        _legendScroller.Visibility = Visibility.Visible;
        _legendDivider.Visibility = _legendScroller.Visibility;

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
        var supportsProcesses = panel?.SupportsProcessTable == true;
        _processToggleButton.Visibility = supportsProcesses ? Visibility.Visible : Visibility.Collapsed;
        _processToggleButton.Text = _processSectionExpanded ? "Hide processes" : "Processes";
        _processToggleButton.IsActive = _processSectionExpanded;
        _processToggleButton.IsFilled = _processSectionExpanded;
        _processSection.Visibility = supportsProcesses && _processSectionExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnProcessSectionToggleClick(object? sender, EventArgs e)
    {
        if (Panel?.SupportsProcessTable != true)
        {
            return;
        }

        _processSectionExpanded = !_processSectionExpanded;
        if (_processSectionExpanded && Panel is { } panel)
        {
            RefreshProcessRows(panel);
            _lastRenderedProcessRows = panel.ProcessRows;
        }

        RefreshVisualMode(Panel);
    }

    private void RefreshRangeButtons(MetricPanelViewModel? panel)
    {
        var selected = panel?.SelectedRange ?? TimeRangePreset.FifteenMinutes;
        var isZoomed = panel?.IsZoomed == true;
        var choice = RangeChoices.FirstOrDefault(item => item.Preset == selected);
        _rangeButton.Text = isZoomed ? "Zoomed" : choice.ShortLabel ?? "15m";
        _rangeButton.IsActive = panel is not null && !isZoomed;
        _rangeButton.IsFilled = panel is not null && !isZoomed;

        foreach (var item in _rangeMenuChecks)
        {
            item.Value.Opacity = !isZoomed && item.Key == selected ? 1 : 0;
        }
    }

    private void RefreshProcessSortButtons(MetricPanelViewModel? panel)
    {
        ApplyRangeState(_sortHighestButton, panel?.ProcessSortMode == ProcessSortMode.Highest);
        ApplyRangeState(_sortLowestButton, panel?.ProcessSortMode == ProcessSortMode.Lowest);
        var isNameSort = panel?.ProcessSortMode is ProcessSortMode.Name or ProcessSortMode.NameReverse;
        ApplyRangeState(_sortNameButton, isNameSort);
        _sortNameButton.Text = panel?.ProcessSortMode == ProcessSortMode.NameReverse ? "Name \u2191" : "Name \u2193";
    }

    private static void ApplyRangeState(ActionChip control, bool isActive)
    {
        control.IsActive = isActive;
        control.IsFilled = isActive;
        control.Opacity = isActive ? 1 : 0.76;
    }

    private static MenuFlyoutItem CreateCheckedMenuItem(
        object tag,
        string text,
        RoutedEventHandler clickHandler,
        out SymbolIcon check)
    {
        check = new SymbolIcon(Symbol.Accept) { Opacity = 0 };
        var item = new MenuFlyoutItem
        {
            Tag = tag,
            Text = text,
            Icon = check,
            MinHeight = 36,
            Padding = new Thickness(12, 0, 16, 0),
            FontSize = 13,
        };
        item.Click += clickHandler;
        return item;
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
        // In edit mode the accent border IS the affordance saying "this panel is draggable".
        // Letting hover overwrite it meant the signal vanished the moment you moved the pointer
        // toward a panel to grab it.
        if (_isHeaderDragActive || _isLayoutEditMode)
        {
            return;
        }

        // Border thickness stays constant. Animating it (0.9 -> 1.15) re-ran a full layout pass
        // on every pointer enter/leave and nudged the card's content by a quarter pixel, which is
        // exactly the sort of thing that reads as "loose". Hover now changes only brushes and a
        // composition-level translation, neither of which invalidates layout.
        _cardBorder.BorderBrush = isHovered
            ? ResolveBrush("SurfaceStrongBrush", "#292D31")
            : ResolveBrush("SurfaceStrokeBrush", "#2C3035");
        _cardBorder.Background = isHovered
            ? ResolveBrush("SurfaceElevatedBrush", "#222529")
            : ResolveBrush("SurfaceBrush", "#1C1F22");

        // 1px lift, composition-only — free, and enough to read as responsive.
        _cardBorder.Translation = new System.Numerics.Vector3(0f, isHovered ? -1f : 0f, 0f);
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
        // Moving panels is an edit-mode action. Outside it the header is inert so clicks fall
        // through to the chips and the chart behaves normally.
        if (!_isLayoutEditMode)
        {
            return;
        }

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

    // TEMPORARY probe hook: replays the exact pointer semantics of the real handlers.
    internal void ProbePointer(int phase, ResizeEdge edge, Windows.Foundation.Point windowPoint)
    {
        if (edge == ResizeEdge.None)
        {
            if (phase == 0)
            {
                _isHeaderPointerDown = true;
                _isHeaderDragActive = false;
                _dragMouseStart = windowPoint;
            }
            else if (phase == 1)
            {
                HandleHeaderMove(windowPoint);
            }
            else
            {
                EndHeaderDragInteraction();
            }

            return;
        }

        // GetCurrentPoint(this): the pointer in this element's own space, render transform included.
        var local = TransformToVisual(null).Inverse.TransformPoint(windowPoint);
        if (phase == 0)
        {
            _activeResizeEdge = edge;
            _resizeStart = local;
            PanelResizeStarted?.Invoke(this, new PanelResizeEventArgs(Panel?.PanelKey ?? string.Empty, edge, 0, 0));
        }
        else if (phase == 1)
        {
            PanelResizeDelta?.Invoke(this, new PanelResizeEventArgs(
                Panel?.PanelKey ?? string.Empty, edge, local.X - _resizeStart.X, local.Y - _resizeStart.Y));
        }
        else
        {
            _activeResizeEdge = ResizeEdge.None;
            PanelResizeCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHeaderPointerDown || XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        HandleHeaderMove(e.GetCurrentPoint(root).Position);
        e.Handled = true;
    }

    private void HandleHeaderMove(Windows.Foundation.Point rootPoint)
    {

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
            _cardBorder.Background = ResolveBrush("SurfaceElevatedBrush", "#222529");
            _cardBorder.Opacity = 0.92;
            _cardBorder.Translation = new System.Numerics.Vector3(0f, -2f, 0f);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            Canvas.SetZIndex(this, 50);
        }

        // Pure mouse delta — card moves exactly with the mouse, no feedback loops
        _dragTranslate.X = rootPoint.X - _dragMouseStart.X;
        _dragTranslate.Y = rootPoint.Y - _dragMouseStart.Y;

        PanelDragMoved?.Invoke(this, new PanelResizeEventArgs(
            Panel?.PanelKey ?? string.Empty,
            ResizeEdge.None,
            _dragTranslate.X,
            _dragTranslate.Y));

        // The swap-on-proximity preview used to live here. It highlighted whichever neighbour
        // the pointer came near, implying "I will swap with this" - but dragging is edit-mode
        // only now, and the shell discards reorder requests in edit mode, so the swap never
        // happened. Two drop previews disagreed and the louder one was wrong. The placeholder
        // rectangle is the single authoritative answer.
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

    private void OnRangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: TimeRangePreset preset })
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

    private void OnPanelSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: PanelSizePreset preset } ||
            Panel is null ||
            !Enum.IsDefined(typeof(PanelSizePreset), preset) ||
            preset == Panel.SizePreset)
        {
            return;
        }

        PanelSizePresetRequested?.Invoke(this, new PanelSizePresetRequestedEventArgs(Panel.PanelKey, preset));
    }

    private void ApplySizePreset(PanelSizePreset preset)
    {
        var (chartHeight, minCardHeight) = preset switch
        {
            PanelSizePreset.Compact => (136d, 280d),
            PanelSizePreset.Wide => (184d, 328d),
            PanelSizePreset.Large => (248d, 392d),
            _ => (176d, 320d),
        };

        // Card MinHeight is deliberately NOT set per preset any more. The grid owns height, and
        // a preset-specific floor of 280-392px silently exceeded what the grid reserved for the
        // panel - which is how a resized panel ended up overlapping its neighbour.
        _ = minCardHeight;
        _chart.Height = chartHeight;
        _chart.MinHeight = chartHeight;
        _sizeButton.IsActive = preset != PanelSizePreset.Standard;
        _sizeButton.IsFilled = preset != PanelSizePreset.Standard;
        foreach (var (choice, check) in _sizeMenuChecks)
        {
            check.Opacity = choice == preset ? 1 : 0;
        }
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

    private void EndHeaderDragInteraction()
    {
        var wasDragging = _isHeaderDragActive;
        _isHeaderPointerDown = false;
        _isHeaderDragActive = false;

        if (wasDragging)
        {
            ProtectedCursor = null;
            _cardBorder.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035");
            _cardBorder.BorderThickness = new Thickness(1);
            _cardBorder.Background = ResolveBrush("SurfaceBrush", "#1C1F22");
            _cardBorder.Opacity = 1.0;
            _cardBorder.Translation = new System.Numerics.Vector3(0f, 0f, 0f);

            // Finalize grid position and clear translate instantly — no swoop
            PanelDragEnded?.Invoke(this, EventArgs.Empty);
            _dragTranslate.X = 0;
            _dragTranslate.Y = 0;

            // Settle, not a flash. The old confirmation faded the card up from 0.7 opacity, which
            // read as the panel blinking rather than landing. A small scale release reads as the
            // card seating into its new cell, and runs on the render transform so the freshly
            // reflowed grid is never re-measured.
            var storyboard = new Storyboard();
            foreach (var axis in new[] { "ScaleX", "ScaleY" })
            {
                var settle = new DoubleAnimation
                {
                    From = 1.025,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(settle, _dropSettleScale);
                Storyboard.SetTargetProperty(settle, axis);
                storyboard.Children.Add(settle);
            }
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

        // Borderless by design. A 16-core CPU panel renders 16 of these; giving each its own
        // outline and inset fill turned the card into a stack of boxes. The row reads as a row —
        // the colour dot and the value column carry the structure, and hover supplies the only
        // affordance needed for the click-to-isolate behaviour.
        var border = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Child = row,
        };

        // Hover reads current theme each time — no stale captured brushes
        border.PointerEntered += (_, _) =>
        {
            border.Background = ResolveBrush("SurfaceElevatedBrush", "#112033");
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
            // Brush only. Thickening the hairline (0.4 -> 1.2) re-ran layout for the row and
            // shunted its text by most of a pixel on every pointer move across the list.
            rowBorder.BorderBrush = ResolveBrush("AccentBrush", "#66E7FF");
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
            rowBorder.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035");
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
        if (string.Equals(iconKey, _lastBadgeIconKey, StringComparison.Ordinal) &&
            ReferenceEquals(accentBrush, _lastBadgeAccentBrush))
        {
            return;
        }

        _lastBadgeIconKey = iconKey;
        _lastBadgeAccentBrush = accentBrush;
        _badgeIconHost.Children.Clear();
        _badgeIconHost.Children.Add(IconFactory.CreateIcon(iconKey, accentBrush, 16));
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

public sealed class PanelSizePresetRequestedEventArgs : EventArgs
{
    public PanelSizePresetRequestedEventArgs(string panelKey, PanelSizePreset preset)
    {
        PanelKey = panelKey;
        Preset = preset;
    }

    public string PanelKey { get; }

    public PanelSizePreset Preset { get; }
}

/// <summary>Pixel deltas from a resize-handle drag; the shell converts them to grid units.</summary>
public sealed class PanelResizeEventArgs(
    string panelKey,
    TelemetryPanelCard.ResizeEdge edge,
    double deltaX,
    double deltaY) : EventArgs
{
    public string PanelKey { get; } = panelKey;
    public TelemetryPanelCard.ResizeEdge Edge { get; } = edge;
    public double DeltaX { get; } = deltaX;
    public double DeltaY { get; } = deltaY;
}
