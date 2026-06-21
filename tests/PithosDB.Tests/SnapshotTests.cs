using PithosDB.Core;

namespace PithosDB.Tests;

public class SnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public SnapshotTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── TryGet isolation ──────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_TryGet_SeesValueAtSnapshotTime()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("original"));

        using var snap = db.GetSnapshot();
        db.Put(K("k"), V("updated"));

        Assert.True(snap.TryGet(K("k"), out var value));
        Assert.Equal(V("original"), value);
    }

    [Fact]
    public void Snapshot_TryGet_DoesNotSeeWritesAfterSnapshot()
    {
        using var db = PithosDb.OpenInMemory();
        using var snap = db.GetSnapshot();

        db.Put(K("new"), V("v"));

        Assert.False(snap.TryGet(K("new"), out _));
    }

    [Fact]
    public void Snapshot_TryGet_DoesNotSeeDeletesAfterSnapshot()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        using var snap = db.GetSnapshot();
        db.Delete(K("k"));

        Assert.True(snap.TryGet(K("k"), out var value));
        Assert.Equal(V("v"), value);
    }

    [Fact]
    public void Snapshot_TryGet_MissingKey_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("exists"), V("yes"));
        using var snap = db.GetSnapshot();

        Assert.False(snap.TryGet(K("nope"), out _));
    }

    [Fact]
    public void Snapshot_TryGet_TombstoneAtSnapshotTime_ReturnsFalse()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));
        db.Delete(K("k"));

        using var snap = db.GetSnapshot();
        Assert.False(snap.TryGet(K("k"), out _));
    }

    // ── Scan isolation ────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_Scan_ReturnsOnlyKeysAtSnapshotTime()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));

        using var snap = db.GetSnapshot();
        db.Put(K("c"), V("3"));

        var results = snap.Scan().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotEqual(K("c"), r.key));
    }

    [Fact]
    public void Snapshot_Scan_WithRange_ReturnsCorrectSubset()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("a"), V("1"));
        db.Put(K("b"), V("2"));
        db.Put(K("c"), V("3"));
        db.Put(K("d"), V("4"));

        using var snap = db.GetSnapshot();
        var results = snap.Scan(from: K("b"), to: K("c")).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(K("b"), results[0].key);
        Assert.Equal(K("c"), results[1].key);
    }

    [Fact]
    public void Snapshot_Scan_EmptyDb_ReturnsEmpty()
    {
        using var db = PithosDb.OpenInMemory();
        using var snap = db.GetSnapshot();
        Assert.Empty(snap.Scan());
    }

    // ── Multiple snapshots ────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_MultipleSnapshots_IndependentViews()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v1"));

        using var snap1 = db.GetSnapshot();
        db.Put(K("k"), V("v2"));
        using var snap2 = db.GetSnapshot();

        snap1.TryGet(K("k"), out var v1);
        snap2.TryGet(K("k"), out var v2);

        Assert.Equal(V("v1"), v1);
        Assert.Equal(V("v2"), v2);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_TryGet_AfterDispose_ThrowsObjectDisposedException()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        var snap = db.GetSnapshot();
        snap.Dispose();

        Assert.Throws<ObjectDisposedException>(() => snap.TryGet(K("k"), out _));
    }

    [Fact]
    public void Snapshot_Scan_AfterDispose_ThrowsObjectDisposedException()
    {
        using var db = PithosDb.OpenInMemory();
        var snap = db.GetSnapshot();
        snap.Dispose();

        Assert.Throws<ObjectDisposedException>(() => snap.Scan().ToList());
    }

    [Fact]
    public void Snapshot_Dispose_DbContinuesNormally()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K("k"), V("v"));

        db.GetSnapshot().Dispose();

        db.Put(K("k2"), V("v2"));
        Assert.True(db.TryGet(K("k2"), out _));
    }

    // ── Disk-backed (exercises SSTableReader path) ────────────────────────────

    [Fact]
    public void Snapshot_WithFlushedSSTables_SeesDataAtSnapshotTime()
    {
        using var db = new PithosDb(_dir, new PithosOptions { MemTableSizeThreshold = 64 });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"val{i}"));

        using var snap = db.GetSnapshot();

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V("after"));

        Assert.True(snap.TryGet(K("key000"), out var value));
        Assert.Equal(V("val0"), value);
    }

    [Fact]
    public void Snapshot_WithFlushedSSTables_Scan_ReturnsOriginalValues()
    {
        using var db = new PithosDb(_dir, new PithosOptions { MemTableSizeThreshold = 64 });

        for (int i = 0; i < 20; i++)
            db.Put(K($"key{i:D3}"), V($"val{i}"));

        using var snap = db.GetSnapshot();

        for (int i = 0; i < 20; i++)
            db.Delete(K($"key{i:D3}"));

        var results = snap.Scan().ToList();
        Assert.Equal(20, results.Count);
    }
}
