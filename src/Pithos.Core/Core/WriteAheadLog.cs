namespace Pithos.Core.Core;

public enum WalEntryType : byte { Put = 1, Delete = 2 }

public sealed class WriteAheadLog : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;

    public WriteAheadLog(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);
    }

    public void AppendPut(byte[] key, byte[] value)
    {
        _writer.Write((byte)WalEntryType.Put);
        _writer.Write(key.Length);
        _writer.Write(key);
        _writer.Write(value.Length);
        _writer.Write(value);
        _stream.Flush();
    }

    public void AppendDelete(byte[] key)
    {
        _writer.Write((byte)WalEntryType.Delete);
        _writer.Write(key.Length);
        _writer.Write(key);
        _stream.Flush();
    }

    public static IEnumerable<(WalEntryType type, byte[] key, byte[]? value)> Replay(string path)
    {
        if (!File.Exists(path)) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        while (stream.Position < stream.Length)
        {
            var type = (WalEntryType)reader.ReadByte();
            var keyLen = reader.ReadInt32();
            var key = reader.ReadBytes(keyLen);

            if (type == WalEntryType.Put)
            {
                var valLen = reader.ReadInt32();
                var value = reader.ReadBytes(valLen);
                yield return (type, key, value);
            }
            else
            {
                yield return (type, key, null);
            }
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
