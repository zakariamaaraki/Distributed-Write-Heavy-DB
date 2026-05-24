namespace LsmWriteDb.Indexes;

internal sealed class BPlusTree<TKey, TValue>
    where TKey : notnull
{
    public const int DefaultOrder = 32;

    private readonly IComparer<TKey> _comparer;
    private readonly IEqualityComparer<TValue> _valueComparer;
    private readonly int _order;
    private readonly int _maxKeys;
    private Node _root;

    public BPlusTree(int order = DefaultOrder, IComparer<TKey>? comparer = null, IEqualityComparer<TValue>? valueComparer = null)
    {
        if (order < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "B+ tree order must be at least 3.");
        }

        _comparer = comparer ?? Comparer<TKey>.Default;
        _valueComparer = valueComparer ?? EqualityComparer<TValue>.Default;
        _order = order;
        _maxKeys = order - 1;
        _root = new LeafNode();
    }

    public void Insert(TKey key, TValue value)
    {
        var leaf = FindLeaf(key);
        var index = LowerBound(leaf.Keys, key);
        if (index < leaf.Keys.Count && _comparer.Compare(leaf.Keys[index], key) == 0)
        {
            if (!leaf.Values[index].Contains(value, _valueComparer))
            {
                leaf.Values[index].Add(value);
            }

            return;
        }

        leaf.Keys.Insert(index, key);
        leaf.Values.Insert(index, [value]);

        if (leaf.Keys.Count > _maxKeys)
        {
            SplitLeaf(leaf);
        }
    }

    public bool Remove(TKey key, TValue value)
    {
        var leaf = FindLeaf(key);
        var index = LowerBound(leaf.Keys, key);
        if (index >= leaf.Keys.Count || _comparer.Compare(leaf.Keys[index], key) != 0)
        {
            return false;
        }

        var removed = leaf.Values[index].Remove(value);
        if (!removed)
        {
            return false;
        }

        if (leaf.Values[index].Count == 0)
        {
            leaf.Values.RemoveAt(index);
            leaf.Keys.RemoveAt(index);
        }

        return true;
    }

    public IReadOnlyList<TValue> Search(TKey key)
    {
        var leaf = FindLeaf(key);
        var index = LowerBound(leaf.Keys, key);
        if (index >= leaf.Keys.Count || _comparer.Compare(leaf.Keys[index], key) != 0)
        {
            return [];
        }

        return leaf.Values[index].ToList();
    }

    public IReadOnlyList<KeyValuePair<TKey, IReadOnlyList<TValue>>> Entries()
    {
        var entries = new List<KeyValuePair<TKey, IReadOnlyList<TValue>>>();
        var leaf = FirstLeaf();
        while (leaf is not null)
        {
            for (var i = 0; i < leaf.Keys.Count; i++)
            {
                entries.Add(new KeyValuePair<TKey, IReadOnlyList<TValue>>(leaf.Keys[i], leaf.Values[i].ToList()));
            }

            leaf = leaf.Next;
        }

        return entries;
    }

    public BPlusTreeDump<TKey, TValue> Dump()
    {
        return new BPlusTreeDump<TKey, TValue>(
            _order,
            Height(_root),
            DumpNode(_root, level: 0),
            DumpLeaves());
    }

    private LeafNode FindLeaf(TKey key)
    {
        var current = _root;
        while (current is InternalNode internalNode)
        {
            var childIndex = UpperBound(internalNode.Keys, key);
            current = internalNode.Children[childIndex];
        }

        return (LeafNode)current;
    }

    private LeafNode FirstLeaf()
    {
        var current = _root;
        while (current is InternalNode internalNode)
        {
            current = internalNode.Children[0];
        }

        return (LeafNode)current;
    }

    private void SplitLeaf(LeafNode leaf)
    {
        var splitIndex = (leaf.Keys.Count + 1) / 2;
        var right = new LeafNode
        {
            Parent = leaf.Parent,
            Next = leaf.Next
        };

        right.Keys.AddRange(leaf.Keys.Skip(splitIndex));
        right.Values.AddRange(leaf.Values.Skip(splitIndex).Select(values => values.ToList()));

        leaf.Keys.RemoveRange(splitIndex, leaf.Keys.Count - splitIndex);
        leaf.Values.RemoveRange(splitIndex, leaf.Values.Count - splitIndex);
        leaf.Next = right;

        InsertIntoParent(leaf, right.Keys[0], right);
    }

    private void SplitInternal(InternalNode node)
    {
        var splitIndex = node.Keys.Count / 2;
        var promotedKey = node.Keys[splitIndex];
        var right = new InternalNode
        {
            Parent = node.Parent
        };

        right.Keys.AddRange(node.Keys.Skip(splitIndex + 1));
        right.Children.AddRange(node.Children.Skip(splitIndex + 1));
        foreach (var child in right.Children)
        {
            child.Parent = right;
        }

        node.Keys.RemoveRange(splitIndex, node.Keys.Count - splitIndex);
        node.Children.RemoveRange(splitIndex + 1, node.Children.Count - (splitIndex + 1));

        InsertIntoParent(node, promotedKey, right);
    }

    private void InsertIntoParent(Node left, TKey key, Node right)
    {
        if (left.Parent is null)
        {
            var root = new InternalNode();
            root.Keys.Add(key);
            root.Children.Add(left);
            root.Children.Add(right);
            left.Parent = root;
            right.Parent = root;
            _root = root;
            return;
        }

        var parent = left.Parent;
        var leftIndex = parent.Children.IndexOf(left);
        parent.Keys.Insert(leftIndex, key);
        parent.Children.Insert(leftIndex + 1, right);
        right.Parent = parent;

        if (parent.Keys.Count > _maxKeys)
        {
            SplitInternal(parent);
        }
    }

    private int LowerBound(List<TKey> keys, TKey key)
    {
        var low = 0;
        var high = keys.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_comparer.Compare(keys[mid], key) < 0)
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

    private int UpperBound(List<TKey> keys, TKey key)
    {
        var low = 0;
        var high = keys.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_comparer.Compare(keys[mid], key) <= 0)
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

    private BPlusTreeNodeDump<TKey, TValue> DumpNode(Node node, int level)
    {
        if (node is LeafNode leaf)
        {
            return new BPlusTreeNodeDump<TKey, TValue>(
                "leaf",
                level,
                leaf.Keys.ToList(),
                [],
                LeafEntries(leaf));
        }

        var internalNode = (InternalNode)node;
        return new BPlusTreeNodeDump<TKey, TValue>(
            "internal",
            level,
            internalNode.Keys.ToList(),
            internalNode.Children.Select(child => DumpNode(child, level + 1)).ToList(),
            []);
    }

    private IReadOnlyList<BPlusTreeLeafDump<TKey, TValue>> DumpLeaves()
    {
        var leaves = new List<BPlusTreeLeafDump<TKey, TValue>>();
        var leaf = FirstLeaf();
        var ordinal = 0;
        while (leaf is not null)
        {
            leaves.Add(new BPlusTreeLeafDump<TKey, TValue>(ordinal, LeafEntries(leaf)));
            ordinal++;
            leaf = leaf.Next;
        }

        return leaves;
    }

    private static IReadOnlyList<BPlusTreeEntryDump<TKey, TValue>> LeafEntries(LeafNode leaf)
    {
        var entries = new List<BPlusTreeEntryDump<TKey, TValue>>();
        for (var i = 0; i < leaf.Keys.Count; i++)
        {
            entries.Add(new BPlusTreeEntryDump<TKey, TValue>(leaf.Keys[i], leaf.Values[i].ToList()));
        }

        return entries;
    }

    private static int Height(Node node)
    {
        var height = 1;
        var current = node;
        while (current is InternalNode internalNode)
        {
            height++;
            current = internalNode.Children[0];
        }

        return height;
    }

    private abstract class Node
    {
        public List<TKey> Keys { get; } = [];

        public InternalNode? Parent { get; set; }
    }

    private sealed class InternalNode : Node
    {
        public List<Node> Children { get; } = [];
    }

    private sealed class LeafNode : Node
    {
        public List<List<TValue>> Values { get; } = [];

        public LeafNode? Next { get; set; }
    }
}

public sealed record BPlusTreeDump<TKey, TValue>(
    int Order,
    int Height,
    BPlusTreeNodeDump<TKey, TValue> Root,
    IReadOnlyList<BPlusTreeLeafDump<TKey, TValue>> Leaves);

public sealed record BPlusTreeNodeDump<TKey, TValue>(
    string Kind,
    int Level,
    IReadOnlyList<TKey> Keys,
    IReadOnlyList<BPlusTreeNodeDump<TKey, TValue>> Children,
    IReadOnlyList<BPlusTreeEntryDump<TKey, TValue>> Entries);

public sealed record BPlusTreeLeafDump<TKey, TValue>(
    int Ordinal,
    IReadOnlyList<BPlusTreeEntryDump<TKey, TValue>> Entries);

public sealed record BPlusTreeEntryDump<TKey, TValue>(
    TKey Key,
    IReadOnlyList<TValue> Values);
