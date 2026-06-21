# Changelog

All notable changes to PithosDB are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `CompareAndSwap` and `CompareAndSwapAsync` — atomic read-modify-write with "insert if absent" semantics when `expectedValue` is `null`

---

## [1.5.0] - 2026-06-21

### Added
- `ScanPrefix` and `ScanPrefixAsync` — iterate all keys sharing a common byte prefix, with correct handling of 0xFF overflow in prefix upper-bound computation
- `DeleteRange` and `DeleteRangeAsync` — delete all live keys in an inclusive byte range in a single atomic `WriteBatch`
- `ApproximateCount` — fast block-level key count estimate for a range; exact for MemTable entries, one unit per overlapping SSTable block for on-disk entries
- `WalSyncMode` enum with three strategies: `Full` (flush after every write), `Periodic` (background timer flush), and `None` (OS-managed); configurable via `PithosOptions`
- Background compaction thread — SSTable merging now runs on a dedicated background thread, keeping the write path latency low
- `docs/architecture.md` and `docs/storage-format.md`

---

## [1.4.0] - 2026-06-20

### Added
- `PutAsync`, `GetAsync`, `DeleteAsync`, `WriteAsync`, `ScanAsync` — full async API via `Task.Run` wrappers; compatible with ASP.NET and other async-first hosts
- `PithosDb.OpenInMemory()` and `PithosOptions.InMemory` — in-memory mode with no WAL, no SSTable flushes, and no disk I/O; useful for testing and ephemeral workloads
- `pithosdb` dotnet global tool — install and run the interactive shell from anywhere with `dotnet tool install`
- GitHub Actions CI/CD pipeline with build, test, and code coverage steps
- Codecov integration with README coverage badge

### Fixed
- `CompactIfNeeded` loop now correctly drains all pending compaction levels in a single pass

---

## [1.3.0] - 2026-06-20

### Added
- Per-entry TTL support (`PithosOptions.EnableTtl`) — expired entries are hidden at read time and physically removed during compaction
- `Put(key, value, TimeSpan ttl)` and TTL-aware `WriteBatch` entries
- `ICompactionFilter` — pluggable callback invoked at read time and during compaction to filter out entries by key or value
- `ValueCodec` — internal encoding layer that prepends a flag byte and optional 8-byte expiry timestamp to stored values
- Interactive shell with `scan`, `get`, `put`, `delete`, and `exit` commands; ASCII jar banner on startup
- `Manifest` file for durable SSTable level tracking across restarts
- `Scan(from, to)` range iteration on `PithosDb`

---

## [1.2.0] - 2026-06-19

### Added
- LZ4 block compression for SSTables (`PithosOptions.Compression = CompressionKind.Lz4`); blocks are transparently decompressed on read
- S3-FIFO block cache (`PithosOptions.BlockCacheKind = BlockCacheKind.S3Fifo`) — scan-resistant eviction policy based on the SOSP 2023 paper
- CRC32 checksums on individual WAL `Put` and `Delete` records, in addition to the existing batch-level checksum; partial-write truncation stops replay at the first corrupt record

### Changed
- `BlockCache` renamed to `LruBlockCache` to reflect that a second policy now exists

---

## [1.1.0] - 2026-06-19

### Added
- `WriteBatch` — group multiple puts and deletes into a single atomic operation; the entire batch is written to the WAL as one CRC32-guarded record before any MemTable mutation occurs
- CRC32 block checksums in SSTable data blocks; a checksum mismatch on read throws `InvalidDataException`

---

## [1.0.0] - 2026-06-19

### Added
- NuGet packaging with metadata, LICENSE, and `nupkg` output
- `PithosOptions` — runtime configuration for MemTable flush threshold, bloom filter false-positive rate, level count, level-size multiplier, block cache size and kind, compression, TTL, and compaction filter
- `ReaderWriterLockSlim` for thread safety — concurrent reads proceed in parallel while writes take an exclusive lock
- LRU block cache (`LruBlockCache`) — SSTable blocks are kept in memory after the first read to avoid repeated disk I/O on hot data
- `SSTableReader` instance cache — file handles are kept open and reused across lookups, eliminating per-read `open`/`close` overhead
- `RandomAccess`-based positional block reads — thread-safe I/O without advancing a shared stream position
- BenchmarkDotNet performance suite
- XML documentation on all public types and members
- Unit tests for `MemTable`, `BloomFilter`, `SSTableReader`, and `PithosDb` (31 tests)

### Fixed
- Tombstone reads, SSTable recovery, bloom filter integration, and compaction merge correctness

---

## [0.1.0] - 2026-06-19

### Added
- Initial LSM-tree storage engine: `MemTable` (sorted skip-list), `WriteAheadLog`, `SSTableWriter`, `SSTableReader`, `BloomFilter`, and leveled compaction via `LeveledCompactor`
- `Put`, `TryGet`, and `Delete` on `PithosDb`
- WAL replay for crash recovery
- SSTable sparse index and bloom filter loaded into memory on open
