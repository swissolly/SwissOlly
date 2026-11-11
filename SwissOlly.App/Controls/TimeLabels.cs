using System.Globalization;

namespace SwissOlly.App.Controls;

/// <summary>
/// Date and time text in the user's regional format: 12- or 24-hour clock, day-month order and
/// month names all come from the current culture, which carries the Windows format settings.
/// Hard-coded patterns showed 24-hour axes next to 12-hour tooltips.
/// </summary>
internal static class TimeLabels
{
    private static DateTimeFormatInfo Format => CultureInfo.CurrentCulture.DateTimeFormat;

    /// <summary>"3:45 PM" or "15:45".</summary>
    public static string ShortTime(DateTime local) =>
        local.ToString(Format.ShortTimePattern, CultureInfo.CurrentCulture);

    /// <summary>"3:45:12 PM" or "15:45:12".</summary>
    public static string LongTime(DateTime local) =>
        local.ToString(Format.LongTimePattern, CultureInfo.CurrentCulture);

    /// <summary>"Nov 1" or "1. Nov.": the culture's month-day order with a short month name.</summary>
    public static string ShortDay(DateTime local) =>
        local.ToString(Format.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), CultureInfo.CurrentCulture);

    /// <summary>"Nov 1 3:45 PM".</summary>
    public static string DayAndTime(DateTime local) => $"{ShortDay(local)} {ShortTime(local)}";

    /// <summary>"11/1/2026 3:45:12 PM": unambiguous, for tooltips and pinned readings.</summary>
    public static string Full(DateTime local) =>
        $"{local.ToString(Format.ShortDatePattern, CultureInfo.CurrentCulture)} {LongTime(local)}";

    /// <summary>A from–to range, leaving out the day when both ends fall on the same one.</summary>
    public static string Range(DateTime startLocal, DateTime endLocal)
    {
        if (startLocal.Date == endLocal.Date)
        {
            var prefix = startLocal.Date == DateTime.Today ? string.Empty : $"{ShortDay(startLocal)} ";
            return $"{prefix}{ShortTime(startLocal)} – {ShortTime(endLocal)}";
        }

        return $"{DayAndTime(startLocal)} – {DayAndTime(endLocal)}";
    }
}
