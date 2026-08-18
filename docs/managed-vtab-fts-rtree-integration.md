# Managed FTS and R-Tree integration

## Upstream reference

Turso `v0.7.2` parses `CREATE VIRTUAL TABLE` into `CreateVirtualTable`
(`turso-src/sqlite/parser/src/ast.rs`) and routes module instances through
`VirtualTable` (`turso-src/core/vtab.rs`). Its planner calls `best_index` with
constraints and ordering, then sends the plan and bound arguments to cursor
`filter`. Turso's built-in FTS is an **index method** (`CREATE INDEX ... USING
fts`), not an FTS5 virtual table (`turso-src/core/index_method/fts.rs`), and
is implemented with Tantivy. The pinned source has no R-Tree module.

The managed implementation follows the lifecycle shape without copying the
native extension ABI or Tantivy dependency. Turso's index-method FTS remains a
separate, possible future alignment path; this module is not an FTS5-parity
claim.

## Implemented modules

`ManagedVirtualTableModuleRegistry` statically registers direct singleton
instances of these NativeAOT-safe modules:

| Module | Accepted declaration | Initial scope |
| --- | --- | --- |
| `fts5` | One or more identifier column names | Managed token/posting index with term, phrase, prefix, AND/OR/NOT queries through the virtual-table cursor contract |
| `rtree` | `id, min0, max0, ...` | Finite floating-point bounds and equality/range cursor constraints |
| `rtree_i32` | `id, min0, max0, ...` | Integer-only coordinates and equality/range cursor constraints |

The modules use the canonical `ManagedVirtualTableModule`,
`ManagedVirtualTable`, `ManagedVirtualTableCursor`, and
`ManagedVirtualTableModuleRegistry` APIs. Registration happens in the
registry's static constructor, uses no reflection or assembly scanning, and
directly roots every module implementation for trimming and NativeAOT.

`ManagedFtsTokenizer`, `ManagedFtsQueryParser`, and `ManagedFtsIndex` provide
the FTS module's reusable tokenization, query parsing, and posting store.
`ManagedRTreeBounds` and `ManagedRTreeIndex` provide inclusive
N-dimensional bounds and deterministic spatial storage. The module adapters
own virtual-table schema validation, `VUpdate` argument conversion, and
transaction snapshots; the reusable components do not own catalog state.

## Durable managed catalog contract

Each managed virtual table returns a
`ManagedVirtualTablePersistencePayload`: a positive module-defined version and
an opaque byte payload. The engine never reflects over modules or interprets
their bytes. Instead, it gives the payload back to the statically registered
module through `ManagedVirtualTableModule.Create(context, payload)` when it
recreates a catalog snapshot, opens a file, or rolls back a
transaction/savepoint.

For file-backed databases, the catalog writes a real `sqlite_schema` row with
`type='table'`, `rootpage=0`, and a `CREATE VIRTUAL TABLE ... USING ...`
declaration. An Ahtola-private comment on that declaration carries the
versioned, base64-encoded module payload. This keeps the payload in the
SQLite-format catalog and makes full catalog rewrites, `VACUUM INTO`, reopen,
and pager/WAL recovery restore the same registered module and state. Invalid,
unsupported-version, truncated, or malformed payloads fail closed during
catalog reconstruction.

This is deliberately **not** an emulation of FTS5 or R-Tree shadow tables.
Classic SQLite readers can enumerate the rootpage-zero declaration in
`sqlite_schema`, but the private payload is meaningful only to Ahtola and
other engines must not be expected to query or maintain these tables.

`CREATE VIRTUAL TABLE`, `SELECT` scans, `DROP`, module creation, cursors, and
SQL `INSERT`, `UPDATE`, and `DELETE` use the canonical foundation. The
foundation extracts source-local conjunctive predicates and ordering, passes
the selected `SqlValue` arguments to `Filter`, and honors
`ManagedVirtualTableConstraintUsage.Omit`. FTS `MATCH` and R-Tree
equality/range plans are therefore available through ordinary SQL, while DML
uses the VUpdate old-rowid/new-rowid/declared-column layout under
begin/sync/commit/rollback.

Every successful virtual-table DML statement publishes a new payload in the
working catalog. Failed statements retain the prior payload; explicit
transactions and savepoints use independent module instances recreated from
their saved payloads, so `ROLLBACK` and `ROLLBACK TO` restore module state as
well as schema state for `CREATE`, `DROP`, and `RENAME`.

## Remaining product limitations

- `fts5` is a small managed query/tokenizer subset, not FTS5 compatibility:
  tokenizer options, external/contentless tables, auxiliary functions,
  ranking, snippets, and FTS5-specific command syntax are unsupported.
- `rtree` does not expose SQLite's full R-Tree auxiliary/geometry callback
  surface. `rtree_i32` accepts only signed 32-bit integer coordinates.
- The private payload is a managed catalog extension, not a portable FTS5/R-Tree
  file representation. Ahtola intentionally does not synthesize shadow tables.
- The current generic DML path auto-assigns FTS rowids; explicitly naming
  `rowid` in an `INSERT` column list is not supported yet.
- Planner extraction is deliberately limited to source-local conjunctive
  predicates. The modules do not consume ordering, so the engine remains
  responsible for applying `ORDER BY`.
