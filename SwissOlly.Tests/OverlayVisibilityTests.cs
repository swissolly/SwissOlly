using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class OverlayVisibilityTests
{
    private const nint Game = 100;
    private const nint OtherGame = 101;
    private const nint Editor = 200;
    private const nint Browser = 300;

    private static bool IsGame(nint hwnd) => hwnd is Game or OtherGame;

    [Fact]
    public void Leaving_A_Game_Hides_The_Overlay_On_The_Second_Poll()
    {
        var overlay = new OverlayVisibility();
        Assert.Equal(OverlayVisibilityChange.ShowForGame, overlay.OnPoll(Game, IsGame));
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));

        // The game closes and the editor stays in front: one poll to be sure, then hide.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.True(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnPoll(Editor, IsGame));
        Assert.False(overlay.IsShown);

        // And it stays hidden on the desktop.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Browser, IsGame));
        Assert.False(overlay.IsShown);
    }

    [Fact]
    public void A_Quick_Alt_Tab_Back_To_The_Same_Game_Changes_Nothing()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);

        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        // The same game is back: no new show, so FPS tracking isn't restarted.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));
        Assert.True(overlay.IsShown);

        // The pending hide was for the alt-tab, not for whatever comes next.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));
        Assert.True(overlay.IsShown);
    }

    [Fact]
    public void Hopping_Between_Windows_After_A_Game_Still_Hides()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);

        overlay.OnPoll(Editor, IsGame);
        overlay.OnPoll(Browser, IsGame);
        Assert.True(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnPoll(Browser, IsGame));
    }

    [Fact]
    public void Switching_Games_Moves_The_Overlay_To_The_New_One()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);

        Assert.Equal(OverlayVisibilityChange.ShowForGame, overlay.OnPoll(OtherGame, IsGame));
        Assert.True(overlay.IsShown);
    }

    [Fact]
    public void The_Hotkey_Pins_The_Overlay_Over_The_Desktop_Until_Pressed_Again()
    {
        var overlay = new OverlayVisibility();
        Assert.Equal(OverlayVisibilityChange.ShowForHotkey, overlay.OnHotkey());

        // Pinned: the desktop in front doesn't hide it.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.True(overlay.IsShown);

        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnHotkey());
        Assert.False(overlay.IsShown);
    }

    [Fact]
    public void One_Press_Hides_The_Overlay_In_A_Game_And_One_Brings_It_Back()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);

        Assert.Equal(OverlayVisibilityChange.Suppress, overlay.OnHotkey());
        Assert.False(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));
        Assert.False(overlay.IsShown);

        Assert.Equal(OverlayVisibilityChange.ShowForHotkey, overlay.OnHotkey());
        Assert.True(overlay.IsShown);

        // Shown for the game again, so leaving it hides as usual.
        overlay.OnPoll(Editor, IsGame);
        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnPoll(Editor, IsGame));
    }

    [Fact]
    public void Quitting_A_Game_The_Overlay_Was_Hidden_In_Stops_Tracking_It()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);
        overlay.OnHotkey();

        // Same grace as leaving a shown game, then Hide (not None): the controller stops FPS
        // tracking for the game that's gone.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnPoll(Editor, IsGame));
        Assert.False(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));

        // The next game shows it again.
        Assert.Equal(OverlayVisibilityChange.ShowForGame, overlay.OnPoll(OtherGame, IsGame));
    }

    [Fact]
    public void A_Glance_Away_From_A_Game_It_Was_Hidden_In_Keeps_It_Hidden()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);
        overlay.OnHotkey();

        // A Discord toast or a quick alt-tab: back within a poll, still hidden, tracking kept.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));
        Assert.False(overlay.IsShown);

        // And the next press still brings it back.
        Assert.Equal(OverlayVisibilityChange.ShowForHotkey, overlay.OnHotkey());
        Assert.True(overlay.IsShown);
    }

    [Fact]
    public void Pinned_Over_The_Desktop_It_Follows_A_Game_And_One_Press_Hides_It_There()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Editor, IsGame);
        Assert.Equal(OverlayVisibilityChange.ShowForHotkey, overlay.OnHotkey());
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Browser, IsGame));

        // The game starts: tracked and placed over it (ShowForGame), not left pinned without FPS.
        Assert.Equal(OverlayVisibilityChange.ShowForGame, overlay.OnPoll(Game, IsGame));
        Assert.True(overlay.IsShown);

        Assert.Equal(OverlayVisibilityChange.Suppress, overlay.OnHotkey());
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Game, IsGame));
        Assert.False(overlay.IsShown);
    }

    [Fact]
    public void The_Hotkey_Right_After_Leaving_A_Game_Hides_And_Stops_Tracking()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);
        overlay.OnPoll(Editor, IsGame);

        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnHotkey());
        Assert.False(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.False(overlay.IsShown);
    }

    [Fact]
    public void No_Foreground_Window_For_Two_Polls_Counts_As_Leaving_The_Game()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);

        // GetForegroundWindow returns 0 while switching; a moment of it keeps the overlay up.
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(0, IsGame));
        Assert.True(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.Hide, overlay.OnPoll(0, IsGame));
    }

    [Fact]
    public void Reset_Judges_The_Current_Window_Afresh()
    {
        var overlay = new OverlayVisibility();
        overlay.OnPoll(Game, IsGame);
        overlay.OnPoll(Editor, IsGame);

        overlay.Reset();
        Assert.False(overlay.IsShown);
        Assert.Equal(OverlayVisibilityChange.None, overlay.OnPoll(Editor, IsGame));
        Assert.Equal(OverlayVisibilityChange.ShowForGame, overlay.OnPoll(Game, IsGame));
    }
}
