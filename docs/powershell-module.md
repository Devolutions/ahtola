# PowerShell module guide

`Devolutions.Ahtola.Sqlite` exposes the pure-managed Ahtola engine as a set of
`*-AhtolaSqlite*` cmdlets, ported from synedgy.PSSqlite and re-backed onto
`Ahtola.Data.Sqlite` instead of Microsoft.Data.Sqlite / SQLitePCLRaw. This guide
goes deeper than the [top-level README](../README.md#powershell-module): full
cmdlet-by-cmdlet usage, plus end-to-end walkthroughs for a **local SQLite
file**, **local concurrent writes (MVCC / `BEGIN CONCURRENT`)**, and **Turso
Cloud** — both a direct connection and a **managed embedded replica**.

- [Requirements and installation](#requirements-and-installation)
- [Connections](#connections)
- [Querying and CRUD](#querying-and-crud)
- [Transactions](#transactions)
- [Schema and database introspection](#schema-and-database-introspection)
- [Maintenance](#maintenance)
- [Backup](#backup)
- [Bulk copy and table interchange](#bulk-copy-and-table-interchange)
- [Encryption / passwords](#encryption--passwords)
- [Metadata and version comparison](#metadata-and-version-comparison)
- [Working with a local SQLite file](#working-with-a-local-sqlite-file)
- [Concurrent writes on a local file (MVCC)](#concurrent-writes-on-a-local-file-mvcc)
- [Turso Cloud: direct connection](#turso-cloud-direct-connection)
- [Turso Cloud: managed embedded replica](#turso-cloud-managed-embedded-replica)
- [Error handling patterns](#error-handling-patterns)

## Requirements and installation

Requires PowerShell **7.4+** (.NET 8+). Windows PowerShell 5.1 is not
supported because Ahtola targets `net8.0`/`net9.0`/`net10.0` only, and no
native SQLite asset is required at any point.

The module is published on the
[PowerShell Gallery](https://www.powershellgallery.com/packages/Devolutions.Ahtola.Sqlite)
as `Devolutions.Ahtola.Sqlite`:

```powershell
Install-Module -Name Devolutions.Ahtola.Sqlite -Scope CurrentUser
Import-Module Devolutions.Ahtola.Sqlite
Get-Command -Module Devolutions.Ahtola.Sqlite
```

Use `-Scope AllUsers` (typically needs an elevated shell) to install it for
every user on the machine instead, and add `-Repository PSGallery` explicitly
if you have other repositories registered and want to be unambiguous about the
source. To update later:

```powershell
Update-Module -Name Devolutions.Ahtola.Sqlite
```

The module bundles offline command help:

```powershell
Get-Help New-AhtolaSqliteConnection -Full
Get-Help Invoke-AhtolaSqliteQuery -Examples
```

Model types are available as module-qualified type accelerators once the
module is imported, e.g. `[Devolutions.Ahtola.Sqlite.SqliteDBConfig]` for the
CRUD-row cmdlets below.

Every destructive cmdlet (writes, imports, backups, maintenance, password
changes, connection close, pool clear) implements `SupportsShouldProcess`, so
`-WhatIf` / `-Confirm` work everywhere you'd expect.

A connection you pass via `-Connection` is always **caller-owned**: a cmdlet
may open it if it's closed, but never closes or disposes it for you.
`New-AhtolaSqliteConnection` returns an already-open connection, and
`Close-AhtolaSqliteConnection` is the explicit disposal cmdlet.

## Connections

`New-AhtolaSqliteConnection` has three parameter sets:

| Parameter set | Parameters | Produces |
| --- | --- | --- |
| `byConnectionString` (default) | `-ConnectionString` (default `Data Source=:memory:;Cache=Shared;`) | `SqliteConnection` |
| `byDatabasePath` | `-DatabasePath`, `-DatabaseFile` | `SqliteConnection` |
| `byTursoCloud` | `-TursoUrl`/`-RemoteUrl`/`-Url`, `-AuthToken`/`-Token`, `-ReplicaPath`, `-UseTursoEnvironment`, `-SyncInterval` | `AhtolaCloudConnection` |

`-ReadOnly` applies to the first two sets and sets `Mode=ReadOnly` on the
connection string before opening. Relative `Data Source`, `-DatabasePath`, and
`-ReplicaPath` values resolve from the active PowerShell filesystem location.
`-DatabasePath`/`-DatabaseFile` and `-ConnectionString` support PowerShell
string expansion (`$env:...`, `$variable`) via `ExpandString`, so you can pass
`'$env:USERPROFILE\data.db'` directly. `-WhatIf` previews local and Cloud
connection creation without opening a connection or creating a database file.

```powershell
# In-memory, shared cache (default)
$mem = New-AhtolaSqliteConnection

# Local file, explicit connection string
$file = New-AhtolaSqliteConnection -ConnectionString 'Data Source=app.db'

# Local file, path + file name combined for you
$file2 = New-AhtolaSqliteConnection -DatabasePath . -DatabaseFile 'app.db'

# Read-only
$ro = New-AhtolaSqliteConnection -ConnectionString 'Data Source=app.db' -ReadOnly
```

Other connection lifecycle cmdlets:

| Cmdlet | Purpose |
| --- | --- |
| `Test-AhtolaSqliteConnection` | Opens the connection if needed and runs `SELECT 1;`; returns `$true`/throws |
| `Close-AhtolaSqliteConnection [-ClearPool] [-AllPools]` | Closes and disposes; `-ClearPool` also clears that connection's pool entry, `-AllPools` clears every managed pool. Omit `-Connection` entirely to just clear all pools. |
| `Clear-AhtolaSqliteConnectionPool` | Clears the pool for one `SqliteConnection` without closing it |

```powershell
Test-AhtolaSqliteConnection -Connection $file      # $true
$file | Close-AhtolaSqliteConnection -ClearPool
Close-AhtolaSqliteConnection                        # no -Connection: clears every pool
```

## Querying and CRUD

`Invoke-AhtolaSqliteQuery` is the general-purpose entry point:

```powershell
Invoke-AhtolaSqliteQuery -Connection $file `
    -CommandText 'SELECT id, name FROM t WHERE name = $name' `
    -Parameters @{ '$name' = 'b' }
```

| Parameter | Notes |
| --- | --- |
| `-Connection` (`-SqliteConnection`) | Mandatory, pipeline-bindable |
| `-CommandText` (`-Query`) | Mandatory SQL text |
| `-Parameters` | `IDictionary`; keys match your placeholder style (`$name`, `:name`, `@name`, or `?`) |
| `-As` | `PSCustomObject` (default) · `DataTable` · `DataSet` · `DetachedDataReader` (alias `DataReader`) · `OrderedDictionary` · `Scalar` · `NonQuery` |
| `-CastAs` | Convert the final result via `LanguagePrimitives.ConvertTo` |
| `-CommandTimeout` | Seconds (default 30); this is also the managed busy-wait timeout — see [Concurrent writes](#concurrent-writes-on-a-local-file-mvcc) |
| `-Transaction` | An open `DbTransaction` from `Start-AhtolaSqliteTransaction` |

`-As DetachedDataReader`/`DataReader` returns a **materialized snapshot**, not
a live streaming reader — it does not retain command or connection ownership,
so it's safe to keep around after the connection moves on.

```powershell
$count  = Invoke-AhtolaSqliteQuery -Connection $file -CommandText 'SELECT COUNT(*) FROM t' -As Scalar
$rows   = Invoke-AhtolaSqliteQuery -Connection $file -CommandText 'UPDATE t SET name = $n WHERE id = $id' `
              -Parameters @{ '$n' = 'updated'; '$id' = 1 } -As NonQuery
$table  = Invoke-AhtolaSqliteQuery -Connection $file -CommandText 'SELECT * FROM t' -As DataTable
```

### Configuration-driven CRUD

`Get-AhtolaSqliteRow`, `New-AhtolaSqliteRow`, `Set-AhtolaSqliteRow`, and
`Remove-AhtolaSqliteRow` build SQL from a `SQLiteDBConfig`
(`-Configuration`/`-SqliteDBConfig`) plus `-Table`/`-TableName`, instead of raw
SQL text. When `-Connection` is omitted, the cmdlet opens (and disposes) a
temporary connection from `$Configuration.ConnectionString` for that one
call — pass `-Connection` explicitly to reuse one connection across calls or
to participate in a caller-owned transaction.

```powershell
$config = [Devolutions.Ahtola.Sqlite.SQLiteDBConfig]::new('.', 'app.db')

New-AhtolaSqliteRow    -Configuration $config -Table Items -Values @{ Name = 'Widget'; Qty = 3 }
Get-AhtolaSqliteRow    -Configuration $config -Table Items -Where @{ Name = 'Widget' }
Set-AhtolaSqliteRow    -Configuration $config -Table Items -Values @{ Qty = 5 } -Where @{ Name = 'Widget' } -OnConflict UPSERT
Remove-AhtolaSqliteRow -Configuration $config -Table Items -Where @{ Name = 'Widget' }
```

`Set-AhtolaSqliteRow -OnConflict` accepts `UPDATE` (default) or `UPSERT`.
Update/delete cmdlets return the affected-row count. `-CaseSensitive` controls
column-name matching against `-Where`/`-Values` keys.

Legacy aliases `-SqliteConnection`, `-SqliteDBConfig`, `-TableName`,
`-RowData`, and `-ClauseData` remain for compatibility; new scripts should use
`-Connection`, `-Configuration`, `-Table`, `-Values`, and `-Where`.

## Transactions

```powershell
$tx = Start-AhtolaSqliteTransaction -Connection $file -IsolationLevel Serializable
Invoke-AhtolaSqliteQuery -Connection $file -Transaction $tx -CommandText 'INSERT INTO t VALUES ($v)' -Parameters @{ '$v' = 1 } -As NonQuery
Save-AhtolaSqliteTransaction -Transaction $tx -Name sp1
Invoke-AhtolaSqliteQuery -Connection $file -Transaction $tx -CommandText 'INSERT INTO t VALUES ($v)' -Parameters @{ '$v' = 2 } -As NonQuery
Undo-AhtolaSqliteTransaction -Transaction $tx -SavepointName sp1   # rolls back only the second insert
Complete-AhtolaSqliteTransaction -Transaction $tx                  # commits
```

| Cmdlet | Notes |
| --- | --- |
| `Start-AhtolaSqliteTransaction` | `-IsolationLevel` (default `Serializable`); `-Deferred` opens `BEGIN DEFERRED` and only works against local `SqliteConnection` (not `AhtolaCloudConnection`) |
| `Save-AhtolaSqliteTransaction -Name <sp>` | Creates a named `SAVEPOINT` |
| `Complete-AhtolaSqliteTransaction [-SavepointName <sp>]` | Without a savepoint name, commits; with one, `RELEASE`s that savepoint |
| `Undo-AhtolaSqliteTransaction [-SavepointName <sp>]` | Without a savepoint name, rolls back the whole transaction; with one, `ROLLBACK TO` that savepoint |

`Invoke-AhtolaSqliteBulkCopy` and `Import-AhtolaSqliteTable` (below) both
accept an optional caller-owned `-Transaction`; when supplied, a failure rolls
back only a savepoint scoped to that one bulk operation, leaving the rest of
your transaction intact. Without one, the cmdlet opens and manages its own
transaction and rolls back the whole insert on the first conflicting row.

## Schema and database introspection

```powershell
Get-AhtolaSqliteSchema -Connection $file -Collection Tables
Get-AhtolaSqliteSchema -Connection $file -Collection Columns -RestrictionValues $null, $null, 't'
Get-AhtolaSqliteTable  -Connection $file                # all tables
Get-AhtolaSqliteTable  -Connection $file -Table t       # one table's definition
Get-AhtolaSqliteIndex  -Connection $file -Table t
Get-AhtolaSqliteDatabaseInfo -Connection $file          # page size, page count, journal mode, etc.
```

`-Collection` accepts `MetaDataCollections`, `ReservedWords`, `Tables`
(default), `Columns`, `Indexes`, `IndexColumns` — these map directly onto
`DbConnection.GetSchema(...)`. All four cmdlets support
`-As DataTable|OrderedDictionary|PSCustomObject`.

## Maintenance

```powershell
Test-AhtolaSqliteIntegrity   -Connection $file
Optimize-AhtolaSqliteDatabase -Connection $file -Analyze
Checkpoint-AhtolaSqliteDatabase -Connection $file -Mode Truncate

# Or the single entry point:
Invoke-AhtolaSqliteMaintenance -Connection $file -Operation Vacuum
Invoke-AhtolaSqliteMaintenance -Connection $file -Operation Analyze
Invoke-AhtolaSqliteMaintenance -Connection $file -Operation IntegrityCheck
Invoke-AhtolaSqliteMaintenance -Connection $file -Operation Checkpoint -CheckpointMode Full
```

`-Mode`/`-CheckpointMode` accept `Passive`, `Full`, `Restart`, `Truncate`
(`PRAGMA wal_checkpoint(...)`). `Optimize-AhtolaSqliteDatabase -Analyze` runs
`VACUUM;` then `ANALYZE;` in sequence. All of these are thin, explicit wrappers
around the equivalent `PRAGMA`/DDL statement — nothing here is Ahtola-specific
beyond command shape.

## Backup

```powershell
$dest = New-AhtolaSqliteConnection -ConnectionString 'Data Source=app.backup.db'
Backup-AhtolaSqliteDatabase -SourceConnection $file -DestinationConnection $dest
```

`Backup-AhtolaSqliteDatabase` copies one managed SQLite database into a
**distinct destination connection** (source and destination must not be the
same connection instance) — it is a live page-level backup, not table export;
see [Bulk copy and table interchange](#bulk-copy-and-table-interchange) for
portable per-table data movement.

## Bulk copy and table interchange

```powershell
# Pipe PSCustomObjects, dictionaries, or DataRow values straight into a table.
$rows = 1..5000 | ForEach-Object { [pscustomobject]@{ Name = "item-$_"; Qty = $_ } }
$rows | Invoke-AhtolaSqliteBulkCopy -Connection $file -Table Items -BatchSize 1000

# Portable export/import — format is inferred from the file extension.
Export-AhtolaSqliteTable -Connection $file -Table Items -Path ./items.json
Export-AhtolaSqliteTable -Connection $file -Query 'SELECT * FROM Items WHERE Qty > $q' -Parameters @{ '$q' = 10 } -Path ./big-items.csv
Import-AhtolaSqliteTable -Connection $file -Table Items -Path ./items.csv -BatchSize 500
```

Relative import and export `-Path` values resolve from the active PowerShell
filesystem location. `Invoke-AhtolaSqliteBulkCopy` inserts all pipelined rows in one
all-or-nothing transaction (or savepoint, with a caller-owned `-Transaction`)
takes at most `-BatchSize` rows from the pipeline at a time, so large pipeline
inputs do not accumulate in memory. A failed later batch still rolls back every
row inserted by the bulk copy. `Export-AhtolaSqliteTable`
takes either `-Table` or `-Query`+`-Parameters` (mutually exclusive parameter
sets) and writes `Json` or `Csv` (`-Format` overrides extension inference).
`Import-AhtolaSqliteTable` reads the same two formats back in with the same
batching/transaction semantics as bulk copy.

## Encryption / passwords

```powershell
$secure = Read-Host -AsSecureString
Set-AhtolaSqlitePassword -Connection $file -Password $secure     # encrypt or rotate
Clear-AhtolaSqlitePassword -Connection $file                     # decrypt back to plaintext
```

These cmdlets are available only for Ahtola's own file-backed managed
AES-256-GCM format (`AHTLA` header) — not SQLCipher, SEE, or loadable
extensions. See the [README's file-encryption section](../README.md#file-encryption-not-see--sqlcipher)
for the underlying passphrase-scheme / raw-key connection-string mechanics
that these cmdlets wrap.

## Metadata and version comparison

```powershell
Get-AhtolaSqliteDatabaseMetadata -Connection $file -MetadataKey SchemaVersion, AppVersion
Compare-AhtolaSqliteDatabaseVersion -Configuration $config -ExpectedVersion '2'
```

`Get-AhtolaSqliteDatabaseMetadata` reads stored key/value metadata from the
supplied connection (`-MetadataKey` defaults to `*`, i.e. everything), so it
also sees in-memory databases and uncommitted metadata changes.
`Compare-AhtolaSqliteDatabaseVersion` compares a `SQLiteDBConfig`'s deployed
version against an expected one (defaulting to `$Configuration.Version`) —
useful as a gate before running schema-migration scripts.

## Working with a local SQLite file

Putting the pieces above together, end to end:

```powershell
Import-Module Devolutions.Ahtola.Sqlite

$connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=app.db'
Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'CREATE TABLE IF NOT EXISTS Items(Id INTEGER PRIMARY KEY, Name TEXT, Qty INTEGER)'

$tx = Start-AhtolaSqliteTransaction -Connection $connection
try {
    1..3 | ForEach-Object {
        Invoke-AhtolaSqliteQuery -Connection $connection -Transaction $tx `
            -CommandText 'INSERT INTO Items(Name, Qty) VALUES ($n, $q)' `
            -Parameters @{ '$n' = "item-$_"; '$q' = $_ } -As NonQuery
    }
    Complete-AhtolaSqliteTransaction -Transaction $tx
}
catch {
    Undo-AhtolaSqliteTransaction -Transaction $tx
    throw
}

Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'SELECT * FROM Items'
Test-AhtolaSqliteIntegrity -Connection $connection
Export-AhtolaSqliteTable -Connection $connection -Table Items -Path ./items.json

$connection | Close-AhtolaSqliteConnection -ClearPool
```

This is a fully managed, single-process, single-file SQLite database — no
native assets, and byte-compatible with files created by
System.Data.SQLite / Microsoft.Data.Sqlite / native `sqlite3` for normal
unencrypted read/write workloads.

## Concurrent writes on a local file (MVCC)

Ahtola supports a **process-local** MVCC mode, ported from Turso's
`journal_mode=mvcc` + `BEGIN CONCURRENT`. It lets multiple connections
**inside the same process** (for example, a shared connection pool) hold
overlapping open write transactions instead of serializing on the classic
single-writer lock, as long as they touch disjoint rows.

Enable it once per database file, then use `BEGIN CONCURRENT` instead of
`BEGIN`/`BEGIN IMMEDIATE`:

```powershell
$a = New-AhtolaSqliteConnection -ConnectionString 'Data Source=shared.db'
$b = New-AhtolaSqliteConnection -ConnectionString 'Data Source=shared.db'

Invoke-AhtolaSqliteQuery -Connection $a -CommandText 'PRAGMA journal_mode=mvcc' -As Scalar
Invoke-AhtolaSqliteQuery -Connection $a -CommandText 'CREATE TABLE IF NOT EXISTS t(v INTEGER)' -As NonQuery

# Two writers, two disjoint rows: both commit without contending on a lock.
Invoke-AhtolaSqliteQuery -Connection $a -CommandText 'BEGIN CONCURRENT' -As NonQuery
Invoke-AhtolaSqliteQuery -Connection $b -CommandText 'BEGIN CONCURRENT' -As NonQuery
Invoke-AhtolaSqliteQuery -Connection $a -CommandText 'INSERT INTO t VALUES (10)' -As NonQuery
Invoke-AhtolaSqliteQuery -Connection $b -CommandText 'INSERT INTO t VALUES (20)' -As NonQuery
Invoke-AhtolaSqliteQuery -Connection $a -CommandText 'COMMIT' -As NonQuery
Invoke-AhtolaSqliteQuery -Connection $b -CommandText 'COMMIT' -As NonQuery
```

Notes and limits (see [`docs/mvcc-port-contract.md`](mvcc-port-contract.md)
for the full port contract):

- **`PRAGMA journal_mode=mvcc` gates everything.** Without it, `BEGIN
  CONCURRENT` fails immediately with *"Concurrent transaction mode is only
  supported when MVCC is enabled"* — it never silently falls back to a
  classic transaction. Once one connection enables MVCC on a database path,
  peers opened against the same path observe `journal_mode` as `mvcc` too
  (one shared version store per path, keyed by canonical physical path).
- **Same-row writes still conflict.** Two `BEGIN CONCURRENT` transactions
  that write the *same* row, or a stale snapshot that tries to commit past a
  peer's commit, fail with the ordinary busy error (`database is locked`,
  SQLite busy code) at whichever statement/`COMMIT` first detects the
  conflict — catch it the same way as any other busy error (see
  [Error handling patterns](#error-handling-patterns)). This is a **must
  roll back** state: `Undo-AhtolaSqliteTransaction` before reusing the
  connection.
- **Savepoints work as expected** inside a concurrent transaction
  (`Save-AhtolaSqliteTransaction` / `Undo-AhtolaSqliteTransaction
  -SavepointName`), including rolling back version-store inserts made after
  the savepoint.
- **Main database only.** A concurrent transaction rejects writes to an
  `ATTACH`ed or temp database ("only supports mutations on the main
  database"); attach a second file if you need it, but keep writes to `main`
  while `BEGIN CONCURRENT` is open.
- **`REINDEX` is rejected** while MVCC is enabled.
- **`PRAGMA wal_checkpoint(...)`** runs a dedicated MVCC checkpoint state
  machine; it reports busy (no truncate) while concurrent transactions are
  still open, and garbage-collects the version store once they've all
  finished.
- **This is process-local, not cross-process or cross-machine.** It gives you
  concurrent writers within one process (e.g. a pooled multi-threaded
  service or a script running several jobs against the same file). It is
  unrelated to the multi-engine file-sharing support described in the
  README's *Multi-engine files* limit, and it is a different mechanism from
  the Turso Cloud replica sync described next.

## Turso Cloud: direct connection

`New-AhtolaSqliteConnection -TursoUrl <url> -AuthToken <SecureString>` opens a
connection straight against Turso Cloud (`libsql://` is normalized to
`https://`) with no local file at all — every read and write is a remote
round trip. It returns an `AhtolaCloudConnection`, a `DbConnection` whose
`ConnectionString` is always redacted (`Data Source=...;Auth Token=***`);
the bearer token is never exposed or serialized.

```powershell
$token = Read-Host -AsSecureString -Prompt 'Turso auth token'
$cloud = New-AhtolaSqliteConnection -TursoUrl 'libsql://my-db.turso.io' -AuthToken $token

Invoke-AhtolaSqliteQuery -Connection $cloud -CommandText 'SELECT 1' -As Scalar
$cloud | Close-AhtolaSqliteConnection
```

`-UseTursoEnvironment` opts into the standard Turso CLI environment variables
as **defaults**, not overrides — explicit `-TursoUrl`/`-AuthToken` still win:

```powershell
$env:TURSO_REMOTE_URL = 'libsql://my-db.turso.io'
$env:TURSO_AUTH_TOKEN = $plaintextToken   # set by your secret manager, not hardcoded

$cloud = New-AhtolaSqliteConnection -UseTursoEnvironment
```

Every cmdlet that talks to the network (`Invoke-AhtolaSqliteQuery`,
`Test-AhtolaSqliteConnection`, open/close) wraps provider failures in a
generic `InvalidOperationException` for cloud connections, specifically so a
credential never leaks into an error message or the error stream.

## Turso Cloud: managed embedded replica

Add `-ReplicaPath <file>` to get a **managed embedded replica**: a local
SQLite file that bootstraps from Turso Cloud, serves reads/writes locally
(no network round trip per statement), and pushes/pulls changes on an
explicit or interval-based sync. Local writes are captured into a durable
on-disk change journal (`<path>.ahtola-replica-journal` alongside the
database file) as soon as they commit, and are replayed to the remote on the
next sync — the replica does not need to be online to accept writes.

```powershell
$token = Read-Host -AsSecureString -Prompt 'Turso auth token'
$replica = New-AhtolaSqliteConnection `
    -TursoUrl 'libsql://my-db.turso.io' -AuthToken $token `
    -ReplicaPath ./replica.db

# Reads and writes hit the local file directly.
Invoke-AhtolaSqliteQuery -Connection $replica -CommandText 'INSERT INTO events(name) VALUES ($n)' -Parameters @{ '$n' = 'local-write' } -As NonQuery

# Explicit sync: pushes the local change journal, then pulls remote changes.
$result = Invoke-AhtolaSqliteReplicaSync -ReplicaConnection $replica
$result.Outcome                  # UpToDate | RemoteChangesApplied
$result.Statistics.LastPush
$result.Statistics.LastPull
```

`-SyncInterval <seconds>` (positive integer) starts a background
synchronization loop as soon as the connection opens, instead of (or in
addition to) calling `Invoke-AhtolaSqliteReplicaSync` yourself:

```powershell
$replica = New-AhtolaSqliteConnection `
    -UseTursoEnvironment -ReplicaPath ./replica.db -SyncInterval 30
```

### Concurrent writes with an embedded replica

Each embedded replica keeps its own local change journal, so "concurrent
writes" here means **multiple independent replicas (processes/machines)**
writing locally and periodically reconciling through the server — not
in-process MVCC:

- Writes against **the same replica connection** are ordinary local SQLite
  transactions (use the same [transaction cmdlets](#transactions), and
  `PRAGMA journal_mode=mvcc` + `BEGIN CONCURRENT` if you also want
  process-local concurrent writers against that one replica file, exactly as
  in the [local-file MVCC section](#concurrent-writes-on-a-local-file-mvcc)).
- Writes made **between two different replicas** (or a replica and the
  primary) are only reconciled when each side calls
  `Invoke-AhtolaSqliteReplicaSync` (or its `-SyncInterval` background loop).
  Synchronization never rebases or auto-merges: if the server rejects a
  pushed change because it conflicts with state committed elsewhere, the sync
  throws an `AhtolaReplicaConflictException` and **the local change journal is
  retained** so you can inspect and resolve the conflict explicitly instead of
  silently losing local writes.

```powershell
try {
    Invoke-AhtolaSqliteReplicaSync -ReplicaConnection $replica
}
catch [Ahtola.AhtolaReplicaConflictException] {
    Write-Warning "Replica push conflicted ($($_.Exception.ConflictKind)): $($_.Exception.Message)"
    # The journal is untouched: inspect local state, resolve manually, and
    # retry the sync (or discard/recreate the replica) once resolved.
}
```

Because each replica's writes stay purely local until the next sync, treat
`-SyncInterval` as a convenience for keeping data roughly fresh, not as a
correctness guarantee — call `Invoke-AhtolaSqliteReplicaSync` explicitly
around any write you need durably pushed before proceeding (e.g. before
reporting success to a caller).

## Error handling patterns

```powershell
try {
    Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'BEGIN CONCURRENT' -As NonQuery
    # ... work ...
    Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'COMMIT' -As NonQuery
}
catch {
    if ($_.Exception.Message -like '*database is locked*') {
        Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'ROLLBACK' -As NonQuery
        # retry, back off, or surface a user-facing "try again" message
    }
    else {
        throw
    }
}
```

- Local busy/lock conflicts (classic single-writer contention, or a same-row
  MVCC conflict) surface as an exception whose message contains `database is
  locked`; `-CommandTimeout` on `Invoke-AhtolaSqliteQuery` (and `PRAGMA
  busy_timeout`) control how long a statement waits before giving up.
- Turso Cloud / embedded-replica network and provider failures are wrapped as
  `InvalidOperationException` with a generic message, to keep credentials and
  provider internals out of the error stream.
- Embedded-replica sync conflicts are `Ahtola.AhtolaReplicaConflictException`
  (see above) and expose `ConflictKind` (`RowWrite`/`SchemaChange`/`Unknown`),
  `RemoteErrorCode`, and `LocalChangeSequence` for programmatic handling.

See the top-level [README's "Important limits"](../README.md#important-limits)
section for what Ahtola does *not* yet implement.
