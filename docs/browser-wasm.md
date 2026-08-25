# Browser WebAssembly

`Devolutions.Ahtola.Data.Sqlite.Browser` runs Ahtola's managed SQL engine in
.NET WebAssembly and stores durable SQLite-compatible files in the browser's
Origin Private File System (OPFS). It is a Razor class library: the NuGet
package carries the JavaScript module and worker as static web assets and does
not ship a second database WebAssembly binary.

> Ahtola is experimental. Treat browser databases as application data that can
> be recreated or exported, and test browser eviction and quota behavior for
> your deployment.

## Supported profile

The Ahtola engine as a whole is experimental, and nothing here claims otherwise.
What *is* defined and continuously validated is a narrow browser profile — the
**read-mirror profile** — with the guarantees below. Everything outside it is
preview surface: usable, exercised by tests, but without these guarantees.

**In profile**

| Guarantee | What is validated |
| --- | --- |
| Memory reads | After one asynchronous open, the database image lives in managed memory. Provably read-only statements (`SELECT`, `VALUES`, `WITH …` terminating in `SELECT`/`VALUES`) execute with zero OPFS/worker operations, asserted by an operation counter rather than a timing threshold. |
| Asynchronous durable mutations | Every mutation flushes to OPFS before its asynchronous operation completes; a failed flush is surfaced, never swallowed. Synchronous close/disposal fails closed while anything is pending. |
| Crash-safe OPFS | Whole-file publication uses atomic replacement, so an interrupted write never leaves a partially visible destination image. |
| Encryption | The AHTLA page format (AES-128/256-GCM via Web Crypto, AEGIS via the managed cipher) is byte-compatible with desktop databases; wrong keys and cipher mismatches fail closed instead of falling back. |
| Single-owner directory | One data source owns an OPFS directory at a time through Web Locks; a competing context is rejected with `AhtolaBrowserDatabaseLockedException` rather than corrupting shared state. |
| Trim-clean ADO stack | Publishing a browser app that uses only `Devolutions.Ahtola.Data.Sqlite.Browser` (plus `Devolutions.Ahtola.Data.Sqlite` and `Devolutions.Ahtola.Core`) with `-p:SuppressTrimAnalysisWarnings=false -p:TrimmerSingleWarn=false` produces **zero** `IL2xxx`/`IL3xxx` warnings in the whole closure. Gated by `./build.ps1 validate-browser-trim`. |

### Trimming and NativeAOT profile

| Profile | Packages | Trim status |
| --- | --- | --- |
| Browser ADO | `Devolutions.Ahtola.Data.Sqlite.Browser` → `Devolutions.Ahtola.Data.Sqlite` → `Devolutions.Ahtola.Core` | **Trim-clean.** Zero total `IL2xxx`/`IL3xxx` warnings with trim analysis unsuppressed. |
| Browser EF Core | The above plus `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | **Not trim-clean, and not Ahtola's to fix.** Zero warnings originate in `Devolutions.Ahtola.*` assemblies or `src/Ahtola.*` source, but the EF Core dependency chain still reports warnings of its own. |

`Microsoft.EntityFrameworkCore` annotates `DbContext` and its query pipeline with
`RequiresUnreferencedCode`/`RequiresDynamicCode`, so any application that uses EF
Core in the browser inherits those warnings regardless of provider. The EF browser
profile becomes trim-clean only once that upstream dependency chain is
warning-free; until then, treat the remaining warnings as upstream and do **not**
read them as an Ahtola trim regression. The gate records them under
`artifacts/trim-analysis/Ef-upstream-warnings.txt` so the split stays auditable.

**Preview / out of profile**

- Synchronous execution of anything the classifier cannot prove read-only —
  mutations, DDL, `PRAGMA`, `EXPLAIN`, transaction control, `ATTACH`/`DETACH`,
  writable CTEs, and batches containing an unproven statement. These continue to
  require the asynchronous API.
- Incremental blobs and backup/restore, which are asynchronous only.
- Cross-tab concurrent access to one database directory.
- WebKit/Safari, where the required OPFS and isolation capabilities are still
  incomplete; `AhtolaBrowserRuntime.GetCapabilitiesAsync()` reports the gap and
  the data source refuses to initialize.
- Large working sets: the mirror holds the whole database image in memory, so
  size the database for the browser tab's heap.

## Install

```bash
dotnet add package Devolutions.Ahtola.Data.Sqlite.Browser

# Add this as well when using EF Core:
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite
```

The packages target `net8.0`, `net9.0`, and `net10.0`.

## Required browser features

Call `AhtolaBrowserRuntime.GetCapabilitiesAsync()` before presenting a
persistent-storage workflow. `IsSupported` requires all of:

- a secure browser context;
- cross-origin isolation;
- `SharedArrayBuffer`;
- OPFS and `FileSystemSyncAccessHandle`;
- module workers; and
- Web Locks.

Current evergreen Chromium, Firefox, and Safari are the intended browser
targets. Chromium is the mandatory automated package gate. Capability
detection, rather than user-agent detection, is authoritative.

## Hosting headers

Every document that creates an Ahtola browser data source must be served with:

```text
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

HTTPS is required outside loopback development hosts. Under
`require-corp`, cross-origin scripts, fonts, images, and other subresources
must opt in through CORS or an appropriate
`Cross-Origin-Resource-Policy` header. A missing or blocked subresource can
prevent `crossOriginIsolated` from becoming true.

Do not copy the packaged modules into the application manually. Razor static
web assets publish them at:

```text
_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-opfs.mjs
_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-opfs-capability-probe-worker.mjs
_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-opfs-worker.mjs
_content/Devolutions.Ahtola.Data.Sqlite.Browser/ahtola-crypto.mjs
```

## ADO.NET

Keep one data source for the lifetime of the application feature and create
short-lived connections from it:

```csharp
using Ahtola.Data.Sqlite.Browser;

await using var dataSource = new AhtolaBrowserDataSource("inventory/main.db");

await using (var connection = await dataSource.OpenConnectionAsync())
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS products(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL
        );
        INSERT INTO products(name) VALUES ($name);
        """;
    command.Parameters.AddWithValue("$name", "keyboard");
    await command.ExecuteNonQueryAsync();
}

await using (var connection = await dataSource.OpenConnectionAsync())
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT id, name FROM products ORDER BY id";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        Console.WriteLine($"{reader.GetInt64(0)} {reader.GetString(1)}");
}
```

The one-argument constructor derives the owned directory from the database
path. Use the explicit overload when attached databases need a larger common
directory:

```csharp
await using var dataSource = new AhtolaBrowserDataSource(
    databasePath: "inventory/databases/main.db",
    ownedDirectory: "inventory");
```

All database, `ATTACH`, WAL, journal, backup, and temporary paths are
normalized relative paths. Traversal (`..`), absolute paths, and paths outside
the owned directory are rejected.

For a non-persistent database that still enforces the browser async-only
contract, use `new AhtolaBrowserDataSource(":memory:")`. Connections from that
data source share one managed in-memory database until the data source is
disposed and do not initialize OPFS. Read-only and encryption settings are not
applicable to `:memory:`.

## Async-only contract (default)

Connections produced by `AhtolaBrowserDataSource` never block on an incomplete
browser operation. Use the asynchronous lifecycle and execution surface:

| Operation | Browser API |
| --- | --- |
| Open / close | `OpenAsync`, `CloseAsync`, `DisposeAsync` |
| Commands | `ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `ExecuteReaderAsync` |
| Readers | `ReadAsync`, `NextResultAsync`, `DisposeAsync` |
| Transactions | `BeginTransactionAsync`, `CommitAsync`, `RollbackAsync`, `DisposeAsync` |
| Incremental blobs | `OpenBlobAsync`, stream async read/write/flush/dispose |
| Backup | `BackupDatabaseAsync` |

Synchronous counterparts throw `PlatformNotSupportedException` when they
would cross the browser storage boundary. Use `await using` for the data
source, connections, readers, transactions, commands, and blobs so pending
durable mutations are flushed before their owners are released.

Cancellation is cooperative. A cancellation requested before a mutation
prevents it. Once a non-reversible managed mutation succeeds, Ahtola completes
the durability boundary with a non-cancelable flush rather than reporting a
false cancellation for committed work.

## Synchronous read-mirror mode (opt-in)

The mirror already loads the whole database image into managed memory during the
asynchronous open, so a statement that cannot mutate that image needs no OPFS
access at all. Opting a data source into
`AhtolaBrowserSynchronousMode.ReadOnlyMirror` lets those statements run on the
synchronous ADO.NET surface that existing repository code is written against.
The mode is offered by a dedicated constructor overload rather than an added
optional parameter, so every constructor signature that shipped before the mode
existed is still present and already-compiled callers keep binding:

```csharp
await using var dataSource = new AhtolaBrowserDataSource(
    databasePath: "inventory/main.db",
    ownedDirectory: "inventory",
    sharedBufferSize: AhtolaBrowserOptions.DefaultSharedBufferSize,
    readOnly: false,
    encryption: null,
    synchronousMode: AhtolaBrowserSynchronousMode.ReadOnlyMirror);

// One asynchronous initialization and open is still required.
var connection = await dataSource.OpenSynchronousReadConnectionAsync();

using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT name FROM items WHERE id = $id";
    var id = command.CreateParameter();
    id.ParameterName = "$id";
    id.Value = 42;
    command.Parameters.Add(id);

    var name = (string?)command.ExecuteScalar();   // no OPFS, no worker call
}

connection.Close();     // legal only while nothing is pending
```

`OpenSynchronousReadAhtolaConnectionAsync` returns the same profile as an
`AhtolaConnection`.

### What may run synchronously

Only statements the provider can *prove* cannot mutate the database:

| Allowed synchronously | Still asynchronous |
| --- | --- |
| `SELECT` | `INSERT`, `UPDATE`, `DELETE`, `REPLACE INTO` |
| `VALUES` | `CREATE`, `DROP`, `ALTER`, `REINDEX`, `VACUUM`, `ANALYZE` |
| `WITH …` whose terminal statement is `SELECT`/`VALUES` | `PRAGMA`, `EXPLAIN`, `EXPLAIN QUERY PLAN` |
| Reader iteration and disposal for those statements | `BEGIN`/`COMMIT`/`ROLLBACK`/`SAVEPOINT`/`RELEASE` |
| `Close`/`Dispose` while nothing is pending | `ATTACH`/`DETACH`, writable CTEs, blobs, backup |

The proof is script-wide: a batch is refused if *any* statement in it is
unproven, and quoted identifiers, string literals, and comments are treated as
data rather than keywords. A line comment ends at CR as well as LF, matching the
production statement splitters exactly, so a lone-CR newline cannot hide a
trailing `; INSERT …` behind a comment. Anything the classifier cannot decide —
including malformed SQL — is refused, and refusal happens before the command
touches the engine, so a rejected statement can never leave a half-applied
change.

The same rule applies to `DbBatch`: a batch whose commands are *all* proven
read-only may be executed, iterated, closed and disposed synchronously, because
it is served entirely from the managed mirror. A batch containing even one
unproven command is refused before its first command runs, so it can never be
partially applied.

Authorization is proven once, when the statement is prepared and executed, and
the resulting reader carries that decision for its whole lifetime. Reassigning
`CommandText` afterwards therefore cannot retroactively authorize an open reader
(and the `Microsoft.Data.Sqlite`-compatible `SqliteCommand` refuses the
reassignment outright while a reader is open). Capturing once is also what keeps
the classifier off the per-row path: it never re-tokenizes the SQL while a
result set is being read.

Registered scalar functions, aggregates, and collations still run inside a
synchronous read and keep their normal exception behaviour. They cannot write
the database through the executing command, so they do not affect the proof.

### Durability and lifecycle rules

- Every mutation still flushes to OPFS before its asynchronous operation
  completes, and a failed flush is still surfaced to the caller.
- `Close`/`Dispose` are allowed synchronously only while the mirror owes the
  persistent store nothing. With a mutation pending they fail closed with
  `PlatformNotSupportedException` and leave the connection open, so `CloseAsync`
  or `DisposeAsync` can drain it.
- Synchronous `Open` is never allowed; the one asynchronous open is what
  materializes the mirror.
- The data source itself is still disposed asynchronously, because it owns the
  OPFS handles.
- `AhtolaBrowserDataSource.GetStorageMetrics()` reports
  `PersistentOperations` (OPFS worker round trips), `PendingMutations` and
  `HasUnflushedWork`. `PendingMutations` counts everything the mirror still owes
  OPFS — mutations still queued **plus** mutations already handed to a running
  flush — so it never reads zero while a flush is in progress, and it is
  non-zero exactly when `HasUnflushedWork` is true, which is the same predicate
  synchronous `Close`/`Dispose` fail closed on. A workload doing only supported
  synchronous reads leaves `PersistentOperations` unchanged — that invariant,
  not a timing threshold, is what the tests and
  `AhtolaBrowserStorageDiagnostics.RunSynchronousReadAsync` assert.

```csharp
var diagnostic = await AhtolaBrowserStorageDiagnostics.RunSynchronousReadAsync(1000);
// diagnostic.Succeeded, .WorkerOperationsUnchanged,
// .ElapsedMilliseconds, .AverageMicrosecondsPerRead
```

## EF Core

Create and asynchronously open a browser connection, then give that existing
connection to `UseAhtola`:

```csharp
await using var dataSource = new AhtolaBrowserDataSource("notes/main.db");
await using var connection = await dataSource.OpenConnectionAsync();

var options = new DbContextOptionsBuilder<NotesContext>()
    .UseAhtola(connection)
    .Options;

await using var context = new NotesContext(options);
await context.Database.EnsureCreatedAsync();
context.Notes.Add(new Note { Text = "stored in OPFS" });
await context.SaveChangesAsync();
var notes = await context.Notes.ToListAsync();
```

Use EF Core async APIs (`EnsureCreatedAsync`, `MigrateAsync`,
`SaveChangesAsync`, async LINQ operators, and async transactions). A context
does not own a separately supplied connection unless configured to do so;
dispose objects in context, connection, data-source order.

## Encrypted OPFS databases

Browser storage supports the same AHTLA AES-GCM page format as desktop Ahtola.
Web Crypto derives/imports a non-extractable key and encrypts database pages,
WAL frame bodies, and rollback-journal page records at the asynchronous OPFS
boundary.

```csharp
using var encryption =
    AhtolaBrowserEncryptionOptions.FromPassword("correct horse battery staple");
using var browserOptions = new AhtolaBrowserOptions(
    databasePath: "secure/main.db",
    encryption: encryption);
await using var dataSource = new AhtolaBrowserDataSource(browserOptions);

await using var connection = await dataSource.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = "CREATE TABLE IF NOT EXISTS secrets(value TEXT NOT NULL)";
await command.ExecuteNonQueryAsync();
```

For exact key material, use `FromKey(AhtolaEncryptionCipher, ReadOnlySpan<byte>)`
or `FromHex(AhtolaEncryptionCipher, string)`. Password mode is the stable
`Ahtola.Password.v1` scheme: PBKDF2-HMAC-SHA256 with its fixed domain salt,
210,000 iterations, and an AES-256-GCM key.

Encryption options are copied by both `AhtolaBrowserOptions` and
`AhtolaBrowserDataSource`. Dispose caller-owned option objects after constructing
their owner; key material is never included in `ConnectionString`. A wrong key,
cipher mismatch, plaintext file, or authentication failure is rejected without
fallback.

The on-disk bytes are desktop/browser compatible. A browser-written file can be
opened by the desktop provider with the matching `Password` or
`Encryption Cipher`/`Encryption Key` settings, and the browser can open a
desktop-written AHTLA database and retained WAL.

See [Browser encrypted storage](browser-encrypted-storage.md) for byte layout,
WAL/journal checksum handling, cancellation, retry, and durability details.

## Ownership and locking

One data source owns one OPFS worker and one Web Lock named for its owned
directory. Connections from that data source may share the database in the
same page. A second data source, tab, or worker attempting to own the same
directory fails immediately with `AhtolaBrowserDatabaseLockedException`
instead of waiting indefinitely.

Choose an owned directory that is private to one logical database family.
Different independently opened data sources need different owned
directories. Dispose every connection before disposing its data source; data
source disposal waits for active connections to drain.

## Persistence, quota, and clearing data

OPFS is scoped to the page's origin. Its lifetime follows browser storage
policy, not the application deployment:

- private browsing may use ephemeral storage;
- users can clear site data at any time;
- browsers may evict non-persistent origins under storage pressure;
- quota differs by browser, device, and user settings.

Applications can inspect `navigator.storage.estimate()` and request
`navigator.storage.persist()` in their own JavaScript when their product
policy calls for it. Ahtola maps quota failures to an explicit storage error;
it does not silently switch to IndexedDB or memory.

Changing the site's scheme, host, or port selects a different origin and
therefore a different OPFS. Plan migrations before changing production
origins.

## Architecture and security boundary

SQL parsing, compilation, execution, and result materialization remain in the
primary .NET WebAssembly runtime. A dedicated JavaScript module worker owns
OPFS synchronous access handles. Requests use a bounded
`SharedArrayBuffer`; the main browser thread never calls `Atomics.wait`.

The worker obtains a fail-fast Web Lock, normalizes every path, and uses a
checksummed replacement-intent journal for crash-safe cross-file publication.
The shared buffer avoids a second JavaScript worker copy, but supported .NET
JavaScript interop still copies at the managed boundary.

No Rust toolchain, native SQLite library, P/Invoke SDK, runtime native asset,
or custom database `.wasm` is part of the browser package.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `IsSupported` is false | HTTPS/loopback, COOP, COEP, `SharedArrayBuffer`, OPFS sync handles, Web Locks |
| Static module import fails | Razor static web assets are enabled and `_content/Devolutions.Ahtola.Data.Sqlite.Browser/` is deployed |
| `AhtolaBrowserDatabaseLockedException` | Another data source/tab owns the same directory; reuse the existing data source or close the other owner |
| Quota or I/O exception | Available site storage, private-browsing policy, eviction, and browser console diagnostics |
| Synchronous API throws | Use the async counterpart and `await using`, or opt into `AhtolaBrowserSynchronousMode.ReadOnlyMirror` for provably read-only statements |
| Data appears missing after deployment | Confirm the application origin (scheme, host, and port) did not change |

## Package validation

The repository validates the actual nupkgs through a published Blazor
consumer:

```powershell
pwsh ./build.ps1 pack -Configuration Release -PackageVersion 0.0.0-local
pwsh ./scripts/Invoke-BrowserPackageConsumer.ps1 `
  -PackageDirectory ./artifacts/managed-packages `
  -PackageVersion 0.0.0-local
```

CI additionally publishes the consumer with browser AOT compilation and
checks the packed/published closure for native, Rust, and custom WebAssembly
payloads.

## Trim analysis gate

`scripts/Invoke-TrimAnalysisGate.ps1` publishes each consumer profile with
trim analysis fully unsuppressed and fails on any warning attributable to Ahtola:

```powershell
pwsh ./build.ps1 validate-browser-trim   # Ado + Ef browser profiles
pwsh ./build.ps1 validate-trim           # also the desktop trimmed/NativeAOT profiles

# or, against an existing package directory
pwsh ./scripts/Invoke-TrimAnalysisGate.ps1 `
  -PackageDirectory ./artifacts/managed-packages `
  -PackageVersion 0.0.0-managed-local `
  -Profile Browser
```

| Profile | Project | Gate |
| --- | --- | --- |
| `Ado` | `samples/BrowserAdoTrimConsumer` | Zero `IL2xxx`/`IL3xxx` warnings in the **entire** closure. |
| `AdoDesktopTrimmed` / `AdoDesktopAot` | `samples/AdoTrimConsumer` | Zero warnings in the **entire** closure under ILLink and under NativeAOT, **and** the published binary is executed and must report `PASS: ado-trim-consumer`. |
| `Ef` | `samples/BrowserEfTrimConsumer` | Zero warnings attributed to Ahtola, and no grouped `IL2104`/`IL3053` naming an Ahtola assembly. Upstream warnings are recorded, not suppressed. |
| `DesktopTrimmed` / `DesktopAot` | `samples/ManagedPackageConsumer` | Same Ahtola-attributed rule, through ILLink and through the NativeAOT compiler. ILC sees a different reachable set than ILLink, so both are gated. |

A warning is attributed to Ahtola when its source file lives under
`src/Ahtola.<project>/` **or** when the member or assembly it names is in a
`Devolutions.Ahtola` assembly or an `Ahtola.*` namespace. Both are checked for
every line: a warning raised at a *consumer* source location still belongs to us
when its payload names an Ahtola member — for example an `IL2091` on the
consumer's own call into an annotated Ahtola generic — so the file prefix never
short-circuits the payload check. Checkout directory names are deliberately not
used as evidence. Pass `-ClassifyOnly <publish.log>` to re-run just the
attribution over a captured log; `TrimAnalysisGateAttributionTests` drives that
same seam from the managed test suite.
