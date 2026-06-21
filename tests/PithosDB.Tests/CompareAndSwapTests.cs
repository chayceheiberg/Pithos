using PithosDB.Core;

namespace PithosDB.Tests;

public class CompareAndSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CompareAndSwapTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] K(int i) => BitConverter.GetBytes(i);
    private static byte[] V(int i) => BitConverter.GetBytes(i * 10);

    // ── Successful swap ───────────────────────────────────────────────────

    [Fact]
    public void CAS_ExistingKey_CorrectExpected_ReturnsTrue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        bool swapped = db.CompareAndSwap(K(1), V(1), V(99));

        Assert.True(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(99), v);
    }

    [Fact]
    public void CAS_NonExistentKey_NullExpected_ReturnsTrue()
    {
        using var db = new PithosDb(_dir);

        bool swapped = db.CompareAndSwap(K(1), null, V(42));

        Assert.True(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(42), v);
    }

    [Fact]
    public void CAS_DeletedKey_NullExpected_ReturnsTrue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));
        db.Delete(K(1));

        bool swapped = db.CompareAndSwap(K(1), null, V(99));

        Assert.True(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(99), v);
    }

    // ── Failed swap ───────────────────────────────────────────────────────

    [Fact]
    public void CAS_ExistingKey_WrongExpected_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        bool swapped = db.CompareAndSwap(K(1), V(999), V(42));

        Assert.False(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(1), v); // unchanged
    }

    [Fact]
    public void CAS_NonExistentKey_ValueExpected_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);

        bool swapped = db.CompareAndSwap(K(1), V(1), V(99));

        Assert.False(swapped);
        Assert.False(db.TryGet(K(1), out _));
    }

    [Fact]
    public void CAS_ExistingKey_NullExpected_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        bool swapped = db.CompareAndSwap(K(1), null, V(99));

        Assert.False(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(1), v); // unchanged
    }

    // ── No side effects on failure ────────────────────────────────────────

    [Fact]
    public void CAS_Failed_DoesNotModifyValue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));
        db.Put(K(2), V(2));

        db.CompareAndSwap(K(1), V(999), V(42)); // wrong expected — fails

        Assert.True(db.TryGet(K(1), out var v1));
        Assert.Equal(V(1), v1);
        Assert.True(db.TryGet(K(2), out var v2));
        Assert.Equal(V(2), v2);
    }

    // ── Idempotency / repeated swaps ─────────────────────────────────────

    [Fact]
    public void CAS_ChainedSwaps_ApplyInOrder()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        Assert.True(db.CompareAndSwap(K(1), V(1), V(2)));
        Assert.True(db.CompareAndSwap(K(1), V(2), V(3)));
        Assert.False(db.CompareAndSwap(K(1), V(2), V(4))); // stale expected

        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(3), v);
    }

    // ── Durability ────────────────────────────────────────────────────────

    [Fact]
    public void CAS_Successful_PersistsAfterReopen()
    {
        using (var db = new PithosDb(_dir))
        {
            db.Put(K(1), V(1));
            db.CompareAndSwap(K(1), V(1), V(99));
        }

        using var db2 = new PithosDb(_dir);
        Assert.True(db2.TryGet(K(1), out var v));
        Assert.Equal(V(99), v);
    }

    // ── In-memory mode ────────────────────────────────────────────────────

    [Fact]
    public void CAS_InMemory_Works()
    {
        using var db = PithosDb.OpenInMemory();
        db.Put(K(1), V(1));

        Assert.True(db.CompareAndSwap(K(1), V(1), V(42)));
        Assert.False(db.CompareAndSwap(K(1), V(1), V(99))); // value changed already

        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(42), v);
    }

    // ── Concurrency ───────────────────────────────────────────────────────

    [Fact]
    public async Task CAS_Concurrent_ExactlyOneSwapSucceeds()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(0));

        int successCount = 0;
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() =>
            {
                if (db.CompareAndSwap(K(1), V(0), V(1)))
                    Interlocked.Increment(ref successCount);
            }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(1), v);
    }

    // ── Async ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompareAndSwapAsync_Successful_ReturnsTrue()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        bool swapped = await db.CompareAndSwapAsync(K(1), V(1), V(42));

        Assert.True(swapped);
        Assert.True(db.TryGet(K(1), out var v));
        Assert.Equal(V(42), v);
    }

    [Fact]
    public async Task CompareAndSwapAsync_Failed_ReturnsFalse()
    {
        using var db = new PithosDb(_dir);
        db.Put(K(1), V(1));

        bool swapped = await db.CompareAndSwapAsync(K(1), V(999), V(42));

        Assert.False(swapped);
    }

    [Fact]
    public async Task CompareAndSwapAsync_InsertIfAbsent_Works()
    {
        using var db = new PithosDb(_dir);

        bool swapped = await db.CompareAndSwapAsync(K(5), null, V(5));

        Assert.True(swapped);
        Assert.True(db.TryGet(K(5), out var v));
        Assert.Equal(V(5), v);
    }
}
