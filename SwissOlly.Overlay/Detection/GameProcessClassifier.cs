using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwissOlly.Overlay.Detection;

internal static class GameProcessClassifier
{
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "dwm.exe",
        "SwissOlly.exe",
        "ApplicationFrameHost.exe",
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe",
        "TextInputHost.exe",
        "LockApp.exe",
        "SystemSettings.exe",
        "taskmgr.exe",
        "sihost.exe",
        "RuntimeBroker.exe",
        "WinStore.App.exe",
        "Microsoft.Photos.exe",
        "WindowsTerminal.exe",
        "devenv.exe",
        "msedge.exe",
        "chrome.exe",
        "firefox.exe",
        "Code.exe",
    };

    internal static bool IsLikelyGame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        string exeName = "unknown.exe";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            try
            {
                exeName = Path.GetFileName(proc.MainModule?.FileName ?? (proc.ProcessName + ".exe"));
            }
            catch
            {
                exeName = proc.ProcessName + ".exe";
            }
        }
        catch
        {
            // Can't identify process — allow it through rather than blocking
            Trace.WriteLine($"[SwissOllyOverlay] IsLikelyGame: can't get process for pid={pid}, allowing");
            return true;
        }

        if (DenyList.Contains(exeName))
        {
            Trace.WriteLine($"[SwissOllyOverlay] IsLikelyGame: denied '{exeName}'");
            return false;
        }

        Trace.WriteLine($"[SwissOllyOverlay] IsLikelyGame: allowed '{exeName}' hwnd=0x{hwnd:X}");
        return true;
    }

    internal static uint GetProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
