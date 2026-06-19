# Pithos

A persistent, embedded key-value storage engine built on an [LSM-tree](https://en.wikipedia.org/wiki/Log-structured_merge-tree) (Log-Structured Merge-Tree) architecture. Written in C# targeting .NET 9.

---

## Architecture

Pithos follows the standard LSM-tree design: all writes go to an in-memory buffer and an append-only log, then are periodically flushed to disk as immutable sorted files. Reads check each layer in order from newest to oldest.

```
Write path:   Put/Delete → WAL (disk) → MemTable (memory) → [flush] → L0 SSTable
Read path:    MemTable → L0 SSTables → L1 SSTables → ... → Ln SSTables
Scan path:    k-way merge across MemTable + all SSTable levels → sorted, deduplicated output
```

All public operations are **thread-safe**. Concurrent reads proceed in parallel via `ReaderWriterLockSlim`; writes take an exclusive lock.

### Components

#### MemTable (`Core/MemTable.cs`)
An in-memory `SortedDictionary<byte[], byte[]?>` that holds the most recent writes. Deleted keys are represented as tombstones (`null` values). When the MemTable exceeds the configured size threshold (default **4 MB**), it is flushed to an L0 SSTable on disk.

#### Write-Ahead Log (`Core/WriteAheadLog.cs`)
An append-only binary log written to `wal.log` in the database directory. Every `Put` and `Delete` is durably fsynced here before being applied to the MemTable. On startup, the WAL is replayed to restore any unflushed writes. The log is deleted and recreated after each successful flush.

#### SSTable (`Storage/SSTableWriter.cs`, `Storage/SSTableReader.cs`)
Immutable, sorted files written when the MemTable is flushed. Each file has the following layout:

```
┌─────────────────────────────────┐
│  Data blocks (4 KB each)        │  ← key-value entries, sorted
├─────────────────────────────────┤
│  Bloom filter                   │  ← MurmurHash3, configurable FPR (default 1%)
├─────────────────────────────────┤
│  Sparse index                   │  ← first key + offset per block
├─────────────────────────────────┤
│  Footer (16 bytes)              │  ← bloomOffset (8) + indexOffset (8)
└─────────────────────────────────┘
```

Point lookups consult the bloom filter first; a definite miss skips all block I/O for that file. Range scans use the sparse index to seek directly to the first candidate block.

#### Bloom Filter (`Core/BloomFilter.cs`)
A bit-array bloom filter using double-hashing over MurmurHash3. Built per SSTable with a configurable false positive rate (default **1%**). Serialized into the SSTable file and loaded into memory on open. A definite miss means no disk reads are needed for that file.

#### Leveled Compactor (`Compaction/LeveledCompactor.cs`)
Runs a configurable leveled compaction strategy (default **7 levels**, **10× size multiplier**). When a level reaches its file-count limit, all its SSTables are merged into a single SSTable at the next level using a k-way merge (via `PriorityQueue`) that deduplicates keys, keeping the value from the newest source file.

---

## Project Structure

```
src/
└── Pithos.Core/
    ├── PithosDb.cs                  # Public API / orchestration
    ├── PithosOptions.cs             # Runtime configuration
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

The directory is created if it does not exist. On open, any unflushed WAL entries are replayed and existing SSTable files are recovered into the level structure.

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

Deletes are tombstoned — the key is logically removed and `TryGet` returns `false`. Tombstones are physically removed during compaction.

### Range Scanning

`Scan` returns all live entries within an inclusive key range, in sorted order. Either bound can be omitted for an open-ended scan.

```csharp
// Bounded scan
foreach (var (key, value) in db.Scan(from: Encoding.UTF8.GetBytes("b"),
                                       to:   Encoding.UTF8.GetBytes("d")))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(key)} = {Encoding.UTF8.GetString(value)}");
}

// From a lower bound to the end
foreach (var (key, value) in db.Scan(from: Encoding.UTF8.GetBytes("m"))) { ... }

// Full scan
foreach (var (key, value) in db.Scan()) { ... }
```

Deleted keys are excluded from scan results. The scan reflects a consistent point-in-time snapshot across the MemTable and all SSTable levels.

### Closing

`PithosDb` implements `IDisposable`. Use a `using` statement or call `Dispose()` explicitly to flush and close the WAL.

---

## Configuration

Pass a `PithosOptions` instance to tune the engine at open time. All properties have sensible defaults.

```csharp
using var db = new PithosDb("path/to/data-directory", new PithosOptions
{
    MemTableSizeThreshold      = 8 * 1024 * 1024, // 8 MB flush threshold
    BloomFilterFalsePositiveRate = 0.001,          // 0.1% false positive rate
    LevelCount                 = 5,                // 5 compaction levels
    LevelZeroFileCountLimit    = 4,                // compact L0 after 4 files
    LevelSizeMultiplier        = 10,               // each level is 10× the previous
});
```

| Option | Default | Description |
|---|---|---|
| `MemTableSizeThreshold` | 4 MB | Raw data bytes before flushing to disk |
| `BloomFilterFalsePositiveRate` | 1% | Lower = fewer disk reads, larger filter |
| `LevelCount` | 7 | Total number of compaction levels |
| `LevelZeroFileCountLimit` | 10 | L0 file count that triggers compaction |
| `LevelSizeMultiplier` | 10 | File-count limit multiplier per level |

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
