# Ahtola performance benchmarks

This is the canonical BenchmarkDotNet suite for Ahtola. It compares equivalent
managed Ahtola workloads with `Microsoft.Data.Sqlite` in the same run and emits
machine-readable results for Ahtola-to-Ahtola historical comparisons.

The workload inventory and upstream provenance are documented in
[`docs/turso-benchmark-portability.md`](../../docs/turso-benchmark-portability.md).
Rust-only benchmarks are not built or executed.

## Run profiles

Run from the repository root with PowerShell 7:

```powershell
./build.ps1 benchmark -BenchmarkProfile smoke
./build.ps1 benchmark -BenchmarkProfile write-short
./build.ps1 benchmark -BenchmarkProfile coverage
./build.ps1 benchmark -BenchmarkProfile full
./build.ps1 benchmark -BenchmarkProfile large
./build.ps1 benchmark -BenchmarkProfile diagnostic -BenchmarkFilter '*Write*'
```

`smoke` executes every case once and produces no useful timing. `write-short`
is the normal write-path development loop. `coverage` uses BenchmarkDotNet's
Short job across the suite. `full` uses the default statistically adaptive job.
`large` selects benchmarks explicitly categorized as `Large`. `diagnostic`
collects an EventPipe CPU trace for a narrow filter.

Results are written below `artifacts/benchmarks/<run-id>/`, including raw BDN
reports, normalized JSON, environment metadata, and a historical comparison
when `-BenchmarkBaseline` names a prior normalized result.

## Interpretation

- Native SQLite ratios provide context; they are not a regression gate.
- Compare Ahtola history only on equivalent hardware, OS, runtime, journal
  mode, durability mode, and fixture version.
- Statement-only cases exclude fixture creation, connection open, and reset.
  End-to-end and recovery cases include those costs intentionally.
- `Allocated` is managed allocation per operation. File, WAL, SHM, and logical
  log sizes are returned or recorded by macrobenchmarks where they matter.
- Default parameter sets are suitable for development and scheduled coverage.
  Expensive upstream scales are isolated in the `Large` category.

Before trusting a new benchmark, run `smoke`, confirm SQLite/Ahtola result
equivalence, inspect its generated report, and ensure repeated invocations do
not accumulate mutable state unless growth is the workload under test.
