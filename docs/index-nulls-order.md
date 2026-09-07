# `CREATE INDEX ... NULLS FIRST/LAST`

Ahtola supports Turso's `NULLS FIRST`/`NULLS LAST` extension on `CREATE INDEX`
terms and table-level `PRIMARY KEY`/`UNIQUE` constraint terms:

```sql
CREATE INDEX idx ON t (a, b DESC NULLS FIRST, c COLLATE NOCASE NULLS LAST);
CREATE TABLE keyed(a INT, b, PRIMARY KEY(a NULLS LAST), UNIQUE(b DESC NULLS FIRST));
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
omitting the clause for key comparison. Explicit schema text can still differ.

This is honored end-to-end: parsed metadata (`IndexedColumnDefinition`/
`EmbeddedIndexColumn.NullPlacement`), the persisted comparator
(`SqliteIndexComparisonTerm.NullsOrder`/`SqliteIndexRecordComparer`), schema
round-trip/reopen validation, forward/reverse
scans, uniqueness/UPSERT comparisons, and MVCC overlay merge ordering all
resolve the same effective placement.

Some reverse-scan ORDER BY elision, range-plan descriptions, and MIN/MAX index
optimizations remain tracked in the gap inventory; supported key ordering does
not imply those planner gaps are closed.

## Scope and exclusions

The clause remains rejected, matching SQLite/Turso, in:

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
extension to the on-disk schema text. A reader without this extension can fail
when it parses the schema, even if that happens after opening the file rather
than during open itself. Table constraints carrying the extension have the
same interoperability limitation.

User-supplied schema text retains explicitly written clauses, including ones
that happen to match the implicit ordering. To keep the schema compatible
with standard SQLite, omit explicit NULLS clauses from index and constraint
definitions; use ordinary ASC/DESC semantics instead.

## Locale collations on ordinary indexes

Ordinary expressions and b-tree keys also support a restricted, pure-managed
locale profile through `COLLATE`. This is separate from `USING <method>`
indexes, which still reject column-level COLLATE.

```sql
SELECT name FROM people ORDER BY name COLLATE 'en-u-kn-true';
CREATE INDEX names ON people(name COLLATE 'fr-FR' NULLS LAST);
```

The accepted base profiles are bare `en`, bare `es`, and `fr-FR`, not arbitrary
BCP-47 locales. Supported options include numeric ordering (`kn`), case-first
ordering (`kf`), and traditional Spanish `es-u-co-trad`. Other languages,
unverified region/variant forms, private-use extensions, Unicode attributes,
and unknown keywords fail explicitly instead of falling back to approximate
root ordering.

The character repertoire is also restricted: ASCII letters/digits, the defined
ASCII punctuation/whitespace weights, and the implemented Latin accented
letters and their corresponding decomposed forms. Unsupported characters or
combining sequences throw rather than silently installing an incompatible
index order. This is not general ICU/CLDR coverage. The `ks` keyword is
validated but does not change comparison strength in this pinned-binding
profile; it must not be used to request accent-insensitive comparison.

Application-registered collations take precedence over these profiles. The
process-wide comparator cache uses at most 24 canonical semantic identities,
not arbitrary caller-supplied tag strings. A foreign reader needs a matching
collation implementation for operations using these keys; accepting a locale
name alone does not establish compatible persisted ordering.
