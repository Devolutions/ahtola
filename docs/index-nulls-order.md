# `CREATE INDEX ... NULLS FIRST/LAST`

Ahtola supports Turso's `NULLS FIRST`/`NULLS LAST` extension on `CREATE INDEX`
indexed-column terms:

```sql
CREATE INDEX idx ON t (a, b DESC NULLS FIRST, c COLLATE NOCASE NULLS LAST);
```

This mirrors `turso-src/sqlite/parser/src/ast.rs` (`NullsOrder`),
`turso-src/core/schema.rs:5744` (`IndexColumn.nulls_order` /
`effective_nulls_order`), and `turso-src/core/types.rs` (`cmp_with_sort`).

## Semantics

Each indexed-column term may carry an explicit NULL placement, independent of
its `ASC`/`DESC` value direction:

- No clause (the default): NULL placement follows SQLite's implicit rule —
  NULLS FIRST for an ascending term, NULLS LAST for a descending term.
- `NULLS FIRST`: NULLs always sort before every other value for this term,
  regardless of `ASC`/`DESC`.
- `NULLS LAST`: NULLs always sort after every other value for this term,
  regardless of `ASC`/`DESC`.

An explicit clause changes physical key ordering (and therefore the on-disk
byte layout of the index b-tree) whenever it disagrees with the implicit
default for that term's direction — e.g. `a ASC NULLS LAST` or
`a DESC NULLS FIRST`. `ASC NULLS FIRST` / `DESC NULLS LAST` are equivalent to
omitting the clause and produce byte-identical output to a plain index.

This is honored end-to-end: parsed metadata (`IndexedColumnDefinition`/
`EmbeddedIndexColumn.NullPlacement`), the persisted comparator
(`SqliteIndexComparisonTerm.NullsOrder`/`SqliteIndexRecordComparer`), schema
round-trip/reopen validation, `ORDER BY` index-satisfaction, forward/reverse
scans, uniqueness/UPSERT comparisons, and MVCC overlay merge ordering all
resolve the same effective placement.

## Scope and exclusions

Only `CREATE INDEX` indexed-column terms accept this clause. It stays
rejected, matching SQLite/Turso, in:

- Table constraints (`PRIMARY KEY (...)`, `UNIQUE (...)`, column-level
  `PRIMARY KEY`) — `"NULLS FIRST/LAST is not supported in table constraints."`
- `INSERT ... ON CONFLICT (...)` conflict targets — SQLite rejects an explicit
  NULLS clause there (`sqlite3HasExplicitNulls`; Turso's
  `reject_explicit_nulls`, `turso-src/core/translate/upsert.rs`).
- `CREATE INDEX ... USING <method> (...)` index-method columns — a method
  index is not an ordinary b-tree; see `docs/managed-index-methods.md`.

`ORDER BY ... NULLS FIRST/LAST` (a separate, pre-existing SQLite feature) was
already supported and is unaffected.

## SQLite interoperability limitation

Standard SQLite's `CREATE INDEX` grammar has **no** `NULLS FIRST`/`NULLS LAST`
clause (unlike its `ORDER BY` grammar, which has supported it since 3.30.0).
An index created with an explicit NULLS clause is therefore a Turso/Ahtola
extension to the on-disk schema text: a real `sqlite3`-based reader (or any
engine without this extension) parses the whole `sqlite_master`/schema table
eagerly at open time and will fail to parse that one `CREATE INDEX` statement,
making the database unusable with that reader. This is a hard, documented
fail-closed boundary, not a silent-corruption risk: the foreign reader gets a
schema parse error up front rather than reading the index with the wrong
implicit comparator.

To keep ordinary indexes fully interoperable, Ahtola never emits an explicit
`NULLS FIRST`/`NULLS LAST` clause for a term whose placement already matches
the implicit ASC/DESC default — the clause appears in persisted/round-tripped
SQL text only when the user actually wrote a placement that diverges from
that default.
