# Browser WebAssembly

`Devolutions.Ahtola.Data.Sqlite.Browser` runs Ahtola's managed SQL engine in
.NET WebAssembly and stores durable SQLite-compatible files in the browser's
Origin Private File System (OPFS). It is a Razor class library: the NuGet
package carries the JavaScript module and worker as static web assets and does
not ship a second database WebAssembly binary.

> Ahtola is experimental. Treat browser databases as application data that can
> be recreated or exported, and test browser eviction and quota behavior for
> your deployment.

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

## Async-only contract

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
| Synchronous API throws | Use the async counterpart and `await using` |
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
