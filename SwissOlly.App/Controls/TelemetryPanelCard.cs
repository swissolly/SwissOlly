using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using SwissOlly.App.ViewModels;
using SwissOlly.Core.Models;

namespace SwissOlly.App.Controls;

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
    private readonly Grid _badgeIconHost;
    private readonly TextBlock _footerText;
    private readonly TextBlock _scaleText;
    private readonly TextBlock _titleText;
    private readonly TextBlock _currentValueText;
    private readonly TextBlock _secondaryValueText;
    private readonly TelemetryChart _chart;
    private readonly UsageGauge _gauge;
    private readonly Viewbox _gaugeHost;
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
    private readonly ActionChip _sortHighestButton;
    private readonly ActionChip _sortLowestButton;
    private readonly ActionChip _sortNameButton;
    private readonly ActionChip _processToggleButton;
    private readonly Border _legendDivider;
    private readonly Grid _contentGrid;
    private readonly Flyout _processFlyout;
    private readonly TextBlock _processFooterText;
    private readonly Dictionary<string, (ClickableBorder Row, Border InnerBorder, TextBlock NameText, TextBlock ValueText, Ellipse Dot, Line Pattern)> _legendRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProcessRowSlot> _processRowSlots = [];
    private readonly List<string> _displayedProcessKeys = [];
    private readonly TextBlock _processEmptyText;
    private bool _processListHovered;
    private bool _processScrollLoadQueued;
    private bool _processSectionExpanded;
    private MetricPanelViewModel? _observedPanel;
    private bool _refreshQueued;
    private string? _lastBadgeIconKey;
    private Brush? _lastBadgeAccentBrush;
    private IReadOnlyList<ChartSeriesViewModel>? _lastRenderedSeries;
    private IReadOnlyList<ProcessListItemViewModel>? _lastRenderedProcessRows;
    private DateTimeOffset _lastRenderedWindowStartUtc;
    private DateTimeOffset _lastRenderedWindowEndUtc;
    private double _lastRenderedCeilingValue = double.NaN;
    private MetricUnit _lastRenderedUnit;
    // Nullable: Highest is the enum's zero value, so a plain field already "matched" the default
    // and the Highest chip never showed as selected until the sort was changed.
    private ProcessSortMode? _lastRenderedSortMode;
    // Nullable so the first render always applies the visual mode. As plain bools they started
    // "false", matched every panel without a process table, and the Processes button kept its
    // default Visible on CPU Cores, Clock, GPU and the rest.
    private bool? _lastRenderedSupportsProcessTable;
    private bool? _lastRenderedGaugeMode;
    // Layout-edit geometry. One offset serves both roles a card plays in a gesture: the
    // floating card's position on the board, or a sibling's displacement while it makes room.
    // It is UIElement.Translation, not a RenderTransform: a RenderTransform change re-runs the
    // effective-viewport pass and moves every ScrollViewer's DirectManipulation viewport inside
    // the card, which cost 20-40ms per frame with ten panels in motion. Translation is applied
    // purely in the compositor.
    private double _offsetX;
    private double _offsetY;
    private readonly ScaleTransform _liftScale = new() { ScaleX = 1d, ScaleY = 1d };
    private readonly Grid _shadowHost = new() { IsHitTestVisible = false };
    private Microsoft.UI.Composition.DropShadow? _liftShadow;
    private Microsoft.UI.Composition.SpriteVisual? _liftCaster;
    private bool _isFloating;
    private bool _gesturePointerDown;
    private bool _gestureActive;
    private ResizeEdge _gestureEdge;
    private Windows.Foundation.Point _gesturePressPoint;
    private UIElement? _gestureCaptureElement;
    private ResizeEdge? _hoveredHandleEdge;
    private bool _bodyHovered;
    private bool _handlesRevealed;
    private readonly List<(Border Visual, ResizeEdge Edge)> _handleVisuals = [];
    private bool _isEffectivelyVisible = true;
    private bool _isRenderingSuspended;
    private bool _deferredRefreshPending;

    public event EventHandler<ChartZoomSelectionEventArgs>? PanelZoomSelectionRequested;

    public event EventHandler? PanelZoomResetRequested;



    /// <summary>
    /// A move (edge None) or resize crossed its drag threshold. Points are in window-content
    /// coordinates, which stay put while this card moves - unlike the card's own space, where
    /// reading the pointer during a left/top resize fed the card's motion back into the input.
    /// </summary>
    public event EventHandler<LayoutGestureEventArgs>? LayoutGestureStarted;

    /// <summary>The pointer moved during a gesture. Raised per input event; the shell coalesces.</summary>
    public event EventHandler<LayoutGestureEventArgs>? LayoutGestureMoved;

    /// <summary>The gesture finished: released (commit) or cancelled (capture lost).</summary>
    public event EventHandler<LayoutGestureEndedEventArgs>? LayoutGestureEnded;

    /// <summary>Arrow key in edit mode: move one cell, or with Shift, resize one cell.</summary>
    public event EventHandler<LayoutNudgeEventArgs>? LayoutNudgeRequested;

    private Grid _resizeHandleLayer = null!;
    private bool _isLayoutEditMode;
    private bool _hasKeyboardFocus;

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

    /// <summary>True while a gesture has lifted the card out of grid layout.</summary>
    public bool IsFloating => _isFloating;

    public TelemetryPanelCard()
    {
        // Height comes from the panel's H in grid units, so the card stretches to fill the rows
        // it spans. The grid's minimum (PanelLayout.MinHeight) is the only floor: a second floor
        // here is what once let a card overflow the space the grid had reserved for it.
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        UseSystemFocusVisuals = false;
        KeyDown += OnLayoutKeyDown;
        GotFocus += (_, _) => OnLayoutFocusChanged(true);
        LostFocus += (_, _) => OnLayoutFocusChanged(false);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            this, "While editing the layout: arrow keys move this panel, Shift+arrow keys resize it");

        _badgeIconHost = new Grid
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Text contrast comes from the semantic text brushes alone. Stacking Opacity on an already
        // muted brush is what pushed captions and column headers to ~2.4:1 against the card.
        _footerText = CreateTextBlock(fontSize: 11);
        _scaleText = CreateTextBlock("Segoe UI Variable Text", 11, FontWeights.Normal);
        _scaleText.TextWrapping = TextWrapping.NoWrap;
        _scaleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _scaleText.MaxWidth = 200;
        _titleText = CreateTextBlock("Segoe UI Variable Text", 14, FontWeights.SemiBold);
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _titleText.TextWrapping = TextWrapping.NoWrap;
        _titleText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");

        _currentValueText = CreateTextBlock("Segoe UI Variable Display", 30, FontWeights.SemiBold);
        _currentValueText.TextTrimming = TextTrimming.CharacterEllipsis;
        _currentValueText.TextWrapping = TextWrapping.NoWrap;
        // Tabular figures: every digit the same width, so the headline number holds still
        // instead of shuffling sideways each time a 1 becomes an 8.
        UseTabularFigures(_currentValueText);
        _secondaryValueText = CreateTextBlock(fontSize: 12);

        _badgeBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            Child = _badgeIconHost,
        };


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
        };

        // The chart fills whatever height the panel's grid span leaves. MinHeight is the
        // smallest plot that still reads as a trend line.
        _chart = new TelemetryChart
        {
            MinHeight = 64,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
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
        // A gauge is fixed artwork; scale it down rather than clip it in a short panel.
        _gaugeHost = new Viewbox
        {
            Child = _gauge,
            MaxWidth = 144,
            MaxHeight = 144,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        _visualGrid.Children.Add(_gaugeHost);
        _visualGrid.Children.Add(_chartFrame);
        Grid.SetColumn(_chartFrame, 1);

        // One Tab stop for the whole legend; arrows move between its rows. Sixteen per-core rows
        // as separate stops would make tabbing past a CPU panel tedious.
        _legendHost = new StackPanel
        {
            Spacing = 4,
            TabFocusNavigation = KeyboardNavigationMode.Once,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
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
            Margin = new Thickness(0, 8, 0, 0),
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
        _processToggleButton = new ActionChip { Text = "Processes" };
        ToolTipService.SetToolTip(_processToggleButton, "Show the top processes for this metric");
        _processToggleButton.Click += OnProcessSectionToggleClick;
        // Hidden until a panel that has a process table says otherwise.
        _processToggleButton.Visibility = Visibility.Collapsed;

        _processSectionTitleText = CreateTextBlock("Segoe UI Variable Text", 12, FontWeights.SemiBold);
        _processRowsHost = new StackPanel
        {
            Spacing = 2,
            TabFocusNavigation = KeyboardNavigationMode.Once,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
        };
        _processEmptyText = CreateEmptyText("Collecting process data…");
        _processEmptyText.Visibility = Visibility.Collapsed;
        _processRowsHost.Children.Add(_processEmptyText);
        // Click handlers are attached per-row in the slow path of RefreshProcessRows
        _processScroller = new ScrollViewer
        {
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
        _processSectionTitleText.VerticalAlignment = VerticalAlignment.Center;
        _processSectionTitleText.FontSize = 14;
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

        // Same columns and horizontal padding as the rows, so each label sits over its values.
        var processColumns = new Grid
        {
            Padding = new Thickness(s_processRowPadding.Left, 0, s_processRowPadding.Right, 2),
        };
        AddProcessColumns(processColumns);

        _processLabelText = CreateTextBlock(fontSize: 10.5);
        _processLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _processLabelText.CharacterSpacing = 40;
        _processLabelText.Text = "PROCESS";
        processColumns.Children.Add(_processLabelText);

        _activityLabelText = CreateTextBlock(fontSize: 10.5);
        _activityLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _activityLabelText.CharacterSpacing = 40;
        _activityLabelText.Text = "DETAILS";
        processColumns.Children.Add(_activityLabelText);
        Grid.SetColumn(_activityLabelText, 1);

        _valueLabelText = CreateTextBlock(fontSize: 10.5);
        _valueLabelText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _valueLabelText.CharacterSpacing = 40;
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

        // Rows hold still under the pointer (see RefreshProcessRows); leaving re-sorts at once.
        _processScroller.PointerEntered += (_, _) => _processListHovered = true;
        _processScroller.PointerExited += (_, _) =>
        {
            _processListHovered = false;
            if (_processSectionExpanded && Panel is { } hoveredPanel)
            {
                RefreshProcessRows(hoveredPanel);
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

        // A grid rather than a stack, so the list fills the section's row and scrolls inside it
        // instead of pushing the card taller than its grid span.
        // States how much of the list is shown and teaches click-to-pin, which had no affordance.
        _processFooterText = CreateTextBlock("Segoe UI Variable Text", 11);
        _processFooterText.Foreground = ResolveBrush("TextMutedBrush", "#90959D");
        _processFooterText.Margin = new Thickness(s_processRowPadding.Left, 0, s_processRowPadding.Right, 0);
        _processFooterText.TextTrimming = TextTrimming.CharacterEllipsis;

        var processLayout = new Grid { RowSpacing = 10 };
        processLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        processLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        processLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        processLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        processLayout.Children.Add(processHeader);
        processLayout.Children.Add(processColumns);
        processLayout.Children.Add(_processScroller);
        processLayout.Children.Add(_processFooterText);
        Grid.SetRow(processColumns, 1);
        Grid.SetRow(_processScroller, 2);
        Grid.SetRow(_processFooterText, 3);

        // The list lives in a popover anchored to the Processes button. Inside the card it got
        // whatever height was left: on a standard card that squashed the chart to ~50px and
        // still showed only a row or two.
        _processSection = new Border
        {
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            Width = 480,
            Height = 440,
            Child = processLayout,
        };
        _processFlyout = new Flyout
        {
            Content = _processSection,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = CreateProcessFlyoutStyle(),
        };
        _processFlyout.Opened += (_, _) =>
        {
            _processSectionExpanded = true;
            RefreshVisualMode(Panel);
        };
        _processFlyout.Closed += (_, _) =>
        {
            _processSectionExpanded = false;
            RefreshVisualMode(Panel);
        };

        var panelActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _processToggleButton,
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
            CornerRadius = new CornerRadius(UiMetrics.ControlRadius),
            Padding = new Thickness(2, 3, 10, 3),
            Child = _scaleText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
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

        // Rows, not a stack: the chart row takes whatever height the panel's grid span leaves,
        // so a taller panel means a taller chart. In a stack the chart kept a fixed 176px and
        // every extra row of panel height became dead space beneath it. Spacing lives on the
        // children (not RowSpacing) so collapsed rows cost nothing.
        _visualGrid.Margin = new Thickness(0, 12, 0, 0);
        _legendDivider.Margin = new Thickness(8, 12, 8, 0);
        _contentGrid = new Grid();
        _contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 76 });
        _contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _contentGrid.Children.Add(headerGrid);
        _contentGrid.Children.Add(_visualGrid);
        _contentGrid.Children.Add(_legendDivider);
        _contentGrid.Children.Add(_legendScroller);
        Grid.SetRow(_visualGrid, 1);
        Grid.SetRow(_legendDivider, 2);
        Grid.SetRow(_legendScroller, 3);

        _cardBorder = new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            RenderTransform = _liftScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            Child = _contentGrid,
        };
        _cardBorder.PointerEntered += (_, _) =>
        {
            _bodyHovered = true;
            SetHoverState(true);
            UpdateCursor();
        };
        _cardBorder.PointerExited += (_, _) =>
        {
            _bodyHovered = false;
            SetHoverState(false);
            UpdateCursor();
        };
        _cardBorder.SizeChanged += (_, e) =>
        {
            // The chart is the panel: the legend only gets what is left once the header and a
            // readable plot are placed, up to a third of the card, and steps aside entirely on
            // short cards. A flat third let a 16-core legend squash the plot to ~35px.
            const double headerAndPaddingHeight = 166;
            const double minimumReadableChartHeight = 120;
            const double minimumUsefulLegendHeight = 56;
            var available = e.NewSize.Height - headerAndPaddingHeight - minimumReadableChartHeight;
            _legendScroller.MaxHeight = available < minimumUsefulLegendHeight
                ? 0
                : Math.Min(available, e.NewSize.Height * 0.33);
            // Opacity, not Visibility: several paths own the divider's visibility already.
            _legendDivider.Opacity = _legendScroller.MaxHeight > 0 ? 1 : 0;
        };

        // In edit mode the whole card body is the move handle.
        _cardBorder.PointerPressed += (_, e) => BeginGesturePointer(ResizeEdge.None, _cardBorder, e);
        _cardBorder.PointerMoved += OnGesturePointerMoved;
        _cardBorder.PointerReleased += OnGesturePointerReleased;
        _cardBorder.PointerCaptureLost += OnGesturePointerCaptureLost;

        PointerEntered += (_, _) => SetHandlesRevealed(_isLayoutEditMode);
        PointerExited += (_, _) => SetHandlesRevealed(_isLayoutEditMode && _gesturePointerDown);
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        // Handles live in an overlay above the card so they can be switched off entirely
        // outside edit mode. Visibility alone is not enough — an invisible-but-hit-testable
        // handle would still swallow the pointer events chart drag-zoom depends on.
        _resizeHandleLayer = BuildResizeHandleLayer();
        Content = new Grid
        {
            Children = { _shadowHost, _cardBorder, _resizeHandleLayer },
        };
        Loaded += (_, _) =>
        {
            // Re-attach on every load. The shell rebuilds the board by removing and re-adding the
            // same cards, and Unloaded below has already let go of the view model - without this
            // a rebuilt board kept rendering but its panels silently stopped updating.
            if (!ReferenceEquals(_observedPanel, Panel))
            {
                DetachPanel(_observedPanel);
                AttachPanel(Panel);
            }

            RefreshFromPanel();
        };

        // Without this the card stays subscribed to its MetricPanelViewModel after the shell
        // drops it. The view models are permanent, so every toggled-off panel left an orphaned
        // card alive - still running RefreshFromPanel and rebuilding its chart on every scrape.
        // WinUI can raise Unloaded after a remove-and-re-add in the same tick, by which point the
        // card is back in the tree; only let go if it really left.
        Unloaded += (_, _) =>
        {
            if (!IsLoaded)
            {
                DetachPanel(_observedPanel);
            }
        };
    }

    /// <summary>
    /// Edit mode gates every layout affordance. Outside it the handle layer is collapsed *and*
    /// non-hit-testable and the card ignores presses, so panels behave exactly as before this
    /// feature existed - which is what keeps chart drag-to-zoom working after a layout is saved.
    /// </summary>
    private void OnLayoutKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isLayoutEditMode || _isFloating)
        {
            return;
        }

        var (dx, dy) = e.Key switch
        {
            Windows.System.VirtualKey.Left => (-1, 0),
            Windows.System.VirtualKey.Right => (1, 0),
            Windows.System.VirtualKey.Up => (0, -1),
            Windows.System.VirtualKey.Down => (0, 1),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        LayoutNudgeRequested?.Invoke(this, new LayoutNudgeEventArgs(dx, dy, shift));
        // Handled either way, so the board's ScrollViewer doesn't also scroll.
        e.Handled = true;
    }

    private void OnLayoutFocusChanged(bool focused)
    {
        _hasKeyboardFocus = focused && _isLayoutEditMode && FocusState == FocusState.Keyboard;
        if (_isLayoutEditMode)
        {
            SetHandlesRevealed(_hasKeyboardFocus);
            _cardBorder.BorderBrush = ResolveEditBorderBrush();
        }
    }

    public void SetLayoutEditMode(bool editing)
    {
        if (!editing)
        {
            AbortLayoutGesture();
        }

        _isLayoutEditMode = editing;
        _resizeHandleLayer.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _resizeHandleLayer.IsHitTestVisible = editing;
        SetHandlesRevealed(false);

        // While arranging, each panel is one tab stop: arrows move it, Shift+arrows resize it.
        // Eight resize handles as separate stops per panel would make tabbing a board tedious.
        IsTabStop = editing;
        if (!editing)
        {
            _hasKeyboardFocus = false;
        }

        if (editing)
        {
            // Edit mode replaces hover; clear any hover lift the pointer left behind.
            _cardBorder.Background = ResolveBrush("SurfaceBrush", "#1C1F22");
            _cardBorder.Translation = System.Numerics.Vector3.Zero;
        }

        _cardBorder.BorderBrush = ResolveEditBorderBrush();

        // In edit mode the whole card is one drag surface. Anything inside that reacts to the
        // pointer - chart zoom, legend rows, the process list, the header chips - would steal
        // the press and the panel would refuse to move.
        _chart.IsHitTestVisible = !editing;
        _legendScroller.IsHitTestVisible = !editing;
        if (editing)
        {
            _processFlyout.Hide();
        }
        _rangeShell.IsHitTestVisible = !editing;
        FadeTo(_rangeShell, editing ? 0d : 1d);
        UpdateCursor();
    }

    private Brush ResolveEditBorderBrush() => _isFloating || (_isLayoutEditMode && _hasKeyboardFocus)
        ? ResolveBrush("AccentStrongBrush", "#9FEFFF")
        : _isLayoutEditMode
            ? ResolveBrush("AccentBrush", "#79B8FF")
            : ResolveBrush("SurfaceStrokeBrush", "#2C3035");

    // ---- Floating geometry ---------------------------------------------------------------
    //
    // During a gesture the shell spans this card across the whole board, pins it top-left and
    // drives its real Width/Height and offset every frame. Real size, not a scale transform:
    // scaling magnified the title, chips and chart like a zoom and re-rasterised all of it on
    // every frame (28-76ms frames against 7ms idle). At real size the content simply reflows,
    // the way a window's does while you drag its edge.

    /// <summary>Lifts the card above the board. <paramref name="lift"/> adds the pick-up scale used for moves.</summary>
    public void BeginFloat(bool lift)
    {
        if (_isFloating)
        {
            return;
        }

        _isFloating = true;
        Canvas.SetZIndex(this, 100);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _cardBorder.BorderBrush = ResolveEditBorderBrush();
        AnimateLiftScale(lift ? LiftScale : 1d);
        if (lift)
        {
            AnimateLiftShadow(lifted: true);
        }
    }

    /// <summary>Board-relative geometry for this frame. Width/Height are real layout sizes.</summary>
    public void SetFloatGeometry(double x, double y, double width, double height)
    {
        ApplyOffset(x, y);

        // Written only when changed: an identical Width still invalidates measure.
        if (!(Math.Abs(Width - width) < 0.01))
        {
            Width = width;
        }

        if (!(Math.Abs(Height - height) < 0.01))
        {
            Height = height;
        }
    }

    /// <summary>Hands the card back to its grid cell. The shell assigns the cell first.</summary>
    public void EndFloat()
    {
        if (!_isFloating)
        {
            return;
        }

        _isFloating = false;
        Width = double.NaN;
        Height = double.NaN;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        ApplyOffset(0, 0);
        Canvas.SetZIndex(this, 0);
        _cardBorder.BorderBrush = ResolveEditBorderBrush();
        AnimateLiftScale(1d);
        AnimateLiftShadow(lifted: false);
    }

    /// <summary>Displaces a sibling toward its preview cell. The shell animates the values.</summary>
    public void SetReflowOffset(double x, double y) => ApplyOffset(x, y);

    private void ApplyOffset(double x, double y)
    {
        if (x == _offsetX && y == _offsetY)
        {
            return;
        }

        _offsetX = x;
        _offsetY = y;
        Translation = new System.Numerics.Vector3((float)x, (float)y, 0f);
    }

    private const double LiftScale = 1.012;

    private void AnimateLiftScale(double to)
    {
        if (!Motion.AnimationsEnabled)
        {
            _liftScale.ScaleX = to;
            _liftScale.ScaleY = to;
            return;
        }

        var storyboard = new Storyboard();
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(to > 1d ? MotionTokens.Quick : MotionTokens.Standard)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, _liftScale);
            Storyboard.SetTargetProperty(animation, axis);
            storyboard.Children.Add(animation);
        }

        storyboard.Begin();
    }

    private void AnimateLiftShadow(bool lifted)
    {
        try
        {
            var shadow = EnsureLiftShadow();
            var compositor = shadow.Compositor;
            if (lifted && _liftCaster is not null)
            {
                _liftCaster.IsVisible = true;
            }

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, lifted ? (IsLightPaletteActive() ? 0.20f : 0.5f) : 0f);
            animation.Duration = TimeSpan.FromMilliseconds(lifted ? MotionTokens.Quick : MotionTokens.Standard);

            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            shadow.StartAnimation("Opacity", animation);
            batch.End();
            batch.Completed += (_, _) =>
            {
                // A zero-opacity shadow still costs a blur pass; stop drawing it once it's gone.
                if (!_isFloating && _liftCaster is not null)
                {
                    _liftCaster.IsVisible = false;
                }
            };
        }
        catch
        {
            // A shadow is decoration; it must never fail a gesture.
        }
    }

    private Microsoft.UI.Composition.DropShadow EnsureLiftShadow()
    {
        if (_liftShadow is not null)
        {
            return _liftShadow;
        }

        var hostVisual = ElementCompositionPreview.GetElementVisual(_shadowHost);
        var compositor = hostVisual.Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 36f;
        shadow.Offset = new System.Numerics.Vector3(0f, 14f, 0f);
        shadow.Color = Microsoft.UI.Colors.Black;
        shadow.Opacity = 0f;

        // The shadow is cast by a plain rectangle inset under the card's rounded corners, so the
        // caster itself is always hidden behind the opaque card.
        var caster = compositor.CreateSpriteVisual();
        caster.Brush = compositor.CreateColorBrush(ResolveColor("SurfaceBrush", "#1C1F22"));
        caster.RelativeSizeAdjustment = System.Numerics.Vector2.One;
        caster.Size = new System.Numerics.Vector2(-8f, -8f);
        caster.Offset = new System.Numerics.Vector3(4f, 4f, 0f);
        caster.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, caster);

        _liftShadow = shadow;
        _liftCaster = caster;
        return shadow;
    }

    private static void FadeTo(UIElement element, double to, double milliseconds = MotionTokens.Quick)
    {
        if (!Motion.AnimationsEnabled)
        {
            element.Opacity = to;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private Grid BuildResizeHandleLayer()
    {
        const double edge = 10;   // grab thickness, centred on the card's outline
        const double corner = 22; // corner hotspot, larger because it is the common grab

        var layer = new Grid
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            // Straddle the outline like a window border: the grab zone sits where the eye says
            // the edge is, half inside the card and half in the gutter.
            Margin = new Thickness(-5),
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

    private const double CornerIdleOpacity = 0.9;
    private const double EdgeIdleOpacity = 0.55;

    private static InputSystemCursorShape CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
        ResizeEdge.Left or ResizeEdge.Right => InputSystemCursorShape.SizeWestEast,
        ResizeEdge.Top or ResizeEdge.Bottom => InputSystemCursorShape.SizeNorthSouth,
        _ => InputSystemCursorShape.SizeAll,
    };

    /// <summary>
    /// One place decides the cursor, from state rather than from whichever enter/exit event
    /// fired last. Moving from the body onto a handle raises the body's exit after the handle's
    /// enter on some paths, which used to wipe the resize arrows the instant they appeared.
    /// </summary>
    private void UpdateCursor()
    {
        InputSystemCursorShape? shape = null;
        if (_gesturePointerDown)
        {
            shape = CursorFor(_gestureEdge);
        }
        else if (_isLayoutEditMode && _hoveredHandleEdge is { } edge)
        {
            shape = CursorFor(edge);
        }
        else if (_isLayoutEditMode && _bodyHovered)
        {
            shape = InputSystemCursorShape.SizeAll;
        }

        try
        {
            ProtectedCursor = shape is { } value ? PointerCursors.Get(value) : null;
        }
        catch
        {
            // Cursor changes are cosmetic.
        }
    }

    /// <summary>
    /// Grips appear on the panel under the pointer only. Eight always-visible grips on fourteen
    /// panels was 112 marks of noise; the outline already says "editable", the grips say "here".
    /// </summary>
    private void SetHandlesRevealed(bool revealed)
    {
        if (_handlesRevealed == revealed)
        {
            return;
        }

        _handlesRevealed = revealed;
        foreach (var (visual, edge) in _handleVisuals)
        {
            FadeTo(visual, revealed ? IdleOpacityFor(edge) : 0d, revealed ? MotionTokens.Quick : MotionTokens.Standard);
        }
    }

    private static bool IsCorner(ResizeEdge edge) => edge is ResizeEdge.TopLeft or ResizeEdge.TopRight
        or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

    private static double IdleOpacityFor(ResizeEdge edge) => IsCorner(edge) ? CornerIdleOpacity : EdgeIdleOpacity;

    private Border CreateHandle(ResizeEdge edge)
    {
        var isCorner = IsCorner(edge);
        var handle = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // hit target only
        };

        // The visible grip. Corners are L-brackets hugging the card's own rounded corner, so they
        // read as "this corner is draggable" rather than as stray squares; edges are short pills
        // centred on the outline.
        Border grip;
        if (isCorner)
        {
            var left = edge.HasFlag(ResizeEdge.Left);
            var top = edge.HasFlag(ResizeEdge.Top);
            grip = new Border
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                // The layer overhangs the card by 5px; this lands the bracket on the outline.
                Margin = new Thickness(left ? 5 : 0, top ? 5 : 0, left ? 0 : 5, top ? 0 : 5),
                BorderThickness = new Thickness(left ? 2.5 : 0, top ? 2.5 : 0, left ? 0 : 2.5, top ? 0 : 2.5),
                CornerRadius = new CornerRadius(
                    left && top ? 8 : 0,
                    !left && top ? 8 : 0,
                    !left && !top ? 8 : 0,
                    left && !top ? 8 : 0),
                BorderBrush = ResolveBrush("AccentStrongBrush", "#9FEFFF"),
            };
        }
        else
        {
            var vertical = edge is ResizeEdge.Left or ResizeEdge.Right;
            grip = new Border
            {
                Width = vertical ? 4 : 32,
                Height = vertical ? 32 : 4,
                CornerRadius = new CornerRadius(2),
                Background = ResolveBrush("AccentStrongBrush", "#9FEFFF"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        grip.IsHitTestVisible = false;
        grip.Opacity = 0;
        handle.Child = grip;
        _handleVisuals.Add((grip, edge));

        handle.PointerEntered += (_, _) =>
        {
            if (!_isLayoutEditMode)
            {
                return;
            }

            _hoveredHandleEdge = edge;
            SetHandlesRevealed(true);
            FadeTo(grip, 1d, MotionTokens.Instant);
            UpdateCursor();
        };

        handle.PointerExited += (_, _) =>
        {
            if (_hoveredHandleEdge == edge)
            {
                _hoveredHandleEdge = null;
            }

            if (!(_gesturePointerDown && _gestureEdge == edge))
            {
                FadeTo(grip, _handlesRevealed ? IdleOpacityFor(edge) : 0d);
            }

            UpdateCursor();
        };

        handle.PointerPressed += (_, e) => BeginGesturePointer(edge, handle, e);
        handle.PointerMoved += OnGesturePointerMoved;
        handle.PointerReleased += OnGesturePointerReleased;
        handle.PointerCaptureLost += OnGesturePointerCaptureLost;

        return handle;
    }

    // ---- Gesture input --------------------------------------------------------------------
    //
    // The card only reports pointer positions; the shell owns geometry, snapping and the board.
    // Positions are read in window-content space, which stays still while the card moves.

    private void BeginGesturePointer(ResizeEdge edge, UIElement source, PointerRoutedEventArgs e)
    {
        if (!_isLayoutEditMode || _gesturePointerDown || Panel is null || XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        var point = e.GetCurrentPoint(root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // On touch and pen the board's ScrollViewer would otherwise claim the drag as a pan and
        // take the pointer away mid-gesture.
        CancelDirectManipulations();

        if (source.CapturePointer(e.Pointer))
        {
            _gesturePointerDown = true;
            _gestureActive = false;
            _gestureEdge = edge;
            _gesturePressPoint = point.Position;
            _gestureCaptureElement = source;
            UpdateCursor();
        }

        e.Handled = true;
    }

    private void OnGesturePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_gesturePointerDown
            || !ReferenceEquals(sender, _gestureCaptureElement)
            || XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        var position = e.GetCurrentPoint(root).Position;
        if (!_gestureActive)
        {
            // A resize grip is an explicit grab and engages almost at once; a press on the body
            // needs a little travel so an ordinary click never nudges a panel.
            var threshold = _gestureEdge == ResizeEdge.None ? 5d : 2d;
            var dx = position.X - _gesturePressPoint.X;
            var dy = position.Y - _gesturePressPoint.Y;
            if ((dx * dx) + (dy * dy) < threshold * threshold)
            {
                e.Handled = true;
                return;
            }

            _gestureActive = true;
            LayoutGestureStarted?.Invoke(this, new LayoutGestureEventArgs(_gestureEdge, _gesturePressPoint));
        }

        LayoutGestureMoved?.Invoke(this, new LayoutGestureEventArgs(_gestureEdge, position));
        e.Handled = true;
    }

    private void OnGesturePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _gestureCaptureElement))
        {
            return;
        }

        FinishGesturePointer(cancelled: false);
        e.Handled = true;
    }

    private void OnGesturePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _gestureCaptureElement))
        {
            return;
        }

        // Capture lost without a release (Alt+Tab, a system prompt): put everything back
        // rather than commit a layout the user never actually let go of.
        FinishGesturePointer(cancelled: true);
    }

    private void FinishGesturePointer(bool cancelled)
    {
        if (!_gesturePointerDown)
        {
            return;
        }

        var wasActive = _gestureActive;
        var element = _gestureCaptureElement;
        _gesturePointerDown = false;
        _gestureActive = false;
        _gestureCaptureElement = null;
        element?.ReleasePointerCaptures();
        UpdateCursor();
        SetHandlesRevealed(_isLayoutEditMode && (_bodyHovered || _hoveredHandleEdge is not null));

        if (wasActive)
        {
            LayoutGestureEnded?.Invoke(this, new LayoutGestureEndedEventArgs(cancelled));
        }
    }

    /// <summary>Drops the pointer interaction without raising events; the shell cancelled it.</summary>
    public void AbortLayoutGesture()
    {
        if (!_gesturePointerDown)
        {
            return;
        }

        var element = _gestureCaptureElement;
        _gesturePointerDown = false;
        _gestureActive = false;
        _gestureCaptureElement = null;
        element?.ReleasePointerCaptures();
        UpdateCursor();
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
            control._processFlyout.Hide();
            control._processSectionExpanded = false;
            control._lastRenderedProcessRows = null;
            control._lastRenderedSortMode = null;
            control._lastRenderedSupportsProcessTable = null;
            control._lastRenderedGaugeMode = null;
            control._thresholdLevel = 0;
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
            // Idempotent: a second attach must not double every refresh.
            panel.PropertyChanged -= OnPanelPropertyChanged;
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
            HideProcessRowSlots(0);
            _processEmptyText.Visibility = Visibility.Collapsed;
            _displayedProcessKeys.Clear();
            _processFlyout.Hide();
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
        _chart.IsLoading = panel.IsHistoryLoading;
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
        UpdateThresholdLevel(panel);
        _gauge.AccentBrush = ResolveThresholdBrush(panel);
        _gauge.Caption = panel.PrefersGaugeVisual ? "Capacity" : "Live";

        if (_processSectionExpanded && !ReferenceEquals(_lastRenderedProcessRows, panel.ProcessRows))
        {
            RefreshProcessRows(panel);
            _lastRenderedProcessRows = panel.ProcessRows;
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
        if (resizing)
        {
            _processFlyout.Hide();
        }
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
        // Constant 1px. Every thickness change is a layout pass, and 0.9 here vs 1 elsewhere
        // shifted the card's content by a fraction of a pixel on each theme change.
        _cardBorder.BorderThickness = new Thickness(1);
        _cardBorder.BorderBrush = ResolveEditBorderBrush();
        if (_liftCaster is not null)
        {
            _liftCaster.Brush = _liftCaster.Compositor.CreateColorBrush(ResolveColor("SurfaceBrush", "#1C1F22"));
        }

        foreach (var (grip, edge) in _handleVisuals)
        {
            if (IsCorner(edge))
            {
                grip.BorderBrush = ResolveBrush("AccentStrongBrush", "#9FEFFF");
            }
            else
            {
                grip.Background = ResolveBrush("AccentStrongBrush", "#9FEFFF");
            }
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

        _processToggleButton.RefreshThemeResources();
        _sortHighestButton.RefreshThemeResources();
        _sortLowestButton.RefreshThemeResources();
        _sortNameButton.RefreshThemeResources();

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

        foreach (var slot in _processRowSlots)
        {
            slot.NameText.Foreground = primaryBrush;
            slot.ValueText.Foreground = primaryBrush;
            slot.CaptionText.Foreground = slot.IsPinned ? ResolveBrush("TextSecondaryBrush", "#C4C8CE") : mutedBrush;
            if (!slot.IsHovered)
            {
                slot.RowBorder.Background = ProcessRowRestBrush(slot.IsPinned);
            }
        }

        _processEmptyText.Foreground = mutedBrush;

        if (Panel is { } panel)
        {
            // Rebuilt, not skipped as unchanged: on a light palette the icon holds a darkened copy
            // of the accent, which went stale when switching back to a dark one.
            _lastBadgeAccentBrush = null;
            UpdateBadgeIcon(panel);
            ApplyPalette(panel);
            // Re-apply sort button states so active highlights use the new theme colors.
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
        // nothing to compare against, click-to-isolate has nothing to isolate from. Pinned
        // processes are the exception on CPU Total and Memory: their lines need names.
        var legendEarnsItsSpace = panel.VisibleSeries.Count > 1 &&
            (!panel.SupportsProcessTable || panel.PerProcessChartsEnabled);
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

        // Rows are keyed by series key, not display name. Two series can share a name (sensors
        // from different hardware, or a renamed adapter in loaded history), and keying by name
        // put the same row in the list twice - adding an element that already has a parent is
        // a hard COMException (0x800F1000) in WinUI.
        // On CPU Total and Memory the legend names the pinned processes only: the panel's own line
        // is the headline above it, and its row pushed the second pin out of the legend's height.
        var legendSeries = panel.SupportsProcessTable
            ? panel.VisibleSeries.Where(series => MetricPanelViewModel.IsProcessSeriesKey(series.Key)).ToList()
            : panel.VisibleSeries;
        var activeKeys = new HashSet<string>(legendSeries.Select(LegendKey), StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _legendRows.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
        {
            _legendHost.Children.Remove(_legendRows[staleKey].Row);
            _legendRows.Remove(staleKey);
        }

        var orderedRows = new List<UIElement>(legendSeries.Count);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The index runs over every visible series, listed or not: the chart picks dash patterns
        // by that position, and the legend's pattern has to match it.
        for (var index = 0; index < panel.VisibleSeries.Count; index++)
        {
            var series = panel.VisibleSeries[index];
            if (!activeKeys.Contains(LegendKey(series)))
            {
                continue;
            }

            var legendKey = LegendKey(series);
            if (!seenKeys.Add(legendKey))
            {
                continue;
            }

            var value = series.Points.Count == 0
                ? "--"
                : FormatValue(series.Points[^1].Value, panel.Unit);

            if (!_legendRows.TryGetValue(legendKey, out var rowParts))
            {
                rowParts = CreateLegendRow(series.Name, series.StrokeBrush);
                _legendRows.Add(legendKey, rowParts);
                _legendHost.Children.Add(rowParts.Row);

                // Click legend row to isolate/focus that series on the chart
                var capturedKey = series.Key;
                rowParts.Row.Tapped += (_, _) => Panel?.ToggleSeriesFocus(capturedKey);
                rowParts.Row.Invoked += (_, _) => Panel?.ToggleSeriesFocus(capturedKey);
            }

            // Dim unfocused legend rows when a series is focused
            var isFocused = panel.FocusedSeriesKey is null ||
                string.Equals(series.Key, panel.FocusedSeriesKey, StringComparison.OrdinalIgnoreCase);
            rowParts.Row.Opacity = isFocused ? 1.0 : 0.35;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rowParts.Row, $"{series.Name}, {value}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(rowParts.Row,
                panel.FocusedSeriesKey is not null && isFocused
                    ? "Press Enter to show every series again"
                    : "Press Enter to show only this series");

            var patterned = SeriesDash.AppliesTo(panel.VisibleSeries.Count);
            rowParts.Pattern.Visibility = patterned ? Visibility.Visible : Visibility.Collapsed;
            rowParts.Dot.Visibility = patterned ? Visibility.Collapsed : Visibility.Visible;
            if (patterned)
            {
                SeriesDash.Apply(rowParts.Pattern, index, panel.VisibleSeries.Count);
            }

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

    private static string LegendKey(ChartSeriesViewModel series)
        => string.IsNullOrEmpty(series.Key) ? series.Name : series.Key;

    /// <summary>
    /// Shows the process list in a fixed set of row slots that are re-bound in place. Rows used to
    /// be created, cleared and re-added on every re-sort: the list flickered, the hovered row
    /// jumped, and the churn of rows entering and leaving the tree is what set off the cursor
    /// crash (see PointerCursors). Slots are only ever added, and hidden when not needed.
    /// </summary>
    private void RefreshProcessRows(MetricPanelViewModel panel)
    {
        panel._forceProcessRowRebuild = false;
        if (!panel.SupportsProcessTable)
        {
            HideProcessRowSlots(0);
            _processEmptyText.Visibility = Visibility.Collapsed;
            _displayedProcessKeys.Clear();
            return;
        }

        _processSectionTitleText.Text = panel.ProcessTableTitle;
        if (panel.ProcessRows.Count == 0)
        {
            HideProcessRowSlots(0);
            _processEmptyText.Visibility = Visibility.Visible;
            _processFooterText.Text = string.Empty;
            _displayedProcessKeys.Clear();
            return;
        }

        _processEmptyText.Visibility = Visibility.Collapsed;
        var seenRowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = panel.ProcessRows.Where(row => seenRowKeys.Add(row.Key)).ToList();
        _processFooterText.Text = panel.TotalProcessCount > rows.Count
            ? $"Top {rows.Count} of {panel.TotalProcessCount:N0} · Click a process to pin it to the chart"
            : "Click a process to pin it to the chart";

        // While the pointer is over the list, rows stay where they are and only their numbers
        // change: re-sorting every refresh moved the row under the cursor as it was clicked.
        if (_processListHovered && _displayedProcessKeys.Count > 0)
        {
            rows = KeepDisplayedProcessOrder(rows);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var slot = index < _processRowSlots.Count ? _processRowSlots[index] : AddProcessRowSlot();
            BindProcessRow(slot, rows[index], panel.AccentBrush);
        }

        HideProcessRowSlots(rows.Count);
        _displayedProcessKeys.Clear();
        foreach (var row in rows)
        {
            _displayedProcessKeys.Add(row.Key);
        }
    }

    private List<ProcessListItemViewModel> KeepDisplayedProcessOrder(List<ProcessListItemViewModel> rows)
    {
        var position = new Dictionary<string, int>(_displayedProcessKeys.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _displayedProcessKeys.Count; index++)
        {
            position[_displayedProcessKeys[index]] = index;
        }

        // Rows already on screen keep their places; newcomers queue after them in sorted order.
        return rows
            .Select((row, sortedIndex) => (row, rank: position.TryGetValue(row.Key, out var shown) ? shown : _displayedProcessKeys.Count + sortedIndex))
            .OrderBy(entry => entry.rank)
            .Select(entry => entry.row)
            .ToList();
    }

    private void HideProcessRowSlots(int fromIndex)
    {
        for (var index = fromIndex; index < _processRowSlots.Count; index++)
        {
            var slot = _processRowSlots[index];
            if (slot.Row.Visibility != Visibility.Collapsed)
            {
                slot.Row.Visibility = Visibility.Collapsed;
            }

            slot.Key = null;
        }
    }

    private ProcessRowSlot AddProcessRowSlot()
    {
        var slot = CreateProcessRowSlot();
        _processRowSlots.Add(slot);
        _processRowsHost.Children.Add(slot.Row);

        // Press and release are tracked to tell a tap from a scroll drag. handledEventsToo, so
        // they still arrive after the ScrollViewer has handled the gesture. The slot is asked for
        // its process at click time: it shows a different one after every re-sort.
        Windows.Foundation.Point pressPosition = default;
        slot.Row.AddHandler(PointerPressedEvent, new PointerEventHandler((sender, args) =>
        {
            pressPosition = args.GetCurrentPoint((UIElement)sender).Position;
        }), true);
        slot.Row.AddHandler(PointerReleasedEvent, new PointerEventHandler((sender, args) =>
        {
            var releasePosition = args.GetCurrentPoint((UIElement)sender).Position;
            if (slot.Key is not null &&
                Math.Abs(releasePosition.X - pressPosition.X) < 12 &&
                Math.Abs(releasePosition.Y - pressPosition.Y) < 12)
            {
                Panel?.ToggleProcessPin(slot.ProcessName);
            }

            args.Handled = true;
        }), true);
        slot.Row.Invoked += (_, _) =>
        {
            if (slot.Key is not null)
            {
                Panel?.ToggleProcessPin(slot.ProcessName);
            }
        };
        return slot;
    }

    private void RefreshVisualMode(MetricPanelViewModel? panel)
    {
        var showGauge = panel?.PrefersGaugeVisual == true;
        _gaugeHost.Visibility = showGauge ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_chartFrame, showGauge ? 1 : 0);
        Grid.SetColumnSpan(_chartFrame, showGauge ? 1 : 2);
        // CPU Total and Memory show a legend only for pinned processes (see RefreshLegend).
        _legendScroller.Visibility = panel is { SupportsProcessTable: true, PerProcessChartsEnabled: false }
            ? Visibility.Collapsed
            : Visibility.Visible;
        var supportsProcesses = panel?.SupportsProcessTable == true;
        _processToggleButton.Visibility = supportsProcesses ? Visibility.Visible : Visibility.Collapsed;
        _processToggleButton.IsActive = _processSectionExpanded;
        if (!supportsProcesses && _processSectionExpanded)
        {
            _processFlyout.Hide();
        }
    }

    private void OnProcessSectionToggleClick(object? sender, EventArgs e)
    {
        if (Panel is not { SupportsProcessTable: true } panel)
        {
            return;
        }

        if (_processSectionExpanded)
        {
            _processFlyout.Hide();
            return;
        }

        RefreshProcessRows(panel);
        RefreshProcessSortButtons(panel);
        _lastRenderedProcessRows = panel.ProcessRows;
        _processScroller.ChangeView(null, 0, null, true);
        _processFlyout.ShowAt(_processToggleButton);
    }

    private static Style CreateProcessFlyoutStyle()
    {
        // Same surface as the shell's other popovers. No ThemeShadow: it barely showed, and with
        // the list refreshing under it the UI thread stalled for over a second in every eight.
        // The border is the muted text colour instead of the hairline stroke, so the list's edge
        // reads clearly against the panels it covers.
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("SurfaceElevatedBrush", "#222529")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ResolveBrush("TextMutedBrush", "#90959D")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(UiMetrics.Hairline)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(UiMetrics.OverlayRadius)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 14, 12, 12)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 560d));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 560d));
        return style;
    }

    private void RefreshProcessSortButtons(MetricPanelViewModel? panel)
    {
        ApplyRangeState(_sortHighestButton, panel?.ProcessSortMode == ProcessSortMode.Highest);
        ApplyRangeState(_sortLowestButton, panel?.ProcessSortMode == ProcessSortMode.Lowest);
        var isNameSort = panel?.ProcessSortMode is ProcessSortMode.Name or ProcessSortMode.NameReverse;
        ApplyRangeState(_sortNameButton, isNameSort);
        // The direction arrow only while the list is sorted by name; under Highest it suggested
        // a name order that wasn't in effect. MinWidth keeps the chip from changing size.
        _sortNameButton.MinWidth = 72;
        _sortNameButton.Text = !isNameSort ? "Name"
            : panel?.ProcessSortMode == ProcessSortMode.NameReverse ? "Name \u2191" : "Name \u2193";
    }

    private static void ApplyRangeState(ActionChip control, bool isActive)
    {
        // The chip's own active/inactive styling carries the state; an extra opacity dip made
        // the inactive options look disabled.
        control.IsActive = isActive;
    }

    private void ApplyPalette(MetricPanelViewModel panel)
    {
        _badgeBorder.Background = ResolveBrush("SurfaceInsetBrush", "#091321");
        _footerText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _scaleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        _titleText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _secondaryValueText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");
        _processSectionTitleText.Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1");

        // Colour-code the value and badge by threshold state.
        var level = UpdateThresholdLevel(panel);
        _badgeBorder.BorderBrush = ResolveThresholdBrush(panel);
        _currentValueText.Foreground = level switch
        {
            2 => ResolveBrush("CriticalBrush", "#FF9761"),
            1 => ResolveBrush("WarningBrush", "#F0C968"),
            _ => ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
        };
    }

    private const double ThresholdHysteresis = 2d;
    private int _thresholdLevel;

    /// <summary>
    /// Normal (0), warning (1) or critical (2), with hysteresis: rising crosses at the threshold,
    /// falling must clear it by a margin. A reading hovering at 74.8/75.2 used to flip the
    /// colour on every sample. Temperatures have their own thresholds - previously none at all,
    /// so the hottest readings in the app were the only ones that never turned amber or red.
    /// </summary>
    private int UpdateThresholdLevel(MetricPanelViewModel panel)
    {
        var (value, warning, critical) = panel.Unit == MetricUnit.Celsius
            ? (HottestReading(panel), 80d, 90d)
            : (panel.UtilizationPercent, 75d, 90d);

        _thresholdLevel = value > critical ? 2
            : _thresholdLevel == 2 && value > critical - ThresholdHysteresis ? 2
            : value > warning ? 1
            : _thresholdLevel >= 1 && value > warning - ThresholdHysteresis ? 1
            : 0;
        return _thresholdLevel;
    }

    private static double HottestReading(MetricPanelViewModel panel)
    {
        var hottest = 0d;
        foreach (var series in panel.VisibleSeries)
        {
            if (series.Points.Count > 0)
            {
                hottest = Math.Max(hottest, series.Points[^1].Value);
            }
        }

        return hottest;
    }

    /// <summary>Badge and gauge accent: threshold colour for load and heat, the panel accent otherwise.</summary>
    private Brush ResolveThresholdBrush(MetricPanelViewModel panel)
    {
        var applies = panel.Unit == MetricUnit.Celsius
            || (panel.Unit == MetricUnit.Percent && panel.UtilizationPercent > 0);
        if (!applies)
        {
            return panel.AccentBrush;
        }

        return _thresholdLevel switch
        {
            2 => ResolveBrush("CriticalBrush", "#FF9761"),
            1 => ResolveBrush("WarningBrush", "#F0C968"),
            _ => panel.AccentBrush,
        };
    }

    private void SetHoverState(bool isHovered)
    {
        // In edit mode the accent border IS the affordance saying "this panel is draggable".
        // Letting hover overwrite it meant the signal vanished the moment you moved the pointer
        // toward a panel to grab it.
        if (_isFloating || _isLayoutEditMode)
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
            MinHeight = UiMetrics.MinHitTarget,
            MinWidth = 58,
            Text = text,
        };

        button.Click += OnProcessSortClick;
        return button;
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

    private static (ClickableBorder Row, Border InnerBorder, TextBlock NameText, TextBlock ValueText, Ellipse Dot, Line Pattern) CreateLegendRow(string name, Brush strokeBrush)
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

        // Shown instead of the dot while the chart draws series with dash patterns, so the
        // legend shows which line is which without relying on colour.
        var pattern = new Line
        {
            X1 = 1,
            Y1 = 0,
            X2 = 15,
            Y2 = 0,
            Stroke = strokeBrush,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        row.Children.Add(pattern);

        var nameText = new TextBlock
        {
            FontSize = 12,
            Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1"),
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(nameText);
        Grid.SetColumn(nameText, 1);

        // Same family as the headline, tabular. Bahnschrift here made two numeral styles in one card.
        var valueText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            Text = "--",
            MinWidth = 42,
            TextAlignment = TextAlignment.Right,
        };
        UseTabularFigures(valueText);
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

        var wrapper = new ClickableBorder(border, invokable: true);
        return (wrapper, border, nameText, valueText, dot, pattern);
    }

    /// <summary>
    /// Columns shared by the process header and every row, so the labels sit exactly over their
    /// values: name, details, meter, value.
    /// </summary>
    private static void AddProcessColumns(Grid grid)
    {
        grid.ColumnSpacing = 10;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ProcessMeterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 52 });
    }

    private const double ProcessMeterWidth = 48;
    private static readonly Thickness s_processRowPadding = new(10, 6, 10, 6);

    /// <summary>One reusable row of the process list. What it shows changes on every re-sort.</summary>
    private sealed class ProcessRowSlot
    {
        public required ClickableBorder Row { get; init; }
        public required Border RowBorder { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock CaptionText { get; init; }
        public required TextBlock ValueText { get; init; }
        public required Border MeterFill { get; init; }
        public required ScaleTransform Meter { get; init; }
        public required Ellipse ChartDot { get; init; }
        public required ToolTip Tip { get; init; }
        public string? Key { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
        public bool IsHovered { get; set; }
    }

    private static ProcessRowSlot CreateProcessRowSlot()
    {
        var nameText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A pinned process is drawn on the chart; its dot is the line's colour, so the row reads as
        // that line's legend entry.
        var chartDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        var nameGrid = new Grid();
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameGrid.Children.Add(chartDot);
        nameGrid.Children.Add(nameText);
        Grid.SetColumn(nameText, 1);

        var captionText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11,
            Foreground = ResolveBrush("TextMutedBrush", "#90959D"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Meter: a track with a fill scaled by intensity. ScaleX is a render transform, so a
        // value update every scrape never re-runs layout for the row.
        var meter = new ScaleTransform { ScaleX = 0d, ScaleY = 1 };
        var meterFill = new Border
        {
            CornerRadius = new CornerRadius(2),
            RenderTransform = meter,
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5),
        };
        var meterTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = ResolveBrush("SurfaceStrongBrush", "#292D31"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = meterFill,
        };

        var valueText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        UseTabularFigures(valueText);

        var rowGrid = new Grid();
        AddProcessColumns(rowGrid);
        rowGrid.Children.Add(nameGrid);
        rowGrid.Children.Add(captionText);
        Grid.SetColumn(captionText, 1);
        rowGrid.Children.Add(meterTrack);
        Grid.SetColumn(meterTrack, 2);
        rowGrid.Children.Add(valueText);
        Grid.SetColumn(valueText, 3);

        var rowBorder = new Border
        {
            Padding = s_processRowPadding,
            CornerRadius = new CornerRadius(6),
            Background = BrushFactory.CreateBrush("#01000000"),
            Child = rowGrid,
        };

        // One tooltip per slot, its text updated in place, so an open tooltip is never orphaned.
        var tip = new ToolTip();
        ToolTipService.SetToolTip(rowBorder, tip);

        // Invokable: the row is a Tab stop and Enter or Space pins it like a click.
        var slot = new ProcessRowSlot
        {
            Row = new ClickableBorder(rowBorder, invokable: true),
            RowBorder = rowBorder,
            NameText = nameText,
            CaptionText = captionText,
            ValueText = valueText,
            MeterFill = meterFill,
            Meter = meter,
            ChartDot = chartDot,
            Tip = tip,
        };

        // Rest: transparent (pinned: a soft accent wash). Hover: a raised fill. Background only,
        // so hover never re-runs layout.
        rowBorder.PointerEntered += (_, _) =>
        {
            slot.IsHovered = true;
            rowBorder.Background = ResolveBrush("SurfaceElevatedBrush", "#222529");
        };
        rowBorder.PointerExited += (_, _) =>
        {
            slot.IsHovered = false;
            rowBorder.Background = ProcessRowRestBrush(slot.IsPinned);
        };
        return slot;
    }

    private static Brush ProcessRowRestBrush(bool pinned) =>
        pinned ? ResolveBrush("AccentSoftBrush", "#294A6B") : BrushFactory.CreateBrush("#01000000");

    private static void BindProcessRow(ProcessRowSlot slot, ProcessListItemViewModel item, Brush accentBrush)
    {
        var pinStateChanged = slot.Key is null || slot.IsPinned != item.IsPinned;
        slot.Key = item.Key;
        slot.ProcessName = item.Name;
        slot.IsPinned = item.IsPinned;
        slot.Row.Tag = item.Key;
        SetTextIfChanged(slot.NameText, item.Name);
        SetTextIfChanged(slot.CaptionText, item.Caption);
        SetTextIfChanged(slot.ValueText, item.Value);
        slot.Meter.ScaleX = Math.Clamp(item.Intensity, 0d, 1d);
        if (!ReferenceEquals(slot.MeterFill.Background, accentBrush))
        {
            slot.MeterFill.Background = accentBrush;
        }

        if (pinStateChanged)
        {
            slot.NameText.FontWeight = item.IsPinned ? FontWeights.SemiBold : FontWeights.Normal;
            // Muted text on the pinned row's accent tint fell under 4.5:1 in most themes.
            slot.CaptionText.Foreground = ResolveBrush(item.IsPinned ? "TextSecondaryBrush" : "TextMutedBrush", "#90959D");
            if (!slot.IsHovered)
            {
                slot.RowBorder.Background = ProcessRowRestBrush(item.IsPinned);
            }

            slot.Tip.Content = item.IsPinned
                ? $"{item.Name} is pinned to the chart. Click to unpin."
                : $"Click to pin {item.Name} to the chart";
        }
        else if (slot.Tip.Content is not string tipText || !tipText.Contains(item.Name, StringComparison.Ordinal))
        {
            slot.Tip.Content = item.IsPinned
                ? $"{item.Name} is pinned to the chart. Click to unpin."
                : $"Click to pin {item.Name} to the chart";
        }

        if (item.ChartColor is { } chartColor)
        {
            slot.ChartDot.Fill = chartColor;
            slot.ChartDot.Visibility = Visibility.Visible;
        }
        else if (slot.ChartDot.Visibility != Visibility.Collapsed)
        {
            slot.ChartDot.Visibility = Visibility.Collapsed;
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slot.Row, $"{item.Name}, {item.Value}{(item.IsPinned ? ", pinned" : string.Empty)}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(slot.Row, item.IsPinned
            ? "Press Enter to remove it from the chart"
            : "Press Enter to pin it to the chart");
        if (slot.Row.Visibility != Visibility.Visible)
        {
            slot.Row.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Skips equal strings: a new string instance re-measures the TextBlock even when it reads the same.</summary>
    private static void SetTextIfChanged(TextBlock textBlock, string text)
    {
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
        {
            textBlock.Text = text;
        }
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

    private static void UseTabularFigures(TextBlock text)
        => Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);

    private static TextBlock CreateEmptyText(string text) =>
        new()
        {
            FontSize = 12,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            Text = text,
        };

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);

    private static string FormatValue(double value, MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => $"{value:0.#}%",
        MetricUnit.Celsius => $"{value:0.#} °C",
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

/// <summary>A layout gesture's pointer position, in window-content coordinates.</summary>
public sealed class LayoutGestureEventArgs(TelemetryPanelCard.ResizeEdge edge, Windows.Foundation.Point point) : EventArgs
{
    /// <summary>The edges being dragged; <see cref="TelemetryPanelCard.ResizeEdge.None"/> for a move.</summary>
    public TelemetryPanelCard.ResizeEdge Edge { get; } = edge;

    public Windows.Foundation.Point Point { get; } = point;
}

public sealed class LayoutGestureEndedEventArgs(bool cancelled) : EventArgs
{
    public bool Cancelled { get; } = cancelled;
}

/// <summary>One-cell keyboard move (or resize of the right/bottom edge) in grid units.</summary>
public sealed class LayoutNudgeEventArgs(int columns, int rows, bool resize) : EventArgs
{
    public int Columns { get; } = columns;

    public int Rows { get; } = rows;

    public bool Resize { get; } = resize;
}
