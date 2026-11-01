namespace SwissOlly.Tests;

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(AppContext.BaseDirectory, $"swissolly-config-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("{ \"graphWindowMinutes\": 24")]
    [InlineData("")]
    public async Task Load_Corrupt_Config_Falls_Back_To_Defaults_And_Keeps_A_Copy(string contents)
    {
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "swissolly-settings.json");
        await File.WriteAllTextAsync(configPath, contents);

        var loaded = await new SwissOlly.Store.Persistence.JsonConfigStore(configPath).LoadAsync(CancellationToken.None);

        Assert.Equal(SwissOllyConfig.CreateDefault().Normalize().GraphWindowMinutes, loaded.GraphWindowMinutes);
        Assert.Single(Directory.GetFiles(_tempDir, "swissolly-settings.json.corrupt-*"));
    }

    [Fact]
    public async Task Save_Then_Load_Preserves_Settings_Across_Store_Restart()
    {
        var configPath = Path.Combine(_tempDir, "settings", "swissolly-settings.json");
        var storageDirectory = Path.Combine(_tempDir, "metrics");
        var config = new SwissOllyConfig
        {
            StorageDirectory = storageDirectory,
            GraphWindowMinutes = 240,
            MaxRetentionHours = 24,
            RetentionInputText = "90m",
            Theme = ThemeMode.Ember,
            PanelOrder = ["cpu-total", "memory"],
            PanelLayouts = new Dictionary<string, PanelLayout>
            {
                ["cpu-total"] = new PanelLayout(2, 4, 6, 9),
            },
            Overlay = new OverlayConfig { Enabled = true, HotkeyGesture = "Ctrl+Shift+F10" },
        }.Normalize();

        await new SwissOlly.Store.Persistence.JsonConfigStore(configPath).SaveAsync(config, CancellationToken.None);
        var loaded = await new SwissOlly.Store.Persistence.JsonConfigStore(configPath).LoadAsync(CancellationToken.None);

        Assert.Equal(240, loaded.GraphWindowMinutes);
        Assert.Equal("90m", loaded.RetentionInputText);
        Assert.Equal(TimeSpan.FromMinutes(90), loaded.GetRetentionWindow());
        Assert.Equal(storageDirectory, loaded.StorageDirectory);
        Assert.Equal(ThemeMode.Ember, loaded.Theme);
        Assert.Equal(new[] { "cpu-total", "memory" }, loaded.PanelOrder);
        Assert.Equal(2, loaded.PanelLayouts["cpu-total"].X);
        Assert.Equal(4, loaded.PanelLayouts["cpu-total"].Y);
        Assert.Equal(6, loaded.PanelLayouts["cpu-total"].W);
        Assert.Equal(9, loaded.PanelLayouts["cpu-total"].H);
        Assert.True(loaded.Overlay!.Enabled);
        Assert.Equal("Ctrl+Shift+F10", loaded.Overlay.HotkeyGesture);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
