using Router;

namespace LsmWriteDb.Tests;

public sealed class ReadConsistencyPolicyTests
{
    [Fact]
    public void MissingOrEventualHeaderUsesEventualReads()
    {
        Assert.True(ReadConsistencyPolicy.TryParse(null, out var missing));
        Assert.Equal(ReadConsistencyLevel.Eventual, missing);
        Assert.True(ReadConsistencyPolicy.TryParse("EVENTUAL", out var explicitEventual));
        Assert.Equal(ReadConsistencyLevel.Eventual, explicitEventual);
        Assert.False(ReadConsistencyPolicy.ShouldRouteToLeader(true, false, missing));
    }

    [Fact]
    public void StrongAndTransactionalReadsUseTheLeader()
    {
        Assert.True(ReadConsistencyPolicy.TryParse("strong", out var strong));
        Assert.True(ReadConsistencyPolicy.ShouldRouteToLeader(true, false, strong));
        Assert.True(ReadConsistencyPolicy.ShouldRouteToLeader(true, true, ReadConsistencyLevel.Eventual));
        Assert.False(ReadConsistencyPolicy.ShouldRouteToLeader(false, false, strong));
    }

    [Fact]
    public void UnknownHeaderValueIsRejected()
    {
        Assert.False(ReadConsistencyPolicy.TryParse("quorum", out _));
    }
}
