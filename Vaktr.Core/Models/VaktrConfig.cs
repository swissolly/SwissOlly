using System.Linq;
using System.Text.Json.Serialization;

namespace Vaktr.Core.Models;

public sealed class VaktrConfig
{
    private const int DefaultScrapeIntervalSecondsValue = 2;
    private const int DefaultGraphWindowMinutesValue = 15;
    private const int DefaultMaxRetentionHoursValue = 24;
    private const int MaxGraphWindowMinutesValue = 60 * 24 * 30;

    public int ScrapeIntervalSeconds { get; set; } = DefaultScrapeIntervalSecondsValue;

    public int GraphWindowMinutes { get; set; } = DefaultGraphWindowMinutesValue;

    public int MaxRetentionHours { get; set; }

    public RetentionPreset Retention { get; set; } = RetentionPreset.OneDay;

    public string RetentionInputText { get; set; } = string.Empty;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public string StorageDirectory { get; set; } = DefaultStorageDirectory;

    public bool LaunchOnStartup { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public Dictionary<string, bool> PanelVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> PanelOrder { get; set; } = [];

    public OverlayConfig? Overlay { get; set; }

    [JsonIgnore]
    public static int DefaultScrapeIntervalSeconds => DefaultScrapeIntervalSecondsValue;

    [JsonIgnore]
    public static int DefaultGraphWindowMinutes => DefaultGraphWindowMinutesValue;

    [JsonIgnore]
    public static int MaxGraphWindowMinutes => MaxGraphWindowMinutesValue;

    [JsonIgnore]
    public static int DefaultMaxRetentionHours => DefaultMaxRetentionHoursValue;

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vaktr");

    [JsonIgnore]
    public static string LegacySettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vaktr");

    [JsonIgnore]
    public static string DefaultStorageDirectory =>
        Path.Combine(SettingsDirectory, "Data");

    [JsonIgnore]
    public static string LegacyDefaultStorageDirectory =>
        Path.Combine(LegacySettingsDirectory, "Data");

    public string GetDatabasePath() => Path.Combine(StorageDirectory, "vaktr-metrics.db");

    public static string GetConfigPath() => Path.Combine(SettingsDirectory, "vaktr-settings.json");

    public static string GetLegacyConfigPath() => Path.Combine(LegacySettingsDirectory, "vaktr-settings.json");

    public TimeSpan GetRetentionWindow()
    {
        if (TryParseRetentionWindow(RetentionInputText, out var retentionWindow, out _))
        {
            return retentionWindow;
        }

        return TimeSpan.FromHours(Math.Clamp(MaxRetentionHours, 1, 24 * 3650));
    }

    public VaktrConfig Normalize()
    {
        var migratedFromLegacyStorage = string.Equals(
            StorageDirectory?.Trim(),
            LegacyDefaultStorageDirectory,
            StringComparison.OrdinalIgnoreCase);

        if (ScrapeIntervalSeconds is < 1 or > 60)
        {
            ScrapeIntervalSeconds = DefaultScrapeIntervalSecondsValue;
        }

        if (GraphWindowMinutes is < 1 or > MaxGraphWindowMinutesValue)
        {
            GraphWindowMinutes = DefaultGraphWindowMinutesValue;
        }

        if (MaxRetentionHours is < 1 or > 24 * 3650)
        {
            MaxRetentionHours = Retention switch
            {
                RetentionPreset.OneDay => 24,
                RetentionPreset.SevenDays => 24 * 7,
                RetentionPreset.ThirtyDays => 24 * 30,
                RetentionPreset.NinetyDays => 24 * 90,
                RetentionPreset.Unlimited => DefaultMaxRetentionHoursValue,
                _ => DefaultMaxRetentionHoursValue,
            };
        }

        if (!TryParseRetentionWindow(RetentionInputText, out var retentionWindow, out var normalizedRetentionInput))
        {
            RetentionInputText = MaxRetentionHours == DefaultMaxRetentionHoursValue
                ? string.Empty
                : FormatRetentionInput(MaxRetentionHours);
        }
        else
        {
            MaxRetentionHours = Math.Clamp((int)Math.Ceiling(retentionWindow.TotalHours), 1, 24 * 3650);
            RetentionInputText = normalizedRetentionInput;
        }

        if (!Enum.IsDefined(typeof(ThemeMode), Theme))
        {
            Theme = ThemeMode.Dark;
        }

        if (migratedFromLegacyStorage)
        {
            StorageDirectory = DefaultStorageDirectory;
            Theme = ThemeMode.Dark;
        }

        Retention = MaxRetentionHours switch
        {
            <= 24 => RetentionPreset.OneDay,
            <= 24 * 7 => RetentionPreset.SevenDays,
            <= 24 * 30 => RetentionPreset.ThirtyDays,
            <= 24 * 90 => RetentionPreset.NinetyDays,
            _ => RetentionPreset.NinetyDays,
        };

        if (string.IsNullOrWhiteSpace(StorageDirectory))
        {
            StorageDirectory = DefaultStorageDirectory;
        }
        else
        {
            // Resolve to absolute path to prevent path traversal (e.g., "..\..\")
            try
            {
                StorageDirectory = Path.GetFullPath(StorageDirectory.Trim());
            }
            catch
            {
                StorageDirectory = DefaultStorageDirectory;
            }
        }

        PanelVisibility ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        PanelOrder ??= [];
        PanelOrder = PanelOrder
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Overlay = (Overlay ?? new OverlayConfig()).Normalize();

        return this;
    }

    public static VaktrConfig CreateDefault() => new()
    {
        StorageDirectory = DefaultStorageDirectory,
        MaxRetentionHours = DefaultMaxRetentionHoursValue,
    };

    private static string FormatRetentionInput(int hours)
    {
        if (hours > 0 && hours % 24 == 0)
        {
            return $"{hours / 24}d";
        }

        return $"{hours}h";
    }

    public static bool TryParseRetentionWindow(string? text, out TimeSpan retentionWindow, out string normalizedText)
    {
        retentionWindow = TimeSpan.FromHours(DefaultMaxRetentionHoursValue);
        normalizedText = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var unit = char.ToLowerInvariant(trimmed[^1]);
        var amountText = trimmed[..^1].Trim();
        if (!int.TryParse(amountText, out var amount) || amount <= 0)
        {
            return false;
        }

        switch (unit)
        {
            case 'm':
                retentionWindow = TimeSpan.FromMinutes(amount);
                normalizedText = $"{amount}m";
                return true;
            case 'h':
                retentionWindow = TimeSpan.FromHours(amount);
                normalizedText = $"{amount}h";
                return true;
            case 'd':
                retentionWindow = TimeSpan.FromDays(amount);
                normalizedText = $"{amount}d";
                return true;
            default:
                return false;
        }
    }
}
