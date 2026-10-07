# Migrating from Microsoft.Data.Sqlite or System.Data.SQLite

`Devolutions.Ahtola.Data.Sqlite` exposes a `Microsoft.Data.Sqlite`-shaped API
(`Ahtola.Data.Sqlite.SqliteConnection`, `SqliteCommand`, …) over the managed
engine, and emulates parts of System.Data.SQLite so applications that persisted
System.Data.SQLite connection strings and files keep working. This page lists what
behaves differently from each provider and from native SQLite, and the switches
that restore the behaviour you depend on.

## From Microsoft.Data.Sqlite

| Area | Microsoft.Data.Sqlite | Ahtola facade | How to match |
| --- | --- | --- | --- |
| `Guid` parameters | Bound as uppercase `TEXT` | Bound as a 16-byte `BLOB` (.NET byte layout) because `BinaryGUID` defaults to `true`, as in System.Data.SQLite | `BinaryGUID=False` binds GUIDs as uppercase `TEXT`, exactly like Microsoft.Data.Sqlite, and reads 16-byte blobs back in RFC 4122 order |
| `DbType` on parameters | Ignored for binding; the value picks the storage class | Picks the storage class when the value can be stored that way. `DbType.Guid` accepts a `Guid` or a GUID string and follows `BinaryGUID` | — |
| `GetValue` on `GUID`/`UNIQUEIDENTIFIER` columns | Returns the stored value | Returns a `Guid` when the value parses as one, and the stored value otherwise (legacy rows, text another client wrote). `GetGuid` still throws for non-GUID content | — |
| `GetFieldType` of `BLOB` columns | `byte[]` | `object`, so a `DataAdapter`/`DataTable` keeps text that SQLite's dynamic typing stored in a `BLOB` column | `Type Mapping=SystemDataSQLite` reports `byte[]` |
| `*ID` columns holding 16-byte blobs | Returned as `byte[]` | Returned as `byte[]` | `Guid Column Name Heuristic=True` turns them into uppercase GUID strings when `BinaryGUID` is on (the behaviour of earlier Ahtola versions) |
| `Local Provider` | n/a | `Managed` (the only shipped engine); `SqliteConnectionStringBuilder.LocalProvider` reports it | — |
| Wrong encryption key | n/a | `SqliteException` with `SqliteErrorCode` 26 (`SQLITE_NOTADB`); the message starts with `file is encrypted or is not a database` and the engine's diagnosis is the `InnerException` | — |
| Lock contention | `SqliteException` 5 | `SqliteException` 5 (`SQLITE_BUSY`), for engine and storage-level lock conflicts alike; the storage diagnosis is the `InnerException` | — |

## From System.Data.SQLite

### Connection-string keywords

The builder accepts System.Data.SQLite's keywords and translates them:

| Keyword | Effect |
| --- | --- |
| `Journal Mode` | Applied as `PRAGMA journal_mode` on every open. A database the open creates is created directly in that mode, so `Journal Mode=Delete` never leaves `-wal`/`-shm` files behind |
| `Synchronous` | `PRAGMA synchronous` on open |
| `Read Only=True` | `Mode=ReadOnly` (when `Mode` is not set) |
| `FailIfMissing=True` | `Mode=ReadWrite`: opening a missing file fails with `SQLITE_CANTOPEN` (when `Mode` is not set) |
| `Page Size`, `Cache Size` | `PRAGMA page_size` / `PRAGMA cache_size` on open |
| `BusyTimeout` (`Busy Timeout`) | `PRAGMA busy_timeout` (milliseconds) on open |
| `Password` | Opens or creates the file with System.Data.SQLite's RC4 page format; existing wxSQLite3/sqlite3secure AES-128 files are detected and opened too (see [Passwords](#passwords-and-legacy-encrypted-files)) |
| `Legacy Format`, `Version` | Accepted and ignored |
| `Ignore Unknown Keywords=True` | Ignores keywords the provider does not know instead of throwing `ArgumentException`, wherever it appears in the string; the ignored pairs are listed in `SqliteConnectionStringBuilder.IgnoredKeywords` |
| `Auto Enlist Transaction=True` | Commands without a `Transaction` run inside the connection's pending transaction (System.Data.SQLite's behaviour) instead of failing with `InvalidOperationException` |
| `Type Mapping=SystemDataSQLite` | Declared types drive the CLR type, as in System.Data.SQLite (below) |

### Typed reads

With `Type Mapping=SystemDataSQLite`, `GetValue`, `GetFieldType`,
`GetSchemaTable().DataType` (used by `DataTable.Load` and `DbDataAdapter.Fill`) and
`ExecuteScalar` follow System.Data.SQLite's declared-type table:

| Declared type (length suffix ignored) | CLR type |
| --- | --- |
| `INTEGER`, `BIGINT`, `INT64`, `LONG`, `COUNTER`, `IDENTITY` | `long` |
| `INT`, `INT32`, `MEDIUMINT` | `int` |
| `SMALLINT`, `INT16` | `short` |
| `TINYINT` | `byte` |
| `BIT`, `BOOL`, `BOOLEAN`, `LOGICAL`, `YESNO` | `bool` |
| `DATE`, `DATETIME`, `DATETIME2`, `SMALLDATE`, `TIME`, `TIMESTAMP` | `DateTime` |
| `MONEY`, `CURRENCY`, `DECIMAL`, `NUMERIC`, `NUMBER` | `decimal` |
| `REAL`, `FLOAT`, `DOUBLE` | `double`; `SINGLE` is `float` |
| `GUID`, `UNIQUEIDENTIFIER` | `Guid` |
| `TEXT`, `VARCHAR`, `NVARCHAR`, `CHAR`, `CLOB`, `MEMO`, … | `string` |
| `BLOB`, `BINARY`, `VARBINARY`, `IMAGE`, `RAW`, … | `byte[]` |

A value stored with another storage class than its column declares (SQLite's
dynamic typing) is returned as stored.

### Files created with `CreateFile()`

`SQLiteConnection.CreateFile` makes a zero-length file. Ahtola opens an existing
empty file as a new database, as native SQLite does, unless a rollback journal or
WAL sits next to it (an interrupted rewrite whose content must be recovered).

### Passwords and legacy encrypted files

`Ahtola.Data.Sqlite.Codecs` ships `IPageCodec` implementations of the formats
native encryption extensions wrote, validated against files those engines produced:

| Codec | Format |
| --- | --- |
| `SystemDataSQLiteRc4PageCodec(password)` | System.Data.SQLite `Password`/`ChangePassword` (CryptoAPI RC4, key `SHA1(UTF-8 password)[0..16]`, whole pages, header included) |
| `WxSQLite3Aes128PageCodec(password)` | wxSQLite3 4.x / sqlite3secure `aes128cbc` (MD5/RC4 key derivation, per-page AES-128-CBC; page 1 keeps bytes 16–23 readable; legacy whole-page mode is read too) |

Use them through the `Password` keyword, `SqliteConnection.PageCodec`, or
`SqliteConnection.PageCodecCandidates` (several codecs; the first whose
`IPageCodec.MatchesHeader` accepts the file is used, `SelectedPageCodec` reports
it). `LegacyPageCodecs.Detect`/`DetectFile` identify a file's format for a
password. These formats are unauthenticated and use RC4/MD5: keep them for opening
existing files, and prefer the built-in authenticated encryption
(`Encryption Cipher`/`Encryption Key`) for new data.

`SqliteConnection.ChangePageCodec(newCodec)` replaces System.Data.SQLite's
`ChangePassword`: it re-encodes every page with the new codec (`null` writes a plain
file), keeps the journal mode, and swaps the file atomically, so a crash leaves the
old or the new database intact. Every other connection to the file must be closed;
the method fails with `SQLITE_BUSY` otherwise.

Writing an `IPageCodec`:

- `CodecId` names the format (algorithm, key derivation, layout), not the key. It is
  only checked to be non-zero and is never persisted; connections that set a codec
  are not pooled.
- The connection does not own the codec: dispose disposable codecs after every
  connection that uses them is closed.
- `EncodePage`/`DecodePage` may run on different threads and concurrently when a
  codec instance is shared, so they must be thread-safe.
- Throw `PageCodecKeyMismatchException` when the input proves the key is wrong;
  providers report it as `SQLITE_NOTADB`. Implement `MatchesHeader` so candidates can
  be told apart and a wrong key is reported before any page is read.

## Behaviours that differ from native SQLite

- **Eager open.** `Open()` validates the file (header, encryption key or codec) and
  fails there; native SQLite and System.Data.SQLite open lazily and fail on the first
  statement. Code that probed a file by opening it with a key (for example a
  `DatabaseExists` helper) must treat an `Open()` failure as "exists but unreadable",
  not as "missing".
- **New databases default to WAL.** Native SQLite, Microsoft.Data.Sqlite and
  System.Data.SQLite create rollback-journal databases. Set `Journal Mode=Delete` to
  create them that way (recommended on network shares, where WAL is unsafe).
- **WAL sidecars.** Closing the last connection to a file (including
  `ClearPool`/`ClearAllPools` for pooled connections) checkpoints the WAL and, when
  no other connection in any process has the database open, deletes the empty
  `-wal` and the `-shm`, as native SQLite does.
- **Locking.** `BEGIN IMMEDIATE` (`BeginTransaction()` / `BeginTransaction(deferred: false)`)
  and a deferred transaction's first write take the write lock (`RESERVED`, or the
  WAL write lock) and hold it until the transaction ends, so other connections and
  processes cannot write meanwhile. Lock waits honour `Default Timeout` and
  `PRAGMA busy_timeout` across processes. A `COMMIT` that fails with
  `SQLITE_BUSY` keeps the transaction and its lock, and can be retried. In WAL mode,
  opening a database never waits for another process's writer.
- **Writes with an open reader.** `INSERT`, `UPDATE`, `DELETE` and
  `INSERT … RETURNING` run on a connection that still has a data reader open, as
  in SQLite. Schema changes, `VACUUM` and `ATTACH`/`DETACH` still wait for the
  connection's readers.
- **`locking_mode=EXCLUSIVE`** is taken lazily: the pragma succeeds and the next
  statement reports `SQLITE_BUSY` while another connection holds the database.
- **Pragmas.** `wal_autocheckpoint` drives the checkpoint threshold (default 1000
  frames; `0` disables the automatic checkpoint, closing still checkpoints).
  `journal_size_limit` is recorded and reported only: the managed engine always
  truncates a reset WAL to its header and deletes the rollback journal after each
  commit. `PRAGMA auto_vacuum = FULL|INCREMENTAL` on a database that already has
  tables is accepted without effect (SQLite applies it at the next `VACUUM`;
  Ahtola never enables auto-vacuum). `cache_size` is accepted and not reported.
- **Read-only WAL databases without `-shm`** (after a crash, or a copied WAL
  workspace) open read-only through a process-local WAL index instead of failing.
