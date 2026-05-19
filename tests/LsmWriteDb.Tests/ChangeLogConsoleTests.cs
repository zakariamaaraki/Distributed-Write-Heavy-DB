using LsmWriteDb.ChangeLogs;

namespace LsmWriteDb.Tests;

public sealed class ChangeLogConsoleTests
{
    [Fact]
    public void Html_IncludesChangeLogStreamShellAndEndpoints()
    {
        var html = ChangeLogConsolePage.Html;

        Assert.Contains("Change Log Stream", html);
        Assert.Contains("id=\"fromSequence\"", html);
        Assert.Contains("new EventSource", html);
        Assert.Contains("/changes/stream?fromSequence=", html);
        Assert.Contains("fetch(`/changes?fromSequence=", html);
    }
}
