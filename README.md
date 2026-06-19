# Pithos

A persistent, embedded key-value storage engine built on an [LSM-tree](https://en.wikipedia.org/wiki/Log-structured_merge-tree) (Log-Structured Merge-Tree) architecture. Written in C# targeting .NET 9.

---

## Architecture

Pithos follows the standard LSM-tree design: all writes go to an in-memory buffer and an append-only log, then are periodically flushed to disk as immutable sorted files. Reads check each layer in order from newest to oldest.

```
Write path:   Put/Delete → WAL (disk) → MemTable (memory) → [flush] → L0 SSTable
Read path:    MemTable → L0 SSTables → L1 SSTables → ... → Ln SSTables
```

### Components

#### MemTable (`Core/MemTable.cs`)
An in-memory `SortedDictionary<byte[], byte[]?>` that holds the most recent writes. Deleted keys are represented as tombstones (`null` values). When the MemTable exceeds **4 MB**, it is flushed to an SSTable on disk.

#### Write-Ahead Log (`Core/WriteAheadLog.cs`)
An append-only binary log written to `wal.log` in the database directory. Every `Put` and `Delete` is durably recorded here before being applied to the MemTable. On startup, the WAL is replayed to restore any unflushed writes.

#### SSTable (`Storage/SSTableWriter.cs`, `Storage/SSTableReader.cs`)
Immutable, sorted files written when the MemTable is flushed. Each file has three sections:

```
┌─────────────────────────────────┐
│  Data blocks (4 KB each)        │  ← key-value entries, sorted
├─────────────────────────────────┤
│  Bloom filter                   │  ← MurmurHash3, ~1% false positive rate
├─────────────────────────────────┤
│  Sparse index                   │  ← first key + offset per block
├─────────────────────────────────┤
│  Footer (16 bytes)              │  ← bloomOffset (8) + indexOffset (8)
└─────────────────────────────────┘
```

Point lookups consult the bloom filter first; a definite miss skips all disk I/O for that file.

#### Bloom Filter (`Core/BloomFilter.cs`)
A standard bit-array bloom filter using double-hashing over MurmurHash3. Built with a target **1% false positive rate** at the time each SSTable is written. Serialized into the SSTable file and loaded on open.

#### Leveled Compactor (`Compaction/LeveledCompactor.cs`)
Runs a **7-level** leveled compaction strategy. Level size limits grow by 10× per level (L0 = 10 files, L1 = 100, ...). When a level is full, all its SSTables are merged into a single SSTable at the next level using a k-way merge (via `PriorityQueue`) that deduplicates keys, keeping the value from the newest source file.

---

## Project Structure

```
src/
└── Pithos.Core/
    ├── PithosDb.cs                  # Public API / orchestration
    ├── Core/
    │   ├── MemTable.cs
    │   ├── WriteAheadLog.cs
    │   ├── BloomFilter.cs
    │   └── ByteArrayComparer.cs
    ├── Storage/
    │   ├── SSTableWriter.cs
    │   └── SSTableReader.cs
    └── Compaction/
        └── LeveledCompactor.cs
tests/
└── Pithos.Tests/                    # xUnit test project
```

---

## Usage

### Opening a Database

```csharp
using Pithos.Core;

using var db = new PithosDb("path/to/data-directory");
```

The directory is created if it does not exist. On open, any unflushed WAL entries are replayed and existing SSTable files are recovered.

### Writing

```csharp
byte[] key   = Encoding.UTF8.GetBytes("hello");
byte[] value = Encoding.UTF8.GetBytes("world");

db.Put(key, value);
```

### Reading

```csharp
if (db.TryGet(key, out byte[]? value))
{
    Console.WriteLine(Encoding.UTF8.GetString(value!));
}
else
{
    Console.WriteLine("Key not found.");
}
```

### Deleting

```csharp
db.Delete(key);
```

Deletes are tombstoned — the key is logically removed and will return `false` from `TryGet`. Tombstones are physically removed during compaction.

### Closing

`PithosDb` implements `IDisposable`. Use a `using` statement or call `Dispose()` explicitly to flush and close the WAL.

---

## Building

```bash
dotnet build
```

## Running Tests

```bash
dotnet test
```

Requires .NET 9 SDK.
