# MVCC port contract (Ahtola ↔ Turso v0.7.2)

Companion to [`wal-interoperability-contract.md`](wal-interoperability-contract.md)
§1.4 and [`turso-gap-analysis.md`](turso-gap-analysis.md) §8.

## Upstream reference

Behavioral baseline: Turso **v0.7.2** (`046e9cbf6`). The read-only submodule
now pins **v0.8.0-pre.7** (`277ddd050`) for browser-WASM work; retrieve the
original baseline with `git -C turso-src show v0.7.2:<path>`.

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
7. **Concurrent DDL is generation gated.** Schema rows and table identities are
   versioned by schema generation. A schema-changing `BEGIN CONCURRENT`
   transaction publishes through the pager-first schema path and fails busy
   while an incompatible peer snapshot is active, rather than exposing or
   discarding a stale catalog.

## Phase map

| Phase | Deliverable |
| --- | --- |
| **1** | Clock, `MvStore` tx registry + write-set WW conflicts, pragma/BEGIN surface, classic catalog DML under concurrent txs |
| **1.5** | Row-version chains (`Insert`/`Update`/`Delete`/`TryRead`/`ScanVisible`), visibility + WW on chains, commit stamp rewrite, rollback drop |
| **2** | Durable logical log (`*.db-log`) with Turso LML2/MVTX framing constants, CRC32C, upsert/delete ops, replay into `MvStore` on enable; encrypted databases AES-GCM-encrypt row payload chunks while authenticating salt/length/op-count/commit-ts/chunk/version metadata and require exact payload consumption; checkpoint TRUNCATE clears log |
| **3** | Header version **255** via pager `SwitchJournalMode(Mvcc)`; cold open restores `MvStore`; typed `MvccKey` rows/index objects and V4 logical-log frames cover rowid and composite keys; **shared `MvStore`/log per path** (`EmbeddedMvStoreRegistry`) lets pooled multi-connection concurrent writers share one version store; concurrent commit reloads durable catalog then merges store snapshots |
| **3.5** | **SQL dual-cursor routing:** under `BEGIN CONCURRENT`, typed base/store overlays cover rowid and `WITHOUT ROWID` primary keys, originally via materialized index-plan overlays (superseded for eligible index paths by the page-native accessor in 3.7). DML records typed table/index versions through `ReportRowChange`, including trigger and foreign-key actions; peer uncommitted writes remain invisible, snapshot isolation holds after peer commit, and same-key writes conflict. |
| **3.6** | **Synchronous page-WAL checkpoint state machine:** `PRAGMA wal_checkpoint` in MVCC mode runs `RunMvccCheckpoint` — AcquireLock → Collect/Materialize (reuse `MergeConcurrentCatalogFromStoreLocked`) → persist pages to WAL **without automatic reset** → backfill and flush the main store → retire/upgrade the logical log → reset the WAL last for TRUNCATE/RESTART/FULL → `GarbageCollectAfterCheckpoint`. Active concurrent txs return busy before mutation. PASSIVE retains both WAL and logical-log recovery evidence. This adapts Turso's cooperative b-tree I/O state machine to managed synchronous `IFileSystem` operations. Fixed alongside 3.7: the schema owner's own pinned reader snapshot is released *before* the internal TRUNCATE checkpoint that `BeginConcurrentSchemaChange` issues, so a schema-changing connection no longer deadlocks/busy-fails against its own pin (`EmbeddedDatabase.cs`). |
| **3.7 (current)** | **Page-native direct-index access:** eligible `BEGIN CONCURRENT` index joins/scans (ordinary, partial, expression, `WITHOUT ROWID`, and validated custom-collation secondary indexes) seek and stream the transaction-pinned durable b-tree snapshot (`EmbeddedFileReadSnapshot` captured at `BEGIN CONCURRENT`, generation/root-map bound) directly via the existing page-native cursors (`SqliteIndexBtreeCursor`/`SqliteTableBtreeCursor`), merging lazily with the visible `MvStore` overlay through `MvccDualCursor`'s two-pointer merge (per Turso's `core/mvcc/cursor.rs` two-peek semantics: peek base, peek overlay, emit the lesser typed key, suppress base rows shadowed by an overlay tombstone/replacement). No `table.GetOrCreateIndexScanOrder`/`BuildBaseIndexScanOrder` full-table materialization occurs on these paths. Classic transactions use the same pinned-cursor contract with a savepoint-aware mutation overlay. Custom-collation callbacks remain connection-bound and are generation/version validated; stale physical order stays ineligible until `REINDEX`. `EmbeddedDatabase.JoinIndexSeekMetrics` differentiates durable cursor plans, prohibited full-index materialization, overlay rows examined/emitted, and base rows suppressed. |
| **Open** | Concurrent DDL remains deliberately exclusive while incompatible MVCC snapshots exist; full cooperative per-page checkpoint-state-machine parity with Turso remains deferred. Custom-collated `WITHOUT ROWID` primary keys remain fail-closed because their comparator is required before the catalog can be reconstructed. |

## Dual-cursor SQL routing notes

- **INSERT:** rowid tables allocate a store-global rowid; `WITHOUT ROWID` tables derive a typed,
  collation-aware primary-key identity. `ReportRowChange` records the corresponding table and
  index versions without double insertion.
- **DELETE/UPDATE:** base-key tombstones and replacements remain one MVCC mutation scope. Primary
  key or indexed-value changes remove old typed/index keys and insert their replacements.
- **WW:** `ThrowIfConcurrentWriterOnRow` applies to the typed identity, so pure base tombstones
  cannot bypass first-committer-wins conflict detection.
- **SELECT:** typed dual-cursor routing covers rowid and `WITHOUT ROWID` primary-key scans.
  Eligible index joins/scans (ordinary, partial, expression, `WITHOUT ROWID`) route through the
  page-native direct-index accessor (phase 3.7): the durable b-tree snapshot is streamed and
  merged lazily with the visible `MvStore` overlay, with zero full base-index materialization.
  `sqlite_schema` publication is schema-generation gated; DDL remains exclusive against active
  MVCC snapshots rather than exposing a stale catalog.
- **Process-local:** store is not cross-process (same as Turso process MVCC scope here).

## Checkpoint notes

- Concurrent **commit** already merges store → catalog; checkpoint re-merges for safety, then writes a fresh page-WAL transaction without resetting it.
- **Durable ordering:** WAL page transaction → main-store backfill/flush → logical-log retirement/flush → WAL reset. A logical-log retirement or WAL-reset failure leaves validated WAL recovery evidence; the latter may leave a header-only log with a retained WAL, which cold reopen accepts.
- After successful restart/truncate with no active readers, version chains are dropped (`GarbageCollectAfterCheckpoint`); dual-cursor defers to catalog.
- PASSIVE/busy with open concurrent txs does **not** mutate or truncate recovery artifacts. A successful passive checkpoint retains WAL and logical-log evidence rather than attempting a destructive reset.

## Testing

- Unit: `MvccStoreUnitTests` (base tombstone / WW / update overlay),
  `MvccHeaderAndDualCursorTests`, `MvccSelectDualCursorRoutingTests` (E2E SQL),
  `MvccCheckpointStateMachineTests` (TRUNCATE cold reopen, busy, GC),
  `ManagedTransactionModeLockingTests` concurrent cases,
  `ManagedAdvancedFeatureBoundaryTests`, `ManagedJournalPageMigrationTests` MVCC case.
- Conformance: clear MVCC markers in
  `managed-sqltest-expected-failures.txt` only when cases pass for real.
- Do not greenwash: remove a failure line only when the case passes for real.
