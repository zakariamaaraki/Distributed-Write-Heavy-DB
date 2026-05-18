using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LsmWriteDb.Storage;

internal sealed class BloomFilter
{
    private readonly byte[] _bits;

    public int BitSize { get; }

    public int HashCount { get; }

    private BloomFilter(int bitSize, int hashCount, byte[] bits)
    {
        BitSize = bitSize;
        HashCount = hashCount;
        _bits = bits;
    }

    public static BloomFilter CreateForItemCount(int expectedItems, double falsePositiveRate = 0.01)
    {
        var items = Math.Max(expectedItems, 1);
        var probability = Math.Clamp(falsePositiveRate, 0.0001, 0.5);
        var bitCount = (int)Math.Ceiling(-(items * Math.Log(probability)) / (Math.Log(2) * Math.Log(2)));
        var hashCount = Math.Max(1, (int)Math.Round((bitCount / (double)items) * Math.Log(2)));
        return new BloomFilter(Math.Max(bitCount, 8), hashCount, new byte[BitsToBytes(Math.Max(bitCount, 8))]);
    }

    public static BloomFilter FromSnapshot(BloomFilterSnapshot snapshot)
    {
        var bits = Convert.FromBase64String(snapshot.BitsBase64);
        if (snapshot.BitSize <= 0)
        {
            throw new InvalidDataException("Bloom filter bit size must be positive.");
        }

        if (snapshot.HashCount <= 0)
        {
            throw new InvalidDataException("Bloom filter hash count must be positive.");
        }

        if (bits.Length != BitsToBytes(snapshot.BitSize))
        {
            throw new InvalidDataException("Bloom filter bitset length does not match the configured bit size.");
        }

        return new BloomFilter(snapshot.BitSize, snapshot.HashCount, bits);
    }

    public void Add(string value)
    {
        foreach (var index in GetIndexes(value))
        {
            SetBit(index);
        }
    }

    public bool MightContain(string value)
    {
        foreach (var index in GetIndexes(value))
        {
            if (!GetBit(index))
            {
                return false;
            }
        }

        return true;
    }

    public BloomFilterSnapshot ToSnapshot()
    {
        return new BloomFilterSnapshot(BitSize, HashCount, Convert.ToBase64String(_bits));
    }

    private IEnumerable<int> GetIndexes(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var h1 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(0, sizeof(ulong)));
        var h2 = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(sizeof(ulong), sizeof(ulong))) | 1UL;

        for (var i = 0; i < HashCount; i++)
        {
            var index = (int)((h1 + (ulong)i * h2) % (ulong)BitSize);
            yield return index;
        }
    }

    private void SetBit(int index)
    {
        var byteIndex = index / 8;
        var bitIndex = index % 8;
        _bits[byteIndex] |= (byte)(1 << bitIndex);
    }

    private bool GetBit(int index)
    {
        var byteIndex = index / 8;
        var bitIndex = index % 8;
        return (_bits[byteIndex] & (1 << bitIndex)) != 0;
    }

    private static int BitsToBytes(int bits)
    {
        return (bits + 7) / 8;
    }
}

internal sealed record BloomFilterSnapshot(int BitSize, int HashCount, string BitsBase64);
