# Consumer Read Benchmarks

BenchmarkDotNet microbenchmarks for read-heavy query shapes taken from real
Ahtola consumer projects. These exist to **guard against regressions** in the
managed (pure C#) Ahtola engine's read path over time — **not** to claim
performance parity with the native engine or with `Microsoft.Data.Sqlite`.

Per the top-level Readme, the managed engine is currently **10–56× slower
than native in transactional/write workloads**. These benchmarks deliberately
avoid that shape and instead pin **read-heavy** query patterns, so that CI/dev
runs can catch accidental read-path slowdowns without being swamped by the
already-known-and-accepted write-path gap.

## Benchmark shapes

1. **`CatalogSearch`** — modeled on the "pinget" (winget-catalog-search-style)
   consumer: a `JOIN` + `LIKE` + `LIMIT` query against a ~1,000-row
   `packages`/`versions` catalog.
   ```sql
   SELECT p.name, v.version
   FROM packages p
   JOIN versions v ON p.id = v.package_id
   WHERE p.name LIKE @term OR p.description LIKE @term
   LIMIT 20;
   ```
2. **`MetadataSelect`** — modeled on the "synedgy" (schema/metadata
   inspection) consumer: a `sqlite_schema` listing plus a
   `PRAGMA table_info`-style read against a small two-table schema.
3. **`PinsReadOnlyOpenAndList`** — modeled on the "pinget" pin-store list
   flow: opens a **read-only** connection against a pre-built file database
   and lists rows from a `pins` table. This benchmark's `[Benchmark]` method
   includes the connection-open cost, since "open + list" is the real
   consumer shape.

Each shape runs against both:

- **Ahtola managed** — `Devolutions.Ahtola.Data.Sqlite` with `Local Provider=Managed`
  (the pure C# engine).
- **Microsoft.Data.Sqlite** — the baseline oracle, using the native
  `Microsoft.Data.Sqlite` package.

The `Microsoft.Data.Sqlite` variant of each shape is marked `[Baseline = true]`
so BenchmarkDotNet reports a `Ratio` column comparing the managed engine
against it directly.

## Running

From the repository root (or this folder):

```powershell
dotnet run -c Release --project D:\dev\Ahtola-dotnet\bindings\dotnet\src\ConsumerBenchmarks\ConsumerBenchmarks.csproj --filter '*'
```

Useful variants:

```powershell
# Only the catalog search shape
dotnet run -c Release --project ConsumerBenchmarks.csproj --filter '*CatalogSearch*'

# List discovered benchmarks without running them
dotnet run -c Release --project ConsumerBenchmarks.csproj -- --list flat

# Quick smoke run (not for real numbers — use for discovery/sanity only)
dotnet run -c Release --project ConsumerBenchmarks.csproj --filter '*' -- --invocationCount 1 --iterationCount 1 --warmupCount 0
```

Full runs take several minutes because BenchmarkDotNet performs pilot,
warmup, and multiple measured iterations per benchmark. Do not run the full
suite in a shared/CI-critical-path context without accounting for that.

## What the numbers mean

- **`Mean` / `Error` / `StdDev`** — wall-clock time per operation. Compare a
  given Ahtola-managed benchmark's `Mean` release-over-release; a large
  regression (e.g. >20-30%) on a read-heavy shape is a signal worth
  investigating, since these shapes should be relatively cheap for the
  managed engine even though writes are not.
- **`Ratio`** — Ahtola-managed time ÷ Microsoft.Data.Sqlite baseline time for
  the same shape. This number is expected to be **> 1** (managed is slower)
  and is provided for context, not as a pass/fail gate. Do **not** treat
  `Ratio` improvements/regressions alone as a correctness signal — track the
  managed engine's own `Mean` over time as the primary regression guard.
- **`Allocated`** (via `[MemoryDiagnoser]`) — managed heap allocations per
  operation. Useful for catching accidental allocation regressions (e.g. a
  change that starts boxing values or building intermediate collections in
  a hot read path).

## Baseline numbers (fill in after each benchmark run)

Replace this table with the output of a full `dotnet run -c Release --filter '*'`
run. Record the machine, .NET SDK version, and Ahtola package version alongside
the numbers so future comparisons are meaningful.

| Benchmark | Mean | Error | StdDev | Ratio | Allocated |
|---|---|---|---|---|---|
| `CatalogSearch_AhtolaManaged` | _TBD_ | _TBD_ | _TBD_ | _TBD_ | _TBD_ |
| `CatalogSearch_MicrosoftDataSqlite` | _TBD_ | _TBD_ | _TBD_ | 1.00 | _TBD_ |
| `MetadataSelect_AhtolaManaged` | _TBD_ | _TBD_ | _TBD_ | _TBD_ | _TBD_ |
| `MetadataSelect_MicrosoftDataSqlite` | _TBD_ | _TBD_ | _TBD_ | 1.00 | _TBD_ |
| `PinsReadOnlyOpenAndList_AhtolaManaged` | _TBD_ | _TBD_ | _TBD_ | _TBD_ | _TBD_ |
| `PinsReadOnlyOpenAndList_MicrosoftDataSqlite` | _TBD_ | _TBD_ | _TBD_ | 1.00 | _TBD_ |

Environment used for baseline (fill in):

- Machine: _TBD_
- OS: _TBD_
- .NET SDK: _TBD_
- `Devolutions.Ahtola.Data.Sqlite` version: `0.8.0-pre.2`
- Date: _TBD_

## Notes on project setup

- Targets `net8.0` with `BenchmarkDotNet` (matching the existing
  `Benchmarks` project's BenchmarkDotNet version).
- `Devolutions.Ahtola.Data.Sqlite` is restored from the local feed configured in this
  folder's `nuget.config` (`../../artifacts/nupkg`), pinned to
  `0.8.0-pre.2`. Rebuild the package (`dotnet pack` for
  `Devolutions.Ahtola.Data.Sqlite`) before bumping this version.
- `Microsoft.Data.Sqlite` is restored from nuget.org as the baseline oracle.
- This project is intentionally separate from the existing `Benchmarks`
  project (which benchmarks the native engine via a `ProjectReference` and
  does not exercise the managed-provider connection-string contract). It is
  not wired into `Ahtola.slnx`; add a project reference there if you want it
  to build as part of the full solution.
