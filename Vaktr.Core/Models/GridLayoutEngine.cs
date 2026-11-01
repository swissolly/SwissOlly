namespace Vaktr.Core.Models;

/// <summary>One panel's geometry on the board, keyed so results can be mapped back to cards.</summary>
public sealed record PanelPlacement(string Key, PanelLayout Layout);

/// <summary>
/// Collision resolution for the dashboard grid.
///
/// Follows the rule Grafana uses: a panel that overlaps another displaces it <em>downward</em>,
/// never sideways, and the board then compacts upward to close gaps. Resolving on one axis is
/// what makes repeated drags predictable — sideways displacement produces layouts that shuffle
/// differently depending on the order panels happen to be visited.
///
/// Deliberately lives in Core with no XAML dependency so it can be unit tested directly.
/// </summary>
public static class GridLayoutEngine
{
    /// <summary>
    /// Produces a collision-free board. Input order breaks ties, so callers should pass panels in
    /// a stable order (the panel list order) for deterministic results.
    /// </summary>
    public static List<PanelPlacement> Resolve(IEnumerable<PanelPlacement> panels)
    {
        // Work on clones so callers' geometry is never mutated underneath them.
        var working = panels
            .Select(p => new PanelPlacement(p.Key, p.Layout.Clone().Normalize()))
            .OrderBy(p => p.Layout.Y)
            .ThenBy(p => p.Layout.X)
            .ToList();

        var placed = new List<PanelPlacement>(working.Count);
        foreach (var panel in working)
        {
            // Push down until this panel clears everything already placed. Each iteration moves
            // to the bottom edge of the blocker rather than stepping one row, so a tall blocker
            // resolves in a single step instead of H iterations.
            while (true)
            {
                var blocker = placed.FirstOrDefault(p => p.Layout.Overlaps(panel.Layout));
                if (blocker is null)
                {
                    break;
                }

                panel.Layout.Y = blocker.Layout.Bottom;
            }

            placed.Add(panel);
        }

        Compact(placed);
        return placed;
    }

    /// <summary>
    /// Creates a collision-free display layout for a responsive breakpoint without changing
    /// persisted geometry. Narrow windows widen cards to the breakpoint minimum, which can make
    /// previously side-by-side panels overlap; resolve those collisions before assigning them to
    /// the visual grid.
    /// </summary>
    public static List<PanelPlacement> ResolveResponsive(IEnumerable<PanelPlacement> panels, int minimumWidth)
    {
        minimumWidth = Math.Clamp(minimumWidth, PanelLayout.MinWidth, PanelLayout.Columns);
        var responsive = panels
            .Select(panel =>
            {
                var layout = panel.Layout.Clone();
                layout.W = Math.Max(layout.W, minimumWidth);
                return new PanelPlacement(panel.Key, layout.Normalize());
            })
            .OrderBy(panel => panel.Layout.Y)
            .ThenBy(panel => panel.Layout.X)
            .ToList();

        // Preserve deliberate vertical gaps in the saved layout. Only push a panel down when
        // widening at this breakpoint makes it collide with a panel already placed.
        var placed = new List<PanelPlacement>(responsive.Count);
        foreach (var panel in responsive)
        {
            while (placed.FirstOrDefault(other => other.Layout.Overlaps(panel.Layout)) is { } blocker)
            {
                panel.Layout.Y = blocker.Layout.Bottom;
            }

            placed.Add(panel);
        }

        return placed;
    }

    /// <summary>
    /// Pulls every panel as far up as it will go, preserving relative order. Without this, moving
    /// a panel out of the top row would leave a permanent hole.
    /// </summary>
    private static void Compact(List<PanelPlacement> placed, PanelPlacement? pinned = null)
    {
        foreach (var panel in placed
            .Where(panel => !ReferenceEquals(panel, pinned))
            .OrderBy(panel => panel.Layout.Y)
            .ThenBy(panel => panel.Layout.X)
            .ToList())
        {
            while (panel.Layout.Y > 0)
            {
                var candidate = panel.Layout.Clone();
                candidate.Y--;

                var blocked = placed.Any(other =>
                    !ReferenceEquals(other, panel) && other.Layout.Overlaps(candidate));

                if (blocked)
                {
                    break;
                }

                panel.Layout.Y--;
            }
        }
    }

    /// <summary>
    /// Sets one panel's full geometry and resolves the board around it.
    ///
    /// Edge resizes change position and size together — dragging a left edge moves X while
    /// shrinking W — so a single primitive is needed. <see cref="Move"/> and <see cref="Resize"/>
    /// are now thin wrappers over this.
    /// </summary>
    public static List<PanelPlacement> Apply(IEnumerable<PanelPlacement> panels, string key, PanelLayout target)
    {
        var list = panels.Select(p => new PanelPlacement(p.Key, p.Layout.Clone())).ToList();
        var subject = list.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (subject is null)
        {
            return Resolve(list);
        }

        subject.Layout.X = target.X;
        subject.Layout.Y = target.Y;
        subject.Layout.W = target.W;
        subject.Layout.H = target.H;
        subject.Layout.Normalize();

        return ResolveWithPriority(list, subject);
    }

    /// <summary>Moves one panel to a target cell, then resolves the rest of the board around it.</summary>
    public static List<PanelPlacement> Move(IEnumerable<PanelPlacement> panels, string key, int targetX, int targetY)
    {
        var list = panels.Select(p => new PanelPlacement(p.Key, p.Layout.Clone())).ToList();
        var moving = list.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (moving is null)
        {
            return Resolve(list);
        }

        moving.Layout.X = targetX;
        moving.Layout.Y = Math.Max(0, targetY);
        moving.Layout.Normalize();

        // The dragged panel wins its cell: ordering it first means everything else resolves
        // around it rather than shoving it back where it came from.
        return ResolveWithPriority(list, moving);
    }

    /// <summary>
    /// Applies snapped edge deltas to one panel while keeping the opposite edge anchored.
    /// Responsive minimum width is supplied by the display breakpoint; persisted geometry stays
    /// at its own width until the caller commits the returned target.
    /// </summary>
    public static PanelLayout ResizeByDelta(
        PanelLayout origin,
        bool left,
        bool right,
        bool top,
        bool bottom,
        int deltaColumns,
        int deltaRows,
        int minimumWidth = PanelLayout.MinWidth)
    {
        origin = origin.Clone().Normalize();
        minimumWidth = Math.Clamp(minimumWidth, PanelLayout.MinWidth, PanelLayout.Columns);

        var x = origin.X;
        var y = origin.Y;
        var w = origin.W;
        var h = origin.H;

        if (left)
        {
            x = Math.Clamp(origin.X + deltaColumns, 0, origin.Right - minimumWidth);
            w = origin.Right - x;
        }
        else if (right)
        {
            w = Math.Clamp(origin.W + deltaColumns, minimumWidth, PanelLayout.Columns - origin.X);
        }

        if (top)
        {
            var minY = Math.Max(0, origin.Bottom - PanelLayout.MaxHeight);
            var maxY = origin.Bottom - PanelLayout.MinHeight;
            y = Math.Clamp(origin.Y + deltaRows, minY, maxY);
            h = origin.Bottom - y;
        }
        else if (bottom)
        {
            h = Math.Clamp(origin.H + deltaRows, PanelLayout.MinHeight, PanelLayout.MaxHeight);
        }

        return new PanelLayout(x, y, w, h).Normalize();
    }

    /// <summary>Resizes one panel, then resolves the rest of the board around it.</summary>
    public static List<PanelPlacement> Resize(IEnumerable<PanelPlacement> panels, string key, int newW, int newH)
    {
        var list = panels.Select(p => new PanelPlacement(p.Key, p.Layout.Clone())).ToList();
        var target = list.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Resolve(list);
        }

        target.Layout.W = newW;
        target.Layout.H = newH;
        target.Layout.Normalize();

        return ResolveWithPriority(list, target);
    }

    /// <summary>
    /// Resolve, but with one panel pinned ahead of the rest so it keeps the cell the user put it
    /// in. Everything else is pushed down around it and then compacted.
    /// </summary>
    private static List<PanelPlacement> ResolveWithPriority(List<PanelPlacement> list, PanelPlacement priority)
    {
        var ordered = new List<PanelPlacement> { priority };
        ordered.AddRange(list
            .Where(p => !ReferenceEquals(p, priority))
            .OrderBy(p => p.Layout.Y)
            .ThenBy(p => p.Layout.X));

        var placed = new List<PanelPlacement>(ordered.Count);
        foreach (var panel in ordered)
        {
            while (true)
            {
                var blocker = placed.FirstOrDefault(p => p.Layout.Overlaps(panel.Layout));
                if (blocker is null)
                {
                    break;
                }

                panel.Layout.Y = blocker.Layout.Bottom;
            }

            placed.Add(panel);
        }

        // The target remains fixed; only surrounding panels close gaps around it.
        Compact(placed, priority);
        return placed;
    }

    /// <summary>
    /// First free cell that fits a panel of the given size, scanning left-to-right then downward.
    /// Used when a panel appears on a board that already has saved geometry — stacking every new
    /// panel at column 0 leaves obvious holes beside them.
    /// </summary>
    public static PanelLayout FindFreeSlot(IEnumerable<PanelPlacement> occupied, int w, int h)
    {
        var taken = occupied.Select(p => p.Layout).ToList();
        w = Math.Clamp(w, PanelLayout.MinWidth, PanelLayout.Columns);
        h = Math.Clamp(h, PanelLayout.MinHeight, PanelLayout.MaxHeight);

        var maxRow = taken.Count == 0 ? 0 : taken.Max(l => l.Bottom);
        for (var y = 0; y <= maxRow; y++)
        {
            for (var x = 0; x + w <= PanelLayout.Columns; x++)
            {
                var candidate = new PanelLayout(x, y, w, h);
                if (!taken.Any(t => t.Overlaps(candidate)))
                {
                    return candidate;
                }
            }
        }

        return new PanelLayout(0, maxRow, w, h);
    }

    /// <summary>
    /// Builds a starting board from the legacy preset + order model so existing users keep a
    /// recognisable dashboard the first time they run a build with saved geometry.
    /// </summary>
    public static List<PanelPlacement> FromLegacy(
        IEnumerable<string> orderedKeys,
        Func<string, PanelSizePreset> presetLookup)
    {
        var result = new List<PanelPlacement>();
        var x = 0;
        var y = 0;
        var rowHeight = 0;

        foreach (var key in orderedKeys)
        {
            var preset = presetLookup(key);
            var w = PanelLayout.WidthFromLegacyPreset(preset);
            var h = PanelLayout.HeightFromLegacyPreset(preset);

            if (x + w > PanelLayout.Columns)
            {
                y += rowHeight == 0 ? h : rowHeight;
                x = 0;
                rowHeight = 0;
            }

            result.Add(new PanelPlacement(key, new PanelLayout(x, y, w, h)));
            x += w;
            rowHeight = Math.Max(rowHeight, h);
        }

        return Resolve(result);
    }
}
