namespace SwissOlly.Tests;

public sealed class OverlayConfigTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F11")]
    [InlineData("Ctrl+Alt+O")]
    [InlineData("Alt+F9")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Win+Alt+O")]
    public void Usable_Hotkeys_Pass(string gesture)
    {
        Assert.Null(OverlayConfig.DescribeHotkeyProblem(gesture));
    }

    [Theory]
    [InlineData("F11")]            // no modifier
    [InlineData("Shift+A")]        // would swallow capital A everywhere
    [InlineData("Ctrl+O")]         // Open, in every app
    [InlineData("Ctrl+C")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Win+L")]          // reserved by Windows
    [InlineData("Ctrl+é")]         // maps to a key that never fires
    [InlineData("")]
    [InlineData(null)]
    public void Hotkeys_That_Would_Hijack_Or_Never_Fire_Are_Refused(string? gesture)
    {
        Assert.NotNull(OverlayConfig.DescribeHotkeyProblem(gesture));
    }

    [Theory]
    [InlineData("#39ff14", "#39FF14")]
    [InlineData("39FF14", "#39FF14")]
    [InlineData("  #66E7FF ", "#66E7FF")]
    [InlineData("#12345", null)]
    [InlineData("neon", null)]
    [InlineData(null, null)]
    public void Hex_Colours_Are_Accepted_With_Or_Without_The_Hash(string? input, string? expected)
    {
        Assert.Equal(expected, OverlayConfig.NormalizeHexColor(input));
    }
}
