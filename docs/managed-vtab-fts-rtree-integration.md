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

## Current integration scope

The initial modules are in-memory only. The current foundation intentionally
rejects managed virtual tables on file-backed databases because it has no
module-specific persistence/reopen contract. Consequently, managed FTS/R-Tree
content is not durable, is not included in backup/catalog recovery, and must
not be represented as persistent SQLite-compatible shadow tables yet.

`CREATE VIRTUAL TABLE`, `SELECT` scans, `DROP`, module creation, cursors, and
SQL `INSERT`, `UPDATE`, and `DELETE` use the canonical foundation. The
foundation extracts source-local conjunctive predicates and ordering, passes
the selected `SqlValue` arguments to `Filter`, and honors
`ManagedVirtualTableConstraintUsage.Omit`. FTS `MATCH` and R-Tree
equality/range plans are therefore available through ordinary SQL, while DML
uses the VUpdate old-rowid/new-rowid/declared-column layout under
begin/sync/commit/rollback.

## Remaining product limitations

- The modules remain in-memory only; no file-backed catalog reopen, shadow
  storage, backup, or recovery support exists.
- `fts5` is a small managed query/tokenizer subset, not FTS5 compatibility:
  tokenizer options, external/contentless tables, auxiliary functions,
  ranking, snippets, and FTS5-specific command syntax are unsupported.
- `rtree` does not yet persist shadow tables or expose SQLite's full R-Tree
  auxiliary/geometry callback surface. `rtree_i32` accepts only signed
  32-bit integer coordinates.
- The current generic DML path auto-assigns FTS rowids; explicitly naming
  `rowid` in an `INSERT` column list is not supported yet.
- Planner extraction is deliberately limited to source-local conjunctive
  predicates. The modules do not consume ordering, so the engine remains
  responsible for applying `ORDER BY`.
