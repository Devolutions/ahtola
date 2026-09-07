# Remaining Turso gap closure plan

## Baseline and scope

Prepared 2026-09-06 against Ahtola `83bb892` and the read-only Turso
`v0.8.0-pre.7` pin, `277ddd050`.

The live expected-failures file contains 100 entries: 82 engine, planner, or
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
| NULLIDX | Explicit index NULLS FIRST/LAST | Integrated Wave 1 parser/schema | Metadata, persisted ordering, forward/reverse seeks, bounds, ORDER BY satisfaction, uniqueness/UPSERT, reopen and corruption checks; adopt 83 upstream cases |
| COVER | Expand upstream coverage | Harness work parallel with Wave 1; engine-dependent adoption after integration | Classify all remaining Turso-specific files, run eligible backend-tagged SQL, and provide equivalents for path fixtures without rewriting expected SQL |

Do not implement plan parity by fabricating upstream display strings. A runtime
optimization can exist while its EQP is incomplete; trace both surfaces.

NULLIDX is not a parser-only change. Propagate null ordering through indexed
column definitions, schema round-trip, key comparators, range termination, and
planner properties, following core/schema.rs, core/types.rs, and
core/translate/optimizer/order.rs. Preserve default SQLite index behavior.
Document nonstandard index SQL/file interoperability and fail closed when a
consumer cannot honor the physical ordering.

COVER starts from all 319 pinned SQLite-suite files already present, plus 5 of
61 Turso-specific files adopted. The 14 excluded path-fixture files comprise
12 integrity/corruption fixtures and 2 other database fixtures. Review the 12
rust-backend annotations individually instead of indiscriminately removing
backend filtering. Preserve genuine CLI-only and unadopted-capability exclusions.
Newly exposed failures become explicit owned follow-up work; do not silently
expand the expected-failures file to declare coverage green.

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
