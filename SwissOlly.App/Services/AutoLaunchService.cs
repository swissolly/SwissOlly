using System.IO;
using Microsoft.Win32;

namespace SwissOlly.App.Services;

/// <summary>
/// "Start with Windows", backed by the per-user Run key. The key is the source of truth: the
/// installer can set it, and Task Manager's Startup apps page can disable it, so the app reads
/// it rather than overwriting it with a remembered setting.
/// </summary>
public sealed class AutoLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Explorer records Task Manager's enable/disable switch for each Run entry.
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppValueName = "SwissOlly";
    // The value written before the rebrand; it points at an exe that no longer exists.
    private const string PreRebrandValueName = "Vaktr";

    public bool IsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (runKey?.GetValue(AppValueName) is not string command || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            // Explorer writes 02 for an enabled entry and 03 for one switched off in Task Manager.
            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
            return approvedKey?.GetValue(AppValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return;
        }

        key.DeleteValue(PreRebrandValueName, false);

        // Turning it on here should win over an old Task Manager "disabled"; dropping the
        // record makes Explorer treat the entry as enabled again.
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true))
        {
            approvedKey?.DeleteValue(AppValueName, false);
        }

        if (!enabled)
        {
            key.DeleteValue(AppValueName, false);
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        key.SetValue(AppValueName, $"\"{processPath}\" {Program.StartInTrayArgument}");
    }

    /// <summary>
    /// Re-points an enabled entry whose executable no longer exists (an install moved to a new
    /// folder) at this one. An entry that still works is left alone, so a second copy, such as a
    /// development build, never takes over another install's startup entry.
    /// </summary>
    public void RefreshIfStale()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey is null)
            {
                return;
            }

            runKey.DeleteValue(PreRebrandValueName, false);
            var processPath = Environment.ProcessPath;
            if (runKey.GetValue(AppValueName) is string command &&
                !string.IsNullOrWhiteSpace(processPath) &&
                !File.Exists(ExtractExecutablePath(command)))
            {
                runKey.SetValue(AppValueName, $"\"{processPath}\" {Program.StartInTrayArgument}");
            }
        }
        catch (Exception)
        {
            // Best effort: the entry keeps whatever path it already has.
        }
    }

    private static string ExtractExecutablePath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : trimmed.Trim('"');
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
