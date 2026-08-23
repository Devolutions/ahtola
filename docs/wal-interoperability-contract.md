# WAL interoperability contract

**Goal:** multi-process WAL interoperability with the original **Turso Rust
engine** (and, by the same on-disk protocol, stock SQLite). Ahtola should be able
to share a physical database with `tursodb` / the Turso core under the same WAL,
`-shm`, lock, and handoff rules those engines already use — not invent a
managed-only WAL dialect.

**Status today: Stages 1–6 core attached, including live multi-engine WAL.**
Physical pagers publish a real WAL-index, pin read marks, checkpoint under
`WAL_CKPT_LOCK`, use SQLite busy backoff, recover with `iChange` bumps, and hold
a Stage 6 SQLite-compatible **main-file lock**. Rollback-journal pagers now use
SHARED, RESERVED, PENDING, and EXCLUSIVE at SQLite transaction boundaries;
WAL pagers retain their lifetime SHARED lock and continue to coordinate writers
and checkpoints through `-shm`. Managed and stock SQLite can share one live WAL
or DELETE-mode database without `IOERR`, and long-lived managed connections
refresh their committed view from peer changes on new statements. Remaining
polish is last-connection `-shm` unlink/heap fallback and expanded Turso binary
differential stress. An explicit
`Foreign Read Only=True` connection may still read without main-file locks
(§1.9).

Nothing here describes behavior that is unimplemented, and nothing here authorizes
relaxing a guard ahead of the stage that replaces it.

Source of truth (Ahtola):

- `src/Ahtola.Core/Storage/SqliteManagedFileOwnership.cs`
- `src/Ahtola.Core/Storage/SqliteWalSharedMemoryLocks.cs`
- `src/Ahtola.Core/Storage/SqlitePagerLockManager.cs`
- `src/Ahtola.Core/Storage/SqlitePager.cs`

External reference (interop target): Turso’s Rust pager/WAL implementation in the
upstream Turso / `tursodb` tree (SQLite-compatible WAL-index and lock bytes).

## 1. The current contract

### 1.1 Main-file lock protocol

`SqliteManagedFileOwnership`, brokered per canonical path by
`SqliteManagedFileOwnershipRegistry`, is now a lock broker rather than an
exclusive ownership boundary.

| Property | Current behavior |
| --- | --- |
| SQLite ranges | `PENDING_BYTE = 0x4000_0000`, `RESERVED_BYTE = PENDING_BYTE + 1`, and the 510-byte SHARED range `[PENDING_BYTE + 2, PENDING_BYTE + 512)` |
| Lock kinds | Shared and exclusive byte-range locks via `SqliteWalByteRangeLock` (Windows `LockFileEx`; Linux OFD `fcntl`; macOS POSIX `fcntl`) |
| Platform gate | Windows; 64-bit Linux with a 32-byte `struct flock` (OFD); macOS with Darwin `struct flock` (`F_SETLK`). Every other platform throws `PlatformNotSupportedException` at open |
| Applies to | `PhysicalFileSystem` only, after unwrapping `AhtolaEncryptionFileSystem`. In-memory and custom file systems receive no cross-process boundary at all |
| Broker lifetime | One canonical-path broker aggregates every managed pager onto one stable OS handle. Logical leases are reference-counted independently |
| WAL lifetime | A WAL/MVCC pager retains SHARED for its lifetime. Its writer/checkpoint roles remain on the SQLite `-shm` lock bytes |
| DELETE lifetime | Open and ordinary reads hold SHARED only for the operation or read transaction. A write transaction holds SHARED + RESERVED from begin through completion, adds PENDING + EXCLUSIVE for commit, then releases everything |
| Registry key | Canonical full path with final symlink resolution where available; case-folded on Windows |
| Contention | SQLite busy backoff runs within the configured timeout. Initial open SHARED contention is wrapped as `SqlitePagerClientOwnershipException`; transaction/read contention is `SqlitePagerBusyException` with the requested operation |
| Create collision | `createNew: true` for a path already owned in this process throws `IOException` |
| Read-only opens | A read-only handle can take SHARED and query a conflicting RESERVED lock. A later managed writer upgrades the broker carrier to a writable handle without dropping aggregate SHARED |

Consequences callers may rely on:

- SHARED acquisition briefly read-locks PENDING, read-locks the complete SHARED
  range, then releases PENDING. A live PENDING or EXCLUSIVE owner therefore blocks
  new readers.
- RESERVED coexists with existing and new readers but excludes a second writer.
  Commit takes PENDING before waiting for existing readers to drain, so it prevents
  reader starvation, then converts the SHARED range to EXCLUSIVE.
- A failed EXCLUSIVE upgrade retains PENDING and the transaction remains active.
  Retrying commit can complete after readers drain; rollback, transaction disposal,
  or pager disposal releases RESERVED and PENDING.
- Hot-journal recovery first distinguishes a live RESERVED owner from an abandoned
  journal, then upgrades SHARED directly through PENDING to EXCLUSIVE before
  recovery. It never recovers another process's live journal.
- Linux uses open-file-description locks so unrelated descriptor closes cannot
  drop main-file locks. macOS uses SQLite-compatible process-scoped POSIX locks;
  the broker defers closing pager and superseded lock descriptors while any
  logical main-file lock remains.

### 1.2 `-shm` is a byte-lock carrier, not a WAL-index

`SqliteWalSharedMemoryLocks` places write/ckpt/recovery/reader byte-range locks on
`<database>-shm`. Separately, `PhysicalSqliteWalSharedMemoryMapping` maps the file
as a real SQLite WAL-index and holds the DMS shared lock at byte 128 for the
mapping lifetime.

Managed roles are placed inside SQLite's reserved lock area, which begins at shm
offset 120 (`WIN_SHM_BASE` / `SQLITE_SHM_BASE`):

| shm byte | SQLite role | Managed use |
| ---: | --- | --- |
| 120 | `WAL_WRITE_LOCK` | Writer takes exclusive `[120, 1)` |
| 121 | `WAL_CKPT_LOCK` | Never taken on its own |
| 122 | `WAL_RECOVER_LOCK` | Writable open and WAL tail recovery take exclusive `[122, 1)` |
| 123–127 | `WAL_READ_LOCK(0..4)` | Reader takes the first free byte (**shared** `LockFileEx`/OFD) |
| 120–127 | — | Checkpoint takes exclusive `[120, 8)` |
| 128 | DMS (`WIN_SHM_DMS` / unix dead-man switch) | Held **shared** for the lifetime of a mapped `-shm` so peers do not truncate a live index |

Additional current behavior:

- One reader byte is held per coordinator (one per database path per process) and
  reference-counted across every managed reader in that process.
- The carrier handle is retained while any range is held and closed once the last
  range is released, because closing a descriptor would drop process-owned POSIX
  record locks.
- Reader carriers requested by a read-only pager open with `FileMode.Open`. A
  missing `-shm` therefore fails with `InvalidOperationException` instead of being
  created, so a read-only open never mutates storage. Writer and checkpoint
  carriers use `FileMode.OpenOrCreate`, and so does a reader lock taken by a
  read-write pager: like a native read-write connection, such a pager recreates
  the carrier on demand after a stock SQLite client removed it on clean close.
- Byte-range locking is enabled on Windows, 64-bit Linux, and macOS; anything
  else throws `PlatformNotSupportedException` rather than falling back to
  process-local locks.
- Contention follows SQLite's busy-backoff schedule until the pager busy timeout
  expires and is then raised as `SqlitePagerBusyException` carrying the requested
  `SqlitePagerLockOperation`.

### 1.3 WAL compatibility boundary

1. **A real WAL-index exists.** Physical WAL pagers map `-shm`, publish dual
   `WalIndexHdr` copies plus page/hash tables, and detect peer `iChange`,
   `mxFrame`, salt, and WAL-stamp changes.
2. **Readers use SQLite read marks.** Managed WAL snapshots take shared
   `WAL_READ_LOCK(i)` leases and pin a validated committed frame boundary.
3. **Writers and checkpoints use SQLite roles.** Writers take `WAL_WRITE_LOCK`;
   checkpoints take `WAL_CKPT_LOCK`, honor held read marks, and publish
   `nBackfill`/`nBackfillAttempted`.
4. **Main-file locking is mode-specific.** WAL retains SHARED and leaves write
   serialization to `-shm`; DELETE uses the rollback-journal protocol in §1.1.
   This change does not weaken WAL mode.
5. **Process-local coordination remains intentional.** `SqlitePagerLockManager`
   aggregates managed readers/writers and prevents same-process lock anomalies,
   while the main-file and `-shm` byte locks are the cross-process authority.
6. **Remaining WAL lifecycle gap.** Last-connection `-shm` unlink and the
   exclusive-locking-mode heap WAL-index fallback are not yet implemented.

#### 1.3.1 `PRAGMA synchronous` durability

The connection-local synchronous mode is passed into every managed commit and
checkpoint rather than being pager-global. This keeps pooled connections with
different settings independent while preserving the SQLite default of `FULL`.

| Mode | WAL commit | WAL checkpoint | DELETE journal |
| --- | --- | --- | --- |
| `OFF` | No WAL flush | Backfill may update the main file, but no WAL/main-file flush is issued and the recovery WAL is retained rather than reset | No journal, database, or invalidation flush |
| `NORMAL` | Commit frames are not flushed | Flush WAL before backfill, flush the main file, then durably reset the WAL | Flush the completed hot journal once before database writes, then flush the database |
| `FULL` | Flush WAL before reporting commit | Same checkpoint barriers as `NORMAL` | Flush journal payload before publishing its hot header, flush the completed header, flush the database, then flush journal invalidation |
| `EXTRA` | Same as `FULL` | Same as `FULL` | Same ordered barriers as `FULL`; the journal is durably invalidated before deletion, so recovery never depends on directory-entry persistence |

Checkpoint barrier failures propagate before backfill publication or WAL reset.
Commit barrier failures fault the pager and are never returned as successful
commits. Browser OPFS uses the same policy: `BrowserMirroredFileSystem` records
only the requested flush operations and replays them in order at the asynchronous
statement boundary.

### 1.4 MVCC mode (Phase 2)

`PRAGMA journal_mode=mvcc` enables Turso-aligned main-memory MVCC on the
connection's main database (including in-memory databases). The engine attaches
an in-process `MvStore` (`src/Ahtola.Core/Mvcc/`) with a Hekaton-style logical
clock and write-set conflict detection. `BEGIN CONCURRENT` is accepted only when
MVCC is enabled; nested `BEGIN` still fails with
`cannot start a transaction within a transaction`.

Phase 2 scope and limits:

- Concurrent transactions skip the classic single-writer reservation and use
  MVCC transaction IDs + first-committer-wins write-write conflicts.
- Row identities are either integer rowids or canonical SQLite primary-key
  records. The record form supports composite `WITHOUT ROWID` primary keys and
  preserves built-in BINARY/NOCASE/RTRIM equality plus numeric equivalence.
- Table scans and materialized managed-index scans merge the transaction's
  version-store rows with its catalog snapshot. A base row shadowed by a
  visible update/delete is never emitted; an own or committed overlay insert
  appears once in key order.
- `WITHOUT ROWID` INSERT/UPDATE/DELETE, trigger bodies, and foreign-key
  cascades are routed through the typed-key overlay. They continue to expose
  no hidden rowid and do not affect update hooks or `last_insert_rowid()`.
- Concurrent DDL is admitted only through a fail-closed schema gate: no other
  MVCC snapshot may be active. The catalog, `sqlite_schema` rows, and schema
  cookie are persisted before the store publishes its next schema generation.
  A conflicting DDL attempt reports busy rather than running against stale
  object bindings.
- File-backed MVCC keeps a WAL open underneath for page durability (matching
  Turso). Enabling MVCC persists SQLite header read/write version **255** and
  opens a durable logical log (`<db>-log`, Turso LML2/MVTX framing). For an
  encrypted database, transaction payloads use Turso's chunked AES-GCM logical-
  log layout; salt, payload length, operation count, commit timestamp, and chunk
  index are authenticated while framing metadata remains visible. New logs
  use V4 typed-key frames and include their logical object name so recovery
  restores the object-id mapping. V3 rowid-only logs are upgraded only by an
  exclusive materializing checkpoint; a cold V3 log whose table identities
  cannot be proven fails closed instead of losing frames.
- A logical-log write or requested durability-barrier failure after frame construction is an
  indeterminate commit. The live MVCC store rejects further transactions and
  callers must dispose and reopen; recovery then accepts a fully framed commit
  or discards a torn tail before a later append.
- Checkpointing folds typed table rows into the catalog and persists the
  resulting secondary indexes through the normal pager/WAL write path before
  truncating or upgrading the logical log.
- `PRAGMA temp.journal_mode=mvcc` is ignored (temp stays `wal`), matching Turso.
- MVCC is process-local and does not replace Stage 6 WAL interop. Cross-process
  MVCC is unsupported (same as Turso v0.7.2).

### 1.5 Busy semantics today

- Every role converts external contention into `SqlitePagerBusyException` whose
  `Operation` is the requested role and whose `Timeout` is the configured pager
  busy timeout. Nested busy failures are wrapped rather than replaced.
- Recovery-lock contention is reported as `SqlitePagerLockOperation.Writer`,
  because recovery is only attempted on behalf of a writable open or a writer.
- Main-file and `-shm` retry paths use `SqliteBusyBackoff`, matching SQLite's
  default increasing delays within the configured timeout.
- Initial main-file SHARED contention is intentionally wrapped in
  `SqlitePagerClientOwnershipException` so callers can distinguish "the pager
  could not open" from contention at a later reader, writer, or checkpoint
  boundary.
- A DELETE commit that cannot drain readers reports Writer/Busy without faulting
  the pager or ending the transaction. It retains PENDING for a retry and releases
  it on rollback or disposal.

### 1.6 SQL transaction modes and the write reservation

`BEGIN DEFERRED`, `BEGIN IMMEDIATE` and `BEGIN EXCLUSIVE` are honored by a
process-local write reservation (`EmbeddedTransactionLock`) layered *above* the
pager, one per database identity: per canonical path for file databases, per
instance for in-memory databases, which is exactly what connections sharing a
managed in-memory database share.

- DEFERRED takes the reservation at its first write, so busy surfaces there.
- IMMEDIATE takes it at `BEGIN`, so a losing writer learns it lost before doing
  any work. This is the whole reason applications choose the mode.
- EXCLUSIVE takes it at `BEGIN` and additionally excludes other connections'
  reads, but only when the database's journal mode is a rollback journal. In WAL
  mode SQLite's EXCLUSIVE does not block readers, and neither does this.
- Autocommit statements do not take the reservation - they are already serialized
  by the owning database - but a write does fail busy when another connection is
  holding one, which is what SQLite reports.
- Contention throws `EmbeddedBusyException` ("database is locked"), surfaced as
  `SqliteException` with `SqliteErrorCode` 5. There is no busy timeout, matching
  SQLite's default `busy_timeout=0`.

This SQL-layer reservation remains independent of the cross-process pager
protocol. The pager holds its process-local writer lease and, in DELETE mode,
main-file SHARED + RESERVED from pager transaction begin through commit,
rollback, or disposal. PENDING + EXCLUSIVE are added only at commit. WAL mode
continues to use its existing `-shm` writer protocol.

The resulting classic-model contract is:

- One active writer per database identity. Explicit-transaction writers queue
  FIFO so a contended reservation rotates across connections. Autocommit
  statements barge instead: each statement is an implicit transaction, and
  queueing them would recreate the EF migrations-lock convoy by handing the
  reservation to a waiting loser before the current owner's next statement.
- Reads are snapshot-isolated against that writer. A managed read transaction
  pins its WAL frame/page snapshot and does not observe later commits until it
  ends; autocommit statements may capture a newer snapshot at the next statement
  boundary. There is no timestamp ordering, multi-writer conflict detection, or
  Turso MVCC row-version lifecycle.
- `VdbeTransactionContext` is not this transaction manager. It snapshots the
  interpreter's scalar registers for VDBE BEGIN/SAVEPOINT/COMMIT/ROLLBACK
  execution and never interacts with the pager, WAL, or durable store.
- `SqlTransactionControl` is not the parser or transaction manager either. It
  is a lexical scanner used by the ADO.NET layers to recognize COMMIT/END/
  ROLLBACK boundaries in raw SQL for connection bookkeeping.

Not covered: two connections that both have live snapshots still reject the
loser's commit with the pre-existing catalog-version conflict rather than a lock
error, because a managed connection's catalog snapshot is fixed for its lifetime
and is only refreshed on pooling reset.

### 1.7 Cache invalidation today

- `SqlitePagerLockManager.Generation` is a monotonic counter bumped by writer and
  checkpoint leases through `PublishStorageChange`. It is purely process-local.
- The pager's bounded clean-main-store cache tags every image with that committed
  view generation. A mismatched lookup evicts the stale image instead of exposing
  it. Read transactions capture their generation, consult their copied WAL overlay
  first, and may use only a matching clean-main-store image; they never adopt an
  image cached for a later writer or checkpoint view.
- Because that counter cannot observe other processes, physical pagers
  (`UsesFileBackedWalLocks`) skip the generation fast path and re-validate on every
  `SynchronizeCommittedView`.
- Validation on synchronize covers a hot rollback journal, a changed page size, a
  changed read/write file-format version, a changed or missing WAL incarnation
  (`ValidateWalIncarnation` compares WAL salts), and — for non-file-backed lock
  managers — any uncommitted or invalid WAL tail.
- `ValidateWalHasNotChanged` re-verifies the WAL immediately before checkpoint
  installation.
- Any of these failures transitions the pager to `SqlitePagerState.Faulted`. The
  pager never silently re-reads a database that changed underneath it.
- `SqlitePagerLockManager.WalPublicationBoundary` is a second process-local signal,
  used only by `AsyncSqlitePager`. A commit-bearing frame reaches the WAL file
  before its durable flush returns, so the committing writer lowers the boundary to
  the last published frame before appending and raises it again only after the
  flush, the post-flush scan, and the committed-view publication have all
  succeeded. Reader scans stop at the boundary, so a concurrent async reader on the
  same database never adopts a transaction that is still in flight or that failed
  to become durable. The boundary constrains visibility only; it never changes WAL
  bytes, and it resets to unbounded on create, open, checkpoint, WAL tail recovery,
  and whenever a new writer takes the writer lock, so a peer process — or a
  reopened pager — still derives visibility from the file exactly as
  §1.8 describes.

### 1.8 Recovery and handoff today

- **Writable open** holds the write byte and the recovery byte, recovers a hot
  rollback journal, scans the WAL, truncates it to the last committed frame, and
  requires the post-repair scan to match the authenticated pre-repair scan exactly.
- **Read-only open** takes only a reader byte, never repairs anything, refuses to
  create a missing `-shm`, and refuses to establish a snapshot that would require
  WAL repair.
- **WAL coexistence is live.** Managed and stock SQLite readers/writers coordinate
  through the mapped WAL-index, `-shm` locks, and the lifetime main-file SHARED
  lock.
- **DELETE coexistence is live.** Idle pagers hold no main-file lock; operations
  acquire SHARED, write transactions reserve at begin, and commits publish only
  while PENDING + EXCLUSIVE are held.
- **Long-lived DELETE pagers rescan under SHARED.** A managed pager therefore
  observes peer-process main-file commits at the next operation instead of
  trusting only the process-local generation.

### 1.9 Foreign read-only opens

A managed connection opened with `Foreign Read Only=True` reads a database that is
*not* managed-owned — typically one created and still owned by an ordinary SQLite
client (for example `winget`'s `index.db`). It is an explicit opt-out of the
ownership boundary, not a relaxation of it: the managed engine acts as a
well-behaved *read-only SQLite client* that never claims the file.

| Property | Behavior |
| --- | --- |
| Ownership | `SqliteManagedFileOwnership` is never acquired; a foreign open does not block the owner, and a live owner does not block the foreign open |
| Lock manager | A coordinator-less process-local `SqlitePagerLockManager` keyed with a `foreign` prefix, so byte locks on `-shm` are never placed |
| `-shm` | Never required, never created, never probed |
| Writes | Rejected the same way as any read-only connection |
| Journal modes | `DELETE`/`TRUNCATE`/`PERSIST` and `WAL` databases open cleanly, with or without live companion files |
| Hot files | An unreadable hot rollback journal faults the pager (fails closed); a missing or mid-incarnation `-wal` is adopted, never repaired |

**Snapshot and freshness.** The foreign pager scans the WAL (if any) on open and
captures a *view token*: the header change counter, committed WAL frame count and
salts, plus the file-system write stamps (length and last-write time) of the main
file and `-wal`. Every autocommit statement re-captures the token; if it changed,
the heap-materialized catalog is reloaded from the store, so a foreign reader
observes commits the owner makes between statements. An explicit `BEGIN`
transaction pins the snapshot for its duration — commits the owner makes
mid-transaction are invisible until the transaction ends, matching SQLite's
read-transaction semantics.

**Constraints enforced at open.** Foreign read-only requires `Local
Provider=Managed`, `Mode=ReadOnly`, a file-backed physical data source, and
`Pooling=False`. Shared
cache, encryption, and custom file systems are rejected, because the token's
write stamps and the lock bypass are only meaningful for a real on-disk file.
Pooling is refused because a pooled connection would hand back a live catalog
whose view token can no longer be refreshed reliably.

**What this is not.** Foreign read-only is not interoperability in the Stage 1–6
sense: the managed engine still does not place SQLite-compatible locks, so the
owner is unaware of the foreign reader. The owner may checkpoint, reset, or
delete the WAL at any time; the foreign reader copes by re-scanning and
re-adopting on the next statement boundary, and a statement racing the owner's
mutation may observe the new incarnation rather than failing. That tradeoff is
acceptable for a read-only guest, and it is strictly safer than claiming shared
state that does not exist.

## 2. Required staged transition to Turso / SQLite multi-process WAL

Each stage is a prerequisite for the next. Stages target the same WAL-index
layout, lock bytes, and handoff rules Turso’s Rust engine and stock SQLite
already share. No stage may ship with the ownership lock relaxed until Stage 6.

### Stage 1 — WAL-index format and shared mapping

Implement the `-shm` layout SQLite defines:

- Two `WalIndexHdr` copies, 48 bytes each, at offsets 0 and 48: `iVersion`
  (`3007000`), padding, `iChange`, `isInit`, `bigEndCksum`, `szPage` (64 KiB
  encoded as `1`), `mxFrame`, `nPage`, `aFrameCksum[2]`, `aSalt[2]`, `aCksum[2]`.
- `WalCkptInfo` at offset 96: `nBackfill`, `aReadMark[5]`, `aLock[8]` (bytes
  120–127), `nBackfillAttempted`, one reserved word. Header region total: 136
  bytes.
- 32 KiB wal-index pages: 4096 `u32` frame slots (4062 on the first page, which
  also carries the 136-byte header) followed by 8192 `u16` hash slots.

This requires a real shared-memory capability on `IFileSystem`
(`mmap`/`MapViewOfFile`), because byte-range locks alone cannot publish state.

*Exit criteria:* managed reads and validates an index written by ordinary SQLite,
including both header copies and their checksums, and its frame lookups agree with
its own independent WAL scan across a corpus of SQLite-produced databases.

#### Stage 1 foundation currently present

`SqliteWalIndexHeader`, `SqliteWalIndexCheckpointInfo`, and
`SqliteWalIndexHeaderRegion` validate a copied `-shm` header region without
attaching it to a pager. They enforce SQLite's native-endian fields, the
big-endian WAL salt bytes, both 48-byte headers and their native checksums, the
136-byte header layout, and the committed-frame bound on backfill accounting.
`SqliteWalIndexLayout` fixes the first- and later-block page-number/hash offsets
for the later lookup implementation.

The header-region parser requires both checksum-valid copies to be identical. A
difference is rejected as a possibly in-progress dual-header publication, stale
mapping, or corruption. This is intentionally stricter than the eventual live
reader, which must use SQLite's mapped-memory barrier and retry protocol before
selecting a stable header. A parser never exposes a candidate `mxFrame` when
those guarantees are absent.

`PhysicalFileSystem` implements `ISqliteWalSharedMemoryFileSystem` with a
file-backed `MapViewOfFile` mapping on Windows and `mmap(MAP_SHARED)` on 64-bit
Linux. The mapping validates ranges, grows only from a writable mapping, rejects
read-only writes, releases native resources deterministically, and supplies a
full memory barrier for header-publication ordering. It is an optional primitive:
no pager reads, maps, writes, or publishes a WAL-index through it. In particular,
the `-shm` file remains a zero-length lock carrier during managed database
operation and the 512-byte Stage 0 main-file ownership lock remains unchanged.

`SqliteWalIndexSharedMemory` is a detached Stage 1 access component over an
explicit `ISqliteWalSharedMemoryMapping`. It reads header copy 0, executes the
shared-memory barrier, then reads copy 1; torn, malformed, or persistently
changing pairs are retried a bounded number of times and then rejected. Its
publisher validates the requested header against the independently scanned WAL,
then writes copy 1, executes the barrier, and writes copy 0, matching
SQLite's `walIndexTryHdr` and `walIndexWriteHdr` ordering. Lookups use the
native-endian `aHash`/`aPgno` tables, reject out-of-block hash references,
validate salts, page size, checksum byte order, committed boundary, database
size, final-frame checksum, and the selected WAL frame, and confirm the header
did not change before returning. It deliberately owns only an in-process gate:
callers must supply any future SQLite role lock.

This component is not reachable from a pager, lock coordinator, or normal
managed database execution. It never creates or writes a live WAL-index for
managed pager activity, does not initialize or recover an index, and does not
modify read marks, backfill accounting, or lock bytes. Focused tests compare
lookups against independent scans of SQLite-produced 512- and 4096-byte WAL/
`-shm` artifacts, verify the publication order, and use separate processes to
hold persistent torn and corrupt publications. Those tests characterize the
format component only; they do not establish concurrent interoperability.

`SqliteWalByteRangeLock` is a detached physical lock primitive over an existing
carrier file. Its leases express non-blocking or bounded shared and exclusive
byte-range acquisition without creating, mapping, or interpreting `-shm`.
Windows uses `LockFileEx`/`UnlockFileEx` with full 64-bit `OVERLAPPED` offsets;
64-bit Linux uses `fcntl(F_OFD_SETLK)` with `F_RDLCK` or `F_WRLCK`; macOS uses
POSIX `fcntl(F_SETLK)` with Darwin lock-type constants (process-associated,
not OFD — matches stock SQLite on Darwin). Each lease owns a dedicated carrier
descriptor until it is disposed, so an unrelated lease cannot shorten an OFD
lock lifetime on Linux. Focused worker-process tests cover shared-reader
coexistence, exclusive and mixed-mode contention, independent ranges, timeout
reporting, and release on disposal. This primitive is not connected to
`SqliteWalSharedMemoryLocks`, normal `-shm` activity, the pager, or any
read-mark, writer, or checkpoint role.

**Stage 1 pager attach (in progress):** the physical managed pager maps `-shm`
and publishes/rebuilds a dual-header WAL-index on create/open, commit, and
checkpoint reset under Stage 0 ownership. Frame lookup can validate against the
WAL scan. This does **not** yet attach Stage 2 read marks, Stage 3 writer roles
beyond ownership locks, or multi-process stock-SQLite interoperability.

**Remaining pager gate:** attach the detached reader protocol to managed
connections; implement runtime writer/checkpointer coordination; and run
differential cross-process stress while all of those mechanisms are attached
to the pager. Until Stages 2–6 complete, concurrent stock-SQLite
interoperability is still not claimed.

### Stage 2 — read marks and the reader protocol

Implement SQLite's `walTryBeginRead`: use `WAL_READ_LOCK(0)` for a database-only
snapshot, otherwise select the largest `aReadMark[i]` not exceeding `mxFrame`, or
claim an unused mark under an exclusive lock and downgrade it to shared. Reader
snapshots must be pinned to `mxFrame` at read-lock time instead of copying a
process-local overlay.

This requires *shared* byte-range locks on both platforms. `FileStream.Lock`
cannot express a shared lock on Windows, so a `LockFileEx` interop layer — and
Linux OFD read locks — must be added first.

*Exit criteria:* many managed readers share one mark, and managed and SQLite
readers coexist on the same mark.

#### Stage 2 detached foundation currently present

`SqliteWalReadSnapshotCoordinator` composes the Stage 1 physical mapping,
validated WAL-index accessor, and shared byte-range lease primitive into a
detached read-snapshot coordinator. It opens only existing `-wal` and `-shm`
artifacts, validates a stable header against an independent WAL checksum scan,
uses `WAL_READ_LOCK(0)` for an already-backfilled database-only view, otherwise
shares an existing current mark, advances any exclusively acquired idle mark to
the current committed boundary, or falls back to the greatest usable existing
mark. An advanced mark is exclusively held while changed and then reacquired
shared. Every selected mark is confirmed after its shared lease is obtained and
its boundary must name a WAL commit frame.

The resulting `SqliteWalReadSnapshot` exposes only direct, bounded WAL-frame
reads. It does not consult a live hash lookup after the boundary is pinned and
retains no page cache, so a writer may append later frames without changing the
snapshot. A changed read mark, a stale/torn header, a changed WAL incarnation,
or an invalid frame fails the snapshot closed and releases its lease. `Reset`,
`Dispose`, coordinator disposal, failed acquisition, and cancellation all
release any acquired mark.

This is deliberately still not pager behavior. It neither acquires nor relaxes
the Stage 0 512-byte main-file ownership guard, and it does not attach to a
managed connection, writer, recovery, or checkpoint path. Its process-isolated
tests operate on ordinary SQLite-produced artifacts to validate lock and frame
semantics only; they do not establish concurrent stock-SQLite client
interoperability for the managed pager.

#### Stage 3 detached foundation currently present

`SqliteWalWriterCheckpointCoordinator` is a detached protocol over an explicit
main database file, WAL, mapped index, and byte-range lock carrier. It takes
only `WAL_WRITE_LOCK` while appending a complete transaction, flushes the WAL
before publishing page/hash entries and the duplicate `WalIndexHdr`, and faults
rather than reusing an artifact when a failure occurs after a commit might have
become durable. Before appending, it requires the independently scanned valid
and committed frame boundaries to exactly equal the published header, so it
cannot turn an abandoned but checksum-valid tail into a later transaction.
Recovery takes `WAL_CKPT_LOCK`, `WAL_WRITE_LOCK`, `WAL_RECOVER_LOCK`, and every
read-mark lock before truncating such a tail. It rejects differing checksum-valid
header copies rather than selecting a zero-frame copy, and authorizes a
destructive tail repair only when the selected header matches the WAL's page
size, checksum byte order, salts, committed frame, database size, and final
frame checksum.

The checkpointer takes `WAL_CKPT_LOCK` independently of the pager. `PASSIVE`
calculates `mxSafeFrame` from only the read marks it cannot exclusively lock;
an active read-mark zero limits progress to `nBackfill`, and every other held
mark limits it to its committed boundary. It releases unheld marks before
copying pages so it does not block new readers. `FULL`, `RESTART`, and
`TRUNCATE` take `WAL_WRITE_LOCK` before waiting for all marks. They flush the
WAL before copying, flush the main file before advancing `nBackfill`, and reset
only after every read mark is exclusive. Restart first writes a durable
checkpoint-reset WAL marker, allowing detached open to repair the stale-index
interruption window; truncate removes that reset WAL header only after the
zero-frame index is visible. A zero-length truncated WAL is reopened as a fresh
empty incarnation using the main database's durable page size, never transient
`-shm` salts or headers. Checkpoint-progress publication remains bound to the
selected authenticated WAL incarnation and safe frame, allowing a later writer
append without advancing past that selected boundary while rejecting an
incarnation reset.
`nBackfillAttempted` is published before installation; `nBackfill` is advanced
only after the main-store flush. On open, it rebuilds all transient index
progress and lookup state from a clean WAL that it independently authenticates
under the full recovery lock set; this makes an unverified `nBackfill` unable
to authorize a reset. It reads recoverable header evidence only after obtaining
that complete lock set, so a torn publication observed while waiting cannot
authorize a later tail truncation. Corruption that reaches before the last
recoverable committed boundary is rejected fail-closed. Physical recovery acquires every checkpoint, writer, recovery, and read-mark
lease from a handle duplicated from the exact file that backs its mapping; it
never reopens the `-shm` path for a destructive recovery lease. On Windows the
recovery-only mapping denies delete sharing for its full lifetime, so the mapped
carrier cannot be unlinked or replaced between final evidence validation and
tail truncation. Failed detached commits release their writer lease and use that
same full recovery protocol before repairing a partial append. On Linux, where
unlink cannot be prevented through this carrier, detached recovery still
rebuilds a clean index but rejects every destructive tail repair fail-closed. A
missing `-shm` carrier is still rejected rather than recreated: without a
pre-existing lock carrier, detached recovery cannot prove that an unlink raced a
live client. Focused tests use SQLite-produced artifacts and separate
reader/writer/lock-worker processes.

This remains unreachable from `SqlitePager`, normal managed execution, cache
invalidation, and managed recovery. It does not relax the Stage 0 ownership
lock or establish any stock-SQLite concurrent interoperability claim.

### Stage 3 — writer and checkpointer protocol

- The writer takes only `WAL_WRITE_LOCK`, verifies the index header is unchanged,
  appends frames, and then publishes `mxFrame`, `nPage`, and checksums by writing
  both header copies with the required barrier between them.
- The checkpointer takes `WAL_CKPT_LOCK`, derives `mxSafeFrame` from
  `aReadMark[]`, backfills, and maintains `nBackfill`/`nBackfillAttempted`. Only a
  checkpointer that obtains exclusive locks on every read mark may reset the WAL.
- Managed must stop demanding the whole `[120, 8)` range for checkpoints and for
  `SqlitePager.Create`.
- **WAL-reset / salt race (SQLite ≤ 3.51.2, Tailscale blog):** a concurrent peer
  may call `walRestartLog` (new salts, `mxFrame = 0`) while a PASSIVE checkpointer
  still holds a stale local `mxFrame`/`nBackfill` view. Stock SQLite before
  3.51.3 could publish the stale safe frame into `nBackfill` and later skip new
  frames. Managed Ahtola does **not** wrap the WAL on ordinary writer commit
  (reset only via RESTART/TRUNCATE / `ResetAfterDurableCheckpoint`). Against a
  multi-engine peer that can wrap mid-PASSIVE, both the pager and detached
  coordinator re-check SHM + durable on-disk WAL salts
  (`TryConfirmCheckpointIncarnation`) after acquiring marks and again after
  PASSIVE releases them; on mismatch they **soft-skip** (no install, no
  `nBackfill` advance, no pager fault) rather than failing closed into a faulted
  state. Prefer SQLite ≥ 3.51.3 when the peer is the checkpointer.

*Exit criteria:* an ordinary SQLite writer and a managed reader (and the reverse)
interleave correctly under a differential stress harness, and `PRAGMA
wal_checkpoint` issued from either side agrees with the other.

### Stage 4 — busy semantics

Map `SQLITE_BUSY`, `SQLITE_BUSY_SNAPSHOT`, and `SQLITE_BUSY_RECOVERY` onto managed
exceptions; adopt SQLite's retry/backoff schedule instead of a flat 10 ms poll;
preserve `SqlitePagerBusyException.Operation` so existing callers keep working; and
add a distinct snapshot-invalidated result for readers whose mark was reset.

### Stage 5 — recovery, handoff, and shared cache invalidation

- Recovery runs under `WAL_RECOVER_LOCK` plus exclusive read marks, rebuilds the
  index from the WAL, and bumps `iChange`.
- Cache invalidation moves from the process-local
  `SqlitePagerLockManager.Generation` to the shared `WalIndexHdr` (`iChange`,
  `mxFrame`, salts). The current physical-pager guards — `ValidateMainFileFormat`,
  `ValidateWalIncarnation`, and the uncommitted-tail check — become ordinary
  snapshot comparisons instead of "dispose and reopen" errors.
- Handle `-shm` unlink by the last connection out, exclusive locking mode, and the
  heap-memory WAL-index fallback.

### Stage 6 — retire process-exclusive ownership

Stage 6 is attached. `SqliteManagedFileOwnership` was retained and repurposed as
the canonical-path broker needed to normalize Windows handle-scoped locks, Linux
OFD locks, and macOS process-scoped locks. WAL pagers hold lifetime SHARED while
DELETE pagers execute the complete SHARED → RESERVED → PENDING → EXCLUSIVE
protocol described in §1.1. Focused stock-SQLite worker tests cover reader/writer
conflicts, PENDING reader exclusion, and release after transaction or pager
disposal.

## 3. Invariants that hold in every stage

1. Never claim interoperability that is not implemented. Ownership and lock
   acquisition fail closed rather than proceeding optimistically.
2. Read-only opens never mutate storage, including companion files.
3. No silent downgrade to process-local locking on an unsupported platform.
4. A validation failure faults the pager instead of re-reading a database that
   changed underneath it.
5. Each stage ships with differential tests against ordinary SQLite before the next
   stage begins.

## 4. Characterization coverage

`src/Ahtola.Tests/SqliteWalInteroperabilityContractTests.cs` pins
the attached WAL boundary:

| Test | Contract clause |
| --- | --- |
| `ManagedWalCommitPublishesValidatedSqliteWalIndex` | Stage 1 — physical pager publishes a validated dual-header WAL-index |
| `ManagedWriterClaimsSqliteWalWriteLockByte` | §1.2 — the writer occupies byte 120 |
| `ManagedWritableOpenClaimsSqliteWalRecoveryLockByte` | §1.2, §1.6 — a writable open occupies byte 122 |
| `ManagedReaderClaimsTheFirstFreeSqliteReadMarkLockByte` | §1.2 — readers walk bytes 123–127 (Windows only; see below) |
| `ManagedReaderIsBusyWhenEverySqliteReadMarkLockByteIsHeld` | §1.3, §1.4 — five reader slots, then busy |
| `ManagedPassiveCheckpointHonorsHeldReadMarksWithoutCoarseLockArea` | Stage 3 — PASSIVE uses marks/`mxSafeFrame`; reset still needs exclusive marks |
| `ManagedCheckpointClaimsSqliteCheckpointLockByte` | Stage 3 — checkpoint takes `WAL_CKPT_LOCK` (byte 121) |
| `ManagedCheckpointPublishesWalIndexBackfillProgress` | Stage 3 — `nBackfill`/`nBackfillAttempted` published after install |
| `ManagedRolesStayInsideSqliteReservedSharedMemoryLockArea` | §1.2 — no locks outside bytes 120–127 |
| `ManagedReadOnlyOpenRefusesToCreateAMissingSharedMemoryLockCarrier` | §1.2, §3 — read-only opens never create `-shm` |
| `PooledReopenSurvivesSharedMemoryCarrierRemovedByNativeClose` (`ManagedConnectionPoolingTests.cs`) | §1.2 — a read-write pager recreates a missing carrier on demand like a native read-write connection, and the pooling catalog refresh tolerates its absence |

`src/Ahtola.Tests/ForeignReadOnlyOpenTests.cs` pins the §1.9
foreign read-only boundary against `Microsoft.Data.Sqlite` as the owner:

| Test | Contract clause |
| --- | --- |
| `CleanlyClosedWalDatabaseOpensAndMatchesSqlite` / `CleanlyClosedDeleteJournalDatabaseOpensAndMatchesSqlite` | §1.9 — foreign open of owner-free files matches the oracle |
| `WalDatabaseOwnedByLiveSqliteProcessOpensAndTracksCommittedState` / `DeleteJournalDatabaseOwnedByLiveSqliteProcessOpensAndTracksCommits` | §1.9 — live-owner commits surface at statement boundaries |
| `OwnerRecreatingWalBetweenStatementsIsAdoptedByForeignReader` | §1.9 — a replaced WAL incarnation is re-adopted |
| `HotRollbackJournalFailsClosed` | §1.9, §3 — an unreadable hot journal faults instead of guessing |
| `ExplicitReadTransactionPinsSnapshotWhileOwnerCommits` | §1.9 — explicit transactions pin their snapshot |
| `ForeignReadOnlyRejectsWrites` | §1.9 — writes are rejected |
| `ForeignReadOnlyWorksThroughAhtolaConnection` | §1.9 — both ADO surfaces expose the mode |
| `FacadeRejectsInvalidForeignReadOnlyCombinations` / `AhtolaConnectionRejectsInvalidForeignReadOnlyCombinations` | §1.9 — pooling, shared cache, encryption, and remote/native providers are refused |

Related existing coverage:

- `SqlitePagerPortableLockCoordinatorTests` — cross-process WAL SHARED
  coexistence; DELETE SHARED/RESERVED/PENDING/EXCLUSIVE conflicts; release on
  rollback, transaction disposal, and pager disposal; hot-journal recovery; and
  peer-commit freshness.
- `SqlitePagerLockingStorageTests` — lock-manager role interleaving and
  cross-process busy behavior.
- `SqlitePagerWalConcurrencyRecoverySliceTests` — main-file lock retry and
  recovery failure surfacing.
- `ManagedJournalPageMigrationTests` — WAL incarnation change detection.
- `SqliteWalProcessIsolationHarnessTests` — detached-only process worker races,
  crash windows, fail-closed tail recovery, and post-handoff SQLite artifact
  reopening.

Tests that need an external holder of a `-shm` byte range start a worker process
(`CrossProcessSharedMemoryLockWorkerHoldsRequestedRanges`) instead of opening a
second handle in the test process, because POSIX record locks are process-scoped
and would not contend with the managed coordinator on Linux. The single test that
must *probe* which read-mark byte the managed reader claimed still needs a
handle-scoped lock inside the process and therefore runs on Windows only.
