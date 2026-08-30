# Managed FTS and R-Tree integration

## Upstream reference

The pinned Turso `v0.8.0-pre.7` source parses `CREATE VIRTUAL TABLE` into `CreateVirtualTable`
(`turso-src/sqlite/parser/src/ast.rs`) and routes module instances through
`VirtualTable` (`turso-src/core/vtab.rs`). Its planner calls `best_index` with
constraints and ordering, then sends the plan and bound arguments to cursor
`filter`. Turso's built-in FTS is an **index method** (`CREATE INDEX ... USING
fts`), not an FTS5 virtual table (`turso-src/core/index_method/fts.rs`), and
is implemented with Tantivy. The pinned source has no R-Tree module.

The managed implementation follows the lifecycle shape without copying the
native extension ABI or Tantivy dependency. Turso's index-method FTS remains a
separate implementation path. Ahtola reuses its own pure-managed posting,
query, ranking, and offset machinery for the SQL-compatible FTS5 slice below;
it does not reuse Tantivy or claim Tantivy's storage representation.

## Implemented modules

`ManagedVirtualTableModuleRegistry` statically registers direct singleton
instances of these NativeAOT-safe modules:

| Module | Accepted declaration | Scope |
| --- | --- | --- |
| `fts5` | One or more identifier columns, optional `UNINDEXED`, and the options listed below | Content-owning managed FTS5 with bounded MATCH, rank/BM25, highlighting/snippets, and `optimize`/`rebuild` |
| `rtree` | `id, min0, max0, ... [, +aux ...]` | SQLite-style 1–5D float32 bounds, auxiliary values, id/range plans, and diagnostics |
| `rtree_i32` | `id, min0, max0, ... [, +aux ...]` | SQLite-style 1–5D signed-int32 bounds, auxiliary values, id/range plans, and diagnostics |

The modules use the canonical `ManagedVirtualTableModule`,
`ManagedVirtualTable`, `ManagedVirtualTableCursor`, and
`ManagedVirtualTableModuleRegistry` APIs. Registration happens in the
registry's static constructor, uses no reflection or assembly scanning, and
directly roots every module implementation for trimming and NativeAOT.

`ManagedFtsTokenization`, `ManagedFtsQueryLanguage`,
`ManagedFtsSearchIndex`, and `ManagedFtsFunctions` provide the FTS module's
reusable tokenization, bounded query parsing, scored posting store, and exact
source-offset rendering.
`ManagedRTreeBounds` and `ManagedRTreeIndex` provide inclusive
N-dimensional bounds and deterministic spatial storage. Searches descend only
into node rectangles that can satisfy the active axis constraints; deletion
performs deterministic condense-by-reinsertion. The module adapters own
virtual-table schema validation, `VUpdate` argument conversion, auxiliary
values, and transaction snapshots; the reusable components do not own catalog
state.

## Content-owning FTS5 SQL surface

The managed `fts5` module accepts bare identifier columns with optional
`UNINDEXED`, plus this fail-closed option subset:

| Option | Accepted values | Managed behavior |
| --- | --- | --- |
| `tokenize` | `unicode61`, `ascii`, `trigram` | Selects the matching statically rooted managed tokenizer. Tokenizer modifiers and `porter` are rejected. |
| `prefix` | One to 16 distinct integer lengths from 1 through 999 | Validated and retained in the declaration. Prefix MATCH uses the managed posting dictionary's bounded live-term expansion; no separate SQLite prefix shadow index is synthesized. |
| `detail` | `full`, `column`, `none` | Controls retained positional/column detail. Phrase/NEAR and column-filter queries fail when the selected detail cannot answer them. |
| `columnsize` | `0`, `1` | Accepted with SQLite-visible ranking semantics. Ahtola derives token lengths from its content rows instead of creating a `%_docsize` shadow table. |

`content` and `content_rowid` are rejected. Contentless and external-content
tables require SQLite's shadow-table/trigger contracts and are not represented
by Ahtola's private payload.

MATCH supports bounded FTS5 term, phrase, quoted-phrase prefix, binary
`NOT`, column-filter, initial-token anchor, and `NEAR(phrase ..., distance)`
grammar, including `+` phrase concatenation and independently prefixed phrase
components within or outside `NEAR`. A tokenless concatenation atom after a
term preserves SQLite's empty boundary by replacing that component's prefix
state with the empty atom's trailing-`*` state instead of dropping the atom.
Implicit
`AND` binds more tightly than binary `NOT`. Barewords use SQLite FTS5's
restricted character set; reserved punctuation such as `.`, `/`, and `,`
must be quoted. A
`column MATCH ?` restriction intersects every explicit column filter in the
query. BLOB content is decoded as UTF-8 for indexing and auxiliary rendering
while the stored SQL value remains a BLOB. Both `table MATCH ?` and
`column MATCH ?` are planner constraints. MATCH cursors expose the hidden
`rank` column, use SQLite's negative BM25 convention (lower is better), return
default scans in rank order, and consume a complete `ORDER BY rank ASC|DESC`.
A full scan exposes `rank` as NULL.

These source-bound auxiliary functions are available on an FTS5 row:

- `bm25(table [, weight...])`
- `highlight(table, column, before, after)`
- `snippet(table, column, before, after, ellipsis, tokens)`, including column
  `-1` for automatic selection

The first argument is resolved from the cursor binding carried by the row,
including through joins and correlated outer-row chains. It is not inferred
from a same-named ordinary column. Application-registered scalar callbacks
still shadow these built-ins. BM25 uses SQLite's IDF floor, total-document
length normalization, negative score convention, and per-column term weights.
Terms and phrases retain per-column occurrence frequencies for weighting;
each constituent phrase of a matching NEAR group contributes independently,
matching stock SQLite's BM25 behavior. Default and weighted scores are covered
by stock-SQLite oracle tests.

`INSERT INTO table VALUES (...)` targets visible columns only. Explicit
`rowid`, `_rowid_`, and `oid` inserts and updates map to VUpdate's new-rowid
slot unless a real declared column shadows that alias. Content-owning tables accept
`INSERT INTO table(table) VALUES('optimize')` and `... VALUES('rebuild')`.
Other commands, rank configuration, and `delete-all` fail closed.

## Managed R-Tree SQL surface

`rtree` and `rtree_i32` accept one through five dimensions. The declaration is
an id followed by min/max pairs and then optional SQLite `+auxiliary` columns;
there may be at most 100 total columns. Quoted names are retained, declaration
type decorations are ignored like SQLite's module, and `table_info` reports
`INT` for the id, `REAL`/`INT` for coordinates, and an empty type for
auxiliaries. `table_xinfo` and `table_list` include the virtual table. Index
and trigger creation and non-rename ALTER operations use SQLite's virtual-table
rejection paths.

Coordinate writes use SQLite's conversion rules: NULL and non-numeric input
become zero, numeric text/blob prefixes are accepted, infinities are retained,
and `rtree` stores float32 values with the same outward rounding constants as
SQLite (`min` down, `max` up). `rtree_i32` truncates through SQLite int64
conversion and then uses the low signed 32 bits. Inverted bounds raise a
constraint `EmbeddedSqlException`; no CLR range exception escapes.

The declared id, not an independently supplied `rowid`, is authoritative.
NULL ids allocate from the current maximum plus one (so deleting the maximum
permits reuse); a `long.MaxValue` maximum uses SQLite's bounded random-positive
fallback. Duplicate ids honor `IGNORE`, `REPLACE`, `FAIL`, `ABORT`, and
`ROLLBACK`. The generic VUpdate instruction carries that conflict mode to the
module. INSERT supports VALUES, SELECT, DEFAULT VALUES, and SQLite's
pre-xUpdate INSERT RETURNING row image. SQLite rejects UPDATE/DELETE RETURNING
for virtual tables, and Ahtola does too. UPDATE supports id changes, FROM, and
the managed pipeline's ORDER BY/LIMIT extension; DELETE supports its
ORDER BY/LIMIT extension. Auxiliary columns retain arbitrary NULL, INTEGER,
REAL, TEXT, and BLOB values.

Planner extraction recognizes the declared id and unshadowed
`rowid`/`_rowid_`/`oid` aliases, both comparison directions, correlated
arguments, and equality/range/`!=`/`IS`/`IS NOT` predicates. Direct id equality
uses dictionary lookup. Coordinate predicates use the R-Tree's axis envelopes
instead of snapshot-and-filter. Non-numeric and NULL constraint values follow
SQLite's storage-class ordering, and large integer strict inequalities are
widened exactly as SQLite does before a predicate is marked omitted.

`rtreecheck(name)` and `rtreecheck(schema,name)` validate the managed tree and
payload invariants. `rtreedepth(blob)` and `rtreenode(dimensions,blob)` decode
SQLite node headers/cells for diagnostics. `integrity_check` and `quick_check`
also include every managed R-Tree.

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
versioned, base64-encoded module payload. R-Tree payload version 2 stores
canonical float32/int32 coordinate words plus typed auxiliary values and
upgrades the earlier coordinate-only version 1. This keeps the payload in the
SQLite-format catalog and makes full catalog rewrites, `VACUUM INTO`, reopen,
and pager/WAL recovery restore the same registered module and state. Invalid,
unsupported-version, truncated, or malformed payloads fail closed during
catalog reconstruction.

This is deliberately **not** an emulation of FTS5 or R-Tree shadow tables.
Classic SQLite readers can enumerate the rootpage-zero declaration in
`sqlite_schema`, but the private payload is meaningful only to Ahtola. A
stock-SQLite R-Tree uses `%_node`, `%_parent`, and `%_rowid` b-trees with
module-owned node blobs. Ahtola's catalog cannot publish or incrementally
maintain those hidden b-trees without a separate shadow-table storage
foundation. It therefore fails closed when opening a foreign payload-less
R-Tree declaration, and stock SQLite fails querying an Ahtola R-Tree because
those shadow tables intentionally do not exist. Tests exercise both directions;
no file-interoperability claim is made.

`CREATE VIRTUAL TABLE`, `SELECT` scans, `DROP`, module creation, cursors, and
SQL `INSERT`, `UPDATE`, and `DELETE` use the canonical foundation. Direct
single-source virtual-table projections are real resumable VDBE programs
(`VOpen`, `VFilter`, `VColumn`, `VNext`); rows are pulled one at a time and
the cursor stays open only until `Halt`, reset, cancellation, failure, or
statement disposal. `EXPLAIN` reports that executed opcode sequence.
`VCreate`, `VDestroy`, and `VRename` likewise own the module callback portion
of schema changes. Catalog publication still surrounds those callbacks so a
throwing create, destroy, or rename cannot publish half a schema change and
does not blur `Disconnect` with `Destroy`.
Unlike Turso's extension-ABI `VCreate`, the managed instruction carries the
already parsed raw module arguments and a statically bound catalog publisher
instead of constructing an FFI record in a register. This is the narrow
intentional implementation difference required by the no-loadable-extension,
trim-safe contract; callback timing and statement atomicity remain the same.

The planner extracts source-local `=`, `!=`, range, `IS`, `IS NOT`, `IS
NULL`, `IS NOT NULL`, `MATCH`, unescaped `LIKE`, `GLOB`, `LIMIT`, and `OFFSET`
constraints, including unshadowed `rowid`/`_rowid_`/`oid`. A constraint that
depends on a not-yet-available join source is reported with `Usable=false`;
the correlated nested-loop form replans it after the outer row is available.
Only literal, parameter, or otherwise explicitly safe scalar arguments are
offered, and every constraint not marked `Omit` remains an engine residual.
Argument indexes returned by `BestIndex` must be unique, contiguous,
one-based, and within the constraint count; unusable constraints cannot
receive an argument or be omitted. `IS NULL` and `IS NOT NULL` may be omitted
without an argument because the operator itself is complete.

`EstimatedCost` and `EstimatedRows` choose between an unbound scan and a
correlated filtered scan and are included in `EXPLAIN QUERY PLAN`. The engine
elides its sorter only when `OrderByConsumed` covers the complete ORDER BY
(including direction and default NULL placement). FTS `MATCH`/rank ordering
and R-Tree equality/range plans are therefore available through ordinary SQL.
The pinned Turso planner does not consult every estimate/order field in every
shape; Ahtola does, so the managed public contract does not advertise inert
planner metadata.
Buffered aggregate, DISTINCT, non-consumed sort, and complex joined shapes
remain on the semantically equivalent evaluator path; they are intentionally
not described as VDBE fast paths.

Built-in table-valued functions are adapters over the same
`ManagedVirtualTable`/cursor planner contract. Positional-call syntax is
parser sugar for hidden-column equality constraints. `generate_series`
enumerates lazily and receives bounded LIMIT/OFFSET information, while
correlated calls create and filter a cursor per outer row. Cursor disposal
follows the same completion, cancellation, failure, reset, and statement
disposal rules as catalog virtual tables.

DML uses the `VUpdate` old-rowid/new-rowid/declared-column layout under
begin/sync/commit/rollback. VALUES, SELECT, and DEFAULT VALUES inserts,
conflict policies, INSERT RETURNING, UPDATE FROM/ORDER BY/LIMIT, and DELETE
ORDER BY/LIMIT all use the ordinary row-production/selection machinery.
SQLite itself rejects UPDATE/DELETE RETURNING on virtual tables, and the
managed engine preserves that diagnostic rather than inventing incompatible
semantics.

Every successful virtual-table DML statement publishes a new payload in the
working catalog. `ABORT` and ordinary statement errors retain the prior
payload; `FAIL` publishes mutations completed before the violating row, while
`ROLLBACK` ends the transaction. Explicit transactions and savepoints use
independent module instances recreated from their saved payloads, so
`ROLLBACK` and `ROLLBACK TO` restore module state as well as schema state for
`CREATE`, `DROP`, and `RENAME`.

Autocommit mutations receive one `Begin`/`Sync`/`Commit` sequence per
successful statement. In an explicit transaction a table receives `Begin`
only on its first mutation, then one `Sync`/`Commit` at the real `COMMIT`;
full rollback receives one `Rollback`, while `ROLLBACK TO` restores the saved
payload without ending the transaction.

`Disconnect` releases one catalog instance without deleting module-owned
persistent state. Database close and replacement or abandonment of catalog
snapshots call it exactly once. `Destroy` is reserved for `DROP TABLE`; if it
throws, the virtual table and dependent catalog objects remain present.
The authorizer reports SQLite-compatible action values 29
(`SQLITE_CREATE_VTABLE`) and 30 (`SQLITE_DROP_VTABLE`) with the table, module,
and schema arguments used by SQLite's hook contract.

## Remaining product limitations

- FTS5 shadow tables, `%_data`/`%_idx`/`%_content`/`%_docsize` file layouts,
  external/contentless tables, custom tokenizers, `fts5vocab`, rank
  configuration, and the complete stock FTS5 query grammar remain
  unsupported. The managed payload is not portable FTS5 storage.
- The ordinary-table `fts` **index method** remains a separate Ahtola surface;
  see [docs/managed-index-methods.md](managed-index-methods.md). It must not be
  confused with the `CREATE VIRTUAL TABLE ... USING fts5` contract here.
- R-Tree geometry/query callbacks registered through SQLite's native extension
  ABI are not exposed. The SQL declaration, numeric/storage, DML, planner,
  transaction, metadata, integrity, and diagnostic-function behavior described
  above is managed; `%_node`/`%_parent`/`%_rowid` file interoperability is not.
- The private payload is a managed catalog extension, not a portable FTS5/R-Tree
  file representation. Foreign R-Tree shadow layouts are rejected explicitly
  rather than guessed at or silently treated as empty.
- Planner extraction remains deliberately limited to safe conjunctive
  predicates. FTS5 consumes only a complete rank ordering; R-Tree and all
  other ordering remain the engine's responsibility.
