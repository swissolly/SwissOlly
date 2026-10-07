using Microsoft.UI.Dispatching;
using Vaktr.App.ViewModels;

namespace Vaktr.App.Controls;

public sealed class UsageGauge : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(UsageGauge),
            new PropertyMetadata(0d, OnGaugePropertyChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(UsageGauge),
            new PropertyMetadata(null, OnGaugePropertyChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(
            nameof(Caption),
            typeof(string),
            typeof(UsageGauge),
            new PropertyMetadata("Usage", OnGaugePropertyChanged));

    private readonly Canvas _canvas;
    private readonly Border _frameBorder;
    private readonly Border _innerBorder;
    private readonly TextBlock _valueText;
    private readonly TextBlock _captionText;
    private bool _redrawQueued;
    private bool _redrawUpgradePending;
    private DispatcherQueuePriority _queuedRedrawPriority = DispatcherQueuePriority.Low;

    // Dirty-check state — avoids rebuilding both arc Path/PathGeometry objects on every
    // scrape tick when the value hasn't meaningfully moved (mirrors TelemetryChart's approach).
    private double _lastRenderedValue = double.NaN;
    private Brush? _lastRenderedAccentBrush;
    private string? _lastRenderedCaption;
    private double _lastRenderedWidth = -1;
    private double _lastRenderedHeight = -1;
    private bool _themeDirty;

    // Retained arc visuals. The value arc is re-shaped up to 60x/second while animating, so it
    // mutates its existing ArcSegment rather than allocating a new Path/PathGeometry/PathFigure/
    // ArcSegment set per frame the way the original clear-and-rebuild did.
    private Path? _trackPath;
    private Path? _valuePath;
    private PathFigure? _valueFigure;
    private ArcSegment? _valueSegment;
    private double _centerX;
    private double _centerY;
    private double _radius;

    private const double StartAngle = 135d;
    private const double TotalSweep = 270d;

    private readonly AnimatedDouble _animatedValue;

    public UsageGauge()
    {
        _canvas = new Canvas();
        _frameBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            Background = CreateThemedGradient(),
        };
        _innerBorder = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderBrush = ResolveBrush("SurfaceGridBrush", "#35587A"),
            BorderThickness = new Thickness(0.8),
            Opacity = 0.18,
        };
        _valueText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
        };
        _captionText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
        };

        Content = new Grid
        {
            Children =
            {
                _frameBorder,
                _innerBorder,
                _canvas,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        _valueText,
                        _captionText,
                    },
                },
            },
        };

        _animatedValue = new AnimatedDouble(0d, ApplyValueVisual);

        Loaded += (_, _) => ScheduleRedraw();
        Unloaded += (_, _) => _animatedValue.Stop();
        SizeChanged += OnGaugeSizeChanged;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private static void OnGaugePropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (UsageGauge)dependencyObject;

        if (args.Property == ValueProperty)
        {
            // Ease toward the new reading instead of snapping. Retargeting mid-flight picks up
            // from wherever the arc currently sits, so back-to-back scrapes stay continuous.
            if (gauge._renderingSuspended || gauge._trackPath == null)
                gauge._animatedValue.SnapTo(Math.Clamp(gauge.Value, 0d, 100d));
            else
                gauge._animatedValue.AnimateTo(Math.Clamp(gauge.Value, 0d, 100d));
            return;
        }

        gauge.ScheduleRedraw();
    }

    public void RefreshThemeResources()
    {
        _frameBorder.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
        _frameBorder.Background = CreateThemedGradient();
        _innerBorder.BorderBrush = ResolveBrush("SurfaceGridBrush", "#35587A");
        _valueText.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _captionText.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
        _themeDirty = true; // theme-resource lookups (e.g. SurfaceStrokeBrush) live inside Redraw()
        Redraw();
    }

    private static LinearGradientBrush CreateThemedGradient()
    {
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = ThemeVisuals.ResolveColor("SurfaceElevatedBrush", "#222529"), Offset = 0d },
                new GradientStop { Color = ThemeVisuals.ResolveColor("SurfaceBrush", "#1C1F22"), Offset = 1d },
            },
        };
    }

    private bool _renderingSuspended;

    private void OnGaugeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderingSuspended) return;
        ScheduleRedraw();
    }

    public void SetRenderingSuspended(bool suspended)
    {
        _renderingSuspended = suspended;

        if (suspended)
        {
            // Don't leave an off-screen or frozen gauge holding the render tick open.
            _animatedValue.SnapTo(Math.Clamp(Value, 0d, 100d));
            return;
        }

        _animatedValue.SnapTo(Math.Clamp(Value, 0d, 100d));
        ScheduleRedraw();
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

    private void Redraw()
    {
        var width = Math.Max(120, ActualWidth);
        var height = Math.Max(120, ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var accentBrush = AccentBrush;
        var caption = Caption;

        var geometryDirty = _themeDirty
            || _trackPath is null
            || Math.Abs(width - _lastRenderedWidth) >= 0.5
            || Math.Abs(height - _lastRenderedHeight) >= 0.5;
        var brushDirty = _themeDirty || !ReferenceEquals(accentBrush, _lastRenderedAccentBrush);
        var captionDirty = !string.Equals(caption, _lastRenderedCaption, StringComparison.Ordinal);

        if (!geometryDirty
            && !brushDirty
            && !captionDirty
            && Math.Abs(_animatedValue.Current - _lastRenderedValue) < 0.05)
        {
            return;
        }

        if (geometryDirty)
        {
            BuildArcs(width, height);
        }

        if (brushDirty && _valuePath is not null)
        {
            _valuePath.Stroke = accentBrush ?? ResolveBrush("AccentBrush", "#66E7FF");
        }

        if (captionDirty)
        {
            _captionText.Text = caption;
        }

        ApplyValueVisual();

        _lastRenderedAccentBrush = accentBrush;
        _lastRenderedCaption = caption;
        _lastRenderedWidth = width;
        _lastRenderedHeight = height;
        _themeDirty = false;
    }

    // Creates the two arcs once per size/theme change. The track arc is fully static afterwards;
    // the value arc keeps references to the figure and segment so ApplyValueVisual can re-shape
    // it without touching the visual tree.
    private void BuildArcs(double width, double height)
    {
        _centerX = width / 2d;
        _centerY = height / 2d;
        _radius = Math.Max(18, Math.Min(width, height) / 2d - 12);

        _canvas.Children.Clear();

        _trackPath = CreateArcPath(
            _centerX, _centerY, _radius, StartAngle, TotalSweep,
            ResolveBrush("SurfaceStrokeBrush", "#27425E"), 11, 0.32,
            out _, out _);

        _valuePath = CreateArcPath(
            _centerX, _centerY, _radius, StartAngle, 0d,
            AccentBrush ?? ResolveBrush("AccentBrush", "#66E7FF"), 11, 1,
            out _valueFigure, out _valueSegment);

        _canvas.Children.Add(_trackPath);
        _canvas.Children.Add(_valuePath);
    }

    // Hot path: runs once per rendered frame while a gauge is animating. Allocation-free.
    private void ApplyValueVisual()
    {
        if (_valuePath is null || _valueSegment is null || _valueFigure is null)
        {
            return;
        }

        var value = Math.Clamp(_animatedValue.Current, 0d, 100d);
        var sweep = value / 100d * TotalSweep;

        // A zero-length arc with round caps still paints a dot; collapse it so an idle gauge
        // reads as genuinely empty rather than showing a stray pip at the 135° origin.
        if (sweep < 0.5d)
        {
            _valuePath.Visibility = Visibility.Collapsed;
        }
        else
        {
            _valuePath.Visibility = Visibility.Visible;
            _valueSegment.Point = ToPoint(_centerX, _centerY, _radius, StartAngle + sweep);
            _valueSegment.IsLargeArc = sweep > 180d;
        }

        // Integer percent: the previous "0.#" format alternated between "45%" and "45.3%" as the
        // fraction crossed zero, which visibly jittered a centred readout — and would now do so
        // every frame of the animation.
        _valueText.Text = $"{value:0}%";
        _lastRenderedValue = value;
    }

    private static Path CreateArcPath(
        double centerX,
        double centerY,
        double radius,
        double startAngle,
        double sweepAngle,
        Brush stroke,
        double thickness,
        double opacity,
        out PathFigure figure,
        out ArcSegment segment)
    {
        var start = ToPoint(centerX, centerY, radius, startAngle);
        var end = ToPoint(centerX, centerY, radius, startAngle + sweepAngle);
        segment = new ArcSegment
        {
            Point = end,
            Size = new Windows.Foundation.Size(radius, radius),
            IsLargeArc = sweepAngle > 180,
            SweepDirection = SweepDirection.Clockwise,
        };
        figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
            Segments = new PathSegmentCollection { segment },
        };

        return new Path
        {
            Data = new PathGeometry
            {
                Figures = new PathFigureCollection { figure },
            },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = opacity,
        };
    }

    private static Windows.Foundation.Point ToPoint(double centerX, double centerY, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Windows.Foundation.Point(
            centerX + Math.Cos(radians) * radius,
            centerY + Math.Sin(radians) * radius);
    }

    private static Brush ResolveBrush(string key, string fallbackHex)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return BrushFactory.CreateBrush(fallbackHex);
    }
}
