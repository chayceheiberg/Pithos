using Pithos.Core.Core;
using Pithos.Core.Storage;

namespace Pithos.Core.Compaction;

public sealed class LeveledCompactor
{
    private readonly string _directory;
    private readonly int[] _levelSizeLimits;

    public LeveledCompactor(string directory, int levels = 7)
    {
        _directory = directory;
        _levelSizeLimits = new int[levels];
        int size = 10;
        for (int i = 0; i < levels; i++) { _levelSizeLimits[i] = size; size *= 10; }
    }

    public void CompactIfNeeded(List<List<string>> levels)
    {
        for (int level = 0; level < levels.Count - 1; level++)
        {
            if (levels[level].Count >= _levelSizeLimits[level])
                Compact(levels, level);
        }
    }

    private void Compact(List<List<string>> levels, int level)
    {
        while (levels.Count <= level + 1)
            levels.Add([]);

        var sources = levels[level].ToList();
        levels[level].Clear();

        var readers = sources.Select(p => new SSTableReader(p)).ToList();
        try
        {
            var merged = MergeEntries(readers);
            string outPath = System.IO.Path.Combine(_directory, $"L{level + 1}_{Guid.NewGuid():N}.sst");
            SSTableWriter.Write(outPath, merged);
            levels[level + 1].Add(outPath);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
            foreach (var p in sources) File.Delete(p);
        }
    }

    private static IEnumerable<KeyValuePair<byte[], byte[]?>> MergeEntries(List<SSTableReader> readers)
    {
        var comparer = ByteArrayComparer.Instance;
        var enumerators = readers.Select(r => r.ReadAllEntries().GetEnumerator()).ToList();
        var heap = new SortedDictionary<byte[], (byte[]? value, int readerIndex)>(comparer);

        for (int i = 0; i < enumerators.Count; i++)
        {
            if (enumerators[i].MoveNext())
            {
                var kv = enumerators[i].Current;
                heap[kv.Key] = (kv.Value, i);
            }
        }

        while (heap.Count > 0)
        {
            var first = heap.First();
            heap.Remove(first.Key);

            yield return new KeyValuePair<byte[], byte[]?>(first.Key, first.Value.value);

            int idx = first.Value.readerIndex;
            if (enumerators[idx].MoveNext())
            {
                var kv = enumerators[idx].Current;
                heap[kv.Key] = (kv.Value, idx);
            }
        }

        foreach (var e in enumerators) e.Dispose();
    }
}
