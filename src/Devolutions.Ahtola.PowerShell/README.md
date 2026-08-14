# Devolutions.Ahtola.PowerShell

Binary PowerShell project that publishes the **Devolutions.Ahtola.Sqlite** module,
backed by Ahtola’s pure-managed SQLite stack (`Ahtola.Data.Sqlite`) instead of
Microsoft.Data.Sqlite / SQLitePCLRaw.

| Layer | Name |
| --- | --- |
| Project / assembly | `Devolutions.Ahtola.PowerShell` |
| Published module | `Devolutions.Ahtola.Sqlite` |
| CLR type namespace | `Ahtola.PSSqlite` (cmdlet surface is `*-AhtolaSqlite*`) |

## Scope

- A collision-resistant `*-AhtolaSqlite*` public command surface for managed SQLite operations.
- Targets PowerShell 7+ only (`net8.0` / `net9.0` / `net10.0`). No Windows PowerShell 5.1 / netstandard2.0 path.
- Adds managed operational cmdlets for connections, transactions, backups, schema
  inspection, maintenance, bulk copy, JSON/CSV table interchange, and Ahtola
  file-password rotation.
- `DataReader` is a backward-compatible name for a detached materialized result
  reader. It is not a live streaming reader and does not retain command or
  connection ownership.
- File-password cmdlets are available only for Ahtola's file-backed managed
  AES-256-GCM format. They do not support SQLCipher, SEE, or loadable
  extensions.

## Build / stage

```powershell
./build.ps1 pack-powershell
# -> artifacts/powershell-modules/Devolutions.Ahtola.Sqlite
```

Or build the project (staging runs after `net8.0` build):

```powershell
dotnet build ./src/Devolutions.Ahtola.PowerShell/Devolutions.Ahtola.PowerShell.csproj -c Debug -f net8.0
```

## Import

```powershell
Import-Module ./artifacts/powershell-modules/Devolutions.Ahtola.Sqlite
Get-Command -Module Devolutions.Ahtola.Sqlite
```

No native SQLite assets are required; PreLoadTypes loads the managed Ahtola assemblies from `bin/`.

## Tests

Library (NUnit):

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
  -Framework net10.0 `
  -Filter "FullyQualifiedName~PSSqliteModuleTests" `
  -MinimumExecutedTests 1
```

Module (Pester 6):

```powershell
./build.ps1 test-powershell
# or:
pwsh ./scripts/Invoke-PowerShellModuleTests.ps1
```

## Notes

- `PowerShellStandard.Library` is compile-only; the PowerShell host supplies real `System.Management.Automation` at import time. Unit tests fall back to `OrderedDictionary` when SMA is absent.
- New scripts should use `-Connection`, `-Configuration`, `-Table`, `-Values`,
  and `-Where`. The former `-SqliteConnection`, `-SqliteDBConfig`,
  `-TableName`, `-RowData`, and `-ClauseData` names remain aliases for
  compatibility.
- A connection passed to a cmdlet is caller-owned: a cmdlet may open it, but
  never closes or disposes it. `New-AhtolaSqliteConnection` returns an open
  connection, and `Close-AhtolaSqliteConnection` is the explicit disposal
  command.
- `Invoke-AhtolaSqliteQuery` emits `PSCustomObject` rows by default. Use
  `-As Scalar` or `-As NonQuery` for direct values/counts and `-As DataTable`
  or `-As DataSet` only when those ADO.NET containers are required.
- `New-AhtolaSqliteConnection -TursoUrl <libsql-url> -AuthToken <SecureString>`
  opens a direct Turso Cloud connection. Add `-ReplicaPath <file>` for a
  managed embedded replica, optionally with `-SyncInterval <seconds>`, then
  use `Invoke-AhtolaSqliteReplicaSync` for an explicit sync. `-UseTursoEnvironment`
  opts into `TURSO_REMOTE_URL` and `TURSO_AUTH_TOKEN` defaults; explicit
  parameters take precedence. Cloud connections expose only a redacted
  connection string and never serialize their token.
- `Export-AhtolaSqliteTable` and `Import-AhtolaSqliteTable` infer `Json` or
  `Csv` from the file extension when `-Format` is omitted. Export can select a
  table or a parameterized `-Query`.
- Destructive cmdlets implement `SupportsShouldProcess`, so use `-WhatIf` to
  preview database writes, imports, backups, maintenance, and password changes.
