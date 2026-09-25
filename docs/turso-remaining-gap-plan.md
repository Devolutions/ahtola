# Remaining Turso gap closure plan

## Baseline and scope

Prepared 2026-09-06 against Ahtola `83bb892` and the read-only Turso
`v0.8.0-pre.7` pin, `277ddd050`.

At the baseline, the expected-failures file contained 100 entries: 82 engine, planner, or
diagnostic parity candidates; 17 deliberate extensions (8 DML LIMIT, 2 STORED
generated columns, 7 WITHOUT ROWID); and 1 Turso CLI error-prefix difference.
These are coverage entries, not independent defects. The historical inventory's
217 closed records and its two-failure metadata are not the current backlog.

Baseline discovery reports 11,077 cases: 10,959 runnable, 85 skipped by the
corpus/backend policy, and 33 unsupported by the harness. These are discovery
counts, not evidence that every runnable case has passed in this execution.

Implement the parity and architectural work below without removing the deliberate
extensions. Native companions, loadable native extensions, and raw sqlite3 handles
remain excluded. Typed values, TYPE/DOMAIN, and incremental materialized views
remain separate product-adoption projects: do not silently enable them or call
them implemented because a historical record says closed. Zstd rejection is not
a missing feature relative to this pin; both engines reject it.

## Worker and integration policy

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

This is an in-progress checkpoint, not a claim of complete Turso parity. The
expanded corpus currently records 112 differences. Coverage growth and explicit
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

The browser workstream is implementing a narrower additive profile: an
explicitly opt-in, read-only asynchronous scan surface over the existing async
pager. Unsupported statement/database shapes fail explicitly rather than
falling back or replaying SQL. It must bound live pages, decoded records, schema
and WAL metadata, and in-flight buffers, with real cancellation/disposal
boundaries. The current WholeImage and ReadOnlyMirror profiles remain unchanged.
This slice is not general asynchronous SQL execution or encrypted-page support.

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

## Current closure order (2026-09-24, `1d074fa` baseline)

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
| 1 | Extend the browser's opt-in bounded read profile in small, explicitly classified shapes | **Current slices:** an `INTEGER PRIMARY KEY = integer literal` predicate uses a page-bounded rowid point seek; integer-literal range comparisons and inclusive `BETWEEN` seek the starting bound and stop at the other, in ascending or descending rowid order. Literal `OFFSET` skips matched rows before `LIMIT` counts emitted rows; unsupported predicates reject before scan I/O. Registered secondary indexes no longer block base-table rowid scans or seeks (the secondary b-trees are not traversed). Expand to typed predicates, joins, secondary-index access and `WITHOUT ROWID` reads only with separate bounds and snapshot tests. |
| 2 | Bound the entire buffered-window evaluator, not only its input | Charge partition keys, frame positions, function inputs, results, and spill indexes *before* allocation; compute/drain partitions incrementally; release reservations even on exceptions. Demonstrate a finite `cache_size` peak with large partitions, both spill modes and evaluator/compiled routes; do not treat output-only spilling as closure. |
| 3 | Complete physical working-set and query-plan depth | Avoid mandatory whole-b-tree validation/first-touch whole-table hydration where safe while retaining explicit corruption detection; make each JSON EQP operation come from the executed access path, including view/CTE materialization, rather than fabricate missing nodes. |
| 4 | Adopt Turso-specific SQL families as distinct product projects | The remaining 28 unadopted pinned `turso-sqltests` files cover TYPE/DOMAIN/typed values (23) and incremental materialized views (5). A gated, durable INTEGER TYPE/DOMAIN *definition registry* is now implemented; no typed column/value semantics or materialized views are claimed. Complete encode/decode, planner/runtime behavior, transaction maintenance and recovery before enabling their `@requires` capabilities; keep the corpus byte-faithful. |
| 5 | Deepen sync and platform compatibility | A SHA-pinned, immutable sync-history root now permits sparse original/committed revert segments across generations with v4/v5 compatibility, leases, and crash/reopen tests. The root still costs one full image; high-change generations fall back to full captures. Expand portable locale tailoring and browser encryption only when deterministic persisted ordering and bounded async page codecs can be guaranteed. Native loadable extensions/raw sqlite3 handles and the PostgreSQL server are separate product-scope decisions, not hidden SQLite failures. |

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
the evaluator's remaining partition entries, prepared function inputs, and
temporary computed results remain outside the retained-memory ledger;
**full end-to-end window bounding remains open**. The output and spill-file
failure paths release their reservations and clean up files rather than
reporting a false finite peak.
Buffered evaluators now also receive statement memory, spill options, and
cancellation in a scoped binding restored on success or failure; this does
not yet spill or account for `PrepareWindowFunctionInputs`' retained arrays.

TYPE/DOMAIN adoption must keep SQLite's five physical storage classes: the
pinned Turso engine persists named definitions as SQL, resolves base-type
chains, and encodes/decodes values at column boundaries. A metadata-only
`CREATE TYPE` parser is not acceptance; persisted definitions must reload,
and scalar/domain validation must agree across INSERT, SELECT, UPDATE,
indexes, and foreign keys before enabling the corresponding corpus capability.
Similarly, a materialized view populated once at CREATE time is not Turso's
incremental feature: the pinned engine maintains both a result b-tree and
operator state from transaction-local source deltas. Creation, DML, rollback,
reopen, and VACUUM must preserve both before declaring that gap closed.
