using Router;

namespace LsmWriteDb.Tests;

public sealed class ReplicaLagCalculatorTests
{
    [Fact]
    public void Calculates_lag_for_a_follower()
    {
        Assert.Equal(25, ReplicaLagCalculator.Calculate(10_000, 9_975));
        Assert.Equal("behind", ReplicaLagCalculator.Status(25));
    }

    [Fact]
    public void Reports_caught_up_when_sequences_match_or_follower_is_ahead()
    {
        Assert.Equal(0, ReplicaLagCalculator.Calculate(10_000, 10_000));
        Assert.Equal(0, ReplicaLagCalculator.Calculate(10_000, 10_001));
        Assert.Equal("caught-up", ReplicaLagCalculator.Status(0));
    }

    [Fact]
    public void Reports_unknown_when_a_sequence_cannot_be_observed()
    {
        Assert.Null(ReplicaLagCalculator.Calculate(null, 10));
        Assert.Null(ReplicaLagCalculator.Calculate(10, null));
        Assert.Equal("unknown", ReplicaLagCalculator.Status(null));
    }
}
