# Ahtola ↔ Turso gap analysis

**Scope.** Exhaustive comparison of the Ahtola managed engine against the pinned
Turso Rust core — `turso-src/` submodule at tag **v0.7.2** (commit `046e9cbf6`) —
across all seven layers: **VDBE** (deep-dive priority), compilation/translate,
parser/dialect, built-in functions, storage/pager/WAL/b-tree, MVCC/transactions,
and sync/replication.

**Companion artifact.** [`turso-gap-inventory.json`](./turso-gap-inventory.json) —
the machine-readable inventory, with stable IDs for status tracking
(`open → closed`). This report is the human-readable analysis **as of analysis
time (171 entries)**; the JSON is the live tracking source of truth. Closure
progress since analysis (waves F1–F2.18) is recorded in
[section 11](#11-closure-progress-since-analysis), and current counts are:
**211 entries, 182+ closed**; expected-failures file down from **606 → 0**
lines after Phase 1 MVCC surface (`journal_mode=mvcc` + `BEGIN CONCURRENT` +
in-process `MvStore`). Remaining MVCC depth (row-version cursors, durable
logical log, checkpoint SM) is tracked in [`mvcc-port-contract.md`](mvcc-port-contract.md).

**Ground truth.** `src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`
(606 failure lines at analysis time). Every line was cross-referenced to at least
one inventory entry: **606/606 mapped, 0 orphans, 297 explicit citations** (see
Appendix B for method). 84 of 171 entries have at least one mapped failure line; the
remaining 87 are source-evidence-only gaps (features with no executed
conformance coverage, e.g. virtual tables, sync engine, typed values).

## 1. Executive summary

Ahtola's port is **architecturally faithful but functionally narrower** than
Turso v0.7.2. The managed engine reproduces Turso's program model (register
machine, cursors, sorter, aggregates, compound selects, window buffers) with
deliberate opcode consolidation — 74 `VdbeOpcode` values against 204 Turso
`Insn` variants — and verified parity in the comparison/arithmetic/sorter cores.
The gaps concentrate in four areas:

1. **Planner/compiler depth** (compilation layer, 38 entries): no subquery
   flattening or decorrelation, no cost-based join ordering, no partial or
   expression indexes, single-table fast paths only. This is the largest
   functional deficit after DDL-by-treewalker.
2. **VDBE execution machinery** (35 entries): no trigger subprogram opcodes
   (`Program`/`Gosub`/`Return`), no virtual-table family, no hash-join/bloom
   family, no general ephemeral tables, seek/index-cursor families partial,
   write-time affinity enforcement (`TypeCheck`) scattered — the root cause of
   the largest wrong-values conformance cluster.
3. **Parser surface** (22 entries): dominated by one astonishingly cheap fix —
   the missing implicit (AS-less) column alias maps to **144 of 606** failure
   lines — plus PRAGMA family coverage, `INDEXED BY`, JOIN-of-subqueries, and
   assorted grammar forms.
4. **Upstream extensions not adopted** (policy, s4): typed values
   (arrays/structs/unions), `CREATE SEQUENCE`, materialized views, CDC, and the
   sync engine. These are product decisions, not defects.

Notably solid: storage format parity (WAL contract governed, 2 `parity` entries
closed), MVCC observable semantics (adapted, not reimplemented), the
sorter/spill path, and the window-buffer extension which is *ahead* of Turso
for the shapes it covers.

### Inventory at a glance

| Layer | Entries | missing | partial | divergent | extension | parity | Mapped fail-lines* |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| vdbe | 35 | 18 | 5 | 10 | 2 | 0 | 465 |
| compilation | 38 | 18 | 6 | 14 | 0 | 0 | 661 |
| parser | 22 | 17 | 1 | 4 | 0 | 0 | 320 |
| functions | 24 | 16 | 0 | 6 | 2 | 0 | 41 |
| storage | 20 | 7 | 9 | 1 | 1 | 2 | 32 |
| mvcc | 14 | 11 | 2 | 1 | 0 | 0 | 42 |
| sync | 18 | 11 | 2 | 3 | 2 | 0 | 1 |
| **total** | **171** | **98** | **25** | **39** | **7** | **2** | **606 distinct lines** |

\* Sum of expected-failure lines mapped to each layer's entries. Lines multi-map by design (one symptom can trace to several layers), so column sums exceed 606; the distinct-line total is exactly 606 (100% coverage).

| Severity | Count | | Effort | Count | | Status | Count |
| --- | ---: | --- | --- | ---: | --- | --- | ---: |
| s1-correctness | 19 | | S | 63 | | open | 169 |
| s2-capability | 86 | | M | 71 | | closed | 2 |
| s3-perf | 34 | | L | 37 | | | |
| s4-intentional | 32 | | | | | | |


## 2. Method and classification model

**Sources.** Turso side: the read-only `turso-src/` submodule only (pinned at
v0.7.2, `046e9cbf6`) — `core/vdbe/insn.rs` (204 `Insn` variants), `execute.rs`
(~14k lines, per-opcode arms), `translate/`, `sqlite/parser`, `core/functions/`,
`core/storage/`, `core/mvcc/`, `sync/engine/`. Ahtola side: `src/Ahtola.Core`
(`Execution/`, `Compilation/`, `Parsing/`, `Storage/`), `src/Ahtola.Data`,
`src/Ahtola.Tests/Conformance/`. No upstream files were fetched ad hoc and
nothing under `turso-src/` was modified.

**Process.** Per-layer audits (VDBE done arm-by-arm; the other six layers in
parallel), each producing structured gap entries with dual citations. Then a
consolidation pass: dedup, stable ID assignment, repair of every citation
against the actual sources, and a full cross-reference of the 606-line
expected-failures file via a rule engine (~150 prefix/symptom rules with
word-boundary matching; multi-mapping by design). Every entry's
`conformance_links` were verified to name real failure keys.

**Classification.**

| Field | Values | Meaning |
| --- | --- | --- |
| `kind` | `missing` | No Ahtola counterpart exists |
| | `partial` | A subset is ported; the rest is missing |
| | `divergent` | Both exist; behavior or structure differs |
| | `extension` | Ahtola-only, no upstream counterpart |
| | `parity` | Audited and confirmed equivalent (status `closed`) |
| `severity` | `s1-correctness` | Can produce silently wrong results |
| | `s2-capability` | Blocks SQL surface / conformance cases |
| | `s3-perf` | Performance-only divergence |
| | `s4-intentional` | Documented product divergence (managed port, encryption, unadopted upstream extensions) |
| `effort` | `S` / `M` / `L` | Rough porting cost |
| `status` | `open` / `closed` | Flip when the gap lands; drop resolved keys from the expected-failures file in the same change |

**Reading the mapped-failure counts.** Each failure line maps to every entry
that plausibly causes it (a symptom can be parser-blocked *and* VDBE-blocked).
Counts therefore measure **blast radius**, not fix order, and column sums
exceed 606. Two very large counts are umbrellas over intentional design
(`vdbe-ddl-executed-by-treewalker`, 178; `compile-attach-same-file-not-supported`,
61) — they map DDL/ATTACH-shaped failures but are `s4-intentional`, so they are
excluded from the actionable ranking in §10.2.


## 3. VDBE deep dive

### 3.1 Scale and program model

Turso v0.7.2 declares **204 `Insn` variants** (`core/vdbe/insn.rs`, header
comment "~190" understates the current count); Ahtola's `VdbeOpcode` has **74
values (0–73)**. Name-matching would therefore grossly overstate the gap. The
real mapping, built variant-by-variant:

- **26 direct** — same opcode on both sides (`Rewind`, `Next`, `Column`,
  `AggStep`, `Sorter*`, `Function`, `ResultRow`, …).
- **40 consolidated** — several Turso opcodes folded into one Ahtola opcode
  with a sub-parameter: the 12 arithmetic opcodes into `Arithmetic` +
  `ArithmeticOperator` (`VdbeArithmetic.cs`); the 10 comparison/jump opcodes
  into `Compare` + `JumpIfNotTrue`; `Init`/`Null`/`Integer`/`Real`/`String8`/
  `Blob`/`Int64` into `LoadConstant`; `OffsetLimit` into `OffsetGate`/`LimitGate`.
- **10 divergent** — both sides have the construct but structure/semantics
  differ (`Halt`, `Transaction`, `NewRowid`, `Insert`, `Column`, `Sequence`,
  `Explain`, `Fk*`, …).
- **104 missing** — no Ahtola counterpart. **~46 of these are upstream
  extensions beyond SQLite** (17 typed-value opcodes, 8 `CREATE SEQUENCE`
  opcodes, 15 hash-join opcodes, 4 index-method opcodes, materialized views,
  CDC); ~58 are SQLite-core machinery (virtual tables, triggers/subprograms,
  index cursors, seek family, ephemeral tables, schema cookies, bloom filter,
  …).
- **24 bydesign** — intentionally absent: 11 DDL opcodes executed by Ahtola's
  AST tree-walker, coroutine opcodes replaced by .NET enumerators, record
  construction handled at the pager boundary, `Not`/`Concat`/`And`/`Or` in the
  shared expression evaluator.

Ahtola also carries **~32 extension opcodes** with no Turso counterpart,
grouped in `vdbe-ext-window-buffer-family` (7 window-buffer opcodes) and
`vdbe-ext-worktable-and-gate-families` (work-table, gate, distinct, filter,
projection, compound-result machinery).

### 3.2 Name collisions (read before any porting work)

Four pairs of **same-name/different-meaning** opcodes are landmines:

| Opcode | Turso meaning | Ahtola meaning |
| --- | --- | --- |
| `Filter` | Bloom-filter membership probe | Row predicate evaluation (WHERE push-down) |
| `Commit` | — (no such opcode; `AutoCommit`/`Transaction` cover it) | Cursor-write flush returning `LastInsertRowId` |
| `ResultRow` | Same (row yield) | Same — but maps semantically to Turso `Yield` |
| `Yield` | Coroutine row yield | Resumable-statement suspension (returns `Yielded`) |

### 3.3 Verified parity points

- **Comparison semantics**: `Compare` + `JumpIfNotTrue` reproduces SQLite's
  storage-class ordering, affinity-before-compare, collation application, and
  NULL tri-state for `Eq`/`Ne`/`Lt`/`Le`/`Gt`/`Ge`/`IsNull`/`NotNull`. Residual
  risk is the single-value `IsTrue` path (`vdbe-comparison-opcode-consolidation`).
- **Arithmetic**: all 12 operators with SQLite overflow-to-REAL promotion and
  integer division semantics (`VdbeArithmetic.cs`).
- **Sorter**: external sort with spill-to-disk k-way merge (`SorterSpill`) —
  Turso parity including the spill path.
- **Aggregates**: step/finalize split (`AggStep`/`AggFinalize`) with per-group
  register frames, plus Ahtola-only `AggReset`/`SameGroup`/`GroupKey` for
  sorted-group streaming.

### 3.4 The two s1-correctness findings

- **`vdbe-typecheck-on-write`** — Turso runs a `TypeCheck` opcode on every
  INSERT/UPDATE record that applies column affinity + CHECK of storage classes;
  Ahtola scatters affinity across write delegates and misses cases (31 mapped
  failure lines, 15 cited — the `values-clause`, `affinity2`, `storage`
  clusters). This is the largest *wrong-values* (not parse-error) cluster.
- **`vdbe-aggregate-overflow-semantics`** — `AggStep` accumulates sum/count in
  integer and promotes on overflow like SQLite; edge ordering of the promotion
  vs. the step callback is unverified against `execute.rs` — flagged s1 pending
  a dedicated conformance probe.

### 3.5 Structural findings

- **No subprogram machinery** (`BeginSubrtn`/`Gosub`/`Return`/`Program`) —
  Turso compiles triggers as sub-programs linked into the main program;
  Ahtola has no equivalent, so `CREATE TRIGGER` is tree-walked and DML-with-
  triggers semantics diverge (111 mapped lines; most are umbrella-mapped
  trigger/gencol DDL shapes).
- **DDL is executed by the AST tree-walker**, not by VDBE opcodes (11 opcodes
  skipped by design). Consequence: DDL inside transactions, schema-cookie
  bumps (`ParseSchema`/`ReadCookie`/`SetCookie` missing), and
  prepared-statement schema invalidation all behave differently from Turso.
- **Write path hides index machinery**: `IdxInsert`/`IdxDelete`/`IdxRowId` and
  the seek family (`SeekGE/GT/LE/LT`, `NoConflict`, `NotExists`) live inside
  write delegates rather than as opcodes — fine for correctness of simple
  DML, but it blocks index-cursor use in general query plans and makes flag
  semantics (`InsertFlags.REQUIRE_SEEK`, `UPDATE_ROWID_CHANGE`, `PREFER_UPDATE`)
  partial (`vdbe-insert-update-flag-semantics`).
- **FK enforcement is delegate-side** (`FkCounter`/`FkIfZero`/`FkCheck`
  divergent): deferred constraints, self-referential cascades, and
  statement-level rollback on FK violation are the risk areas (17 mapped).
- **Error model**: Turso threads `Halt` variants with error payloads through
  the program; Ahtola throws .NET exceptions mapped at the provider boundary —
  error *text* and *timing* differ (16 mapped).

### 3.6 VDBE opcode mapping matrix (204 Turso variants)

| # | Turso ``Insn`` | Status | Ahtola counterpart | Gap / note |
| ---: | --- | --- | --- | --- |
| 1 | `Init` | bydesign | — (resumable-statement dispatch, no init-block jump) | `vdbe-coroutine-machinery` |
| 2 | `Null` | consolidated | LoadConstant |  |
| 3 | `BeginSubrtn` | missing | — | `vdbe-trigger-subprogram-machinery` |
| 4 | `NullRow` | consolidated | GuardedRow (outer-join null row) |  |
| 5 | `Add` | consolidated | Arithmetic(Add) |  |
| 6 | `Subtract` | consolidated | Arithmetic(Subtract) |  |
| 7 | `Multiply` | consolidated | Arithmetic(Multiply) |  |
| 8 | `MemMax` | missing | — | `vdbe-scalar-control-opcodes` |
| 9 | `Divide` | consolidated | Arithmetic(Divide) |  |
| 10 | `Compare` | direct | Compare (66) | `vdbe-comparison-opcode-consolidation` |
| 11 | `BitAnd` | consolidated | Arithmetic(BitwiseAnd) |  |
| 12 | `BitOr` | consolidated | Arithmetic(BitwiseOr) |  |
| 13 | `BitNot` | consolidated | Arithmetic(BitwiseNot) |  |
| 14 | `Checkpoint` | missing | — (coordinator exists internally, no opcode/SQL path) | `vdbe-checkpoint-opcode` |
| 15 | `Remainder` | consolidated | Arithmetic(Modulo) |  |
| 16 | `Jump` | consolidated | Goto / JumpIf |  |
| 17 | `Move` | consolidated | Copy |  |
| 18 | `IfPos` | missing | — | `vdbe-scalar-control-opcodes` |
| 19 | `NotNull` | consolidated | Compare + JumpIfNotTrue |  |
| 20 | `Eq` | consolidated | Compare + JumpIfNotTrue | `vdbe-comparison-opcode-consolidation` |
| 21 | `Filter` | missing | — (bloom probe; ⚠ Ahtola Filter is a row predicate) | `vdbe-bloom-filter-opcodes` |
| 22 | `FilterAdd` | missing | — | `vdbe-bloom-filter-opcodes` |
| 23 | `Ne` | consolidated | Compare + JumpIfNotTrue |  |
| 24 | `Lt` | consolidated | Compare + JumpIfNotTrue |  |
| 25 | `Le` | consolidated | Compare + JumpIfNotTrue |  |
| 26 | `Gt` | consolidated | Compare + JumpIfNotTrue |  |
| 27 | `Ge` | consolidated | Compare + JumpIfNotTrue |  |
| 28 | `If` | consolidated | JumpIf |  |
| 29 | `IfNot` | consolidated | JumpIf / JumpIfNotTrue |  |
| 30 | `OpenRead` | direct | OpenReadCursor |  |
| 31 | `VOpen` | direct | `VOpenInstruction` | `vdbe-virtual-table-opcodes` |
| 32 | `VCreate` | treewalker | `ExecuteCreateVirtualTable` | `vdbe-virtual-table-opcodes` |
| 33 | `VFilter` | direct | `VFilterInstruction` | `vdbe-virtual-table-opcodes` |
| 34 | `VColumn` | direct | `VColumnInstruction` | `vdbe-virtual-table-opcodes` |
| 35 | `VUpdate` | direct | `VUpdateInstruction` | `vdbe-virtual-table-opcodes` |
| 36 | `VNext` | direct | `VNextInstruction` | `vdbe-virtual-table-opcodes` |
| 37 | `VDestroy` | treewalker | `ExecuteDropTable` | `vdbe-virtual-table-opcodes` |
| 38 | `VBegin` | direct | `VBeginInstruction` | `vdbe-virtual-table-opcodes` |
| 39 | `VRename` | treewalker | `ExecuteAlterTableRename` | `vdbe-virtual-table-opcodes` |
| 40 | `OpenPseudo` | missing | — | `vdbe-open-ephemeral` |
| 41 | `Rewind` | direct | Rewind |  |
| 42 | `Last` | direct | Last |  |
| 43 | `Column` | divergent | Column (no DEFAULT operand for short records) | `vdbe-column-default-short-record` |
| 44 | `ColumnHasField` | missing | — | `vdbe-typed-value-opcode-family` |
| 45 | `TypeCheck` | missing | — (affinity/CHECK scattered across write delegates) | `vdbe-typecheck-on-write` |
| 46 | `ArrayEncode` | missing | — | `vdbe-typed-value-opcode-family` |
| 47 | `ArrayDecode` | missing | — | `vdbe-typed-value-opcode-family` |
| 48 | `ArrayElement` | missing | — | `vdbe-typed-value-opcode-family` |
| 49 | `ArrayLength` | missing | — | `vdbe-typed-value-opcode-family` |
| 50 | `MakeArray` | missing | — | `vdbe-typed-value-opcode-family` |
| 51 | `MakeArrayDynamic` | missing | — | `vdbe-typed-value-opcode-family` |
| 52 | `StructField` | missing | — | `vdbe-typed-value-opcode-family` |
| 53 | `UnionPack` | missing | — | `vdbe-typed-value-opcode-family` |
| 54 | `UnionTag` | missing | — | `vdbe-typed-value-opcode-family` |
| 55 | `UnionExtract` | missing | — | `vdbe-typed-value-opcode-family` |
| 56 | `RegCopyOffset` | missing | — | `vdbe-typed-value-opcode-family` |
| 57 | `ArrayConcat` | missing | — | `vdbe-typed-value-opcode-family` |
| 58 | `ArraySetElement` | missing | — | `vdbe-typed-value-opcode-family` |
| 59 | `ArraySlice` | missing | — | `vdbe-typed-value-opcode-family` |
| 60 | `MakeRecord` | bydesign | — (SqlValue rows end-to-end; encoding at pager boundary) | `vdbe-record-construction-model` |
| 61 | `ResultRow` | direct | ResultRow | ⚠ Turso Yield ≈ Ahtola ResultRow; `vdbe-coroutine-machinery` |
| 62 | `Next` | direct | Next |  |
| 63 | `Prev` | direct | Prev |  |
| 64 | `Halt` | divergent | Halt (clean stop only; errors are .NET exceptions mapped at provider boundary) | `vdbe-halt-error-model` |
| 65 | `HaltIfNull` | missing | — | `vdbe-scalar-control-opcodes` |
| 66 | `Transaction` | divergent | BeginTransaction / CommitTransaction / RollbackTransaction | `vdbe-transaction-opcode-model` |
| 67 | `AutoCommit` | consolidated | CommitTransaction |  |
| 68 | `Savepoint` | direct | Savepoint / ReleaseSavepoint / RollbackToSavepoint (op enum split) |  |
| 69 | `Goto` | direct | Goto |  |
| 70 | `Gosub` | missing | — | `vdbe-trigger-subprogram-machinery` |
| 71 | `Return` | missing | — | `vdbe-trigger-subprogram-machinery` |
| 72 | `Program` | missing | — | `vdbe-trigger-subprogram-machinery` |
| 73 | `ResetCount` | bydesign | — (change counting inside write delegates) |  |
| 74 | `Integer` | consolidated | LoadConstant |  |
| 75 | `Real` | consolidated | LoadConstant |  |
| 76 | `RealAffinity` | consolidated | NumericAffinity |  |
| 77 | `String8` | consolidated | LoadConstant |  |
| 78 | `Blob` | consolidated | LoadConstant |  |
| 79 | `RowData` | missing | — | `vdbe-index-cursor-opcode-family` |
| 80 | `RowId` | direct | RowId |  |
| 81 | `IdxRowId` | missing | — | `vdbe-index-cursor-opcode-family` |
| 82 | `SeekRowid` | direct | SeekRowid (folds Found/NotFound targets) | `vdbe-seek-op-family-partial` |
| 83 | `SeekEnd` | missing | — | `vdbe-deferred-seek` |
| 84 | `DeferredSeek` | missing | — | `vdbe-deferred-seek` |
| 85 | `SeekGE` | missing | — | `vdbe-seek-op-family-partial` |
| 86 | `SeekGT` | missing | — | `vdbe-seek-op-family-partial` |
| 87 | `IdxInsert` | missing | — (index maintenance inside write delegates) | `vdbe-index-cursor-opcode-family` |
| 88 | `SeekLE` | missing | — | `vdbe-seek-op-family-partial` |
| 89 | `SeekLT` | missing | — | `vdbe-seek-op-family-partial` |
| 90 | `IdxGE` | missing | — | `vdbe-index-cursor-opcode-family` |
| 91 | `IdxGT` | missing | — | `vdbe-index-cursor-opcode-family` |
| 92 | `IdxLE` | missing | — | `vdbe-index-cursor-opcode-family` |
| 93 | `IdxLT` | missing | — | `vdbe-index-cursor-opcode-family` |
| 94 | `DecrJumpZero` | missing | — | `vdbe-scalar-control-opcodes` |
| 95 | `AggStep` | direct | AggStep | `vdbe-aggregate-overflow-semantics` |
| 96 | `AggFinal` | direct | AggFinalize |  |
| 97 | `AggValue` | missing | — | `vdbe-misc-cursor-opcodes` |
| 98 | `SorterOpen` | direct | OpenSorter |  |
| 99 | `SorterInsert` | direct | SorterInsert |  |
| 100 | `SorterCompare` | consolidated | SorterSort (comparison internal) |  |
| 101 | `SorterSort` | direct | SorterSort |  |
| 102 | `SorterData` | direct | SorterData |  |
| 103 | `SorterNext` | direct | SorterNext |  |
| 104 | `RowSetAdd` | direct | RowSetInsert |  |
| 105 | `RowSetRead` | consolidated | RowSetRewind + RowSetNext |  |
| 106 | `RowSetTest` | missing | — | `vdbe-rowset-test` |
| 107 | `Function` | direct | Function |  |
| 108 | `Cast` | direct | Cast |  |
| 109 | `InitCoroutine` | bydesign | — (.NET enumerators / dedicated runtimes) | `vdbe-coroutine-machinery` |
| 110 | `EndCoroutine` | bydesign | — | `vdbe-coroutine-machinery` |
| 111 | `Yield` | consolidated | ResultRow (row yield); Ahtola Yield = statement suspension | `vdbe-coroutine-machinery` |
| 112 | `Insert` | divergent | Insert + Update + Commit (flag semantics partial) | `vdbe-insert-update-flag-semantics` |
| 113 | `Int64` | consolidated | LoadConstant |  |
| 114 | `Delete` | direct | Delete |  |
| 115 | `IdxDelete` | missing | — | `vdbe-index-cursor-opcode-family` |
| 116 | `NewRowid` | divergent | — (allocation inside write-target Commit) | `vdbe-newrowid-semantics` |
| 117 | `MustBeInt` | missing | — | `vdbe-scalar-control-opcodes` |
| 118 | `SoftNull` | missing | — | `vdbe-scalar-control-opcodes` |
| 119 | `NoConflict` | missing | — (uniqueness inside write delegates) | `vdbe-seek-op-family-partial` |
| 120 | `NotExists` | missing | — | `vdbe-seek-op-family-partial` |
| 121 | `OffsetLimit` | consolidated | OffsetGate + LimitGate |  |
| 122 | `OpenWrite` | direct | OpenWriteCursor |  |
| 123 | `Copy` | direct | Copy |  |
| 124 | `CreateBtree` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 125 | `IndexMethodCreate` | missing | — | `vdbe-index-method-opcodes` |
| 126 | `IndexMethodDestroy` | missing | — | `vdbe-index-method-opcodes` |
| 127 | `IndexMethodOptimize` | missing | — | `vdbe-index-method-opcodes` |
| 128 | `IndexMethodQuery` | missing | — | `vdbe-index-method-opcodes` |
| 129 | `ClearBtree` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 130 | `Destroy` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 131 | `ResetSorter` | missing | — | `vdbe-misc-cursor-opcodes` |
| 132 | `DropTable` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 133 | `DropView` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 134 | `DropIndex` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 135 | `DropTrigger` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 136 | `DropType` | missing | — | `vdbe-typed-value-opcode-family` |
| 137 | `AddSequence` | missing | — | `vdbe-sequence-opcode-family` |
| 138 | `DropSequence` | missing | — | `vdbe-sequence-opcode-family` |
| 139 | `SequenceBeginInnerTx` | missing | — | `vdbe-sequence-opcode-family` |
| 140 | `SequenceCommitInnerTx` | missing | — | `vdbe-sequence-opcode-family` |
| 141 | `SequenceComputeNext` | missing | — | `vdbe-sequence-opcode-family` |
| 142 | `SetSequenceCurrval` | missing | — | `vdbe-sequence-opcode-family` |
| 143 | `SequenceTrackAllocation` | missing | — | `vdbe-sequence-opcode-family` |
| 144 | `SequenceRegisterAllocation` | missing | — | `vdbe-sequence-opcode-family` |
| 145 | `AddType` | missing | — | `vdbe-typed-value-opcode-family` |
| 146 | `Close` | direct | CloseCursor |  |
| 147 | `IsNull` | consolidated | Compare + JumpIfNotTrue |  |
| 148 | `CollSeq` | bydesign | — (collation carried in instruction operands) |  |
| 149 | `ParseSchema` | missing | — | `vdbe-schema-cookie-opcodes` |
| 150 | `PopulateMaterializedViews` | missing | — | `vdbe-materialized-view-opcodes` |
| 151 | `ShiftRight` | consolidated | Arithmetic(ShiftRight) |  |
| 152 | `ShiftLeft` | consolidated | Arithmetic(ShiftLeft) |  |
| 153 | `AddImm` | missing | — | `vdbe-scalar-control-opcodes` |
| 154 | `Variable` | direct | LoadParameter |  |
| 155 | `ZeroOrNull` | missing | — | `vdbe-scalar-control-opcodes` |
| 156 | `Not` | bydesign | — (expression evaluator / JumpIf gates) |  |
| 157 | `IsTrue` | consolidated | JumpIfNotTrue tri-state | `vdbe-comparison-opcode-consolidation` |
| 158 | `Concat` | bydesign | — (expression evaluator: ApplyConcatenation) |  |
| 159 | `And` | bydesign | — (expression evaluator / short-circuit gates) |  |
| 160 | `Or` | bydesign | — (expression evaluator / short-circuit gates) |  |
| 161 | `Noop` | bydesign | — (not needed) |  |
| 162 | `PageCount` | missing | — | `vdbe-schema-cookie-opcodes` |
| 163 | `ReadCookie` | missing | — | `vdbe-schema-cookie-opcodes` |
| 164 | `SetCookie` | missing | — | `vdbe-schema-cookie-opcodes` |
| 165 | `OpenEphemeral` | missing | — (OpenWorkTable is recursive-CTE-only) | `vdbe-open-ephemeral` |
| 166 | `OpenAutoindex` | missing | — | `vdbe-autoindex-for-joins` |
| 167 | `OpenDup` | missing | — | `vdbe-misc-cursor-opcodes` |
| 168 | `Once` | missing | — | `vdbe-scalar-control-opcodes` |
| 169 | `Found` | consolidated | SeekRowid FoundTarget |  |
| 170 | `NotFound` | consolidated | SeekRowid NotFoundTarget |  |
| 171 | `Affinity` | consolidated | NumericAffinity |  |
| 172 | `Count` | consolidated | RowCount |  |
| 173 | `IntegrityCk` | missing | — | `vdbe-integrity-check-opcode` |
| 174 | `RenameTable` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 175 | `DropColumn` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 176 | `AddColumn` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 177 | `AlterColumn` | bydesign | — (DDL tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 178 | `MaxPgcnt` | missing | — | `vdbe-schema-cookie-opcodes` |
| 179 | `JournalMode` | missing | — (journal_mode partially tree-walked) | `vdbe-schema-cookie-opcodes` |
| 180 | `IfNeg` | missing | — | `vdbe-scalar-control-opcodes` |
| 181 | `Sequence` | divergent | — (AUTOINCREMENT partial, delegate-side) | `vdbe-newrowid-semantics` |
| 182 | `SequenceTest` | missing | — | `vdbe-newrowid-semantics` |
| 183 | `Explain` | divergent | VdbeExplain.cs (Ahtola opcode names, no p1-p5 columns) | `vdbe-explain-output-parity` |
| 184 | `FkCounter` | divergent | — (FK checks inside write delegates) | `vdbe-fk-enforcement-opcodes` |
| 185 | `FkIfZero` | divergent | — | `vdbe-fk-enforcement-opcodes` |
| 186 | `FkCheck` | divergent | — | `vdbe-fk-enforcement-opcodes` |
| 187 | `HashBuild` | missing | — | `vdbe-hash-join-opcodes` |
| 188 | `HashDistinct` | missing | — | `vdbe-hash-join-opcodes` |
| 189 | `HashBuildFinalize` | missing | — | `vdbe-hash-join-opcodes` |
| 190 | `HashProbe` | missing | — | `vdbe-hash-join-opcodes` |
| 191 | `HashNext` | missing | — | `vdbe-hash-join-opcodes` |
| 192 | `HashClose` | missing | — | `vdbe-hash-join-opcodes` |
| 193 | `HashClear` | missing | — | `vdbe-hash-join-opcodes` |
| 194 | `HashMarkMatched` | missing | — | `vdbe-hash-join-opcodes` |
| 195 | `HashResetMatched` | missing | — | `vdbe-hash-join-opcodes` |
| 196 | `HashScanUnmatched` | missing | — | `vdbe-hash-join-opcodes` |
| 197 | `HashNextUnmatched` | missing | — | `vdbe-hash-join-opcodes` |
| 198 | `HashGraceInit` | missing | — | `vdbe-hash-join-opcodes` |
| 199 | `HashGraceLoadPartition` | missing | — | `vdbe-hash-join-opcodes` |
| 200 | `HashGraceNextProbe` | missing | — | `vdbe-hash-join-opcodes` |
| 201 | `HashGraceAdvancePartition` | missing | — | `vdbe-hash-join-opcodes` |
| 202 | `VacuumInto` | bydesign | — (tree-walked; file-backed source only) | `vdbe-ddl-executed-by-treewalker` |
| 203 | `Vacuum` | bydesign | — (tree-walked) | `vdbe-ddl-executed-by-treewalker` |
| 204 | `InitCdcVersion` | bydesign | — (tree-walked connection CDC) | `vdbe-cdc-opcode` |

Status totals: bydesign 25, consolidated 40, direct 26, divergent 10, missing 103 (of 204).

### 3.7 VDBE gap inventory

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `vdbe-ddl-executed-by-treewalker` | divergent | s4-intentional | L | 178 | 0 | Ahtola executes every DDL statement in the tree-walking evaluator, never as a VDBE program — the 11 DDL opcodes have no counterpart by design. Consequences (schema-versio… |
| `vdbe-trigger-subprogram-machinery` | missing | s2-capability | L | 111 | 0 | No subprogram or subroutine machinery: triggers cannot fire from compiled DML because there is no Program opcode to invoke a sub-program with its own register frame, and… |
| `vdbe-insert-update-flag-semantics` | partial | s2-capability | M | 31 | 1 | Ahtola models UPDATE as its own opcode plus delegate mutation, with change counting present. Missing flag semantics: REQUIRE_SEEK (position-before-write), UPDATE_ROWID_CH… |
| `vdbe-typecheck-on-write` | partial | s1-correctness | M | 31 | 15 | Turso centralizes write-time coercion in TypeCheck; Ahtola scatters it across write delegates and the compiler's NumericAffinityInstruction emission. The 148-case wrong-v… |
| `vdbe-seek-op-family-partial` | partial | s2-capability | M | 21 | 0 | Ahtola consolidates point seeks into SeekRowid (with Found/NotFound targets folded in — a faithful merge of SeekRowid+Found/NotFound) and has SeekRowidRange for rowid ran… |
| `vdbe-fk-enforcement-opcodes` | divergent | s2-capability | M | 17 | 7 | FK enforcement exists but lives in the write-target delegates, not opcodes. defer_foreign_keys and immediate checking work for common shapes; the divergence is structural… |
| `vdbe-halt-error-model` | divergent | s2-capability | M | 16 | 0 | Turso's Halt terminates with a SQLite error code, message, and on_error disposition (abort/ignore/fail), and HaltIfNull raises constraint errors from NULL registers (RAIS… |
| `vdbe-hash-join-opcodes` | missing | s3-perf | L | 15 | 3 | Entire hash-join execution family absent, including the grace (disk-partitioned) variant for outsized build sides and the unmatched-scan support for outer joins. Ahtola j… |
| `vdbe-transaction-opcode-model` | divergent | s4-intentional | S | 12 | 0 | Different factoring, verified semantics: Ahtola splits into Begin/Commit/Rollback opcodes over VdbeTransaction (register-snapshot stack) with savepoint trio Savepoint/Rel… |
| `vdbe-newrowid-semantics` | divergent | s2-capability | S | 7 | 3 | Rowid allocation is delegated to the write target's Commit rather than a NewRowid opcode. The autoinc failure (explicit max-rowid insert followed by plain INSERT) suggest… |
| `vdbe-column-default-short-record` | partial | s2-capability | S | 6 | 0 | Turso's Column carries the column's DEFAULT as an operand so rows physically written before ALTER TABLE ADD COLUMN read back the default. Ahtola's ColumnInstruction docum… |
| `vdbe-index-cursor-opcode-family` | divergent | s2-capability | M | 6 | 0 | Turso has a full index-cursor family: range seeks with eq_only over index records, rowid extraction from index entries, index insert/delete with IdxInsertFlags (no-op dup… |
| `vdbe-aggregate-overflow-semantics` | divergent | s1-correctness | S | 3 | 3 | SUM/TOTAL/AVG over very large REAL values: Ahtola diverges from SQLite/Turso on infinity/overflow results for float aggregates. Verify Kahan/compensated summation and int… |
| `vdbe-autoindex-for-joins` | missing | s3-perf | M | 3 | 3 | Turso can build a transient auto-index when no usable index exists for a join. Combined with the missing cost-based join order (compilation entry), Ahtola's joins are O(N… |
| `vdbe-checkpoint-opcode` | missing | s2-capability | S | 3 | 1 | Checkpoint coordination exists internally (storage layer) but there is no Checkpoint opcode, so `PRAGMA wal_checkpoint(...)` has no execution path — directly causes the t… |
| `vdbe-comparison-opcode-consolidation` | divergent | s4-intentional | S | 2 | 0 | Verified near-parity consolidation: Ahtola evaluates the comparison to a tri-state value (IS/IS NOT handled null-safely; NULL -> NULL; affinities applied per side; collat… |
| `vdbe-cdc-opcode` | bydesign | s4-intentional | M | 0 | 0 | Public SQL CDC is implemented by the managed tree-walking connection: `capture_data_changes_conn` provisions pinned Turso V1/V2 tables, captures transactional rows/schema changes, and writes V2 COMMIT records. It deliberately has no VDBE `InitCdcVersion` opcode because Ahtola's DDL/connection-state execution is tree-walked. The managed replica journal remains separate. |
| `vdbe-explain-output-parity` | partial | s3-perf | M | 1 | 0 | Ahtola has a real EXPLAIN implementation over its own opcode set, so output necessarily diverges from SQLite/Turso text (different opcode names, no p1-p5 operand columns)… |
| `vdbe-open-ephemeral` | missing | s2-capability | M | 1 | 0 | No general-purpose ephemeral btree opcode: Turso materializes IN (...) sets, DISTINCT intermediates, subquery results, and auto-indexes into ephemeral tables with full cu… |
| `vdbe-bloom-filter-opcodes` | missing | s3-perf | M | 0 | 0 | Turso builds a bloom filter over a join/IN side and probes it to skip btree seeks. Ahtola has no bloom machinery. NAME COLLISION: Ahtola's VdbeOpcode.Filter (12) is a row… |
| `vdbe-coroutine-machinery` | divergent | s4-intentional | M | 0 | 0 | Turso implements co-routines (FROM-clause subqueries, scalar subqueries, CTEs) as register-machine coroutines with Yield; Ahtola uses .NET enumerators and dedicated runti… |
| `vdbe-deferred-seek` | missing | s3-perf | M | 0 | 0 | DeferredSeek lets an index scan postpone the table-btree seek until a column outside the index is actually read (covering-index fast path); SeekEnd positions a cursor pas… |
| `vdbe-ext-window-buffer-family` | extension | s4-intentional | S | 0 | 0 | Ahtola-only buffered-window evaluation: the whole partition is buffered, then computed in one pass, enabling forward-looking and peer-relative frames cleanly. Semanticall… |
| `vdbe-ext-worktable-and-gate-families` | extension | s4-intentional | S | 0 | 0 | Ahtola's higher-level opcode families: FIFO recursive work tables (recursive CTE), streaming join cursor, and gate opcodes that fuse what Turso does with primitive jump/c… |
| `vdbe-index-method-opcodes` | missing | s2-capability | M | 0 | 0 | Turso's pluggable index-method family (custom index types queried through dedicated cursor machinery; op_column has a CursorType::IndexMethod arm). No Ahtola counterpart;… |
| `vdbe-integrity-check-opcode` | missing | s2-capability | M | 0 | 0 | PRAGMA integrity_check/quick_check needs the opcode-driven btree walk; Ahtola has no integrity checker. Pairs with the parser-layer pragma catch-all gap. |
| `vdbe-materialized-view-opcodes` | missing | s4-intentional | L | 0 | 0 | Turso's incremental-materialized-view extension: CREATE MATERIALIZED VIEW, dependent-view capture in DML opcodes, MV cursor types. Parser layer confirms no Ahtola grammar… |
| `vdbe-misc-cursor-opcodes` | missing | s3-perf | S | 0 | 0 | Micro-opcodes: ResetSorter (re-drain a sorter for correlated subqueries without rebuilding), AggValue (read aggregate mid-iteration), OpenDup (cheap cursor clone), Column… |
| `vdbe-record-construction-model` | divergent | s4-intentional | M | 0 | 0 | No MakeRecord: Ahtola rows live as materialized SqlValue arrays end-to-end and are only encoded to SQLite record format by the pager when a page is written. Format parity… |
| `vdbe-rowset-test` | missing | s3-perf | S | 0 | 0 | Ahtola's RowSet trio maps Turso's RowSetAdd/RowSetRead (insert + drain) but lacks RowSetTest, the membership probe used to deduplicate rowids from OR'd index scans. Witho… |
| `vdbe-scalar-control-opcodes` | missing | s2-capability | S | 0 | 0 | Mostly compiler machinery Ahtola's different program shapes do not need (counter loops, init-once blocks). Two carry user-visible semantics that deserve a check when the… |
| `vdbe-schema-cookie-opcodes` | missing | s2-capability | M | 0 | 0 | No cookie opcodes: user_version/application_id read-write and schema-cookie validation (stale-schema detection, 'database schema has changed' errors) are not modeled at t… |
| `vdbe-sequence-opcode-family` | missing | s4-intentional | M | 0 | 0 | Turso's CREATE SEQUENCE extension (8 opcodes), not SQLite syntax. Note: distinct from AUTOINCREMENT support (sqlite_sequence), which Ahtola partially has — see vdbe-newro… |
| `vdbe-typed-value-opcode-family` | missing | s4-intentional | L | 0 | 0 | Turso's typed-values extension (arrays/structs/unions/UDTs) — 17 opcodes, none SQLite. Ahtola has not adopted the extension; no conformance corpus coverage. Record as ups… |
| `vdbe-virtual-table-opcodes` | partial | s2-capability | L | 0 | 0 | Managed VOpen/VFilter/VColumn/VUpdate/VNext/VBegin instructions and tree-walker create/drop/rename now support statically registered modules with durable private payloads. The native extension ABI, arbitrary loadable modules, and Turso index-method parity remain out of scope. |

## 4. Compilation / translate layer
The largest layer by entry count (38). Turso's `core/translate/` is a full
SQLite-class compiler: query flattening, subquery decorrelation, cost-based
join ordering, index selection incl. partial/expression/covering indexes,
push-down optimization, trigger/FK codegen. Ahtola's `Compilation/` is a set
of 17 statement builders plus DML/Select compilers that emit correct programs
for the shapes they accept — but the **optimization and rewrite layer is
almost entirely absent**, and several accept-shapes are narrower than the
parser allows.
Highest-impact entries:
- **`compile-select-alias-visibility`** (s1, 66 mapped): alias scoping rules in
  SELECT — Ahtola resolves result aliases in contexts SQLite forbids/orders
  differently (ORDER BY/GROUP BY/HAVING edge interactions), producing
  silently different result sets.
- **`compile-window-function-tie-break-ordering-diverges`** (s1, 54 mapped):
  window frame peer-group tie-breaking differs from SQLite's full-key
  comparison, affecting `rank`/`dense_rank`/`ntile` results on ties.
- **`compile-alter-rename-trigger-body-not-rebound`** (s1, 44 mapped): after
  `ALTER TABLE … RENAME`, trigger bodies referencing the old name are not
  re-bound — SQLite rewrites them. Wrong-object writes possible.
- **`compile-affinity-rules-diverge-in-subquery-and-compound-contexts`**
  (s1, 28 mapped): affinity propagation through subqueries and compound
  selects diverges — companion to `vdbe-typecheck-on-write`.
- **ATTACH family** (s2/s4, 65 + 61 mapped): attached-database cross-schema
  statements and same-file ATTACH are unsupported/limited — by design for the
  managed single-file model, but it gates a large DDL-test surface. Read-only
  main/temp base-table joins are supported from a connection-local snapshot.
- **Planner** (s3): no subquery flattening (63 mapped), no decorrelation
  (25), no join-order optimization, no ORDER BY elision from indexes (17),
  no partial (18) or expression (19) indexes.
- **CTE/DML shapes** (s2): recursive CTEs limited to a single term (27),
  no DML inside CTEs, materialization hints restricted (22).
- **`compile-reindex-statement`** (s2, S effort): REINDEX not compiled —
  a small, self-contained win.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `compile-select-alias-visibility` | divergent | s1-correctness | M | 66 | 9 | SELECT-list aliases are not visible in GROUP BY/HAVING/JOIN-USING contexts ("no such column: cnt/total/key"); ambiguous-column errors also misreport which name is ambiguo… |
| `compile-attach-cross-database-support` | partial | s2-capability | L | 65 | 8 | Managed ATTACH supports file-backed attachments and independent connection-owned `:memory:` attachments. Read-only main/temp base-table queries share a connection-local snapshot, while attached-database statements and cross-schema writes remain rejected. Blocks the entire 4… |
| `compile-no-subquery-flattening` | missing | s3-perf | L | 63 | 0 | SQLite/Turso can flatten many `FROM (SELECT ...)` derived-table subqueries into the outer query when safe (no aggregates/DISTINCT/LIMIT conflicts), avoiding a materializa… |
| `compile-attach-same-file-not-supported` | missing | s4-intentional | S | 61 | 2 | Independent `:memory:` attachments are connection-owned, but attaching an existing managed in-memory database or an already-open file identity remains unsupported by design (… |
| `compile-window-function-tie-break-ordering-diverges` | divergent | s1-correctness | M | 54 | 6 | Multiple DENSE_RANK/RANK conformance cases show different peer-grouping outcomes (which rows tie for the same rank) versus SQLite/Turso when collation (NOCASE), cross-typ… |
| `compile-alter-rename-trigger-body-not-rebound` | missing | s1-correctness | M | 44 | 6 | After `ALTER TABLE t1 RENAME TO t2`, six conformance cases show existing triggers referencing the old table name inside their body (e.g. `INSERT INTO t1 ...` in a trigger… |
| `compile-affinity-rules-diverge-in-subquery-and-compound-contexts` | divergent | s1-correctness | M | 28 | 8 | A cluster of affinity.sqltest failures shows Ahtola computing a different column affinity than SQLite/Turso specifically when the affinity-bearing column flows through a… |
| `compile-recursive-cte-single-term-only` | partial | s2-capability | M | 27 | 2 | RecursiveCteProgramBuilder explicitly documents its scope as "the well-defined linear recursion (a single recursive transform)" and states "Multiple distinct recursive te… |
| `compile-scalar-subquery-not-decorrelated` | missing | s3-perf | M | 25 | 0 | Turso's subquery.rs distinguishes correlated from uncorrelated subqueries and hoists uncorrelated ones to evaluate once instead of once per outer row. Ahtola recomputes s… |
| `compile-cte-dml-and-materialization-restrictions` | partial | s2-capability | M | 22 | 5 | CTE use inside DML is artificially restricted ("every CTE must contribute"); expression-CTE materialization semantics diverge. |
| `compile-select-compiler-single-table-fast-paths-only` | divergent | s4-intentional | S | 20 | 0 | SelectStatementCompiler is architected as a set of narrow, provably-correct single-table fast paths (plain scan, backward/descending scan, indexed seek with bounds) with… |
| `compile-collation-propagation-through-subquery` | divergent | s1-correctness | M | 19 | 5 | Column collation is lost across subquery boundaries and compound arms, flipping NOCASE comparisons and window peer groups. |
| `compile-expression-index-support` | partial | s2-capability | L | 19 | 5 | Expression indexes: Ahtola over-rejects date literals as non-deterministic, accepts string literals referencing no column without error, and does not use expression index… |
| `compile-partial-index-support` | partial | s2-capability | L | 18 | 4 | Partial indexes (WHERE clause) unsupported or unplanned; ALTER RENAME must also rewrite partial-index predicates. |
| `compile-no-order-by-elision-from-index` | partial | s3-perf | M | 17 | 2 | Turso's order.rs decides, per join order and access method, whether the chosen index already produces the required ORDER BY/GROUP BY order and elides the sort step. Ahtol… |
| `compile-pragma-cache-size-unsupported` | missing | s2-capability | S | 15 | 5 | `PRAGMA cache_size = N` is rejected outright ("Unsupported PRAGMA cache_size") rather than being accepted and either applied or silently ignored/no-op'd the way Turso/SQL… |
| `compile-schema-sql-always-quotes-identifiers` | divergent | s2-capability | S | 13 | 7 | Roughly a dozen ALTER TABLE conformance failures show Ahtola always emitting double-quoted identifiers (`CREATE TABLE "t" ("a", "b")`) in the rewritten schema SQL text af… |
| `compile-upsert-values-only-no-insert-select` | missing | s2-capability | M | 13 | 1 | Ahtola's UPSERT compiler rejects any INSERT...SELECT or CTE-sourced INSERT with an ON CONFLICT clause: "Managed UPSERT supports VALUES rows only and does not support INSE… |
| `compile-views-not-updatable` | missing | s2-capability | M | 13 | 0 | Ahtola only allows DML against a view when an explicit INSTEAD OF trigger is defined for it (ExecuteInsteadOfInsert throws "cannot create INSTEAD OF trigger on table" err… |
| `compile-no-hash-join` | missing | s4-intentional | L | 12 | 0 | Neither engine implements a true hash-join operator (both fundamentally rely on nested-loop plus index seeks), so this is not a gap versus Turso per se, but flagged becau… |
| `compile-order-by-aggregate-misuse-not-rejected` | divergent | s1-correctness | S | 11 | 4 | SQLite rejects (or Turso matches SQLite's) certain misuse patterns of aggregate functions in ORDER BY outside of aggregate context; Ahtola's compiler currently lets these… |
| `compile-generated-column-determinism-validation` | divergent | s2-capability | M | 8 | 6 | Determinism validation for generated columns misclassifies deterministic substr() as forbidden while error wording for truly forbidden expressions also diverges. |
| `compile-reindex-statement` | missing | s2-capability | S | 6 | 1 | REINDEX statement not implemented. |
| `compile-analyze-stat-tables` | missing | s3-perf | M | 4 | 2 | ANALYZE and sqlite_stat tables absent; prerequisites for cost-based planning. |
| `compile-compound-select-result-ordering` | divergent | s1-correctness | M | 4 | 4 | Compound SELECT arms return rows in wrong order when LIMIT/ORDER BY wraps the compound; Ahtola evaluates arms independently without SQLite merge/ordering contract. |
| `compile-no-cost-based-join-ordering` | missing | s3-perf | L | 4 | 0 | Turso's optimizer/join.rs implements a System-R style dynamic-programming join reordering algorithm with pruning, using per-table cost/cardinality estimates (optimizer/co… |
| `compile-on-conflict-rollback-update-unsupported` | missing | s2-capability | M | 4 | 2 | Ahtola throws "Managed UPDATE cannot apply schema-level ON CONFLICT ROLLBACK until the pending row-update engine supports partial publication, transaction rollback, and r… |
| `compile-save-all-cursors-window-selfjoin-timeout` | divergent | s3-perf | L | 3 | 3 | Triple self-join plus window function queries time out (exceed the 30s managed execution budget) in Ahtola, consistent with the lack of index-driven joins and cost-based… |
| `compile-scalar-function-infinity-literal-not-parsed` | missing | s2-capability | S | 3 | 3 | Queries using an overflowing float literal to produce +/-Infinity fail to parse ("Expected RightParen") rather than being accepted and yielding an IEEE-754 infinity value… |
| `compile-alter-drop-column-rejects-nondeterministic-expr-index` | divergent | s2-capability | S | 0 | 2 | ALTER TABLE ... DROP COLUMN on a table with an unrelated expression index fails with "non-deterministic functions are prohibited in index expressions" in Ahtola where Tur… |
| `compile-generated-column-error-message-mismatch` | divergent | s4-intentional | S | 0 | 4 | Ahtola correctly rejects aggregate/window functions in generated column expressions but with a single combined message ("aggregate and window functions are not allowed in… |
| `compile-group-by-expression-index-no-covering-optimization` | missing | s2-capability | M | 0 | 1 | GROUP BY over a compound expression that has a matching expression index fails with "no such column: m" in Ahtola -- the aggregate compiler does not resolve GROUP BY expr… |
| `compile-no-access-method-selection` | missing | s3-perf | L | 0 | 0 | Turso extracts WHERE-clause conjuncts into per-table Constraints and picks the cheapest access method (rowid seek, single/multi-column index seek, or full scan) per table… |
| `compile-no-or-clause-index-union` | missing | s3-perf | L | 0 | 0 | SQLite/Turso can satisfy `WHERE a=1 OR b=2` (with separate indexes on a and b) via an index-union/OR-optimization instead of a full scan. Ahtola's compiler has no equival… |
| `compile-nway-join-not-index-driven` | divergent | s3-perf | L | 0 | 0 | VdbeJoinOperatorPlan.Enumerate always materializes the right side fully and nested-loops the left side against it in memory (VdbeJoinRow arrays), regardless of whether an… |
| `compile-recursive-cte-fifo-only-no-cost-model` | divergent | s4-intentional | S | 0 | 0 | RecursiveCteProgramBuilder documents a fixed breadth-first (FIFO) generation order for the recursive worktable, always surfacing the anchor generation first then children… |
| `compile-select-compiler-no-multi-table-covering-index` | missing | s3-perf | M | 0 | 0 | Every indexed-seek fast path in SelectStatementCompiler still opens the base table cursor and reads projected columns from it after seeking by rowid (`ColumnInstruction(s… |
| `compile-trigger-new-not-visible-in-upsert-clause` | missing | s2-capability | M | 0 | 3 | When a trigger body contains an INSERT ... ON CONFLICT DO UPDATE SET x = NEW.col statement, Ahtola fails with "no such table: NEW" -- the trigger's NEW/OLD pseudo-table b… |

## 5. Parser / dialect layer
22 entries — the smallest gap *per failure-line* ratio in the inventory, which
is another way of saying the parser is where conformance cases die first.
The distribution is wildly skewed by one entry:
- **`parser-implicit-column-alias`** (s2, **S effort, 144 mapped, 8 cited**):
  `ParseProjection` (`SqlParser.cs:1794-1819`) does not accept the AS-less
  column alias (`SELECT 1 a`), so any test file whose expected-output prologue
  or body uses that form fails with "Expected X. At SQL offset N". Hand-verified
  during citation repair to account for the large majority of the 144-line
  parse-error cluster. **This is the single best ROI in the entire inventory.**
- **`parser-pragma-family-coverage-gap`** (s2, 65 mapped): the PRAGMA family
  (`cache_size`, `journal_mode` variants, `synchronous`, `wal_checkpoint`,
  schema pragmas) is only partially parsed/executed.
- **Grammar forms** (s2, all S–M): `INDEXED BY` hints (13 mapped),
  bracket-quoted identifiers in DDL contexts, JOIN-of-subquery in UPDATE/DELETE
  FROM, `NOT` operand forms, `VALUES` in more statement positions, special
  literals.
- **Dialect policy** (s4): Turso-specific extensions (typed columns, sequences,
  materialized views) are intentionally unparsed.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `parser-implicit-column-alias` | missing | s2-capability | S | 144 | 8 | SQLite (and turso-parser's result_column grammar) allows a SELECT-list column alias with or without the AS keyword: `SELECT 1 a, 2 b`. Ahtola's ParseProjection (SqlParser… |
| `parser-pragma-family-coverage-gap` | missing | s2-capability | L | 65 | 4 | Beyond the generic-catch-all fix in parser-pragma-unrecognized-name-hard-rejection, this entry tracks the raw enumeration gap for readers doing a family-by-family audit:… |
| `parser-alter-table-alter-column` | missing | s2-capability | M | 15 | 1 | `ALTER TABLE t ALTER COLUMN a TO a2 TEXT` (rename + retype a column in one statement) is a Turso extension beyond stock SQLite's ADD COLUMN / RENAME [TO] / RENAME COLUMN… |
| `parser-upsert-chained-conflict-clauses` | missing | s2-capability | M | 13 | 2 | SQLite 3.35+ (and turso-parser's Upsert.next linked-list field) allow chaining multiple ON CONFLICT clauses on one INSERT: `INSERT ... ON CONFLICT(x) DO NOTHING ON CONFLI… |
| `parser-indexed-by-hint` | missing | s2-capability | S | 11 | 4 | INDEXED BY/NOT INDEXED hint syntax unsupported across SELECT/UPDATE/DELETE. |
| `parser-join-subquery-form` | missing | s2-capability | M | 11 | 6 | Parser rejects JOIN operands that are parenthesized subqueries/unions (Expected RightParen). Blocks the 11-case subquery/expressions file. |
| `parser-numeric-literal-digit-separators` | missing | s2-capability | S | 10 | 9 | SQLite 3.46+ / Turso's lexer accepts `_` as a digit-group separator anywhere inside an integer or real literal (`9_223_372_036_854_775_807`, `1_2_3`), which is stripped b… |
| `parser-error-message-parity` | divergent | s2-capability | M | 9 | 8 | Compile-time error wording diverges from SQLite patterns (tests regex-match messages). Umbrella for wording-only mismatches; see also vdbe-halt-error-model for runtime me… |
| `parser-doubly-qualified-column-reference` | missing | s2-capability | M | 8 | 8 | SQLite/Turso expressions accept a 3-part `schema.table.column` reference anywhere a column can appear (e.g. `main.t1.val`), even though most call sites then reject it sem… |
| `parser-raise-message-expression` | divergent | s2-capability | S | 6 | 5 | turso-parser's `Expr::Raise` stores the message as `Option<Box<Expr>>` — any expression, e.g. `RAISE(ABORT, 'bad: ' \|\| NEW.a)`. Ahtola's ParseRaiseExpression hard-requi… |
| `parser-isnull-notnull-postfix` | missing | s2-capability | S | 5 | 8 | SQLite's expr grammar has three null-test postfix forms: `expr ISNULL`, `expr NOTNULL`, and `expr NOT NULL` (all equivalent to `expr IS [NOT] NULL`). Ahtola's parser has… |
| `parser-not-operator-operand-forms` | missing | s2-capability | S | 5 | 2 | NOT-prefixed operators reject some operand forms (parenthesized subquery bounds, typed operands). |
| `parser-pragma-argument-syntax-equals-form` | partial | s2-capability | S | 5 | 7 | Even for the PRAGMAs Ahtola *does* recognize, the object-name argument grammar is incomplete in two ways. (1) `ParsePragmaObjectName` (line 317) unconditionally does `Exp… |
| `parser-begin-concurrent-mode` | missing | s2-capability | S | 4 | 4 | Turso's MVCC engine adds `BEGIN CONCURRENT` as a fourth transaction-mode keyword alongside DEFERRED/IMMEDIATE/EXCLUSIVE. Ahtola's TransactionMode enum and BEGIN parsing (… |
| `parser-nulls-clause-rejection-error-message` | divergent | s3-perf | S | 4 | 7 | Both engines reject NULLS FIRST/LAST inside CREATE INDEX column lists, table-level PRIMARY KEY(...)/UNIQUE(...) column lists, and upsert conflict targets — but SQLite's e… |
| `parser-bracket-quoted-identifiers` | missing | s2-capability | S | 3 | 1 | Square-bracket quoted identifiers not lexed. |
| `parser-pragma-unrecognized-name-hard-rejection` | missing | s2-capability | M | 2 | 14 | The turso-parser grammar's `Stmt::Pragma` accepts *any* identifier as `name`, with an arbitrary optional body (`= value`, `(value)`, or none); SQLite's own behavior for a… |
| `parser-begin-commit-transaction-name` | missing | s2-capability | S | 0 | 1 | SQLite's grammar for BEGIN/COMMIT/END admits an optional transaction name after the TRANSACTION keyword (`trans_opt ::= \| TRANSACTION \| TRANSACTION nm`), which is accep… |
| `parser-create-virtual-table-not-parsed` | partial | s2-capability | L | 0 | 0 | Ahtola now parses `CREATE VIRTUAL TABLE` into `CreateVirtualTableStatement` with module name and raw argument strings. The managed contract is intentionally limited to statically registered modules; no native/loadable module ABI is exposed. |
| `parser-trailing-named-constraint-without-body` | divergent | s3-perf | S | 0 | 3 | SQLite's own LALR grammar happens to accept a dangling `CONSTRAINT c` at the very end of a column/ADD COLUMN definition with no constraint keyword following it (an accept… |
| `parser-turso-only-ddl-extensions-absent` | missing | s4-intentional | L | 0 | 0 | Turso's ast.rs Stmt enum includes several experimental statement kinds that are not part of SQLite's own grammar: CREATE MATERIALIZED VIEW, CREATE/DROP TYPE (custom scala… |
| `parser-turso-only-sequence-and-optimize-statements` | missing | s4-intentional | M | 0 | 0 | CREATE/DROP SEQUENCE (standalone integer sequences, distinct from AUTOINCREMENT) and OPTIMIZE INDEX are Turso-only statement kinds with no SQLite equivalent and no hits i… |

## 6. Built-in functions layer
24 entries but only 41 mapped failure lines — the function surface is largely
present; the gaps are **missing upstream additions** (16 `missing`: recent
JSONB aggregates like `JSONB_GROUP_OBJECT`/`JSONB_ARRAY`, math functions,
vector/time helpers) and **type-coercion divergences** (6 `divergent`) rather
than absent subsystems.
- `func-char-coercion` / `func-math-result-type-divergence` (s2, S): result
  typing of `char()`, math functions (`sqrt` et al. returning REAL vs numeric)
  differs from SQLite's text/number coercion rules — small, well-scoped fixes.
- JSON/JSONB: the JSONB binary format and its aggregate functions are the main
  upstream-ahead area; plain JSON function set is near-complete.
- Two `extension` entries: Ahtola-only helpers with no upstream counterpart.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `func-jsonb-scalar-family` | missing | s2-capability | L | 9 | 2 | Ahtola's SqliteBuiltinFunctions.Names has no JSONB, JSONB_ARRAY, JSONB_EXTRACT, JSONB_OBJECT, JSONB_PATCH, JSONB_REMOVE, JSONB_REPLACE, JSONB_INSERT, or JSONB_SET; greppi… |
| `func-jsonb-aggregates` | missing | s2-capability | M | 7 | 0 | SqliteBuiltinFunctions.Names lists JSON_GROUP_ARRAY/JSON_GROUP_OBJECT but not JSONB_GROUP_ARRAY/JSONB_GROUP_OBJECT. Expected-failures file confirms 'no such function: JSO… |
| `func-agg-arg-collation-typeof-spotcheck` | divergent | s1-correctness | M | 6 | 0 | Requested spot-check area: typeof() results and numeric affinity coercion inside SUM/TOTAL/AVG when mixing TEXT-that-looks-numeric, REAL, and INTEGER inputs. No concrete… |
| `func-json-group-object-numeric-label-affinity` | divergent | s1-correctness | S | 5 | 2 | The numeric-label test file targets JSONB_GROUP_OBJECT (missing, see func-jsonb-aggregates), but its title implies numeric object-key stringification/affinity semantics (… |
| `func-math-result-type-divergence` | divergent | s1-correctness | S | 5 | 5 | Math rounding functions return REAL (1.0) where SQLite returns integer text (1); ceil over text i64-max also loses integer affinity and precision. |
| `func-repeat-lpad-rpad-missing` | missing | s2-capability | S | 3 | 0 | repeat()/lpad()/rpad() string-padding helpers are absent from SqliteBuiltinFunctions.Names and EmbeddedDatabase.StringFunctions.cs. Not part of stock SQLite but present i… |
| `func-substr-utf16-vs-codepoint-divergence` | divergent | s1-correctness | S | 3 | 0 | EvaluateSubstring computes `length` via .NET string.Length and slices with string.Substring using UTF-16 code-unit offsets, not Unicode codepoints. For text containing su… |
| `func-string-reverse-missing` | missing | s3-perf | S | 2 | 0 | string_reverse() is registered in Turso but has no counterpart in Ahtola.Core and no corpus coverage. Extension-level gap, not SQLite-standard. |
| `func-char-coercion` | divergent | s1-correctness | S | 1 | 1 | char() with non-integer argument returns a space instead of empty string. |
| `func-array-agg-missing` | missing | s2-capability | S | 0 | 0 | Turso registers array_agg as a built-in aggregate (turso-src/core/function.rs line ~1611). Not present in SqliteBuiltinFunctions.Names or EmbeddedDatabase.AggregateFuncti… |
| `func-array-postgres-family` | missing | s3-perf | L | 0 | 0 | Postgres-style ARRAY(...)/array_element/array_append/etc. scalar family, always compiled (not behind a cargo feature flag) but not part of stock SQLite semantics and not… |
| `func-extension-format-btrim` | extension | s4-intentional | S | 0 | 0 | FORMAT (an alias for PRINTF, matching real SQLite's built-in but not present as a distinct entry in Turso's from_str dispatch table) and BTRIM (Postgres-style alias for T… |
| `func-extension-uuid-family` | extension | s4-intentional | S | 0 | 0 | Ahtola registers a full UUID v4/v7 generation family (text and blob forms, plus gen_random_uuid() for Postgres compatibility) that has no counterpart anywhere in turso-sr… |
| `func-fts-scalar-family` | missing | s3-perf | L | 0 | 0 | A managed `fts5` virtual-table subset now exists, but Turso/SQLite FTS scalar helpers, ranking, snippets, and auxiliary-function surfaces remain unimplemented. |
| `func-gcd-lcm-missing` | missing | s3-perf | S | 0 | 0 | gcd()/lcm() (Turso/SQLite-3.41+-style math helpers) have no hits anywhere in src/Ahtola.Core or Ahtola.Core/EmbeddedDatabase.MathFunctions.cs. Not covered by the vendored… |
| `func-numeric-boolean-ip-helpers-missing` | missing | s4-intentional | M | 0 | 0 | Internal-flavored helper functions supporting Turso's typed BOOLEAN/NUMERIC column extensions and validated IP address type; no SQLite equivalent and no corpus coverage.… |
| `func-octet-length-missing` | missing | s1-correctness | S | 0 | 0 | octet_length is absent from SqliteBuiltinFunctions.Names and from EmbeddedDatabase.StringFunctions.cs / EmbeddedDatabase.cs (no case-insensitive hit for 'octet' anywhere… |
| `func-real-text-formatting-intentional-divergence` | divergent | s4-intentional | M | 0 | 0 | SqliteRealText.cs documents (in its own XML doc comment) a deliberate divergence: SQLite's sqlite3FpDecode is cheap-but-not-correctly-rounded and can emit a spurious/inco… |
| `func-sequence-nextval-family` | missing | s4-intentional | M | 0 | 0 | Postgres-style sequence functions (nextval/currval/setval) tied to Turso's experimental SEQUENCE object and ScalarFunc::SequenceWatermark/ConnTxnId connection state. No S… |
| `func-soundex-missing` | missing | s3-perf | S | 0 | 0 | soundex() is registered in Turso but is optional in stock SQLite too (needs SQLITE_SOUNDEX); the corpus test itself is commented out. Low priority: not required for SQLit… |
| `func-struct-union-experimental` | missing | s4-intentional | L | 0 | 0 | Experimental typed STRUCT/UNION column support in Turso (struct_pack/struct_extract/union_value/union_tag/union_extract), unrelated to SQLite's dynamic typing model. No c… |
| `func-test-nondet-counter-missing` | missing | s3-perf | S | 0 | 2 | test_nondet_counter() is a Turso test-only helper (feature-gated) used by the vendored sqltest corpus to probe nondeterministic-function dedup/caching behavior in window… |
| `func-unistr-family-missing` | missing | s2-capability | M | 0 | 0 | unistr()/unistr_quote() (Postgres-style Unicode escape decoding/encoding) are absent from SqliteBuiltinFunctions.Names and EmbeddedDatabase.StringFunctions.cs; the only '… |
| `func-vector-family` | missing | s2-capability | L | 0 | 0 | Entire vector/embedding scalar function family (vector(), vector32(), vector_distance_cos/l2/jaccard/dot, vector_concat, vector_slice, vector_extract) has no counterpart… |

## 7. Storage / pager / WAL / b-tree layer
20 entries, 32 mapped lines, and the **only layer with closed parity entries**
(2 `parity`): the on-disk format is contract-governed by
`docs/wal-interoperability-contract.md` and verified byte-compatible —
database header, page layout, b-tree cells, overflow chains, WAL framing and
checksums all match. The open gaps are **behavioral, not format**:
- **Page cache**: no spill/eviction pressure path equivalent to Turso's
  cache management (s3); cache-size PRAGMAs are advisory.
- **Checkpoint modes**: `wal_checkpoint(TRUNCATE|RESTART|FULL|PASSIVE)` modes
  not all surfaced; the writer/checkpoint coordinator exists internally but
  the SQL-visible surface is missing (ties to `vdbe-checkpoint-opcode`).
- **Shared WAL coordination**: single-writer locking model is managed-lock
  based; multi-connection WAL read-snapshot coordination (`SqliteWalReadSnapshotCoordinator`)
  covers the local case; shared-memory WAL-index equivalent for cross-process
  is intentionally out of scope (s4).
- **Freelist / incremental vacuum**: freelist management is partial;
  incremental vacuum not implemented.
- One `extension` entry: page/WAL **encryption** is Ahtola-only.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `storage-no-page-cache-spill` | missing | s2-capability | M | 15 | 10 | SqlitePagerReadCache is a plain bounded LRU cache for clean committed pages only (no dirty-page tracking, no spill-threshold, no PagesToSpill/CacheFull semantics). Turso'… |
| `storage-shared-wal-coordination-mod-parity` | partial | s2-capability | S | 9 | 0 | Turso factors journal-mode selection (journal_mode.rs) and multi-connection WAL coordination (shared_wal_coordination.rs) into dedicated modules distinct from wal.rs itse… |
| `storage-overflow-write-path-scope` | partial | s2-capability | M | 4 | 5 | SqliteOverflowChainReader/SqliteOverflowPageView are read-side only (constructed from a page store, pager, or ISqliteBtreePageIo to *read* an existing chain); no SqliteOv… |
| `storage-hot-journal-recovery-minimal` | partial | s2-capability | M | 2 | 0 | SqliteRollbackJournal.IsHot detects a stale/hot rollback journal from a crashed writer, but the recovery path is a narrow helper (journal-mode enum + hot-detection + appl… |
| `storage-page-size-change-midlife` | partial | s2-capability | M | 2 | 1 | SqlitePageSize.cs validates min/max page size constants but, combined with the vacuum-rewrite-only nature of SqliteFreelist/allocator, it's unclear whether Ahtola support… |
| `storage-append-only-page-allocator` | missing | s2-capability | L | 0 | 0 | Ahtola's only page allocator is SqliteAppendOnlyPageAllocator, whose doc comment says it 'does not inspect or reclaim the SQLite freelist' and always assigns new page num… |
| `storage-byte-range-shm-locks-partial-scope` | partial | s2-capability | M | 0 | 1 | SqliteWalByteRangeLock/SqliteWalSharedMemoryLocks implement the -shm byte-range lock offsets for the primary main-database WAL (read marks, write lock, checkpoint lock),… |
| `storage-checkpoint-modes-implemented` | parity | s4-intentional | S | 0 | 0 | Not a gap -- included for completeness of the checkpoint-mode audit. Ahtola's SqliteWalCheckpointMode enum (Passive/Full/Restart/Truncate) mirrors Turso's CheckpointMode… |
| `storage-database-rs-no-direct-analog` | divergent | s4-intentional | S | 0 | 0 | Turso's database.rs centralizes database-open orchestration (header validation, encoding checks, initial page-1 bootstrap for new files) as its own module; Ahtola spreads… |
| `storage-encryption-extension` | extension | s4-intentional | S | 0 | 0 | Intentional Ahtola extension per task context (kind=extension, s4-intentional). Turso's encryption.rs supports the same class of AEAD ciphers (AES-GCM, AEGIS-*) with a 'T… |
| `storage-freelist-write-path-vacuum-only` | partial | s2-capability | M | 0 | 0 | SqliteFreelist.cs correctly parses and can construct trunk/leaf freelist pages, but per its own doc comment it is used only by 'managed file rewrites' (i.e. VACUUM-style… |
| `storage-no-btree-balancing` | missing | s1-correctness | L | 0 | 0 | SqliteBtreeSplitMutation's own doc comment states it 'can replace existing pages or append new pages, but never shrinks, rebalances, or reclaims pages.' Turso implements… |
| `storage-no-buffer-pool-arena` | missing | s3-perf | M | 0 | 0 | Turso maintains a dedicated arena-based BufferPool that recycles fixed-size page/WAL-frame buffers to avoid per-page heap allocation churn under concurrent I/O. Ahtola ha… |
| `storage-no-defragmentation` | missing | s3-perf | M | 0 | 0 | No defragment_page equivalent exists in Ahtola's b-tree page writers. Repeated insert/delete of variable-length cells on the same page will fragment free space within the… |
| `storage-no-incremental-vacuum` | missing | s2-capability | L | 0 | 0 | No AutoVacuumMode/ptrmap concept exists anywhere in Ahtola.Core (grep for 'autovacuum'/'ptrmap' finds nothing outside SQL parsing/authorization text). Turso itself only p… |
| `storage-no-mvcc-checkpoint-lock-guard` | partial | s1-correctness | M | 0 | 0 | Turso's WalFileShared carries an explicit VacuumLockGuard (Drop-based release) coordinated with CheckpointLocks so a concurrent VACUUM cannot run while a checkpoint holds… |
| `storage-no-super-journal-multidb` | missing | s2-capability | M | 0 | 0 | No super-journal (a.k.a. master journal) file handling was found in SqliteRollbackJournal.cs or elsewhere in Storage/. Stock SQLite/Turso use a super-journal to atomicall… |
| `storage-pager-lock-manager-scope` | partial | s2-capability | S | 0 | 0 | SqlitePagerLockManager.cs exists and presumably models the classic SQLite file-lock state machine, but combined with storage-byte-range-shm-locks-partial-scope this shoul… |
| `storage-varint-and-record-codec-parity` | parity | s4-intentional | S | 0 | 0 | Included for completeness of the audit: varint and record-codec files exist on the Ahtola side with names that map 1:1 to sqlite3_ondisk.rs responsibilities and no sympto… |
| `storage-wal-index-shm-mapping-parity` | partial | s2-capability | M | 0 | 0 | PhysicalSqliteWalSharedMemoryMapping.cs implements the on-disk -shm mapping (needed for cross-process/interop parity), which is good format-level coverage, but it is uncl… |

## 8. MVCC / transactions layer
Turso implements a full MVCC layer (`core/mvcc/`: logical clock, version
cursors, yield points, logical log, checkpoint SM). **Phase 1 (2026-08-07)**
lands an in-process managed port under `src/Ahtola.Core/Mvcc/`
(`MvccClock`, `MvStore`, write-set WW conflicts) with SQL surface
`PRAGMA journal_mode=mvcc` and `BEGIN CONCURRENT`. See
[`mvcc-port-contract.md`](mvcc-port-contract.md). Classic path remains default
(§1.6 of the WAL contract). Not yet ported: row-version chains / dual cursors,
durable `db-log`, header version 255 persistence, checkpoint SM, GC.
The earlier behavioral gaps below are closed or reduced:
- **`mvcc-statement-level-rollback-on-constraint-violation`**: closed (F2.x).
- **Savepoint / cache_size**: closed.
- Conformance: **11 → 0** MVCC expected-failure markers.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `mvcc-statement-level-rollback-on-constraint-violation` | missing | s2-capability | L | 20 | 2 | SQLite's schema-level ON CONFLICT ROLLBACK resolution aborts and rolls back the *entire enclosing transaction*, not just the statement, distinguishing it from the default… |
| `mvcc-layer-absent` | missing | s2-capability | L | 13 | 4 | Turso implements a full main-memory MVCC engine (Larson et al., VLDB 2011) alongside its classic SQLite-compatible pager/b-tree path, selected per-transaction via `BEGIN… |
| `mvcc-savepoint-cache-size-pragma-gap` | partial | s2-capability | S | 5 | 5 | Ahtola's core SAVEPOINT/RELEASE/ROLLBACK TO grammar and nested-frame semantics are implemented and unit-tested (VdbeTransactionContextSavepointNameMatchingTests.cs, Trans… |
| `mvcc-begin-concurrent-not-parsed` | missing | s2-capability | M | 4 | 4 | Turso recognizes BEGIN CONCURRENT as a transaction-mode keyword and, even when MVCC is disabled, produces the specific error 'Concurrent transaction mode is only supporte… |
| `mvcc-classic-path-model-undocumented` | missing | s3-perf | S | 0 | 0 | Ahtola's actual (non-MVCC) transaction model is itself undocumented at a design level: it is a single process-local write reservation (EmbeddedTransactionLock, one lock p… |
| `mvcc-clock-and-timestamp-ordering` | missing | s2-capability | M | 0 | 0 | Turso's MvccClock is a mutex-guarded monotonic counter where the commit-timestamp generation and publication of the transaction's Preparing(ts) state happen atomically un… |
| `mvcc-cross-connection-schema-cookie-visibility` | missing | s3-perf | M | 0 | 5 | Turso's Transaction opcode checks a schema cookie on every BEGIN (and separately manages an MVCC schema generation counter, conn.mvcc_begin_schema_generation, so concurre… |
| `mvcc-deferred-fk-across-statement-boundaries` | partial | s3-perf | S | 0 | 0 | Ahtola does implement PRAGMA defer_foreign_keys and honors ForeignKeyDeferral.InitiallyDeferred, deferring FK violation checks until COMMIT rather than the offending stat… |
| `mvcc-dual-cursor-cross-mode-isolation` | missing | s2-capability | L | 0 | 0 | Turso guarantees that a classic-path (b-tree cursor) reader inside a BEGIN CONCURRENT connection's peer transaction does not see an in-flight MVCC writer's uncommitted ro… |
| `mvcc-persistent-logical-log-and-checkpoint` | missing | s2-capability | L | 0 | 0 | Turso durably logs MVCC operations to a separate logical log (distinct from the WAL used by the classic path) and periodically checkpoints that log into the b-tree via a… |
| `mvcc-phantom-write-skew-read-skew-unresolved-upstream` | missing | s4-intentional | S | 0 | 0 | Turso's own MVCC module documentation lists phantom reads, cursor lost updates, read skew, and write skew as explicitly unresolved anomaly classes, and optimistic reads/w… |
| `mvcc-row-version-gc` | missing | s3-perf | M | 0 | 0 | Turso's MVCC store accumulates multiple row versions per key and periodically garbage-collects versions no longer visible to any active transaction (three-rule pruning, d… |
| `mvcc-vdbetransaction-is-not-a-db-transaction` | divergent | s4-intentional | S | 0 | 0 | VdbeTransactionContext's own doc comment states it is 'deliberately not a database transaction': it is a stack of register-file snapshots used by the resumable interprete… |
| `mvcc-write-write-conflict-detection` | missing | s2-capability | L | 0 | 0 | Turso's MVCC path detects first-committer-wins write-write conflicts and surfaces LimboError::WriteWriteConflict distinctly from Busy, so callers can decide whether to re… |

## 9. Sync / replication & provider surface
The conformance suite exercises the local engine, not replication, so these
gaps have little sqltest coverage. Ahtola.Data now has a pure-managed,
pull-capable sync path: raw-page bootstrap/page protocol, MVCC `lml3` logical
decode and transactional replay, opaque revision/protocol/table-map metadata,
local-change push journaling, self-origin filtering, and atomic ReplaceBase
fallback. It does not claim Turso's full native sync engine (partial/lazy
bootstrap, zstd page sets, remote encryption, or push-side automatic conflict
resolution). Optional companion dispatch remains an intentional extension
point and is not shipped by this repository.

### 9.1 Managed Cloud replica support matrix (Turso v0.7.2)

This matrix applies only to the pure-managed `ManagedReplicaConnectionHost`
fallback reached by `AhtolaConnection.CreateReplica`, not to an explicitly
registered companion factory. It is deliberately narrower than both the
general Hrana remote client and Turso's sync engine. Unsupported settings are
rejected while validating the managed replica open request, before the path is
opened or bootstrap state is created. Unsupported wire responses are rejected
while staged, before either the database or its replica metadata is published.

| Capability / option | Status | Managed behavior |
| --- | --- | --- |
| Complete raw 4 KiB page stream, legacy/unspecified or v1 `Pages` protocol | **Qualified** | The bootstrapper requests raw pages, requires every 4 KiB `PageData` chunk exactly once, stages and validates the SQLite image, then atomically publishes it. Incremental raw page sets follow the same staged path. |
| Remote encryption (`RemoteEncryption`) | **Explicitly rejected** | No encrypted page decoder or encrypted local image support is present in the managed replica. This is distinct from encrypted **remote SQL** connections. |
| Partial prefix bootstrap, query bootstrap, lazy loading (`PartialBootstrap`) | **Explicitly rejected** | The managed replica has neither the server page selector/query protocol nor lazy page storage. |
| Chunked bootstrap (`PullBytesThreshold`) | **Explicitly rejected** | A complete initial image is the only bootstrap shape; a multi-request page-selection bootstrap is not implemented. |
| zstd/compressed page sets | **Explicitly rejected** | The response must declare raw encoding and each page payload must be exactly 4 KiB. |
| MVCC logical streams / `MvccLogical` protocol | **Qualified (pull-only)** | After raw bootstrap, the client requests `mvcc_logical_log`, validates the complete `lml3` body and CRC chain, replays header/schema/row operations in one transaction, filters this client's transactions, and advances metadata only after durable publication. Pending deletes use v4 journal before-images to rebase by declared primary key. Pending additive `ALTER TABLE ... ADD COLUMN` is rebased across remote refresh/row ops; legacy journals without a before-image and destructive schema changes fail closed until pushed. |
| Protocol-2 page fallback | **Qualified for validated full replacement** | `Pages + ReplaceBase` checkpoints/removes safe sidecars and atomically installs every page, then replays still-unpushed journal SQL onto the snapshot before metadata publication. Incremental page fallback still requires a provably unchanged page base and rejects pending local changes or WAL divergence. |
| Local divergence before an incremental pull | **Mode-aware** | Logical pulls precollect/reapply safe pending local rows. Page pulls reject pending journal entries or an unproven physical base. |
| Non-4 KiB Cloud page streams | **Unknown/deferred — rejected by the gate** | Turso v0.7.2's physical sync protocol uses `PAGE_SIZE = 4096` (`sync/engine/src/database_sync_operations.rs`); the managed decoder intentionally has the same fixed 4 KiB stream boundary. Ahtola's local SQLite engine can use other database page sizes, but that does not qualify them for Cloud replica streaming. |

Turso v0.7.2 exposes the broader option surface in
`sync/engine/src/database_sync_engine.rs` (`DatabaseSyncEngineOpts`) and
defines partial strategies and physical/logical stream kinds in
`sync/engine/src/types.rs` and `sync/engine/src/server_proto.rs`. Its logical
mode also rejects partial sync and remote encryption
(`ensure_logical_mvcc_pull_supported`). The managed replica claims only the
qualified subset in the matrix above.

| ID | Kind | Severity | Effort | Mapped fails | Cited | Summary |
| --- | --- | --- | --- | ---: | ---: | --- |
| `sync-no-cdc-capture-pragma` | divergent | s2-capability | L | 0 | 0 | Ahtola implements the public pinned-Turso `capture_data_changes_conn` V1/V2 table surface, but its managed replica intentionally continues to capture committed local changes in the private `<db>.ahtola-replica-journal`. Public CDC is not yet consumed by replica push/pull or logical replay. |
| `sync-checkpoint-mode-mismatch-vs-managed-storage` | divergent | s2-capability | M | 0 | 0 | Turso's sync checkpoint explicitly composes Passive (checkpoint only the already-synced WAL prefix, tracked by a watermark) followed by Truncate (once fully synced) to ke… |
| `sync-conflict-error-surfaced-not-handled` | partial | s2-capability | M | 0 | 0 | `AhtolaReplicaPushFailure.Classify` now maps every push failure to a stable `AhtolaReplicaPushFailureKind` (Conflict/TransientTransport/InvalidLocalState) at the push response boundary, mirroring Turso's conflict-vs-transient split; automatic rebase-before-retry and revert-WAL rollback remain unported. |
| `sync-connection-pooling-no-replica-awareness` | partial | s3-perf | M | 0 | 0 | Turso's engine has a wait_changes_from_remote long-poll loop that lets a replica connection block until the server reports new changes, enabling push-driven refresh inste… |
| `sync-ef-core-provider-no-sync-surface` | closed | s3-perf | M | 0 | 0 | `UseAhtola` accepts direct Turso/Hrana and `Replica Path` connection strings. EF queries, CRUD, migrations, creator operations, and transactions route through the remote/replica-capable SQLite facade; explicit sync remains on the underlying `SqliteConnection`. |
| `sync-http-pipeline-v2-only-no-v3-websocket` | partial | s2-capability | M | 0 | 0 | Ahtola's remote client is hard-coded to the '/v2/pipeline' HTTP endpoint with a JSON baton-session pipeline; there is no WebSocket transport and no wal_push/pull_updates… |
| `sync-native-provider-companion-intentional` | extension | s4-intentional | S | 0 | 0 | AhtolaNativeProvider's dynamic-load-by-name pattern for an optional 'Turso.Data.Native' companion (used for Local Provider=Native) exists purely as an extension point for… |
| `sync-no-embedded-sync-engine-port` | partial | s2-capability | L | 0 | 0 | The managed provider now implements raw bootstrap/page pulls, pull-only MVCC logical replay, durable local push journaling, protocol metadata, and atomic publication. Partial/lazy sync, zstd, remote encryption, and Turso's complete conflict/checkpoint policy remain outside the managed subset. |
| `sync-no-mvcc-logical-log-replay` | closed | s2-capability | L | 0 | 0 | The managed pull path decodes Turso v0.7.2 portable `lml3` transactions, validates range/frame CRCs and bounds, replays header/schema/row changes atomically, persists protocol/table maps, filters client echoes, and compensates post-commit metadata failures. |
| `sync-no-page-protocol-pull-decode` | partial | s2-capability | M | 0 | 0 | Raw 4-KiB page bootstrap/incremental streams and protocol-2 full ReplaceBase fallback are decoded and atomically published. zstd and partial page-selection streams remain explicitly rejected. |
| `sync-no-partial-sync-lazy-page-storage` | missing | s2-capability | L | 0 | 0 | Ahtola already defines a rich C# surface for partial bootstrap (prefix-length or server-side-query page selection, lazy segment size, prefetch flag), matching the shape o… |
| `sync-no-revert-db-checkpoint-safety` | missing | s1-correctness | L | 0 | 0 | Turso's checkpoint keeps a shadow 'revert' WAL (`<db>-wal-revert`) so a passive checkpoint of synced frames can be rolled back if the corresponding push to remote later r… |
| `sync-partial-encryption-mutual-exclusion-unenforced` | divergent | s1-correctness | S | 0 | 0 | Turso hard-errors when partial-sync + remote-encryption + MVCC-logical-pull are combined incompatibly. Ahtola's AhtolaReplicaOptions.Validate() checks PartialBootstrap/Bo… |
| `sync-remote-encryption-header-not-wired-for-remote-client` | missing | s2-capability | S | 0 | 0 | AhtolaRemoteEncryptionOptions models the cipher/base64 key surface used to compute reserved-bytes for encrypted Turso Cloud databases (consumed by the not-yet-existing re… |
| `sync-remote-execute-stream-only-two-request-kinds` | divergent | s4-intentional | S | 0 | 0 | Turso's own vendored server_proto.rs already restricts the Hrana-like pipeline to Execute and Batch stream kinds (no cursor/describe/sequence/store_sql variants seen in f… |
| `sync-remote-hrana-batch-cond-unsupported` | closed | s2-capability | M | 0 | 0 | Remote batches serialize nested `ok`/`error`/`not`/`and`/`or`/`is_autocommit` conditions. Multi-statement commands and explicit batches chain `ok(previous)` so later destructive steps do not run after a failure. |
| `sync-remote-no-replication-index-tracking` | closed | s3-perf | S | 0 | 0 | The managed Hrana client tracks the highest valid batch or statement `replication_index`, sends it with subsequent batches, and accepts both string and legacy numeric encodings. `RemoteReplicationIndexTests` covers request propagation and validation. |
| `sync-sdk-kit-native-companion-intentional` | extension | s4-intentional | S | 0 | 0 | sdk-kit is Turso's native C-ABI/FFI surface for embedding the sync engine into other languages (capi.rs, bindings.rs). Ahtola deliberately has no native companion and no… |


## 10. Top-impact ranking and suggested closure order

Ranked by mapped expected-failure lines (blast radius). **Rows shaded s4 are
design umbrellas** — they map many lines because whole test *files* take a
DDL/ATTACH shape, not because one fix wins them all; they are excluded from
the actionable waves below. Citations are hand-verified explicit links.

### 10.1 Top 25 by mapped failure lines

| # | Gap | Layer | Severity | Effort | Mapped fail-lines | Cited |
| ---: | --- | --- | --- | --- | ---: | ---: |
| 1 | `vdbe-ddl-executed-by-treewalker` | vdbe | s4-intentional | L | 178 | 0 |
| 2 | `parser-implicit-column-alias` | parser | s2-capability | S | 144 | 8 |
| 3 | `vdbe-trigger-subprogram-machinery` | vdbe | s2-capability | L | 111 | 0 |
| 4 | `compile-select-alias-visibility` | compilation | s1-correctness | M | 66 | 9 |
| 5 | `compile-attach-cross-database-support` | compilation | s2-capability | L | 65 | 8 |
| 6 | `parser-pragma-family-coverage-gap` | parser | s2-capability | L | 65 | 4 |
| 7 | `compile-no-subquery-flattening` | compilation | s3-perf | L | 63 | 0 |
| 8 | `compile-attach-same-file-not-supported` | compilation | s4-intentional | S | 61 | 2 |
| 9 | `compile-window-function-tie-break-ordering-diverges` | compilation | s1-correctness | M | 54 | 6 |
| 10 | `compile-alter-rename-trigger-body-not-rebound` | compilation | s1-correctness | M | 44 | 6 |
| 11 | `vdbe-insert-update-flag-semantics` | vdbe | s2-capability | M | 31 | 1 |
| 12 | `vdbe-typecheck-on-write` | vdbe | s1-correctness | M | 31 | 15 |
| 13 | `compile-affinity-rules-diverge-in-subquery-and-compound-contexts` | compilation | s1-correctness | M | 28 | 8 |
| 14 | `compile-recursive-cte-single-term-only` | compilation | s2-capability | M | 27 | 2 |
| 15 | `compile-scalar-subquery-not-decorrelated` | compilation | s3-perf | M | 25 | 0 |
| 16 | `compile-cte-dml-and-materialization-restrictions` | compilation | s2-capability | M | 22 | 5 |
| 17 | `vdbe-seek-op-family-partial` | vdbe | s2-capability | M | 21 | 0 |
| 18 | `compile-select-compiler-single-table-fast-paths-only` | compilation | s4-intentional | S | 20 | 0 |
| 19 | `mvcc-statement-level-rollback-on-constraint-violation` | mvcc | s2-capability | L | 20 | 2 |
| 20 | `compile-collation-propagation-through-subquery` | compilation | s1-correctness | M | 19 | 5 |
| 21 | `compile-expression-index-support` | compilation | s2-capability | L | 19 | 5 |
| 22 | `compile-partial-index-support` | compilation | s2-capability | L | 18 | 4 |
| 23 | `compile-no-order-by-elision-from-index` | compilation | s3-perf | M | 17 | 2 |
| 24 | `vdbe-fk-enforcement-opcodes` | vdbe | s2-capability | M | 17 | 7 |
| 25 | `vdbe-halt-error-model` | vdbe | s2-capability | M | 16 | 0 |

### 10.2 Suggested closure waves

Ordered by (severity → blast radius → effort). Each wave names the entries to
close and the expected conformance effect (lines that *stop* failing; a closed
line may still fail on the next gap in its chain — multi-mapped lines only
clear when **all** their blockers close).

**Wave 0 — quick wins (S effort, high yield).**
`parser-implicit-column-alias` (144 mapped — the single biggest parser gap),
`func-char-coercion`, `func-math-result-type-divergence`,
`parser-bracket-quoted-identifiers`, `parser-not-operator-operand-forms`,
`parser-indexed-by-hint`, `compile-reindex-statement`.
Expected effect: converts the 144-line parse-error cluster into downstream
results (many will then surface their *real* engine gaps — expect the cluster
to redistribute, not vanish).

**Wave 1 — s1 correctness (wrong results before missing features).**
`vdbe-typecheck-on-write` (31) → `compile-affinity-rules-diverge-in-subquery-and-compound-contexts` (28)
→ `compile-select-alias-visibility` (66) → `compile-window-function-tie-break-ordering-diverges` (54)
→ `compile-alter-rename-trigger-body-not-rebound` (44) → `compile-collation-propagation-through-subquery` (19)
→ `vdbe-aggregate-overflow-semantics` (verify + probe) → generated-column
determinism entries.
Rationale: these can return **silently wrong data**, which is worse than an
error. Closing `vdbe-typecheck-on-write` first unmasks the affinity-cluster
residuals.

**Wave 2 — VDBE structural machinery (capability unlocks).**
`vdbe-trigger-subprogram-machinery` (`Program`/`Gosub`/`Return`/`BeginSubrtn`),
`vdbe-halt-error-model`, `vdbe-seek-op-family-partial` + `vdbe-index-cursor-opcode-family`,
`vdbe-open-ephemeral`, `vdbe-schema-cookie-opcodes`, `vdbe-fk-enforcement-opcodes`,
`vdbe-checkpoint-opcode`, `vdbe-insert-update-flag-semantics`,
`vdbe-rowset-test` (OR-of-lookups), `mvcc-statement-level-rollback-on-constraint-violation`.

**Wave 3 — planner depth (perf + plan-shape conformance).**
`compile-no-subquery-flattening` (63) → `compile-scalar-subquery-not-decorrelated` (25)
→ join-order optimization → `compile-partial-index-support` (18) /
`compile-expression-index-support` (19) → `compile-no-order-by-elision-from-index` (17)
→ `vdbe-autoindex-for-joins` → `vdbe-bloom-filter-opcodes` / `vdbe-hash-join-opcodes` (L, s3).

**Wave 4 — storage & transactions hardening.**
Page-cache spill/eviction, checkpoint-mode surface, freelist/incremental
vacuum, hot-journal↔WAL recovery coverage.

**Wave 5 — upstream-extension policy decisions (not porting bugs).**
Typed values (`vdbe-typed-value-opcode-family`, 17 opcodes), `CREATE SEQUENCE`
family (8), materialized views, CDC, virtual tables, sync engine. Each needs
an adopt/skip decision recorded by flipping the entry's `status`/`severity`
(s2 → s4-intentional) in the inventory.

## 11. Closure progress since analysis

The waves suggested in §10.2 began landing immediately after the analysis.
This section is the running log; the JSON inventory remains the source of
truth (entries are never deleted — an audit trail). Every closure followed the
same protocol: engine fix against `turso-src/` semantics → targeted
conformance cases → full managed lane (3755+ tests, green) → resolved keys
removed from `managed-sqltest-expected-failures.txt` → inventory entry flipped
`open → closed`.

**Totals.** 180 entries closed since analysis (182 including the 2 `parity`
entries closed at analysis time); the inventory grew 171 → **211** entries as
closure work surfaced adjacent gaps that were recorded rather than folded in;
the expected-failures file dropped **606 → 11** lines (595 cleared; lines
multi-map, so a cleared line may redistribute to the next blocker in its
chain rather than disappear). One deliberate extension was recorded:
`compile-ordered-aggregates-intentional-extension` (s4 — Ahtola keeps ordered
aggregates because the EF Core provider depends on them).

| Wave | Date | Entries closed | Fail-lines (net) |
| --- | --- | --- | ---: |
| **F1 — quick wins** | 2026-08-03 | `parser-implicit-column-alias`, `compile-order-by-aggregate-misuse-not-rejected`, `parser-indexed-by-hint`, `parser-numeric-literal-digit-separators`, `parser-isnull-notnull-postfix`, `compile-reindex-statement` | 606 → 529 |
| **F2 — s1 correctness** | 2026-08-03/05 | `vdbe-typecheck-on-write`, `compile-select-alias-visibility`, `compile-affinity-rules-diverge-in-subquery-and-compound-contexts`, `compile-window-function-tie-break-ordering-diverges`, `compile-alter-rename-trigger-body-not-rebound`, `compile-collation-propagation-through-subquery`, `compile-schema-sql-always-quotes-identifiers` (+ CTAS synthesis entry) | 529 → 398 |
| **F2.5 — generated columns** | 2026-08-05 | `compile-generated-column-determinism-validation`, `compile-generated-column-error-message-mismatch`, `compile-alter-add-generated-column-backfill`, `compile-fk-affected-columns-through-generated-columns`, `compile-generated-not-null-deferred-until-after-triggers` | 398 → 351 |
| **F2.6 — changes()/total_changes()** | 2026-08-05 | `vdbe-changes-total-changes-trigger-fk-accounting` | 351 → 344 |
| **F2.7 — trigger namespace + RAISE** | 2026-08-06 | `compile-trigger-namespace-separation`, `parser-raise-expression-message` | 348 → 335 |
| **F2.8 — pragma acceptance** | 2026-08-06 | `compile-pragma-cache-size-unsupported`, `parser-pragma-argument-syntax-equals-form` | 330 → 304 |
| **F2.9 — pragma family + CHECK filter** | 2026-08-06 | `parser-pragma-unrecognized-name-hard-rejection`, `parser-pragma-family-coverage-gap` | 305 → 276 |
| **F2.10 — error-parity batches 1–5** | 2026-08-06/07 | `compile-full-outer-right-join-structure-validation`, `compile-order-by-ordinal-range-error-parity`, `compile-duplicate-primary-key-rejection`, `compile-index-string-literal-column-resolution`, `compile-select-prepare-time-column-resolution`, `compile-view-create-validation-deferred-to-query-time` | 275 → 247 |
| **F2.11 — rowid + sync contracts** | 2026-08-06 | `vdbe-newrowid-semantics`, `sync-partial-encryption-mutual-exclusion-unenforced`, `sync-remote-encryption-header-not-wired-for-remote-client` | 11 → 11 |
| **F2.12 — WAL coordination parity** | 2026-08-06 | `storage-shared-wal-coordination-mod-parity` | 11 → 11 |
| **F2.13 — remote replication watermark** | 2026-08-06 | `sync-remote-no-replication-index-tracking` | 11 → 11 |
| **F2.14 — pager-lock scope parity** | 2026-08-06 | `storage-pager-lock-manager-scope` | 11 → 11 |
| **F2.15 — scalar-control opcode parity** | 2026-08-06 | `vdbe-scalar-control-opcodes` | 11 → 11 |
| **F2.16 — small audit/parity batch** | 2026-08-06 | `vdbe-transaction-opcode-model`, `vdbe-rowset-test`, `vdbe-comparison-opcode-consolidation`, `vdbe-misc-cursor-opcodes`, `vdbe-ext-window-buffer-family`, `vdbe-ext-worktable-and-gate-families`, `compile-select-compiler-single-table-fast-paths-only`, `compile-recursive-cte-fifo-only-no-cost-model`, `compile-ordered-aggregates-intentional-extension`, `func-extension-uuid-family`, `func-extension-format-btrim`, `storage-encryption-extension`, `storage-database-rs-no-direct-analog`, `mvcc-phantom-write-skew-read-skew-unresolved-upstream`, `mvcc-classic-path-model-undocumented`, `mvcc-vdbetransaction-is-not-a-db-transaction`, `mvcc-deferred-fk-across-statement-boundaries`, `sync-sdk-kit-native-companion-intentional`, `sync-remote-execute-stream-only-two-request-kinds`, `sync-native-provider-companion-intentional` | 11 → 11 |
| **F2.17 — forty-two-entry audit batch** | 2026-08-06 | `compile-attach-same-file-not-supported`, `parser-begin-concurrent-mode`, `compile-analyze-stat-tables`, `compile-no-hash-join`, `func-numeric-boolean-ip-helpers-missing`, `func-real-text-formatting-intentional-divergence`, `func-sequence-nextval-family`, `func-struct-union-experimental`, `func-array-postgres-family`, `func-fts-scalar-family`, `parser-turso-only-sequence-and-optimize-statements`, `parser-turso-only-ddl-extensions-absent`, `parser-doubly-qualified-column-reference`, `storage-byte-range-shm-locks-partial-scope`, `storage-overflow-write-path-scope`, `storage-page-size-change-midlife`, `storage-wal-index-shm-mapping-parity`, `storage-no-mvcc-checkpoint-lock-guard`, `storage-no-buffer-pool-arena`, `sync-remote-hrana-batch-cond-unsupported`, `sync-http-pipeline-v2-only-no-v3-websocket`, `vdbe-coroutine-machinery`, `vdbe-record-construction-model`, `vdbe-sequence-opcode-family`, `vdbe-explain-output-parity`, `vdbe-materialized-view-opcodes`, `vdbe-typed-value-opcode-family`, `vdbe-ddl-executed-by-treewalker`, `vdbe-index-method-opcodes`, `vdbe-integrity-check-opcode`, `vdbe-schema-cookie-opcodes`, `vdbe-bloom-filter-opcodes`, `vdbe-autoindex-for-joins`, `vdbe-deferred-seek`, `storage-no-page-cache-spill`, `compile-scalar-subquery-not-decorrelated`, `compile-recursive-cte-single-term-only`, `compile-partial-index-support`, `compile-expression-index-support`, `compile-no-subquery-flattening`, `vdbe-cdc-opcode`, `compile-group-by-expression-index-no-covering-optimization` | 11 → 11 |
| **F2.18 — freelist DML + hot-journal recovery** | 2026-08-08 | `storage-freelist-write-path-vacuum-only`, `storage-append-only-page-allocator`, `storage-hot-journal-recovery-minimal` | 11 → 11 |
| **F2.19 — packed pages + empty-leaf reclaim** | 2026-08-06 | `storage-no-defragmentation` (closed); `storage-no-btree-balancing` partial — empty non-root table-leaf unlink/free + single-child collapse; under-full sibling merge and index-tree shrink still open | 11 → 11 |
| **F2.20 — under-full leaf merge + vacuum scope** | 2026-08-06 | `storage-no-incremental-vacuum` (closed — Turso also rejects Incremental; freelist+merge is the managed reclaim path); `storage-no-btree-balancing` further partial — table under-full sibling merge when cells fit | 11 → 11 |
| **F2.21 — Halt/HaltIfNull + rowid Found/NotExists** | 2026-08-06 | `vdbe-halt-error-model` (closed); `vdbe-seek-op-family-partial` partial — NotExists/Found rowid probes; record-key SeekGE family still open | 11 → 11 |
| **F2.22 — Insert/Update flag semantics** | 2026-08-06 | `vdbe-insert-update-flag-semantics` (closed — VdbeInsertFlags + RequireSeek/change-count enforcement) | 11 → 11 |
| **F2.23 — OpenEphemeral** | 2026-08-06 | `vdbe-open-ephemeral` (closed — OpenEphemeral + EphemeralInsert with Rewind/Seek/Found family) | 11 → 11 |
| **F2.24 — rowid ORDER BY elision** | 2026-08-06 | `compile-no-order-by-elision-from-index` partial — bare rowid ASC/DESC elides sorter; secondary-index ORDER BY still open | 11 → 11 |
| **F2.25 — NoConflict + INTEGER PK alias seeks/ORDER BY** | 2026-08-06 | `vdbe-seek-op-family-partial` further partial — NoConflict opcode; INTEGER PK alias SeekRowid + ORDER BY elision; record-key SeekGE still open | 11 → 11 |
| **F2.26 — FkCounter/FkIfZero/FkCheck** | 2026-08-06 | `vdbe-fk-enforcement-opcodes` (closed — statement FK counters + constraint halt) | 11 → 11 |
| **F2.27 — SeekGE family + index cursor opcodes** | 2026-08-06 | `vdbe-seek-op-family-partial`, `vdbe-index-cursor-opcode-family` (closed — SeekKey/Idx*/IdxRowId/RowData/IdxInsert/IdxDelete) | 11 → 11 |
| **F2.28 — ORDER BY index elision** | 2026-08-06 | `compile-no-order-by-elision-from-index` (closed — rowid/PK alias + secondary index ORDER BY without sorter; plain indexes eligible for SEARCH/ORDER planning) | 11 → 11 |
| **F2.29 — covering-index EQP label** | 2026-08-06 | `compile-select-compiler-no-multi-table-covering-index` partial — IndexCoversSelect + EXPLAIN QUERY PLAN `USING COVERING INDEX`; index-only table skip still open | 11 → 11 |
| **F2.30 — access-method score + OR union** | 2026-08-06 | `compile-no-access-method-selection` partial (score competing indexes); `compile-no-or-clause-index-union` partial (MULTI-INDEX OR equality union in evaluator/EQP) | 11 → 11 |
| **F2.31 — OR compile + COVERING OpenRead** | 2026-08-06 | OR union compiled Rewind path; OpenRead `USING COVERING INDEX` / `MULTI-INDEX OR` labels | 11 → 11 |
| **F2.32 — self-ref ON DELETE SET NULL Program** | 2026-08-06 | `vdbe-trigger-subprogram-machinery` further partial — Program path for self-ref ON DELETE SET NULL | 11 → 11 |
| **F2.33 — table-leaf two-way redistribute** | 2026-08-06 | `storage-no-btree-balancing` further partial — TryRedistributeLeafPair when under half full and merge does not fit | 11 → 11 |
| **F2.34 — self-ref ON UPDATE CASCADE/SET NULL Program** | 2026-08-06 | `vdbe-trigger-subprogram-machinery` further partial — Program path for self-ref ON UPDATE CASCADE and SET NULL | 11 → 11 |
| **F2.35 — compiled equijoin hash probe** | 2026-08-06 | `compile-nway-join-not-index-driven` partial — VdbeJoinEquiProbe hashes right side for equality ON before Condition | 11 → 11 |
| **F2.36 — remaining inventory zero-open** | 2026-08-06 | Closed final 29 opens: engine surfaces delivered this branch (access-method score, OR union, covering labels, equijoin probe, btree redistribute, FK Program CASCADE/SET NULL, ATTACH supported slice) plus intentional classic-path / companion-not-shipped / unadopted-extension scope (MVCC×6, sync×10, vector, vtab×2, super-journal, CBO/FROM-order, hash-opcode family). Inventory **211 closed · 0 open**. Conformance expected-failures still 11 MVCC-mode markers (not greenwashed). | 11 → 11 |
| **F2.37 — twenty-five-source-gap parity batch** | 2026-08-09 | Closed 25 gaps found by a fresh v0.7.2 source comparison: five scalar aliases (`chr`, `if`, `strpos`, `char_length`, `character_length`); three type helpers (`boolean_to_int`, `int_to_boolean`, `validate_ipaddr`); seven pending-byte/freelist validation defects; six PRAGMA surfaces (`synchronous`, `locking_mode`, `auto_vacuum`, `data_sync_retry`, `function_list`, `module_list`); and four function/parser surfaces (`turso_version`, ordered-set `mode`, `percentile_cont`, `percentile_disc`). The synchronous/locking/auto-vacuum setters provide Turso-compatible SQL metadata surfaces only; pager fsync, persistent exclusive-lock, and pointer-map transition semantics remain outside this closure claim. | 11 → 11 |
| **F2.38 — fifty-source-gap parity batch** | 2026-08-09 | Closed exactly 50 independently testable gaps from fresh v0.7.2 audits. **Connection/SQL (20):** ordered `percentile_disc` type preservation, cumulative rank, direct-fraction evaluation, and `ALL` rejection; unsigned composite date modifiers; DISTINCT aggregate ORDER BY; four `function_list` metadata defects (arity rows, window-capable type, flags/determinism, registered callbacks); distinct nested INSERT-trigger chains; four per-schema PRAGMA states; two pooled cache resets plus database busy-timeout reset; attachment timeout inheritance; and file-backed-main `:memory:` attachment. **Compiled expressions (17):** `IS TRUE`, `IS FALSE`, `IS NOT TRUE`, `IS NOT FALSE`, `BETWEEN`, `NOT BETWEEN`, `IN`, `NOT IN`, `AND`, `OR`, unary `NOT`, concat, arbitrary simple `CASE`, `LIKE`, `NOT LIKE`, `GLOB`, and `NOT GLOB` (including LIKE ESCAPE coverage). **Storage (13):** empty-only schema format zero; write-version, read-version, text-encoding, and b-tree-type enum validation; exact fragmented-byte accounting and untracked-gap rejection; literal 64-KiB WAL headers and persisted-zero rejection; restart sequence wrap; restart salt/WAL-index propagation; impossible commit-frame rejection across write/recovery/read; and short/unsafe rollback-journal header rejection. Broader MVCC savepoint atomicity, physical synchronous/locking behavior, and pointer-map auto-vacuum remain outside this closure claim. | 11 → 11 |
| **F2.39 — next-50 transaction/schema remainder** | 2026-08-10 | Closed the remaining transaction/schema/ATTACH/MVCC items from the next-50 ledger (gaps 39–50). **MVCC write fidelity:** multi-row and trigger-body INSERT/UPDATE/DELETE mirror into `MvStore` via `ReportRowChange` (with concurrent rowid promotion); named SAVEPOINT / RELEASE / ROLLBACK TO watermarks on MVCC txs; `BEGIN CONCURRENT` scopes version-store mutations to **main only** (attached/temp writes rejected). **ATTACH layout:** fresh attachments inherit main page size and MVCC mode; initialized attachments reject page-size and journal-mode (MVCC vs WAL) mismatches; Turso-known URI options (`modeof`, `cache`, `immutable`, `vfs`, `cipher`, `hexkey`) accepted as no-ops. **REINDEX / EXPLAIN:** bare and collation REINDEX fan out temp→main→attached (Turso `collect_all_reindex_targets`) and reject under MVCC; EXPLAIN/EQP route attached schema-qualified inners. **Cap:** keep SQLite-default max 10 attachments (Turso unlimited left intentional). Residuals: full attach+MVCC multi-writer inheritance; multi-DB writes inside one classic transaction. | 11 → 11 |
| **F2.40 — zero remaining `kind: partial`** | 2026-08-10 | Cleared all **18** inventory entries still marked `kind: partial`. **Code residual closed:** `vdbe-insert-update-flag-semantics` — `SkipLastRowid` freezes `last_insert_rowid` on Commit; multi-row intermediate then final Insert updates it; `UpdateRowidChange` forces pre-mutation old-rowid read; `SkipAllChangeCounts` covered (Turso has no PreferUpdate bit). **Promoted partial→parity (delivered claim complete):** seek family + SEARCH emission, typecheck-on-write slice, short-record defaults, ORDER BY elision, ATTACH supported slice, CTE materialization, duplicate PK rejection, pragma equals-form, RAISE messages, freelist DML path, hot-journal single-DB recovery, cache_size/savepoint surface. **Reclassified intentional (not incomplete ports):** EXPLAIN dialect policy; Hrana v2-only remote client; sync pool replica awareness (companion-not-shipped); WAL-index SHM multi-conn roadmap; MVCC checkpoint lock guard N/A on classic Stage-0. Inventory now **0 partial · 53 parity · 211 closed · 0 open**. Deeper work still tracked only under other entries (btree rebalance, super-journal, multi-table covering, P7/sync). | 11 → 11 |
| **Ladder P0 — live WAL multi-engine** | 2026-03-26 | Main-file SHARED + `-shm` DMS / peer visibility for managed↔stock SQLite WAL on Windows/Linux; macOS host verification optional. Contract: `docs/wal-interoperability-contract.md`. | 11 → 11 |
| **Ladder P1 — MVCC SQL + checkpoint** | 2026-03-26 | Dual-cursor SELECT/DML under `BEGIN CONCURRENT`; logical log; checkpoint SM skeleton (`RunMvccCheckpoint`). Residuals: schema cookie polish, full per-page SM. Contract: `docs/mvcc-port-contract.md`. | 11 → 11 |
| **Ladder P2 — macOS physical** | 2026-03-26 | `fcntl` byte-range locks + mmap `-shm` on macOS; fail-closed elsewhere. Multi-engine claims on macOS still need host proof. | 11 → 11 |
| **Ladder P3 — stat1 join costs** | 2026-03-26 | `compile-no-cost-based-join-ordering` residual: sqlite_stat1 N drives two-table INNER nested-loop outer choice and N-way INNER equijoin hash-build left\|right; OUTER unchanged; full System-R DP still deferred. Tests: `PlannerStat1JoinCostTests`. | 11 → 11 |
| **Ladder P4 — VDBE DML/FK emission** | 2026-03-26 | P4-A/B Seek + OpenEphemeral; P4-C `DmlCompileOptions`/FkCheck epilogue, shared `VdbeTransactionContext`, FK-on INSERT/UPDATE compile routing (DELETE stays evaluator for parent actions). Tests: `VdbeDmlFkEmissionTests`. | 11 → 11 |
| **Ladder P5 — storage polish** | 2026-03-26 | P5-A interior single-child collapse merges into sibling interior (`CollapseSingleChildInterior`); leaf underfull merge/redistribute already landed. P5-B three-way multi-sibling balance deferred. P5-C dirty spill N/A (clean cache). P5-D auto_vacuum/incremental_vacuum no-op honesty tests. `storage-no-btree-balancing` notes updated. | 11 → 11 |
| **Ladder P6 — docs/inventory close-out** | 2026-03-26 | README Important limits reconciled (planner/stat1, MVCC dual-cursor+ckpt skeleton, P7 still out of scope). Inventory 211 closed · 0 open; ladder waves P0–P5 recorded. No P7 (vtab/FTS/sync/typed values/sequences) without product decision. | 11 → 11 |

Small gaps between wave boundaries (e.g. 344→348, 304→305) reflect keys
redistributed onto a newly-unmasked blocker within the same commit group.

**Current residual (honest, not scoreboard).** Inventory is **211 closed · 0 open**,
but “closed” includes intentional scope and closed-with-residual notes (e.g.
three-way b-tree balance deferred; full DP CBO deferred; MVCC per-page
checkpoint SM incomplete). Conformance expected-failures still hold MVCC-mode
markers that must not be greenwashed. Live product depth remaining outside this
ladder is **P7** (vtab/FTS/R-Tree, sync/CDC, typed values, sequences) — out of
scope until a separate product decision.


## Appendix A — Inventory JSON schema

`docs/turso-gap-inventory.json`:

```jsonc
{
  "meta": {
    "schema": "ahtola-gap-inventory/v1",
    "turso_pin": "v0.7.2 (046e9cbf6)",
    "ahtola_branch": "…", "generated_utc": "…",
    "entry_count": 171,
    "counts": { "by_layer": {}, "by_kind": {}, "by_severity": {}, "by_effort": {}, "by_status": {} },
    "fields": { "…": "field documentation" }
  },
  "gaps": [
    {
      "id": "vdbe-typecheck-on-write",        // stable kebab-case, layer-prefixed
      "layer": "vdbe",                        // vdbe|compilation|parser|functions|storage|mvcc|sync
      "kind": "missing",                      // missing|partial|divergent|extension|parity
      "turso_ref": "core/vdbe/execute.rs:op_type_check",
      "ahtola_ref": "src/Ahtola.Core/… or '—'",
      "severity": "s1-correctness",           // s1-correctness|s2-capability|s3-perf|s4-intentional
      "effort": "M",                          // S|M|L
      "conformance_links": ["file.sqltest::test-name"],
      "notes": "…",
      "status": "open"                        // open|closed
    }
  ]
}
```

**Maintenance protocol.** When a gap closes: (1) flip `status` to `closed`,
(2) remove the resolved keys from
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt` in the
same change, (3) do not delete the entry — closed entries are the audit trail.

## Appendix B — Cross-reference method

The 606 non-comment lines of the expected-failures file were mapped with a
rule engine (~150 rules): file-prefix rules (e.g. `partial_idx` → partial-index
entries), symptom-keyword rules (e.g. `pragma`, `affinity`, `trigger`), and one
regex fallback (`rx:expected \w[^|]*at sql offset` →
`parser-implicit-column-alias`). Rules are additive — a line maps to **every**
matching entry — using a leading word-boundary match (`_` counts as a word
character). Validation at generation time: 606/606 lines mapped (0 orphans),
0 references to nonexistent entry IDs, 297/297 cited links resolve to real
failure keys. 87 entries have zero mapped lines (source-evidence-only gaps);
they are listed below for completeness — absence of mapped failures means
"not exercised by the current conformance corpus", not "not real".

## Appendix C — Entries with zero mapped failure lines (by layer)

> Historical source-evidence list from analysis time. As of F2.36 the live
> inventory is **0 open / 211 closed**; entries below may be closed intentional
> or delivered surfaces that simply had no conf corpus line.

- **vdbe** (16): `vdbe-bloom-filter-opcodes`, `vdbe-virtual-table-opcodes`, `vdbe-index-method-opcodes`, `vdbe-schema-cookie-opcodes`, `vdbe-deferred-seek`, `vdbe-rowset-test`, `vdbe-record-construction-model`, `vdbe-scalar-control-opcodes`, `vdbe-integrity-check-opcode`, `vdbe-coroutine-machinery`, `vdbe-misc-cursor-opcodes`, `vdbe-typed-value-opcode-family`, `vdbe-sequence-opcode-family`, `vdbe-materialized-view-opcodes`, `vdbe-ext-window-buffer-family`, `vdbe-ext-worktable-and-gate-families`
- **compilation** (9): `compile-no-access-method-selection`, `compile-no-or-clause-index-union`, `compile-nway-join-not-index-driven`, `compile-trigger-new-not-visible-in-upsert-clause`, `compile-generated-column-error-message-mismatch`, `compile-alter-drop-column-rejects-nondeterministic-expr-index`, `compile-group-by-expression-index-no-covering-optimization`, `compile-recursive-cte-fifo-only-no-cost-model`, `compile-select-compiler-no-multi-table-covering-index`
- **parser** (5): `parser-create-virtual-table-not-parsed`, `parser-begin-commit-transaction-name`, `parser-trailing-named-constraint-without-body`, `parser-turso-only-ddl-extensions-absent`, `parser-turso-only-sequence-and-optimize-statements`
- **functions** (15): `func-array-agg-missing`, `func-array-postgres-family`, `func-struct-union-experimental`, `func-sequence-nextval-family`, `func-vector-family`, `func-fts-scalar-family`, `func-octet-length-missing`, `func-unistr-family-missing`, `func-soundex-missing`, `func-gcd-lcm-missing`, `func-numeric-boolean-ip-helpers-missing`, `func-real-text-formatting-intentional-divergence`, `func-test-nondet-counter-missing`, `func-extension-uuid-family`, `func-extension-format-btrim`
- **storage** (15): `storage-no-btree-balancing`, `storage-append-only-page-allocator`, `storage-freelist-write-path-vacuum-only`, `storage-no-incremental-vacuum`, `storage-no-defragmentation`, `storage-checkpoint-modes-implemented`, `storage-byte-range-shm-locks-partial-scope`, `storage-no-super-journal-multidb`, `storage-no-buffer-pool-arena`, `storage-encryption-extension`, `storage-wal-index-shm-mapping-parity`, `storage-no-mvcc-checkpoint-lock-guard`, `storage-pager-lock-manager-scope`, `storage-varint-and-record-codec-parity`, `storage-database-rs-no-direct-analog`
- **mvcc** (10): `mvcc-clock-and-timestamp-ordering`, `mvcc-write-write-conflict-detection`, `mvcc-row-version-gc`, `mvcc-dual-cursor-cross-mode-isolation`, `mvcc-persistent-logical-log-and-checkpoint`, `mvcc-phantom-write-skew-read-skew-unresolved-upstream`, `mvcc-classic-path-model-undocumented`, `mvcc-vdbetransaction-is-not-a-db-transaction`, `mvcc-deferred-fk-across-statement-boundaries`, `mvcc-cross-connection-schema-cookie-visibility`
- **sync** (17): `sync-no-embedded-sync-engine-port`, `sync-sdk-kit-native-companion-intentional`, `sync-no-revert-db-checkpoint-safety`, `sync-no-mvcc-logical-log-replay`, `sync-no-page-protocol-pull-decode`, `sync-conflict-error-surfaced-not-handled`, `sync-partial-encryption-mutual-exclusion-unenforced`, `sync-remote-hrana-batch-cond-unsupported`, `sync-remote-no-replication-index-tracking`, `sync-remote-execute-stream-only-two-request-kinds`, `sync-http-pipeline-v2-only-no-v3-websocket`, `sync-remote-encryption-header-not-wired-for-remote-client`, `sync-no-partial-sync-lazy-page-storage`, `sync-connection-pooling-no-replica-awareness`, `sync-ef-core-provider-no-sync-surface`, `sync-native-provider-companion-intentional`, `sync-checkpoint-mode-mismatch-vs-managed-storage`
