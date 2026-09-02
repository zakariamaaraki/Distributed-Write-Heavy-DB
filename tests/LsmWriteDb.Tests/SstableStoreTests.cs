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
            Assert.True(File.Exists(Path.ChangeExtension(files[0], ".index.json")));

            var bloom = await store.ReadBloomFilterAsync(files[0]);
            var index = await store.ReadSparseIndexAsync(files[0]);

            Assert.NotNull(bloom);
            Assert.NotNull(index);
            Assert.Single(index);
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

    [Fact]
    public async Task WriteTableAsync_CreatesSparseIndexEntriesForBlocks()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            var store = new SstableStore(dataPath, blockSizeBytes: 180);
            var records = Enumerable
                .Range(1, 10)
                .Select(number => new StoredRecord(
                    number,
                    $"key:{number:000}",
                    new string((char)('a' + number), 40),
                    IsDeleted: false))
                .ToList();

            await store.WriteTableAsync(records);

            var file = store.GetDataFilesNewestFirst().Single();
            var index = await store.ReadSparseIndexAsync(file);

            Assert.NotNull(index);
            Assert.True(index.Count > 1);
            Assert.Equal(records.Count, index.Sum(entry => entry.RecordCount));
            Assert.All(index, entry =>
            {
                Assert.True(entry.Length > 0);
                Assert.True(entry.RecordCount > 0);
                Assert.True(string.CompareOrdinal(entry.FirstKey, entry.LastKey) <= 0);
            });

            Assert.Equal(0, index[0].Offset);
            Assert.True(index.Zip(index.Skip(1)).All(pair => pair.First.Offset + pair.First.Length == pair.Second.Offset));

            var hit = await store.TryGetAsync(file, "key:007");
            Assert.NotNull(hit);
            Assert.Equal(records[6].Value, hit.Value);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }

    [Fact]
    public async Task WriteTableAsyncSplitsLargeRunsAndUsesSparseIndexesForPointCandidates()
    {
        var dataPath = CreateTempDataPath();
        try
        {
            var store = new SstableStore(dataPath, blockSizeBytes: 180, maxFileSizeBytes: 500);
            var records = Enumerable
                .Range(1, 20)
                .Select(number => new StoredRecord(
                    number,
                    $"key:{number:000}",
                    new string('x', 80),
                    IsDeleted: false))
                .ToList();

            await store.WriteTableAsync(records);

            var files = store.GetDataFilesNewestFirst();
            Assert.True(files.Count > 1);
            Assert.Single(store.GetRunsByTier(0));
            Assert.All(files, file =>
            {
                Assert.True(new FileInfo(file).Length <= 500);
                Assert.True(File.Exists(Path.ChangeExtension(file, ".bloom.json")));
                Assert.True(File.Exists(Path.ChangeExtension(file, ".index.json")));
            });

            var candidates = await store.GetCandidateDataFilesAsync("key:017");
            Assert.NotEmpty(candidates);
            Assert.True(candidates.Count < files.Count);
            var hit = await store.TryGetAsync(candidates[0], "key:017");
            Assert.NotNull(hit);
            Assert.Equal(17, hit.Sequence);
        }
        finally
        {
            DeleteTempDataPath(dataPath);
        }
    }
    [Fact]
    public async Task ReadTableAsync_FallsBackToLegacyJsonSstableWithoutIndex()
    {
        var dataPath = CreateTempDataPath();

        try
        {
            Directory.CreateDirectory(Path.Combine(dataPath, "sstables"));
            var dataFile = Path.Combine(dataPath, "sstables", "sstable-legacy.json");
            var records = new[]
            {
                new StoredRecord(1, "alpha", "one", false),
                new StoredRecord(2, "bravo", "two", false)
            };

            await File.WriteAllTextAsync(
                dataFile,
                System.Text.Json.JsonSerializer.Serialize(records, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

            var store = new SstableStore(dataPath);

            var rows = await store.ReadTableAsync(dataFile);
            var hit = await store.TryGetAsync(dataFile, "bravo");

            Assert.Equal(["alpha", "bravo"], rows.Select(row => row.Key));
            Assert.NotNull(hit);
            Assert.Equal("two", hit.Value);
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
