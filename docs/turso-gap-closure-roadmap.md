# Turso gap closure roadmap

## Baseline

This roadmap was reconciled on 2026-08-28 against the read-only Turso
`v0.8.0-pre.7` submodule (`277ddd050`) and Ahtola `51aec39`.

The newly enabled deterministic `:default:` sqltest fixtures expose 135 known
differences in
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`.
Two are intentional: Ahtola accepts STORED generated columns where the pinned
Turso corpus expects them to be rejected. The remaining 133 cases form seven
user-visible correctness workstreams. Three architecture workstreams come from
the explicit residual limits in `README.md`.

The historical inventory in `turso-gap-inventory.json` remains an audit of the
earlier v0.7.2 closure waves. A `closed` entry there can mean an intentional
scope decision; it does not mean Ahtola implements every newer Turso feature.

## Ranked workstreams

| Rank | ID | Current evidence | Upstream reference | Status |
| ---: | --- | ---: | --- | --- |
| 1 | `scalar-expression-parity` | 31 sqltests closed | `core/functions/`, `core/time/`, `core/uuid.rs`, `core/dialect/sqlite.rs` | Done |
| 2 | `pragma-introspection-parity` | 29 sqltests closed | `core/translate/pragma.rs`, `core/pragma.rs`, `core/vtab.rs` | Done |
| 3 | `json-jsonb-parity` | 21 sqltests closed | `core/json/` | Done |
| 4 | `grouped-aggregation-parity` | 30 sqltests closed | `core/translate/aggregation.rs`, `group_by.rs`, `subquery.rs`, `order_by.rs` | Done |
| 5 | `select-validation-parity` | 10 sqltests closed | `core/translate/planner.rs`, `expr/`, `select.rs` | Done |
| 6 | `window-group-pipeline` | 9 sqltests closed | `core/translate/window.rs` | Done |
| 7 | `join-coalescing-parity` | 4 sqltests closed | `core/translate/planner.rs`, `plan.rs`, `select.rs` | Done |
| 8 | `planner-access-path-depth` | Documented runtime limit | `core/translate/optimizer/`, `planner.rs`, `main_loop/` | Planned |
| 9 | `mvcc-page-native-depth` | Documented runtime limit | `core/mvcc/` | Planned |
| 10 | `sync-engine-depth` | Wait/apply lifecycle split landed | `sync/engine/src/` | Partial (checkpoint policy blocked by rank 9) |

The sqltest counts overlap by subsystem only in implementation, not in this
classification: ranks 1-7 account for 133 distinct expected-failure entries.

## Progress

- 2026-08-28: closed `scalar-expression-parity` (31 markers).
- 2026-08-28: closed `pragma-introspection-parity` (29 markers).
- 2026-08-29: closed `json-jsonb-parity` (21 markers).
- 2026-08-29: closed `select-validation-parity` (10 markers).
- 2026-08-29: closed `join-coalescing-parity` (4 markers).
- 2026-08-29: closed `grouped-aggregation-parity` (30 markers).
- 2026-08-29: closed `window-group-pipeline` (9 observed cases; one newly
  exposed case was not in the 135-entry baseline).
- Current expected-failure count: 2, both intentional STORED-generated-column
  differences. There are no actionable sqltest failures in the baseline.
- 2026-08-29: `sync-engine-depth` -- landed the managed equivalent of Turso's
  `wait_changes_from_remote` -> opaque staged changes -> `apply_changes_from_remote`
  split (`ManagedReplicaBootstrapper.WaitForRemoteChangesAsync` /
  `ApplyRemoteChangesAsync` / `ManagedReplicaStagedChanges`), and refactored
  `AhtolaConnection.SyncAsync`'s host publication gate to close/reopen sibling
  connections only around push and the local apply, never around the network
  long-poll in between. See the detailed TODOs below for what remains blocked
  on the parallel MVCC branch.

## Detailed TODOs

### 1. Scalar expression parity

- [x] Add SQLite/Turso variadic `iif()` and `if()` evaluation: condition/value
  pairs, optional final else, NULL when no branch matches, and prepare-time
  minimum-arity validation.
- [x] Permit typeless `CAST(expr AS)` and apply SQLite's empty type-name
  affinity instead of rejecting the syntax.
- [x] Make `substr`, `quote`, `char(0)`, numeric `length`, exponent-to-integer
  casts, and floating-point quote formatting match Turso byte-for-byte,
  including embedded NULs.
- [x] Match `concat_ws` and generic function arity errors at prepare time.
- [x] Reject non-positive/out-of-range UUIDv7 timestamps as Turso does.
- [x] Port `time_date` and align out-of-range `unixepoch` behavior.
- [x] Enforce the LIKE pattern-complexity boundary without unbounded
  backtracking or allocation.
- [x] Add focused NUnit differential tests, run the four affected sqltest
  files, and remove only markers that now pass.

### 2. PRAGMA introspection parity

- [x] Accept both `PRAGMA table_info = value` and
  `PRAGMA table_info(value)` forms, including supported aliases.
- [x] Route `pragma_table_info`, `pragma_table_xinfo`, and related pragma
  modules through the table-valued-function planner with SQLite argument
  coercion.
- [x] Make pragma virtual tables independent of join order and usable on
  either side of a join.
- [x] Implement the documented update/error contract for writable pragma
  virtual tables rather than silently accepting mutation.
- [x] Populate `pragma_module_list` from the static managed module registry.
- [x] Populate `pragma_function_list` from built-ins and registered functions,
  with correct scalar/aggregate/window flags and arities.
- [x] Add parser, TVF, join, and metadata tests; run
  `pragma/default.sqltest`; clear passing markers.

### 3. JSON and JSONB parity

- [x] Match `json_extract` arity and JSON path escaping, including mixed quoted
  keys and arrow/shift numeric coercion.
- [x] Preserve JSON numeric tokens beyond Int32/Int64 without wraparound or
  precision-changing eager conversion.
- [x] Reject JSONB trailing bytes and malformed/oversized child lengths before
  slicing or allocating.
- [x] Port `json_error_position` offsets for valid text, leading whitespace,
  arrays, complex JSON5, and binary JSONB.
- [x] Support the JSON5 set/insert notation and hexadecimal numeric forms
  accepted by Turso.
- [x] Preserve JSON subtype only within an expression; deliberately erase it
  at table/subquery materialization boundaries like SQLite.
- [x] Add malformed-input and differential tests, run
  `json/default.sqltest`, and clear passing markers.

### 4. Grouped aggregation parity and spill

- [x] Separate aggregate-call validation from runtime dispatch so nested
  aggregates and illegal `*` arguments fail with SQLite-compatible errors.
- [x] Resolve GROUP BY ordinals, aliases, function expressions, constants, and
  hidden aggregate expressions using the same bound expression graph.
- [x] Evaluate HAVING and scalar functions over finalized aggregate registers,
  including expressions that depend on multiple aggregates.
- [x] Implement DISTINCT aggregate state and deterministic group/tie ordering.
- [x] Reuse the external sorter budget for grouped state so default fixtures
  spill instead of failing the managed execution memory limit.
- [x] Carry grouped rows correctly through ORDER BY, LIMIT/OFFSET, scalar
  subqueries, CTEs, and outer grouped subqueries.
- [x] Add direct VDBE, spill, and SQLite differential tests; run the aggregate,
  groupby, orderby, offset, and subquery default files; clear passing markers.

### 5. SELECT validation and LIMIT/OFFSET coercion

- [x] Reject `*` without a FROM source, including mixed constant projections
  and scalar subqueries.
- [x] Reject `?0` and equivalent zero-index parameters while preparing.
- [x] Centralize LIMIT/OFFSET integer coercion for evaluator and VDBE paths.
- [x] Match Turso for boolean, text, arithmetic, NULL, negative, and overflow
  limit expressions.
- [x] Add parser/compiler/runtime tests, run `select/default.sqltest`, and
  clear passing markers.

### 6. Window and grouping pipeline

- [x] Rewrite each distinct window definition into its own ordered subquery
  layer, following `core/translate/window.rs`.
- [x] Push FROM, WHERE, GROUP BY, and HAVING into the innermost layer while
  retaining ORDER BY, LIMIT, and OFFSET on the outermost query.
- [x] Preserve peer order for multiple windows and `row_number`.
- [x] Evaluate aggregate window arguments from finalized grouped rows.
- [x] Reject unknown, non-window, and illegal-star window calls during
  preparation.
- [x] Add direct and differential tests, run `window/default.sqltest`, and
  clear passing markers.

### 7. Multi-table join coalescing

- [x] Preserve LEFT/INNER boundaries when constant predicates make one side
  empty or universal.
- [x] Build one coalesced USING/NATURAL column namespace across three or more
  inputs.
- [x] Keep quoted identifier resolution and output-column order stable.
- [x] Apply NULL extension after ON evaluation and before outer WHERE
  filtering.
- [x] Prevent cost-based rewrites from crossing OUTER/NATURAL/USING barriers.
- [x] Add plan and result differential tests, run `join/default.sqltest`, and
  clear passing markers.

### 8. Planner access-path depth

- [ ] Add multi-index AND intersection with rowid-set cardinality costing and
  a full-scan fallback.
- [ ] Read STAT4 samples and use histogram selectivity only when schema and
  collation metadata match.
- [ ] Build transient automatic indexes for profitable inner join inputs.
- [ ] Replace materialized durable-index probes with lazy pager/index cursors
  where a covering or rowid lookup is proven.
- [ ] Preserve deterministic plans without statistics and all existing outer
  join barriers.
- [ ] Extend EXPLAIN QUERY PLAN, selectivity tests, and bounded benchmarks for
  every new path.

### 9. Page-native MVCC depth

- [ ] Version `sqlite_schema` identities and schema generations so concurrent
  DDL can commit or conflict without exposing discarded catalog changes.
- [ ] Replace materialized table/index overlays with lazy typed dual cursors
  over base B-trees and version chains.
- [ ] Preserve statement snapshots while peer commits advance the shared
  store.
- [ ] Port Turso checkpoint phases onto managed I/O: lock, collect,
  materialize, page-WAL persist, backfill, logical-log retirement, WAL reset,
  then version GC.
- [ ] Keep recovery evidence valid at every injected crash boundary.
- [ ] Add deterministic schema-cookie, multi-connection, cursor, checkpoint,
  reopen, and oldest-snapshot GC tests.

### 10. Managed sync-engine depth

- [ ] Port the passive synced-prefix/history checkpoint policy without
  weakening ambiguous-push recovery. Blocked on the parallel page-native MVCC
  branch: Turso's `checkpoint_passive` (`sync/engine/src/database_sync_engine.rs`)
  checkpoints only the already-synced WAL prefix
  (`revert_since_wal_watermark`), which is a bounded, WAL-frame-based concept
  that only exists once the MVCC logical protocol has a real logical log/WAL
  to bound. The page protocol already has a *functionally* equivalent, tested
  safety net for its own on-disk format: `ManagedReplicaRevertWal.CaptureAndCheckpoint`
  captures a full pre-checkpoint page image before checkpointing WAL into the
  main store, so a push that turns out to conflict can still restore the exact
  pre-push database. Needed from the MVCC branch before this item can close:
  an `Ahtola.Core/Mvcc` primitive that checkpoints up to a caller-supplied WAL
  watermark while leaving frames above it (and their revert evidence) intact --
  the managed equivalent of Turso's `revert_since_wal_watermark` /
  `CheckpointMode::Passive { upper_bound_inclusive }`. No fail-closed seam was
  added speculatively ahead of that primitive existing; see the sync-engine
  session's report to the MVCC session for the exact requirement.
- [x] Complete wait-for-changes cancellation, timeout, reconnect, and revision
  ordering. Ported Turso's `wait_changes_from_remote` -> opaque staged changes
  -> `apply_changes_from_remote` split
  (`ManagedReplicaBootstrapper.WaitForRemoteChangesAsync` /
  `ApplyRemoteChangesAsync` / `ManagedReplicaStagedChanges`): waiting stages
  and validates a response without touching local state or holding any
  publication gate; applying consumes the staged result exactly once (fails
  closed on cross-replica, duplicate-apply, and disposed-result misuse) and
  throws a dedicated `ManagedReplicaStaleChangesException` -- instead of
  silently retrying a network fetch under a lease -- when local state advanced
  past the snapshot the response was negotiated against. `AhtolaConnection.SyncAsync`
  now runs push and the local apply as their own short publication windows,
  with the network long-poll for remote changes in between holding no gate at
  all, so sibling connections to the same replica keep serving local reads and
  writes for however long the remote takes to answer. Cancellation, timeout,
  and no-change (up-to-date) responses were already exercised end-to-end and
  remain covered; reconnect/redirect handling in the HTTP transport itself was
  untouched (no protocol-layer change was needed for this split).
- [x] Negotiate physical and logical protocols explicitly and qualify them
  against the pinned reference server. Already in place before this workstream
  (raw `PageUpdatesEncodingReq=0`, persisted Pages/MvccLogical detection,
  stream/apply mode parsing, LML3 logical replay) and unaffected by the
  wait/apply split, which reuses the exact same request/response parsing code.
- [x] Evaluate compressed page sets only through a pure-managed,
  NativeAOT/trim-safe dependency; otherwise keep explicit raw negotiation and
  fail closed. No pure-managed, trim-safe zstd implementation is available, so
  the existing explicit raw-encoding request and fail-closed zstd rejection
  stand as the deliberate, documented choice; the wait/apply split changes
  nothing about encoding negotiation.
- [x] Preserve sparse-bootstrap publication, encryption exclusions, conflict
  quarantine, and one push flight per physical identity. Verified unchanged
  by the full existing `ManagedReplicaPublicationRaceTests` /
  `ManagedEmbeddedReplicaPushRecoveryTests` /
  `ManagedReplicaBootstrapCatchUpDurabilityTests` suites, including the
  cross-process conflict-during-network-wait races.
- [x] Add canned-server protocol tests, every-boundary fault injection, and
  cross-process publication tests. Added direct unit tests for the new staged
  wait/apply primitives (cross-replica, duplicate-apply, disposed-result,
  stale-revision retry, no-change response, cancellation mid-long-poll) plus
  an integration test proving `SyncAsync` no longer blocks sibling reads/writes
  during the long-poll (`ManagedReplicaWaitApplySplitTests.cs`); reran the full
  existing canned-server and cross-process publication-race suites unchanged.

## Definition of done

Each workstream must cite the matching Turso source, add focused tests, run the
smallest affected managed suite through `Invoke-ManagedTestSuite.ps1`, run the
full affected sqltest file(s), remove newly passing expected-failure entries,
and keep the shipped closure pure managed, trim-safe, and NativeAOT-safe.
