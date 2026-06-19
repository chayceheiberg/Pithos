namespace Pithos.Core.Core;

public class MemTable
{
    private readonly SortedDictionary<byte[], byte[]?> _data =
        new(ByteArrayComparer.Instance);

    private long _sizeBytes;

    public long SizeBytes => _sizeBytes;

    public void Put(byte[] key, byte[] value)
    {
        if (_data.TryGetValue(key, out var existing))
            _sizeBytes -= existing?.Length ?? 0;
        else
            _sizeBytes += key.Length;

        _data[key] = value;
        _sizeBytes += value.Length;
    }

    public void Delete(byte[] key)
    {
        if (_data.TryGetValue(key, out var existing))
            _sizeBytes -= key.Length + (existing?.Length ?? 0);

        _data[key] = null; // tombstone
    }

    public bool TryGet(byte[] key, out byte[]? value) =>
        _data.TryGetValue(key, out value);

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetSortedEntries() => _data;

    public void Clear()
    {
        _data.Clear();
        _sizeBytes = 0;
    }
}
