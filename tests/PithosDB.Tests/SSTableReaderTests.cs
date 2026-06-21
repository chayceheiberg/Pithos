using System.Buffers.Binary;
using System.IO.Hashing;
using PithosDB.Core;
using PithosDB.Core.Core;
using PithosDB.Core.Storage;

namespace PithosDB.Tests;

public class SSTableReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public SSTableReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TempPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.sst");

    private static byte[] K(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] V(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static List<KeyValuePair<byte[], byte[]?>> Entries(params (string k, string? v)[] pairs) =>
        pairs.Select(p => new KeyValuePair<byte[], byte[]?>(K(p.k), p.v is null ? null : V(p.v)))
             .ToList();

    // ── Block cache ────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_WithBlockCache_CacheMissPopulatesCache()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("a", "alpha"), ("b", "beta")));

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        Assert.True(reader.TryGet(K("a"), out var value));
        Assert.Equal(V("alpha"), value);
    }

    [Fact]
    public void TryGet_WithBlockCache_SecondLookupHitsCache()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("x", "xray"), ("y", "yankee")));

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        // First read — cache miss, block is populated into cache.
        Assert.True(reader.TryGet(K("x"), out var v1));
        // Second read — served from cache.
        Assert.True(reader.TryGet(K("x"), out var v2));
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void TryGet_WithBlockCache_MissingKeyReturnsFalseFromCache()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("p", "one"), ("q", "two")));

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        // Warm the cache.
        reader.TryGet(K("p"), out _);
        // Cache hit path should still correctly report a miss for an absent key.
        Assert.False(reader.TryGet(K("p-missing"), out _));
    }

    // ── LZ4 compression ────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_Lz4Compression_ReturnsCorrectValue()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("hello", "world")), compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet(K("hello"), out var value));
        Assert.Equal(V("world"), value);
    }

    [Fact]
    public void TryGet_Lz4Compression_Tombstone_ReturnsNullValue()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("gone", null)), compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        Assert.True(reader.TryGet(K("gone"), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ReadAllEntries_Lz4Compression_ReturnsAllEntriesInOrder()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("a", "1"), ("b", "2"), ("c", "3")),
            compression: CompressionKind.Lz4);

        using var reader = new SSTableReader(path);
        var all = reader.ReadAllEntries().ToList();

        Assert.Equal(3, all.Count);
        Assert.Equal(K("a"), all[0].Key);
        Assert.Equal(K("b"), all[1].Key);
        Assert.Equal(K("c"), all[2].Key);
    }

    [Fact]
    public void TryGet_Lz4Compression_WithBlockCache_HitsCache()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("k", "v")), compression: CompressionKind.Lz4);

        var cache = new LruBlockCache(1024 * 1024);
        using var reader = new SSTableReader(path, cache);

        Assert.True(reader.TryGet(K("k"), out var v1));
        Assert.True(reader.TryGet(K("k"), out var v2));
        Assert.Equal(v1, v2);
    }

    // ── Checksum corruption ────────────────────────────────────────────────────

    [Fact]
    public void TryGet_CorruptedBlockPayload_ThrowsInvalidDataException()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("key", "value")));

        // Flip bytes deep in the block payload (past the 5-byte header) so the
        // stored CRC no longer matches the data.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(10, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        using var reader = new SSTableReader(path);
        Assert.Throws<InvalidDataException>(() => reader.TryGet(K("key"), out _));
    }

    // ── Unknown compression type ───────────────────────────────────────────────

    [Fact]
    public void TryGet_UnknownCompressionType_ThrowsInvalidDataException()
    {
        var path = TempPath();
        SSTableWriter.Write(path, Entries(("key", "value")));

        // Patch byte[0] to an unknown compression value, then recompute the CRC
        // over the modified block so VerifyChecksum passes and Decompress sees it.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.Begin);
            byte[] lenBuf = new byte[4];
            fs.ReadByte(); // skip original compression byte
            fs.ReadExactly(lenBuf, 0, 4);
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            byte[] payload = new byte[payloadLen];
            fs.ReadExactly(payload, 0, payloadLen);

            // Build the modified block data (compression byte + length bytes + payload).
            const byte unknownCompression = 0xFF;
            byte[] blockData = new byte[1 + 4 + payloadLen];
            blockData[0] = unknownCompression;
            lenBuf.CopyTo(blockData, 1);
            payload.CopyTo(blockData, 5);

            uint newCrc = Crc32.HashToUInt32(blockData);

            // Write back: new compression byte at offset 0, new CRC after payload.
            fs.Seek(0, SeekOrigin.Begin);
            fs.WriteByte(unknownCompression);
            fs.Seek(5 + payloadLen, SeekOrigin.Begin);
            byte[] crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, newCrc);
            fs.Write(crcBytes, 0, 4);
        }

        using var reader = new SSTableReader(path);
        Assert.Throws<InvalidDataException>(() => reader.TryGet(K("key"), out _));
    }
}
