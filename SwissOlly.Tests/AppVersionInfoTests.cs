using System.Runtime.InteropServices;
using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class AppVersionInfoTests
{
    [Theory]
    [InlineData("1.2.1+0a1b2c3d4e5f60718293", "1.2.1")]
    [InlineData("1.2.1", "1.2.1")]
    [InlineData(" 1.2.1 ", "1.2.1")]
    public void Number_Drops_The_Commit(string informational, string expected)
    {
        Assert.Equal(expected, AppVersionInfo.Number(informational, new Version(9, 9, 9, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+abc")]
    public void Number_Falls_Back_To_The_Assembly_Version(string? informational)
    {
        Assert.Equal("1.2.1", AppVersionInfo.Number(informational, new Version(1, 2, 1, 0)));
        Assert.Equal("unknown", AppVersionInfo.Number(informational, null));
    }

    [Theory]
    [InlineData("1.2.1+0a1b2c3d4e5f60718293", "0a1b2c3")]
    [InlineData("1.2.1+abc", "abc")]
    [InlineData("1.2.1", null)]
    [InlineData("1.2.1+", null)]
    [InlineData(null, null)]
    public void Commit_Is_The_Short_Hash_After_The_Plus(string? informational, string? expected)
    {
        Assert.Equal(expected, AppVersionInfo.Commit(informational));
    }

    [Fact]
    public void Architecture_Uses_The_Installer_Names()
    {
        Assert.Equal("x64", AppVersionInfo.ArchitectureName(Architecture.X64));
        Assert.Equal("ARM64", AppVersionInfo.ArchitectureName(Architecture.Arm64));
    }

    [Theory]
    [InlineData(10, 0, 26200, "Windows 11 (build 26200)")]
    [InlineData(10, 0, 22000, "Windows 11 (build 22000)")]
    [InlineData(10, 0, 19045, "Windows 10 (build 19045)")]
    public void Windows_11_Is_Told_Apart_By_Build(int major, int minor, int build, string expected)
    {
        Assert.Equal(expected, AppVersionInfo.WindowsName(new Version(major, minor, build, 0)));
    }

    [Fact]
    public void Diagnostics_Line_Has_Everything_A_Bug_Report_Needs()
    {
        Assert.Equal(
            "SwissOlly 1.2.1 (0a1b2c3), ARM64, Windows 11 (build 26200)",
            AppVersionInfo.Diagnostics("1.2.1", "0a1b2c3", "ARM64", "Windows 11 (build 26200)"));
        Assert.Equal(
            "SwissOlly 1.2.1, x64, Windows 10 (build 19045)",
            AppVersionInfo.Diagnostics("1.2.1", null, "x64", "Windows 10 (build 19045)"));
    }

    [Fact]
    public void Links_Point_At_This_Version_And_Fill_The_Bug_Form()
    {
        Assert.Equal("https://github.com/swissolly/SwissOlly/releases/tag/v1.2.1", AppVersionInfo.ReleaseNotesUrl("1.2.1"));
        Assert.Equal(
            "https://github.com/swissolly/SwissOlly/issues/new?template=bug_report.yml&version=1.2.1%20%28x64%29&windows=Windows%2011%20%28build%2026200%29",
            AppVersionInfo.BugReportUrl("1.2.1", "x64", "Windows 11 (build 26200)"));
    }
}
