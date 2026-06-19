namespace Pithos.Core.Core;

public sealed class BloomFilter
{
    private readonly bool[] _bits;
    private readonly int _hashCount;

    public BloomFilter(int capacity, double falsePositiveRate = 0.01)
    {
        int bitCount = OptimalBitCount(capacity, falsePositiveRate);
        _bits = new bool[bitCount];
        _hashCount = OptimalHashCount(bitCount, capacity);
    }

    public BloomFilter(bool[] bits, int hashCount)
    {
        _bits = bits;
        _hashCount = hashCount;
    }

    public void Add(byte[] key)
    {
        foreach (var index in GetIndexes(key))
            _bits[index] = true;
    }

    public bool MightContain(byte[] key)
    {
        foreach (var index in GetIndexes(key))
            if (!_bits[index]) return false;
        return true;
    }

    public (bool[] bits, int hashCount) Serialize() => (_bits, _hashCount);

    private IEnumerable<int> GetIndexes(byte[] key)
    {
        var h1 = MurmurHash3(key, 0);
        var h2 = MurmurHash3(key, h1);
        for (int i = 0; i < _hashCount; i++)
            yield return (int)(((h1 + (uint)i * h2) % (uint)_bits.Length));
    }

    private static uint MurmurHash3(byte[] data, uint seed)
    {
        uint h = seed;
        int i = 0;
        while (i + 4 <= data.Length)
        {
            uint k = BitConverter.ToUInt32(data, i);
            k *= 0xcc9e2d51; k = (k << 15) | (k >> 17); k *= 0x1b873593;
            h ^= k; h = (h << 13) | (h >> 19); h = h * 5 + 0xe6546b64;
            i += 4;
        }
        uint rem = 0;
        switch (data.Length - i)
        {
            case 3: rem ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: rem ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: rem ^= data[i]; rem *= 0xcc9e2d51; rem = (rem << 15) | (rem >> 17); rem *= 0x1b873593; h ^= rem; break;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16;
        return h;
    }

    private static int OptimalBitCount(int n, double p) =>
        (int)Math.Ceiling(-n * Math.Log(p) / (Math.Log(2) * Math.Log(2)));

    private static int OptimalHashCount(int m, int n) =>
        Math.Max(1, (int)Math.Round((double)m / n * Math.Log(2)));
}
