---
name: managed-test-suite
description: How to run NUnit tests and the conformance suite in Ahtola correctly, including running a single test and why bare `dotnet test` lies. Use this whenever you need to run, filter, or interpret test results.
---

# Managed test suite

Ahtola tests are **NUnit 4.x** with **AwesomeAssertions** (not
Shouldly/Fluent). `using NUnit.Framework;` is a global `Using` in
`Ahtola.Tests.csproj` — do not redeclare it in test files. The test project
multi-targets `$(AhtolaTargetFrameworks)` = `net8.0;net9.0;net10.0`.

Scale: ~2,950 `[Test]` + ~549 `[TestCase]` + 12 `[TestCaseSource]` NUnit entry
points, plus the 270-file / ~8,163-case SQLite conformance corpus.

## The wrapper, not bare `dotnet test`

`dotnet test` returns exit code 0 for an **empty** run (no tests matched the
filter). That silently lies. Always go through the wrapper, which parses TRX
and enforces a minimum executed count:

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 -Framework net10.0 -MinimumExecutedTests 2500
```

## Running a single test or subset

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 -Framework net10.0 `
    -Filter "FullyQualifiedName~MyTestClass.MyTest" -MinimumExecutedTests 1
```

`-Filter` is a NUnit filter expression (same as `dotnet test --filter`).
`-MinimumExecutedTests 1` is the guard that proves the filter actually matched
something.

## Useful parameters

- `-Framework net10.0` (default; also `net8.0`/`net9.0`) — run a specific leg.
- `-Configuration Debug|Release`.
- `-Filter "<nunit filter>"`.
- `-KnownGapFailurePattern` / `-KnownGapReason` — tolerate a documented
  platform gap by matching the failure message. Use when adding a new platform
  leg, not to silence real failures.
- `-RequirePassingClass` / `-RequireDiscoveredClass` — assert a named class
  ran/was discovered.
- `-HangTimeoutMinutes` — `--blame-hang` timeout for stuck runs.
- `-DenyNativeToolchain` — shims `cargo`/`rustc` to fail, proving the managed
  lane doesn't shell out to Rust.

## Full gate via build.ps1

`./build.ps1 test` runs the full gate: pack, validate, run suite. Use it for
the final check before pushing; use the wrapper directly for iteration.

## Conformance gaps

SQLite conformance gaps go in
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`, not in
`[Ignore]` attributes. See the `conformance-suite` skill.
