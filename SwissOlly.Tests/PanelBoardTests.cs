using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class PanelBoardTests
{
    private static readonly Func<string, PanelSizePreset> Standard = _ => PanelSizePreset.Standard;

    private static Dictionary<string, PanelLayout> Board(params (string Key, int X, int Y, int W, int H)[] panels)
        => panels.ToDictionary(p => p.Key, p => new PanelLayout(p.X, p.Y, p.W, p.H), StringComparer.OrdinalIgnoreCase);

    private static void AssertSameGeometry(IReadOnlyDictionary<string, PanelLayout> expected, IReadOnlyDictionary<string, PanelLayout> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
        foreach (var (key, layout) in expected)
        {
            var other = actual[key];
            Assert.True(
                (layout.X, layout.Y, layout.W, layout.H) == (other.X, other.Y, other.W, other.H),
                $"{key}: expected ({layout.X},{layout.Y},{layout.W},{layout.H}), got ({other.X},{other.Y},{other.W},{other.H})");
        }
    }

    [Fact]
    public void Startup_Waves_Keep_A_Custom_Layout_And_An_Early_Save_Writes_All_Of_It()
    {
        // A layout nobody would get from the free-slot packer.
        var saved = Board(
            ("gpu-temperature", 8, 18, 4, 6),
            ("cpu-total", 0, 0, 8, 8),
            ("cpu-cores", 8, 0, 4, 12),
            ("memory", 0, 8, 8, 5),
            ("disk-c", 0, 13, 6, 5));
        var active = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);
        var parked = saved.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);

        // First wave: only the baseline panel exists.
        Assert.True(PanelBoard.Reconcile(active, parked, ["gpu-temperature"], Standard));
        Assert.Equal(new[] { "gpu-temperature" }, active.Keys);

        // A save now (tray launch at sign-in) must still write the whole layout.
        AssertSameGeometry(saved, PanelBoard.ForSaving(active, parked));

        // History arrives: everything returns to its saved cell.
        Assert.True(PanelBoard.Reconcile(active, parked, saved.Keys.ToList(), Standard));
        AssertSameGeometry(saved, active);
        Assert.Empty(parked);
    }

    [Fact]
    public void Panel_Off_The_Board_Is_Parked_And_Returns_To_Its_Cell()
    {
        var active = Board(("a", 0, 0, 4, 6), ("b", 4, 0, 4, 6), ("c", 8, 0, 4, 6));
        var parked = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);

        Assert.False(PanelBoard.Reconcile(active, parked, ["a", "c"], Standard));
        Assert.Equal(new[] { "a", "c" }, active.Keys.OrderBy(k => k));
        Assert.Equal(4, parked["b"].X);
        Assert.Equal(3, PanelBoard.ForSaving(active, parked).Count);

        Assert.True(PanelBoard.Reconcile(active, parked, ["a", "b", "c"], Standard));
        AssertSameGeometry(Board(("a", 0, 0, 4, 6), ("b", 4, 0, 4, 6), ("c", 8, 0, 4, 6)), active);
    }

    [Fact]
    public void New_Panel_Takes_The_First_Free_Slot()
    {
        var active = Board(("a", 0, 0, 4, 6));
        var parked = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);

        Assert.False(PanelBoard.Reconcile(active, parked, ["a", "new"], Standard));

        var placed = active["new"];
        Assert.Equal((4, 0, 4, 6), (placed.X, placed.Y, placed.W, placed.H));
    }

    [Fact]
    public void Nothing_Saved_Builds_The_Legacy_Board()
    {
        var active = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);
        var parked = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);

        PanelBoard.Reconcile(active, parked, ["a", "b"], Standard);

        var legacy = GridLayoutEngine.FromLegacy(["a", "b"], Standard).ToDictionary(p => p.Key, p => p.Layout, StringComparer.OrdinalIgnoreCase);
        AssertSameGeometry(legacy, active);
    }

    [Fact]
    public void Saved_Order_Keeps_Panels_Not_Seen_Yet_At_Their_Rank()
    {
        var savedRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpu-temperature"] = 0,
            ["cpu-total"] = 1,
            ["memory"] = 2,
            ["disk-c"] = 3,
        };
        var known = new HashSet<string>(["gpu-temperature", "memory", "new-panel"], StringComparer.OrdinalIgnoreCase);

        var order = PanelBoard.MergeOrder(["gpu-temperature", "memory", "new-panel"], savedRanks, known.Contains);

        Assert.Equal(new[] { "gpu-temperature", "cpu-total", "memory", "disk-c", "new-panel" }, order);
    }
}
