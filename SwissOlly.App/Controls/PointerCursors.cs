using Microsoft.UI.Input;

namespace SwissOlly.App.Controls;

/// <summary>
/// Shared pointer cursors. A control assigns its cursor once, in its constructor, never from
/// <c>Loaded</c>.
/// </summary>
/// <remarks>
/// Rows and chips used to create a fresh cursor in every <c>Loaded</c>. Both crash dumps of the
/// "app dies while the process list is open" bug stop inside <c>ProtectedCursor</c>'s setter,
/// called from a process row's <c>Loaded</c>: WinUI fail-fasts there (0xC000027B, E_FAIL) when
/// the list re-adds rows at the wrong moment. Rows no longer set a cursor at all, nothing sets
/// one from <c>Loaded</c>, and one cached cursor per shape means no cursor objects are created
/// over and over or finalized off the UI thread.
/// </remarks>
internal static class PointerCursors
{
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> s_cursors = [];

    /// <summary>The shared cursor for <paramref name="shape"/>, or null where cursors are unavailable.</summary>
    public static InputSystemCursor? Get(InputSystemCursorShape shape)
    {
        if (s_cursors.TryGetValue(shape, out var cursor))
        {
            return cursor;
        }

        try
        {
            cursor = InputSystemCursor.Create(shape);
            s_cursors[shape] = cursor;
            return cursor;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
