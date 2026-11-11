namespace SwissOlly.Core.Models;

/// <summary>What the overlay should do after a foreground poll or a hotkey press.</summary>
public enum OverlayVisibilityChange
{
    None,

    /// <summary>A game came to the front: track its FPS, move over it and show.</summary>
    ShowForGame,

    /// <summary>Show by the hotkey: pinned over whatever is in front, or back over the game it was
    /// hidden in.</summary>
    ShowForHotkey,

    /// <summary>Hidden by the hotkey over a game that's still in front: keep tracking its FPS.</summary>
    Suppress,

    /// <summary>Hidden with no game to follow: stop tracking FPS.</summary>
    Hide,
}

/// <summary>
/// When the in-game overlay shows. The foreground window is polled every 500 ms: a game in front
/// shows the overlay, and leaving the game hides it once another window has stayed in front for a
/// second poll, so a quick alt-tab doesn't flicker it (or restart FPS tracking, if it's the same
/// game coming back). The hotkey hides it over a game until you leave that game, and otherwise
/// pins it over whatever is in front until pressed again or a game comes to the front. Window
/// handles are plain numbers here, so this runs without Win32.
///
/// The second poll used to be skipped because the foreground hadn't changed since the first, so
/// the overlay stayed on the desktop after every game until the next one started.
/// </summary>
public sealed class OverlayVisibility
{
    private enum State { Hidden, ShownByGame, SuppressedInGame, ShownByHotkey }

    // Written under the caller's lock; read without it by the render path.
    private volatile State _state = State.Hidden;
    private nint _lastForeground;
    private nint _gameWindow;
    private bool _hidePending;

    public bool IsShown => _state is State.ShownByGame or State.ShownByHotkey;

    public OverlayVisibilityChange OnPoll(nint foreground, Func<nint, bool> isGame)
    {
        // An unchanged foreground is only judged again to confirm a pending hide.
        var confirming = _hidePending && foreground == _lastForeground;
        if (foreground == _lastForeground && !confirming)
        {
            return OverlayVisibilityChange.None;
        }

        _lastForeground = foreground;
        var followingGame = _state is State.ShownByGame or State.SuppressedInGame;
        if (isGame(foreground))
        {
            _hidePending = false;
            if (followingGame && foreground == _gameWindow)
            {
                // Back to the same game after a glance away: as it was (shown, or hidden by the
                // hotkey), and FPS tracking carries on.
                return OverlayVisibilityChange.None;
            }

            // A game came to the front, pinned or not: follow it, so its FPS is tracked, the
            // overlay sits over it, and the next press hides it there.
            _state = State.ShownByGame;
            _gameWindow = foreground;
            return OverlayVisibilityChange.ShowForGame;
        }

        if (!followingGame)
        {
            // Hidden, or pinned over the desktop: nothing to follow.
            return OverlayVisibilityChange.None;
        }

        if (confirming)
        {
            _hidePending = false;
            _state = State.Hidden;
            _gameWindow = 0;
            return OverlayVisibilityChange.Hide;
        }

        // The first poll away from the game may be a quick glance: wait for one more.
        _hidePending = true;
        return OverlayVisibilityChange.None;
    }

    public OverlayVisibilityChange OnHotkey()
    {
        switch (_state)
        {
            case State.ShownByGame when !_hidePending:
                _state = State.SuppressedInGame;
                return OverlayVisibilityChange.Suppress;

            case State.SuppressedInGame when !_hidePending:
                _state = State.ShownByGame;
                return OverlayVisibilityChange.ShowForHotkey;

            case State.ShownByGame:
            case State.SuppressedInGame:
            case State.ShownByHotkey:
                // Pressed after leaving the game (its hide still pending), or to unpin.
                _state = State.Hidden;
                _hidePending = false;
                _gameWindow = 0;
                return OverlayVisibilityChange.Hide;

            default:
                _state = State.ShownByHotkey;
                return OverlayVisibilityChange.ShowForHotkey;
        }
    }

    /// <summary>Hidden, with the next poll judging the foreground window afresh.</summary>
    public void Reset()
    {
        _state = State.Hidden;
        _lastForeground = 0;
        _gameWindow = 0;
        _hidePending = false;
    }
}
