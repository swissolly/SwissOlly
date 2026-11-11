namespace SwissOlly.App.Controls;

/// <summary>
/// Stroke patterns that tell series apart without relying on colour: solid, dashed, dotted.
/// Only used for 2-4 series; past that (per-core CPU) patterns are noise and every line is solid.
/// Dash lengths are in multiples of stroke thickness, so chart and legend read the same.
/// </summary>
internal static class SeriesDash
{
    public static bool AppliesTo(int seriesCount) => seriesCount is >= 2 and <= 4;

    /// <summary>A fresh collection each call: a DoubleCollection cannot be shared between shapes.</summary>
    public static DoubleCollection? For(int index, int seriesCount)
    {
        if (!AppliesTo(seriesCount))
        {
            return null;
        }

        return (index % 3) switch
        {
            1 => new DoubleCollection { 3, 2 },
            // A near-zero dash with round caps draws as dots.
            2 => new DoubleCollection { 0.01, 2.2 },
            _ => null,
        };
    }

    public static void Apply(Shape shape, int index, int seriesCount)
    {
        var dash = For(index, seriesCount);
        if (dash is null)
        {
            shape.ClearValue(Shape.StrokeDashArrayProperty);
            return;
        }

        shape.StrokeDashArray = dash;
        shape.StrokeDashCap = PenLineCap.Round;
    }
}
