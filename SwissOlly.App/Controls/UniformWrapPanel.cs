using Windows.Foundation;

namespace SwissOlly.App.Controls;

/// <summary>
/// Lays children out in equal-width columns that fill the available width, wrapping to new rows.
/// As many columns as fit at <see cref="MinItemWidth"/> each, never more than <see cref="MaxColumns"/>.
/// </summary>
/// <remarks>
/// Two panels given the same width pick the same column count, so groups stacked one above the
/// other (dark themes, then light) line up card for card. Under an infinite width, as inside a
/// horizontal StackPanel, everything goes on one row at the minimum width instead of collapsing:
/// star-sized Grid columns resolve to zero there, which once shrank all of Settings to a sliver.
/// </remarks>
internal sealed class UniformWrapPanel : Panel
{
    public double MinItemWidth { get; set; } = 140;

    public int MaxColumns { get; set; } = int.MaxValue;

    public double ColumnSpacing { get; set; } = 12;

    public double RowSpacing { get; set; } = 12;

    private int ColumnsFor(double width)
    {
        var count = Math.Max(1, Children.Count);
        if (double.IsInfinity(width))
        {
            return Math.Min(count, MaxColumns);
        }

        // Half a pixel of slack, so measure and arrange agree when the width they see differs by
        // floating-point noise.
        var fit = (int)Math.Floor((width + ColumnSpacing + 0.5) / (MinItemWidth + ColumnSpacing));
        return Math.Clamp(fit, 1, MaxColumns);
    }

    private double ItemWidthFor(double width, int columns) =>
        double.IsInfinity(width)
            ? MinItemWidth
            : Math.Max(0, (width - (ColumnSpacing * (columns - 1))) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
        var itemWidth = ItemWidthFor(availableSize.Width, columns);
        var height = 0d;
        var rowHeight = 0d;
        var visibleIndex = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            visibleIndex++;
            if (visibleIndex % columns == 0)
            {
                height += rowHeight + RowSpacing;
                rowHeight = 0;
            }
        }

        height = visibleIndex % columns == 0 ? Math.Max(0, height - RowSpacing) : height + rowHeight;
        var usedColumns = Math.Min(columns, Math.Max(1, visibleIndex));
        var width = double.IsInfinity(availableSize.Width)
            ? (usedColumns * itemWidth) + (ColumnSpacing * (usedColumns - 1))
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width);
        var itemWidth = ItemWidthFor(finalSize.Width, columns);

        // Row heights first, so every item in a row gets that row's full height.
        var visible = Children.Where(child => child.Visibility != Visibility.Collapsed).ToList();
        var y = 0d;
        for (var rowStart = 0; rowStart < visible.Count; rowStart += columns)
        {
            var rowEnd = Math.Min(rowStart + columns, visible.Count);
            var rowHeight = 0d;
            for (var index = rowStart; index < rowEnd; index++)
            {
                rowHeight = Math.Max(rowHeight, visible[index].DesiredSize.Height);
            }

            for (var index = rowStart; index < rowEnd; index++)
            {
                var x = (index - rowStart) * (itemWidth + ColumnSpacing);
                visible[index].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }
}
