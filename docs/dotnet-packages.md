# NuGet packages guide

Ahtola ships as three NuGet packages that build directly on top of each
other. This guide goes deeper than the [top-level README](../README.md#install):
which package to install for a given scenario, the full ADO.NET / EF Core API
surface, connection-string keywords, and end-to-end walkthroughs for a
**local SQLite file**, **local concurrent writes (MVCC / `BEGIN CONCURRENT`)**,
and **Turso Cloud** — both a direct connection and a **managed embedded
replica**.

- [Packages and installation](#packages-and-installation)
- [Which package do I need?](#which-package-do-i-need)
- [The SQLite-compatible facade](#the-sqlite-compatible-facade)
- [Native Ahtola types](#native-ahtola-types)
- [Connection string reference](#connection-string-reference)
- [Working with a local SQLite file](#working-with-a-local-sqlite-file)
- [Concurrent writes on a local file (MVCC)](#concurrent-writes-on-a-local-file-mvcc)
- [Encryption](#encryption)
- [Entity Framework Core](#entity-framework-core)
- [Turso Cloud: direct connection](#turso-cloud-direct-connection)
- [Turso Cloud: managed embedded replica](#turso-cloud-managed-embedded-replica)
- [Error handling patterns](#error-handling-patterns)

## Packages and installation

| Package | NuGet | Role |
| --- | --- | --- |
| `Devolutions.Ahtola.Core` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.Core) | Pure-managed engine (pager, b-tree, WAL, VDBE). Rarely referenced directly — it flows in transitively — unless you need engine-level types such as `IPageCodec`. |
| `Devolutions.Ahtola.Data.Sqlite` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.Data.Sqlite) | ADO.NET provider: the `Microsoft.Data.Sqlite`-compatible facade (`Ahtola.Data.Sqlite.SqliteConnection`, …) plus the native `Ahtola.AhtolaConnection` types (local files, MVCC, Turso Cloud direct/replica). Embeds `Ahtola.Data`. |
| `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.EntityFrameworkCore.Sqlite) | EF Core provider (`UseAhtola`) — local databases, direct remote Turso Cloud/Hrana connections, and embedded replicas (see [Entity Framework Core](#entity-framework-core)). |

```bash
# ADO.NET only
dotnet add package Devolutions.Ahtola.Data.Sqlite

# + EF Core (9.x on net8.0/net9.0, 10.x on net10.0)
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite
```

Targets `net8.0`, `net9.0`, `net10.0` — no `net48` / .NET Framework assets, no
native SQLite binary, and no P/Invoke SDK to restore. All three packages are
`IsAotCompatible`/`IsTrimmable` in `Ahtola.Core`, and the shipped
provider/EF Core packages publish and trim cleanly on every supported TFM.

Adding `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` automatically brings in
`Devolutions.Ahtola.Data.Sqlite`, which in turn brings in
`Devolutions.Ahtola.Core` — you rarely need to `dotnet add package
Devolutions.Ahtola.Core` yourself. Add it directly only if you're writing an
`IPageCodec` implementation or otherwise coding straight against
`Ahtola.Core.Storage` types.

## Which package do I need?

- **Drop-in replacement for `Microsoft.Data.Sqlite`** → `Devolutions.Ahtola.Data.Sqlite`,
  `using Ahtola.Data.Sqlite;` — see [The SQLite-compatible facade](#the-sqlite-compatible-facade).
- **Native Ahtola API, or you need Turso Cloud (direct connection or embedded
  replica)** → same package, `using Ahtola;` — see
  [Native Ahtola types](#native-ahtola-types) and the Turso Cloud sections below.
- **EF Core** → also add `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` and call
  `optionsBuilder.UseAhtola(...)` — local files, direct remote Turso Cloud/Hrana
  URLs, and embedded replicas are all supported (see
  [Entity Framework Core](#entity-framework-core)).

## The SQLite-compatible facade

`Ahtola.Data.Sqlite` mirrors `Microsoft.Data.Sqlite`'s public shape closely
enough that most code only needs a `using` swap:

```csharp
using Ahtola.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=app.db");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a INTEGER, b TEXT)");
connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'hello')");

using var command = connection.CreateCommand();
command.CommandText = "SELECT a, b FROM t";
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
```

`ExecuteNonQuery(string)`, `ExecuteReader(string)`, and `ExecuteScalar(string)`
/ `ExecuteScalar<T>(string)` are convenience extension methods on
`SqliteConnection` for one-shot statements without allocating a `SqliteCommand`
yourself. `SqliteFactory.Instance` is the `DbProviderFactory` if you need
provider-agnostic construction (e.g. `DbProviderFactories.RegisterFactory`).

The same facade also opens direct Turso/Hrana URLs and managed embedded
replicas, so code written against `Microsoft.Data.Sqlite` can usually keep its
`SqliteConnection`/`SqliteCommand`/`SqliteBatch` shapes:

```csharp
using var cloud = new SqliteConnection(
    "Data Source=turso://my-db.turso.io;Auth Token=" + authToken);

using var replica = new SqliteConnection(
    "Data Source=turso://my-db.turso.io;Auth Token=" + authToken
    + ";Replica Path=./replica.db");
```

Local-only APIs (user-defined functions/aggregates, custom collations, hooks,
extensions, backup, and incremental blobs) fail explicitly when the selected
remote mode cannot provide them. Inspect `connection.Capabilities` before using
an optional surface.

### Standard SQLite files

Managed open of **unencrypted** SQLite databases created by System.Data.SQLite
/ Microsoft.Data.Sqlite / native `sqlite3` is supported (`Data Source=path`
only; no special flags). Ahtola is byte-compatible with the on-disk format for
normal read/write workloads.

## Native Ahtola types

`using Ahtola;` exposes `AhtolaConnection`, `AhtolaCommand`, `AhtolaParameter`,
`AhtolaTransaction`, `AhtolaBatch`/`AhtolaBatchCommand`, and
`AhtolaFactory.Instance`. This is the same package
(`Devolutions.Ahtola.Data.Sqlite`) as the facade above — pick whichever set of
types fits your code, or mix them (both wrap the same engine and honor the
same connection string):

```csharp
using Ahtola;

using var connection = new AhtolaConnection("Data Source=:memory:");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a, b)");
```

Use the native types when you prefer the Ahtola-specific API and exception
types. Both facades support direct remote connections, managed embedded
replicas, batches, `Sync`/`SyncAsync`, and `Capabilities` (e.g.
`CanCreateBatch`, `SupportsSync`) for feature-testing a connection before use.

## Connection string reference

Common keywords accepted by both `Ahtola.Data.Sqlite.SqliteConnectionStringBuilder`
and `Ahtola.AhtolaConnectionStringBuilder`:

| Keyword | Notes |
| --- | --- |
| `Data Source` (`Filename`) | File path, `:memory:`, or a Turso/Hrana URL (`turso://…`, `libsql://…`, `https://…`) |
| `Mode` | `ReadWriteCreate` (default), `ReadWrite`, `ReadOnly`, `Memory` |
| `Cache` | `Private` (default) / `Shared` |
| `Pooling` | Connection pooling (default `true`) |
| `Foreign Keys` | `PRAGMA foreign_keys` |
| `Default Timeout` / `Command Timeout` | Busy timeout in seconds |
| `Vfs` | Named VFS registration |
| `Password` / `Password Scheme` | Passphrase-based encryption (see [Encryption](#encryption)) |
| `Encryption Cipher` / `Encryption Key` | Raw-key encryption (hex AES-128/256-GCM) |
| `Local Provider` | `Managed` (default) or `Native` |
| `Foreign Read Only` | Read another engine's open database without taking main-file locks (`Mode=ReadOnly` + `Pooling=False`) |
| `DateTimeKind`, `BinaryGUID` | Facade-only ADO.NET conversion behavior |

Remote keywords accepted by both facades (see the Turso Cloud sections below):
`Auth Token`, `Replica Path`, `Sync Interval`, `Read Your Writes`, `Tls`.

## Working with a local SQLite file

```csharp
using Ahtola.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=app.db");
connection.Open();
connection.ExecuteNonQuery(
    "CREATE TABLE IF NOT EXISTS Items(Id INTEGER PRIMARY KEY, Name TEXT, Qty INTEGER)");

using (var transaction = connection.BeginTransaction())
{
    using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = "INSERT INTO Items(Name, Qty) VALUES ($name, $qty)";
    var name = insert.CreateParameter();
    name.ParameterName = "$name";
    insert.Parameters.Add(name);
    var qty = insert.CreateParameter();
    qty.ParameterName = "$qty";
    insert.Parameters.Add(qty);

    foreach (var (itemName, itemQty) in new[] { ("Widget", 3), ("Gadget", 7) })
    {
        name.Value = itemName;
        qty.Value = itemQty;
        insert.ExecuteNonQuery();
    }

    transaction.Commit();
}

using var reader = connection.ExecuteReader("SELECT Id, Name, Qty FROM Items");
while (reader.Read())
    Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetString(1)} x{reader.GetInt32(2)}");
```

Everything here works identically with `Ahtola.AhtolaConnection` /
`AhtolaCommand` / `AhtolaTransaction` if you prefer the native types.

## Concurrent writes on a local file (MVCC)

`PRAGMA journal_mode=mvcc` plus `BEGIN CONCURRENT` lets multiple **in-process**
connections write to disjoint rows of the same local file without contending
on the classic single-writer lock:

```csharp
using Ahtola.Data.Sqlite;

using var a = new SqliteConnection("Data Source=app.db");
using var b = new SqliteConnection("Data Source=app.db");
a.Open();
b.Open();

a.ExecuteNonQuery("PRAGMA journal_mode=mvcc");
a.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS t(v INTEGER)");

// Two writers, two disjoint rows: both commit without contending on a lock.
a.ExecuteNonQuery("BEGIN CONCURRENT");
b.ExecuteNonQuery("BEGIN CONCURRENT");
a.ExecuteNonQuery("INSERT INTO t VALUES (10)");
b.ExecuteNonQuery("INSERT INTO t VALUES (20)");
a.ExecuteNonQuery("COMMIT");
b.ExecuteNonQuery("COMMIT");
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
  peer's commit, fail with the ordinary busy exception (message contains
  `database is locked`) at whichever statement/`COMMIT` first detects the
  conflict — catch it the same way as any other busy error (see
  [Error handling patterns](#error-handling-patterns)). This is a **must
  roll back** state: execute `ROLLBACK` (or call `transaction.Rollback()` if
  you used `BeginTransaction`/`BeginTransaction(deferred: true)` instead of
  raw `BEGIN CONCURRENT` text) before reusing the connection.
- **Savepoints work as expected** inside a concurrent transaction
  (`DbTransaction.Save`/`Release`/`Rollback(savepointName)`, or raw
  `SAVEPOINT`/`RELEASE`/`ROLLBACK TO` text), including rolling back
  version-store inserts made after the savepoint.
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
  concurrent writers within one process (e.g. a pooled multi-threaded service
  or a background job set). It is unrelated to the multi-engine file-sharing
  support described in the README's *Multi-engine files* limit, and it is a
  different mechanism from the Turso Cloud replica sync described next.

## Encryption

Encryption is layered so new recipes can be added without rewriting the pager:

| Layer | Role | Extension point |
| --- | --- | --- |
| **Passphrase scheme** | Password to AES key | `IAhtolaPassphraseScheme` + `AhtolaPassphraseSchemes`; CS `Password Scheme=` |
| **Built-in AHTLA page crypto** | On-disk AES-GCM pages (`AHTLA` header) | `AhtolaEncryptionOptions` / `Encryption Cipher` + `Encryption Key` |
| **External page codec** | Entirely different page layout | `IPageCodec` (mutually exclusive with built-in encryption; see [`samples/PageCodecExamples`](../samples/PageCodecExamples)) |

```csharp
using Ahtola.Data.Sqlite;

var passphrase = GetPassphraseFromSecretManager();
using var connection = new SqliteConnection(
    $"Data Source=app.db;Password Scheme=Ahtola.Password.v1;Password={passphrase}");
connection.Open();

// Rekey later without recreating the connection string:
connection.ChangePassword(newPassphrase);
// ...or decrypt in place, moving the file to plaintext:
connection.ClearPassword();
// ...or encrypt a currently-plaintext, already-open connection:
connection.SetPassword(newPassphrase);
```

Built-in scheme `Ahtola.Password.v1`: PBKDF2-HMAC-SHA256, fixed domain salt
`Ahtola.Password.v1`, 210k iterations to AES-256-GCM. Raw-key encryption
(`Encryption Cipher=Aes256Gcm; Encryption Key=<64 hex chars>`) uses the same
on-disk AHTLA format without a passphrase KDF. Do **not** combine `Password`
and `Encryption Key`. Legacy SEE/SQLCipher files are **not** opened by
passphrase schemes — use a dedicated `IPageCodec` or export/recreate under
Ahtola password or plain SQLite. Wrong/missing password failures include the
phrase `file is encrypted or is not a database`.

## Entity Framework Core

```csharp
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseAhtola("Data Source=app.db")
    .Options;

using var context = new AppDbContext(options);
context.Database.EnsureCreated(); // or context.Database.Migrate();
```

`UseAhtola` overloads accept a connection string, an already-constructed
`Ahtola.Data.Sqlite.SqliteConnection` (optionally with `contextOwnsConnection`),
and an optional `Action<SqliteDbContextOptionsBuilder>` — the same shape as
`UseSqlite`, since the provider is layered directly on top of
`Microsoft.EntityFrameworkCore.Sqlite.Core` (9.x on net8.0/net9.0, 10.x on
net10.0; the pinned range is enforced at load time and throws
`NotSupportedException` on a mismatched EF Core version).

**`UseAhtola` supports local files, direct remote Turso Cloud/Hrana
connections, and embedded replicas** — it classifies the `Data Source` the
same way the ADO.NET facades do and wires up the matching services:

```csharp
// Local file or :memory: (native or Local Provider=Managed)
.UseAhtola("Data Source=app.db")

// Direct remote Turso Cloud/Hrana — every read and write is a round trip
.UseAhtola("Data Source=turso://my-db.turso.io;Auth Token=" + authToken)

// Embedded replica — queries run against a local synced copy
.UseAhtola("Data Source=turso://my-db.turso.io;Auth Token=" + authToken + ";Replica Path=replica.db")
```

Migrations, `EnsureCreated`/`EnsureDeleted`, querying, `SaveChanges`
(including `INSERT ... RETURNING`), and explicit transactions all work over a
direct remote or embedded-replica connection. A direct remote connection
cannot enforce `Mode=ReadOnly` (there is no local file to guard) and its
`EnsureDeleted`/`Delete` is always a no-op — the remote database is
provisioned and owned independently (e.g. via the Turso CLI/API), so
`UseAhtola` never attempts to drop it.

Because a remote/replica connection cannot register client-side SQL
functions or collations, a few LINQ constructs that translate to those on
local connections are rejected with a `NotSupportedException` **at query
translation time** (before any request reaches the server) instead of
failing late with an opaque "no such function" error:

- `Regex.IsMatch(...)` (translates to a client-registered `regexp` function).
- Decimal arithmetic (`+ - * / %`, unary `-`) and decimal `Sum`/`Average`/`Max`/`Min`.
- `OrderBy`/`OrderByDescending` on a `decimal` value (needs the `EF_DECIMAL` collation).

Everything else — standard SQL, JSON1 (including primitive collections),
equality/inequality on decimals, and simply storing/reading decimal values —
keeps working normally. No automatic `EnableRetryOnFailure`-style execution
strategy is registered for remote connections: a transient remote failure
(e.g. `SQLITE_BUSY`) propagates to the caller as-is rather than being
silently retried, since safely retrying inside a user-managed transaction is
not implemented yet. Use `SqliteRemoteExceptionClassifier.IsTransient(...)`
if you want to detect and retry transient failures yourself.

## Turso Cloud: direct connection

`AhtolaConnection` and `Ahtola.Data.Sqlite.SqliteConnection` open a connection
straight against Turso Cloud (`turso://` / `libsql://` normalize to HTTPS) with
no local file at all — every read and write is a remote round trip.

```csharp
using Ahtola;

using var cloud = new AhtolaConnection(
    "Data Source=libsql://my-db.turso.io;Auth Token=" + authToken);
cloud.Open();

using var command = cloud.CreateCommand();
command.CommandText = "SELECT 1";
var result = command.ExecuteScalar();

cloud.Close();
```

The SQLite-compatible facade maps remote failures to `SqliteRemoteException`;
use `SqliteRemoteExceptionClassifier.IsTransient(...)` for explicit retry
policy. The native facade exposes `AhtolaException` /
`AhtolaRemoteSqlException`. Connection strings redact the bearer token
(`Data Source=...;Auth Token=***`) — it is never exposed in diagnostics. Never
hardcode the token; read it from a secret manager or environment variable.

Expired Hrana streams are retried once automatically only for stateless
commands. An active transaction is never replayed: it becomes unusable and
commit/rollback preserves the original stream failure instead of masking it
with a local "database is closed" error.

## Turso Cloud: managed embedded replica

Add `Replica Path=<file>` to get a **managed embedded replica**: a local
SQLite file that bootstraps from Turso Cloud, serves reads/writes locally (no
network round trip per statement), and pushes/pulls changes on an explicit or
interval-based sync. Local writes are captured into a durable on-disk change
journal (`<path>.ahtola-replica-journal` alongside the database file) as soon
as they commit, and are replayed to the remote on the next sync — the replica
does not need to be online to accept writes.

```csharp
using Ahtola;

using var replica = new AhtolaConnection(
    "Data Source=libsql://my-db.turso.io;Auth Token=" + authToken + ";Replica Path=./replica.db");
replica.Open();

// Reads and writes hit the local file directly.
replica.ExecuteNonQuery("INSERT INTO events(name) VALUES ('local-write')");

// Explicit sync: pushes the local change journal, then pulls remote changes.
AhtolaSyncResult result = replica.Sync(new AhtolaSyncOptions());
Console.WriteLine(result.Outcome);              // UpToDate | RemoteChangesApplied
Console.WriteLine(result.Statistics.LastPush);
Console.WriteLine(result.Statistics.LastPull);

// Async overloads are also available:
await replica.SyncAsync(cancellationToken);
```

`Sync Interval=<seconds>` (positive integer, connection-string only) starts a
background synchronization loop as soon as the connection opens, instead of
(or in addition to) calling `Sync`/`SyncAsync` yourself:

```csharp
using var replica = new AhtolaConnection(
    "Data Source=libsql://my-db.turso.io;Auth Token=" + authToken +
    ";Replica Path=./replica.db;Sync Interval=30");
```

Bootstrap is a validated raw-page snapshot. For protocol-2 databases,
incremental pull is Turso's MVCC logical stream: Ahtola validates the complete
`lml3` response, replays the transaction atomically, filters this client's own
transactions, and only then advances the opaque server revision. If retained
logical history is unavailable, a complete `Pages + ReplaceBase` response is
installed atomically. Pending local row changes are preserved across logical
pulls; unsafe residual deletes or schema changes fail closed until they have
been pushed. Protocol-1 databases keep the page-incremental path.

Embedded replicas support `DbBatch` through both facades. Enum parameters bind
as their underlying SQLite integer value. Extra parameters that are not
referenced by the SQL are ignored; every referenced slot still requires a
value.

Server-side rejection of a replayed local change surfaces as
`Ahtola.AhtolaReplicaConflictException`: `ConflictKind`
(`RowWrite`/`SchemaChange`/`Unknown`), `RemoteErrorCode`, and
`LocalChangeSequence` for programmatic handling. Synchronization never
rebases or auto-merges — the journal is retained on conflict so the
application can resolve it explicitly (e.g. read the remote state, decide
whether to discard or replay the conflicting local change).

### Concurrent writes with an embedded replica

Each embedded replica keeps its own local change journal, so "concurrent
writes" here means **multiple independent replicas (processes/machines)**
writing locally and periodically reconciling through the server — not
in-process MVCC:

- Writes against **the same replica connection** are ordinary local SQLite
  transactions (`BeginTransaction()`, or `PRAGMA journal_mode=mvcc` +
  `BEGIN CONCURRENT` if you also want process-local concurrent writers
  against that one replica file, exactly as in the
  [local-file MVCC section](#concurrent-writes-on-a-local-file-mvcc)).
- Writes made **between two different replicas** (or a replica and the
  primary) are only reconciled when each side calls `Sync`/`SyncAsync` (or
  its `Sync Interval` background loop). Synchronization never rebases or
  auto-merges: if the server rejects a replayed change, catch
  `AhtolaReplicaConflictException` and decide how to reconcile.

## Error handling patterns

```csharp
try
{
    connection.ExecuteNonQuery("INSERT INTO t VALUES (1)");
}
catch (Exception ex) when (ex.Message.Contains("database is locked"))
{
    // Local busy/lock conflict — classic single-writer contention, or a
    // same-row MVCC conflict. Retry with backoff, or roll back an open
    // transaction before retrying.
}
catch (Ahtola.AhtolaReplicaConflictException ex)
{
    Console.WriteLine($"{ex.ConflictKind}: {ex.Message} (remote code {ex.RemoteErrorCode})");
    // Journal is retained; decide whether to discard or replay the change.
}
catch (Ahtola.Data.Sqlite.SqliteRemoteException ex)
{
    Console.WriteLine(ex.Classification);
    // Retry only when your operation is safe to repeat.
}
```

- Local busy/lock conflicts (classic single-writer contention, or a same-row
  MVCC conflict) surface as an exception whose message contains `database is
  locked`; `Default Timeout` / `Command Timeout` on the connection string
  (and `PRAGMA busy_timeout`) control how long a statement waits before
  giving up.
- The SQLite-compatible facade maps Turso/Hrana failures to
  `SqliteRemoteException` with transient/permanent classification and optional
  HTTP status. The native facade exposes `AhtolaException` /
  `AhtolaRemoteSqlException`.
- Embedded-replica sync conflicts are `Ahtola.AhtolaReplicaConflictException`
  (see above) and expose `ConflictKind`, `RemoteErrorCode`, and
  `LocalChangeSequence` for programmatic handling.

See the top-level [README's "Important limits"](../README.md#important-limits)
section for what Ahtola does *not* yet implement, and
[docs/powershell-module.md](powershell-module.md) if you'd rather drive Ahtola
from PowerShell instead of C#.
