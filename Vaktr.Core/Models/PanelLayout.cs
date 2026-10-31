namespace Vaktr.Core.Models;

/// <summary>
/// A panel's position and size on the dashboard grid, in grid units.
///
/// Replaces the old <see cref="PanelSizePreset"/> + order pairing, which could only express a
/// column span and a flow position — there was no way to say "pinned at row 3, column 5".
/// </summary>
public sealed class PanelLayout
{
    /// <summary>Columns in the dashboard grid. 12 divides by 2/3/4/6, so a third is 4 units, a half 6, full 12.</summary>
    public const int Columns = 12;

    public const int MinWidth = 2;
    /// <summary>
    /// Minimum height in row units. At a 48px row unit this reserves 336px, which is the
    /// smallest a card can actually render without its chart and header overflowing. A smaller
    /// floor let the grid hand a panel fewer rows than its own content minimum, so the card
    /// spilled over the panel beneath it.
    /// </summary>
    public const int MinHeight = 7;
    public const int MaxHeight = 24;

    /// <summary>Leftmost column, 0-based.</summary>
    public int X { get; set; }

    /// <summary>Top row, 0-based. Unbounded — the board grows downward.</summary>
    public int Y { get; set; }

    /// <summary>Width in columns.</summary>
    public int W { get; set; } = 4;

    /// <summary>Height in row units.</summary>
    public int H { get; set; } = 6;

    public PanelLayout() { }

    public PanelLayout(int x, int y, int w, int h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public int Right => X + W;

    public int Bottom => Y + H;

    public bool Overlaps(PanelLayout other)
        => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public PanelLayout Clone() => new(X, Y, W, H);

    /// <summary>
    /// Clamps into a valid board position. Width is clamped before X so a too-wide panel shrinks
    /// rather than being pushed off the left edge.
    /// </summary>
    public PanelLayout Normalize()
    {
        W = Math.Clamp(W, MinWidth, Columns);
        H = Math.Clamp(H, MinHeight, MaxHeight);
        X = Math.Clamp(X, 0, Columns - W);
        Y = Math.Max(0, Y);
        return this;
    }

    /// <summary>
    /// Converts a legacy preset into a width. Wide/Large spanned two of three columns, which is
    /// two thirds of the board — 8 of 12. Compact and Standard were a single third.
    /// </summary>
    public static int WidthFromLegacyPreset(PanelSizePreset preset) => preset switch
    {
        PanelSizePreset.Wide or PanelSizePreset.Large => 8,
        _ => 4,
    };

    public static int HeightFromLegacyPreset(PanelSizePreset preset) => preset switch
    {
        PanelSizePreset.Large => 8,
        PanelSizePreset.Compact => 5,
        _ => 6,
    };
}
