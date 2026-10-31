namespace Vaktr.Core.Models;

public enum MetricCategory
{
    Cpu = 0,
    Memory = 1,
    Disk = 2,
    Network = 3,
    System = 4,
    Gpu = 5,
}

public enum MetricUnit
{
    Percent = 0,
    Gigabytes = 1,
    MegabytesPerSecond = 2,
    MegabitsPerSecond = 3,
    Megahertz = 4,
    Count = 5,
    Celsius = 6,
}

public enum ThemeMode
{
    Dark = 0,
    Light = 1,

    /// <summary>Deep blue-black with a cool cyan accent.</summary>
    Midnight = 2,

    /// <summary>Warm charcoal with an amber accent.</summary>
    Ember = 3,

    /// <summary>Deep green-grey with a mint accent.</summary>
    Forest = 4,

    /// <summary>Muted plum with a lavender accent.</summary>
    Violet = 5,

    /// <summary>Soft blue-grey, lighter than Dark — the classic low-contrast palette.</summary>
    Nord = 6,

    /// <summary>Warm off-white paper tone with a slate accent.</summary>
    Sandstone = 7,
}

public enum PanelSizePreset
{
    Compact = 0,
    Standard = 1,
    Wide = 2,
    Large = 3,
}

public enum TimeRangePreset
{
    OneMinute = 1,
    FiveMinutes = 5,
    FifteenMinutes = 15,
    ThirtyMinutes = 30,
    OneHour = 60,
    TwelveHours = 720,
    TwentyFourHours = 1440,
    TwoDays = 2880,
    FiveDays = 7200,
    SevenDays = 10080,
    ThirtyDays = 43200,
    NinetyDays = 129600,
    OneYear = 525600,
}

public enum RetentionPreset
{
    Unlimited = 0,
    OneDay = 1,
    SevenDays = 7,
    ThirtyDays = 30,
    NinetyDays = 90,
}
