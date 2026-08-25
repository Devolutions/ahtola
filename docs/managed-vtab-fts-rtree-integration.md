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
| `rtree` | `id, min0, max0, ...` | Finite floating-point bounds and equality/range cursor constraints |
| `rtree_i32` | `id, min0, max0, ...` | Integer-only coordinates and equality/range cursor constraints |

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
N-dimensional bounds and deterministic spatial storage. The module adapters
own virtual-table schema validation, `VUpdate` argument conversion, and
transaction snapshots; the reusable components do not own catalog state.

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
grammar, including prefix and quoted-phrase operands within `NEAR`. Implicit
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
`ManagedVirtualTableConstraintUsage.Omit`. FTS `MATCH`/rank ordering and R-Tree equality/range plans are therefore
available through ordinary SQL, while DML
uses the VUpdate old-rowid/new-rowid/declared-column layout under
begin/sync/commit/rollback.

Every successful virtual-table DML statement publishes a new payload in the
working catalog. Failed statements retain the prior payload; explicit
transactions and savepoints use independent module instances recreated from
their saved payloads, so `ROLLBACK` and `ROLLBACK TO` restore module state as
well as schema state for `CREATE`, `DROP`, and `RENAME`.

## Remaining product limitations

- FTS5 shadow tables, `%_data`/`%_idx`/`%_content`/`%_docsize` file layouts,
  external/contentless tables, custom tokenizers, `fts5vocab`, rank
  configuration, and the complete stock FTS5 query grammar remain
  unsupported. The managed payload is not portable FTS5 storage.
- The ordinary-table `fts` **index method** remains a separate Ahtola surface;
  see [docs/managed-index-methods.md](managed-index-methods.md). It must not be
  confused with the `CREATE VIRTUAL TABLE ... USING fts5` contract here.
- `rtree` does not expose SQLite's full R-Tree auxiliary/geometry callback
  surface or SQLite's outward-rounded float32 coordinate storage.
  `rtree_i32` accepts only signed 32-bit integer coordinates.
- The private payload is a managed catalog extension, not a portable FTS5/R-Tree
  file representation. Ahtola intentionally does not synthesize shadow tables.
- Planner extraction is deliberately limited to source-local conjunctive
  predicates. FTS5 consumes only a complete rank ordering; R-Tree and all
  other ordering remain the engine's responsibility.
