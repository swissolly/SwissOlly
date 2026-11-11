using Windows.Foundation;

namespace SwissOlly.App.Controls;

/// <summary>
/// Lays children out left to right at their own widths and starts a new row when the next one
/// does not fit. For chip rows and swatches, so a row that is too wide for a narrow card wraps
/// instead of being clipped.
/// </summary>
internal sealed class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;

    public double RowSpacing { get; set; } = 8;

    /// <summary>
    /// Slack in the wrap test. Arranged at exactly its measured width, a row could sum a hair
    /// over it in floating point and wrap its last chip onto a line nobody measured.
    /// </summary>
    private const double WrapTolerance = 0.5;

    protected override Size MeasureOverride(Size availableSize)
    {
        var x = 0d;
        var width = 0d;
        var height = 0d;
        var rowHeight = 0d;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width + WrapTolerance)
            {
                height += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width + Spacing;
            width = Math.Max(width, x - Spacing);
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(width, height + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Each row is arranged once its height is known, so shorter items centre in their row.
        var row = new List<UIElement>();
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        void ArrangeRow()
        {
            var left = 0d;
            foreach (var item in row)
            {
                var size = item.DesiredSize;
                item.Arrange(new Rect(left, y + ((rowHeight - size.Height) / 2), size.Width, size.Height));
                left += size.Width + Spacing;
            }

            row.Clear();
        }

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width + WrapTolerance)
            {
                ArrangeRow();
                y += rowHeight + RowSpacing;
                x = 0;
                rowHeight = 0;
            }

            row.Add(child);
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        ArrangeRow();
        return finalSize;
    }
}
