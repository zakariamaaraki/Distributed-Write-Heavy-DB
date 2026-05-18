using LsmWriteDb.Storage;

namespace LsmWriteDb.Tests;

public sealed class BloomFilterTests
{
    [Fact]
    public void AddAndMightContain_ReturnsTrueForInsertedKeys()
    {
        var filter = BloomFilter.CreateForItemCount(8);

        filter.Add("apple");
        filter.Add("banana");
        filter.Add("carrot");

        Assert.True(filter.MightContain("apple"));
        Assert.True(filter.MightContain("banana"));
        Assert.True(filter.MightContain("carrot"));
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesMembership()
    {
        var filter = BloomFilter.CreateForItemCount(4);
        filter.Add("alpha");
        filter.Add("bravo");

        var restored = BloomFilter.FromSnapshot(filter.ToSnapshot());

        Assert.True(restored.MightContain("alpha"));
        Assert.True(restored.MightContain("bravo"));
    }
}
