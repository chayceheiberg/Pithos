using PithosDB.Core;

namespace PithosDB.Tests;

public class PithosDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PithosDbTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Put_ThenTryGet_ReturnsValue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("hello"), V("world"));

        Assert.True(db.TryGet(K("hello"), out var value));
        Assert.Equal(V("world"), value);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);

        Assert.False(db.TryGet(K("missing"), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Put_OverwritesExistingKey()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("key"), V("first"));
        db.Put(K("key"), V("second"));

        Assert.True(db.TryGet(K("key"), out var value));
        Assert.Equal(V("second"), value);
    }

    [Fact]
    public void Delete_MakesKeyUnreadable()
    {
        using var db = new PithosDb(_dir);
        db.Put(K("key"), V("value"));
        db.Delete(K("key"));

        Assert.False(db.TryGet(K("key"), out _));
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        using var db = new PithosDb(_dir);
        db.Delete(K("ghost")); // should not throw
        Assert.False(db.TryGet(K("ghost"), out _));
    }

    [Fact]
    public void WalRecovery_RestoresUnflushedWrites()
    {
        // Write without triggering a flush, then reopen.
        db_Put(K("persisted"), V("yes"));

        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K("persisted"), out var value));
        Assert.Equal(V("yes"), value);

        void db_Put(byte[] k, byte[] v)
        {
            using var db = new PithosDb(_dir);
            db.Put(k, v);
        }
    }

    [Fact]
    public void WalRecovery_DoesNotReturnDeletedKeys()
    {
        using (var db = new PithosDb(_dir))
        {
            db.Put(K("key"), V("value"));
            db.Delete(K("key"));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K("key"), out _));
    }

    [Fact]
    public void SSTableRecovery_RestoresFlushedData()
    {
        // Force a flush by writing enough data to exceed the 4 MB threshold.
        using (var db = new PithosDb(_dir))
        {
            var bigValue = new byte[1024]; // 1 KB per entry
            for (int i = 0; i < 5000; i++)
                db.Put(K($"key-{i:D5}"), bigValue);
        }

        // Reopen — data must be recovered from SSTables, not the WAL.
        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K("key-00000"), out _));
        Assert.True(db2.TryGet(K("key-04999"), out _));
    }

    [Fact]
    public void SSTableRecovery_TombstoneRespected()
    {
        using (var db = new PithosDb(_dir))
        {
            var bigValue = new byte[1024];
            for (int i = 0; i < 5000; i++)
                db.Put(K($"key-{i:D5}"), bigValue);

            // Delete a key after the flush has happened (memtable tombstone).
            db.Delete(K("key-00000"));
        }

        using var db2 = new PithosDb(_dir);
        Assert.False(db2.TryGet(K("key-00000"), out _));
    }

    [Fact]
    public void MultipleWritesAndReads_Correct()
    {
        using var db = new PithosDb(_dir);
        var pairs = Enumerable.Range(0, 100)
            .Select(i => (key: K($"k{i}"), value: V($"v{i}")))
            .ToList();

        foreach (var (key, value) in pairs)
            db.Put(key, value);

        foreach (var (key, value) in pairs)
        {
            Assert.True(db.TryGet(key, out var result));
            Assert.Equal(value, result);
        }
    }

    // ── WriteBatch TTL guard ───────────────────────────────────────────────────

    [Fact]
    public void Write_BatchWithTtlEntry_Throws_WhenEnableTtlFalse()
    {
        using var db = new PithosDb(_dir); // EnableTtl=false by default
        var batch = new WriteBatch().Put(K("k"), V("v"), TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => db.Write(batch));
    }

    // ── Scan across MemTable + SSTables (k-way merge) ─────────────────────────
    //
    // With only MemTable data, CollectScan uses a single source and the PQ
    // comparator lambda never fires (heap of one item needs no comparison).
    // Flushing to an SSTable before scanning exercises:
    //   • the SSTable source loop in CollectScan (lines 243-244)
    //   • the PQ comparator body (lines 249-250)
    // Overwriting a key after flush ensures the same key appears in both
    // MemTable and SSTable, triggering the tie-breaker branch (line 250).

    [Fact]
    public void Scan_ReturnsEntries_FromSSTables()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 256 };
        using var db = new PithosDb(_dir, opts);

        db.Put(K("a"), new byte[128]);
        db.Put(K("b"), new byte[128]);
        db.Put(K("c"), new byte[128]); // triggers flush to SSTable

        var keys = db.Scan().Select(e => e.key).ToList();
        Assert.Contains(K("a"), keys);
        Assert.Contains(K("b"), keys);
        Assert.Contains(K("c"), keys);
    }

    [Fact]
    public void Scan_DeduplicatesKey_WhenPresentInBothMemTableAndSSTable()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 256 };
        using var db = new PithosDb(_dir, opts);

        // Flush "a" and "b" to an SSTable.
        db.Put(K("a"), new byte[128]);
        db.Put(K("b"), new byte[128]);
        db.Put(K("c"), new byte[128]); // triggers flush

        // Overwrite "a" — now in both SSTable (old) and MemTable (new).
        db.Put(K("a"), V("updated"));

        var results = db.Scan().ToList();

        // "a" must appear exactly once with the newest value.
        var aEntries = results.Where(e => e.key.SequenceEqual(K("a"))).ToList();
        Assert.Single(aEntries);
        Assert.Equal(V("updated"), aEntries[0].value);
    }

    // ── No-manifest SSTable recovery ──────────────────────────────────────────
    //
    // When a MANIFEST file is absent, RecoverSSTables falls back to scanning
    // the directory for *.sst files. Exercised by deleting the manifest between
    // two opens.

    [Fact]
    public void RecoverSSTables_WithoutManifest_RecoversByDirectoryScan()
    {
        var opts = new PithosOptions { MemTableSizeThreshold = 256 };

        using (var db = new PithosDb(_dir, opts))
        {
            db.Put(K("x"), new byte[128]);
            db.Put(K("y"), new byte[128]);
            db.Put(K("z"), new byte[128]); // triggers flush + manifest write
        }

        // Remove the manifest to force the fallback recovery path.
        File.Delete(Path.Combine(_dir, "MANIFEST"));

        using var db2 = new PithosDb(_dir, opts);
        Assert.True(db2.TryGet(K("x"), out _));
        Assert.True(db2.TryGet(K("y"), out _));
        Assert.True(db2.TryGet(K("z"), out _));
    }
}
