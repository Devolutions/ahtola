---
name: storage-format-port
description: How to keep Ahtola's on-disk SQLite file format and WAL framing byte-compatible with SQLite/Turso. Use this when touching anything in `Ahtola.Core/Storage` — pager, b-tree, WAL, page allocator, overflow, varints, headers.
---

# Storage format porting

`Ahtola.Core/Storage` implements the on-disk SQLite file format and the WAL.
This is where silent corruption bugs live: a wrong offset, a flipped flag, or
an off-by-one in varint decoding produces files that *read* fine in our engine
but are unreadable by SQLite/Turso (or vice versa). **Byte-exactness with
SQLite/Turso is the contract.** See `docs/wal-interoperability-contract.md`
for the WAL interop target.

## Where things live

| Concern | Ahtola (C#) | Turso (Rust) |
| --- | --- | --- |
| Database header | `SqliteDatabaseHeader.cs` | `turso-src/core/storage/sqlite3_ondisk/header.rs` (and `mod.rs`) |
| Pager / page store | `SqlitePager.cs`, `SqlitePageStore.cs`, `SqlitePagerReadCache.cs` | `turso-src/core/storage/pager.rs` |
| Page allocator / freelist | `SqlitePageAllocator.cs`, `SqliteFreelist.cs` | `turso-src/core/storage/` allocator/freelist |
| B-tree pages (table/index, interior/leaf) | `SqliteTableInteriorPage.cs`, `SqliteTableLeafPage*.cs`, `SqliteIndexInteriorPage.cs`, `SqliteIndexLeaf*.cs`, `SqliteBtree*.cs` | `turso-src/core/storage/sqlite3_ondisk/btree.rs` |
| Cell pointer array / payload layout / overflow | `SqliteCellPointerArray.cs`, `SqlitePayloadLayout.cs`, `SqliteOverflowChainReader.cs`, `SqliteOverflowPageView.cs` | `turso-src/core/storage/sqlite3_ondisk/` |
| Varint | `SqliteVarint.cs` | `turso-src/core/storage/sqlite3_ondisk/varint.rs` |
| Record codec | `SqliteRecordCodec.cs` | `turso-src/core/storage/sqlite3ondisk` record serial |
| WAL + WAL index + locks | `SqliteWal.cs`, `SqliteWalIndex.cs`, `SqliteWalByteRangeLock.cs`, `SqliteWalSharedMemoryLocks.cs`, `SqliteWalReadSnapshotCoordinator.cs`, `SqliteWalWriterCheckpointCoordinator.cs` | `turso-src/core/storage/wal.rs`, `turso-src/core/wal/` |
| Page size / encryption | `SqlitePageSize.cs`, `AhtolaPageEncryption.cs`, `AhtolaEncryptionFileSystem.cs` | `turso-src/core/storage/` |

## Porting rules

- **Match offsets, sizes, and flag bits exactly.** The SQLite file format
  header has fixed offsets (page size at offset 16, text encoding at 56, …).
  Cross-check every field against `turso-src/core/storage/sqlite3_ondisk/` and
  the SQLite file-format docs referenced in the WAL contract.
- **Varint encoding is Huffman-ish big-endian, high-bit continuation.** Do not
  "simplify" `SqliteVarint`. Mirror the upstream decode/encode edge cases
  (1..9 byte forms, the 9-byte form's full 64-bit payload).
- **WAL framing is the interop contract.** Frame headers (page number, commit
  marker, checksums, salt-1/salt-2) must match byte-for-byte; the checksum
  algorithm and salt handling are specified in
  `docs/wal-interoperability-contract.md`. A WAL file Ahtola writes must be
  readable by the Turso Rust engine and vice versa.
- **Locks/shmem are the one allowed P/Invoke.** Byte-range locks and the
  shared-memory mapping in `SqliteWalByteRangeLock.cs` /
  `PhysicalSqliteWalSharedMemoryMapping.cs` are the intentional OS interop in
  `Storage` (see the `pure-managed-closure` skill). Do not spread P/Invoke
  beyond this.
- **In-memory vs physical**: `InMemoryFileSystem.cs` / `PhysicalFileSystem.cs`
  implement `IFileSystem`. New storage code should go through `IFileSystem`,
  not call OS APIs directly.

## Verification

- Add/extend a conformance case that round-trips the affected structure
  through disk and re-reads it.
- For WAL changes, cross-check against the Turso Rust engine per the WAL
  contract — do not just "our engine reads what we wrote."
