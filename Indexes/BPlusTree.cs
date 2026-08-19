using System.Text;
using System.Text.Json;

namespace LsmWriteDb.Indexes;

internal sealed class DiskBackedBPlusTree
{
    public const int DefaultOrder = 32;

    private const string InternalKind = "internal";
    private const string LeafKind = "leaf";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly DiskIndexKeyComparer KeyComparer = new();

    private readonly string _directory;
    private readonly string _pagesDirectory;
    private readonly string _metadataPath;
    private DiskBPlusTreeMetadata _metadata;

    private DiskBackedBPlusTree(string directory, DiskBPlusTreeMetadata metadata)
    {
        _directory = directory;
        _pagesDirectory = Path.Combine(directory, "pages");
        _metadataPath = Path.Combine(directory, "metadata.json");
        _metadata = metadata;
    }

    public static bool Exists(string directory)
    {
        return File.Exists(Path.Combine(directory, "metadata.json"));
    }

    public static DiskBackedBPlusTree Open(string directory)
    {
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"B+ tree metadata not found at '{metadataPath}'.", metadataPath);
        }

        var metadata = JsonSerializer.Deserialize<DiskBPlusTreeMetadata>(File.ReadAllText(metadataPath), JsonOptions)
            ?? throw new InvalidOperationException($"B+ tree metadata at '{metadataPath}' is invalid.");
        return new DiskBackedBPlusTree(directory, metadata);
    }

    public static DiskBackedBPlusTree CreateNew(string directory, int order = DefaultOrder)
    {
        if (order < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "B+ tree order must be at least 3.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        var pagesDirectory = Path.Combine(directory, "pages");
        Directory.CreateDirectory(pagesDirectory);

        var metadata = new DiskBPlusTreeMetadata(order, RootPageId: 1, NextPageId: 2);
        var tree = new DiskBackedBPlusTree(directory, metadata);
        tree.WritePage(new DiskBPlusTreePage
        {
            PageId = metadata.RootPageId,
            Kind = LeafKind
        });
        tree.WriteMetadata();
        return tree;
    }

    public void Insert(string indexedValue, string rowKey)
    {
        var key = new DiskIndexKey(indexedValue, rowKey);
        var search = FindLeaf(key);
        var leaf = search.Leaf;
        var insertAt = LowerBound(leaf.Entries, key);
        if (insertAt < leaf.Entries.Count && KeyComparer.Compare(leaf.Entries[insertAt].ToKey(), key) == 0)
        {
            return;
        }

        leaf.Entries.Insert(insertAt, new DiskBPlusTreeLeafEntry(indexedValue, rowKey));
        if (leaf.Entries.Count <= MaxKeys)
        {
            WritePage(leaf);
            return;
        }

        SplitLeaf(leaf, search.ParentPageIds);
    }

    public bool Remove(string indexedValue, string rowKey)
    {
        var key = new DiskIndexKey(indexedValue, rowKey);
        var search = FindLeaf(key);
        var leaf = search.Leaf;
        var index = LowerBound(leaf.Entries, key);
        if (index >= leaf.Entries.Count || KeyComparer.Compare(leaf.Entries[index].ToKey(), key) != 0)
        {
            return false;
        }

        leaf.Entries.RemoveAt(index);
        WritePage(leaf);
        return true;
    }

    public IReadOnlyList<string> Search(string indexedValue)
    {
        var key = new DiskIndexKey(indexedValue, string.Empty);
        var search = FindLeaf(key);
        var results = new List<string>();
        var current = search.Leaf;

        while (true)
        {
            foreach (var entry in current.Entries)
            {
                var comparison = string.CompareOrdinal(entry.IndexedValue, indexedValue);
                if (comparison < 0)
                {
                    continue;
                }

                if (comparison > 0)
                {
                    return results;
                }

                results.Add(entry.RowKey);
            }

            if (current.NextLeafPageId is not long nextLeafPageId)
            {
                return results;
            }

            current = ReadPage(nextLeafPageId);
        }
    }

    public BPlusTreeDump<string, string> Dump()
    {
        return new BPlusTreeDump<string, string>(
            _metadata.Order,
            Height(),
            CountPages(),
            DumpNode(_metadata.RootPageId, level: 0),
            DumpLeaves());
    }

    private void SplitLeaf(DiskBPlusTreePage leaf, List<long> parentPageIds)
    {
        var splitIndex = (leaf.Entries.Count + 1) / 2;
        var right = NewPage(LeafKind);

        right.Entries.AddRange(leaf.Entries.Skip(splitIndex));
        right.NextLeafPageId = leaf.NextLeafPageId;

        leaf.Entries.RemoveRange(splitIndex, leaf.Entries.Count - splitIndex);
        leaf.NextLeafPageId = right.PageId;

        var promotedKey = right.Entries[0].ToKey();
        WritePage(leaf);
        WritePage(right);

        InsertIntoParent(leaf.PageId, promotedKey, right.PageId, parentPageIds);
    }

    private void SplitInternal(DiskBPlusTreePage page, List<long> parentPageIds)
    {
        var splitIndex = page.Keys.Count / 2;
        var promotedKey = page.Keys[splitIndex];
        var right = NewPage(InternalKind);

        right.Keys.AddRange(page.Keys.Skip(splitIndex + 1));
        right.Children.AddRange(page.Children.Skip(splitIndex + 1));

        page.Keys.RemoveRange(splitIndex, page.Keys.Count - splitIndex);
        page.Children.RemoveRange(splitIndex + 1, page.Children.Count - (splitIndex + 1));

        WritePage(page);
        WritePage(right);

        InsertIntoParent(page.PageId, promotedKey, right.PageId, parentPageIds);
    }

    private void InsertIntoParent(long leftPageId, DiskIndexKey promotedKey, long rightPageId, List<long> parentPageIds)
    {
        if (parentPageIds.Count == 0)
        {
            var root = NewPage(InternalKind);
            root.Keys.Add(promotedKey);
            root.Children.Add(leftPageId);
            root.Children.Add(rightPageId);
            WritePage(root);

            _metadata = _metadata with { RootPageId = root.PageId };
            WriteMetadata();
            return;
        }

        var parentPageId = parentPageIds[^1];
        parentPageIds.RemoveAt(parentPageIds.Count - 1);

        var parent = ReadPage(parentPageId);
        var leftIndex = parent.Children.IndexOf(leftPageId);
        if (leftIndex < 0)
        {
            throw new InvalidOperationException($"Parent page {parent.PageId} does not reference child page {leftPageId}.");
        }

        parent.Keys.Insert(leftIndex, promotedKey);
        parent.Children.Insert(leftIndex + 1, rightPageId);

        if (parent.Keys.Count <= MaxKeys)
        {
            WritePage(parent);
            return;
        }

        SplitInternal(parent, parentPageIds);
    }

    private LeafSearchResult FindLeaf(DiskIndexKey key)
    {
        var current = ReadPage(_metadata.RootPageId);
        var parents = new List<long>();

        while (current.Kind == InternalKind)
        {
            parents.Add(current.PageId);
            var childIndex = UpperBound(current.Keys, key);
            current = ReadPage(current.Children[childIndex]);
        }

        return new LeafSearchResult(current, parents);
    }

    private DiskBPlusTreePage FirstLeaf()
    {
        var current = ReadPage(_metadata.RootPageId);
        while (current.Kind == InternalKind)
        {
            current = ReadPage(current.Children[0]);
        }

        return current;
    }

    private DiskBPlusTreePage NewPage(string kind)
    {
        var page = new DiskBPlusTreePage
        {
            PageId = _metadata.NextPageId,
            Kind = kind
        };

        _metadata = _metadata with { NextPageId = _metadata.NextPageId + 1 };
        WriteMetadata();
        return page;
    }

    private DiskBPlusTreePage ReadPage(long pageId)
    {
        var pagePath = PagePath(pageId);
        var page = JsonSerializer.Deserialize<DiskBPlusTreePage>(File.ReadAllText(pagePath), JsonOptions)
            ?? throw new InvalidOperationException($"B+ tree page '{pagePath}' is invalid.");
        return page;
    }

    private void WritePage(DiskBPlusTreePage page)
    {
        Directory.CreateDirectory(_pagesDirectory);
        var pagePath = PagePath(page.PageId);
        var tempPath = pagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(page, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ReplaceFileWithRetry(tempPath, pagePath);
    }

    private void WriteMetadata()
    {
        Directory.CreateDirectory(_directory);
        var tempPath = _metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_metadata, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ReplaceFileWithRetry(tempPath, _metadataPath);
    }

    private static void ReplaceFileWithRetry(string tempPath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 8)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
    private string PagePath(long pageId)
    {
        return Path.Combine(_pagesDirectory, $"page-{pageId:D20}.json");
    }

    private int CountPages()
    {
        return Directory.Exists(_pagesDirectory)
            ? Directory.EnumerateFiles(_pagesDirectory, "page-*.json").Count()
            : 0;
    }

    private int Height()
    {
        var height = 1;
        var current = ReadPage(_metadata.RootPageId);
        while (current.Kind == InternalKind)
        {
            height++;
            current = ReadPage(current.Children[0]);
        }

        return height;
    }

    private BPlusTreeNodeDump<string, string> DumpNode(long pageId, int level)
    {
        var page = ReadPage(pageId);
        if (page.Kind == LeafKind)
        {
            return new BPlusTreeNodeDump<string, string>(
                page.PageId,
                LeafKind,
                level,
                LeafBoundaryKeys(page),
                [],
                LeafEntries(page));
        }

        return new BPlusTreeNodeDump<string, string>(
            page.PageId,
            InternalKind,
            level,
            page.Keys.Select(DisplayKey).ToList(),
            page.Children.Select(child => DumpNode(child, level + 1)).ToList(),
            []);
    }

    private IReadOnlyList<BPlusTreeLeafDump<string, string>> DumpLeaves()
    {
        var leaves = new List<BPlusTreeLeafDump<string, string>>();
        var leaf = FirstLeaf();
        var ordinal = 0;
        while (true)
        {
            leaves.Add(new BPlusTreeLeafDump<string, string>(leaf.PageId, ordinal, LeafEntries(leaf)));
            ordinal++;

            if (leaf.NextLeafPageId is not long nextLeafPageId)
            {
                break;
            }

            leaf = ReadPage(nextLeafPageId);
        }

        return leaves;
    }

    private static IReadOnlyList<string> LeafBoundaryKeys(DiskBPlusTreePage leaf)
    {
        if (leaf.Entries.Count == 0)
        {
            return [];
        }

        return leaf.Entries
            .Select(entry => entry.IndexedValue)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<BPlusTreeEntryDump<string, string>> LeafEntries(DiskBPlusTreePage leaf)
    {
        return leaf.Entries
            .GroupBy(entry => entry.IndexedValue, StringComparer.Ordinal)
            .Select(group => new BPlusTreeEntryDump<string, string>(
                group.Key,
                group.Select(entry => entry.RowKey).ToList()))
            .ToList();
    }

    private static string DisplayKey(DiskIndexKey key)
    {
        return $"{key.IndexedValue} -> {key.RowKey}";
    }

    private int MaxKeys => _metadata.Order - 1;

    private static int LowerBound(List<DiskBPlusTreeLeafEntry> entries, DiskIndexKey key)
    {
        var low = 0;
        var high = entries.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (KeyComparer.Compare(entries[mid].ToKey(), key) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int UpperBound(List<DiskIndexKey> keys, DiskIndexKey key)
    {
        var low = 0;
        var high = keys.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (KeyComparer.Compare(keys[mid], key) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private sealed record LeafSearchResult(DiskBPlusTreePage Leaf, List<long> ParentPageIds);
}

internal sealed record DiskBPlusTreeMetadata(
    int Order,
    long RootPageId,
    long NextPageId);

internal sealed class DiskBPlusTreePage
{
    public long PageId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public List<DiskIndexKey> Keys { get; set; } = [];

    public List<long> Children { get; set; } = [];

    public List<DiskBPlusTreeLeafEntry> Entries { get; set; } = [];

    public long? NextLeafPageId { get; set; }
}

internal sealed record DiskBPlusTreeLeafEntry(string IndexedValue, string RowKey)
{
    public DiskIndexKey ToKey()
    {
        return new DiskIndexKey(IndexedValue, RowKey);
    }
}

internal sealed record DiskIndexKey(string IndexedValue, string RowKey);

internal sealed class DiskIndexKeyComparer : IComparer<DiskIndexKey>
{
    public int Compare(DiskIndexKey? x, DiskIndexKey? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var valueComparison = string.CompareOrdinal(x.IndexedValue, y.IndexedValue);
        return valueComparison != 0
            ? valueComparison
            : string.CompareOrdinal(x.RowKey, y.RowKey);
    }
}

public sealed record BPlusTreeDump<TKey, TValue>(
    int Order,
    int Height,
    int PageCount,
    BPlusTreeNodeDump<TKey, TValue> Root,
    IReadOnlyList<BPlusTreeLeafDump<TKey, TValue>> Leaves);

public sealed record BPlusTreeNodeDump<TKey, TValue>(
    long PageId,
    string Kind,
    int Level,
    IReadOnlyList<TKey> Keys,
    IReadOnlyList<BPlusTreeNodeDump<TKey, TValue>> Children,
    IReadOnlyList<BPlusTreeEntryDump<TKey, TValue>> Entries);

public sealed record BPlusTreeLeafDump<TKey, TValue>(
    long PageId,
    int Ordinal,
    IReadOnlyList<BPlusTreeEntryDump<TKey, TValue>> Entries);

public sealed record BPlusTreeEntryDump<TKey, TValue>(
    TKey Key,
    IReadOnlyList<TValue> Values);
