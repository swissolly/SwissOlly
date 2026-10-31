using Vaktr.Core.Models;

namespace Vaktr.Tests;

public class GridLayoutEngineTests
{
    private static PanelPlacement P(string key, int x, int y, int w, int h)
        => new(key, new PanelLayout(x, y, w, h));

    private static PanelLayout Find(IEnumerable<PanelPlacement> board, string key)
        => board.Single(p => p.Key == key).Layout;

    private static void AssertNoOverlaps(IReadOnlyList<PanelPlacement> board)
    {
        for (var i = 0; i < board.Count; i++)
        {
            for (var j = i + 1; j < board.Count; j++)
            {
                Assert.False(
                    board[i].Layout.Overlaps(board[j].Layout),
                    $"{board[i].Key} overlaps {board[j].Key}");
            }
        }
    }

    [Fact]
    public void Resolve_Separates_Overlapping_Panels()
    {
        var board = GridLayoutEngine.Resolve([
            P("a", 0, 0, 6, 6),
            P("b", 0, 0, 6, 6),
        ]);

        AssertNoOverlaps(board);
    }

    [Fact]
    public void Resolve_Pushes_Down_Not_Sideways()
    {
        var board = GridLayoutEngine.Resolve([
            P("a", 0, 0, 6, 6),
            P("b", 0, 0, 6, 6),
        ]);

        // Both keep their column; the displaced one moves down.
        Assert.Equal(0, Find(board, "a").X);
        Assert.Equal(0, Find(board, "b").X);
        Assert.NotEqual(Find(board, "a").Y, Find(board, "b").Y);
    }

    [Fact]
    public void Resolve_Keeps_Side_By_Side_Panels_On_One_Row()
    {
        var board = GridLayoutEngine.Resolve([
            P("a", 0, 0, 4, 6),
            P("b", 4, 0, 4, 6),
            P("c", 8, 0, 4, 6),
        ]);

        AssertNoOverlaps(board);
        Assert.Equal(0, Find(board, "a").Y);
        Assert.Equal(0, Find(board, "b").Y);
        Assert.Equal(0, Find(board, "c").Y);
    }

    [Fact]
    public void Resolve_Compacts_Gaps_Upward()
    {
        var board = GridLayoutEngine.Resolve([P("a", 0, 9, 4, 6)]);

        Assert.Equal(0, Find(board, "a").Y);
    }

    [Fact]
    public void Resolve_Is_Deterministic_And_Stable()
    {
        PanelPlacement[] input = [
            P("a", 0, 0, 4, 6),
            P("b", 4, 0, 8, 6),
            P("c", 0, 6, 12, 4),
        ];

        var first = GridLayoutEngine.Resolve(input);
        var second = GridLayoutEngine.Resolve(first);

        foreach (var panel in first)
        {
            var again = Find(second, panel.Key);
            Assert.Equal(panel.Layout.X, again.X);
            Assert.Equal(panel.Layout.Y, again.Y);
        }
    }

    [Fact]
    public void Move_Gives_The_Dragged_Panel_Its_Target_Cell()
    {
        PanelPlacement[] input = [
            P("a", 0, 0, 4, 6),
            P("b", 4, 0, 4, 6),
        ];

        var board = GridLayoutEngine.Move(input, "b", 0, 0);

        // The dragged panel wins the cell it was dropped on.
        Assert.Equal(0, Find(board, "b").X);
        Assert.Equal(0, Find(board, "b").Y);
        AssertNoOverlaps(board);
    }

    [Fact]
    public void Move_Displaces_The_Occupant_Downward()
    {
        PanelPlacement[] input = [
            P("a", 0, 0, 12, 6),
            P("b", 0, 6, 12, 6),
        ];

        var board = GridLayoutEngine.Move(input, "b", 0, 0);

        Assert.Equal(0, Find(board, "b").Y);
        Assert.True(Find(board, "a").Y >= Find(board, "b").Bottom);
        AssertNoOverlaps(board);
    }

    [Fact]
    public void Resize_Widening_Pushes_Neighbours_Away()
    {
        PanelPlacement[] input = [
            P("a", 0, 0, 4, 6),
            P("b", 4, 0, 4, 6),
        ];

        var board = GridLayoutEngine.Resize(input, "a", 12, 6);

        Assert.Equal(12, Find(board, "a").W);
        AssertNoOverlaps(board);
    }

    [Fact]
    public void Resize_Clamps_To_Board_Limits()
    {
        var board = GridLayoutEngine.Resize([P("a", 0, 0, 4, 6)], "a", 99, 99);

        Assert.Equal(PanelLayout.Columns, Find(board, "a").W);
        Assert.Equal(PanelLayout.MaxHeight, Find(board, "a").H);
    }

    [Fact]
    public void Resolve_Terminates_With_Many_Stacked_Panels()
    {
        // All 14 panels dropped on the same cell — the pathological case for a push-down loop.
        var input = Enumerable.Range(0, 14).Select(i => P($"p{i}", 0, 0, 12, 4)).ToArray();

        var board = GridLayoutEngine.Resolve(input);

        Assert.Equal(14, board.Count);
        AssertNoOverlaps(board);
    }

    [Fact]
    public void FromLegacy_Wraps_Rows_And_Produces_A_Valid_Board()
    {
        string[] keys = ["a", "b", "c", "d"];
        var board = GridLayoutEngine.FromLegacy(
            keys,
            key => key == "b" ? PanelSizePreset.Wide : PanelSizePreset.Standard);

        Assert.Equal(4, board.Count);
        AssertNoOverlaps(board);
        Assert.All(board, p => Assert.True(p.Layout.Right <= PanelLayout.Columns));
    }

    [Fact]
    public void PanelLayout_Normalize_Shrinks_Rather_Than_Shifting_Off_Board()
    {
        var layout = new PanelLayout(10, 0, 12, 6).Normalize();

        Assert.True(layout.Right <= PanelLayout.Columns);
        Assert.True(layout.X >= 0);
    }
}

public class GridLayoutEngineLegacyTests
{
    [Fact]
    public void FromLegacy_Places_Three_Standard_Panels_Per_Row()
    {
        var keys = Enumerable.Range(0, 9).Select(i => $"p{i}").ToArray();

        var board = GridLayoutEngine.FromLegacy(keys, _ => PanelSizePreset.Standard);

        var row0 = board.Where(p => p.Layout.Y == 0).ToList();
        Assert.Equal(3, row0.Count);
        Assert.Equal(new List<int> { 0, 4, 8 }, row0.Select(p => p.Layout.X).OrderBy(x => x).ToList());
    }
}

public class GridLayoutEngineApplyTests
{
    private static PanelPlacement P(string key, int x, int y, int w, int h)
        => new(key, new PanelLayout(x, y, w, h));

    [Fact]
    public void Apply_Sets_Position_And_Size_Together()
    {
        // A left-edge drag: X moves right while W shrinks by the same amount.
        PanelPlacement[] input = [P("a", 0, 0, 8, 6), P("b", 8, 0, 4, 6)];

        var board = GridLayoutEngine.Apply(input, "a", new PanelLayout(2, 0, 6, 6));
        var a = board.Single(p => p.Key == "a").Layout;

        Assert.Equal(2, a.X);
        Assert.Equal(6, a.W);
    }

    [Fact]
    public void Apply_Is_Idempotent_For_The_Same_Target()
    {
        PanelPlacement[] input = [P("a", 0, 0, 4, 6), P("b", 4, 0, 4, 6)];
        var target = new PanelLayout(0, 0, 8, 6);

        var once = GridLayoutEngine.Apply(input, "a", target);
        var twice = GridLayoutEngine.Apply(once, "a", target);

        foreach (var panel in once)
        {
            var again = twice.Single(p => p.Key == panel.Key).Layout;
            Assert.Equal(panel.Layout.X, again.X);
            Assert.Equal(panel.Layout.Y, again.Y);
            Assert.Equal(panel.Layout.W, again.W);
            Assert.Equal(panel.Layout.H, again.H);
        }
    }

    [Fact]
    public void Apply_Repeated_Growth_Does_Not_Compound()
    {
        // Regression: resize used to add an absolute pointer delta to an already-updated width,
        // so each pointer move compounded and the panel grew erratically.
        PanelPlacement[] input = [P("a", 0, 0, 4, 6)];
        var origin = new PanelLayout(0, 0, 4, 6);

        var board = input.ToList();
        foreach (var deltaCols in new[] { 1, 2, 3 })
        {
            board = GridLayoutEngine.Apply(
                board, "a", new PanelLayout(origin.X, origin.Y, origin.W + deltaCols, origin.H));
        }

        Assert.Equal(7, board.Single(p => p.Key == "a").Layout.W);
    }
}
