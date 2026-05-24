using LsmWriteDb.Indexes;

namespace LsmWriteDb.Tests;

public sealed class BPlusTreeTests
{
    [Fact]
    public void Search_ReturnsDuplicateValuesAcrossLeafSplits()
    {
        var tree = new BPlusTree<string, string>(order: 4, comparer: StringComparer.Ordinal);

        tree.Insert("gold", "user:3");
        tree.Insert("silver", "user:2");
        tree.Insert("bronze", "user:1");
        tree.Insert("gold", "user:4");
        tree.Insert("platinum", "user:5");
        tree.Insert("gold", "user:6");

        Assert.Equal(["user:3", "user:4", "user:6"], tree.Search("gold"));
        Assert.Equal(["user:2"], tree.Search("silver"));
        Assert.Empty(tree.Search("missing"));
    }

    [Fact]
    public void Remove_RemovesOneIndexedValueWithoutLosingOtherValues()
    {
        var tree = new BPlusTree<string, string>(order: 4, comparer: StringComparer.Ordinal);

        tree.Insert("gold", "user:1");
        tree.Insert("gold", "user:2");
        tree.Insert("silver", "user:3");

        Assert.True(tree.Remove("gold", "user:1"));
        Assert.False(tree.Remove("gold", "missing"));

        Assert.Equal(["user:2"], tree.Search("gold"));
        Assert.Equal(["user:3"], tree.Search("silver"));
    }

    [Fact]
    public void Dump_ReturnsRootAndLeafChain()
    {
        var tree = new BPlusTree<string, string>(order: 4, comparer: StringComparer.Ordinal);

        tree.Insert("bronze", "user:1");
        tree.Insert("gold", "user:2");
        tree.Insert("platinum", "user:3");
        tree.Insert("silver", "user:4");
        tree.Insert("gold", "user:5");

        var dump = tree.Dump();

        Assert.Equal(4, dump.Order);
        Assert.True(dump.Height >= 2);
        Assert.Equal("internal", dump.Root.Kind);
        Assert.True(dump.Root.Children.Count > 1);
        Assert.Equal(["bronze", "gold", "platinum", "silver"], dump.Leaves.SelectMany(leaf => leaf.Entries).Select(entry => entry.Key));
        Assert.Contains(dump.Leaves.SelectMany(leaf => leaf.Entries), entry =>
            entry.Key == "gold" && entry.Values.SequenceEqual(["user:2", "user:5"]));
    }
}
