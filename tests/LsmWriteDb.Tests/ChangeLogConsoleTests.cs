namespace LsmWriteDb.Tests;

public sealed class ChangeLogConsoleTests
{
    [Fact]
    public void StaticAssets_IncludeChangeLogStreamShellAndEndpoints()
    {
        var html = StaticAssetTestHelper.Read(Path.Combine("changes-console", "index.html"));
        var script = StaticAssetTestHelper.Read(Path.Combine("changes-console", "change-log-console.js"));

        Assert.Contains("Change Log Stream", html);
        Assert.Contains("id=\"fromSequence\"", html);
        Assert.Contains("/changes-console/change-log-console.css", html);
        Assert.Contains("/changes-console/change-log-console.js", html);
        Assert.Contains("new EventSource", script);
        Assert.Contains("/changes/stream?fromSequence=", script);
        Assert.Contains("fetch(`/changes?fromSequence=", script);
    }
}
