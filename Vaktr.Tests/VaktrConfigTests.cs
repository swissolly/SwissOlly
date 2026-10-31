using System.Text.Json;
using Vaktr.Core.Models;

namespace Vaktr.Tests;

public sealed class VaktrConfigTests
{
    [Fact]
    public void Normalize_Resets_Out_Of_Range_Values()
    {
        var config = new VaktrConfig
        {
            ScrapeIntervalSeconds = 0,
            GraphWindowMinutes = -1,
            StorageDirectory = string.Empty,
        };

        config.Normalize();

        Assert.Equal(2, config.ScrapeIntervalSeconds);
        Assert.Equal(VaktrConfig.DefaultGraphWindowMinutes, config.GraphWindowMinutes);
        Assert.False(string.IsNullOrWhiteSpace(config.StorageDirectory));
    }

    [Fact]
    public void Normalize_Clamps_High_ScrapeInterval()
    {
        var config = new VaktrConfig { ScrapeIntervalSeconds = 999 };
        config.Normalize();
        Assert.Equal(VaktrConfig.DefaultScrapeIntervalSeconds, config.ScrapeIntervalSeconds);
    }

    [Fact]
    public void Normalize_Keeps_Valid_ScrapeInterval()
    {
        var config = new VaktrConfig { ScrapeIntervalSeconds = 10 };
        config.Normalize();
        Assert.Equal(10, config.ScrapeIntervalSeconds);
    }

    [Fact]
    public void Normalize_Clamps_High_GraphWindowMinutes()
    {
        var config = new VaktrConfig { GraphWindowMinutes = int.MaxValue };
        config.Normalize();
        Assert.Equal(VaktrConfig.DefaultGraphWindowMinutes, config.GraphWindowMinutes);
    }

    [Theory]
    [InlineData("5m", 5)]
    [InlineData("24h", 24 * 60)]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("1d", 24 * 60)]
    [InlineData("90d", 90 * 24 * 60)]
    public void TryParseRetentionWindow_Parses_Valid_Formats(string input, int expectedMinutes)
    {
        var result = VaktrConfig.TryParseRetentionWindow(input, out var window, out var normalized);
        Assert.True(result);
        Assert.Equal(expectedMinutes, (int)window.TotalMinutes);
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("abc")]
    [InlineData("0h")]
    [InlineData("-5d")]
    public void TryParseRetentionWindow_Rejects_Invalid_Formats(string? input)
    {
        var result = VaktrConfig.TryParseRetentionWindow(input, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void Normalize_Sets_RetentionInputText_From_MaxRetentionHours()
    {
        var config = new VaktrConfig { RetentionInputText = "", MaxRetentionHours = 48 };
        config.Normalize();
        Assert.Equal("2d", config.RetentionInputText);
    }

    [Fact]
    public void Normalize_Preserves_Valid_RetentionInputText()
    {
        var config = new VaktrConfig { RetentionInputText = "7d", MaxRetentionHours = 24 };
        config.Normalize();
        Assert.Equal("7d", config.RetentionInputText);
    }

    [Fact]
    public void GetRetentionWindow_Uses_RetentionInputText_When_Set()
    {
        var config = new VaktrConfig { RetentionInputText = "7d", MaxRetentionHours = 1 };
        var window = config.GetRetentionWindow();
        Assert.Equal(TimeSpan.FromDays(7), window);
    }

    [Fact]
    public void GetRetentionWindow_Falls_Back_To_MaxRetentionHours()
    {
        var config = new VaktrConfig { RetentionInputText = "", MaxRetentionHours = 48 };
        var window = config.GetRetentionWindow();
        Assert.Equal(TimeSpan.FromHours(48), window);
    }

    [Fact]
    public void CreateDefault_Returns_Valid_Config()
    {
        var config = VaktrConfig.CreateDefault();
        Assert.Equal(VaktrConfig.DefaultStorageDirectory, config.StorageDirectory);
        Assert.Equal(VaktrConfig.DefaultMaxRetentionHours, config.MaxRetentionHours);
    }

    [Fact]
    public void Normalize_Resolves_StorageDirectory_To_Absolute_Path()
    {
        var config = new VaktrConfig { StorageDirectory = "relative\\path" };
        config.Normalize();
        Assert.True(Path.IsPathRooted(config.StorageDirectory));
    }

    [Fact]
    public void Normalize_Resets_Invalid_StorageDirectory()
    {
        var config = new VaktrConfig { StorageDirectory = "" };
        config.Normalize();
        Assert.Equal(VaktrConfig.DefaultStorageDirectory, config.StorageDirectory);
    }

    [Fact]
    public void Normalize_Cleans_PanelOrder_Duplicates()
    {
        var config = new VaktrConfig { PanelOrder = ["cpu-total", "memory", "cpu-total", ""] };
        config.Normalize();
        Assert.Equal(2, config.PanelOrder.Count);
        Assert.Contains("cpu-total", config.PanelOrder);
        Assert.Contains("memory", config.PanelOrder);
    }

    [Fact]
    public void Normalize_Cleans_PanelSizes_And_Uses_Standard_For_Invalid_Values()
    {
        var config = new VaktrConfig
        {
            PanelSizes = new Dictionary<string, PanelSizePreset>
            {
                ["cpu-total"] = PanelSizePreset.Wide,
                ["memory"] = (PanelSizePreset)99,
                [" "] = PanelSizePreset.Large,
            },
        };

        config.Normalize();

        Assert.Equal(PanelSizePreset.Wide, config.PanelSizes["CPU-TOTAL"]);
        Assert.Equal(PanelSizePreset.Standard, config.PanelSizes["memory"]);
        Assert.DoesNotContain(" ", config.PanelSizes.Keys);
    }

    [Fact]
    public void VaktrConfig_JsonRoundTrip_Preserves_Theme_And_PanelSizes()
    {
        var original = new VaktrConfig
        {
            Theme = ThemeMode.Light,
            PanelSizes = new Dictionary<string, PanelSizePreset>
            {
                ["cpu-total"] = PanelSizePreset.Large,
                ["memory"] = PanelSizePreset.Compact,
            },
        };

        var restored = JsonSerializer.Deserialize<VaktrConfig>(JsonSerializer.Serialize(original))!;
        restored.Normalize();

        Assert.Equal(ThemeMode.Light, restored.Theme);
        Assert.Equal(PanelSizePreset.Large, restored.PanelSizes["cpu-total"]);
        Assert.Equal(PanelSizePreset.Compact, restored.PanelSizes["memory"]);
    }

    [Fact]
    public void Normalize_Resets_Undefined_Theme_Value_To_Dark()
    {
        // 2 used to be the removed OLED theme and was the sentinel here; it is now a real
        // palette (Midnight), so this guards an value no ThemeMode defines instead.
        var config = new VaktrConfig { Theme = (ThemeMode)99 };

        config.Normalize();

        Assert.Equal(ThemeMode.Dark, config.Theme);
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Midnight)]
    [InlineData(ThemeMode.Ember)]
    [InlineData(ThemeMode.Forest)]
    [InlineData(ThemeMode.Violet)]
    [InlineData(ThemeMode.Nord)]
    [InlineData(ThemeMode.Sandstone)]
    public void Normalize_Preserves_Every_Defined_Theme(ThemeMode theme)
    {
        var config = new VaktrConfig { Theme = theme };

        config.Normalize();

        Assert.Equal(theme, config.Theme);
    }

    [Fact]
    public void Normalize_Initializes_Null_Collections()
    {
        var config = new VaktrConfig { PanelVisibility = null!, PanelOrder = null!, PanelSizes = null! };
        config.Normalize();
        Assert.NotNull(config.PanelVisibility);
        Assert.NotNull(config.PanelOrder);
        Assert.NotNull(config.PanelSizes);
    }

    // ── OverlayConfig tests ────────────────────────────────────────────────

    [Theory]
    [InlineData(9)]
    [InlineData(33)]
    [InlineData(-1)]
    [InlineData(0)]
    public void OverlayConfig_Normalize_Clamps_FontSize(int fontSize)
    {
        var cfg = new OverlayConfig { FontSizePx = fontSize };
        cfg.Normalize();
        Assert.Equal(14, cfg.FontSizePx);
    }

    [Theory]
    [InlineData("notacolor")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("")]
    [InlineData(null)]
    public void OverlayConfig_Normalize_Rejects_Malformed_Hex(string? hex)
    {
        var cfg = new OverlayConfig { TextColor = hex! };
        cfg.Normalize();
        Assert.Equal("#66E7FF", cfg.TextColor);
    }

    [Theory]
    [InlineData("NotAHotkey")]
    [InlineData("Ctrl")]
    [InlineData("F11")]
    [InlineData("")]
    [InlineData(null)]
    public void OverlayConfig_Normalize_Rejects_Invalid_Hotkey(string? gesture)
    {
        var cfg = new OverlayConfig { HotkeyGesture = gesture! };
        cfg.Normalize();
        Assert.Equal("Ctrl+Shift+F11", cfg.HotkeyGesture);
    }

    [Fact]
    public void OverlayConfig_JsonRoundTrip_Preserves_All_Properties()
    {
        var original = new OverlayConfig
        {
            Enabled = true,
            Mode = OverlayMode.Custom,
            Corner = OverlayCorner.BottomLeft,
            TextColor = "#FF0000",
            FontSizePx = 18,
            Opacity = 0.75,
            HotkeyGesture = "Ctrl+Alt+F9",
            CustomMetrics = ["fps", "cpu"],
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<OverlayConfig>(json)!;

        Assert.True(restored.Enabled);
        Assert.Equal(OverlayMode.Custom, restored.Mode);
        Assert.Equal(OverlayCorner.BottomLeft, restored.Corner);
        Assert.Equal("#FF0000", restored.TextColor);
        Assert.Equal(18, restored.FontSizePx);
        Assert.Equal(0.75, restored.Opacity);
        Assert.Equal("Ctrl+Alt+F9", restored.HotkeyGesture);
        Assert.Equal(["fps", "cpu"], restored.CustomMetrics);
    }

    [Fact]
    public void VaktrConfig_Normalize_Reconstructs_Null_Overlay()
    {
        var config = new VaktrConfig { Overlay = null };
        config.Normalize();
        Assert.NotNull(config.Overlay);
        Assert.Equal("Ctrl+Shift+F11", config.Overlay.HotkeyGesture);
        Assert.Equal("#66E7FF", config.Overlay.TextColor);
    }
}
