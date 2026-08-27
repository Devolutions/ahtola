# WAL Stages 1–6 implementation roadmap

Normative protocol details live in [`wal-interoperability-contract.md`](wal-interoperability-contract.md).
This document is the **ordered engineering plan** to finish full Turso/SQLite WAL interop.

**Hard rules**

1. Main-file locking is the Stage 6 SQLite protocol, not exclusive 512-byte
   ownership: WAL retains SHARED; DELETE uses SHARED, RESERVED, PENDING, and
   EXCLUSIVE at transaction boundaries.
2. Keep WAL write/checkpoint serialization on `-shm`; the DELETE main-file
   protocol must not weaken WAL mode.
3. Detached foundations already exist; each stage’s job is to **attach** them to `SqlitePager` (and only then refine locks/busy/recovery).
4. Byte-exact WAL-index layout (`SqliteWalIndex*`) is non-negotiable.
5. MVCC is out of scope for this roadmap.

---

## Current state (as of this branch)

| Piece | Status |
| --- | --- |
| Stage 0 process-exclusive ownership | **Retired** |
| Stage 1 WAL-index mapped + published from pager | **Attached** |
| Stage 2 read marks via `SqliteWalReadSnapshotCoordinator` | **Attached** for physical WAL readers |
| Stage 3 writer publish + CKPT_LOCK checkpoint/`nBackfill` | **Attached** |
| Stage 4 busy taxonomy + SQLite backoff | **Attached** |
| Stage 5 recovery + `iChange` invalidation | **Attached** |
| Stage 6 main-file SHARED (retire exclusive 512-byte ownership) | **Attached** |
| Live multi-engine WAL (`-shm` DMS + shared readers) | **Attached** (stock SQLite 3.53+ coexistence proven; Turso binary differential optional) |
| Foreign read-only guest (§1.9) | Live; not full interop |

**Pager gate:** Stages 1–6 and physical EXCLUSIVE-mode transitions are attached.
Ordinary closes intentionally retain `-shm` so a later read-only open can
coordinate without creating storage. Windows/Linux perform DMS-proven carrier
cleanup when explicitly entering `locking_mode=EXCLUSIVE`, then use a heap
WAL-index. macOS retains the carrier and rejects physical EXCLUSIVE mode because
its process-owned `fcntl` locks cannot distinguish an in-process foreign SQLite
mapping. Optional Turso binary differential stress remains qualification depth.

---

## Stage order (do not reorder)

### Stage 1 — WAL-index format attached to pager

**Goal:** Managed physical WAL activity maintains a real SQLite WAL-index in `-shm` under exclusive ownership.

**Work**

1. On writable physical WAL open / create / `PRAGMA journal_mode=WAL`: map `db-shm` and `RebuildFromWal` (or publish empty header when `mxFrame=0`).
2. After every durable WAL commit (`Flush` + recovery boundary check): rebuild or `PublishCommittedFrames` so dual headers + `aPgno`/hash match the WAL scan.
3. After checkpoint + WAL reset: `ResetAfterDurableRestart` (or rebuild empty index) so salts/`iChange` stay coherent.
4. Dispose mapping with the pager; fail closed if mapping/publish fails (fault pager).
5. Optional internal API: `FindFrame` via index must agree with independent WAL scan (test).
6. Update Stage 0 characterization test `ManagedWalActivityNeverMaterializesASqliteWalIndex` → assert non-zero validated index instead of length 0.
7. In-memory / non-physical FS: leave index null (no mmap).

**Exit criteria**

- [x] After managed commit, `-shm` length ≥ one WAL-index region and dual headers validate against the WAL.
- [x] `FindFrame` for every page written in the commit matches `SqliteWalFile` scan (via pager hooks).
- [x] Ownership lock still held; no foreign writer allowed.
- [x] Existing WAL unit/integration suites green (`SqliteWalInteroperabilityContractTests` + related).

**Landed:** physical pager attaches mapping on create/open/journal switch; rebuilds on commit and checkpoint; `EnsureWritableBlocks` grows zero-length carriers; contract test flipped to Stage 1 publication.

**Does not include:** readers using read marks; dropping ownership; SQLite peer coexistence.

---

### Stage 2 — Read marks and reader protocol on pager

**Goal:** Managed readers pin snapshots through `aReadMark[]` + `WAL_READ_LOCK(i)` instead of process-local overlays alone.

**Work**

1. Attach `SqliteWalReadSnapshotCoordinator` (or equivalent) when acquiring a managed read transaction on physical WAL.
2. Prefer sharing an existing mark ≤ `mxFrame`; claim idle mark under exclusive then downgrade shared.
3. Use `WAL_READ_LOCK(0)` only for fully backfilled DB-only views.
4. Keep Stage 0 ownership; process-local overlay may remain as cache but boundary comes from the mark.
5. Multi-reader tests: N managed readers share marks without exclusive conflict.

**Exit criteria**

- [x] Multiple managed readers coexist on the same mark.
- [x] Snapshot boundary is the mark’s `mxFrame`, not a later writer append.
- [x] Ownership still exclusive for production opens.

**Landed:** physical pager `BeginReadTransaction` uses `SqliteWalReadSnapshotCoordinator` when a Stage 1 index is attached; overlay is pinned (or rebuilt) to the mark; busy maps to `SqlitePagerBusyException`.

**Deferred to Stage 6 tests:** true managed+SQLite reader coexistence on one mark (requires no ownership).

---

### Stage 3 — Writer and checkpointer protocol on pager

**Goal:** Commit and checkpoint use SQLite lock roles via the index, not only coarse process locks.

**Work**

1. Attach `SqliteWalWriterCheckpointCoordinator` paths into `CommitTransaction` and checkpoint APIs.
2. Writer: `WAL_WRITE_LOCK` only → append → flush → publish frames/headers.
3. Checkpointer: `WAL_CKPT_LOCK`; derive `mxSafeFrame` from held marks; advance `nBackfill` only after main flush.
4. Stop demanding the entire `[120, 8)` range for every checkpoint once protocol is live (keep Stage 0 ownership separately).
5. Recovery rebuild under full recovery lock set when tails are dirty.

**Landed:** commit uses incremental `PublishCommittedFrames` when the prior dual-header is valid; physical checkpoint takes `WAL_CKPT_LOCK`, honors held read marks for `mxSafeFrame`, publishes `nBackfill`/`nBackfillAttempted`, and resets only with exclusive marks. Point reads no longer occupy legacy lock-manager mark bytes when the WAL-index protocol is live. Ownership unchanged.

**Exit criteria**

- [x] Managed writer incremental publish agrees with independent scan after commit (under Stage 0 writer lock).
- [x] Passive/full checkpoint via CKPT_LOCK + read marks + `nBackfill` on pager.
- [x] Stop coarse full-range checkpoint lock demand for physical WAL-index checkpoints.
- [x] Still no ownership relaxation.

---

### Stage 4 — Busy semantics

**Goal:** Busy errors match SQLite taxonomy and retry schedule.

**Work**

1. Map `SQLITE_BUSY`, `SQLITE_BUSY_SNAPSHOT`, `SQLITE_BUSY_RECOVERY` onto managed exceptions (`SqlitePagerBusyException` + new snapshot-invalidated type).
2. Replace flat 10 ms poll with SQLite-like backoff.
3. Preserve `Operation` for existing callers.

**Landed:** `SqlitePagerBusyReason` {Busy, Snapshot, Recovery}; recovery opens use Recovery; rewritten read marks surface Snapshot on `ReadPage`; `SqliteBusyBackoff` matches sqliteDefaultBusyCallback delays across lock waiters.

**Exit criteria**

- [x] Tests for busy class taxonomy + backoff schedule.
- [x] Reader whose mark was reset gets Snapshot busy.

---

### Stage 5 — Recovery, handoff, shared cache invalidation

**Goal:** Crash recovery and multi-connection (same process / future multi-process) cache invalidation use shared header fields.

**Work**

1. Recovery under `WAL_RECOVER_LOCK` + exclusive read marks; rebuild index; bump `iChange`.
2. Replace process-local `LockManager.Generation` invalidation with `iChange` / `mxFrame` / salts comparisons for physical WAL.
3. Handle last-connection `-shm` unlink, exclusive locking mode, heap WAL-index fallback where required.

**Landed:** pager recovery takes CKPT + exclusive read marks (reuses lock-manager writer/recovery bytes when already held), repairs dirty tails, `RebuildFromWal` bumps `iChange`; `SynchronizeCommittedView` invalidates on shared header identity change. Windows/Linux EXCLUSIVE transitions take exclusive DMS before unlinking; exclusive locking mode uses a heap WAL-index. Ordinary close retains the carrier for non-mutating read-only reopen.

**Exit criteria**

- [x] Torn/corrupt publication fail-closed; clean WAL rebuilds index with bumped `iChange`.
- [x] Committed-view rescan when `iChange`/`mxFrame`/salts advance.
- [x] DMS-proven EXCLUSIVE-transition `-shm` unlink + heap fallback on platforms with descriptor-owned locks; macOS fails closed as described above.
- [ ] Ordinary final-close unlink is intentionally retained as a residual because removing a header-only carrier breaks later non-mutating read-only opens.

---

### Stage 6 — Retire process-exclusive ownership

**Goal:** Full multi-process interop with ordinary SQLite and Turso.

**Work**

1. Only after Stages 1–5 exit criteria are green.
2. Replace 512-byte main-file ownership with SQLite
   `PENDING`/`RESERVED`/`SHARED`/`EXCLUSIVE` locks for DELETE mode.
3. Repurpose `SqliteManagedFileOwnership` as the canonical-path process broker
   required to normalize Windows handle-scoped, Linux OFD, and macOS
   process-scoped lock lifetimes.
4. Differential stress: SQLite writer ↔ managed reader and reverse; `PRAGMA wal_checkpoint` agreement; Turso open of managed-produced artifacts and reverse.
5. Update contract status banner from “Stage 0” to “Stage 6 complete”.

**Landed:** path canonicalization follows symlinks and one broker aggregates
logical locks onto a stable OS handle. WAL pagers retain lifetime SHARED while
write exclusion stays on `-shm`. DELETE operations acquire SHARED; transactions
add RESERVED at begin and PENDING + EXCLUSIVE at commit, with correct busy retry,
downgrade, rollback, and disposal release. Long-lived DELETE pagers rescan the
main file under their next SHARED lock. Stock-SQLite worker tests prove reader
coexistence, writer conflict, PENDING exclusion of new readers, release after
transaction/pager disposal, and peer-commit freshness. Live multi-engine WAL
remains proven in both directions with checkpoint agreement.

**Known remaining qualification:** optional Turso binary differential stress
and an expanded process-isolation harness on Linux/macOS CI.

**Exit criteria**

- [x] Stock SQLite and managed open the same live DB without exclusive 512-byte ownership (DELETE-mode / handoff proven).
- [x] No silent downgrade on unsupported platforms (still fail closed off Windows/Linux/macOS).
- [x] Characterization suite rewritten for SHARED coexistence.
- [x] Live WAL multi-engine `-shm` interop (stock SQLite + managed both WAL; reader/writer both directions + checkpoint).
- [x] DELETE SHARED/RESERVED/PENDING/EXCLUSIVE protocol and release paths.
- [ ] Optional Turso differential stress.

---

## Suggested implementation slices (PRs)

| PR | Scope |
| --- | --- |
| A | This roadmap + Stage 1 pager publish/rebuild + contract test flip |
| B | Stage 1 FindFrame path optional dual-read (overlay vs index) parity tests |
| C | Stage 2 attach read marks |
| D | Stage 3 attach writer/checkpoint |
| E | Stage 4 busy |
| F | Stage 5 recovery/invalidation |
| G | Stage 6 ownership retirement + interop harness |

---

## Non-goals

- MVCC journal mode / concurrent writers inside one process via MVCC.
- Virtual tables, vector, super-journal.
- Faking `journal_mode=mvcc` in conformance.

## References

- `docs/wal-interoperability-contract.md`
- `src/Ahtola.Core/Storage/SqliteWalIndex.cs`
- `src/Ahtola.Core/Storage/SqliteWalReadSnapshotCoordinator.cs`
- `src/Ahtola.Core/Storage/SqliteWalWriterCheckpointCoordinator.cs`
- `src/Ahtola.Core/Storage/SqlitePager.cs`
- `src/Ahtola.Tests/SqliteWalIndex*.cs`, `SqliteWalProcessIsolationHarnessTests.cs`
