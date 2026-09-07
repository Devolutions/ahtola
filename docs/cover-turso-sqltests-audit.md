# COVER workstream: turso-sqltests coverage audit and expansion

Prepared 2026-09-06 against Ahtola engine baseline `83bb892` (parent plan/reconciliation
`032c6ec`/`9ce49a2`/`9bdb053`) and the read-only Turso `v0.8.0-pre.7` pin, `277ddd050`.
Scope: workstream **COVER** from `docs/turso-remaining-gap-plan.md` (Wave 2). This document
is COVER-owned; it does not edit or supersede the parent's gap plan or historical inventory.

## Corrected baseline counts

The parent plan's COVER section states "319 pinned SQLite-suite files already present" and
"61 files" in the pinned `turso-src/sqlite/conformance/turso-sqltests/` corpus, with 56
remaining after the 5 already-adopted files (`regexp`, `sequence`, `time`, `vector`,
`without_rowid`). Direct measurement of the checked-out submodule at this pin shows:

- `turso-src/sqlite/conformance/turso-sqltests/`: **67** `.sqltest` files (not 61).
- Managed corpus (`conformance/sqlite-sqltests/`) before this workstream: **326** files
  (319 sqlite-suite + 2 managed additions + 5 turso subset), matching the plan.
- Remaining turso-sqltests files to classify: **67 − 5 = 62** (not 56).

The discrepancy is reported as-is rather than silently reconciled to the plan's number; the
classification below covers the full, measured set of 62.

## Disposition summary (62 files)

| Disposition | Count | Notes |
| --- | ---: | --- |
| Adopted (byte-for-byte, this workstream) | 31 | See "Adopted" below |
| Product-excluded: TYPE/DOMAIN custom-type system | 23 | Separate product-adoption project per parent plan |
| Product-excluded: incremental materialized views | 5 | Separate product-adoption project per parent plan |
| Implementation-assigned: ICU locale collations | 1 | `turso/collate.sqltest` (7 cases) — assigned to a dedicated worker (session `e9784de6-cc3f-4738-a631-8176c8b78abe`), not silently excluded |
| Implementation-assigned to another worker (NULLIDX) | 1 | `create-index-nulls-first-last.sqltest` |
| Implementation-assigned to Wave 2 (EQP) | 1 | `explain-query-plan-json.sqltest` |
| **Total** | **62** | |

### Adopted (31 files, vendored unmodified with SHA-256 provenance recorded in commit)

All 31 files live under `conformance/sqlite-sqltests/turso/`, preserving Turso's own relative
layout (including `turso/attach/`), alongside the existing `turso/{regexp,sequence,time,
vector,without_rowid}.sqltest` — turso-sqltests content never mixes into the canonical
sqlite-sqltests root, so a colliding filename (see `turso/collate.sqltest` above) can never
silently overwrite an existing sqlite-suite file.

General SQL correctness, no Turso-specific gating:
`analyze.sqltest`, `gcd_lcm_repeat_pad.sqltest`, `gencol-index-order.sqltest`,
`get-set-byte.sqltest`, `group-by-having-infinite-loop.sqltest`, `insert-default.sqltest`,
`invalid-argument-error-message.sqltest`, `is-operator-index-seek.sqltest`,
`join-limit-offset.sqltest`, `json_object_star.sqltest` (one sub-case gated by
`@requires materialized_views`, auto-skipped), `multi-column-subquery-comparison.sqltest`,
`mvcc_checkpoint_replace_delete.sqltest`, `mvcc_feature_compat.sqltest`,
`mvcc_left_join_null_row.sqltest`, `mvcc_pragma_table_valued_join.sqltest`,
`on_conflict_constraint.sqltest`, `raise.sqltest`, `row-value-adversarial.sqltest`,
`within-group.sqltest` (WITHIN GROUP ordered-set aggregates: MODE/PERCENTILE_CONT/
PERCENTILE_DISC are natively implemented in `SqlParser.cs`, not a parse-then-reject stub).

`attach/` (under `turso/attach/`; ATTACH DATABASE is implemented; `default.sqltest`/
`memory.sqltest`/`sequence.sqltest`/`writes.sqltest`/`cross_db_views_rejection.sqltest` are
ordinary `:memory:`/`:default:` fixtures; `small.sqltest` reuses the
`database/testing_small.db` static fixture already vendored for `where/small.sqltest`, see
below).

`turso/` (Turso-specific extended SQL surface Ahtola implements, grouped with the existing
`turso/{regexp,sequence,time,vector,without_rowid}.sqltest`): `alter_column.sqltest`
(`ALTER TABLE t ALTER COLUMN a TO b <type>` — implemented as `AlterTableAlterColumnStatement`),
`fts.sqltest` (Turso's `CREATE INDEX … USING fts` index-method full-text search, see
`docs/managed-index-methods.md`), `legacy-unquoted-view-columns.sqltest` (historical
unquoted-view-column compat quirk; static fixture below), `mvcc_sequence.sqltest`,
`mvcc_sequence_adversarial.sqltest`, `sequence_adversarial.sqltest` (companions to the
already-adopted `turso/sequence.sqltest`).

### Product-excluded: TYPE/DOMAIN (23 files)

All gated by `@requires-file custom_types` (file-level) and/or `@requires strict`/
`@requires custom_types` (per-test), which the managed capability set
(`SqltestCorpus.ManagedCapabilities = {trigger, strict}`) does not include, so these are
already `SkippedByCorpus` if vendored — not silently hidden, properly gated the same way
other unsupported capabilities already are. Not vendored, per the parent plan's explicit
exclusion of "Typed values, TYPE/DOMAIN … remain separate product-adoption projects":
`alter-table-domain-check.sqltest`, `custom_type_alter_default.sqltest`,
`custom_type_datetime_validation.sqltest`, `custom_type_default_values.sqltest`,
`custom_type_deterministic.sqltest`, `custom_type_expr_index.sqltest`,
`custom_type_notnull_decode.sqltest`, `custom_type_operators.sqltest`,
`custom_type_ordering.sqltest`, `custom_type_upsert_trigger.sqltest`,
`custom_types_fk_cascade.sqltest`, `custom_types_non_strict.sqltest`, `custom_types.sqltest`,
`custom-type-null-default.sqltest`, `domain.sqltest`,
`strict_custom_type_input_validation.sqltest`, `strict_check_types.sqltest`,
`partial-index-union-tag.sqltest`, `array.sqltest`, `array-bugs.sqltest`,
`array-edge-cases.sqltest` (all three `@requires-file custom_types "array tests require
custom types support"` — Postgres-style typed arrays are layered on the custom-type system,
not an independent feature), `builtin_pg_types.sqltest` (`@requires-file custom_types`),
`struct_union.sqltest` (`CREATE TYPE point AS STRUCT(...)`).

### Product-excluded: incremental materialized views (5 files)

`CREATE MATERIALIZED VIEW` is not implemented anywhere in `Ahtola.Core` (confirmed: no
`MaterializedView`/`CREATE MATERIALIZED VIEW` symbol). The one materialized-view file already
in the managed corpus, `matview-create-index.sqltest`, is gated by `@requires
materialized_views` and is currently `SkippedByCorpus`, not passing — consistent with this
being unimplemented rather than partially working. Not vendored, per the parent plan's
explicit exclusion of "incremental materialized views": `materialized_view_text_arithmetic.sqltest`,
`materialized_views.sqltest`, `matview_create_same_txn.sqltest`,
`matview_insert_or_replace.sqltest`, `matview_integrity_check.sqltest`.

### Implementation-assigned: ICU locale collations (1 file)

`turso/collate.sqltest` in `turso-sqltests` tests ICU locale-tag collations (`COLLATE
'fr-FR'`, `'es-u-co-trad'`, `'en-u-kf-upper'`, `'en-u-kn-true-kf-upper-ks-level2'`, 7 cases).
This is a **different feature** from the file of the same name already vendored at
`conformance/sqlite-sqltests/collate.sqltest` (1458 lines, ordinary `BINARY`/`NOCASE`/`RTRIM`
collation coverage from the sqlite-suite) — copying it over would have both silently
destroyed existing coverage and mis-adopted an unrelated feature under a colliding filename;
placing the new file under `turso/` instead of the corpus root avoids the collision entirely.
Confirmed no ICU integration exists in `Ahtola.Core` (grep for `icu`/`ICU`: no match). Assigned
to a dedicated worker (Claude Sonnet 5, high/long-context, session
`e9784de6-cc3f-4738-a631-8176c8b78abe`), which owns byte-faithful vendoring of the 7 cases plus
the general managed/AOT-safe collation implementation/portability decision — not silently
excluded, and not COVER's to implement.

### Implementation-assigned to other workers (not adopted here)

- `create-index-nulls-first-last.sqltest` (83 cases behind it, per parent's index-NULL
  ordering worker, session `757f46a2`) — explicitly assigned; left unvendored by COVER.
- `explain-query-plan-json.sqltest` — the pinned corpus's 15 `FORMAT=JSON` EQP cases the
  parent plan assigns to Wave 2's **EQP** workstream (queued behind **PLAN**); left
  unvendored by COVER.

## Static path-fixture vendoring (3 files, closes 2 of the 14 exclusions)

`turso-src/sqlite/conformance/database/` contains 3 byte-identical, git-tracked (not
`.gitignore`d — only `*.db-wal`/`*.db-shm` are) static database fixtures also used by our
own already-vendored sqltest files:

| Fixture | Referenced by | SHA-256 verified |
| --- | --- | --- |
| `testing_small.db` | `where/small.sqltest` (already vendored, previously harness-excluded), `attach/small.sqltest` (newly adopted) | ✅ matches `turso-src` |
| `testing_user_version_10.db` | `pragma/user_version_10.sqltest` (already vendored, previously harness-excluded) | ✅ matches `turso-src` |
| `testing_legacy_unquoted_view_columns.db` | `turso/legacy-unquoted-view-columns.sqltest` (newly adopted) | ✅ matches `turso-src` |

Vendored to `conformance/sqlite-sqltests/database/*.db` and wired through
`SqltestPhysicalFixtures`/`SqltestManagedRunner.OpenDatabase` for `SqltestDatabaseKind.Path`,
closing 2 of the 14 previously-excluded path-fixture files without needing corruption logic.
Each static image is copied privately and opened through the storage pager to establish its
WAL carrier without parsing SQL schema. This lets the legacy malformed-view image reach the
actual engine-open comparison. Lazy per-key cache entries prevent concurrent duplicate
fixture generation.

## Integrity-check corruption fixtures (12 files, the remaining 12 of the 14 exclusions)

The pinned corpus's `integrity_check/parity_*.sqltest` files reference `@database
database/integrity_*.db readonly` paths that are **not** static files — Turso's own sqltest
tooling generates them at test-run time (`turso-src/testing/sqltest/src/generator/mod.rs`:
`generate_integrity_fixture` and its `generate_*_fixture`/`patch_*` helpers). Each fixture is
built by running ordinary setup SQL through a real engine, checkpointing, locating a specific
b-tree page/cell by root-page number, and flipping a small number of bytes to introduce one
specific, well-understood defect (dropped index cell, patched NOT NULL/CHECK column value,
duplicated/retargeted index key, inflated freelist counts, truncated overflow chain).

`SqltestIntegrityFixtureGenerator.cs`/`SqliteFixtureBytePatcher.cs` port this generator
1:1 into the test-only managed harness: setup SQL runs through `EmbeddedDatabase.OpenFile`
(the real production write path, not a mock), and the byte patches mirror
`parse_sqlite_varint`/`sqlite_serial_type_payload_len`/`patch_*` exactly, so `PRAGMA
integrity_check`/`quick_check` exercises Ahtola's actual page/record-format parsing against
genuinely corrupted bytes. See the class doc comments for the upstream Rust function each
routine mirrors.

| Fixture file | Defect | Rust source function |
| --- | --- | --- |
| `parity_corrupt_index.sqltest` | dropped last cell in a plain index | `generate_missing_index_entry_fixture` |
| `parity_corrupt_expression_index.sqltest` | dropped last cell in an expression index | `generate_missing_index_entry_fixture` |
| `parity_corrupt_partial_index.sqltest` | dropped last cell in a partial index | `generate_missing_index_entry_fixture` |
| `parity_check_constraint.sqltest` | CHECK constraint violated post-write | `generate_check_constraint_violation_fixture` |
| `parity_quick_check_constraint.sqltest` | same, separate file for `quick_check` | `generate_check_constraint_violation_fixture` |
| `parity_not_null_violation.sqltest` | NOT NULL column patched to NULL | `generate_not_null_violation_fixture` |
| `parity_non_unique_index.sqltest` | UNIQUE index key collapsed onto another | `generate_non_unique_index_entry_fixture` |
| `parity_missing_unique_index.sqltest` | UNIQUE index key retargeted to an unused value | `generate_missing_unique_index_entry_fixture` |
| `parity_freelist_count_mismatch.sqltest` | header freelist-page count off by one | `generate_freelist_count_mismatch_fixture` |
| `parity_freelist_trunk_corrupt.sqltest` | freelist trunk leaf-pointer count inflated | `generate_freelist_trunk_corrupt_fixture` |
| `parity_overflow_list_length_mismatch.sqltest` | overflow chain truncated | `generate_overflow_list_length_mismatch_fixture` |
| `parity_gencol_not_null_violation.sqltest` | virtual generated NOT NULL column's base patched to NULL | `generate_gencol_not_null_violation_fixture` |

All 12 fixtures execute as comparable outcomes; none is harness-excluded.
`SqltestManagedRunner.RunVariant` materializes explicit physical fixtures before its error
boundary, then classifies `EmbeddedSqlException`/`InvalidDataException` from the final open.
Fixture construction, default generation, unsupported paths, and programmer errors remain
hard failures. The outcome retains both outer context and the underlying validation message.
Dedicated tests first require successful fixture construction, then assert the expected
engine rejection and its specific diagnostic.

3 of the 12 (`parity_check_constraint`, `parity_quick_check_constraint`,
`parity_not_null_violation`) open successfully — ordinary column CHECK/NOT NULL values are not
re-validated on load, only generated-column values are recomputed (and rechecked) — and their
`PRAGMA integrity_check`/`quick_check` output matches the pinned corpus exactly, closing 3 of
the 14 original path-fixture exclusions.

The other 9 (18 cases: `parity_corrupt_index`, `parity_corrupt_expression_index`,
`parity_corrupt_partial_index`, `parity_non_unique_index`, `parity_missing_unique_index`,
`parity_freelist_count_mismatch`, `parity_freelist_trunk_corrupt`,
`parity_overflow_list_length_mismatch`, `parity_gencol_not_null_violation`) apply the intended
corruption correctly — confirmed because the resulting "database failed to open" message names
the *exact* defect each patch introduced (e.g. "SQLite freelist header declares 202 pages but
its trunks contain 201" for the +1 header patch, "duplicate non-NULL keys" for the
key-collision patch) — but `EmbeddedFileStore.Load()` validates b-tree/index/allocation-map/
generated-column structural integrity **eagerly at file-open time**, where Turso/SQLite open
the file unconditionally and defer to `PRAGMA integrity_check`/`quick_check` to report the same
defect as row output. This is the same class of pre-existing divergence already exercised by
`ManagedIntegrityCheckPragmaTests.AStoredNonUniqueIndexEntryFailsToOpenInsteadOfBeingReported`.
These 18 cases are recorded as ordinary entries in `managed-sqltest-expected-failures.txt` with
the exact "database failed to open: …" message, assigned to the coordinator — they are real,
executed, comparable outcomes, not a harness exclusion, and `managed-sqltest-harness-exclusions.txt`
remains empty (`HarnessExclusionsAreReviewableAndReferToUnsupportedCases` still asserts that).

All 14 original path-fixture files are now runnable. Five match the upstream SQL result
contract; the other nine produce explicit open-time differences rather than being skipped.

The corrected parent integration run selected `Name~turso/`,
`Name~integrity_check/parity_`, the two existing static-fixture files, physical-fixture
regressions, and inventory/discovery/no-exclusions guards. With an execution floor of 69,
it executed and passed 69 NUnit tests, with zero failures and one corpus-policy skip
(`turso/fts.sqltest`, `@backend cli`). Skipped files were not counted toward that floor.
This validates the harness and recorded difference accounting, not implementation of all
known engine gaps. Discovery reports 11,651 cases, 11,511 runnable, 140 corpus-policy skips,
and zero unsupported-harness cases at this integration checkpoint.

## `@backend rust` routing (12 cases across 4 files)

See `managed-sqltest-expected-failures.txt`'s COVER provenance note and commit
`7e33e6cb1bc06998ed332e170b3c79e9d7c05a2a` for the full detail: all 12 `@backend rust`
declarations in the pinned corpus were reviewed individually and found to be ordinary SQL/
PRAGMA cases (none needed a harness exclusion). Ten now match after the parent corrected the
two `VACUUM INTO` diagnostics. The `pragma_journal_mode()` function is implemented with
per-connection execution context; the two journal-mode cases retain a documented
storage-mode difference
(`journal_mode='memory'` for `:memory:` is SQLite-correct; Turso's Rust engine always reports
`wal`/`mvcc`). `@backend cli` (sqlite3 CLI dot-commands: `btree_dump`, `cmdlineshell*`,
`select/memory.sqltest`'s dot-command cases, etc.) remains skipped — genuinely CLI-process-
only, no managed-engine equivalent; this is corpus policy, not a COVER-introduced exclusion.

## Newly exposed differences from 31 additional files (116 cases)

All recorded as precise per-case entries in `managed-sqltest-expected-failures.txt` (COVER
2026-09-06 block). Every entry below is classified by root cause and by whether the divergence
is a **pure rendering/message-text difference** (the engine's actual behavior — acceptance,
rejection, or computed value — matches Turso; only the round-tripped schema text or error
wording differs) or a **wrong value / rejected valid SQL** (a real behavioral difference:
missing feature, incorrect computed value, missing or over-broad validation). Case IDs are
listed in full in the coordinator report; this section summarizes by group.

### `turso/alter_column.sqltest` (32 cases)

| Root cause | Classification | Cases |
| --- | --- | --- |
| `ALTER COLUMN … TO … AS (…)` (generated-column conversion) not implemented | Missing feature | 12 |
| `ALTER COLUMN` virtual→regular conversion leaves stale computed values instead of clearing/recomputing them | Wrong value | 3 |
| `RENAME COLUMN` over-renames an unrelated column in another table that happens to share the old column's name (collision in the FK-reference-update pass) | Wrong value | 4 |
| `ADD COLUMN … REFERENCES` keeps the inline column-constraint form instead of normalizing to a table-level `FOREIGN KEY (…) REFERENCES …` clause | Pure rendering (schema normalization) | 2 |
| Missing space before `(` in `REFERENCES table(col)`, extraneous quoting of an unquoted-safe renamed table/column identifier, and single-line-vs-multi-line reflow of the persisted `CREATE TABLE` text (one of these, `rename-self-1`, only surfaces as a wrong boolean because it asserts via a `LIKE '%REFERENCES s1_new%'` substring check that the extraneous quoting defeats) | Pure rendering | 11 |

### `turso/attach/writes.sqltest` (21 cases) + `turso/attach/cross_db_views_rejection.sqltest` (9 cases)

| Root cause | Classification | Cases |
| --- | --- | --- |
| Cross-database JOIN/INSERT SELECT/subquery/DELETE-with-subquery rejected ("Cross-database statements are not supported by managed ATTACH") | Missing feature | 8 |
| Multi-database transactions rejected ("Managed ATTACH transactions cannot modify more than one database…") | Missing feature | 6 |
| Schema-qualified `CREATE VIEW`/`DROP VIEW` rejected outright | Missing feature | 2 |
| `turso_cdc` (change-data-capture) table not implemented | Missing feature | 3 |
| `DROP TABLE IF EXISTS aux.t1` errors instead of no-op for a nonexistent schema-qualified table | Wrong value | 1 |
| `CREATE INDEX … ON t1 (name)` persisted schema text drops the space before `(` | Pure rendering | 1 |
| Cross-database `CREATE VIEW`/subquery/EXISTS/UNION/CTE bodies correctly rejected, but with a generic message instead of Turso's `view cannot reference table in attached database: schema.table` | Message-text-only | 7 |
| Same-database views (schema-qualified same-db, or attached-db-local) incorrectly rejected as if cross-database | Wrong value | 2 |

### `turso/analyze.sqltest` (5 cases)

`ANALYZE` does not create/populate `sqlite_stat1` for `:memory:` databases (4 cases, missing
feature), and `ANALYZE` on a `WITHOUT ROWID` table does not reject as the pinned corpus expects
(1 case, missing validation). All 5: wrong value / missing feature.

### `turso/attach/{default,memory,sequence}.sqltest` (2 + 3 + 4 = 9 cases)

| Root cause | Classification | Cases |
| --- | --- | --- |
| `ATTACH … AS main`/`AS temp` correctly rejected, message differs ('cannot attach database as main/temp' vs pattern 'in use') | Message-text-only | 2 |
| Cross-database JOIN via `ATTACH ':memory:'` rejected (same root cause as attach/writes above) | Missing feature | 2 |
| `PRAGMA database_list(<arg>)` rejects an argument SQLite/Turso silently accept as a no-op | Wrong value | 1 |
| Sequence not found reported before checking the schema exists (`sequence "bogus.my_seq" does not exist` vs pattern `no such database: bogus`) | Message-text-only (order-of-checks) | 1 |
| Per-schema/attached-database `SEQUENCE` `nextval`/`currval`/`setval` state is not scoped independently per schema — values from what should be two independent sequences interleave | Wrong value | 3 |

### `turso/mvcc_sequence.sqltest` (3) + `turso/mvcc_sequence_adversarial.sqltest` (5)

All wrong value. `mvcc_sequence.sqltest`: after switching back from `mvcc` to `wal` journal
mode, `PRAGMA journal_mode` still reports `mvcc` (3 cases). `mvcc_sequence_adversarial.sqltest`:
a sequence/rowid counter's advance is rolled back with its owning transaction, where Turso's
semantics is that the counter is *not* rolled back ("burned") even on rollback (5 cases).

### `turso/gencol-index-order.sqltest` (7 cases)

| Root cause | Classification | Cases |
| --- | --- | --- |
| EQP reports `SEARCH/SCAN … USING COVERING INDEX …` where the corpus expects the plain `USING INDEX …` label (same index is used either way) | Pure rendering (EQP vocabulary) | 3 |
| EQP reports an opaque `MANAGED COMPILED VDBE`/`MANAGED EVALUATOR FALLBACK` step instead of a real index-scan/sort plan entry for `GROUP BY`/`MAX`/`ORDER BY` over a virtual generated column | Wrong value (EQP completeness gap — likely PLAN/EQP workstream territory) | 4 |

### Remaining files (1 case each unless noted)

| File::case | Root cause | Classification |
| --- | --- | --- |
| `turso/group-by-having-infinite-loop.sqltest::group-by-literal-having-value` | `GROUP BY` ordinal-reference off-by-one ("1st GROUP BY term out of range - should be between 1 and 1") on a query that should succeed | Wrong value |
| `turso/insert-default.sqltest` (6 cases) | `INSERT … VALUES (DEFAULT, …)` not implemented ("no such column: DEFAULT") | Missing feature |
| `turso/invalid-argument-error-message.sqltest::invalid-argument-error-has-no-prefix` | expects an error, statement succeeds | Wrong value (missing validation) |
| `turso/json_object_star.sqltest` (2 cases) | `json_object(t.*)` does not pick up a derived/computed column alias, and does not exclude a virtual table's hidden columns (`generate_series`'s start/stop/step) | Wrong value |
| `turso/mvcc_feature_compat.sqltest` (4 cases) | DDL (`CREATE`/`ALTER`/`DROP TABLE`, `CREATE INDEX`) is allowed inside `BEGIN CONCURRENT`, where Turso rejects it with "DDL statements require an exclusive transaction" | Wrong value (missing validation) |
| `turso/raise.sqltest` (5 cases) | `RAISE()` is restricted to trigger bodies only; Turso permits it in a broader set of contexts (e.g. inside `CASE`/scalar contexts) and surfaces the custom message text supplied to `RAISE(ABORT, '…')` | Wrong value (missing feature — narrower validation than Turso) |
| `turso/sequence_adversarial.sqltest::alter-rename-rejects-internal-prefix-target` | expects an error (rename to a reserved internal-prefix name), statement succeeds | Wrong value (missing validation) |
| `turso/within-group.sqltest::ordered-set-correlated-outer-fraction`, `::percentile-correlated-null-fraction-is-null` | correlated outer-column reference inside `percentile_cont`/`percentile_disc`'s fraction argument is not resolved ("no such column: oc.frac") | Wrong value |
| `turso/within-group.sqltest::percentile_disc_rejects_percentage` | rejects a fraction value Turso accepts ("Percentile value must be between 0.0 and 1.0 inclusive") | Wrong value (over-strict validation) |
| `turso/within-group.sqltest::percentile-cont-floats-30` | `percentile_cont`'s linear interpolation returns `2.4000000000000004` instead of `2.4` | Wrong value (floating-point precision) |
| `turso/within-group.sqltest::within-group-column-collation` | ordered-set aggregate returns wrong case (`Apple` instead of `apple`) — likely a `NOCASE` collation propagation bug | Wrong value |

`turso/legacy-unquoted-view-columns.sqltest` (3 cases) is a distinct, narrower **real
schema-loader gap**, not a harness limitation: its static fixture's whole point is a malformed
`CREATE VIEW` row Turso tolerates on open (the database opens, the rest of the schema works,
only the one broken view is unavailable), but `ManagedSchemaRowParser.ParseView` rethrows the
parser's "Expected RightParen" error instead of degrading gracefully, so the whole database
fails to open. Recorded as an ordinary entry in `managed-sqltest-expected-failures.txt`
("database failed to open: Expected RightParen. At SQL offset 19."), assigned to the
coordinator — same mechanism as the integrity fixtures above, not a harness exclusion.
