# Turso gap closure roadmap

> Current execution plan: [Remaining Turso gap closure plan](turso-remaining-gap-plan.md).
> The 2026-09-06 baseline at `83bb892` has 100 expected-failure entries:
> 82 parity candidates, 17 deliberate extensions, and one CLI-only diagnostic.
> Counts and completed waves below are historical, not a current zero-gap claim.

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
| 8 | `planner-access-path-depth` | Access-path depth completed | `core/translate/optimizer/`, `planner.rs`, `main_loop/` | Done |
| 9 | `mvcc-page-native-depth` | Documented runtime limit | `core/mvcc/` | Done |
| 10 | `sync-engine-depth` | Split wait/apply lifecycle and crash-safe checkpoint policy | `sync/engine/src/` | Done |
| 11 | `corpus-refresh-v0.8.0-pre.7` | 51 new files vendored, 30 refreshed, matrix runner added | `sqlite/conformance/sqlite-sqltests/`, `testing/sqltest/src/` | Done |
| 12 | `upstream-delta-correctness` | ~180 newly vendored cases closed by engine fixes | `core/vdbe/value.rs`, `sqlite/parser/src/parser.rs`, `core/translate/select.rs` | Done |
| 13 | `eqp-format-json` | Parser clause + plan_json envelope | `core/translate/eqp.rs`, `docs/eqp-json.md` | Done |
| 14 | `get-set-byte-json-object-star` | Built-in `get_byte`/`set_byte` + `json_object(*)` | `turso-sqltests/get-set-byte.sqltest`, `json_object_star.sqltest` | Done |
| 15 | `regexp-family` | Built-in regexp + operator without registration; corpus 19/19 | `core/regexp.rs`, `extensions/regexp`, `turso-sqltests/regexp.sqltest` | Done |
| 16 | `time-family` | Full sqlean time_*/dur_* family; corpus 34/34 | `core/time/`, `turso-sqltests/time.sqltest` | Done |
| 17 | `turso-sqltests-adoption` | regexp/sequence/time/vector/without_rowid vendored into turso/ | `turso-sqltests/` | Done |
| 18 | `eqp-json-op-objects` | Structured per-node op objects not yet modeled | `core/translate/eqp.rs` | Open |

The sqltest counts overlap by subsystem only in implementation, not in this
classification: ranks 1-7 account for 133 distinct expected-failure entries.

## Progress

- 2026-09-06: **upstream-delta continuation wave** (17 more vendored cases closed):
  - `timediff(A, B)` now produces a modifier that turns B into A using SQLite's
    directional month arithmetic: forward month adds clamp a day-of-month overflow
    forward (Jan 31 + 1 month = Mar 2), while a negative diff is anchored on the
    later date and walked backwards — the asymmetry upstream issue #8242 pins. All
    12 `timediff-negative-month-roundtrip.sqltest` cases pass.
  - EXISTS subqueries drop ORDER BY and DISTINCT (name-resolution still runs on the
    dropped terms; LIMIT/OFFSET stay, since OFFSET decides whether a row comes out
    at all): the three `exists-drops-order-by-distinct.sqltest` gaps close.
  - A WINDOW-clause definition with a base that is defined nowhere errors
    (`no such window: X`), while a forward reference (a base defined later in the
    same clause) is silently ignored — SQLite's resolution order, matching all
    14 named-window-chain cases.
  - UPDATE ... RETURNING sees the row after the write but before the AFTER trigger
    fires (the returning.sqltest after-trigger cases; two prior tests asserting the
    old post-trigger behavior updated to the SQLite-cross-checked semantics).
  - A DELETE resets the conflict-policy override to ABORT for the triggers it
    fires (SQLite's OE_Default row-delete coding), so an outer UPDATE OR REPLACE no
    longer propagates through a DELETE into its trigger's plain INSERT, while an
    explicit OR clause on the inner statement still wins.
  - TEMP triggers fire before the table's own triggers and, among themselves, in
    creation order (the main schema's own list stays newest-first), matching
    temp-trigger-fires-before-main.sqltest.
  - `WITH cte1(x) AS ... SELECT * FROM cte1(7)` rejects with `'cte1' is not a
    function` (parser CTE-scope tracking; the plain-table variant needs a plan-time
    catalog check and remains an expected-failure).
- 2026-09-06: **sqlean extension ports completed.**
  - The regexp family is now built-in: `regexp(pattern, source)` follows
    `core/regexp.rs`'s `to_text_coerced` semantics (integers/reals as text, blobs as
    UTF-8 bytes, NULL as NULL, invalid patterns as NULL, wrong arity as an error),
    the `X REGEXP Y` operator works without user registration, and the
    `extensions/regexp` family (`regexp_like`, `regexp_substr`, `regexp_replace`
    first-match with `$N` group expansion, `regexp_capture` with an optional group
    index) is registered. The vendored `turso/regexp.sqltest` passes 19/19.
  - The full sqlean time family (~45 functions) is ported in
    `EmbeddedDatabase.TimeFunctions.cs`: a `SqleanTime` value with the documented
    13-byte blob layout (version, 8 big-endian seconds since 0001-01-01, 4 big-endian
    nanoseconds) and civil-calendar arithmetic (Howard Hinnant's
    days_from_civil/civil_from_days) so the corpus's BCE years round-trip;
    time_now/make_date/make_timestamp; the time_get family (named getters return
    INTEGER whole seconds per `get_second()`, the two-argument `time_get(t,
    'second')` carries the nanosecond fraction as REAL); time_unix/to_timestamp/
    time_milli/micro/nano and time_to_*; comparisons; the `dur_*` constants;
    time_add/time_add_date (chrono-style whole-month moves with day clamping)/
    time_sub/since/until; time_trunc (field form incl. the corpus's ISO-week
    Jan-1+(week-1)*7 semantics, duration form) and time_round (nearest-multiple,
    ties away from zero, via decimal to avoid seconds*1e9 overflow); time_fmt_* with
    optional UTC offsets; time_parse (RFC 3339, naive datetime, date, time forms).
    The vendored `turso/time.sqltest` passes 34/34.
  - The supported `turso-sqltests` subset is vendored under
    `conformance/sqlite-sqltests/turso/`: regexp (19/19), sequence (23/23), time
    (34/34), vector (21/22 — the one difference is Turso's CLI "Parse error:"
    display prefix, recorded as an expected-failure), without_rowid (3/11 — the
    failing cases pin Turso's insert-only WITHOUT ROWID restrictions that Ahtola
    deliberately exceeds: secondary UNIQUE/INDEX, UPDATE, DELETE, INSERT OR
    REPLACE, UPSERT, and FOREIGN KEY are all supported extensions, recorded as
    intentional expected-failures). The AUTOINCREMENT-on-WITHOUT-ROWID message was
    aligned to upstream ("is not allowed").
- 2026-09-05: **corpus refresh to the v0.8.0-pre.7 pin.** The vendored
  `conformance/sqlite-sqltests/` corpus was stale at the v0.7.2 vintage while
  `turso-src/` had moved to v0.8.0-pre.7 (`277ddd050`). Vendored the 51
  post-v0.7.2 files (~707 test blocks: the window-frame matrix suite,
  `recursive-cte` (118), `unnest-correlated` (90), `json-valid-strict` (101),
  error-message/affinity/trigger regression files) and refreshed the 30 drifted
  files (90 net-new tests). Added the upstream `@var`/`matrix` grammar to the
  managed sqltest parser with a differential SQLite oracle
  (`SqltestMatrixOracle` over Microsoft.Data.Sqlite, mirroring
  `matrix_oracle.rs`): every matrix expansion runs against bundled SQLite and
  the managed engine must agree. Fixed the slug/dedup semantics so operator
  values (`=`, `<=`) that slug identically get `~N` suffixes.
- 2026-09-05: **upstream-delta correctness wave** (~180 newly vendored cases
  closed by engine fixes): `char()` now coerces every argument to an integer
  codepoint (text/blob numeric prefix, real truncation, NULL as 0) per
  `exec_char` in `core/vdbe/value.rs`; `IN ((SELECT ...))` is subquery
  membership, not a one-element value list (parser.rs
  `is_bare_subquery`); ORDER BY/GROUP BY ordinals only reference a column when
  the literal fits an i32 (select.rs `resolve_order_by_or_group_by_expr`),
  so `ORDER BY 6641019685895816357` is a constant and `ORDER BY -1` errors;
  compound ORDER BY before an operator reports
  "ORDER BY clause should come after UNION not before"; missing
  qualified tables report the user-written name (`no such table: main.nosuch`)
  for SELECT/DML/DDL/CREATE INDEX/CREATE TRIGGER, with pre-routing validation
  that respects transaction-local catalogs, temp virtual tables, and
  `no such database` precedence for unknown schemas; "already exists"
  collision errors echo the user's written quoting
  (`table "t""q" already exists`) and view/table cross-namespace clashes use
  SQLite's "view v already exists" / "there is already a table named t"
  spellings; CHECK-constraint failures dequote the leading quoted token and
  carry no `(19)` shell suffix (matching sqlite3_errmsg); `printf` precision
  cap raised to 1,000,000 (upstream `MAX_WIDTH`) for the overflow-payload
  fixtures; `PRAGMA count_changes` implemented (DML returns one changes row);
  `pragma_function_list` now lists `percent_rank`/`cume_dist`;
  `ANALYZE sqlite_schema` re-analyzes main.
- 2026-09-05: **new upstream extensions**: `EXPLAIN QUERY PLAN FORMAT=JSON`
  (case-insensitive clause, `plan_json` row with the documented
  version/sql/result_columns/nodes envelope; the structured `op` objects are a
  documented follow-up), built-in `get_byte`/`set_byte`
  (PostgreSQL-parity byte access with wrap semantics and range errors), and
  `json_object(*)`/`jsonb_object(*)` star expansion over the current FROM row.
- 2026-09-05: expected-failures baseline regenerated for the refreshed
  corpus: 177 entries. The newly exposed remaining gaps are concentrated in
  json_valid strict flags (45), recursive-CTE depth semantics (36), the
  window frame matrix edge families (23), `unnest` correlated-plan EQP shapes
  (21), timediff negative-month asymmetry (8), DELETE/UPDATE LIMIT
  rejections (8 — intentional extension, documented), plus scattered
  smaller clusters (partial-index EQP labels, aggregate-of-outer-column,
  ALTER default negate overflow, SUM inverse-mode persistence). The
  sqlean-time family, the sqlean regexp extension functions, and adopting
  the supported subset of `turso-sqltests/` remain open (rank 15).
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
- 2026-09-01: removed the final in-process harness exclusion. Cancellable
  evaluator joins now poll every nested candidate loop, and deterministic
  numeric arithmetic equalities can use sound expression hash probes without
  caching registered functions or bypassing custom collations. The
  10,000-row `join/default.sqltest::four-way-inner-join` case and the complete
  join corpus now run inside the managed timeout.
- 2026-09-01: began the next VDBE-depth wave with parent
  `ProgramInstruction` IGNORE control flow/shared transaction state, a narrow
  AFTER INSERT/UPDATE/DELETE trigger program route, bounded spillable
  DISTINCT/compound keyed sets, page-native bounded record-column reads,
  read-only file-backed incremental-BLOB handles over pinned pager/transaction
  views, and `AggInverse` streaming for current-row and one-preceding
  COUNT/SUM/AVG windows.
- 2026-09-01: extended `AggInverse` streaming to `ROWS n PRECEDING … CURRENT ROW`
  for n ≤ 1024 (COUNT/SUM/AVG). The builder keeps a departing-argument ring and
  skip counter; 1 PRECEDING bytecode is unchanged. RANGE/GROUPS, FOLLOWING,
  EXCLUDE, and non-invertible aggregates stay on the buffered evaluator.
  Page-native incremental-BLOB writes overwrite leaf/overflow payload in place
  for autocommit file-backed rowid tables.
- 2026-09-01: deferred VDBE opcodes 137–144 (`BlobRead`/`BlobWrite`/`BlobLen`,
  `ColumnRange`, `OpenPseudo`, `TypeCheck`, `Once`, `ResetOnce`) now have
  validation, execution, and EXPLAIN. BEFORE INSERT/UPDATE/DELETE leaf bodies
  route through `Program` with `ColumnRange` image capture; STRICT INSERT emits
  `TypeCheck`. Distinct worktables spill through `VdbeKeyedRowStore`; window
  buffers fail closed against the statement memory budget.
- 2026-09-05: recursive-worktable frontiers now spill through the managed temporary
  file system. `WorkTableRuntime` retains buffered frontier rows against the
  statement memory budget and, once the budget can no longer hold the queue,
  drains it to a `worktable-frontier` spill file (a new `VdbeSpillFileKind`);
  FIFO order is append order, so dequeues drain the buffered prefix and then
  read records sequentially. `TryPeek` spans the spill boundary for
  generation-at-a-time recursion, and the per-generation buffer is retained
  and fails closed (the transform contract takes in-memory rows). Distinct
  worktables therefore coexist with two spill stores (seen set + frontier) in
  one statement. New `WorkTableFrontiersSpilled` metric. Coverage:
  `RecursiveWorkTableOpcodeExecutionTests` spill cases (binary-tree level order
  across the boundary, interleaved fan-out, distinct coexistence, and
  no-spill fail-closed with `AllowTemporaryFileSpill=false`).
  Also verified the earlier "window buffers fail closed" claim was superseded:
  buffered window partitions already spill (`WindowBufferRuntime.EnsureSpilled`,
  2026-09-01, indexed Compute reads), and the README working-set matrix now
  says so. Remaining non-spilling structures: evaluator non-equijoin build
  sides and ephemeral tables (deliberately fail-closed); an aggregate's own
  accumulator is a single bounded value per group, so there is no engine-owned
  growing aggregate state to spill.
- 2026-09-05: ephemeral tables spill too — the last VDBE-side fail-closed
  structure. `EphemeralTableRuntime` keeps its full access contract
  (sequential scan, positional delete, key-prefix delete/contains, ResetSorter
  clears, cursor-source rowids) across a spill transition: rows flush to an
  `ephemeral-table` append-log of (slot, rowid, values) records with an
  `ephemeral-index` slot→offset file (the keyed-row-set layout), and a compact
  live-slot list preserves positional semantics afterwards (one reference per
  live row, itself retained against the budget). The lazy
  `SpilledEphemeralRowList`/`RowIdList` views give `VdbeCursorSource` indexed
  reads without reloading the table. New `EphemeralTablesSpilled` metric.
  Coverage: `VdbeEphemeralTableOpcodeTests` spill case (order, values, metrics,
  file lifecycle). With this, every VDBE execution intermediate either spills
  or is a bounded per-group/per-row value; the only non-spilling remainder is
  the evaluator's materializing nested-loop join, which is an architectural
  property of the evaluator, not a missing spill path.
- 2026-09-01: appended `ChangeCount` (opcode 145). Streaming `AggInverse` now
  covers `ROWS CURRENT ROW … m FOLLOWING` and `ROWS n PRECEDING … m FOLLOWING`
  (n,m ≤ 1024). Ephemeral tables fail closed against the statement memory
  budget. Streaming now also covers default RANGE/GROUPS UNBOUNDED PRECEDING
  … CURRENT ROW and RANGE/GROUPS CURRENT ROW peer frames via an ephemeral
  delay buffer. Streaming `GROUPS n PRECEDING … CURRENT ROW` inverses each
  departing peer group (n ≤ 1024). Window-buffer scanned rows spill to a
  temp file under the statement memory budget and reload for Compute.
  Streaming `RANGE n PRECEDING … CURRENT ROW` (single ORDER BY key, n ≤ 1024)
  keeps a history ephemeral of in-frame prior groups and compact/inverses with
  `Compare` + `AggInverse` (ASC: `row + n >= current`; DESC subtracts and
  flips to `<=`). Streaming `GROUPS CURRENT ROW … m FOLLOWING` delays emit
  until m later peer groups exist (m ≤ 1024). Streaming
  `RANGE CURRENT ROW … n FOLLOWING` (single ORDER BY key) delays emit until the
  next ORDER BY value falls outside the offset, then flushes/inverses the
  oldest queued group. Streaming RANGE/GROUPS `CURRENT ROW … UNBOUNDED FOLLOWING`
  drains the queued groups at partition end with inverse; `UNBOUNDED PRECEDING …
  UNBOUNDED FOLLOWING` emits the full-partition aggregate on every row.
  Streaming ROWS `CURRENT ROW`/`UNBOUNDED PRECEDING` to `UNBOUNDED FOLLOWING`
  uses a delay ephemeral and drains at partition end. ROWS running
  `EXCLUDE CURRENT ROW` emits before AggStep. MIN/MAX on moving frames stream
  through a value-bag inverse. Window-buffer Compute reads spilled rows by index
  instead of reloading the partition. EXCLUDE GROUP/TIES stream on running and
  current-peer frames (per-row inverse for TIES). FILTER on non-moving frames
  streams via FilterRegisters. row_number, rank, and dense_rank stream as
  COUNT-style accumulators. first_value/last_value, lag(offset ≤ 1024), and
  group_concat with a literal separator, and scan-evaluable computed arguments
  stream. lead and nth_value stream; FILTER on moving frames skips AggInverse
  when the predicate is false. group_concat-style list aggregates inverse via
  a tuple queue. percent_rank/cume_dist/ntile stream via a full-partition
  drain. ROWS n PRECEDING AND m PRECEDING streams through a delayed-step
  wrapper. RANGE/GROUPS n PRECEDING AND m FOLLOWING and non-integer RANGE
  offsets stream via a full-partition drain that re-folds in-range rows on
  Finalize. Matching-spec `row_number` plus a ROWS running/current aggregate
  streams; extra/missing top ORDER BY re-sorts projected ResultRows. Distinct
  OVER specs, joins/GROUP BY/compounds, and DISTINCT still use OpenWindowBuffer
  or the evaluator.
- 2026-08-29: closed `planner-access-path-depth` with costed AND intersections,
  validated STAT4 selectivity, automatic covering indexes, and direct durable
  index-btree seeks.
- 2026-08-30: completed planner depth beyond the original workstream: the
  System-R subset DP now matches Turso's 12-member threshold and deterministic
  greedy planning covers SQLite's 64-table limit. Direct pager seeks now cover
  transaction-local and MVCC overlays, `WITHOUT ROWID` primary/secondary
  indexes, partial/expression indexes, and validated connection-bound custom
  collations without changing SQLite record bytes.
- 2026-08-29: closed `mvcc-page-native-depth` with generation-scoped schema
  identities, lazy typed cursors, crash-ordered checkpointing, recovery
  watermarks, and reader-generation-aware GC.
- 2026-08-29: closed `sync-engine-depth` with a split wait/apply lifecycle,
  bounded stale-base retries, one-shot staged changes, and crash-safe
  page-replacement checkpoint evidence.
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

- [x] Add multi-index AND intersection with rowid-set cardinality costing and
  a full-scan fallback.
- [x] Read STAT4 samples and use histogram selectivity only when schema and
  collation metadata match.
- [x] Build transient automatic indexes for profitable inner join inputs.
- [x] Replace materialized durable-index probes with lazy pager/index cursors
  where a covering or rowid lookup is proven.
- [x] Match Turso's 12-member subset-DP threshold and retain deterministic
  greedy planning through SQLite's 64-table join limit.
- [x] Extend direct access to transaction-local and MVCC mutation overlays,
  `WITHOUT ROWID` primary/secondary b-trees, and safe partial/expression-index
  predicates.
- [x] Support connection-bound custom collations in secondary indexes with
  generation/version validation and targeted `REINDEX`, without changing
  SQLite index record bytes.
- [x] Preserve deterministic plans without statistics and all existing outer
  join barriers.
- [x] Extend EXPLAIN QUERY PLAN, selectivity tests, and bounded benchmarks for
  every new path.

The managed planner mirrors Turso's `multi_index.rs` row-set costing and
automatic-index eligibility, while keeping LEFT/RIGHT/FULL, NATURAL, and USING
subtrees opaque. Eligible committed, classic-transaction, and MVCC paths seek
the pinned SQLite index b-tree directly and merge ordered local/version effects
without rebuilding the complete index. The same access contract covers rowid
and `WITHOUT ROWID` tables plus safe partial/expression indexes. Custom
collations remain runtime callbacks: only their names are stored in SQLite
schema SQL, physical order is generation/version validated, and targeted
`REINDEX` repairs a stale tree while preserving unrelated unavailable custom
indexes byte-for-byte. The pinned Turso release has no STAT4 reader, so Ahtola's
histogram extension validates the standard `sqlite_stat4` schema, current
`sqlite_stat1` row count, collation metadata, and sample record before using it.

### 9. Page-native MVCC depth

- [x] Version `sqlite_schema` identities and schema generations so concurrent
  DDL can commit or conflict without exposing discarded catalog changes.
- [x] Replace materialized table/index overlays with lazy typed dual cursors
  over base B-trees and version chains.
- [x] Preserve statement snapshots while peer commits advance the shared
  store.
- [x] Port Turso checkpoint phases onto managed I/O: lock, collect,
  materialize, page-WAL persist, backfill, recovery-watermark publication,
  logical-log retirement, WAL reset, then version GC.
- [x] Keep recovery evidence valid at every injected crash boundary.
- [x] Add deterministic schema-cookie, multi-connection, cursor, checkpoint,
  reopen, and oldest-snapshot GC tests.

The managed port follows the pinned Turso `v0.8.0-pre.7` implementation:
`core/mvcc/database/mod.rs` (`MVTableId`, schema-generation begin/commit
checks, low-water mark GC), `core/mvcc/cursor.rs` (`MvccLazyCursor` and
two-peek table/index merging), and
`core/mvcc/database/checkpoint_state_machine.rs` (publish, backfill,
logical-log retirement, WAL reset, then GC). Managed I/O completes each phase
synchronously. A pager-first schema publish and pre-DDL retirement checkpoint
keep discarded catalogs out of the logical log, while inclusive checkpoint
watermark frames make every crash boundary replay-safe.

### 10. Managed sync-engine depth

- [x] Port the passive synced-prefix/history checkpoint policy without
  weakening ambiguous-push recovery. Turso's page-stream apply protects its
  revert database by passively backfilling the synced WAL prefix before replay.
  Ahtola's page protocol uses the stronger format-appropriate
  `ManagedReplicaRevertWal.CaptureAndCheckpoint`: it publishes a complete
  pre-checkpoint page image before folding WAL into the main store, then keeps
  that recovery bundle through ambiguous push outcomes. The Core-only
  `SqliteWalWriterCheckpointCoordinator.CheckpointPassiveValidated` also
  exposes an inclusive safe backfill watermark bound to WAL salts and the
  WAL-index change counter for callers that need non-resetting passive evidence.
  The MVCC-logical path does not use Turso's separate revert-WAL page replay and
  therefore does not invent a page-frame watermark for logical transactions.
- [x] Complete wait-for-changes cancellation, timeout, reconnect, and revision
  ordering. Ported Turso's `wait_changes_from_remote` -> opaque staged changes
  -> `apply_changes_from_remote` split
  (`ManagedReplicaBootstrapper.WaitForRemoteChangesAsync` /
  `ApplyRemoteChangesAsync` / `ManagedReplicaStagedChanges`): waiting stages
  and validates a response without touching local state or holding any
  publication gate; applying consumes the staged result exactly once (fails
  closed on cross-replica, duplicate-apply, and disposed-result misuse) and
  throws a dedicated `ManagedReplicaStaleChangesException` when the remote-facing
  base advanced past the snapshot the response was negotiated against.
  Local-only journal advancement is rebased onto the staged response without
  another pull; genuinely stale bases retry with a finite backoff and bound.
  `AhtolaConnection.SyncAsync`
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

- 2026-09-06: **json conformance wave** (the 45-case json-valid-strict cluster
  closed, 152 expected-failures remain):
  - Two-argument `json_valid(X, Y)`: Y is a bitmask (1 = strict RFC 8259 text,
    2 = JSON5 text, 4 = blob that superficially looks like JSONB, 8 = blob that
    is fully valid JSONB) and X is valid when any selected check passes. The
    FLAGS argument coerces like sqlite3_value_int (numeric conversion, text and
    blobs take their leading integer prefix), and out-of-range flags error with
    SQLite's exact message. The parser now tracks which constructs were JSON5
    (unquoted keys, single quotes, trailing commas, comments, hex numbers,
    leading `+`/`.` and trailing `.`, Infinity/NaN, `\x`/`\v`/`\0` escapes, raw
    control bytes, line continuations), and the marking survives later standard
    escapes exactly like upstream's TEXT5/TEXTJ element-type promotion.
  - A byte-faithful port of SQLite's jsonbValidityCheck drives flags 4/8 and
    json_error_position on JSONB blobs: shallow outer-header classification with
    the ambiguous small `{`/`[`/digit-blob fallback to strict validation,
    32-bit nine-byte header reads, bare-header requirements for NULL/TRUE/FALSE,
    digit/hex/canonical-float payload checks, TEXTJ/TEXT5 escape validation
    including the strchr backslash-NUL bug-compatibility, line continuations,
    the blind \u conversion after a continuation, and the 1000-deep nesting
    limit. Non-JSONB blobs and text validate as before, and document parsing
    now stops at the first embedded NUL like SQLite.
  - JSON5 leniencies: numbers need a mantissa digit with SQLite's error
    positions (bare dot at the dot, signed just after the dot, exponent-less
    forms at the number start, repeated dots/exponents at the offending
    character); `\` + U+2028/U+2029 line continuations; unquoted keys follow
    SQLite's identifier rules (letters/`_`/`$` start, digits continue,
    non-ASCII accepted) with `\uXXXX` escapes kept verbatim in the rendered key
    while matching decodes them and every other escape rejected; comments after
    a key act as whitespace; the vertical-tab escape renders through
    `\u0009` (SQLite through 3.51.1 bug-compatibility) while extraction still
    yields 0x0B.
  - JSONB TEXT5 payloads store verbatim JSON5 escapes (hex(jsonb('"\x41\n"'))
    is a TEXT5 element with the raw source), and TEXT5 payloads re-read through
    a dedicated decoder so json(jsonb(...)) round-trips.
  - `subtype(X)` builtin: 74 for text carrying the JSON subtype, 0 for
    everything else including JSONB blobs.
  - Bad-path messages use SQLite's %Q: apostrophes doubled, embedded NUL ends
    the message.

- 2026-09-06 (continued): **compound ORDER BY + hex-negation overflow wave**
  (6 more vendored cases closed, 146 expected-failures remain):
  - A compound SELECT's ORDER BY term now matches an arm's projection
    structurally (function calls like `upper(b)`, arithmetic, and qualified
    references against an aliased output), porting Turso's
    resolve_compound_order_by_expr + exprs_are_equivalent (select.rs/util.rs):
    identifiers compare case-insensitively and commutative binary operators
    match with sides swapped.
  - A negated hex literal folds at parse time like SQLite's codeInteger:
    `-0xNNNN` becomes the negated integer while the magnitude fits, and
    `-0x8000000000000000` is the prepare-time "hex literal too big" error.
    The ALTER TABLE ADD COLUMN backfill still promotes the negation to the
    REAL 2^63 (upstream eval_constant_default_value), while INSERT
    code-generation and direct SELECT hit the error (upstream issue #4621).

- 2026-09-06 (continued): **partial-index covering wave** (5 vendored cases
  closed, 141 expected-failures remain):
  - A partial index covers a query whose WHERE contains the partial predicate
    verbatim: the implied term is dropped from the key-only check (SQLite's
    whereLoopAddBtree pPartIdxWhere handling), so `EXPLAIN QUERY PLAN` reports
    USING COVERING INDEX for both SEARCH and SCAN shapes.
  - A reference to the table's rowid-alias column under its declared name (e.g.
    `id` for `id INTEGER PRIMARY KEY`) is always satisfiable from an index
    entry, closing the plain covering gap that also affected non-partial
    indexes.
  - `INSERT INTO t AS z ...` parses SQLite's target alias (used to qualify
    UPSERT DO UPDATE references); the plain statement accepts and ignores it.
  - Two EQP tests that pinned the pre-fix non-covering wording were updated to
    the SQLite-verified COVERING plans.

- 2026-09-06 (continued): **recursive-CTE LIMIT/OFFSET wave** (12 vendored
  cases closed, 129 expected-failures remain):
  - A recursive CTE's own LIMIT/OFFSET now bounds the recursion the way
    SQLite's co-routine does (recursive_cte.rs init_limit/emit_offset): a
    literal LIMIT 0 skips everything (the anchor is never evaluated, so its
    errors never surface), a negative limit is unlimited, expressions evaluate
    once up front, OFFSET rows are dropped from the output while still feeding
    the recursive step, and the limit counter stops the expansion the moment
    enough rows have been emitted. The emit budget combines with (and caps at)
    the outer query's row budget.
  - Closed: limit, limit-offset, zero-limit x2, expression-limit-and-offset,
    fuzz-120717, left-join-on-clause x2, order-with-limit-stops-infinite, and
    the correlated limit/offset family.
  - Remaining in the cluster: ORDER BY priority-queue semantics (7), nested-CTE
    self-reference rules (7), and scattered singles (10).
