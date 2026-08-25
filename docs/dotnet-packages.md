# NuGet packages guide

Ahtola ships as four NuGet packages that build directly on top of each
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
| `Devolutions.Ahtola.Data.Sqlite.Browser` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.Data.Sqlite.Browser) | Blazor/.NET WebAssembly data source with durable OPFS storage. See the [browser deployment guide](browser-wasm.md). |
| `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.EntityFrameworkCore.Sqlite) | EF Core provider (`UseAhtola`) — local databases, direct remote Turso Cloud/Hrana connections, and embedded replicas (see [Entity Framework Core](#entity-framework-core)). |

```bash
# ADO.NET only
dotnet add package Devolutions.Ahtola.Data.Sqlite

# + EF Core (9.x on net8.0/net9.0, 10.x on net10.0)
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite

# Blazor/.NET WebAssembly OPFS support
dotnet add package Devolutions.Ahtola.Data.Sqlite.Browser
```

Targets `net8.0`, `net9.0`, `net10.0` — no `net48` / .NET Framework assets, no
native SQLite binary, and no P/Invoke SDK to restore. Every shipped package
(`Devolutions.Ahtola.Core`, `Devolutions.Ahtola.Data.Sqlite` — which embeds
`Devolutions.Ahtola.Data` — `Devolutions.Ahtola.Data.Sqlite.Browser`, and
`Devolutions.Ahtola.EntityFrameworkCore.Sqlite`) builds with
`IsAotCompatible`/`IsTrimmable` and ships no trim-warning suppression, so a
trimmed or NativeAOT publish reports nothing from Ahtola itself.

The ADO stack (`…Core` → `…Data.Sqlite` → optionally `…Data.Sqlite.Browser`) is
trim-clean end to end: a publish with
`-p:SuppressTrimAnalysisWarnings=false -p:TrimmerSingleWarn=false` reports zero
`IL2xxx`/`IL3xxx` warnings across the whole closure. Adding
`Devolutions.Ahtola.EntityFrameworkCore.Sqlite` still reports warnings, but they
come from `Microsoft.EntityFrameworkCore`, which annotates `DbContext` and the
query pipeline with `RequiresUnreferencedCode`/`RequiresDynamicCode`; none of
them originate in Ahtola. An EF Core profile only becomes trim-clean once that
upstream chain is warning-free.

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
- **Blazor/.NET WebAssembly with durable OPFS storage** → add
  `Devolutions.Ahtola.Data.Sqlite.Browser`, create an
  `AhtolaBrowserDataSource`, and use async APIs. Opt into
  `AhtolaBrowserSynchronousMode.ReadOnlyMirror` when existing repository code
  needs synchronous reads: after one asynchronous open, provably read-only
  statements are served from the managed in-memory mirror without touching OPFS.
  See the [browser deployment guide](browser-wasm.md).

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
| `Data Source` (`Filename`) | File path, `:memory:`, or a Turso/Hrana URL (`turso://…`, `libsql://…`, `https://…`, `wss://…`) |
| `Mode` | `ReadWriteCreate` (default), `ReadWrite`, `ReadOnly`, `Memory` |
| `Cache` | `Private` (default) / `Shared` |
| `Pooling` | Connection pooling (default `true`) |
| `Foreign Keys` | `PRAGMA foreign_keys` |
| `Default Timeout` / `Command Timeout` | Busy timeout in seconds |
| `Vfs` | Named VFS registration |
| `Password` / `Password Scheme` | Passphrase-based encryption (see [Encryption](#encryption)) |
| `Encryption Cipher` / `Encryption Key` | Raw-key encryption (hex AES-128/256-GCM) |
| `Local Provider` | `Managed` (default) or `Native`. `Native` requires the optional, non-shipped native companion to have called `AhtolaNativeProvider.Register(factory)` (typically from a `[ModuleInitializer]`); nothing is loaded by assembly name, so without a registration the connection fails closed with `NotSupportedException`. |
| `Foreign Read Only` | Read another engine's open database without taking main-file locks (`Mode=ReadOnly` + `Pooling=False`) |
| `DateTimeKind`, `BinaryGUID` | Facade-only ADO.NET conversion behavior |

> **Companion compatibility.** Earlier versions activated `Local Provider=Native`
> by loading `Turso.Data.Native` reflectively and invoking its
> `NativeProviderRegistration.Register`. Reflective probing is invisible to the
> trimmer and to NativeAOT, so it is gone: activation is now explicit only. A
> companion package built against the old behavior never calls `Register` itself
> and is therefore never activated — `Local Provider=Native` fails closed with
> `NotSupportedException` even when the package is installed. Companions must
> ship a release that calls `AhtolaNativeProvider.Register(...)`,
> `SqliteNativeProvider.Register(...)` and `AhtolaReplicaProvider.Register(...)`
> from a `[ModuleInitializer]`, or document an explicit startup call. That
> companion release is tracked separately from this repository.

Remote keywords accepted by both facades (see the Turso Cloud sections below):
`Auth Token`, `Replica Path`, `Sync Interval`, `Read Your Writes`, `Tls`.

Hrana WebSocket keywords, used only by `ws://`/`wss://` data sources (see
[Hrana over WebSocket](#hrana-over-websocket-wswss)):

| Keyword | Aliases | Default | Notes |
| --- | --- | --- | --- |
| `Ws Keepalive Interval` | `WsKeepaliveInterval`, `WebSocket Keepalive Interval`, `WebSocketKeepAliveInterval` | `30` | Keep-alive ping interval in seconds; `0` disables |
| `Ws Keepalive Timeout` | `WsKeepaliveTimeout`, `WebSocket Keepalive Timeout`, `WebSocketKeepAliveTimeout` | `20` | Pong grace period in seconds (.NET 9+; ignored on net8.0) |
| `Ws Half Open Timeout` | `WsHalfOpenTimeout`, `WebSocket Half Open Timeout`, `WebSocketHalfOpenTimeout` | `0` | Seconds of total peer silence, while requests are outstanding, that abort the connection as half-open. `0` disables it. This is the only half-open detection on net8.0; because a Hrana server sends nothing while a statement runs, a non-zero value also caps how long one request may take |
| `Ws Max Message Bytes` | `WsMaxMessageBytes`, `WebSocket Max Message Bytes`, `WebSocketMaxMessageBytes` | `16777216` | Hard cap on one reassembled message (8 KiB–512 MiB) |
| `Ws Connect Attempts` | `WsConnectAttempts`, `WebSocket Connect Attempts`, `WebSocketConnectAttempts` | `3` | Bounded connection-establishment attempts (1–10); never replays operations |

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

Direct remote connections use Hrana HTTP v3. Pipeline operations are sent to
`/v3/pipeline`, while `ExecuteReader`/`ExecuteReaderAsync` use `/v3/cursor` and
parse the newline-delimited cursor response incrementally; rows are available
to the `DbDataReader` without buffering the complete response. The cursor's
baton follows the same transaction and `Read Your Writes` lifetime as pipeline
requests, and stateless cursors are closed explicitly after their terminating
frame.

For an unversioned database URL, a `404 Not Found` from the first stateless v3
request selects Hrana v2 for that connection and retries through
`/v2/pipeline`; readers are buffered on that compatibility path because v2 has
no cursor endpoint. A URL that explicitly ends in `/v2/pipeline` or a v3
endpoint is pinned to that version and is never downgraded. No fallback is
attempted after a baton has been issued, so an expired or invalid live session
cannot be mistaken for protocol negotiation.

## Hrana over WebSocket (ws/wss)

A `ws://` or `wss://` data source opens a **persistent Hrana WebSocket
connection** instead of the stateless HTTP pipeline. `http`, `https`, `libsql`
and `turso` URLs are unaffected and keep using `/v3/pipeline` + `/v3/cursor`
exactly as described above; the transport is chosen once, when the connection is
opened, and never silently downgrades from WebSocket to HTTP.

```csharp
using var cloud = new SqliteConnection(
    "Data Source=wss://my-db.turso.io;Auth Token=" + token);
cloud.Open();
```

**Target server.** This transport implements the authoritative libSQL/sqld Hrana
WebSocket protocol (`docs/HRANA_{1,2,3}_SPEC.md` in `tursodatabase/libsql`).
The Turso engine pinned by this repository has **no native Hrana WebSocket
server** — it maps `ws`/`wss` onto its HTTP pipeline endpoint — so point
`ws`/`wss` connection strings at a legacy libSQL/sqld deployment (including
Turso Cloud) rather than at the new engine.

**Negotiation.** The upgrade happens on the URL's own path (there is no `/v2` or
`/v3` suffix for WebSocket) and offers the JSON subprotocols `hrana3`, `hrana2`,
`hrana1`. An empty/absent `Sec-WebSocket-Protocol` response is treated as
Hrana 1, per the spec. `hrana3-protobuf` and any other unknown value are
rejected and the socket is closed: this client speaks only the JSON encoding.
Authentication travels in the first `hello` message as a JWT (a WebSocket has no
per-message headers).

**Features by negotiated version.**

| Feature | v1 | v2 | v3 |
| --- | --- | --- | --- |
| `open_stream` / `close_stream` / `execute` / `batch` | yes | yes | yes |
| `store_sql` / `close_sql` / `sequence` / `describe` | no | yes | yes |
| `open_cursor` / `fetch_cursor` / `close_cursor` / `get_autocommit` | no | no | yes |
| `ok` / `error` / `not` / `and` / `or` batch conditions | yes | yes | yes |
| `is_autocommit` batch condition | no | no | yes |
| `ExecuteReader` streaming | buffered `execute` | buffered `execute` | paged cursor |

Version checks run **before** anything is written to the socket and before a
stream is opened, so a request the negotiated version cannot serve never leaves
a half-created `stream_id` behind. The `is_autocommit` check walks the whole
condition tree, so it also catches the common `not(is_autocommit)` shape.

**Fail-closed boundaries.**

- **Remote encryption is refused over `ws`/`wss`.** The official `hello` message
  has no encryption-key field, so the `x-turso-encryption-key` value the HTTP
  pipeline sends as a header cannot be conveyed. Use an `https` URL instead.
- **Protocol violations terminate the connection.** An unknown message
  discriminator, a response for an unknown request id, a binary frame on a JSON
  subprotocol, an unparsable message, or a message larger than
  `Ws Max Message Bytes` closes the socket and fails every pending request.
- **Malformed payloads are protocol violations, not data.** Every response is
  checked against the contract for the request it answers — `result` for
  `execute`/`batch`/`describe`, `is_autocommit` for `get_autocommit`,
  `entries` + `done` for `fetch_cursor`, `error` for `response_error` — before
  the waiting caller sees it. A missing or mistyped mandatory field, an
  out-of-range integer, a row whose width does not match `cols`, or an unknown
  nested discriminator (cursor entry type, value type) terminates the
  generation instead of silently becoming `false`, `[]` or a skipped row.
- **Nothing is ever replayed.** Streams, cursors and stored SQL die with their
  connection. If the connection is lost while a session (transaction or cursor)
  is open, the ADO.NET remote session is invalidated and the failure surfaces to
  the caller; a later operation may open a brand-new connection and stream, but
  the client never re-sends an in-flight statement.
- **No transport downgrade.** A `ws`/`wss` connection never falls back to HTTP.
- **TLS and credentials follow the HTTP policy.** `Auth Token` requires `wss`
  (or a loopback host), the upgrade never follows redirects, and certificate
  validation is left to the platform — there is no certificate bypass.

**Concurrency.** One serialized send path and one continuous receive loop own the
socket, so there is never a concurrent `SendAsync` or `ReceiveAsync`; keeping a
receive outstanding is also what lets the runtime process keep-alive pongs.
`CloseOutputAsync` is itself a send, so it is only issued once the send loop has
been observed to stop — against a wedged peer the close frame is skipped and the
socket is aborted instead. Requests are correlated by `request_id`, so the server
may answer out of order across streams while per-stream ordering is preserved.

**Cancellation.** Cancelling a command (or hitting `Command Timeout`) abandons
only that caller's wait — the socket stays healthy and a late response for the
abandoned id is discarded. Two details make that safe:

- Requests that mint a server-side handle (`open_stream`, `open_cursor`) keep
  their correlation slot after the caller walks away. If the server answers
  late, the handle is closed immediately; if it cannot be closed, the
  connection is retired so nothing leaks for the rest of its life.
- The discard list is scoped to the connection and never evicted while the
  connection lives, because an abandoned request can be answered arbitrarily
  late and forgetting it first would turn a valid reply into a spurious
  "unknown request id" abort. If more than 65 536 requests are abandoned
  unanswered, the connection is retired rather than start forgetting.

**Liveness.** On .NET 9+ the runtime enforces `Ws Keepalive Timeout` with real ping/pong,
which a busy server keeps answering. On net8.0 `ClientWebSocket` has no pong timeout, so
half-open detection is opt-in through `Ws Half Open Timeout`: a watchdog aborts the
connection when nothing at all has arrived for that budget *and* a request has been
outstanding that long. It sends no frames of its own. It is off by default on purpose —
without ping/pong, "the server has sent nothing" cannot distinguish a dead socket from one
running a slow statement, so any budget also caps how long one request may take. Set it
above the longest statement the workload issues, or leave it disabled and rely on
`Command Timeout`.

**Disposal.** `Dispose()` and `DisposeAsync()` converge on one idempotent
disposal. The graceful phase (drain, `close_stream`, close frame) is bounded by
`Ws Close Timeout`; once that budget is spent the socket is aborted, and
disposal still waits for both loops to terminate and the socket to be disposed.
A synchronous `Dispose()` therefore never returns while the socket is live.

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

The pure-managed provider supports `AhtolaPartialBootstrapOptions.Prefix(...)`
and `AhtolaPartialBootstrapOptions.QueryPages(...)` as the initial page
selector. It publishes the selected complete 4 KiB pages, records the missing
ranges in an integrity-protected durable sidecar, and fetches a missing page
from the pinned bootstrap revision before the pager can observe its bytes.
Concurrent faults are coalesced, `SegmentSize` controls the fetch segment, and
`Prefetch` opts into fetching the rest of that segment. The bootstrap marker,
metadata, and page-state sidecar are durable before the sparse database becomes
visible. A physical partial replica has one process-exclusive materializer, and
write-ahead mutation intents make an interrupted local page write recoverable
without treating sparse zeroes as data. Before an ordinary sync advances the
revision, Ahtola pushes tracked local changes, completes the pinned image, and
transitions back to the normal full-file publication path.

`QueryPages(...)` sends the query as Turso's `server_query_selector`
(`PullUpdatesReqProtoBody` tag 7) on the single bootstrap request only, never
together with a page selector and never chunked — the server, not the client,
decides which pages the query touches, so `PullBytesThreshold` is rejected with
it. The returned page set may be unordered and non-contiguous, `db_size` still
describes the whole database, and page 1 (the SQLite header page) is mandatory;
duplicate, out-of-range, wrong-sized, or header-less responses fail closed
without publishing anything. After bootstrap the query is never persisted or
resent: missing pages fault by page id against the pinned revision.

Two caveats. **The remote must implement query selection.** Turso's vendored
local dev server ignores `server_query_selector` by design, so a query
bootstrap against it silently degrades to a full-database response; only a
server that honours tag 7 produces a genuinely partial image. **Sidecar size
scales with scatter.** The page-state sidecar stores materialized pages as a
run list, so a worst-case scattered selection (for example every other page)
degenerates to one `(start, count)` pair per page. That is bounded and durable
but noticeably larger than a prefix image's single run; a bitmap-backed sidecar
would bound it better and is not implemented.

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
