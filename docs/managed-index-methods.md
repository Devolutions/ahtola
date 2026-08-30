# Managed index methods and the `fts` full-text method

Ahtola implements Turso-style **index methods**: a pluggable, statically registered access path
attached to an ordinary SQLite index through `CREATE INDEX … USING <method> (…)`. The first shipped
method is `fts`, a pure-managed full-text index. The same foundation is the intended substrate for
the vector index method tracked by `managed-vector-index`.

Everything below is pure managed C#. There is no Tantivy, no native library, no P/Invoke, and no
reflection: `ManagedIndexMethodRegistry`'s static constructor registers `ManagedFtsIndexMethod`
with a direct call, exactly like `ManagedVirtualTableModuleRegistry`.

## SQL surface

```sql
CREATE INDEX [IF NOT EXISTS] name ON table USING method ( col [, col …] )
    [ WITH ( key = literal [, key = literal …] ) ];

DROP INDEX [IF EXISTS] name;      -- releases all method state
REINDEX name;                     -- drops derived state and rebuilds it from the base rows
OPTIMIZE INDEX [name];            -- compacts one/all method indexes transactionally
```

The managed FTS method also accepts Turso's documented field-local tokenizer form:

```sql
CREATE INDEX docs_fts ON docs USING fts (
    title WITH tokenizer=simple,
    body  WITH (tokenizer=ngram, min_gram=2, max_gram=3)
);
```

Field-local options are represented in the parsed/catalog model and survive file reopen; they are
not accepted-and-ignored text. Other managed methods reject field-local options unless they
explicitly opt in.

Method indexes deliberately reject the shapes Turso also rejects
(`turso-src/core/schema.rs:5847-5853`):

| Rejected | Message |
| --- | --- |
| `CREATE UNIQUE INDEX … USING …` | `UNIQUE is not supported with an index method` |
| `… USING … WHERE <predicate>` (partial) | `A partial WHERE clause is not supported with an index method` |
| expression column under `USING` | `An index method column must be a plain column name` |
| `DESC` under `USING` | `DESC is not supported on an index method column` |
| `COLLATE` under `USING` | `COLLATE is not supported on an index method column` |
| `WITH (…)` without `USING` | `WITH is valid only on an index that declares USING` |
| method index on a `WITHOUT ROWID` table | `… cannot use an index method on WITHOUT ROWID table …` |
| method index on a view / `sqlite_*` table | `views may not be indexed` / `… may not be indexed` |
| unknown method name | `no such index method: <name>` |
| unknown `WITH` key | `unknown fts index parameter: <key>` |

`WITH` values are **literals only** (text, integer, real, blob, `NULL`, `TRUE`/`FALSE`, and a
leading sign on numbers), mirroring `resolve_index_method_parameters`
(`turso-src/core/translate/index.rs:1170`). Duplicate keys are rejected.

Auxiliary object names owned by a method embed the reserved infix `_ahtola_idxm_`. User DDL that
tries to create or collide with such a name fails with `object name reserved for internal use`.

## `fts` functions

| Function | Arity | Semantics |
| --- | --- | --- |
| `fts_match(col…, query)` | varargs | Boolean method-grammar match. A `NULL` query returns integer `0`. A covering index contributes its configured tokenizers; without one, the pinned `default` tokenizer is used. Only TEXT column values participate. |
| `fts_score(col…, query)` | varargs | Positive, float-precision BM25 (higher is better) when a covering method index supplies corpus statistics; otherwise REAL `0.0`, matching Turso's scalar fallback. |
| `fts_highlight(text…, before, after, query)` | varargs (minimum 4) | Concatenates non-NULL text inputs with spaces and wraps matching tokens. A NULL tag/query returns NULL. |
| `fts_highlight_legacy(text, query, before, after)` | 4 | Compatibility spelling for Ahtola's former argument order; `fts_highlight` itself is never interpreted ambiguously. |
| `fts_snippet(text, query, before, after, ellipsis, tokens)` | 6 | Densest window of `tokens` tokens containing matches, with matches wrapped and elisions marked. |

For a base table carrying an FTS method index, SQL also rewrites `col MATCH query`,
`(col1,col2) MATCH query`, and `NOT MATCH` to the method `fts_match` surface. A MATCH attached to a
base table with no FTS method index still raises `unable to use function MATCH in the requested
context`. A virtual-table MATCH is not rewritten and continues through FTS5's `BestIndex`/`Filter`
path and SQLite query grammar.

### Binding, determinism and shadowing

Three rules govern how a scalar `fts_*` call finds an index. They exist because breaking any of them
would let the *presence* of an index change the *meaning* of a query.

1. **Binding is by resolved source identity, never by column-name similarity.** All column arguments
   must resolve to one source — the qualifier they carry, or the source the current row belongs to
   when they are unqualified — and only indexes on *that* source's table are considered. The rowid
   used for corpus scoring is that source's rowid. An index on an unrelated table that happens to
   have columns with the same names has no effect, and in a join each row scores against its own
   source.

   The identity travels **on the row**, not in a statement-wide alias registry. Every scanned base
   row records the table it came from alongside its qualifier, joins merge one entry per contributing
   source, and lookups walk the outer-row chain. A statement-wide map keyed only by alias is
   overwritten the moment a nested query reuses the name — `SELECT d.id, fts_score(d.title, d.body,
   ?) FROM docs AS d JOIN (SELECT id FROM notes AS d) AS n ON n.id = d.id` scored the outer rows
   against `notes` — so the registry survives only as a fallback for callers that have no row at all
   (EXPLAIN), and it refuses to answer for any qualifier it saw bound to two different tables.
2. **Column order is irrelevant, configuration is not.** A call's resolved columns are compared as
   an unordered set. Tokenizers and weights are remapped by column name, so
   `fts_score(body,title,?)` retains the weights declared for `title` and `body`. If two covering
   indexes have observably different tokenizers, weights, detail, or column-size behavior, planning
   declines and scalar binding stays unbound. Identically configured duplicates use cost/currentness
   first and the lexical index name as the final tie-break.
3. **A shadowing connection callback suppresses everything.** Registering a scalar function named
   `fts_match`, `fts_score`, `fts_highlight` or `fts_snippet` on the connection replaces the built-in
   for every call. When that happens the planner declines every method plan and the scalar path stops
   consulting the index, so the user's callback is the only semantics in play.
4. **The function registry follows pinned Turso and marks all FTS scalars deterministic.** One
   intentional correctness boundary remains: `fts_score` is rejected in `CHECK` constraints because
   Ahtola's index-aware scalar can read a live corpus while that same statement mutates it. Stored
   index/generated expressions use Turso's deterministic registration; an unbound score is `0.0`.


### Query grammar

`USING fts` has a dedicated Tantivy-compatible profile. It is deliberately separate from the
SQLite grammar used by `CREATE VIRTUAL TABLE ... USING fts5`; changing one does not change the
other.

```
term            fox
adjacency       database search                              (OR)
boolean         database AND search | database NOT nosql
phrase          "quick brown"
prefix          data*
column filter   title:fox | body:"quick brown"
boost           title:database^2 body:database
```

Operators are the uppercase `AND`, `OR`, and `NOT` spellings used by Tantivy. Parentheses group
expressions; a leading `-` is also exclusion. A trailing finite non-negative `^number` multiplies
that subtree's score. Ahtola's earlier `^term` anchor and `NEAR/n(...)` extensions remain available
only to the legacy internal profile, not to SQL method queries. Managed FTS5 retains SQLite's
implicit-AND, phrase-concatenation, column-filter, anchor, and `NEAR(...)` grammar.

### `WITH` keys

| Key | Values | Default |
| --- | --- | --- |
| `tokenizer` | Pinned: `default`, `raw`, `simple`, `whitespace`, `ngram`; explicit Ahtola extensions: `unicode61`, `ascii`, `trigram` | `default` |
| `weights` | `'col=weight, col=weight'` (non-negative finite float boosts, no duplicate column) | `1.0` per column |
| `min_gram` / `max_gram` | integers, `1 ≤ min ≤ max ≤ 16`; **accepted only with `tokenizer = 'ngram'`** | `2` / `3` |
| `columnsize` (Ahtola extension) | `0` or `1`; `0` disables per-column BM25 length normalization | `1` |
| `detail` (Ahtola extension) | `full`, `columns`, `none` | `full` |

Unknown keys are a hard error, matching the `fts_with_keys_all_validated_and_consumed` intent
(`turso-src/core/index_method/fts.rs:1640-1649`). Every accepted key is *implemented*; nothing is
parsed and then ignored:

- `min_gram`/`max_gram` on a non-gram tokenizer is rejected (`… require the 'ngram' or 'trigram'
  tokenizer`), and on `trigram` — whose gram size is fixed at 3 — it is rejected as well.
- `columnsize = 0` genuinely disables length normalization; BM25 degrades to the unnormalized
  saturation curve. Any value other than `0` or `1` is rejected.
- `detail = 'columns'` stops recording token positions, so phrase and legacy `NEAR`/anchor
  (`^term`) queries fail with `… does not record positions, so … queries are unavailable` rather
  than silently returning wrong rows. It does keep the metadata a column-specific question needs: a
  compact per-column occurrence count is stored alongside the column mask, so `col:term` matches
  every row that carries the term in that column — even when the same term also occurs in an
  unselected one — and BM25 weights each selected column by its own frequency. `detail = 'none'`
  additionally drops column attribution, so `col:term` filters fail with `… does not record column
  attribution`. Every error is raised on the scalar path as well as the indexed path, so the plan
  can never change whether a query errors.

### Tokenization and offsets

The pinned tokenizer behavior is distinct:

- `default`: Unicode alphanumeric runs, lowercase, and Tantivy's remove-long filter (only terms
  shorter than 40 UTF-8 bytes survive);
- `raw`: one exact, case-sensitive whole-field token;
- `simple`: split Unicode punctuation/whitespace and preserve case;
- `whitespace`: split only whitespace and preserve punctuation/case;
- `ngram`: lowercase 2- and 3-character sliding grams by default.

The explicitly named `unicode61`, `ascii`, and `trigram` extensions retain their previous managed
behavior. Offset-bearing tokenization keeps highlight/snippet source spans exact.

Query text goes through the tokenizer of each searched field. For heterogeneous field tokenizers,
an unqualified operand is analyzed once per field and ORs those field-specific queries, just as
Tantivy's default-field parser does. A gram tokenizer has no separate prefix notion; a term shorter
than `min_gram` cannot be sliced and therefore never matches.

### Ranking

Okapi BM25 with `k1 = 1.2`, `b = 0.75`:

```
score = Σ_terms Σ_cols  w_col · IDF(t) · tf_col·(k1+1) / (tf_col + k1·(1 − b + b·|D_col|/avgdl_col))
IDF(t) = ln(1 + (N − n + 0.5) / (n + 0.5))
```

Scores are quantized to Tantivy's observable `f32` precision, are positive (higher is better), and
ordered descending with ascending-rowid tie-breaking. Field and query `^boost` factors multiply
term/phrase/prefix contributions. The managed phrase implementation remains an approximation of
Tantivy's internal phrase scorer; ordering and monotonic boost behavior are covered, but exact
cross-engine score bits are not promised.

### Posting generations

Every posting carries the generation of the document image it was produced from, and a document's
generation is bumped on each upsert. A rowid that is deleted and re-inserted — an `UPDATE`, an
`INSERT OR REPLACE`, an `UPSERT`, or SQLite's rowid reuse after a delete — therefore retires its
previous postings *immediately*, not merely when compaction gets around to reclaiming them. Without
the stamp the physically-present old posting would become visible again the moment the rowid became
live again, and a query for the old text would return the row.

Tokenization also runs to completion, and every per-document limit is checked, *before* any index
state is mutated. A document that trips a limit leaves the index exactly as it was rather than
half-updated with the previous image already removed.

## Persistence representation

**Read this before assuming SQLite compatibility.**

A method index has two durable parts:

1. **The ordinary SQLite index b-tree.** A method index is a real `index` row in `sqlite_schema`
   with a real, non-zero `rootpage`, and the file store builds, validates and rewrites its b-tree
   through exactly the same code path as any other secondary index. Page format, cell format, WAL
   framing, rollback journal, freelist, `VACUUM` and backup are untouched — there is **no custom
   page type and no new cell layout**.
2. **A versioned state header** appended to the index's `sqlite_schema.sql` text as a trailing SQL
   comment: `/*ahtola-index-method:<version>:<base64>*/`. This is the same mechanism already shipped
   for managed virtual tables (`ManagedVirtualTableSchemaSql`), so it is written and rolled back by
   the same pager/WAL transaction that writes the rest of `sqlite_schema`. The header carries the
   storage version, column count, global and per-field tokenizer identities/gram windows, and
   column weights. Field-local declarations also live in canonical CREATE text and are revalidated
   against the version-2 envelope on reopen.

**The postings are derived state, not durable state.** The inverted index (term dictionary, term
frequencies, positions, column masks, generation-stamped tombstones) is reconstructed from the base
rows and kept in the catalog snapshot that the engine already isolates per connection, transaction
and savepoint. That is why atomicity is *inherited* rather than reimplemented: rolling a statement,
transaction or savepoint back restores the rows, and the derived state reconciles against them.
State version 2 records field-local analyzers. A version-1 declaration is accepted only when its
stored global tokenizer still agrees with the explicit/current declaration; an old implicit
`unicode61` default is not silently reinterpreted as the new pinned `default`.

### Keeping the derived state current

Reconciliation is **revision aware**, not a walk of the base rows on every use:

- `RowStore` already bumps a revision counter on every base-row mutation. An attachment records the
  revision it last reconciled at, so a query against an unchanged table costs `O(1)`.
- `QueryContext.ReportRowChange` — the single funnel every DML path passes through, including plain
  `INSERT`/`UPDATE`/`DELETE`, `REPLACE` and `UPSERT` conflict resolution, trigger bodies and foreign
  key cascade actions — appends the touched rowid to a per-table mutation journal
  (`ManagedIndexMethodJournal`). A statement that changed *k* rows then costs `O(k)`.
- The journal's safety argument is one-sided on purpose. Re-deriving a row that did not change is
  idempotent, so naming extra rowids is harmless; missing one is not. A delta is accepted only when
  every revision bump in its range was recorded. A gap (a mutation that reached the row store without
  reaching the journal) poisons it until the next full rebuild, and a trailing unrecorded bump makes
  the journal refuse. The fallback is always a correct full rebuild, never a stale answer.
- Catalog snapshots do **not** copy the journal, and forked attachments start empty. A restored
  snapshot therefore rebuilds from the rows it restored; a delta recorded before a rollback can never
  be replayed against post-rollback state.
- `CommitTransaction` runs each published method's `PreCommit` hook inside the same pager/WAL
  transaction that writes the catalog, so nothing method-visible is left pending across a commit.

### Attachment lifecycle

An attachment is cached on its table only after it is fully constructed *and* its persisted state
envelope has loaded. A `CREATE INDEX` that throws — an unknown `WITH` key, a rejected option
combination, a malformed envelope — removes the cache entry on the way out, so the next statement
starts from a clean slate instead of finding half-initialized state. Cache entries are keyed by the
index *definition instance*, not merely by name, so dropping `docs_fts` and recreating it under the
same name with different `WITH` options can never resurrect the previous options.

`REINDEX` and the method's `Optimize` hook both build into a **detached** posting set and publish it
only once the build succeeded, so a rebuild that throws part-way leaves the previously published
state whole and queryable. `DROP INDEX` runs the method's `Destroy` hook before the attachment is
forgotten.

### Why not hidden shadow tables

Ahtola's engine is an in-memory catalog and row store that the file store serializes into SQLite
b-trees at commit; there is no per-statement b-tree mutation API a method could write postings
through. Synthesizing user-visible shadow tables would have added a large new catalog surface
(`DROP`, `ALTER`, `VACUUM`, `ATTACH`, authorizer, `sqlite_master` visibility) without buying
interoperability, because…

### Interoperability: none, by construction

`CREATE INDEX … USING fts (…)` is **not parseable by stock SQLite**. A database containing a method
index is an Ahtola/Turso database: stock `sqlite3` reports a malformed schema for that row. Do not
describe this format as SQLite-compatible or as an FTS5 shadow-table layout — it is neither. This is
the same contract already documented for the managed `fts5`/`rtree` virtual tables in
[docs/managed-vtab-fts-rtree-integration.md](managed-vtab-fts-rtree-integration.md).

### Fail-closed matrix

| Condition | Behavior |
| --- | --- |
| `USING m` where `m` is not registered | `no such index method: m` while loading the catalog |
| state version newer than this build | `index 'x' was written by a newer managed index method (vN)` |
| truncated state header | `malformed managed index 'x': truncated state` |
| column count, tokenizer, gram bounds, `detail`, `columnsize` or weights disagree with the declaration | `malformed managed index 'x': …` |
| envelope present but empty | `managed index method state is empty` |
| encoded envelope longer than `MaxStateEncodedChars` | `managed index method state exceeds its maximum size` (checked **before** the base64 decode allocates) |
| state base64 or version token malformed | `managed index method state is not valid base64` / `… state version is invalid` |
| state header absent | rebuilt silently from the base rows (it is a cache, not the authority) |
| trailing comment that merely *looks* like an envelope on an ordinary index | left untouched; the declaration round-trips byte for byte |

The envelope is only decoded once the candidate declaration has been parsed and proven to be a
`USING`-method index. An ordinary `CREATE INDEX … (col) /*ahtola-index-method:2:…*/` keeps its
comment: it is the user's SQL text, not method state.

## Planning and execution

The planner mirrors Turso's optimizer stage that matches a single-table source against a method's
declared patterns, most specific first (`turso-src/core/translate/optimizer/mod.rs:236-413`), and
restricts candidates to single-table access paths (`mod.rs:2368`).

**The core planner contains no method-specific SQL knowledge.** It hands each candidate index a
`ManagedIndexMethodPlannerContext` (source qualifier, index columns, predicate, `ORDER BY`, literal
limit, a shadowed-function probe and a row-dependence probe) to that method's
`IManagedIndexMethodPlannerAdapter`, and receives back a `ManagedIndexMethodPatternMatch`. The FTS
adapter (`ManagedFtsPlannerAdapter`) is the only thing that knows what `fts_match` and `fts_score`
look like.

| Pattern | Recognized shape | Filters rows? |
| --- | --- | --- |
| `Score` | `… ORDER BY fts_score(cols…, ?) DESC LIMIT n` (sole ordering term) | no |
| `CombinedOrderedLimit` | score projection + matching predicate on the same query, score order, limit | yes |
| `CombinedOrdered` | score projection + matching predicate on the same query and score order | yes |
| `CombinedLimit` | score projection + matching predicate on the same query and unordered limit | yes |
| `Combined` | score projection + matching predicate on the same query | yes |
| `MatchLimit` | `… WHERE fts_match(cols…, ?) LIMIT n` with no residual/order | yes |
| `Match` | `… WHERE fts_match(cols…, ?)` | yes |
| `Knn` | `… ORDER BY <distance>(col, ?) ASC` (vector method) | no |
| `KnnLimit` | `… ORDER BY <distance>(col, ?) ASC LIMIT n` (vector method, sole ordering term) | no |

A call binds to an index only when the argument columns are exactly the index's columns as an
unordered resolved set, resolving to that source, and the query argument is safe to evaluate once for
the whole scan: it must not depend on the scanned row, and every call inside it must be a
deterministic built-in that no connection-registered callback shadows. A non-deterministic built-in
(`random()`, `changes()`, `datetime('now')`), a registered scalar callback — even one taking no
columns at all — `CURRENT_TIMESTAMP`, and any unmodelled node shape all keep the call on the
ordinary per-row scalar path.

The seven FTS shapes mirror `FTS_PATTERN_*` in pinned Turso. Unordered `Match`/`Combined` shapes use
the cursor stream and never invoke the relevance top-k collector. Their limited forms are selected
only for a plain projection with no residual predicate or ordering; a residual or `ORDER BY id`
degrades to the corresponding unlimited shape and lets the ordinary pipeline perform the cut.
Globally ranked shapes use a bounded top-k heap. This deliberately avoids Turso's formerly unsafe
"first segment hits" interpretation for unordered limits while preserving SQL results.

The chosen path is accepted only when `EstimateCost` beats a full scan (`rows` reads). The
original `WHERE` predicate is **never** marked omitted, so choosing the index can filter and rank
but can never change the answer — including on joins, subqueries, aliases and collations.

Five rules keep "the plan cannot change the answer" true:

1. **Ranking never removes rows.** A `FiltersRows = false`
   pattern (score ordering, KNN) must still produce every base row, so the executor emits the ranked
   rows first and then appends every base row the method did not rank, in **ascending rowid order** —
   the order an ordinary table scan produces, because a table b-tree cursor rewinds to its smallest
   integer key. Storage order would not do: the evaluator's row list is in insertion order until a
   reopen reloads it in b-tree order, so a tie at the `LIMIT` boundary would resolve one way on the
   method path, another way on the scan path, and a third way after a reopen. Consequently
   an unlimited ranking plan is priced at the full base-row cost and loses to the scan it would
   otherwise replace.
   A method may opt out by clearing `ManagedIndexMethodPatternMatch.RetainsUnrankedRows`, which is a
   claim that the rows it returns are a superset of the rows the statement's own `ORDER BY` and
   `LIMIT` will keep. The core honours that only when the shape really carries a pushed-down limit
   **and** `ManagedIndexMethodPlannerContext.AllowsRowTruncation` proved the whole statement is a
   plain projection: no `DISTINCT`, `GROUP BY`, `HAVING`, aggregate, window, second ordering term,
   non-literal `LIMIT`/`OFFSET`, join arm or subquery source. Anything else keeps every row, so a
   method mistake degrades to extra rows rather than to missing ones. A `NULL` query argument also
   forces retention: "the method ranked nothing" is not the claim "the statement keeps nothing".
   The decision is made *before* pricing and passed to `EstimateCost` as
   `ManagedIndexMethodCostContext.RetainsUnrankedRows`, so a method prices the plan it will actually
   be asked to run.
2. **Scalar type and error semantics are preserved.** The evaluated query argument is validated by
   the method's own `ValidateArgument` hook *before* the plan runs, so
   `WHERE fts_match(title, body, 123)` raises `fts_match() requires a text query` on the indexed path
   exactly as it does on the scan path. A `NULL` argument selects nothing on either path.
3. **A shadowed function declines the plan.** See *Binding, determinism and shadowing* above.
4. **The advertised plan is the executed plan.** A `SELECT` with a viable method-index path is not
   lowered to bytecode, because the bytecode compiler has no method cursor. Without that gate
   `EXPLAIN QUERY PLAN` would report a method scan that execution silently replaced with a sort.
5. **Duplicate coverage must be semantically unambiguous.** Differently configured indexes covering
   the same resolved set make the method plan decline. Equivalent duplicates use a lexical-name
   final tie-break rather than catalog insertion order.

### Cost model

`EstimateCost` is expressed in the same row-read unit the join cost model uses: a method scan costs
one posting walk per query term plus one base-row seek per produced row, and a full table scan costs
one read per base row.

Pending maintenance is *not* charged to the method plan, because it is not attributable to it: a
scalar `fts_score()` evaluated on the plain scan path consults the same corpus, so the index has to
be current whichever path wins. But it is not *performed* during planning either. A method reports
it as `ManagedIndexMethodCostEstimate.RefreshCost`, a named component of the total, and the planner
subtracts it to get the steady-state cost it compares against the scan. The mutation journal above
is what keeps the real reconciliation `O(rows changed since the last statement)` rather than
`O(base rows)`; a cursor that is genuinely cold (a fresh attachment, or one restored by a rollback)
reports the full rebuild it will be forced to perform rather than advertising a cheap plan and then
walking the table.

Planning is therefore **deferred**: `ManagedIndexMethodAttachment.EstimateCost` opens a probe cursor
without `OpenRead`/`OpenWrite` — the only two entry points that reconcile — and returns a
`ManagedIndexMethodCostSnapshot`. The winner is opened afterwards, through the statement-scoped
cache, and it is the only candidate that pays. Three consequences are asserted by tests rather than
assumed:

- `EXPLAIN QUERY PLAN` prices and reports a plan without rebuilding a cold index;
- a table carrying three method indexes rebuilds one of them to answer a query, not three;
- a candidate that loses the comparison is never reconciled at all.

`ManagedIndexMethodDiagnostics.StateRebuilds` counts publications of freshly derived state, which is
what makes "planning did no work" an assertion instead of an inference. Ties between two otherwise
equal candidates are broken by the total cost including the refresh, so an already-current index wins
over a cold one.

`EXPLAIN QUERY PLAN` reports the selection:

```
SEARCH docs USING INDEX METHOD fts INDEX docs_fts (pattern=Match rows~4 cost~10.6)
```

### Appended VDBE opcodes

New opcodes are **appended**; no existing opcode was renumbered (`VRollback` stays 106).

| Value | Opcode | Turso analogue |
| --- | --- | --- |
| 107 | `IndexMethodCreate` | `insn.rs:1437` |
| 108 | `IndexMethodDestroy` | `insn.rs:1442` |
| 109 | `IndexMethodOptimize` | `insn.rs:1447` |
| 110 | `IndexMethodQuery` | `insn.rs:1452` |
| 111 | `IndexMethodNext` | Ahtola-only (Turso folds advance into the query cursor) |
| 112 | `IndexMethodColumn` | Ahtola-only; column 0 is the method score |
| 113 | `IndexMethodRowId` | Ahtola-only; `IndexMethodCursor::query_rowid` |
| 114 | `IndexMethodInsert` | `IndexMethodCursor::insert` |
| 115 | `IndexMethodDelete` | `IndexMethodCursor::delete` |

Maintenance values use Turso's layout: index columns in declaration order, rowid last
(`turso-src/core/index_method/mod.rs:168-175`).

## Limits

| Limit | Value |
| --- | --- |
| `MaxIndexedColumns` | 32 (the posting column mask is a `uint`) |
| `MaxParameters` (`WITH` keys) | 32 |
| `MaxStateBytes` | 16 MiB |
| `MaxStateEncodedChars` | base64 bound checked before the decode allocates |
| `default` term limit | terms must be shorter than 40 UTF-8 bytes (Tantivy remove-long behavior) |
| `unicode61` / `ascii` extension term limit | 256 UTF-16 code units (longer terms are truncated) |
| `MaxPrefixTerms` | 4096 **live** terms per prefix wildcard (stale terms are purged before the limit is enforced) |
| `MaxQueryTerms` | 256 |
| `MaxQueryDepth` | 64 |
| `MaxPositionsPerDocument` | 1,000,000 |
| `MaxNearDistance` | 1024 |
| `MaxHighlightSpans` | 1,000,000 |
| `MaxHighlightPrefixTerms` | 64 |

There is no total-match ceiling. Unordered cursor shapes enumerate matches and ordered LIMIT shapes
retain only a bounded top-k heap. Unlimited consumers may naturally materialize their full SQL
result, but `LIMIT 1` cannot fail merely because more than one million rows match.

Merge policy constants are ported from `turso-src/core/index_method/fts.rs:73-91`:
`DeletedDocumentsCompactionThreshold = 0.30`, `MaxSynchronousCompactionDocuments = 64_000`.
Deletes are generation-stamped tombstones, invisible to readers immediately; compaction reclaims
their space, and above the synchronous bound it is deferred to `REINDEX`. Prefix expansion purges
stale terms first so the limit counts live terms only.

## MVCC

`fts` declares `ManagedIndexMethodMvccSupport.TransactionalBackingStore`. That declaration is
honest: every byte of durable state is either the ordinary index b-tree or the `sqlite_schema` row,
both of which the engine already keeps snapshot-isolated, and the derived postings live in the
catalog snapshot. `ManagedIndexMethodMvcc.Ensure` (a port of `ensure_mvcc_support`,
`turso-src/core/index_method/mod.rs:86-106`) runs at schema load, `CREATE`, `REINDEX`, and query
planning, and fails closed with `index method 'x' does not support MVCC` /
`… is read-only in MVCC` for methods that declare less.

Under the concurrent MVCC overlay the planner deliberately **falls back to an ordinary scan**: the
overlay materializes a different row set than the base snapshot the derived state was built from,
so answering from that state would be stale. The scalar `fts_match`/`fts_score` path still runs and
returns the same rows.

Lifetime: `ManagedIndexMethod` singletons are immutable and thread safe; attachments are per-catalog
and forked (never shared) by every catalog snapshot; cursors are per statement, single threaded and
disposed at statement finalize/reset. There is no cross-connection cache — Turso's `CachedFtsStates`
(`fts.rs:1489-1580`) is a Tantivy artifact and is deliberately **not** ported; the pager snapshot is
the cache.

## Divergences from Turso

1. **No Tantivy, no `HybridBTreeDirectory`, no chunk-blob file emulation** (`fts.rs:568-1400`).
   Postings are a native managed inverted index.
2. **No cross-connection read-state cache** (`fts.rs:1489-1580`).
3. **`MvccSupport = TransactionalBackingStore`**, not Turso's `Unsupported` (`fts.rs:1847`), because
   all durable state is ordinary transactional storage.
4. **Patterns are a typed enum**, not parsed `ast::Select` fragments (`mod.rs:74`), which keeps the
   AOT footprint flat and avoids a parse-at-attach step.
5. **Postings are derived, not persisted.** Turso persists a Tantivy directory; Ahtola rebuilds from
   the base rows, which makes rollback, savepoint, crash recovery and `VACUUM` correct by
   construction and makes stale state impossible.
6. **Exact score bits are not promised.** Ahtola implements positive BM25 in managed code and rounds
   public scores to `f32`, but does not reproduce Tantivy's segment internals. Ranking direction,
   boosts, phrases/prefixes and deterministic tie-breaking are the compatibility contract.
7. **CHECK-only deterministic exception.** Turso marks `fts_score` deterministic. Ahtola's registry
   does too, but rejects it in CHECK constraints because the index-aware scalar can observe the
   corpus being mutated by the checked statement.

## The vector index method

The foundation is method generic, and that claim is enforced by a test rather than asserted:
`ManagedIndexMethodGenericFoundationTests` registers a minimal non-FTS method that declares
`Knn`/`KnnLimit` patterns over a blob column, recognizes
`ORDER BY vector_distance_l2(col, ?) ASC [LIMIT n]` through its own planner adapter, and is planned
and executed end to end by the core engine.

Nothing in the core is FTS shaped:

- rows cross the method boundary as `ManagedIndexMethodResultRow` (rowid plus result columns), so
  `GetMethodIndexRows` performs no cast to an FTS attachment or hit type;
- the pushed-down argument is an arbitrary `SqlValue`, not a query string, so the vector method
  receives its blob unchanged;
- SQL pattern recognition lives entirely in each method's `IManagedIndexMethodPlannerAdapter`;
- `ManagedIndexPatternShapes` centralizes the ranking-only and limit-pushdown classification so both
  methods inherit the same correctness rules;
- the EXPLAIN detail a method contributes is an opaque string the core appends verbatim.

The shipped vector method (`USING vector`) is documented separately in
[managed-vector-index.md](managed-vector-index.md): an IVF-Flat structure with an exactness
certificate, deterministic k-means training, a persisted centroid envelope, and a cost model that
prices the rows a query actually reads rather than the pruning it hopes for.

## Source map

| Concern | File |
| --- | --- |
| Registry, factory, attachment, definition, cursor, result rows, cost, MVCC, limits | `src/Ahtola.Core/Indexing/ManagedIndexMethods.cs` |
| Planner adapter contract and pattern-shape classification | `src/Ahtola.Core/Indexing/ManagedIndexMethodPlanner.cs` |
| Base-row mutation journal for incremental maintenance | `src/Ahtola.Core/Indexing/ManagedIndexMethodJournal.cs` |
| State envelope codec and reserved names | `src/Ahtola.Core/Indexing/ManagedIndexMethodStateSql.cs` |
| Catalog validation, attachment publication, revision-aware base-row adapter | `src/Ahtola.Core/Indexing/ManagedIndexMethodSemantics.cs` |
| Tokenizers, offset-preserving folding, gram slicing | `src/Ahtola.Core/Search/ManagedFtsTokenization.cs` |
| Extended query grammar and limits | `src/Ahtola.Core/Search/ManagedFtsQueryLanguage.cs` |
| Postings, generations, BM25, compaction | `src/Ahtola.Core/Search/ManagedFtsSearchIndex.cs` |
| `fts` method, options, attachment, cursor | `src/Ahtola.Core/Search/ManagedFtsIndexMethod.cs` |
| `fts` planner adapter (`fts_match`/`fts_score` SQL matching) | `src/Ahtola.Core/Search/ManagedFtsPlannerAdapter.cs` |
| `fts_*` scalar surface | `src/Ahtola.Core/Search/ManagedFtsFunctions.cs` |
| `vector` method, options, attachment, cursor, cost model | `src/Ahtola.Core/Vectors/ManagedVectorIndexMethod.cs` |
| IVF structure, certified search, postings and radii | `src/Ahtola.Core/Vectors/ManagedVectorIvfIndex.cs` |
| Per-metric geometry and the provable list bounds | `src/Ahtola.Core/Vectors/ManagedVectorGeometry.cs` |
| Deterministic sampling and k-means training | `src/Ahtola.Core/Vectors/ManagedVectorTraining.cs` |
| Deterministic generator (`xoshiro256**`/`SplitMix64`) | `src/Ahtola.Core/Vectors/ManagedVectorRandom.cs` |
| Centroid state envelope codec | `src/Ahtola.Core/Vectors/ManagedVectorIndexState.cs` |
| `vector` planner adapter (`vector_distance_*` SQL matching) | `src/Ahtola.Core/Vectors/ManagedVectorPlannerAdapter.cs` |
| Vector decode/distance bridge to the scalar functions | `src/Ahtola.Core/SqliteVectorFunctions.Indexing.cs` |
| Generic planner dispatch, execution, EXPLAIN detail, cost comparison | `src/Ahtola.Core/EmbeddedDatabase.IndexMethods.cs` |
| FTS scalar binding (the only FTS casts outside `Search/`) | `src/Ahtola.Core/EmbeddedDatabase.FtsFunctions.cs` |
| Opcodes and instructions | `src/Ahtola.Core/Execution/VdbeProgram.cs` |
| Opcode EXPLAIN rendering | `src/Ahtola.Core/Execution/VdbeExplain.cs` |
| Opcode dispatch | `src/Ahtola.Core/Execution/ResumableStatement.cs` |

## Test map

| Area | File |
| --- | --- |
| Review-finding reproducers and negative controls | `src/Ahtola.Tests/ManagedIndexMethodReviewRegressionTests.cs` |
| Transaction, savepoint, trigger and foreign-key lifecycle | `src/Ahtola.Tests/ManagedIndexMethodTransactionTests.cs` |
| Non-FTS KNN method proving the foundation is generic | `src/Ahtola.Tests/ManagedIndexMethodGenericFoundationTests.cs` |
| Planner selection, cost comparison, EXPLAIN evidence | `src/Ahtola.Tests/ManagedIndexMethodPlannerTests.cs` |
| Durability, state envelope, catalog round-trip | `src/Ahtola.Tests/ManagedIndexMethodDurabilityTests.cs` |
| Search engine semantics | `src/Ahtola.Tests/ManagedFtsSearchEngineTests.cs` |
| SQL surface and syntax rejection | `src/Ahtola.Tests/ManagedIndexMethodSyntaxTests.cs` |
| AOT/trim safety and builtin registration | `src/Ahtola.Tests/ManagedIndexMethodAotSafetyTests.cs` |
| Vector index suites (syntax, recall, planner, determinism, maintenance, transactions, durability, bridge) | `src/Ahtola.Tests/ManagedVectorIndex*.cs` |
