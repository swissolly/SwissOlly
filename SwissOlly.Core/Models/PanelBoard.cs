namespace SwissOlly.Core.Models;

/// <summary>
/// Keeps saved geometry and order for panels that are not on the board right now.
///
/// Panels reach the board in waves at startup: the baseline GPU temperature panel first, then
/// whatever the history read finds, then anything only live samples report. Geometry for panels
/// that had not arrived yet used to be dropped on the first wave, so every launch repacked a
/// custom layout, and a save in that window (a sign-in launch hides to the tray and saves at
/// once) wrote a one-panel board over the whole layout. A hidden panel, or one whose hardware is
/// missing for a run, likewise comes back where it was.
/// </summary>
public static class PanelBoard
{
    /// <summary>
    /// Gives every panel on the board geometry and parks the rest. Saved geometry comes first; a
    /// panel never seen before takes the first free slot, or the legacy preset layout when nothing
    /// is saved at all. Returns true when parked geometry came back, so the caller can resolve
    /// overlaps with panels placed while it was away.
    /// </summary>
    public static bool Reconcile(
        Dictionary<string, PanelLayout> active,
        Dictionary<string, PanelLayout> parked,
        IReadOnlyList<string> boardKeys,
        Func<string, PanelSizePreset> presetLookup)
    {
        var restored = false;
        var unplaced = new List<string>();
        foreach (var key in boardKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (active.ContainsKey(key))
            {
                continue;
            }

            if (parked.Remove(key, out var saved))
            {
                active[key] = saved;
                restored = true;
            }
            else
            {
                unplaced.Add(key);
            }
        }

        if (unplaced.Count > 0)
        {
            if (active.Count == 0 && parked.Count == 0)
            {
                // Nothing saved: rebuild the board from the legacy preset + order model so an
                // existing dashboard upgrades to something recognisable.
                foreach (var placement in GridLayoutEngine.FromLegacy(unplaced, presetLookup))
                {
                    active[placement.Key] = placement.Layout;
                }
            }
            else
            {
                // Panels added after a layout was saved take the first free slot, so they sit
                // beside existing panels instead of stacking in a column at the bottom.
                foreach (var key in unplaced)
                {
                    var occupied = active.Select(pair => new PanelPlacement(pair.Key, pair.Value)).ToList();
                    active[key] = GridLayoutEngine.FindFreeSlot(occupied, 4, 6);
                }
            }
        }

        // Off-board panels keep their geometry for when they return, but do not reserve rows.
        var onBoard = new HashSet<string>(boardKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in active.Keys.Where(key => !onBoard.Contains(key)).ToList())
        {
            parked[key] = active[key];
            active.Remove(key);
        }

        return restored;
    }

    /// <summary>The geometry to save: the board's, over whatever is parked.</summary>
    public static Dictionary<string, PanelLayout> ForSaving(
        IReadOnlyDictionary<string, PanelLayout> active,
        IReadOnlyDictionary<string, PanelLayout> parked)
    {
        var all = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in parked)
        {
            all[key] = layout.Clone();
        }

        foreach (var (key, layout) in active)
        {
            all[key] = layout.Clone();
        }

        return all;
    }

    /// <summary>
    /// The panel order to save. <paramref name="knownInOrder"/> must already be sorted by saved
    /// rank; saved keys for panels not seen this run keep their rank among them.
    /// </summary>
    public static List<string> MergeOrder(
        IReadOnlyList<string> knownInOrder,
        IReadOnlyDictionary<string, int> savedRanks,
        Func<string, bool> isKnown)
    {
        return knownInOrder
            .Select((key, index) => (Key: key, Rank: savedRanks.TryGetValue(key, out var rank) ? rank : int.MaxValue, Index: index))
            .Concat(savedRanks
                .Where(pair => !isKnown(pair.Key))
                .Select(pair => (pair.Key, Rank: pair.Value, Index: -1)))
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Key)
            .ToList();
    }
}
