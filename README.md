# Ahtola .NET

An experimental pure managed (C#) port of [Turso](https://turso.tech)’s
SQLite-compatible database engine, with [ADO.NET](https://learn.microsoft.com/dotnet/framework/data/adonet/) and [EF Core](https://learn.microsoft.com/efcore/) providers.

> ⚠️ **Experimental project.** Ahtola is a research / prototype engine and is
> **not** production-ready. For production .NET workloads, use the official
> bindings to the original Turso Rust core at
> [tursodatabase/turso](https://github.com/tursodatabase/turso).

Ahtola is a C# engine that reads and writes SQLite’s on-disk format directly —
automatically vibe-ported from Turso’s Rust core, as a fun experiment. It is
**not** a binding over native SQLite or over any Rust core — no native
companion, P/Invoke SDK, or Rust toolchain is required to restore, build, pack,
or run.

- [Install](#install) ([full guide](docs/dotnet-packages.md))
- [Quick start](#quick-start)
- [PowerShell module](#powershell-module) ([full guide](docs/powershell-module.md))
- [What this is good for](#what-this-is-good-for)
- [Important limits](#important-limits)
- [Building from source](#building-from-source)

## Install

```bash
dotnet add package Devolutions.Ahtola.Data.Sqlite
# optional EF Core provider (9.x on net8/net9, 10.x on net10):
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite
```

Targets: `net8.0`, `net9.0`, `net10.0`. No `net48` / .NET Framework assets.

| Package | Role | NuGet |
| --- | --- | --- |
| `Devolutions.Ahtola.Core` | Managed engine | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.Core) |
| `Devolutions.Ahtola.Data.Sqlite` | ADO.NET provider + `Microsoft.Data.Sqlite`-compatible facade; embeds `Ahtola.Data` | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.Data.Sqlite) |
| `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | EF Core provider (`UseAhtola`) | [nuget.org](https://www.nuget.org/packages/Devolutions.Ahtola.EntityFrameworkCore.Sqlite) |

`Devolutions.Ahtola.Core` flows in transitively via `Devolutions.Ahtola.Data.Sqlite`
— most consumers never add it directly unless they implement an `IPageCodec`
or touch `Ahtola.Core.Storage` types directly.

| Layer | Name |
| --- | --- |
| NuGet PackageId | `Devolutions.Ahtola.*` |
| Assemblies | `Devolutions.Ahtola.*` |
| Namespaces / types | `Ahtola.*` (`AhtolaConnection`, `UseAhtola`, …) |
| Project folders | `src/Ahtola.*` |

For connection strings, Turso Cloud (direct + managed embedded replica),
concurrent writes (MVCC), encryption, and EF Core in more depth, see the
[**.NET packages guide**](docs/dotnet-packages.md).

## Quick start

**SQLite-compatible facade** (drop-in `using` swap from Microsoft.Data.Sqlite):

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

**Ahtola types** (same package):

```csharp
using Ahtola;

using var connection = new AhtolaConnection("Data Source=:memory:");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a, b)");
// AhtolaConnection, AhtolaCommand, AhtolaParameter, AhtolaFactory.Instance, …
```

**EF Core:**

```csharp
options.UseAhtola("Data Source=app.db");
```

Common connection-string keywords: `Data Source`, `Mode`, `Cache`, `Pooling`,
`Foreign Keys`, `Default Timeout` / `Command Timeout`, `Foreign Read Only`,
`DateTimeKind`, `BinaryGUID`, `Password` (passphrase → AES-256-GCM), or
`Encryption Cipher` + `Encryption Key` (hex AES-128/256-GCM). Default local
provider is managed-only.

### Standard SQLite files

Managed open of **unencrypted** SQLite databases created by System.Data.SQLite /
Microsoft.Data.Sqlite / native sqlite3 is supported (`Data Source=path` only;
no special flags). Ahtola is byte-compatible with the on-disk format for normal
read/write workloads.

### File encryption (not SEE / SQLCipher)

Encryption is layered so new recipes can be added without rewriting the pager:

| Layer | Role | Extension point |
| --- | --- | --- |
| **Passphrase scheme** | Password to AES key | `IAhtolaPassphraseScheme` + `AhtolaPassphraseSchemes`; CS `Password Scheme=` |
| **Built-in AHTLA page crypto** | On-disk AES-GCM pages (`AHTLA` header) | `AhtolaEncryptionOptions` / `Encryption Cipher` + `Encryption Key` |
| **External page codec** | Entirely different page layout | `IPageCodec` (mutually exclusive with built-in encryption) |

| Mechanism | Connection string | Notes |
| --- | --- | --- |
| Passphrase (explicit scheme) | `Password=secret;Password Scheme=Ahtola.Password.v1` | **Preferred** for apps (e.g. RDM). Scheme id is a stable KDF contract. |
| Passphrase (default scheme) | `Password=secret` | Same as `Ahtola.Password.v1` when `Password Scheme` is omitted |
| Raw key | `Encryption Cipher=Aes256Gcm; Encryption Key=<64 hex chars>` | Same on-disk AHTLA format |
| Rekey | `SqliteConnection.ChangePassword` / `ClearPassword` / `SetPassword` | Rewrite backup + atomic file replace; exclusive access |

Built-in scheme `Ahtola.Password.v1`: PBKDF2-HMAC-SHA256, fixed domain salt
`Ahtola.Password.v1`, 210k iterations to AES-256-GCM. Changing KDF bytes requires a
**new scheme id** (via `AhtolaPassphraseSchemes.Register` or a future built-in),
never a silent change to `v1`.

Do **not** combine `Password` and `Encryption Key`. Legacy SEE/SQLCipher files are
**not** opened by passphrase schemes — use a dedicated `IPageCodec` or
export/recreate under Ahtola password / plain SQLite.

Wrong/missing password failures include the phrase
`file is encrypted or is not a database` for SDS-shaped detection.

## PowerShell module

`Devolutions.Ahtola.Sqlite` is a binary PowerShell module that exposes the
Ahtola engine through `*-AhtolaSqlite*` cmdlets. Its implementation is ported
from synedgy.PSSqlite and re-backed onto `Ahtola.Data.Sqlite` instead of
Microsoft.Data.Sqlite / SQLitePCLRaw — so importing it pulls in **no native
SQLite assets**. The
public command noun is `AhtolaSqlite` to avoid collisions with other SQLite
PowerShell modules.

Requires PowerShell **7.4+**. Windows PowerShell 5.1 is not supported.

### Getting the module

Install it from the [PowerShell Gallery](https://www.powershellgallery.com/packages/Devolutions.Ahtola.Sqlite):

```powershell
Install-Module -Name Devolutions.Ahtola.Sqlite -Scope CurrentUser
```

Then import it from anywhere pwsh 7 runs — no native SQLite binary, no .NET SDK
needed at import time:

```powershell
Import-Module Devolutions.Ahtola.Sqlite
Get-Command -Module Devolutions.Ahtola.Sqlite
```

Model types are available as module-qualified type accelerators, e.g.
`[Devolutions.Ahtola.Sqlite.SqliteDBConfig]`.

### Cmdlets

| Cmdlet | Purpose |
| --- | --- |
| `New-AhtolaSqliteConnection` / `Test-AhtolaSqliteConnection` / `Close-AhtolaSqliteConnection` / `Clear-AhtolaSqliteConnectionPool` | Open, test, close/dispose, and explicitly clear managed connection pools |
| `Invoke-AhtolaSqliteQuery` | Run parameterized SQL; emits `PSCustomObject` rows by default and supports scalar, non-query, `DataTable`, `DataSet`, and detached-reader modes |
| `Start-AhtolaSqliteTransaction` / `Save-AhtolaSqliteTransaction` / `Complete-AhtolaSqliteTransaction` / `Undo-AhtolaSqliteTransaction` | Start, save, commit/release, or roll back managed transactions and savepoints |
| `Invoke-AhtolaSqliteBulkCopy` | Insert pipeline objects, dictionaries, or `DataRow` values in an all-or-nothing transaction |
| `Backup-AhtolaSqliteDatabase` | Copy one managed SQLite database into a distinct destination connection |
| `Get-AhtolaSqliteSchema` / `Get-AhtolaSqliteTable` / `Get-AhtolaSqliteIndex` / `Get-AhtolaSqliteDatabaseInfo` | Inspect provider schema, database objects, and database page/journal information |
| `Test-AhtolaSqliteIntegrity` / `Optimize-AhtolaSqliteDatabase` / `Checkpoint-AhtolaSqliteDatabase` / `Invoke-AhtolaSqliteMaintenance` | Run focused integrity, optimization, WAL checkpoint, and constrained maintenance operations |
| `Export-AhtolaSqliteTable` / `Import-AhtolaSqliteTable` | Move table data as portable JSON or CSV; this is distinct from a database backup |
| `Set-AhtolaSqlitePassword` / `Clear-AhtolaSqlitePassword` | Encrypt, rekey, or decrypt file-backed managed Ahtola databases using a `SecureString` passphrase |
| `Get-AhtolaSqliteRow` / `New-AhtolaSqliteRow` / `Set-AhtolaSqliteRow` / `Remove-AhtolaSqliteRow` | CRUD driven by a programmatically constructed `SQLiteDBConfig` + `-Table` (+ `-Values` / `-Where`); update/delete emit affected-row counts |
| `Get-AhtolaSqliteDatabaseMetadata` / `Compare-AhtolaSqliteDatabaseVersion` | Read stored metadata; compare deployed vs expected configuration version |

`New-AhtolaSqliteConnection` returns an open connection. Every cmdlet that
receives `-Connection` may open a closed connection but never closes or
disposes it. Configuration-driven CRUD creates and disposes its own temporary
connection only when `-Connection` is omitted. `-SqliteConnection`,
`-SqliteDBConfig`, `-TableName`, `-RowData`, and `-ClauseData` remain
compatibility aliases; use `-Connection`, `-Configuration`, `-Table`,
`-Values`, and `-Where` in new scripts.

`Invoke-AhtolaSqliteQuery` and the `Get-AhtolaSqliteRow` family support
`-As DataTable | DetachedDataReader | DataSet | OrderedDictionary |
PSCustomObject`; `Invoke-AhtolaSqliteQuery` additionally supports `Scalar` and
`NonQuery`. `DataReader` remains a compatibility alias for
`DetachedDataReader`: it is a materialized snapshot, not a live streaming
reader.

Bulk imports fail and roll back their own transaction on the first conflicting
row. When passed a caller-owned transaction, the cmdlet uses a savepoint and
rolls back only that bulk operation.

### Example

```powershell
# Ad hoc query and default PowerShell-object output
$connection = New-AhtolaSqliteConnection -ConnectionString 'Data Source=:memory:'
Invoke-AhtolaSqliteQuery -Connection $connection `
    -CommandText 'SELECT id, name FROM t WHERE name = $name' `
    -Parameters @{ '$name' = 'b' }

$transaction = Start-AhtolaSqliteTransaction -Connection $connection
Invoke-AhtolaSqliteQuery -Connection $connection -Transaction $transaction `
    -CommandText 'UPDATE Items SET Name = $name WHERE Id = $id' `
    -Parameters @{ '$name' = 'updated'; '$id' = 1 } -As NonQuery
Complete-AhtolaSqliteTransaction -Transaction $transaction

# Portable table export/import infers JSON or CSV from the file extension.
Export-AhtolaSqliteTable -Connection $connection -Table Items -Path ./items.json
Import-AhtolaSqliteTable -Connection $connection -Table Items -Path ./items.csv
$connection | Close-AhtolaSqliteConnection -ClearPool
```

If you'd rather call the ADO.NET provider from a plain script module instead of
using these cmdlets, see [samples/PSSqlite.Managed](samples/PSSqlite.Managed).

For a deeper cmdlet reference plus worked walkthroughs — a local SQLite file,
local concurrent writes with MVCC/`BEGIN CONCURRENT`, a direct Turso Cloud
connection, and a managed embedded replica — see
[docs/powershell-module.md](docs/powershell-module.md).

## What this is good for

- Fully managed local SQLite-format databases with **no native assets**
- Small-to-moderate workloads, in-process embedding, constrained deployment
- A familiar ADO.NET / MDS-shaped API and an EF Core provider

## Important limits

Treat Ahtola as SQLite-*compatible*, not a full SQLite replacement:

- **In-memory working set** — tables and intermediate results stay in the process
  heap; nothing spills to disk. Prefer modest databases and explicit transactions
  for writes (managed writes are slower than native SQLite and the gap grows with
  table size).
- **Planner** — `ANALYZE` / `sqlite_stat1` feed index scoring and limited join
  cost gates (selective outer for two-table INNER nested loops; equijoin hash
  build side). Full System-R DP join reordering and multi-index AND intersection
  are still deferred; OUTER JOIN order stays correctness-preserving. Prefer
  `ORDER BY` when order matters (`GROUP BY` is first-encounter order).
- **File-backed platforms** — Windows, 64-bit Linux, and macOS. In-memory works
  everywhere; other platforms (e.g. 32-bit Linux) throw
  `PlatformNotSupportedException` on physical open. macOS uses POSIX
  `fcntl(F_SETLK)` (process-associated locks, not Linux OFD); multi-engine
  claims on macOS need host verification.
- **Multi-engine files (Stage 6)** — physical opens use SQLite main-file SHARED
  locking (Windows / 64-bit Linux / macOS). Managed and stock SQLite can share
  the same live WAL database on Windows/Linux (`-shm` DMS + peer WAL visibility
  on new statements). Pooling may retain managed handles until `Pooling=False`
  or `SqliteConnection.ClearAllPools()`. PENDING/RESERVED DELETE-mode polish and
  a Turso binary differential remain optional depth. See
  [docs/wal-interoperability-contract.md](docs/wal-interoperability-contract.md).
- **Foreign read-only** — `Mode=ReadOnly;Foreign Read Only=True;Pooling=False`
  can read a DB still held by native SQLite/Turso (e.g. winget `index.db`) without
  taking main-file locks.
- **MVCC** — process-local `PRAGMA journal_mode=mvcc` + `BEGIN CONCURRENT` with
  dual-cursor SELECT/DML routing, logical log, and a checkpoint SM skeleton
  (`PRAGMA wal_checkpoint` in MVCC mode). Not cross-process; residual schema-
  cookie polish and full per-page b-tree checkpoint SM remain open — see
  [docs/mvcc-port-contract.md](docs/mvcc-port-contract.md).
- **Not implemented** — virtual tables / FTS / R-Tree, loadable extensions, raw
  `sqlite3*` handles (`Handle` is null), AEGIS encryption ciphers, sync engine /
  CDC, CREATE SEQUENCE, typed-value extensions.
- **Native / Sync companions** — not shipped. Connection-string paths that need
  them fail closed. OS P/Invoke in the pager for locks/WAL is intentional engine
  code, not a Rust SDK binding.
- **Remote Hrana** — optional pure-managed HTTP `/v2/pipeline` on
  `AhtolaConnection` (tests use a canned server). Not a cloud product surface.

Encryption format v0 uses a fixed 5-byte magic `AHTLA`, then version and cipher
id (AES-GCM page AEAD).

## Building from source

Requires the .NET SDK and PowerShell 7+:

```powershell
./build.ps1 build
./build.ps1 test
./build.ps1 pack              # -> ./artifacts/managed-packages
./build.ps1 pack-powershell   # -> ./artifacts/powershell-modules
./build.ps1 validate-runtime  # packed consumer trim + NativeAOT publish
```

Contributor details — the full task list, validation gates, conformance suite,
and repo layout — live in [AGENTS.md](AGENTS.md) and [docs/](docs).

## License

MIT — see [LICENSE](LICENSE).
