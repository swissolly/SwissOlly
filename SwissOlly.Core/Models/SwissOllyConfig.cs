using System.Linq;
using System.Text.Json.Serialization;

namespace SwissOlly.Core.Models;

public sealed class SwissOllyConfig
{
    private const int DefaultScrapeIntervalSecondsValue = 2;
    private const int DefaultGraphWindowMinutesValue = 15;
    private const int DefaultMaxRetentionHoursValue = 24;
    private const int MaxGraphWindowMinutesValue = 60 * 24 * 30;

    /// <summary>Longest retention accepted: ten years, the same ceiling as MaxRetentionHours.</summary>
    public const long MaxRetentionMinutes = 3650L * 24 * 60;

    public int ScrapeIntervalSeconds { get; set; } = DefaultScrapeIntervalSecondsValue;

    public int GraphWindowMinutes { get; set; } = DefaultGraphWindowMinutesValue;

    public int MaxRetentionHours { get; set; }

    public RetentionPreset Retention { get; set; } = RetentionPreset.OneDay;

    public string RetentionInputText { get; set; } = string.Empty;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public string StorageDirectory { get; set; } = DefaultStorageDirectory;

    public bool LaunchOnStartup { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Set once the "still running in the tray" notice has been shown, so it shows once.</summary>
    public bool HasShownTrayNotice { get; set; }

    public Dictionary<string, bool> PanelVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> PanelOrder { get; set; } = [];

    public Dictionary<string, PanelSizePreset> PanelSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit panel geometry on the 12-column dashboard grid. Supersedes
    /// <see cref="PanelSizes"/> + <see cref="PanelOrder"/>, which could only express a column
    /// span and a flow position. Those two are still read so an existing board migrates once.
    /// </summary>
    public Dictionary<string, PanelLayout> PanelLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwissOlly");

    // Legacy locations keep the app's former name, Vaktr, on purpose: they are where existing
    // installs left their data, and RebrandMigration moves it across on first launch.

    /// <summary>Oldest layout: settings under roaming AppData.</summary>
    [JsonIgnore]
    public static string LegacySettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vaktr");

    /// <summary>Where installs from before the SwissOlly rebrand keep settings and history.</summary>
    [JsonIgnore]
    public static string PreRebrandSettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vaktr");

    [JsonIgnore]
    public static string PreRebrandDefaultStorageDirectory =>
        Path.Combine(PreRebrandSettingsDirectory, "Data");

    public const string DatabaseFileName = "swissolly-metrics.db";
    public const string PreRebrandDatabaseFileName = "vaktr-metrics.db";
    public const string SettingsFileName = "swissolly-settings.json";
    public const string PreRebrandSettingsFileName = "vaktr-settings.json";

    [JsonIgnore]
    public static string DefaultStorageDirectory =>
        Path.Combine(SettingsDirectory, "Data");

    [JsonIgnore]
    public static string LegacyDefaultStorageDirectory =>
        Path.Combine(LegacySettingsDirectory, "Data");

    public string GetDatabasePath() => Path.Combine(StorageDirectory, DatabaseFileName);

    public static string GetConfigPath() => Path.Combine(SettingsDirectory, SettingsFileName);

    public static string GetLegacyConfigPath() => Path.Combine(LegacySettingsDirectory, PreRebrandSettingsFileName);

    public TimeSpan GetRetentionWindow()
    {
        if (TryParseRetentionWindow(RetentionInputText, out var retentionWindow, out _))
        {
            return retentionWindow;
        }

        return TimeSpan.FromHours(Math.Clamp(MaxRetentionHours, 1, 24 * 3650));
    }

    /// <summary>
    /// Accepts only a fully-qualified local drive path. Relative paths would resolve against
    /// whatever the working directory happens to be; UNC and device (\\?\, \\.\) paths put
    /// SQLite on a share, where its file locking is unreliable and the database can corrupt.
    /// </summary>
    public static bool TryNormalizeStorageDirectory(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = candidate?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Path.IsPathFullyQualified(trimmed)
            || trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            // Collapses "..\.." segments so the stored path is the one actually used.
            var fullPath = Path.GetFullPath(trimmed);
            if (fullPath.IndexOf(':', 2) >= 0 ||
                fullPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(IsReservedDeviceName))
            {
                // "C:\data:stream" names an alternate data stream, and CON, NUL, COM1 and friends
                // are devices in any folder: neither is a directory SQLite can live in.
                return false;
            }

            normalized = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ');
        return name.Length is 3 or 4 &&
            (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
             (name.Length == 4 &&
              (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
              name[3] is >= '0' and <= '9'));
    }

    public SwissOllyConfig Normalize()
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
            StorageDirectory = TryNormalizeStorageDirectory(StorageDirectory, out var storageDirectory)
                ? storageDirectory
                : DefaultStorageDirectory;
        }

        // Saved before the rebrand, the default storage path still names the old folder. Follow
        // it to the new one only once RebrandMigration has moved it; if the move could not run
        // (files locked), keep using the old folder rather than start with empty history.
        if (string.Equals(StorageDirectory, PreRebrandDefaultStorageDirectory, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(PreRebrandDefaultStorageDirectory))
        {
            StorageDirectory = DefaultStorageDirectory;
        }

        PanelVisibility ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        PanelOrder ??= [];
        PanelSizes ??= new Dictionary<string, PanelSizePreset>(StringComparer.OrdinalIgnoreCase);
        PanelOrder = PanelOrder
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedPanelSizes = new Dictionary<string, PanelSizePreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, size) in PanelSizes)
        {
            if (string.IsNullOrWhiteSpace(key) || normalizedPanelSizes.ContainsKey(key))
            {
                continue;
            }

            normalizedPanelSizes[key] = Enum.IsDefined(typeof(PanelSizePreset), size)
                ? size
                : PanelSizePreset.Standard;
        }
        PanelSizes = normalizedPanelSizes;

        PanelLayouts ??= new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);
        var normalizedLayouts = new Dictionary<string, PanelLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in PanelLayouts)
        {
            if (string.IsNullOrWhiteSpace(key) || layout is null || normalizedLayouts.ContainsKey(key))
            {
                continue;
            }

            normalizedLayouts[key] = layout.Normalize();
        }
        PanelLayouts = normalizedLayouts;

        Overlay = (Overlay ?? new OverlayConfig()).Normalize();

        return this;
    }

    public static SwissOllyConfig CreateDefault() => new()
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
        if (!int.TryParse(amountText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        // Bounded before building the TimeSpan: "99999999d" overflowed TimeSpan and threw out of
        // config loading and the settings page instead of being rejected.
        var minutes = unit switch
        {
            'm' => (long)amount,
            'h' => amount * 60L,
            'd' => amount * 1440L,
            _ => -1L,
        };
        if (minutes <= 0 || minutes > MaxRetentionMinutes)
        {
            return false;
        }

        retentionWindow = TimeSpan.FromMinutes(minutes);
        normalizedText = $"{amount}{unit}";
        return true;
    }
}
