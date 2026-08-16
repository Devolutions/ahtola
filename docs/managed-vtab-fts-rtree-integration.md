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
direct `Update` lifecycle calls use the canonical foundation. Ordinary SQL
`INSERT`, `UPDATE`, and `DELETE` are not yet lowered to the foundation's
`ManagedVirtualTable.Update` contract, so writes are currently exercised at
that contract boundary rather than asserted as SQL DML behavior.

The modules already implement `BestIndex` and cursor `Filter` for FTS `MATCH`
and R-Tree equality/range constraints. The present
`EmbeddedDatabase.GetVirtualTableRows` calls `BestIndex` with an empty
constraint/order list and `Filter` with no arguments. Thus SQL predicates
currently execute only after an unfiltered virtual scan and cannot drive FTS
`MATCH` or R-Tree range pushdown.

## Smallest follow-up foundation change

No second module API is needed. To enable SQL predicate integration, extend
only the existing virtual-table scan path to:

1. Collect source-local, conjunctive `WHERE` constraints with column ordinal,
   operator (`MATCH`, equality, and ranges), and usability.
2. Pass those constraints and source-local `ORDER BY` terms to `BestIndex`.
3. Evaluate the selected constraint expressions in the returned ordinal
   argument order and pass the resulting `SqlValue` values to `Filter`.
4. Honor `ManagedVirtualTableConstraintUsage.Omit` when deciding which
   predicates the engine evaluates after the scan.

That incremental change preserves the existing static module/table/cursor
contracts and lets the implemented FTS/R-Tree plans become SQL-visible without
an alternate parser, catalog, registry, or dispatch stack. SQL DML lowering
should then route VUpdate-style old rowid, new rowid, and declared columns to
the already implemented `Update` methods under the existing
begin/sync/commit/rollback lifecycle.
