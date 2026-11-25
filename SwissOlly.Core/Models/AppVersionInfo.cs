using System.Runtime.InteropServices;

namespace SwissOlly.Core.Models;

/// <summary>
/// The version text Settings shows ("SwissOlly 1.2.1, x64") and the links built from it. Until
/// 1.2.1 the app showed its version nowhere; only startup-trace.log had it.
/// </summary>
public static class AppVersionInfo
{
    public const string RepositoryUrl = "https://github.com/swissolly/SwissOlly";

    /// <summary>
    /// "1.2.1" from the assembly's informational version, which the .NET SDK writes as
    /// "1.2.1+&lt;commit&gt;" when it knows the commit. Falls back to the assembly version.
    /// </summary>
    public static string Number(string? informationalVersion, Version? assemblyVersion)
    {
        var text = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }

        return assemblyVersion is null ? "unknown" : assemblyVersion.ToString(3);
    }

    /// <summary>The first 7 characters of the commit after "+", or null when there isn't one.</summary>
    public static string? Commit(string? informationalVersion)
    {
        var parts = informationalVersion?.Split('+', 2);
        if (parts is not { Length: 2 } || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        var commit = parts[1].Trim();
        return commit.Length > 7 ? commit[..7] : commit;
    }

    /// <summary>The names the installers use: x64 and ARM64.</summary>
    public static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "ARM64",
        Architecture.X86 => "x86",
        _ => architecture.ToString(),
    };

    /// <summary>"Windows 11 (build 26200)": builds from 22000 are Windows 11, though both report 10.0.</summary>
    public static string WindowsName(Version osVersion) =>
        osVersion.Major == 10
            ? $"Windows {(osVersion.Build >= 22000 ? 11 : 10)} (build {osVersion.Build})"
            : $"Windows {osVersion}";

    /// <summary>The line "Copy version info" puts on the clipboard, for bug reports.</summary>
    public static string Diagnostics(string number, string? commit, string architecture, string windows) =>
        $"SwissOlly {number}{(commit is null ? string.Empty : $" ({commit})")}, {architecture}, {windows}";

    public static string ReleaseNotesUrl(string number) => $"{RepositoryUrl}/releases/tag/v{number}";

    /// <summary>The bug form with the version and Windows fields filled in (GitHub fills form fields from query parameters named after their ids).</summary>
    public static string BugReportUrl(string number, string architecture, string windows) =>
        $"{RepositoryUrl}/issues/new?template=bug_report.yml"
        + $"&version={Uri.EscapeDataString($"{number} ({architecture})")}"
        + $"&windows={Uri.EscapeDataString(windows)}";
}
