using Vaktr.App.ViewModels;
using Vaktr.Core.Models;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using System.Text;
using Windows.UI;

namespace Vaktr.App.Controls;

public sealed class TelemetryChart : UserControl
{
    private const double LeftPadding = 14d;
    private const double RightPadding = 14d;
    private const double TopPadding = 14d;
    private const double BottomPadding = 28d;
    private const double MinimumSelectionWidth = 18d;

    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(
            nameof(Series),
            typeof(IReadOnlyList<ChartSeriesViewModel>),
            typeof(TelemetryChart),
            new PropertyMetadata(Array.Empty<ChartSeriesViewModel>(), OnChartPropertyChanged));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(
            nameof(Unit),
            typeof(MetricUnit),
            typeof(TelemetryChart),
            new PropertyMetadata(MetricUnit.Percent, OnChartPropertyChanged));

    public static readonly DependencyProperty WindowStartUtcProperty =
        DependencyProperty.Register(
            nameof(WindowStartUtc),
            typeof(DateTimeOffset),
            typeof(TelemetryChart),
            new PropertyMetadata(default(DateTimeOffset), OnChartPropertyChanged));

    public static readonly DependencyProperty WindowEndUtcProperty =
        DependencyProperty.Register(
            nameof(WindowEndUtc),
            typeof(DateTimeOffset),
            typeof(TelemetryChart),
            new PropertyMetadata(default(DateTimeOffset), OnChartPropertyChanged));

    public static readonly DependencyProperty CeilingValueProperty =
        DependencyProperty.Register(
            nameof(CeilingValue),
            typeof(double),
            typeof(TelemetryChart),
            new PropertyMetadata(0d, OnChartPropertyChanged));

    public static readonly DependencyProperty EmptyStateTextProperty =
        DependencyProperty.Register(
            nameof(EmptyStateText),
            typeof(string),
            typeof(TelemetryChart),
            new PropertyMetadata("Waiting for samples", OnChartPropertyChanged));

    private readonly Canvas _plotCanvas;
    private readonly Canvas _gridCanvas;
    private readonly Canvas _dataCanvas;
    private readonly Canvas _interactionCanvas;
    private readonly Rectangle _plotFrame;
    private readonly Rectangle _plotTint;
    private readonly Rectangle _leftEdgeFade;
    private readonly Rectangle _rightEdgeFade;
    private readonly Rectangle _selectionRectangle;
    private readonly Line _hoverLine;
    private readonly Border _hoverTooltip;
    private readonly TextBlock _hoverTooltipText;
    private readonly TextBlock _emptyStateText;
    private bool _isSelecting;
    private bool _redrawQueued;
    private bool _redrawUpgradePending;
    private DispatcherQueuePriority _queuedRedrawPriority = DispatcherQueuePriority.Low;
    private double _selectionStartX;
    private double _selectionCurrentX;

    // Pointer-move coalescing. A 1000 Hz mouse raises PointerMoved far more often than the
    // compositor draws, and each hover update rebuilds the tooltip string and runs a Measure()
    // layout pass. Cheap state is written synchronously; the expensive visual work is deferred
    // to at most once per rendered frame.
    private bool _pointerFlushQueued;
    private Windows.Foundation.Point _pendingPointerPosition;
    private Action<double>? _pointerFlush;
    private readonly List<(Border Tooltip, Line Marker)> _pinnedTooltips = [];


    private static readonly FontFamily s_numericFont = new("Segoe UI Variable Text");

    public TelemetryChart()
    {
        UseLayoutRounding = false; // smoother diagonal lines on chart strokes

        _plotCanvas = new Canvas();
        _gridCanvas = new Canvas();
        _dataCanvas = new Canvas();
        _interactionCanvas = new Canvas();

        // Fills beneath every line, so a second series' area never paints over the first's trace.
        _dataCanvas.Children.Add(_fillLayer);
        _dataCanvas.Children.Add(_lineLayer);
        _plotFrame = new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            StrokeThickness = 0,
            Opacity = 0.98,
            IsHitTestVisible = false,
        };
        _plotTint = new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            Opacity = 0.04,
            IsHitTestVisible = false,
        };
        _leftEdgeFade = new Rectangle
        {
            IsHitTestVisible = false,
        };
        _rightEdgeFade = new Rectangle
        {
            IsHitTestVisible = false,
        };
        _plotCanvas.Children.Add(_plotFrame);
        _plotCanvas.Children.Add(_plotTint);
        _plotCanvas.Children.Add(_leftEdgeFade);
        _plotCanvas.Children.Add(_rightEdgeFade);
        RefreshPlotSurfaceThemeResources();
        _selectionRectangle = new Rectangle
        {
            Visibility = Visibility.Collapsed,
            Stroke = ResolveBrush("AccentStrongBrush", "#B7F7FF"),
            StrokeThickness = 1,
            Fill = ResolveBrush("AccentHaloBrush", "#2B8FE6C4"),
            RadiusX = 8,
            RadiusY = 8,
            IsHitTestVisible = false,
        };
        _hoverLine = new Line
        {
            Visibility = Visibility.Collapsed,
            Stroke = ResolveBrush("AccentStrongBrush", "#B7F7FF"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Opacity = 0.55,
            IsHitTestVisible = false,
        };
        _hoverTooltipText = new TextBlock
        {
            FontFamily = s_numericFont,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            MaxWidth = 210,
        };
        _hoverTooltip = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = ResolveBrush("SurfaceElevatedBrush", "#222529"),
            BorderBrush = ResolveBrush("AccentSoftBrush", "#163B60"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Child = _hoverTooltipText,
            IsHitTestVisible = false,
            Translation = new System.Numerics.Vector3(0, 0, 16), // Subtle elevation shadow
        };
        _interactionCanvas.Children.Add(_selectionRectangle);
        _interactionCanvas.Children.Add(_hoverLine);
        _interactionCanvas.Children.Add(_hoverTooltip);
        _emptyStateText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            Text = "Waiting for samples",
            TextAlignment = TextAlignment.Center,
            Opacity = 0.82,
            Visibility = Visibility.Collapsed,
        };
        Content = new Grid
        {
            Children =
            {
                _plotCanvas,
                _gridCanvas,
                _dataCanvas,
                _interactionCanvas,
                _emptyStateText,
            },
        };

        Loaded += (_, _) => ScheduleRedraw();
        SizeChanged += OnChartSizeChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerExited += (_, _) => { CancelPointerFlush(); HideHover(); };
        Unloaded += (_, _) => CancelPointerFlush();
        DoubleTapped += (_, _) =>
        {
            ClearPinnedTooltips();
            ZoomResetRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler<ChartZoomSelectionEventArgs>? ZoomSelectionRequested;

    public event EventHandler? ZoomResetRequested;

    public IReadOnlyList<ChartSeriesViewModel> Series
    {
        get => (IReadOnlyList<ChartSeriesViewModel>)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public MetricUnit Unit
    {
        get => (MetricUnit)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public DateTimeOffset WindowStartUtc
    {
        get => (DateTimeOffset)GetValue(WindowStartUtcProperty);
        set => SetValue(WindowStartUtcProperty, value);
    }

    public DateTimeOffset WindowEndUtc
    {
        get => (DateTimeOffset)GetValue(WindowEndUtcProperty);
        set => SetValue(WindowEndUtcProperty, value);
    }

    public double CeilingValue
    {
        get => (double)GetValue(CeilingValueProperty);
        set => SetValue(CeilingValueProperty, value);
    }

    public string EmptyStateText
    {
        get => (string)GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    private static void OnChartPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((TelemetryChart)dependencyObject).ScheduleRedraw();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Skip if rendering is suspended (window resize) — the redraw will
        // happen when rendering resumes via the deferred refresh path.
        if (_renderingSuspended)
        {
            return;
        }

        // Redraw in this layout pass rather than on a later dispatcher turn. While a panel is
        // being resized the deferred redraw trailed the card's edge by a frame, so the plot
        // visibly lagged the frame around it. Retained rendering makes this cheap enough.
        Redraw();
    }

    private bool _renderingSuspended;

    /// <summary>Called by TelemetryPanelCard to suppress all redraws during resize.</summary>
    public void SetRenderingSuspended(bool suspended)
    {
        _renderingSuspended = suspended;
        if (!suspended)
        {
            // Force a redraw when coming out of suspension
            _lastRenderedSeries = null;
            ScheduleRedraw();
        }
    }

    public void RefreshThemeResources()
    {
        RefreshPlotSurfaceThemeResources();
        _hoverLine.Stroke = ResolveBrush("AccentStrongBrush", "#B7F7FF");
        _hoverTooltip.Background = ResolveBrush("SurfaceElevatedBrush", "#222529");
        _hoverTooltip.BorderBrush = ResolveBrush("AccentSoftBrush", "#163B60");
        _selectionRectangle.Fill = ResolveBrush("AccentHaloBrush", "#2B8FE6C4");
        _hoverTooltipText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _emptyStateText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _emptyStateText.Text = string.IsNullOrWhiteSpace(EmptyStateText) ? "Waiting for samples" : EmptyStateText;
        HideHover();
        // Force immediate redraw — don't defer, theme swap should feel instant
        _lastRenderedSeries = null;
        Redraw();
    }

    private void ScheduleRedraw(DispatcherQueuePriority priority = DispatcherQueuePriority.Low)
    {
        if (_redrawQueued)
        {
            if (GetPriorityRank(priority) > GetPriorityRank(_queuedRedrawPriority))
            {
                _queuedRedrawPriority = priority;
                _redrawUpgradePending = true;
            }

            return;
        }

        _redrawQueued = true;
        _queuedRedrawPriority = priority;
        _ = DispatcherQueue.TryEnqueue(priority, () =>
        {
            _redrawQueued = false;
            Redraw();

            if (_redrawUpgradePending)
            {
                _redrawUpgradePending = false;
                ScheduleRedraw(_queuedRedrawPriority);
            }
        });
    }

    private static int GetPriorityRank(DispatcherQueuePriority priority) => priority switch
    {
        DispatcherQueuePriority.High => 2,
        DispatcherQueuePriority.Normal => 1,
        _ => 0,
    };

    private IReadOnlyList<ChartSeriesViewModel>? _lastRenderedSeries;
    private int _lastRenderedPointHash;
    private DateTimeOffset _lastRenderedStart;
    private DateTimeOffset _lastRenderedEnd;
    private double _lastRenderedCeiling;
    private double _lastRenderedWidth;
    private double _lastRenderedHeight;
    private double _lastResolvedMaxValue;
    private bool _hasRenderedFrame;
    // Effective bounds used for rendering (may differ from Window*Utc when those are default)
    private DateTimeOffset _effectiveRenderStart;
    private DateTimeOffset _effectiveRenderEnd;

    private bool SeriesMatchesLastRendered(IReadOnlyList<ChartSeriesViewModel> series)
    {
        if (_lastRenderedSeries is null || series.Count != _lastRenderedSeries.Count)
        {
            return false;
        }

        // Quick hash: compare series count and last point timestamp of each series
        var hash = series.Count;
        for (var i = 0; i < series.Count; i++)
        {
            var pts = series[i].Points;
            if (pts.Count > 0)
            {
                hash = unchecked(hash * 31 + pts[^1].Timestamp.GetHashCode());
            }
        }

        return hash == _lastRenderedPointHash;
    }

    // ---- Retained rendering ------------------------------------------------------------------
    //
    // Redraw used to clear both canvases and rebuild everything: one LineSegment object per data
    // point (twice when a fill was drawn) plus every grid line and label - roughly 4,400 XAML
    // objects per scrape across the board, and the dominant cost of a live panel resize. Every
    // element is now created once and updated in place. A series is one Path whose figure holds
    // a single PolyLineSegment, so a redraw is arithmetic plus a few property sets.

    private sealed class SeriesVisual
    {
        public required Path Fill { get; init; }
        public required PathGeometry FillGeometry { get; init; }
        public required Path Line { get; init; }
        public required PathGeometry LineGeometry { get; init; }
        public required Ellipse Glow { get; init; }
        public required Ellipse Dot { get; init; }
    }

    private readonly Canvas _fillLayer = new();
    private readonly Canvas _lineLayer = new();
    private readonly List<SeriesVisual> _seriesVisuals = [];
    private readonly List<Ellipse> _soloDots = [];
    private readonly List<Rectangle> _gridBands = [];
    private readonly List<Line> _gridRows = [];
    private readonly List<Line> _gridColumns = [];
    private readonly List<TextBlock> _timeLabels = [];
    private TextBlock? _ceilingLabel;
    private TextBlock? _midLabel;
    private TextBlock? _floorLabel;

    private void Redraw()
    {
        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        if (width < 24 || height < 24)
        {
            return;
        }

        var series = Series ?? Array.Empty<ChartSeriesViewModel>();

        // Skip redraw if nothing meaningful has changed
        if (_hasRenderedFrame &&
            Math.Abs(width - _lastRenderedWidth) < 0.5d &&
            Math.Abs(height - _lastRenderedHeight) < 0.5d &&
            WindowStartUtc == _lastRenderedStart &&
            WindowEndUtc == _lastRenderedEnd &&
            Math.Abs(CeilingValue - _lastRenderedCeiling) < 0.01 &&
            SeriesMatchesLastRendered(series))
        {
            return;
        }

        _lastRenderedSeries = series;
        _lastRenderedStart = WindowStartUtc;
        _lastRenderedEnd = WindowEndUtc;
        _lastRenderedCeiling = CeilingValue;
        _lastRenderedWidth = width;
        _lastRenderedHeight = height;
        var hash = series.Count;
        for (var i = 0; i < series.Count; i++)
        {
            var pts = series[i].Points;
            if (pts.Count > 0)
                hash = unchecked(hash * 31 + pts[^1].Timestamp.GetHashCode());
        }
        _lastRenderedPointHash = hash;

        if (!TryGetPointBounds(series, out var minTimestamp, out var maxTimestamp, out var maxObservedValue))
        {
            var emptyPlotWidth = Math.Max(16d, width - LeftPadding - RightPadding);
            var emptyPlotHeight = Math.Max(16d, height - TopPadding - BottomPadding);
            UpdatePlotSurface(emptyPlotWidth, emptyPlotHeight);
            _lastResolvedMaxValue = ResolveMaxValue(0d);
            DrawGrid(width, height, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, _lastResolvedMaxValue, includeLabels: false);
            HideSeriesVisuals(0);
            HideFrom(_soloDots, 0);
            // Plain text: an emoji hourglass clashed with the Fluent icon set used everywhere else.
            _emptyStateText.Text = string.IsNullOrWhiteSpace(EmptyStateText) ? "Waiting for samples" : EmptyStateText;
            _emptyStateText.Visibility = Visibility.Visible;
            _hasRenderedFrame = true;
            HideHover();
            return;
        }

        _emptyStateText.Visibility = Visibility.Collapsed;

        var start = WindowStartUtc == default ? minTimestamp : WindowStartUtc;
        var end = WindowEndUtc == default ? maxTimestamp : WindowEndUtc;
        if (end <= start)
        {
            end = start.AddMinutes(1);
        }

        _effectiveRenderStart = start;
        _effectiveRenderEnd = end;

        var maxValue = ResolveMaxValue(maxObservedValue);
        _lastResolvedMaxValue = maxValue;
        var plotWidth = Math.Max(16d, width - LeftPadding - RightPadding);
        var plotHeight = Math.Max(16d, height - TopPadding - BottomPadding);
        var plotBottom = TopPadding + plotHeight;
        var seriesCount = series.Count;
        var drawFilledArea = seriesCount <= 3;
        var drawPointMarkers = seriesCount <= 4;
        var pointBudget = ResolvePointBudget(width, seriesCount, end - start);
        // A touch heavier for sparse series — a confident trace over a soft fill is what makes a
        // chart read as designed rather than plotted.
        var strokeThickness = seriesCount <= 2 ? 2.4d : seriesCount <= 6 ? 1.9d : 1.4d;

        UpdatePlotSurface(plotWidth, plotHeight);
        DrawGrid(width, height, start, end, maxValue);

        var visualIndex = 0;
        var soloIndex = 0;
        var runs = new List<IReadOnlyList<Windows.Foundation.Point>>();
        foreach (var item in series)
        {
            if (item.Points.Count == 0)
            {
                continue;
            }

            var renderablePoints = GetRenderablePoints(item.Points, start, end);
            if (renderablePoints.Count == 0)
            {
                continue;
            }

            var downsampled = Downsample(renderablePoints, pointBudget);

            // Split into contiguous segments at gaps (>3x the expected interval between points)
            runs.Clear();
            foreach (var segment in SplitAtGaps(downsampled, end - start))
            {
                var rawProjected = new Windows.Foundation.Point[segment.Count];
                for (var pi = 0; pi < segment.Count; pi++)
                {
                    rawProjected[pi] = Project(segment[pi], LeftPadding, TopPadding, plotWidth, plotHeight, start, end, maxValue);
                }

                var projected = NormalizeProjectedPoints(rawProjected);
                if (projected.Count == 1)
                {
                    PlaceDot(soloIndex++, projected[0], item.StrokeBrush);
                }
                else if (projected.Count > 1)
                {
                    runs.Add(projected);
                }
            }

            var visual = SeriesVisualAt(visualIndex++);
            if (runs.Count > 0)
            {
                if (drawFilledArea)
                {
                    SetFigures(visual.FillGeometry, runs, plotBottom, closed: true);
                    visual.Fill.Fill = item.FillBrush;
                    // Single series can carry its full gradient; stacked series stay
                    // translucent so overlapping fills don't turn to mud.
                    visual.Fill.Opacity = seriesCount == 1 ? 1.0 : 0.45;
                    visual.Fill.Visibility = Visibility.Visible;
                }
                else
                {
                    visual.Fill.Visibility = Visibility.Collapsed;
                }

                SetFigures(visual.LineGeometry, runs, plotBottom, closed: false);
                visual.Line.Stroke = item.StrokeBrush;
                visual.Line.StrokeThickness = strokeThickness;
                visual.Line.Visibility = Visibility.Visible;
            }
            else
            {
                visual.Fill.Visibility = Visibility.Collapsed;
                visual.Line.Visibility = Visibility.Collapsed;
            }

            // Latest-point marker only for primary (non-dimmed) series; a low-alpha stroke
            // means the series is unfocused.
            var isDimmed = item.StrokeBrush is SolidColorBrush scb && scb.Color.A < 80;
            if (drawPointMarkers && !isDimmed)
            {
                var lastPoint = Project(renderablePoints[^1], LeftPadding, TopPadding, plotWidth, plotHeight, start, end, maxValue);
                visual.Glow.Fill = item.StrokeBrush;
                visual.Dot.Fill = item.StrokeBrush;
                Canvas.SetLeft(visual.Glow, lastPoint.X - (visual.Glow.Width / 2d));
                Canvas.SetTop(visual.Glow, lastPoint.Y - (visual.Glow.Height / 2d));
                Canvas.SetLeft(visual.Dot, lastPoint.X - (visual.Dot.Width / 2d));
                Canvas.SetTop(visual.Dot, lastPoint.Y - (visual.Dot.Height / 2d));
                visual.Glow.Visibility = Visibility.Visible;
                visual.Dot.Visibility = Visibility.Visible;
            }
            else
            {
                visual.Glow.Visibility = Visibility.Collapsed;
                visual.Dot.Visibility = Visibility.Collapsed;
            }
        }

        HideSeriesVisuals(visualIndex);
        HideFrom(_soloDots, soloIndex);
        _hasRenderedFrame = true;
    }

    private SeriesVisual SeriesVisualAt(int index)
    {
        while (_seriesVisuals.Count <= index)
        {
            var fillGeometry = new PathGeometry();
            var lineGeometry = new PathGeometry();
            var visual = new SeriesVisual
            {
                Fill = new Path { Data = fillGeometry, IsHitTestVisible = false },
                FillGeometry = fillGeometry,
                Line = new Path
                {
                    Data = lineGeometry,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                },
                LineGeometry = lineGeometry,
                Glow = new Ellipse { Width = 14, Height = 14, Opacity = 0.22, IsHitTestVisible = false },
                Dot = new Ellipse { Width = 5.5, Height = 5.5, IsHitTestVisible = false },
            };
            _fillLayer.Children.Add(visual.Fill);
            _lineLayer.Children.Add(visual.Line);
            _lineLayer.Children.Add(visual.Glow);
            _lineLayer.Children.Add(visual.Dot);
            _seriesVisuals.Add(visual);
        }

        return _seriesVisuals[index];
    }

    private void HideSeriesVisuals(int from)
    {
        for (var index = from; index < _seriesVisuals.Count; index++)
        {
            var visual = _seriesVisuals[index];
            visual.Fill.Visibility = Visibility.Collapsed;
            visual.Line.Visibility = Visibility.Collapsed;
            visual.Glow.Visibility = Visibility.Collapsed;
            visual.Dot.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Writes each gap-free run as one figure with one PolyLineSegment, reusing figures. A fill
    /// closes down to the plot floor at both ends.
    /// </summary>
    private static void SetFigures(
        PathGeometry geometry,
        List<IReadOnlyList<Windows.Foundation.Point>> runs,
        double plotBottom,
        bool closed)
    {
        var figures = geometry.Figures;
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            PathFigure figure;
            PolyLineSegment polyline;
            if (index < figures.Count && figures[index].Segments.Count == 1 && figures[index].Segments[0] is PolyLineSegment existing)
            {
                figure = figures[index];
                polyline = existing;
            }
            else
            {
                polyline = new PolyLineSegment();
                figure = new PathFigure();
                figure.Segments.Add(polyline);
                if (index < figures.Count)
                {
                    figures[index] = figure;
                }
                else
                {
                    figures.Add(figure);
                }
            }

            // A fresh collection, assigned: in-place edits to a PointCollection are not reliably
            // picked up by the renderer, while a property assignment always is.
            var points = new PointCollection();
            if (closed)
            {
                figure.StartPoint = new Windows.Foundation.Point(run[0].X, plotBottom);
                for (var k = 0; k < run.Count; k++)
                {
                    points.Add(run[k]);
                }

                points.Add(new Windows.Foundation.Point(run[^1].X, plotBottom));
            }
            else
            {
                figure.StartPoint = run[0];
                for (var k = 1; k < run.Count; k++)
                {
                    points.Add(run[k]);
                }
            }

            figure.IsClosed = closed;
            figure.IsFilled = closed;
            polyline.Points = points;
        }

        while (figures.Count > runs.Count)
        {
            figures.RemoveAt(figures.Count - 1);
        }
    }

    private void PlaceDot(int index, Windows.Foundation.Point point, Brush brush)
    {
        const double size = 5;
        var dot = Pooled(_soloDots, index, _lineLayer, () => new Ellipse { Width = size, Height = size, IsHitTestVisible = false });
        dot.Fill = brush;
        Canvas.SetLeft(dot, point.X - (size / 2d));
        Canvas.SetTop(dot, point.Y - (size / 2d));
    }

    /// <summary>Returns pool element <paramref name="index"/>, creating it on first use, visible.</summary>
    private static T Pooled<T>(List<T> pool, int index, Canvas host, Func<T> create)
        where T : UIElement
    {
        while (pool.Count <= index)
        {
            var created = create();
            pool.Add(created);
            host.Children.Add(created);
        }

        var element = pool[index];
        element.Visibility = Visibility.Visible;
        return element;
    }

    private static void HideFrom<T>(List<T> pool, int count)
        where T : UIElement
    {
        for (var index = count; index < pool.Count; index++)
        {
            pool[index].Visibility = Visibility.Collapsed;
        }
    }

    private static void SetText(TextBlock block, string text)
    {
        // An identical string still invalidates text layout.
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }

    private void UpdatePlotSurface(double plotWidth, double plotHeight)
    {
        var edgeFadeWidth = Math.Min(24d, plotWidth * 0.06);
        _plotFrame.Width = plotWidth;
        _plotFrame.Height = plotHeight;
        _plotTint.Width = plotWidth;
        _plotTint.Height = plotHeight;
        _leftEdgeFade.Width = edgeFadeWidth;
        _leftEdgeFade.Height = plotHeight;
        _rightEdgeFade.Width = edgeFadeWidth;
        _rightEdgeFade.Height = plotHeight;

        Canvas.SetLeft(_plotFrame, LeftPadding);
        Canvas.SetTop(_plotFrame, TopPadding);
        Canvas.SetLeft(_plotTint, LeftPadding);
        Canvas.SetTop(_plotTint, TopPadding);
        Canvas.SetLeft(_leftEdgeFade, LeftPadding);
        Canvas.SetTop(_leftEdgeFade, TopPadding);
        Canvas.SetLeft(_rightEdgeFade, LeftPadding + plotWidth - edgeFadeWidth);
        Canvas.SetTop(_rightEdgeFade, TopPadding);
    }

    private void RefreshPlotSurfaceThemeResources()
    {
        // No outline. The inset fill already separates the plot from the card, and a hard
        // stroked rectangle around every chart — inside a bordered card, inside a bordered
        // panel — was the main source of the nested-boxes look.
        _plotFrame.Stroke = null;
        _plotFrame.Fill = ResolveBrush("SurfaceInsetBrush", "#091321");
        _plotTint.Fill = ResolveBrush("AccentSoftBrush", "#10394D");
        _leftEdgeFade.Fill = CreateEdgeFadeBrush(isLeadingEdge: true);
        _rightEdgeFade.Fill = CreateEdgeFadeBrush(isLeadingEdge: false);
    }

    private void DrawGrid(double width, double height, DateTimeOffset start, DateTimeOffset end, double maxValue, bool includeLabels = true)
    {
        var gridBrush = ResolveBrush("SurfaceGridBrush", "#35587A");
        var plotWidth = Math.Max(16d, width - LeftPadding - RightPadding);
        var plotHeight = Math.Max(16d, height - TopPadding - BottomPadding);
        var bottomY = TopPadding + plotHeight;
        var divisions = width >= 760 ? 5 : width >= 540 ? 4 : 3;

        for (var index = 0; index < divisions; index++)
        {
            var band = Pooled(_gridBands, index, _gridCanvas, () => new Rectangle { IsHitTestVisible = false });
            band.Width = plotWidth;
            band.Height = plotHeight / divisions;
            band.Fill = gridBrush;
            band.Opacity = index % 2 == 0 ? 0.06 : 0.03;
            Canvas.SetLeft(band, LeftPadding);
            Canvas.SetTop(band, TopPadding + ((plotHeight / divisions) * index));
        }

        HideFrom(_gridBands, divisions);

        for (var index = 0; index <= divisions; index++)
        {
            var y = TopPadding + ((plotHeight / divisions) * index);
            var row = Pooled(_gridRows, index, _gridCanvas, () => new Line { StrokeThickness = 1, IsHitTestVisible = false });
            row.Stroke = gridBrush;
            row.Opacity = index is 0 || index == divisions ? 0.28 : 0.16;
            row.X1 = LeftPadding;
            row.X2 = LeftPadding + plotWidth;
            row.Y1 = y;
            row.Y2 = y;
        }

        HideFrom(_gridRows, divisions + 1);

        var columnIndex = 0;
        var labelIndex = 0;
        for (var index = 0; index <= divisions; index++)
        {
            var x = LeftPadding + ((plotWidth / divisions) * index);
            if (index is not 0 && index != divisions)
            {
                var column = Pooled(_gridColumns, columnIndex++, _gridCanvas, () => new Line { StrokeThickness = 1, Opacity = 0.08, IsHitTestVisible = false });
                column.Stroke = gridBrush;
                column.X1 = x;
                column.X2 = x;
                column.Y1 = TopPadding;
                column.Y2 = bottomY;
            }

            if (!includeLabels)
            {
                continue;
            }

            // Skip interior labels at narrow widths to prevent overlap
            var labelSpacing = plotWidth / divisions;
            if (labelSpacing < 70 && index != 0 && index != divisions)
            {
                continue;
            }

            var tick = start + TimeSpan.FromTicks((end - start).Ticks / divisions * index);
            var label = Pooled(_timeLabels, labelIndex++, _gridCanvas, () => CreateAxisLabel(10));
            label.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            SetText(label, FormatTimeLabel(tick, end - start));
            Canvas.SetLeft(label, Math.Clamp(x - 20, LeftPadding, width - 58));
            Canvas.SetTop(label, bottomY + 4);
        }

        HideFrom(_gridColumns, columnIndex);
        HideFrom(_timeLabels, labelIndex);

        _ceilingLabel ??= AddAxisLabel(10);
        _midLabel ??= AddAxisLabel(10);
        _floorLabel ??= AddAxisLabel(10);
        if (!includeLabels)
        {
            _ceilingLabel.Visibility = Visibility.Collapsed;
            _midLabel.Visibility = Visibility.Collapsed;
            _floorLabel.Visibility = Visibility.Collapsed;
            return;
        }

        _ceilingLabel.Foreground = ResolveBrush("TextSecondaryBrush", "#A8C2DA");
        SetText(_ceilingLabel, FormatAxisValue(maxValue, Unit));
        Canvas.SetLeft(_ceilingLabel, LeftPadding);
        Canvas.SetTop(_ceilingLabel, 2);
        _ceilingLabel.Visibility = Visibility.Visible;

        // Mid-point Y-axis label for context
        var showMid = maxValue > 0 && plotHeight >= 80;
        _midLabel.Visibility = showMid ? Visibility.Visible : Visibility.Collapsed;
        if (showMid)
        {
            _midLabel.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            SetText(_midLabel, FormatAxisValue(maxValue / 2d, Unit));
            Canvas.SetLeft(_midLabel, LeftPadding);
            Canvas.SetTop(_midLabel, TopPadding + (plotHeight / 2d) - 6);
        }

        _floorLabel.Foreground = ResolveBrush("TextSecondaryBrush", "#A8C2DA");
        SetText(_floorLabel, FormatAxisValue(0d, Unit));
        Canvas.SetLeft(_floorLabel, LeftPadding);
        Canvas.SetTop(_floorLabel, bottomY - 15);
        _floorLabel.Visibility = Visibility.Visible;
    }

    private static TextBlock CreateAxisLabel(double fontSize, double opacity = 1d)
    {
        var label = new TextBlock
        {
            FontFamily = s_numericFont,
            FontSize = fontSize,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        return label;
    }

    private TextBlock AddAxisLabel(double fontSize, double opacity = 1d)
    {
        var label = CreateAxisLabel(fontSize, opacity);
        _gridCanvas.Children.Add(label);
        return label;
    }

    private double ResolveMaxValue(double peak)
    {
        if (CeilingValue > 0d)
        {
            return CeilingValue;
        }

        if (Unit == MetricUnit.Percent)
        {
            return 100d;
        }

        if (peak <= 0d)
        {
            return Unit == MetricUnit.Gigabytes ? 1d : 100d;
        }

        return peak * 1.14d;
    }

    private static IReadOnlyList<MetricPoint> Downsample(IReadOnlyList<MetricPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints || maxPoints < 3)
        {
            return points;
        }

        // Deterministic min/max bucket downsampling: divide into fixed-width time
        // buckets and keep the min and max value per bucket (in chronological order).
        // This produces stable output regardless of when the render occurs.
        var bucketCount = maxPoints / 2;
        if (bucketCount < 2) bucketCount = 2;

        var startMs = points[0].Timestamp.ToUnixTimeMilliseconds();
        var endMs = points[^1].Timestamp.ToUnixTimeMilliseconds();
        var rangeMs = Math.Max(1d, endMs - startMs);
        var bucketWidthMs = rangeMs / bucketCount;

        var sampled = new List<MetricPoint>(maxPoints + 2);
        sampled.Add(points[0]);

        var bucketIndex = 0;
        var bucketEndMs = startMs + bucketWidthMs;
        var hasMin = false;
        MetricPoint minPoint = points[0];
        MetricPoint maxPoint = points[0];

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var pointMs = point.Timestamp.ToUnixTimeMilliseconds();

            while (pointMs >= bucketEndMs && bucketIndex < bucketCount - 1)
            {
                if (hasMin)
                {
                    if (minPoint.Timestamp <= maxPoint.Timestamp)
                    {
                        sampled.Add(minPoint);
                        if (minPoint.Timestamp != maxPoint.Timestamp)
                            sampled.Add(maxPoint);
                    }
                    else
                    {
                        sampled.Add(maxPoint);
                        if (minPoint.Timestamp != maxPoint.Timestamp)
                            sampled.Add(minPoint);
                    }
                }

                bucketIndex++;
                bucketEndMs = startMs + ((bucketIndex + 1) * bucketWidthMs);
                hasMin = false;
            }

            if (!hasMin)
            {
                minPoint = point;
                maxPoint = point;
                hasMin = true;
            }
            else
            {
                if (point.Value < minPoint.Value) minPoint = point;
                if (point.Value > maxPoint.Value) maxPoint = point;
            }
        }

        if (hasMin)
        {
            if (minPoint.Timestamp <= maxPoint.Timestamp)
            {
                sampled.Add(minPoint);
                if (minPoint.Timestamp != maxPoint.Timestamp)
                    sampled.Add(maxPoint);
            }
            else
            {
                sampled.Add(maxPoint);
                if (minPoint.Timestamp != maxPoint.Timestamp)
                    sampled.Add(minPoint);
            }
        }

        if (sampled.Count > 0 && sampled[^1].Timestamp != points[^1].Timestamp)
        {
            sampled.Add(points[^1]);
        }

        return sampled;
    }

    private static int ResolvePointBudget(double width, int seriesCount, TimeSpan window)
    {
        var density = seriesCount switch
        {
            <= 1 => 0.50d,
            2 => 0.40d,
            <= 4 => 0.30d,
            <= 8 => 0.22d,
            _ => 0.16d,
        };

        var windowFactor = window switch
        {
            _ when window >= TimeSpan.FromDays(365) => 0.12d,
            _ when window >= TimeSpan.FromDays(90) => 0.15d,
            _ when window >= TimeSpan.FromDays(30) => 0.20d,
            _ when window >= TimeSpan.FromDays(7) => 0.25d,
            _ when window >= TimeSpan.FromDays(2) => 0.28d,
            _ when window >= TimeSpan.FromDays(1) => 0.32d,
            _ when window >= TimeSpan.FromHours(12) => 0.40d,
            _ when window >= TimeSpan.FromHours(1) => 0.50d,
            _ => 0.65d,
        };

        return Math.Max(24, (int)Math.Round(width * density * windowFactor));
    }

    private static IReadOnlyList<MetricPoint> GetRenderablePoints(
        IReadOnlyList<MetricPoint> points,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (points.Count == 0)
        {
            return Array.Empty<MetricPoint>();
        }

        // Binary search for start index
        var lo = 0;
        var hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (points[mid].Timestamp < start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo >= points.Count || points[lo].Timestamp > end)
        {
            var nearest = FindNearestPoint(points, start);
            return nearest is null
                ? Array.Empty<MetricPoint>()
                :
                [
                    new MetricPoint(start, nearest.Value),
                    new MetricPoint(end, nearest.Value),
                ];
        }

        // Find end index with a single scan, then slice as an array
        var endIdx = lo;
        while (endIdx < points.Count && points[endIdx].Timestamp <= end)
        {
            endIdx++;
        }

        var count = endIdx - lo;
        if (count == 0)
        {
            return Array.Empty<MetricPoint>();
        }

        var filtered = new MetricPoint[count];
        for (var i = 0; i < count; i++)
        {
            filtered[i] = points[lo + i];
        }

        return filtered;
    }

    private static IReadOnlyList<Windows.Foundation.Point> NormalizeProjectedPoints(Windows.Foundation.Point[] source)
    {
        if (source.Length == 0)
        {
            return source;
        }

        var normalized = new List<Windows.Foundation.Point>(source.Length);
        var active = source[0];
        normalized.Add(active);

        for (var index = 1; index < source.Length; index++)
        {
            var point = source[index];
            if (Math.Abs(point.X - active.X) < 0.8d)
            {
                active = point;
                normalized[^1] = point;
                continue;
            }

            active = point;
            normalized.Add(point);
        }

        return normalized;
    }

    private static bool TryGetPointBounds(
        IReadOnlyList<ChartSeriesViewModel> series,
        out DateTimeOffset minTimestamp,
        out DateTimeOffset maxTimestamp,
        out double maxObservedValue)
    {
        minTimestamp = default;
        maxTimestamp = default;
        maxObservedValue = 0d;
        var found = false;

        // Points within each series are sorted by timestamp, so we can use
        // first/last for timestamp bounds. Only value needs full scan, but
        // the ViewModel already downsamples to a small point budget.
        foreach (var item in series)
        {
            if (item.Points.Count == 0)
            {
                continue;
            }

            var seriesMin = item.Points[0].Timestamp;
            var seriesMax = item.Points[^1].Timestamp;

            if (!found)
            {
                minTimestamp = seriesMin;
                maxTimestamp = seriesMax;
                found = true;
            }
            else
            {
                if (seriesMin < minTimestamp) minTimestamp = seriesMin;
                if (seriesMax > maxTimestamp) maxTimestamp = seriesMax;
            }

            foreach (var point in item.Points)
            {
                if (point.Value > maxObservedValue)
                {
                    maxObservedValue = point.Value;
                }
            }
        }

        return found;
    }

    private static Windows.Foundation.Point Project(
        MetricPoint point,
        double leftPadding,
        double topPadding,
        double plotWidth,
        double plotHeight,
        DateTimeOffset start,
        DateTimeOffset end,
        double maxValue)
    {
        var totalMs = Math.Max(1d, (end - start).TotalMilliseconds);
        var xRatio = (point.Timestamp - start).TotalMilliseconds / totalMs;
        var yRatio = maxValue <= 0d ? 0d : point.Value / maxValue;

        return new Windows.Foundation.Point(
            leftPadding + (Math.Clamp(xRatio, 0d, 1d) * plotWidth),
            topPadding + plotHeight - (Math.Clamp(yRatio, 0d, 1d) * plotHeight));
    }

    private static IReadOnlyList<IReadOnlyList<MetricPoint>> SplitAtGaps(
        IReadOnlyList<MetricPoint> points,
        TimeSpan windowSpan)
    {
        if (points.Count <= 1)
        {
            return [points];
        }

        // Detect the typical interval using median-of-sample approach (avoids sorting all intervals).
        // Sample up to 15 intervals evenly spaced through the data to find the typical gap.
        var medianIntervalMs = 0d;
        if (points.Count >= 3)
        {
            var sampleCount = Math.Min(15, points.Count - 1);
            var step = Math.Max(1, (points.Count - 1) / sampleCount);
            var sampleIntervals = new double[sampleCount];
            var si = 0;
            for (var i = step; i < points.Count && si < sampleCount; i += step)
            {
                sampleIntervals[si++] = (points[i].Timestamp - points[i - step].Timestamp).TotalMilliseconds / step;
            }
            if (si > 0)
            {
                Array.Sort(sampleIntervals, 0, si);
                medianIntervalMs = sampleIntervals[si / 2];
            }
        }

        // Minimum gap threshold: 3x the median interval, but at least 10 seconds.
        // For rolled-up data (60s intervals), this gives a 180s threshold.
        var gapThresholdMs = Math.Max(medianIntervalMs * 3d, 10_000d);

        // Fast path: check if there are any gaps at all before allocating segment lists
        var hasGap = false;
        for (var i = 1; i < points.Count; i++)
        {
            if ((points[i].Timestamp - points[i - 1].Timestamp).TotalMilliseconds > gapThresholdMs)
            {
                hasGap = true;
                break;
            }
        }

        if (!hasGap)
        {
            return [points];
        }

        var segments = new List<IReadOnlyList<MetricPoint>>();
        var segStart = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if ((points[i].Timestamp - points[i - 1].Timestamp).TotalMilliseconds > gapThresholdMs)
            {
                if (i > segStart)
                {
                    var segment = new MetricPoint[i - segStart];
                    for (var j = 0; j < segment.Length; j++)
                        segment[j] = points[segStart + j];
                    segments.Add(segment);
                }
                segStart = i;
            }
        }

        if (segStart < points.Count)
        {
            var segment = new MetricPoint[points.Count - segStart];
            for (var j = 0; j < segment.Length; j++)
                segment[j] = points[segStart + j];
            segments.Add(segment);
        }

        return segments;
    }

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static Brush CreateEdgeFadeBrush(bool isLeadingEdge)
    {
        var plotBackground = ThemeVisuals.ResolveColor("SurfaceInsetBrush", "#141619");

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop
                {
                    Color = Color.FromArgb(isLeadingEdge ? (byte)40 : (byte)0, plotBackground.R, plotBackground.G, plotBackground.B),
                    Offset = 0,
                },
                new GradientStop
                {
                    Color = Color.FromArgb(isLeadingEdge ? (byte)0 : (byte)40, plotBackground.R, plotBackground.G, plotBackground.B),
                    Offset = 1,
                },
            },
        };
    }

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);

    private static string FormatTimeLabel(DateTimeOffset timestamp, TimeSpan window)
    {
        var local = timestamp.LocalDateTime;
        var isToday = local.Date == DateTime.Today;

        if (window <= TimeSpan.FromMinutes(5))
        {
            return local.ToString("HH:mm:ss");
        }

        if (window <= TimeSpan.FromHours(12) && isToday)
        {
            return local.ToString("HH:mm");
        }

        if (window <= TimeSpan.FromHours(12))
        {
            // Data from a different day - show the date
            return local.ToString("MMM d HH:mm");
        }

        if (window <= TimeSpan.FromDays(2))
        {
            return local.ToString("MMM d HH:mm");
        }

        if (window <= TimeSpan.FromDays(90))
        {
            return local.ToString("MMM d");
        }

        return local.ToString("yyyy-MM-dd");
    }

    private static string FormatAxisValue(double value, MetricUnit unit) => unit switch
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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        HideHover();
        _isSelecting = true;
        _selectionStartX = point.Position.X;
        _selectionCurrentX = _selectionStartX;
        UpdateSelectionVisual();
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pendingPointerPosition = e.GetCurrentPoint(this).Position;

        // Kept synchronous so CompleteSelection always sees the true release position even if
        // the final move's visual update was coalesced away.
        if (_isSelecting)
        {
            _selectionCurrentX = _pendingPointerPosition.X;
            e.Handled = true;
        }

        QueuePointerFlush();
    }

    private void QueuePointerFlush()
    {
        if (_pointerFlushQueued) return;
        _pointerFlushQueued = true;
        _pointerFlush ??= FlushPointerVisual;
        MotionTicker.Add(_pointerFlush);
    }

    private void CancelPointerFlush()
    {
        if (!_pointerFlushQueued || _pointerFlush is null) return;
        _pointerFlushQueued = false;
        MotionTicker.Remove(_pointerFlush);
    }

    private void FlushPointerVisual(double dt)
    {
        CancelPointerFlush();

        if (_isSelecting)
        {
            UpdateSelectionVisual();
        }
        else
        {
            UpdateHover(_pendingPointerPosition);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        // Flip the flag BEFORE releasing capture so OnPointerCaptureLost
        // (which fires synchronously from ReleasePointerCaptures) doesn't
        // re-enter CompleteSelection and undo the just-pinned tooltip.
        _isSelecting = false;
        ReleasePointerCaptures();
        CompleteSelection();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        CompleteSelection();
    }

    private void CompleteSelection()
    {
        _isSelecting = false;
        CancelPointerFlush();
        _selectionRectangle.Visibility = Visibility.Collapsed;

        var left = Math.Min(_selectionStartX, _selectionCurrentX);
        var right = Math.Max(_selectionStartX, _selectionCurrentX);
        if (right - left < MinimumSelectionWidth)
        {
            // Short click (not a drag) — pin a tooltip at this position
            TryPinTooltip(_selectionStartX);
            return;
        }

        // Use the effective rendering bounds (which account for default Window*Utc
        // falling back to data min/max) so the pixel→timestamp conversion matches
        // exactly what the user sees on screen. Fall back to Window properties.
        var renderStart = _effectiveRenderEnd > _effectiveRenderStart ? _effectiveRenderStart : WindowStartUtc;
        var renderEnd = _effectiveRenderEnd > _effectiveRenderStart ? _effectiveRenderEnd : WindowEndUtc;
        if (renderEnd <= renderStart)
        {
            return;
        }

        var plotWidth = Math.Max(16d, ActualWidth - LeftPadding - RightPadding);
        var clampedLeft = Math.Clamp(left, LeftPadding, LeftPadding + plotWidth);
        var clampedRight = Math.Clamp(right, LeftPadding, LeftPadding + plotWidth);
        if (clampedRight - clampedLeft < MinimumSelectionWidth)
        {
            return;
        }

        var normalizedLeft = (clampedLeft - LeftPadding) / plotWidth;
        var normalizedRight = (clampedRight - LeftPadding) / plotWidth;
        var start = renderStart + TimeSpan.FromTicks((long)((renderEnd - renderStart).Ticks * normalizedLeft));
        var end = renderStart + TimeSpan.FromTicks((long)((renderEnd - renderStart).Ticks * normalizedRight));

        if (end > start)
        {
            ZoomSelectionRequested?.Invoke(this, new ChartZoomSelectionEventArgs(start, end));
        }
    }

    private void UpdateSelectionVisual()
    {
        var left = Math.Min(_selectionStartX, _selectionCurrentX);
        var width = Math.Abs(_selectionCurrentX - _selectionStartX);
        _selectionRectangle.Visibility = width >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _selectionRectangle.Width = width;
        _selectionRectangle.Height = Math.Max(0d, ActualHeight - BottomPadding);
        Canvas.SetLeft(_selectionRectangle, left);
        Canvas.SetTop(_selectionRectangle, 0);
    }

    private void UpdateHover(Windows.Foundation.Point position)
    {
        if (_isSelecting || Series is null || Series.Count == 0)
        {
            HideHover();
            return;
        }

        var plotWidth = Math.Max(16d, ActualWidth - LeftPadding - RightPadding);
        var plotHeight = Math.Max(16d, ActualHeight - TopPadding - BottomPadding);
        if (position.X < LeftPadding || position.X > LeftPadding + plotWidth || position.Y < TopPadding || position.Y > TopPadding + plotHeight)
        {
            HideHover();
            return;
        }

        if (!TryBuildHoverSnapshot(position.X, plotWidth, plotHeight, out var hoverX, out var tooltip))
        {
            HideHover();
            return;
        }

        _hoverLine.Visibility = Visibility.Visible;
        _hoverLine.X1 = hoverX;
        _hoverLine.X2 = hoverX;
        _hoverLine.Y1 = TopPadding;
        _hoverLine.Y2 = TopPadding + plotHeight;

        _hoverTooltipText.Text = tooltip;
        _hoverTooltip.Measure(new Windows.Foundation.Size(240, double.PositiveInfinity));
        var desired = _hoverTooltip.DesiredSize;

        // Prefer right side of hover line, flip to left only when near right edge
        var tooltipLeft = hoverX + 14;
        if (tooltipLeft + desired.Width + 8 > ActualWidth)
        {
            tooltipLeft = hoverX - desired.Width - 14;
        }

        // Follow mouse Y with clamping
        var tooltipTop = Math.Clamp(position.Y - desired.Height / 2, TopPadding + 4, Math.Max(TopPadding + 4, ActualHeight - desired.Height - 8));
        _hoverTooltip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_hoverTooltip, Math.Clamp(tooltipLeft, 8, Math.Max(8, ActualWidth - desired.Width - 8)));
        Canvas.SetTop(_hoverTooltip, tooltipTop);
    }

    private bool TryBuildHoverSnapshot(double pointerX, double plotWidth, double plotHeight, out double hoverX, out string tooltip)
    {
        hoverX = 0d;
        tooltip = string.Empty;

        if (_lastResolvedMaxValue <= 0d)
        {
            return false;
        }

        // Prefer the effective render bounds (set by Redraw) but fall back to
        // WindowStartUtc/WindowEndUtc if Redraw hasn't set them yet.
        var start = _effectiveRenderEnd > _effectiveRenderStart ? _effectiveRenderStart : WindowStartUtc;
        var end = _effectiveRenderEnd > _effectiveRenderStart ? _effectiveRenderEnd : WindowEndUtc;
        if (end <= start)
        {
            return false;
        }

        var maxValue = _lastResolvedMaxValue;
        var normalized = Math.Clamp((pointerX - LeftPadding) / plotWidth, 0d, 1d);
        var target = start + TimeSpan.FromTicks((long)((end - start).Ticks * normalized));
        var lines = new List<(string Name, double Value)>();
        DateTimeOffset? anchorTime = null;
        var bestDistance = TimeSpan.MaxValue;

        foreach (var series in Series)
        {
            if (series.Points.Count == 0)
            {
                continue;
            }

            var nearestPoint = FindNearestPoint(series.Points, target);
            if (nearestPoint is null)
            {
                continue;
            }

            var distance = (nearestPoint.Timestamp - target).Duration();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                anchorTime = nearestPoint.Timestamp;
            }

            lines.Add((series.Name, nearestPoint.Value));
        }

        if (anchorTime is null || lines.Count == 0)
        {
            return false;
        }

        hoverX = Project(
            new MetricPoint(anchorTime.Value, 0d),
            LeftPadding,
            TopPadding,
            plotWidth,
            plotHeight,
            start,
            end,
            Math.Max(maxValue, 1d)).X;

        var builder = new StringBuilder();
        builder.AppendLine(FormatHoverTime(anchorTime.Value, end - start));

        // Sort by value descending, show top 5 with clean formatting
        lines.Sort((a, b) => b.Value.CompareTo(a.Value));
        var visibleLines = Math.Min(lines.Count, 5);
        for (var i = 0; i < visibleLines; i++)
        {
            var line = lines[i];
            builder.AppendLine($"\u25CF {line.Name}  {FormatAxisValue(line.Value, Unit)}");
        }

        if (lines.Count > 5)
        {
            builder.Append($"+{lines.Count - 5} more");
        }

        tooltip = builder.ToString().TrimEnd();
        return true;
    }

    private static MetricPoint? FindNearestPoint(IReadOnlyList<MetricPoint> points, DateTimeOffset target)
    {
        if (points.Count == 0)
        {
            return null;
        }

        // Binary search for the closest point — O(log N) instead of O(N).
        // Points are sorted by timestamp.
        var lo = 0;
        var hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (points[mid].Timestamp < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo is now the first point >= target. Compare lo and lo-1 to find nearest.
        var best = points[lo];
        if (lo > 0)
        {
            var prev = points[lo - 1];
            if ((target - prev.Timestamp).Duration() < (best.Timestamp - target).Duration())
            {
                best = prev;
            }
        }

        return best;
    }

    private void HideHover()
    {
        _hoverLine.Visibility = Visibility.Collapsed;
        _hoverTooltip.Visibility = Visibility.Collapsed;
    }

    private static string FormatHoverTime(DateTimeOffset timestamp, TimeSpan window)
    {
        // Always show full date + time in hover tooltips for maximum clarity
        return timestamp.LocalDateTime.ToString("MMM d, yyyy  h:mm:ss tt");
    }

    private void TryPinTooltip(double pointerX)
    {
        // Only one annotation at a time — clicking again replaces it
        if (_pinnedTooltips.Count > 0)
        {
            ClearPinnedTooltips();
            return;
        }

        var plotWidth = Math.Max(16d, ActualWidth - LeftPadding - RightPadding);
        var plotHeight = Math.Max(16d, ActualHeight - TopPadding - BottomPadding);
        if (!TryBuildHoverSnapshot(pointerX, plotWidth, plotHeight, out var hoverX, out var tooltip))
        {
            return;
        }

        // Create a pinned vertical marker line
        var markerLine = new Line
        {
            Stroke = ResolveBrush("AccentStrongBrush", "#B7F7FF"),
            StrokeThickness = 1,
            Opacity = 0.6,
            X1 = hoverX,
            X2 = hoverX,
            Y1 = TopPadding,
            Y2 = TopPadding + plotHeight,
            IsHitTestVisible = false,
        };

        // Create pinned tooltip — clickable to dismiss
        var pinnedText = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            MaxWidth = 200,
            Text = tooltip,
        };
        var pinnedBorder = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#222529"),
            BorderBrush = ResolveBrush("AccentStrongBrush", "#B7F7FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Child = pinnedText,
            IsHitTestVisible = true,
            Opacity = 0.95,
        };
        pinnedBorder.Tapped += (_, e) =>
        {
            ClearPinnedTooltips();
            e.Handled = true;
        };

        _interactionCanvas.Children.Add(markerLine);
        _interactionCanvas.Children.Add(pinnedBorder);

        // Position the tooltip to the right of the marker, flipping left if near the edge
        pinnedBorder.Measure(new Windows.Foundation.Size(220, double.PositiveInfinity));
        var desired = pinnedBorder.DesiredSize;
        var tooltipLeft = hoverX + 10;
        if (tooltipLeft + desired.Width + 8 > ActualWidth)
        {
            tooltipLeft = hoverX - desired.Width - 10;
        }

        Canvas.SetLeft(pinnedBorder, Math.Clamp(tooltipLeft, 8, Math.Max(8, ActualWidth - desired.Width - 8)));
        Canvas.SetTop(pinnedBorder, Math.Clamp(TopPadding + 8, TopPadding, Math.Max(TopPadding, ActualHeight - desired.Height - 8)));

        _pinnedTooltips.Add((pinnedBorder, markerLine));
    }

    /// <summary>Removes all pinned tooltips from the chart.</summary>
    public void ClearPinnedTooltips()
    {
        foreach (var (tooltip, marker) in _pinnedTooltips)
        {
            _interactionCanvas.Children.Remove(tooltip);
            _interactionCanvas.Children.Remove(marker);
        }

        _pinnedTooltips.Clear();
    }
}

public sealed class ChartZoomSelectionEventArgs : EventArgs
{
    public ChartZoomSelectionEventArgs(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }
}
