# Remaining Turso gap closure plan

## v0.8.1 refresh (2026-09-30)

The `turso-src` pin moved to `v0.8.1` (`8549c1659`), and the vendored corpus
was refreshed to match: 33 new and 41 changed sqltest files. The refresh
exposed 113 new case-level differences. The 0.8 parity work closed 80 of them,
plus two older generated-column integrity markers. The ledger now holds
**109** entries: 76 from earlier baselines and 33 from this refresh.

### Delivered

- **MVCC correctness.** A `BEGIN CONCURRENT` writer whose snapshot predates a
  peer's committed update or delete now gets a write-write conflict. Before
  this, it could overwrite the peer or bring back a deleted row (Turso
  `0f7f30eac`). Explicit rowids and INTEGER PRIMARY KEY values are no longer
  rewritten by the store-global allocator. Only rowids the statement chose
  itself are promoted, and two concurrent explicit inserts of one key conflict.
- **SQL surface.**
  - `CROSS JOIN ... ON/USING`, and a comma join followed by `ON`, now parse.
    CROSS JOIN no longer allows the joins to be reordered.
  - Correlated IN/EXISTS after a FULL JOIN now runs instead of being rejected.
  - Generated columns: REPLACE and DEFAULT values are applied before virtual
    columns are computed, and invalid clauses are rejected.
  - UPSERT honours the target alias. A BEFORE UPDATE trigger now refreshes the
    columns the UPSERT does not SET.
  - Internal tables are protected whatever their letter case.
  - The 2,000-column limit is enforced.
  - Parameter names follow SQLite's rules: a bare `$` is rejected, and
    TCL-style `$::v` and `$a(b)` names are accepted.
  - `PRAGMA count_changes` behaves like SQLite.
  - Smaller fixes: renaming a table onto an existing view, the
    temp.synchronous value, and NULL arguments to `generate_series`.
- **Functions.**
  - `json_group_object` skips NULL labels, and `jsonb(NULL)` returns NULL.
  - `json_each` and `json_tree` expose a `rowid`.
  - Date/time functions read BLOB arguments as text, and round correctly just
    before the Unix epoch.
  - `min`/`max` break ties the way SQLite does.
  - A COUNT on a NOT NULL column uses the simple-count plan.
- **Planner (Turso 0.8 cost model, `TursoCostModel.cs`).**
  - Correlated IN/EXISTS with non-equality comparisons is unnested, and the
    inner side is costed with Turso's semi/anti-join model. NOT EXISTS can use
    a hash anti-join.
  - IN-list and OR-implied IN index searches; multi-index OR with AND branches.
  - A disjunct of a partial index's predicate can prove that index usable.
  - Compiled joins are costed with Turso's join planner. This includes
    left-deep hash joins and materialized build prefixes.
  - EQP output describes the join tree that actually executes, and FORMAT=JSON
    reports the cost model's real estimates.
  - ANALYZE row estimates are reported for unfiltered scans.
- **Storage.**
  - `PRAGMA max_page_count` is enforced on file-backed and `:memory:`
    databases, failing with `database or disk is full` (code 13).
  - `page_count` is exact; for `:memory:` it is computed from a fresh page
    image built by the same page builders the engine uses to write the file.
  - integrity_check reports STRICT storage-class violations and virtual
    generated-column violations.
  - Explicit checkpoints are refused while a statement is still running on
    the same connection.
  - WAL readers can use read-marks 1–4 while a checkpoint holds read-mark 0.
  - auto_vacuum reports the mode stored in page 1.
  - MVCC ALTER COLUMN rejects changes that would require rebuilding an index.
  - FTS converts non-text queries to text.
- **Error codes.** SQLite primary and extended result codes now reach
  `SqliteException`, on both local and remote connections: constraint
  19/2067/1299/275/1555/787, full 13, notadb 26, busy 5, readonly 8.
- **Bindings (Turso 0.8 `bindings/dotnet`).**
  - 13 replica connection-string keys.
  - Explicit `Pull`, `Push`, `Checkpoint` and `GetSyncStatistics` calls.
  - An automatic-sync status property and change event, plus an opt-in
    `PullOnly` mode (the default still pushes and pulls).
  - An auth-token provider hook for rotating tokens.
  - Unexpected `replication_index` values are tolerated instead of throwing.

### Remaining v0.8.1 differences (33)

- **Planner shapes the evaluator route cannot honestly report (19):** in
  join/memory, hash_join_order_by and multi_index_or_compound, plus one
  shared-CTE JSON case. The corpus harness runs with a cancellation token,
  which sends non-aggregate SELECTs through the evaluator. The evaluator always
  hashes the right input. It has no build-left join, no materialized-prefix
  join and no reordering. Other remaining cases need Turso's
  1,000,000-row default for tables with no ANALYZE statistics, or per-step
  JSON estimates passed through to the executed plan. Printing Turso's plan
  for any of these would describe work that does not run.
- **Documented policies (14):**
  - Eight `turso/alter_column` cases differ only because a `:memory:`
    database keeps journal_mode=memory. Every later row was verified to match.
  - Two MVCC ALTER COLUMN rejections differ only by the CLI's `Parse error:`
    prefix.
  - Schema text keeps SQLite's verbatim `COLLATE 'fr-FR'` quoting.
  - VACUUM under query_only reports SQLite's readonly error.
  - Managed ATTACH requires a file-backed primary database.
  - One planner case would print `COVERING` for a seek that does read table
    rows, so it is left as is.

### Not ported

- The PostgreSQL frontend, general typed values and incremental materialized
  views remain separate product decisions (see below).
- Replica connection pooling, the standalone Serverless/Platform client
  packages, `Force Logical MVCC Pull=True`, and per-step batch statistics.

### Known follow-ups

- Fixed: a single-table GROUP BY over the 10,000-row default fixture took
  25–40 s, which also predated the refresh (`4d17cd2`). The sort spills
  under the default 2 MB execution budget, and the spill codec issued two or
  three unbuffered file operations for every value it wrote or read. The
  sorter's spill file now coalesces writes, and each run reader reads ahead.
  Both buffers scale with the memory limit and are charged to it. The same
  query now takes under a second, and `groupby/default.sqltest` runs in
  about 80 s for the whole file, down from about 15 minutes.
- Fixed: a spilled 3,000 × 3,000 equi-join took about 32 s. Resident build
  partitions starved probe-batch admission, so batches held one probe and each
  probe reloaded its partition. About half of those loads ran out of memory
  part-way and started again, and every read went one value at a time.
  Now a probe batch evicts cached partitions to keep growing, and groups are
  answered resident-first. A load first makes room for its partition's
  recorded entry retention, and skips a load that cannot fit. Each sequential
  partition scan reads through a charged read-ahead block. The join now takes
  about 0.4 s, and 10,000 × 10,000 takes about 2.4 s. Each probe batch still
  re-reads the partitions it touches, because output keeps probe order.
  Window and keyed-row spills still use unbuffered I/O.
- INDEXED BY / NOT INDEXED should rule out hash joins and ephemeral indexes,
  as in Turso.
- At page size 1024, large index keys pack less tightly than in SQLite, which
  shows in `page_count`.

## Baseline and scope

Prepared 2026-09-06 against Ahtola `83bb892` and the read-only Turso
`v0.8.0-pre.7` pin, `277ddd050`.

**Current status (2026-09-25; code verified through `6765675` in PR #71).** The tracked
expected-failures file has **73 case-level differences** (verified against the
file), not 73 missing features; the detailed grouping and live TODOs are
below. The older integration checkpoints and worker/first-wave instructions
record how this branch was assembled, not work that should be restarted.
This PR does **not** claim full Turso parity.

At the baseline, the expected-failures file contained 100 entries: 82 engine, planner, or
diagnostic parity candidates; 17 deliberate extensions (8 DML LIMIT, 2 STORED
generated columns, 7 WITHOUT ROWID); and 1 Turso CLI error-prefix difference.
These are coverage entries, not independent defects. The historical inventory's
217 closed records and its two-failure metadata are not the current backlog.

Baseline discovery reports 11,077 cases: 10,959 runnable, 85 skipped by the
corpus/backend policy, and 33 unsupported by the harness. These are discovery
counts, not evidence that every runnable case has passed in this execution.

Implement the remaining parity and architectural work without removing the
deliberate extensions. Native companions, loadable native extensions, and raw
sqlite3 handles remain excluded. Restricted experimental STRICT INTEGER
DOMAIN and identity TYPE slices are delivered; general typed values and
incremental materialized views remain separate product-adoption projects.
Do not enable their full capabilities based on a historical closed record.
Zstd rejection is not a missing feature relative to this pin; both engines
reject it.

## Historical worker and integration policy

- Every implementation worker uses Claude Sonnet 5 (`claude-sonnet-5`), high
  reasoning effort, and the long-context tier requested for 1M context.
- Use isolated worktree sessions, with at most three implementation workers
  active initially. A fourth, harness-only coverage worker can proceed without
  changing production engine files. Each owns one cohesive workstream and
  commits its result.
- Base the first wave on the analyzed integration branch, not the stale local
  master ref. Subsequent workers start from integrated prerequisite commits.
- The coordinator owns this plan, historical inventory reconciliation, integration
  order, and final aggregate coverage. Workers may remove only their proven
  passing expected-failure entries; do not regenerate the entire baseline.
- Shared files, especially EmbeddedDatabase.cs and SqlParser.cs, are integrated
  serially. Resolve conflicts semantically and rerun affected coverage after
  integration; do not select one worker's whole file over another's.
- Workers report commit IDs, exact closed case IDs, executed validation commands
  and counts, upstream citations, remaining blockers, and any new failures.
- Concurrent heavy validation uses two shared process leases through a
  session-local wrapper around the existing commands. Workers acquire/release
  leases automatically instead of waiting for chat approvals, which can arrive
  after an active turn. No new test framework or repository build entrypoint is
  introduced.
- No worker pushes, creates a PR, or changes another worktree. The coordinator
  brings every accepted worker commit back to `copilot/closeable-turso-gaps`,
  resolves overlaps, and validates the combined result here. The deliverable is
  one final PR from this parent session, not one PR per child.

## Wave 1: recorded SQL correctness

Launch these three workstreams in parallel. Counts are disjoint and total 64.

| ID | Work | Baseline entries | Acceptance |
| --- | --- | ---: | --- |
| SQL | Correlated IN binding; outer aggregate ownership; declared BLOB versus no affinity; safe FULL JOIN EXISTS rewrites; cross-schema TEMP views; table-call diagnostics | 13 | Correct values/cardinality and scope, safe null-extension handling, matching errors, no regression in existing rewrites |
| WIN | RANGE membership; numeric aggregate lifecycle; moving MIN/MAX collation/ties; moving concatenation | 27 | Correct frames, values, storage types, and overflow timing on both evaluator and compiled execution routes |
| CTE | Recursive priority queue and collation; per-current-row recursion; dependency/scope resolution; recursive CTAS metadata; validation | 24 | Correct expansion order, duplicates, LIMIT/OFFSET, nested/forward scope, supported SQL, and documented rejection parity |

### SQL implementation contract

Preserve bound outer-column identity when synthesizing IN equalities
(EmbeddedDatabase.SubqueryRewrites.cs around 857); do not allow the inner row
to capture an unqualified outer operand. Discover outer aggregate ownership
before deciding query cardinality. Keep declared BLOB affinity distinct from
absent expression affinity through derived/CTE metadata. Separate TEMP catalog
ownership from a view body's resolution context.

For FULL JOIN correlation, port only safe complete semi/anti rewrites from
core/translate/optimizer/unnest.rs and mod.rs. Do not simply delete the existing
guard. General plan reporting and bytecode lowering belong to Wave 2.

### WIN implementation contract

Mirror core/translate/window.rs RANGE comparison order, mixed INTEGER/REAL
arithmetic, NULL placement, and empty-prefix behavior rather than converting
every coordinate to double.

Mirror core/vdbe/execute.rs aggregate step/inverse/value ordering. SUM overflow
may be cleared by a later REAL before value/finalization, inverse errors have
different timing, and approximate/infinity history must survive moving frames.
Avoid independent per-frame reconstruction where history is observable.

Moving MIN/MAX must preserve argument collation and the upstream sequence-based
equal-key representative without changing ordinary aggregate tie behavior.
Moving GROUP_CONCAT/STRING_AGG requires separator/empty-buffer inverse state.

Cancelable execution currently declines compiled windows, and the sqltest
runner uses cancellation. A streaming-opcode-only fix is not acceptance.

### CTE implementation contract

Port core/translate/recursive_cte.rs queue semantics: ordered dequeue with a
stable sequence tie-breaker, one current row feeding recursion, correctly
collated seen sets, and recursive-arm collation fallback. ORDER BY decides the
next row to expand, not just final output order.

Unify dependency traversal and lexical scope across execution, validation, and
metadata. Ignore unused nested CTE bodies when counting recursive references;
respect shadowing and detect real cycles. Bind recursive input metadata from
the anchor before describing CTAS output.

Preserve the already-closed LIMIT/OFFSET wave. Two current FULL JOIN recursion
entries request upstream unsupported-form rejection; handle those as explicit
rejection parity, not a promise of general recursive FULL JOIN support.

## Wave 2: planner, new feature parity, and coverage

| ID | Work | Dependencies | Acceptance |
| --- | --- | --- | --- |
| PLAN | Remaining 18 EQP/EXPLAIN entries | SQL, CTE | EQP describes actual rewrites; cost original versus decorrelated plans; reuse identical aggregate subqueries; lower eligible semi/anti/grouped shapes for real EXPLAIN |
| EQP | Complete FORMAT=JSON | PLAN | Structured op variants, CTE materializations, null root parents, original SQL, correct result columns; adopt the 15 pinned upstream cases |
| NULLIDX | Explicit index and PK/UNIQUE constraint NULLS FIRST/LAST | Integrated Wave 1 parser/schema | Metadata, persisted ordering, forward/reverse seeks, bounds, ORDER BY satisfaction, uniqueness, rowid aliases, reopen and corruption checks; adopt 83 upstream cases |
| LOCALE | Newly discovered ICU-locale collation parity | Coordinate NULLIDX comparator integration | Generalized locale/tag options, consistent equality/order, and durable index ordering through pure-managed, trim/AOT-safe facilities; adopt 7 upstream cases without replacing the canonical SQLite collation corpus |
| COVER | Expand upstream coverage | Harness work parallel with Wave 1; engine-dependent adoption after integration | Classify all remaining Turso-specific files, run eligible backend-tagged SQL, and provide equivalents for path fixtures without rewriting expected SQL |

Do not implement plan parity by fabricating upstream display strings. A runtime
optimization can exist while its EQP is incomplete; trace both surfaces.

NULLIDX is not a parser-only change. Propagate null ordering through indexed
column definitions, schema round-trip, key comparators, range termination, and
planner properties, following core/schema.rs, core/types.rs, and
core/translate/optimizer/order.rs. Preserve default SQLite index behavior.
The pinned 83-case fixture also positively covers PRIMARY KEY and UNIQUE table
constraints; implement those instead of preserving their historical rejection.
Explicit NULLS clauses on UPSERT conflict targets remain rejected upstream.
Document nonstandard index SQL/file interoperability and fail closed when a
consumer cannot honor the physical ordering.

COVER starts from all 319 pinned SQLite-suite files already present, plus 5 of
67 Turso-specific files adopted. The initial top-level count of 61 omitted six
files under the upstream attach directory; 62 files therefore need disposition.
New Turso-suite files stay under `conformance/sqlite-sqltests/turso`, preserving
their upstream relative paths, rather than overwriting same-named SQLite files.
The 14 excluded path-fixture files comprise
12 integrity/corruption fixtures and 2 other database fixtures. Review the 12
rust-backend annotations individually instead of indiscriminately removing
backend filtering. Preserve genuine CLI-only and unadopted-capability exclusions.
Newly exposed failures become explicit owned follow-up work; do not silently
expand the expected-failures file to declare coverage green.

The expanded audit exposed 116 additional recorded differences. Ownership is
split into schema migration (33), attached database/sequence behavior (41),
SQL extensions and ANALYZE (25), generated-column plan reporting (7), and
explicit policy review (10). Three legacy-view loader cases are additional
schema work. In particular, losing a sequence watermark on COMMIT is a bug;
reusing rolled-back allocations is the existing managed contract.

Corrupt-image rejection during open must remain fail-closed. Such behavior is
not permission to skip the fixture or remove the existing empty-harness-exclusion
assertion. Exercise and classify the rejection explicitly. Schema rendering,
private bookkeeping layout, and richer covering-index plans must be distinguished
from actual wrong values or unsupported valid SQL before choosing a fix.

LOCALE was exposed by the broader audit: `fr-FR`, traditional Spanish tailoring,
upper-first ordering, numeric collation, and strength/case distinctions have
seven positive upstream cases. A host-dependent approximate comparator or
hardcoded sample outputs do not establish parity. If deterministic portable
ordering cannot be supplied under the managed/AOT constraint, record the actual
design blocker instead of silently accepting incompatible persisted indexes.

## Wave 3: architectural depth

| ID | Work | Dependencies | Acceptance |
| --- | --- | --- | --- |
| PAGE | Page-backed base rows and incremental persistence | Stable SQL/planner integration | Physical open and ordinary scans avoid eager whole-table row loading; MVCC retains pinned page/version semantics; extend delta maintenance without removing safe fallback prematurely |
| WBOUND | Bound window evaluation, not just its input | WIN | Partition-at-a-time computation, spillable positions/results, safe eviction; account for partition keys, offsets, results, and finite MEMORY budgets |
| SYNC | Checkpoint/rebase and page staging depth | Stable engine baseline | Supported pending writes/schema changes replay safely; synced-prefix history and file-backed page staging preserve publication leases, one-shot ownership, and crash recovery |
| BROWSER | Bounded asynchronous OPFS page profile | PAGE | Offset-based bounded I/O, encryption/durability preserved, explicit synchronous read-mirror profile retained |
| HASH | Adaptive spill/probe scheduling | Stable execution baseline | Avoid unnecessary partition reloads/rescans while preserving skew fallback, result order guarantees, null/collation semantics, and finite memory |

PAGE targets EmbeddedFileStore eager catalog-row loading and full-tree fallback
preparation, not an alleged absence of incremental writes or lazy index seeks.
MVCC version cursors, logical logging, checkpointing, and generation-aware GC
already exist and must not be replaced with weaker shortcuts.

WBOUND targets ResumableStatement.WindowBufferRuntime.Compute and the whole
partition evaluator. Input spill already exists; uncharged offsets and retained
partition/result arrays still need bounded handling.

SYNC follows sync/engine's checkpoint/history and rollback/apply/replay contracts.
Keep recoverable evidence through ambiguous push outcomes. Browser and sync
publication changes require every-boundary fault and reopen coverage.

HASH is an optimization of existing spilling, not a missing spill feature.
Use existing diagnostics and benchmark infrastructure to establish benefit.

## Acceptance and execution gates

1. Read the matching pinned Rust implementation before changing behavior. Invoke
   the applicable repository skills, including conformance, VDBE, storage, MVCC,
   managed closure, and AOT guidance where relevant.
2. Reproduce each targeted semantic difference with focused regressions; where
   SQLite shares the contract, use the existing differential infrastructure.
3. Run the existing managed wrapper with an explicit nonzero execution floor:
   `pwsh .\scripts\Invoke-ManagedTestSuite.ps1 -Framework net10.0 -Filter "<affected selectors>" -MinimumExecutedTests 1`.
   Corpus selectors use `Name~relative/file.sqltest`; combine them with `|` and
   `FullyQualifiedName~RegressionClass` as needed. Each corpus file is one NUnit
   test, not one test per internal SQL case. Set the execution floor accordingly.
   `FullyQualifiedName~Sqltest` selects the whole corpus/harness, and
   `--list-tests` does not prove that an execution filter narrowed the run.
4. Exercise affected complete sqltest files and both evaluator/compiled routes
   when routing differs. Remove only entries that demonstrably pass.
5. After integration, run the affected broader conformance lane and net8/net9/net10
   coverage appropriate to the change. Run existing storage/MVCC/sync/browser
   fault and interop gates for their respective workstreams.
6. Keep the shipped closure pure managed, NativeAOT-compatible, and trimmable.
   Do not suppress diagnostics, weaken thresholds, introduce native companions,
   mutate upstream fixtures, or hide failures behind new exclusions.
7. Before claiming closure, reconcile current counts and actual residuals in
   documentation/inventory. Preserve separate delivered, intentionally divergent,
   newly discovered, and pending statuses. Architecture acceptance is based on
   actual bounded behavior and recovery invariants, not opcode counts.

The initial 82-marker goal would leave 18 documented differences (17 intentional
extensions and one CLI prefix), but that is not an upper bound on the eventual
backlog exposed by COVER. A workstream is complete only after its accepted
behavior is integrated and evidenced; starting a worker is not closure.

## Integration checkpoint: `73af390` (2026-09-07)

This was an in-progress checkpoint, not a claim of complete Turso parity. The
expanded corpus then recorded 112 differences. Coverage growth and explicit
intentional differences make that count incomparable with the original 100
without considering which files and capabilities are now exercised.

The 64 original SQL/WIN/CTE correctness entries are closed. CREATE INDEX and
table-constraint NULL ordering, generated-column conversions, dependent-FK
renames, malformed stored-view isolation, safe cross-schema routing, and
committed sequence watermarks are integrated. Physical fixture construction
and engine-open outcomes now run through the normal conformance comparison;
the harness-exclusion list remains empty.

The remaining 19 ALTER entries describe schema rendering or diagnostic
decoration, not the previously fixed value-loss and dependent-column-rename
bugs. Managed token-spliced schema SQL preserves SQLite-style whitespace,
inline REFERENCES clauses, and replacement-name quoting. Replay/reopen and
generated-column value-preservation controls guard semantics independently.
Ten MVCC entries retain explicit managed policies: conflict-free concurrent
DDL, rollback-restored allocations, and in-memory journal-mode behavior.
Nine corruption-fixture files retain 18 observed eager-open rejection
differences; none are hidden as harness skips.

Architecture work remains open. The integrated page accessor now serves live
MVCC base-row scans, but this alone does not eliminate every whole-catalog
hydration boundary. Sync staging/rebase and checkpoint hash reuse are delivered,
not a complete sparse revert-history format. Hash scheduling corrections remain
under review for predicate replay and honest live-memory accounting. Window
output spilling alone is not the required bound on evaluator partitions.
Planner/JSON completeness and the constrained locale implementation remain
separate pending work.

At this checkpoint the browser workstream was implementing a narrower additive
profile: an explicitly opt-in, read-only asynchronous scan surface over the existing async
pager. Unsupported statement/database shapes fail explicitly rather than
falling back or replaying SQL. It must bound live pages, decoded records, schema
and WAL metadata, and in-flight buffers, with real cancellation/disposal
boundaries. The existing WholeImage and ReadOnlyMirror profiles remain unchanged.
That checkpoint did not yet include encrypted-page support; the current
bounded reader does, with authenticated AHTLA main/WAL reads and a metadata cap.

### Later checkpoint: `b383ae6`

The live inventory now contains 104 recorded differences. Eleven planner
markers closed (eight correlated-query descriptions and three generated-column
index descriptions); adoption of the 15-case JSON EQP file exposed 12 remaining
contract differences. The compiled LEFT JOIN suffix change does not close the
cancelable evaluator route used by the corpus, so that case remains explicitly
assigned rather than being reported as passing.

The SQL extension follow-up now binds JSON stars inside inline/named windows
and ordered-set expressions without sending ordinary COUNT(*) through that
rewrite. Same-value SETVAL uses a true MVCC update: a later transaction moving
off the repeated key no longer resurrects an obsolete watermark. Unchanged
INTEGER PRIMARY KEY sibling tables no longer hydrate merely because another
table is written.

HASH is delivered: partition residency, adaptive splitting, lightweight key
indexes, and bounded probe batching preserve output order. Per-probe output
reservation happens before predicate evaluation; failed admission uses the
streaming path without replaying callbacks, matched flags are committed only
with retained output, and released batch entries drop their actual references.
Working sets larger than cache capacity can still reload indexes; this is not
a claim of full partition-reordered grace execution.

Safe ATTACH transaction scope and foreign CDC are delivered: multiple
connection-private memory databases can commit together, while any physical
database write must be the transaction's sole database mutation. This
conservative guard avoids claiming physical-plus-memory atomicity. Partial
ON CONFLICT FAIL writes publish their matching CDC records after the statement
outcome is known. The constrained locale and bounded browser profiles are
delivered with their explicit supported-shape limits.

Reusable sync-prefix history remains deferred. Its frozen delta-capture
checkpoint was withheld because a v5-only decoder would reject durable,
in-flight v4 recovery state after upgrade. The accepted staging/rebase/hash-
reuse slice is not a reusable multi-generation history implementation.

The broader conformance run initially exposed timeout failures in
groupby/default.sqltest and subquery/default.sqltest. On the integrated parent,
the two complete files now pass under the unchanged 30-second per-case cap
(2/2 NUnit file tests, 10m59s total). A spill-record batching prototype was
therefore not needed and was withheld because its whole-record pooled buffers
were outside the finite execution-memory ledger. Neither the timeout nor the
fixture size was relaxed.

The frozen window-bounding checkpoint was also withheld after final review
found allocate-before-reserve and exception-path accounting leaks. Existing
window semantics remain integrated; a strict overall out-of-core evaluator
bound is not claimed.

### FORMAT=JSON result-metadata slice (2026-09-24)

The JSON envelope now derives `result_columns` for DML `RETURNING` from the
statement's projection and active catalog rather than copying the four TEXT
plan-column names. Non-returning DML still reports no result columns; an
unknown `RETURNING` target fails instead of producing a plausible-looking
plan. For the supported shared-CTE plan shape, materialization metadata is
emitted by the same plan branch that emits its body node, rather than inferred
afterward from the AST and a presumed node ID. The remaining EQP work is to
replace `unmodeled` operations and shape-specific descriptions with actual
planner/program data; this slice does not claim general CTE or plan parity.
Virtual-table scans now identify their `virtual_table` source and table/alias;
selected `fts` and `vector` index-method plans report Turso's structured
`index_method` operation with the selected method name. These fields come
from the same planner branches as their TEXT plan rows, not from parsing
those rows. Cost-selected multi-index AND intersections now emit `multi_index`
with `set_op: "and"`, their ordered index names, and the table alias when
present; the existing OR form retains `set_op: "or"` and now retains aliases.
Other `unmodeled` operations remain open.
The join-local OR plan's actual hash DISTINCT step emits a `distinct` op;
partial-index fallback plans that decline the index describe the real base
table scan and retain its alias. Generic placeholder plans no longer invent
a base-table scan for a view: JSON leaves its nodes empty until the view's
actual access path can be described. A standalone hash-build JSON op was
not added because this managed planner does not currently emit a corresponding
TEXT plan step; inventing one would misrepresent the executed plan.

## Current closure order (updated 2026-09-25, `6765675`)

The tracked expected-failures file now has **73 case-level differences**, not
73 missing features. They group as 18 eager corruption-open differences,
19 ALTER schema-text/CLI differences, 4 other schema-text differences,
21 SQL extension, journal-mode, or CLI-policy differences, and 11 MVCC
allocation/DDL/storage-policy differences. The harness-exclusion file is empty.
Do not erase these markers by weakening fail-closed corruption checks,
SQLite-style persisted schema SQL, or intentional managed extensions.

Snapshot isolation for a table first touched after a peer commit is **already
covered**: classic transactions hydrate their cloned catalog under the file
write gate before opening a pager pin; MVCC hydrates its shared baseline before
concurrent readers begin and whenever a catalog is published. The relevant
`PageBackedLazyRowLoadTests` regressions cover both. The older
`EmbeddedFileStore.Load()` comment describing this as an open isolation gap was
stale, not a new implementation task.

| Order | Work | Completion criterion |
| --- | --- | --- |
| 1 | Extend the browser's opt-in bounded read profile in small, explicitly classified shapes | **Current slices:** an `INTEGER PRIMARY KEY = integer literal` or unshadowed hidden `rowid`/`_rowid_`/`oid` predicate uses a page-bounded rowid point seek; integer-literal range comparisons and inclusive `BETWEEN` seek the starting bound and stop at the other, in ascending or descending rowid order. Explicit hidden-rowid projections retain SQLite's declared-column shadowing and `*` omission. Literal `OFFSET` skips matched rows before `LIMIT` counts emitted rows; unsupported predicates reject before scan I/O. Registered secondary indexes do not block base-table rowid scans or seeks. `WITHOUT ROWID` tables with ascending BINARY primary keys stream index interior/leaf records forward or backward under the same page budget, with logical column remapping and overflow checks; explicit uniformly ASC/DESC PK prefixes need no sort. INTEGER/TEXT first-key comparisons and BETWEEN seek the strongest starting bound, filter before LIMIT/OFFSET, and stop past the far bound; counted-page regressions guard against whole-tree scans for distant prefixes. Equality on every declared INTEGER/TEXT PK column with matching literal storage type uses direct B-tree page descent. Later-key ranges, implicit type conversions, other declared key types, joins, secondary-index access, and collated/mixed-direction ordering remain open. |
| 2 | Bound the entire buffered-window evaluator, not only its input | Charge partition keys, frame positions, function inputs, results, and spill indexes *before* allocation; compute/drain partitions incrementally; release reservations even on exceptions. Demonstrate a finite `cache_size` peak with large partitions, both spill modes and evaluator/compiled routes; do not treat output-only spilling as closure. |
| 3 | Complete physical working-set and query-plan depth | Avoid mandatory whole-b-tree validation/first-touch whole-table hydration where safe while retaining explicit corruption detection; make each JSON EQP operation come from the executed access path, including view/CTE materialization, rather than fabricate missing nodes. |
| 4 | Adopt Turso-specific SQL families as distinct product projects | The 28 pinned Turso-specific files originally omitted cover TYPE/DOMAIN/typed values (23) and incremental materialized views (5). A gated, durable INTEGER TYPE/DOMAIN registry now supports restricted STRICT INTEGER-domain columns (inherited DEFAULT/NOT NULL/CHECK) and identity INTEGER TYPE columns through validated writes/reopen; CAST to an identity type preserves its input value. General typed values and incremental materialized views remain unsupported. Expand encode/decode, planner/runtime behavior, transaction maintenance and recovery before enabling their full `@requires` capabilities; keep the corpus byte-faithful. |
| 5 | Deepen sync and platform compatibility | A SHA-pinned, immutable sync-history root permits sparse original/committed revert segments across generations with v4/v5 compatibility, leases, and crash/reopen tests. The root still costs one full image; high-change generations fall back to full captures. AHTLA-encrypted bounded browser reads now authenticate main/WAL pages with a WAL-location metadata budget; further portable locale tailoring and browser SQL shapes still require deterministic persisted ordering and bounded async operation. Native loadable extensions/raw sqlite3 handles and the PostgreSQL server are separate product-scope decisions, not hidden SQLite failures. |

### Live TODOs (not all represented by expected-failure cases)

- [x] Integrate the bounded read, restricted TYPE/DOMAIN, reusable sync-history,
  window input/output spill, and selected JSON EQP slices in **one** parent PR.
  As of code commit `6765675`, the bounded reader supports rowid predicates/projection,
  INTEGER/TEXT BINARY `WITHOUT ROWID` full-key seeks and first-key ranges,
  encrypted AHTLA/WAL snapshots, and an enforced page/WAL-location budget;
  unsupported SQL fails closed.
- [x] Keep the same head's opt-in STRICT INTEGER DOMAIN and identity INTEGER
  TYPE metadata/writes/reopen/cast behavior behind the experimental switch.
  Domain casts, non-identity encoding/decoding, and general typed values
  remain unimplemented.
- [ ] **WBOUND:** charge or spill partition keys, order entries, frame/peer
  positions, small-array argument payloads, and computed result scratch
  *before* allocation on compiled and evaluator routes. Compute/drain
  incrementally; verify a finite peak for large partitions with spill on/off,
  cancellation, exceptions, and temporary-file cleanup. Large argument/FILTER
  input spill and minimum-slot reservations are only partial progress;
  `WindowEvaluatorMemoryUnbounded` must remain true until the entire retained
  working set is covered.
- [ ] **IMV:** introduce view-owned persisted result **and** operator-state
  roots, with catalog ownership in schema staging, loader, allocation-map,
  pager and VACUUM paths. Route reads to that result, consume transaction-local
  source-row deltas atomically on INSERT/UPDATE/DELETE, and cover rollback,
  savepoints, reopen, concurrent snapshots, ALTER/DROP and recovery. The
  existing view has a rootpage-0 SQL definition and evaluates its SELECT on
  reads; `ReportRowChange` is not a materialized-view delta pipeline. Do not
  substitute a writable backing table, CREATE-time refresh, or parser-only
  acceptance.
- [ ] **Typed values:** extend the gated registry beyond the current
  primitive INTEGER identity/DOMAIN subset only with resolved base chains,
  parameter/encode/decode semantics, constraint checks on all DML paths,
  durable reload, and compatible index/FK behavior. Keep unsupported
  STRUCT/UNION/parametric/non-INTEGER types, non-STRICT typed columns,
  DROP TYPE/DOMAIN and domain casts fail-closed until their own contracts pass.
  Then adopt exact upstream `@requires` cases, not the entire family at once.
- [ ] **Planner/storage/platform:** eliminate remaining unmodeled JSON EQP
  operations from executed plan data (especially view/derived joins and CTE
  relationships); extend page-backed catalog/first-touch reads without
  weakening corruption and snapshot guarantees; add other bounded browser
  predicates/access paths and portable locale tailoring only with deterministic
  persisted index ordering. Reassess the full-image sync-history root and
  high-change fallback separately from the delivered sparse generations.
- [ ] **Final parity audit:** re-run affected complete sqltest files and
  cross-framework/package/browser gates after each change. Reconcile the
  expected-failures ledger case by case, distinguishing intentional SQLite or
  managed policies (including eager corruption-open rejection) from wrong
  values. Do not turn the 73 entries into 73 feature TODOs or remove a
  passing/intentional marker without proving its exact upstream case.

For each slice: compare the pinned Rust implementation, add focused regression
coverage, run the affected managed suite and cross-framework/package gates,
and remove an expected-failure entry only when its exact case passes. A
conformance count unchanged by an architectural improvement does not mean
the improvement was not delivered; conversely, a deliberately accepted
difference should remain documented.

The first SYNC format-depth slice stores a sparse committed-image revert
segment for a protected snapshot when its changed-page set is smaller than
the full image, with hash-checked reconstruction on reopen. The follow-on
format-6 revert state references an immutable full-image history root by
SHA-256; metadata versions 9–12 retain that root while a protected recovery
or ambiguous push can need it. Subsequent generations can store sparse
original **and** committed segments. Format-4/5 recovery remains readable
without a history root. This closes reusable protected-prefix capture for
the supported cases; it is not a claim that every Turso sync-engine path,
large delta, or compressed replica format is ported.

Compiled named-table join seeks now attach typed JSON `search` operations
from the selected program plan (actual table/alias, chosen durable or
automatic index, covering status, constraints, and join kind). TEXT EQP
remains unchanged. A derived join seek with no base-table metadata still
reports `unmodeled`, and compiled joins with no indexed leg still need an
executed access-path description when they exceed the supported two-table
shape. Two-table non-indexed joins now describe actual named-table scans and
the selected hash-probe operation from `OpenJoinCursor`; their JSON roots
follow the pinned two-table plan without inventing a standalone materialized
hash-build step that Ahtola's TEXT planner does not emit.
For a proven three-table compiled plan that materializes a two-table
named-table join prefix, JSON now nests the prefix's hash join and scan
under the pinned `hash_build` parent. Derived and deeper/unsupported plan
trees remain explicitly unmodeled rather than inventing scans.

The WBOUND spill-index and output-drain slices now keep random-access row
offsets on disk and avoid a second whole-partition output array for large
results. Per-function result arrays now write directly into the final
result rows, and partitions own their order entries without a second global
order-key array. `WindowEvaluatorMemoryUnbounded` explicitly records that
the evaluator's remaining partition entries, small-array argument payloads,
and temporary computed results remain outside the retained-memory ledger;
**full end-to-end window bounding remains open**. The output and spill-file
failure paths release their reservations and clean up files rather than
reporting a false finite peak.
Buffered evaluators now also receive statement memory, spill options, and
cancellation in a scoped binding restored on success or failure.
Unfiltered window functions whose arguments are all literal values share
one immutable input value (including the argument-free case) instead of
allocating a row-count-wide array. Large row-dependent argument and FILTER
input sets now use an indexed temporary file when their minimum row slots
exceed half the remaining compiled-window memory budget. Smaller retained
input arrays reserve their minimum slots before allocation. Cached spill
reads reserve memory and the files are disposed on both success and evaluator
failure. Retained argument payloads on the small-array path, partition
metadata and computed result scratch still require the full spill-backed
evaluator redesign;
`WindowEvaluatorMemoryUnbounded` remains true.

TYPE/DOMAIN adoption must keep SQLite's five physical storage classes: the
pinned Turso engine persists named definitions as SQL, resolves base-type
chains, and encodes/decodes values at column boundaries. Identity INTEGER TYPE
columns now carry resolved metadata through STRICT-table writes and reopen,
and their CAST returns the unchanged stored representation;
custom encode/decode expressions and general type chains are not implemented.
Persisted definitions must reload, and scalar/domain validation must agree
across INSERT, SELECT, UPDATE, indexes, and foreign keys before enabling the
corresponding corpus capability.
Similarly, a materialized view populated once at CREATE time is not Turso's
incremental feature: the pinned engine maintains both a result b-tree and
operator state from transaction-local source deltas. Creation, DML, rollback,
reopen, and VACUUM must preserve both before declaring that gap closed.
