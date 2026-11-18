using System.Security.Principal;

namespace SwissOlly.Overlay.Fps;

/// <summary>
/// Whether this process may start a trace session: Windows asks for Performance Log Users or
/// Administrators. Read from the process token, which only picks up a new group membership at
/// the next sign-in, so "added by the installer" and "in this token" can differ.
/// </summary>
internal static class TraceRight
{
    private static readonly SecurityIdentifier PerformanceLogUsers = new("S-1-5-32-559");

    internal static bool ProcessHasRight()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            // IsInRole checks the token as Windows will: a filtered admin token's Administrators
            // group is deny-only and counts as absent, which is right, since it can't trace either.
            return principal.IsInRole(PerformanceLogUsers) || principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Unknown: say "couldn't start, see the log" rather than send someone to sign out.
            return true;
        }
    }
}
