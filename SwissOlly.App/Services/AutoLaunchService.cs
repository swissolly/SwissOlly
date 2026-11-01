using Microsoft.Win32;

namespace SwissOlly.App.Services;

public sealed class AutoLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "SwissOlly";
    // The value written before the rebrand; it points at an exe that no longer exists.
    private const string PreRebrandValueName = "Vaktr";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return;
        }

        key.DeleteValue(PreRebrandValueName, false);

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

        key.SetValue(AppValueName, $"\"{processPath}\"");
    }
}
