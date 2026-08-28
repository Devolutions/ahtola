# Managed testing capabilities

Ahtola's managed test suite includes deterministic differential, conformance,
storage-fault, model-based, and concurrency testing derived from the testing
architecture in the pinned Turso source.

## Running the suite

Use the wrapper so an empty or unexpectedly small run fails:

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
    -Framework net10.0 `
    -MinimumExecutedTests 2500
```

Run the coverage ratchet with:

```powershell
pwsh ./build.ps1 test-coverage -Framework net10.0
```

The normal Windows test lane remains authoritative for the complete corpus
and migration stress coverage. The duplicate instrumented lane excludes the
`CoverageExcluded` category because Coverlet changes lock-test timing and
instrumenting the deterministic 10,000-row sqltest fixtures is prohibitively
slow. Those exclusions are category-based and remain fully executed by the
ordinary managed suite.

## Deterministic seeds and replay

Generated tests use a stable SplitMix64 implementation rather than
`System.Random`, so a seed produces the same operation sequence across .NET
runtime versions. Override the root seed when reproducing a failure:

```powershell
$env:AHTOLA_TEST_SEED = '0x1234'
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
    -Framework net10.0 `
    -Filter 'FullyQualifiedName~OracleFoundationTests' `
    -MinimumExecutedTests 6
```

On failure, generated tests write JSON and SQL replay artifacts below the
NUnit work directory and register them as test attachments. The JSON records
the root and derived seeds, SQL operations, actor and dependency metadata,
and deterministic schedule choices. The dependency-aware minimizer can remove
irrelevant operations while retaining the same normalized failure.

## Differential and metamorphic testing

`src/Ahtola.Tests/Oracle/` provides:

- typed SQLite values for NULL, INTEGER, REAL, TEXT, and BLOB;
- ordered and duplicate-preserving unordered row comparison;
- normalized SQLite error categories and codes;
- table, schema, and `integrity_check` snapshots;
- stable seeded streams and replay artifacts.

`OracleFoundationTests` compares generated SQL against
`Microsoft.Data.Sqlite` in the host test process. Pure-managed metamorphic
tests cover ternary-logic partitioning, indexed versus `NOT INDEXED` scans,
failed-statement atomicity, savepoint restoration, and `UNION ALL`
cardinality.

## SQL conformance

The sqltest runner supports Turso's deterministic `:default:` and
`:default-no-rowidalias:` fixtures, multiple database variants, and
`@cross-check-integrity`. The vendored corpus remains read-only. Engine gaps
are recorded in
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`.

## Storage and crash testing

Reusable test infrastructure under `src/Ahtola.Tests/Infrastructure/`
includes:

- deterministic queued async I/O with selectable completion order;
- path- and occurrence-specific I/O failures plus operation history;
- separate live and durable file images;
- power-loss restoration and deterministic torn flushes.

The storage reliability tests verify durable WAL commits, rejection of
volatile/uncommitted tails, checkpoint committed-prefix atomicity, sidecar
lifecycle, and out-of-order asynchronous completion.

## Model and concurrency testing

The model suites include:

- a bounded independent LRU model for pager-cache operations;
- a three-actor transaction shadow model with snapshots, pending changes,
  savepoints, checkpoints, and reopen;
- deterministic cooperative scheduling with replayable named yield points;
- bounded depth-first and stable-random schedule exploration;
- observer checks for commit persistence, rollback invisibility, snapshot
  consistency, completion, and livelock.

These tests control only explicit test actors and test I/O. They do not depend
on timing sleeps or attempt to intercept arbitrary CLR synchronization.

## Logical database fingerprints and processes

The logical fingerprint utility hashes canonical user schema and typed,
ordered table contents independently of physical page layout. It is suitable
for backup, vacuum, migration, and replication assertions.

The process lifecycle trace records child-process start, operation, exit,
timeout, and restart events as JSON Lines with deterministic sequence numbers.
Process tests use bounded waits and attach the trace when they fail.
