using System.Runtime.InteropServices;

namespace SwissOlly.App.Services;

/// <summary>
/// One SwissOlly per user session. A second launch asks the running copy to show its window and
/// exits, instead of starting a second collector on the same database. The mutex name is also what
/// the installer checks to close a running copy before upgrading.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "SwissOllyMutex";
    private const nint HwndBroadcast = 0xFFFF;
    private const uint AsfwAny = unchecked((uint)-1);

    private static Mutex? s_mutex;

    /// <summary>Registered once per session; the running copy's window listens for it.</summary>
    public static readonly uint ActivateMessage = RegisterWindowMessageW("SwissOlly.ActivateRunningInstance");

    /// <summary>Takes the session's instance lock. False when another copy already holds it.</summary>
    public static bool TryClaim()
    {
        try
        {
            s_mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                s_mutex.Dispose();
                s_mutex = null;
            }

            return createdNew;
        }
        catch (Exception)
        {
            // Can't tell (e.g. a same-named object we may not open): run rather than refuse to.
            return true;
        }
    }

    /// <summary>Asks the running copy to come forward. This process just got the foreground from
    /// the user's click, so it lets the other one take it.</summary>
    public static void SignalRunningInstance()
    {
        AllowSetForegroundWindow(AsfwAny);
        PostMessageW(HwndBroadcast, ActivateMessage, 0, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
