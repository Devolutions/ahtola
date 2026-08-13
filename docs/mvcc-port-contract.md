# MVCC port contract (Ahtola ↔ Turso v0.7.2)

Companion to [`wal-interoperability-contract.md`](wal-interoperability-contract.md)
§1.4 and [`turso-gap-analysis.md`](turso-gap-analysis.md) §8.

## Upstream reference

Pinned submodule: `turso-src/` @ **v0.7.2** (`046e9cbf6`).

| Turso | Ahtola |
| --- | --- |
| `core/mvcc/clock.rs` | `src/Ahtola.Core/Mvcc/MvccClock.cs` |
| `core/mvcc/database/mod.rs` (`MvStore`) | `src/Ahtola.Core/Mvcc/MvStore.cs` |
| `core/mvcc/cursor.rs` | `src/Ahtola.Core/Mvcc/MvccDualCursor.cs` + SQL SELECT/DML routing under concurrent scope |
| `core/mvcc/persistent_storage/logical_log.rs` | `src/Ahtola.Core/Mvcc/MvccLogicalLog.cs` |
| `core/mvcc/database/checkpoint_state_machine.rs` | `MvccCheckpoint` + `EmbeddedDatabase.RunMvccCheckpoint` (sync skeleton: materialize → persist → truncate log → GC; not full cooperative btree IO SM) |
| shared store per DB identity | `src/Ahtola.Core/Mvcc/EmbeddedMvStoreRegistry.cs` |
| `LimboError::WriteWriteConflict` | `EmbeddedWriteWriteConflictException` |

## Invariants (must hold)

1. **Clock publish atomicity.** Commit timestamp generation and transition to
   `Preparing(ts)` happen under the same clock lock so a peer cannot take a
   `begin_ts` between those steps (snapshot isolation).
2. **No fake `journal_mode=mvcc`.** Reporting `mvcc` requires a live `MvStore`
   on the database. Disabling MVCC clears the store.
3. **`BEGIN CONCURRENT` gate.** Without MVCC → Turso error string
   `Concurrent transaction mode is only supported when MVCC is enabled`.
   With MVCC → open concurrent tx; nested BEGIN →
   `cannot start a transaction within a transaction`.
4. **Temp DB.** `PRAGMA temp.journal_mode=mvcc` is ignored; temp reports `wal`.
5. **Classic path default.** Without the pragma, behavior matches §1.6
   (single write reservation, WAL snapshots).
6. **Upstream anomaly TODOs.** Do not claim protection against phantoms, cursor
   lost updates, read skew, or write skew beyond Turso v0.7.2.
7. **Concurrent DDL fails closed.** Turso versions `sqlite_schema` rows through
   MVCC. Ahtola does not yet version schema rows, so schema-changing statements
   inside `BEGIN CONCURRENT` are rejected rather than appearing to succeed and
   then being discarded by the committed-row merge.

## Phase map

| Phase | Deliverable |
| --- | --- |
| **1** | Clock, `MvStore` tx registry + write-set WW conflicts, pragma/BEGIN surface, classic catalog DML under concurrent txs |
| **1.5** | Row-version chains (`Insert`/`Update`/`Delete`/`TryRead`/`ScanVisible`), visibility + WW on chains, commit stamp rewrite, rollback drop |
| **2** | Durable logical log (`*.db-log`) with Turso LML2/MVTX framing constants, CRC32C, upsert/delete ops, replay into `MvStore` on enable; checkpoint TRUNCATE clears log |
| **3** | Header version **255** via pager `SwitchJournalMode(Mvcc)`; cold open restores `MvStore`; `MvccDualCursor` merge primitive; **shared `MvStore`/log per path** (`EmbeddedMvStoreRegistry`) so pooled multi-connection concurrent writers share one version store + rowid allocator; concurrent commit reloads durable catalog then merges store snapshots |
| **3.5** | **SQL dual-cursor routing:** under `BEGIN CONCURRENT`, `GetNamedTableRows` merges base catalog + store via `MvccDualCursor.MergeVisibleRows`; DML records versions via `ReportRowChange` → `DeleteOrTombstoneBase` / `UpdateIncludingBase` (DELETE/UPDATE) and connection `RecordConcurrentMvccMutation` (INSERT + global rowid); peer uncommitted writes invisible; SI after peer commit; same-row WW on SQL path |
| **3.6 (current)** | **Checkpoint SM skeleton:** `PRAGMA wal_checkpoint` in MVCC mode runs `RunMvccCheckpoint` — AcquireLock → Collect/Materialize (reuse `MergeConcurrentCatalogFromStoreLocked`) → Persist catalog → Truncate logical log (TRUNCATE/RESTART/FULL) → `GarbageCollectAfterCheckpoint` (clear store when no active txs; else prune past reader LWM). Active concurrent txs → busy=1, no truncate. Not a full Turso cooperative btree page walk. |
| **Open** | MVCC-versioned schema rows (concurrent DDL currently fails closed); schema generation cookie polish; full per-page btree checkpoint SM / WAL TRUNCATE interleave parity with Turso if product requires it |

## Dual-cursor SQL routing notes

- **INSERT:** classic catalog insert first; connection allocates store-global rowid and `MvStore.Insert`. `ReportRowChange` does **not** re-insert (avoids double store rows).
- **DELETE of base-only row:** pure tombstone (`begin=txId`, `end=null`, `IsTombstone=true`). `SnapshotCommittedDeletes` treats committed live tombstones as deletes for catalog merge.
- **UPDATE of base-only row:** `UpdateIncludingBase` = tombstone prior + insert new cells.
- **WW on concurrent base tombstones:** `ThrowIfConcurrentWriterOnRow` — pure tombstones share `End=null`, so classic chain WW alone is insufficient.
- **SELECT:** dual-cursor path is rowid tables only; `sqlite_*` and WITHOUT ROWID stay catalog-only.
- **Process-local:** store is not cross-process (same as Turso process MVCC scope here).

## Checkpoint notes

- Concurrent **commit** already merges store → catalog; checkpoint re-merges for safety then persists so TRUNCATE cannot drop the only durable copy of rows.
- After successful truncate with no active readers, version chains are dropped (`GarbageCollectAfterCheckpoint`); dual-cursor defers to catalog.
- PASSIVE/busy with open concurrent txs does **not** truncate the log (avoids losing unrecovered store-only frames if materialize is skipped).

## Testing

- Unit: `MvccStoreUnitTests` (base tombstone / WW / update overlay),
  `MvccHeaderAndDualCursorTests`, `MvccSelectDualCursorRoutingTests` (E2E SQL),
  `MvccCheckpointStateMachineTests` (TRUNCATE cold reopen, busy, GC),
  `ManagedTransactionModeLockingTests` concurrent cases,
  `ManagedAdvancedFeatureBoundaryTests`, `ManagedJournalPageMigrationTests` MVCC case.
- Conformance: clear MVCC markers in
  `managed-sqltest-expected-failures.txt` only when cases pass for real.
- Do not greenwash: remove a failure line only when the case passes for real.
