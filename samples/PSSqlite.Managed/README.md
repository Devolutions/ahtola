# PSSqlite.Managed

A minimal PowerShell 7 module sample that wires PowerShell directly to the
fully **managed** `Devolutions.Ahtola.Data.Sqlite` provider — there are **no native
`e_sqlite3`/SQLitePCLRaw binaries** involved anywhere in this sample. Every
connection opts into `Local Provider=Managed`, so all reads/writes go through
Ahtola's managed storage engine.

Assembly file names and C# namespaces still use the `Ahtola.*` prefix (Phase 1
compatibility). Package IDs are `Devolutions.Ahtola.*`.

This is a self-contained sibling to [`ManagedPackageConsumer`](../ManagedPackageConsumer),
demonstrating a vendoring pattern for PowerShell module authors: build once
with the .NET SDK to fetch and copy the managed Ahtola assemblies into the
module folder, then import the module anywhere pwsh 7 runs (Windows or Linux)
without needing the .NET SDK or any native SQLite binary at import time.

## What's here

- `nuget.config` — adds a local NuGet feed pointing at
  `ahtola/artifacts/nupkg` (where locally packed
  `Devolutions.Ahtola.Data.Sqlite` packages live), alongside nuget.org.
- `PSSqlite.Managed.csproj` — a `net8.0` helper project (the lowest TFM the
  package ships) that references `Devolutions.Ahtola.Data.Sqlite` and, after
  building, copies `Ahtola.Core.dll`, `Ahtola.Data.dll`, and
  `Ahtola.Data.Sqlite.dll` into `source/lib/net8.0/`.
- `source/PSSqlite.Managed.psd1` — the module manifest (PowerShell 7+,
  `RootModule = 'PSSqlite.Managed.psm1'`,
  `ScriptsToProcess = 'ScriptsToProcess\PreLoadTypes.ps1'`).
- `source/PSSqlite.Managed.psm1` — the root module, exporting:
  - `New-ManagedConnection` — opens a
    `Data Source=:memory:;Cache=Shared;Local Provider=Managed` connection.
  - `Invoke-ManagedQuery` — runs a command against an open connection and
    returns rows as `PSCustomObject`s.
  - `Start-ManagedSample` — end-to-end demo: creates a metadata table,
    inserts a row, reads it back, prints it, then calls
    `[Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()` on close.
- `source/ScriptsToProcess/PreLoadTypes.ps1` — loads the three vendored
  assemblies via `[System.Reflection.Assembly]::LoadFrom()` from
  `source/lib/net8.0`, in dependency order: `Ahtola.Core` → `Ahtola.Data` →
  `Ahtola.Data.Sqlite`. No native library, no PATH/RID resolution, no net48
  branch. Throws a clear error if any DLL is missing (run `build.ps1` first).
- `build.ps1` — runs `dotnet build`, which triggers the restore + vendor
  copy, and prints where the DLLs landed.

## Build

```powershell
# From ahtola/, pack packages into artifacts/nupkg first (or point nuget.config
# at your feed), then:
./build.ps1
```

This restores `Devolutions.Ahtola.Data.Sqlite` from the local feed and vendors
`Ahtola.Core.dll`, `Ahtola.Data.dll`, and `Ahtola.Data.Sqlite.dll` into
`source/lib/net8.0/`.

## Import and run the demo

```powershell
Import-Module ./source/PSSqlite.Managed.psd1
Start-ManagedSample
```

## Notes

- **No native SQLite binaries.** This sample never restores or loads
  `e_sqlite3`, `SQLitePCLRaw`, or `Microsoft.Data.Sqlite` — only the three
  managed Ahtola assemblies.
- **PowerShell 7+ only** (`#Requires -Version 7.0`, `CompatiblePSEditions =
  'Core'`).
- **net8.0** is the target framework for the vendoring helper project — it's
  the lowest TFM the `Devolutions.Ahtola.Data.Sqlite` package ships (`net8.0`,
  `net9.0`, `net10.0`).
- Loads on Windows and Linux amd64 pwsh 7 without any native SQLite binary or
  platform-specific RID resolution, because `Assembly.LoadFrom` is used
  directly instead of relying on the .NET native asset resolver.
