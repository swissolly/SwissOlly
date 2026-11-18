using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class FpsFailureTextTests
{
    [Fact]
    public void Access_Denied_Without_The_Right_Says_How_To_Get_It()
    {
        var text = FpsFailureText.For("etw-access-denied", processHasTraceRight: false);

        Assert.Contains("Performance Log Users", text);
        Assert.Contains("sign out", text);
    }

    [Fact]
    public void Access_Denied_With_The_Right_Does_Not_Send_People_To_Sign_Out()
    {
        // The 1.2.0 bug: the app had the group and still told the owner to sign out and back in.
        var text = FpsFailureText.For("etw-access-denied", processHasTraceRight: true);

        Assert.DoesNotContain("sign out", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows error 5", text);
        Assert.Contains("keeps trying", text);
        Assert.Contains(FpsFailureText.LogPath, text);
    }

    [Theory]
    [InlineData("etw-start-failed-1450", "Windows error 1450 starting the trace session")]
    [InlineData("etw-enable-failed-87", "Windows error 87 turning on the present events")]
    [InlineData("etw-open-failed-2", "Windows error 2 opening the trace")]
    [InlineData("etw-unavailable", "Windows' event tracing isn't available")]
    [InlineData("etw-start-failed-x", "Windows' event tracing isn't available")]
    public void Names_The_Step_And_The_Error(string reason, string expected)
    {
        Assert.Equal(expected, FpsFailureText.Describe(reason));
        Assert.Contains(expected, FpsFailureText.For(reason, processHasTraceRight: false));
    }
}
