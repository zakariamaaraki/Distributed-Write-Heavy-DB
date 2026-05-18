using LsmWriteDb.Storage;

namespace LsmWriteDb.Tests;

public sealed class SstableStoreTests
{
    [Fact]
    public async Task WriteTableAsync_CreatesBloomSidecarAndSupportsLookups()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = new SstableStore(dataPath);
            var records = new[]
            {
                new StoredRecord(1, "alpha", "one", false),
                new StoredRecord(2, "bravo", "two", false)
            };

            await store.WriteTableAsync(records);

            var files = store.GetDataFilesNewestFirst();
            Assert.Single(files);
            Assert.True(File.Exists(Path.ChangeExtension(files[0], ".bloom.json")));

            var bloom = await store.ReadBloomFilterAsync(files[0]);
            Assert.NotNull(bloom);
            Assert.True(bloom.MightContain("alpha"));

            var hit = await store.TryGetAsync(files[0], "bravo");
            var miss = await store.TryGetAsync(files[0], "charlie");

            Assert.NotNull(hit);
            Assert.Equal("two", hit.Value);
            Assert.Null(miss);
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
