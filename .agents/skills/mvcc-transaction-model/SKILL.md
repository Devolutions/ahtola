---
name: mvcc-transaction-model
description: How transaction, snapshot, and MVCC semantics map between Turso's Rust core and Ahtola's managed engine, and what must stay aligned. Use this when touching transactions, snapshots, isolation, commit/rollback, or the local/replica adapter dispatch.
---

# MVCC and transaction model

Turso implements an MVCC layer (`turso-src/core/mvcc/`: `clock.rs`,
`cursor.rs`, `portable_logical.rs`, `yield_hooks.rs`, `yield_points.rs`) on
top of the storage/pager layer. Ahtola's managed engine mirrors the
observable transaction semantics (begin/commit/rollback, snapshot isolation,
savepoints) but adapts the implementation to managed idioms.

## Where things live in Ahtola

- Transaction execution: `src/Ahtola.Core/Execution/VdbeTransaction.cs`
  (`BeginTransaction`/`CommitTransaction`/`RollbackTransaction` opcodes).
- Snapshot/adapter layer: `src/Ahtola.Core/ManagedLocalAdapters.cs` —
  `IManagedConnectionAdapter`, `ManagedSnapshotException`,
  `ManagedSnapshotFailure`, `CopySnapshotTo(...)` between connections.
- Rollback journal: `src/Ahtola.Core/Storage/SqliteRollbackJournal.cs`.
- WAL read-snapshot/checkpoint coordination:
  `SqliteWalReadSnapshotCoordinator.cs`,
  `SqliteWalWriterCheckpointCoordinator.cs`.
- Local/remote/replica provider dispatch: `Ahtola.Data` (connection pooling,
  provider dispatch, Hrana remote client).

## What must stay aligned

- **Isolation semantics**: a snapshot taken at begin-transaction must see a
  consistent database view for the transaction's lifetime. Do not weaken
  snapshot isolation to "read latest" to fix a test — fix the snapshot
  coordinator.
- **Commit/rollback atomicity**: commit must durably flush per the WAL
  contract; rollback must restore the pre-transaction page state exactly.
  Cross-check `SqliteRollbackJournal.cs` and the WAL checkpoint coordinator
  against `turso-src/core/mvcc/` and `turso-src/core/storage/wal.rs`.
- **Schema version / user version / application id**: `ApplySnapshotPragmaHeader`
  propagates these on snapshot copy; do not drop them.
- **Snapshot copy invariants**: `CopySnapshotTo` checks
  `CannotProveDistinctSnapshotFiles(...)` and raises a
  `ManagedSnapshotException` with a `ManagedSnapshotFailure` reason. Preserve
  those failure reasons — callers branch on them.

## Adaptation, not reimplementation

- Turso's MVCC uses cooperative yielding (`yield_hooks`/`yield_points`) for
  scheduling. Ahtola uses `async`/`await` or synchronous managed flow — the
  *semantics* (when a transaction yields, what a reader sees) must match, not
  the call style. See the `async-io-port` skill.
- The Turso MVCC clock (`clock.rs`) assigns logical timestamps; mirror the
  ordering invariants in the managed layer even if the clock implementation
  differs.
- Do **not** introduce a native MVCC or sync companion. Replica/sync behavior
  references `Turso.Data.Sync` by name and fails closed (see
  `pure-managed-closure`); that is a product decision.

## When unsure

Consult `turso-src/core/mvcc/` for the upstream semantics and
`docs/wal-interoperability-contract.md` for the WAL framing that the snapshot
coordinators depend on.
