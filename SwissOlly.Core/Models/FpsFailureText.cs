using System.Globalization;

namespace SwissOlly.Core.Models;

/// <summary>
/// The status line shown when FPS can't start. 1.2.0 said "sign out and back in" for any
/// access-denied error, even when the process already had the right, which sent people to do
/// something that couldn't help. Only say it when the process really lacks the right.
/// </summary>
public static class FpsFailureText
{
    public const string LogPath = @"%LocalAppData%\SwissOlly\fps.log";

    /// <param name="reason">The overlay's reason: <c>etw-access-denied</c>, <c>etw-start-failed-N</c>,
    /// <c>etw-enable-failed-N</c>, <c>etw-open-failed-N</c> or <c>etw-unavailable</c>.</param>
    /// <param name="processHasTraceRight">Whether this process's token holds Performance Log Users
    /// or administrator rights, the ones Windows asks for to start a trace session.</param>
    public static string For(string reason, bool processHasTraceRight)
    {
        if (reason == "etw-access-denied" && !processHasTraceRight)
        {
            return "FPS needs the \"Performance Log Users\" group, and this sign-in doesn't have it yet. "
                + "If you ticked \"Allow exact FPS\" when installing, sign out of Windows and back in. "
                + "If you didn't, run the installer again and tick it.";
        }

        return $"FPS couldn't start ({Describe(reason)}). SwissOlly keeps trying while the game is in front. Details: {LogPath}";
    }

    /// <summary>The step and the Windows error, in words, e.g. "Windows error 1450 starting the trace session".</summary>
    public static string Describe(string reason)
    {
        if (reason == "etw-access-denied")
        {
            return "Windows error 5, access denied, starting the trace session";
        }

        (string Prefix, string Step)[] steps =
        [
            ("etw-start-failed-", "starting the trace session"),
            ("etw-enable-failed-", "turning on the present events"),
            ("etw-open-failed-", "opening the trace"),
        ];

        foreach (var (prefix, step) in steps)
        {
            if (reason.StartsWith(prefix, StringComparison.Ordinal)
                && uint.TryParse(reason.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                return $"Windows error {code} {step}";
            }
        }

        return "Windows' event tracing isn't available";
    }
}
