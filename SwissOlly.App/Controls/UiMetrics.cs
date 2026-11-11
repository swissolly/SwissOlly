namespace SwissOlly.App.Controls;

/// <summary>
/// Shared shape and size scale. Use these instead of literals so surfaces stay consistent.
/// Pills and circles are the exception: their radius is half their height.
/// </summary>
internal static class UiMetrics
{
    /// <summary>Swatches, small tiles and badges.</summary>
    public const double SmallRadius = 4;

    /// <summary>Chips, fields, cards and panels.</summary>
    public const double ControlRadius = 8;

    /// <summary>Flyouts, popovers and other floating surfaces.</summary>
    public const double OverlayRadius = 12;

    /// <summary>Every stroke. Sub-pixel strokes render soft at 100% scale, so there are none.</summary>
    public const double Hairline = 1;

    /// <summary>Smallest pointer target for anything clickable.</summary>
    public const double MinHitTarget = 32;

    /// <summary>Radius for a container that wraps a rounded child with <paramref name="inset"/> padding,
    /// so both curves stay concentric.</summary>
    public static double Enclosing(double childRadius, double inset) => childRadius + inset;
}
