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
    public void SqlSelectStatementsAreClassifiedAsReads()
    {
        Assert.Equal("SELECT", ReadConsistencyPolicy.ClassifySqlStatement("  SELECT * FROM users"));
        Assert.Equal("SHOW TABLES", ReadConsistencyPolicy.ClassifySqlStatement("SHOW TABLES"));
        Assert.Equal("SEARCH", ReadConsistencyPolicy.ClassifySqlStatement("SEARCH articles MATCH 'database'"));
        Assert.Null(ReadConsistencyPolicy.ClassifySqlStatement("INSERT INTO users VALUES ('x')"));
    }
    [Fact]
    public void SessionConsistencyModesUseSequenceTokensWithoutForcingTheLeader()
    {
        Assert.True(ReadConsistencyPolicy.TryParse("monotonic", out var monotonic));
        Assert.True(ReadConsistencyPolicy.TryParse("consistent-prefix", out var prefix));
        Assert.True(ReadConsistencyPolicy.TryParse("session", out var session));
        Assert.Equal(ReadConsistencyLevel.ConsistentPrefix, prefix);
        Assert.Equal(ReadConsistencyLevel.ConsistentPrefix, session);
        Assert.True(ReadConsistencyPolicy.RequiresSessionSequence(monotonic));
        Assert.True(ReadConsistencyPolicy.RequiresSessionSequence(prefix));
        Assert.False(ReadConsistencyPolicy.ShouldRouteToLeader(true, false, monotonic));
    }

    [Fact]
    public void UnknownHeaderValueIsRejected()
    {
        Assert.False(ReadConsistencyPolicy.TryParse("quorum", out _));
    }
}
