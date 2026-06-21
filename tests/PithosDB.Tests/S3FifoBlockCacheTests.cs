using PithosDB.Core.Core;
using PithosDB.Core.Storage;

namespace PithosDB.Tests;

public class S3FifoBlockCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public S3FifoBlockCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Block(int size, byte fill = 0)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    // ── Basic behaviour ───────────────────────────────────────────────────────

    [Fact]
    public void TryGet_ReturnsFalse_WhenNotCached()
    {
        var cache = new S3FifoBlockCache(1024);
        Assert.False(cache.TryGet("file.sst", 0, out _));
    }

    [Fact]
    public void TryGet_ReturnsBlock_AfterPut()
    {
        var cache = new S3FifoBlockCache(1024);
        var data = Block(64, fill: 7);
        cache.Put("file.sst", 0, data);

        Assert.True(cache.TryGet("file.sst", 0, out var result));
        Assert.Equal(data, result);
    }

    [Fact]
    public void Put_SecondCallForSameKey_IsNoOp()
    {
        var cache = new S3FifoBlockCache(1024);
        var first  = Block(64, fill: 1);
        var second = Block(64, fill: 2);

        cache.Put("f", 0, first);
        cache.Put("f", 0, second);

        cache.TryGet("f", 0, out var result);
        Assert.Equal(first, result);
    }

    // ── Small queue: cold eviction ────────────────────────────────────────────
    //
    // Small = 10 % of capacity; with capacity=1000 and 100-byte blocks, Small
    // holds exactly 1 block.  A block never accessed (Freq=0) is evicted cold
    // and its key is recorded in the ghost set.

    [Fact]
    public void ColdBlock_Evicted_WhenSmallQueueFull()
    {
        // Small=100 B (1 block), Main=900 B.
        var cache = new S3FifoBlockCache(1000);

        cache.Put("f", 0, Block(100, fill: 1));   // enters Small, Freq=0
        // No access — Freq stays 0.
        cache.Put("f", 100, Block(100, fill: 2)); // evicts f0 (cold) to ghost; f100 enters Small

        Assert.False(cache.TryGet("f", 0,   out _), "cold block should be evicted");
        Assert.True (cache.TryGet("f", 100, out _), "new block should remain in Small");
    }

    // ── Small queue: hot promotion ────────────────────────────────────────────
    //
    // A block accessed at least once (Freq=1) is promoted to Main when evicted
    // from Small rather than being dropped.

    [Fact]
    public void HotBlock_PromotedToMain_WhenEvictedFromSmall()
    {
        var cache = new S3FifoBlockCache(1000);

        cache.Put("f", 0, Block(100));    // enters Small, Freq=0
        cache.TryGet("f", 0, out _);      // Freq → 1
        cache.Put("f", 100, Block(100));  // evicts f0 (Freq=1) → promoted to Main

        Assert.True(cache.TryGet("f", 0,   out _), "hot block should survive in Main");
        Assert.True(cache.TryGet("f", 100, out _), "new block should be in Small");
    }

    // ── Ghost re-admission ────────────────────────────────────────────────────
    //
    // A block evicted cold from Small is recorded in the ghost set.  If it is
    // Put again while still in the ghost, it bypasses Small and goes directly
    // to Main — proving it has frequency ≥ 2.

    [Fact]
    public void GhostReAdmission_SkipsSmall_GoesDirectlyToMain()
    {
        var cache = new S3FifoBlockCache(1000);

        cache.Put("f", 0, Block(100, fill: 1));  // Small, Freq=0
        cache.Put("f", 100, Block(100));          // evicts f0 (cold) → ghost
        Assert.False(cache.TryGet("f", 0, out _));

        // Re-insert while key is in ghost — goes to Main.
        cache.Put("f", 0, Block(100, fill: 3));
        Assert.True(cache.TryGet("f", 0, out var result));
        Assert.Equal(Block(100, fill: 3), result);
    }

    // ── Main queue: one-chance eviction ───────────────────────────────────────
    //
    // When Main is full and eviction is required:
    //   • tail entry with Freq=1 → re-inserted at head with Freq=0 (one chance)
    //   • tail entry with Freq=0 → evicted permanently
    //
    // Setup: promote 9 blocks into Main via the hot path, access the oldest
    // (tail) to set its Freq=1, then force a Main eviction via ghost re-admission.
    // The one-chanced block should survive; the next cold tail entry is dropped.

    [Fact]
    public void MainEviction_OneChance_BlockWithFreqOne_IsGivenAnotherChance()
    {
        // Small=100 B (1 block), Main=900 B (9 blocks).
        var cache = new S3FifoBlockCache(1000);

        // Fill Main with 9 promoted blocks.
        // Each iteration: put a hot block → access it → push a filler to evict
        // the hot block (Freq=1) into Main.  The filler itself lands in Small
        // with Freq=0 and is evicted cold to ghost on the next iteration.
        for (int i = 0; i < 9; i++)
        {
            cache.Put("f", (long)i * 100, Block(100));   // enters Small
            cache.TryGet("f", (long)i * 100, out _);     // Freq → 1
            cache.Put("filler", i, Block(100));           // evicts hot block → Main
        }
        // Main=[f800,…,f100,f0] (f0 at tail), ghost={filler0…filler7}.

        // Give the tail (f0) one more access so Freq=1.
        cache.TryGet("f", 0, out _);

        // Re-admit filler0 from ghost → triggers two EvictFromMain calls:
        //   1st: tail=f0, Freq=1 → re-inserted at head with Freq=0.
        //   2nd: new tail=f100, Freq=0 → evicted.
        cache.Put("filler", 0, Block(100));

        Assert.True (cache.TryGet("f", 0,   out _), "one-chanced block should survive");
        Assert.False(cache.TryGet("f", 100, out _), "cold-tail block should be evicted");
    }

    // ── Scan resistance ───────────────────────────────────────────────────────
    //
    // Hot blocks (accessed ≥ 2 times) land in Main after their first eviction
    // from Small, so subsequent cold-scan traffic cannot displace them.

    [Fact]
    public void HotBlocks_SurviveEvictionPressure_FromColdScans()
    {
        // Small=20 B (2 blocks of 10 B), Main=180 B.
        var cache = new S3FifoBlockCache(200);

        cache.Put("f", 0,  Block(10, fill: 1));
        cache.Put("f", 10, Block(10, fill: 2));
        cache.TryGet("f", 0,  out _);   // Freq → 1
        cache.TryGet("f", 10, out _);   // Freq → 1

        // Two cold blocks: each eviction from full Small promotes a hot block to Main.
        cache.Put("scan", 0, Block(10)); // f0  → Main
        cache.Put("scan", 1, Block(10)); // f10 → Main

        Assert.True(cache.TryGet("f", 0,  out _), "hot block should survive in Main");
        Assert.True(cache.TryGet("f", 10, out _), "hot block should survive in Main");
    }

    // ── EvictFile ─────────────────────────────────────────────────────────────

    [Fact]
    public void EvictFile_RemovesAllBlocksForPath_FromSmall()
    {
        var cache = new S3FifoBlockCache(1024);
        cache.Put("a.sst", 0,   Block(64));
        cache.Put("a.sst", 64,  Block(64));
        cache.Put("b.sst", 0,   Block(64));

        cache.EvictFile("a.sst");

        Assert.False(cache.TryGet("a.sst", 0,  out _));
        Assert.False(cache.TryGet("a.sst", 64, out _));
        Assert.True (cache.TryGet("b.sst", 0,  out _), "other file should be unaffected");
    }

    [Fact]
    public void EvictFile_RemovesBlocks_FromMainAsWell()
    {
        // Promote a block into Main, then EvictFile should still remove it.
        var cache = new S3FifoBlockCache(1000);

        cache.Put("f", 0, Block(100));    // Small, Freq=0
        cache.TryGet("f", 0, out _);      // Freq → 1
        cache.Put("f", 100, Block(100));  // f0 promoted to Main; f100 in Small

        cache.EvictFile("f");

        Assert.False(cache.TryGet("f", 0,   out _), "Main block should be removed");
        Assert.False(cache.TryGet("f", 100, out _), "Small block should be removed");
    }

    [Fact]
    public void EvictFile_NoOp_WhenFileNotCached()
    {
        var cache = new S3FifoBlockCache(1024);
        cache.Put("a.sst", 0, Block(64));
        cache.EvictFile("b.sst"); // must not throw or disturb a.sst

        Assert.True(cache.TryGet("a.sst", 0, out _));
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentPutsAndGets_DoNotCorruptState()
    {
        var cache = new S3FifoBlockCache(64 * 200);
        const int threads      = 8;
        const int opsPerThread = 500;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                long offset = (t * opsPerThread + i) * 64L;
                cache.Put("f", offset, Block(64, fill: (byte)(offset % 256)));
                cache.TryGet("f", offset, out _);
            }
        })));
    }

    // ── Integration: SSTableReader with S3FifoBlockCache ─────────────────────

    [Fact]
    public void SSTableReader_WithS3FifoCache_ReturnsCorrectValues()
    {
        var path = Path.Combine(_dir, "test.sst");
        var entries = Enumerable.Range(0, 50)
            .Select(i => new KeyValuePair<byte[], byte[]?>(
                System.Text.Encoding.UTF8.GetBytes($"key-{i:D4}"),
                System.Text.Encoding.UTF8.GetBytes($"val-{i}")))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .ToList();

        SSTableWriter.Write(path, entries);

        var cache = new S3FifoBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        // Cold pass — blocks read from disk and admitted to Small/Main.
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var value));
            Assert.Equal(expected, value);
        }

        // Warm pass — blocks served from cache.
        foreach (var (key, expected) in entries)
        {
            Assert.True(reader.TryGet(key, out var value));
            Assert.Equal(expected, value);
        }
    }
}
