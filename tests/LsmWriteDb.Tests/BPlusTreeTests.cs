using LsmWriteDb.Indexes;

namespace LsmWriteDb.Tests;

public sealed class BPlusTreeTests
{
    [Fact]
    public void Search_ReturnsDuplicateIndexedValuesAcrossLeafSplits()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var tree = DiskBackedBPlusTree.CreateNew(dataPath, order: 4);

            tree.Insert("gold", "user:3");
            tree.Insert("silver", "user:2");
            tree.Insert("bronze", "user:1");
            tree.Insert("gold", "user:4");
            tree.Insert("platinum", "user:5");
            tree.Insert("gold", "user:6");

            Assert.Equal(["user:3", "user:4", "user:6"], tree.Search("gold"));
            Assert.Equal(["user:2"], tree.Search("silver"));
            Assert.Empty(tree.Search("missing"));
            Assert.True(File.Exists(Path.Combine(dataPath, "metadata.json")));
            Assert.True(Directory.GetFiles(Path.Combine(dataPath, "pages"), "page-*.json").Length > 1);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public void Remove_RemovesOneIndexedValueWithoutLosingOtherValues()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var tree = DiskBackedBPlusTree.CreateNew(dataPath, order: 4);

            tree.Insert("gold", "user:1");
            tree.Insert("gold", "user:2");
            tree.Insert("silver", "user:3");

            Assert.True(tree.Remove("gold", "user:1"));
            Assert.False(tree.Remove("gold", "missing"));

            Assert.Equal(["user:2"], tree.Search("gold"));
            Assert.Equal(["user:3"], tree.Search("silver"));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public void Open_RestoresTreeFromDiskPages()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var tree = DiskBackedBPlusTree.CreateNew(dataPath, order: 4);
            tree.Insert("bronze", "user:1");
            tree.Insert("gold", "user:2");
            tree.Insert("gold", "user:5");
            tree.Insert("platinum", "user:3");
            tree.Insert("silver", "user:4");

            var restored = DiskBackedBPlusTree.Open(dataPath);

            Assert.Equal(["user:2", "user:5"], restored.Search("gold"));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public void Search_ReturnsManyRowsForSameIndexedValueWithoutPostingListEntry()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var tree = DiskBackedBPlusTree.CreateNew(dataPath, order: 4);

            for (var i = 0; i < 50; i++)
            {
                tree.Insert("gold", $"user:{i:0000}");
            }

            var dump = tree.Dump();

            Assert.Equal(Enumerable.Range(0, 50).Select(i => $"user:{i:0000}"), tree.Search("gold"));
            Assert.True(dump.Leaves.Count > 1);
            Assert.All(dump.Leaves.SelectMany(leaf => leaf.Entries), entry => Assert.Equal("gold", entry.Key));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public void Dump_ReturnsRootAndLeafChainFromDiskPages()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var tree = DiskBackedBPlusTree.CreateNew(dataPath, order: 4);

            tree.Insert("bronze", "user:1");
            tree.Insert("gold", "user:2");
            tree.Insert("platinum", "user:3");
            tree.Insert("silver", "user:4");
            tree.Insert("gold", "user:5");

            var dump = tree.Dump();

            Assert.Equal(4, dump.Order);
            Assert.True(dump.Height >= 2);
            Assert.True(dump.PageCount > 1);
            Assert.Equal("internal", dump.Root.Kind);
            Assert.True(dump.Root.Children.Count > 1);
            Assert.Equal(["bronze", "gold", "platinum", "silver"], dump.Leaves.SelectMany(leaf => leaf.Entries).Select(entry => entry.Key));
            Assert.Contains(dump.Leaves.SelectMany(leaf => leaf.Entries), entry =>
                entry.Key == "gold" && entry.Values.SequenceEqual(["user:2", "user:5"]));
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    private static string CreateTempDataPath()
    {
        return Path.Combine(Path.GetTempPath(), "LsmWriteDb.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempDataPath(string dataPath)
    {
        if (Directory.Exists(dataPath))
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
