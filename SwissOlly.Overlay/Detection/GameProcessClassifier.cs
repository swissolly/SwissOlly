using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SwissOlly.Overlay.Detection;

internal static class GameProcessClassifier
{
    // Any other foreground app counts as a game, so this list (with the Windows folder and console
    // windows, below) is what keeps the overlay off everyday windows: chat, music, office,
    // launchers, terminals. A real game signal (a steady present rate) would let it shrink.
    private static readonly HashSet<string> DenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows shell and tools outside the Windows folder
        "SwissOlly.exe",
        "WinStore.App.exe",
        "Microsoft.Photos.exe",
        "Widgets.exe",
        "PhoneExperienceHost.exe",
        "SecHealthUI.exe",
        "SnippingTool.exe",
        "ScreenClippingHost.exe",
        "CalculatorApp.exe",
        "Notepad.exe",
        "mspaint.exe",

        // Overlays that take the front mid-game (Win+G, Alt+Z, Alt+R)
        "GameBar.exe",
        "NVIDIA Overlay.exe",
        "RadeonSoftware.exe",

        // Terminals and editors
        "WindowsTerminal.exe",
        "OpenConsole.exe",
        "pwsh.exe",
        "devenv.exe",
        "Code.exe",
        "Cursor.exe",
        "rider64.exe",
        "idea64.exe",
        "pycharm64.exe",
        "sublime_text.exe",
        "notepad++.exe",
        "GitHubDesktop.exe",

        // Browsers
        "msedge.exe",
        "chrome.exe",
        "firefox.exe",
        "brave.exe",
        "opera.exe",
        "vivaldi.exe",

        // Chat, calls, music and video
        "Discord.exe",
        "Slack.exe",
        "Teams.exe",
        "ms-teams.exe",
        "Zoom.exe",
        "Telegram.exe",
        "WhatsApp.exe",
        "Signal.exe",
        "Spotify.exe",
        "vlc.exe",
        "ChatGPT.exe",
        "Claude.exe",

        // Office and documents
        "WINWORD.EXE",
        "EXCEL.EXE",
        "POWERPNT.EXE",
        "OUTLOOK.EXE",
        "olk.exe",
        "ONENOTE.EXE",
        "Acrobat.exe",
        "AcroRd32.exe",

        // Game launchers and recording (the games they start still count)
        "steam.exe",
        "steamwebhelper.exe",
        "EpicGamesLauncher.exe",
        "EADesktop.exe",
        "Battle.net.exe",
        "upc.exe",
        "GalaxyClient.exe",
        "Riot Client.exe",
        "RiotClientUx.exe",
        "LeagueClientUx.exe",
        "XboxPcApp.exe",
        "obs64.exe",
    };

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    internal static bool IsLikelyGame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // A console window reports the program running in it (cmd, python, ssh...), never a game.
        if (GetWindowClass(hwnd) == "ConsoleWindowClass") return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        var path = GetImagePath(pid);
        if (path is not null && path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Explorer, Task Manager, Settings, mmc and every other part of Windows.
            Trace.WriteLine($"[SwissOllyOverlay] IsLikelyGame: denied Windows component '{path}'");
            return false;
        }

        string exeName;
        if (path is not null)
        {
            exeName = Path.GetFileName(path);
        }
        else
        {
            try
            {
                // From the process snapshot; some anti-cheat protected games refuse even the
                // limited handle above, and they should still get the overlay.
                using var proc = Process.GetProcessById((int)pid);
                exeName = proc.ProcessName + ".exe";
            }
            catch
            {
                Trace.WriteLine($"[SwissOllyOverlay] IsLikelyGame: can't identify pid={pid}, allowing");
                return true;
            }
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

    /// <summary>
    /// The executable's full path, read with the least access Windows offers (what Task Manager
    /// uses). Never <c>Process.MainModule</c>: that opens the game for reading its memory, which
    /// is exactly the kind of handle anti-cheat watches for.
    /// </summary>
    private static string? GetImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
