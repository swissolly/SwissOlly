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

    [Theory]
    [InlineData("{ \"scrapeIntervalSeconds\": \"fast\" }")]
    [InlineData("{ \"scrapeIntervalSeconds\": 99999999999999 }")]
    [InlineData("{ \"panelLayouts\": [1, 2, 3] }")]
    [InlineData("[]")]
    public async Task Load_Wrong_Types_Fall_Back_To_Defaults(string contents)
    {
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "swissolly-settings.json");
        await File.WriteAllTextAsync(configPath, contents);

        var loaded = await new SwissOlly.Store.Persistence.JsonConfigStore(configPath).LoadAsync(CancellationToken.None);

        Assert.Equal(SwissOllyConfig.DefaultScrapeIntervalSeconds, loaded.ScrapeIntervalSeconds);
        Assert.Equal(SwissOllyConfig.DefaultStorageDirectory, loaded.StorageDirectory);
        Assert.Single(Directory.GetFiles(_tempDir, "swissolly-settings.json.corrupt-*"));
    }

    [Fact]
    public async Task Load_Hostile_Values_Are_Normalized_Instead_Of_Trusted()
    {
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "swissolly-settings.json");
        await File.WriteAllTextAsync(configPath, """
            {
              "scrapeIntervalSeconds": -4,
              "graphWindowMinutes": 2147483647,
              "maxRetentionHours": 2147483647,
              "retentionInputText": "99999999d",
              "theme": 999,
              "storageDirectory": "\\\\evil\\share\\swissolly",
              "panelLayouts": { "cpu-total": null, "memory": { "x": -50, "y": 999999, "w": 0, "h": -1 } }
            }
            """);

        var loaded = await new SwissOlly.Store.Persistence.JsonConfigStore(configPath).LoadAsync(CancellationToken.None);

        Assert.Equal(SwissOllyConfig.DefaultScrapeIntervalSeconds, loaded.ScrapeIntervalSeconds);
        Assert.Equal(SwissOllyConfig.DefaultGraphWindowMinutes, loaded.GraphWindowMinutes);
        Assert.Equal(TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours), loaded.GetRetentionWindow());
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal(SwissOllyConfig.DefaultStorageDirectory, loaded.StorageDirectory);
        Assert.False(loaded.PanelLayouts.ContainsKey("cpu-total"));
        var memory = loaded.PanelLayouts["memory"];
        Assert.InRange(memory.X, 0, PanelLayout.Columns - 1);
        Assert.InRange(memory.W, 1, PanelLayout.Columns);
        Assert.InRange(memory.H, PanelLayout.MinHeight, PanelLayout.MaxHeight);
        Assert.InRange(memory.Y, 0, PanelLayout.MaxY);
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
