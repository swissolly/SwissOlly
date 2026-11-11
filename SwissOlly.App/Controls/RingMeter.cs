namespace SwissOlly.App.Controls;

/// <summary>
/// A small ring that fills clockwise with a 0-100 value: the summary strip's meter. Three quarters
/// of a circle, open at the bottom, like the drive panels' <see cref="UsageGauge"/> without its
/// frame and readout. Both arcs are built once; a new value moves the end of the value arc.
/// </summary>
/// <remarks>
/// Brushes are passed in and never made here: the track is a palette resource and the value is
/// the card's accent or a threshold resource, all recoloured in place by a theme change.
/// </remarks>
internal sealed class RingMeter
{
    private const double StartAngle = 135d;
    private const double TotalSweep = 270d;

    private readonly Path _value;
    private readonly ArcSegment _valueSegment;
    private readonly double _center;
    private readonly double _radius;
    private readonly AnimatedDouble _animated;

    public RingMeter(double diameter, double thickness, Brush trackBrush, Brush valueBrush, double initialValue)
    {
        _center = diameter / 2d;
        _radius = (diameter - thickness) / 2d;

        var track = CreateArc(TotalSweep, trackBrush, thickness, out _);
        // Fainter than the value, but there: at the line meter's 0.38 the track all but vanished on
        // dark themes, and a low reading looked like a stray tick instead of a nearly empty gauge.
        track.Opacity = 0.8;
        _value = CreateArc(0d, valueBrush, thickness, out _valueSegment);

        Element = new Canvas
        {
            Width = diameter,
            Height = diameter,
            IsHitTestVisible = false,
            Children = { track, _value },
        };

        _animated = new AnimatedDouble(Math.Clamp(initialValue, 0d, 100d), Apply);
        Apply();
        // Don't leave a ring that has left the screen holding the render tick open.
        Element.Unloaded += (_, _) => _animated.SnapTo(_animated.Current);
    }

    public Canvas Element { get; }

    public Brush ValueBrush
    {
        set
        {
            if (!ReferenceEquals(_value.Stroke, value))
            {
                _value.Stroke = value;
            }
        }
    }

    /// <summary>Eases the ring to <paramref name="value"/> (0-100); at once when animations are off.</summary>
    public void AnimateTo(double value) => _animated.AnimateTo(Math.Clamp(value, 0d, 100d));

    private void Apply()
    {
        var sweep = _animated.Current / 100d * TotalSweep;
        // A zero-length arc with round caps still paints a dot; an idle meter should be empty.
        if (sweep < 0.5d)
        {
            _value.Visibility = Visibility.Collapsed;
            return;
        }

        _value.Visibility = Visibility.Visible;
        _valueSegment.Point = PointAt(StartAngle + sweep);
        _valueSegment.IsLargeArc = sweep > 180d;
    }

    private Path CreateArc(double sweep, Brush stroke, double thickness, out ArcSegment segment)
    {
        segment = new ArcSegment
        {
            Point = PointAt(StartAngle + sweep),
            Size = new Windows.Foundation.Size(_radius, _radius),
            IsLargeArc = sweep > 180d,
            SweepDirection = SweepDirection.Clockwise,
        };
        var figure = new PathFigure
        {
            StartPoint = PointAt(StartAngle),
            IsClosed = false,
            IsFilled = false,
            Segments = new PathSegmentCollection { segment },
        };

        return new Path
        {
            Data = new PathGeometry { Figures = new PathFigureCollection { figure } },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private Windows.Foundation.Point PointAt(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Windows.Foundation.Point(
            _center + (Math.Cos(radians) * _radius),
            _center + (Math.Sin(radians) * _radius));
    }
}
