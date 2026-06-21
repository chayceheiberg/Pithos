using PithosDB.Core;

namespace PithosDB.Tests;

public class InMemoryTests
{
    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── Factory ───────────────────────────────────────────────────────────────

    [Fact]
    public void OpenInMemory_NoOptions_CreatesInstance()
    {
        using var db = PithosDb.OpenInMemory();
        Assert.NotNull(db);
    }

    [Fact]
    public void OpenInMemory_WithValidOptions_CreatesInstance()
    {
        using var db = PithosDb.OpenInMemory(new PithosOptions { InMemory = true, EnableTtl = true });
        Assert.NotNull(db);
    }

    [Fact]
    public void OpenInMemory_OptionsWithInMemoryFalse_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PithosDb.OpenInMemory(new PithosOptions { InMemory = false }));
        Assert.Contains(nameof(PithosOptions.InMemory), ex.Message);
    }

    // ── Basic ops ─────────────────────────────────────────────────────────────

    [Fact]
    public void Put_ThenTryGet_ReturnsValue()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("hello"), V("world"));

        Assert.True(db.TryGet(K("hello"), out var val));
        Assert.Equal(V("world"), val);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        Assert.False(db.TryGet(K("missing"), out _));
    }

    [Fact]
    public void Put_OverwritesKey()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("first"));
        db.Put(K("k"), V("second"));

        Assert.True(db.TryGet(K("k"), out var val));
        Assert.Equal(V("second"), val);
    }

    [Fact]
    public void Delete_MakesKeyInvisible()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));
        db.Delete(K("k"));

        Assert.False(db.TryGet(K("k"), out _));
    }

    [Fact]
    public void Write_AppliesBatch()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("c"), V("old"));

        var batch = new WriteBatch()
            .Put(K("a"), V("1"))
            .Put(K("b"), V("2"))
            .Delete(K("c"));

        db.Write(batch);

        Assert.True(db.TryGet(K("a"), out var a));
        Assert.Equal(V("1"), a);
        Assert.True(db.TryGet(K("b"), out var b));
        Assert.Equal(V("2"), b);
        Assert.False(db.TryGet(K("c"), out _));
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_ReturnsAllLiveEntries()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));
        db.Delete(K("b"));

        var results = db.Scan().ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.key.SequenceEqual(K("a")));
        Assert.Contains(results, e => e.key.SequenceEqual(K("c")));
    }

    [Fact]
    public void Scan_WithBounds_ReturnsRange()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));
        db.Put(K("d"), V("4"));

        var results = db.Scan(from: K("b"), to: K("c")).ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.key.SequenceEqual(K("b")));
        Assert.Contains(results, e => e.key.SequenceEqual(K("c")));
    }

    // ── TTL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Put_WithTtl_ExpiresEntry()
    {
        using var db = PithosDb.OpenInMemory(new PithosOptions { InMemory = true, EnableTtl = true });
        db.Put(K("exp"), V("v"), TimeSpan.FromMilliseconds(1));
        db.Put(K("live"), V("v"), TimeSpan.FromHours(1));

        Thread.Sleep(20);

        Assert.False(db.TryGet(K("exp"), out _));
        Assert.True(db.TryGet(K("live"), out _));
    }

    // ── No disk I/O ───────────────────────────────────────────────────────────

    [Fact]
    public void OpenInMemory_WritesNoFilesToDisk()
    {
        var tempDir = Path.GetTempPath();
        var before = Directory.GetFiles(tempDir, "*.sst").ToHashSet();
        var before2 = Directory.GetFiles(tempDir, "wal.log").ToHashSet();

        using (var db = PithosDb.OpenInMemory())
        {
            for (int i = 0; i < 200; i++)
                db.Put(K($"key{i}"), V($"val{i}"));
        }

        var afterSst = Directory.GetFiles(tempDir, "*.sst").Except(before).ToList();
        var afterWal = Directory.GetFiles(tempDir, "wal.log").Except(before2).ToList();

        Assert.Empty(afterSst);
        Assert.Empty(afterWal);
    }

    [Fact]
    public void OpenInMemory_DoesNotCreateDirectory()
    {
        // The sentinel path ":memory:" must never be created on disk.
        Assert.False(Directory.Exists(":memory:"));

        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        Assert.False(Directory.Exists(":memory:"));
    }

    // ── Isolation between instances ───────────────────────────────────────────

    [Fact]
    public void TwoInMemoryInstances_AreIsolated()
    {
        using var db1 = PithosDb.OpenInMemory();
        using var db2 = PithosDb.OpenInMemory();

        db1.Put(K("key"), V("from-db1"));

        Assert.True(db1.TryGet(K("key"), out _));
        Assert.False(db2.TryGet(K("key"), out _));
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentWrites_DoNotCorruptData()
    {
        using var db = PithosDb.OpenInMemory();
        const int threads = 8;
        const int writesPerThread = 100;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < writesPerThread; i++)
                db.Put(K($"t{t}-k{i}"), V($"v{t}-{i}"));
        });

        for (int t = 0; t < threads; t++)
        {
            Assert.True(db.TryGet(K($"t{t}-k0"), out var val));
            Assert.Equal(V($"v{t}-0"), val);
        }
    }
}
