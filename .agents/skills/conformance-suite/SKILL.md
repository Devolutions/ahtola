---
name: conformance-suite
description: How the SQLite conformance corpus (`conformance/sqlite-sqltests/`) and the expected-failures file work, and how to close a conformance gap correctly. Use this when a sqltest fails or when closing an engine gap.
---

# Conformance suite

`conformance/sqlite-sqltests/` is a vendored, **read-only** corpus of 270
`.sqltest` files (~8,163 cases) that exercises the SQLite SQL surface. It is
the primary correctness signal that Ahtola matches SQLite/Turso behavior.

## Hard rules

- **Do not edit `.sqltest` files to fix tests.** Fix the engine. The corpus is
  upstream; editing it silently weakens correctness.
- Conformance gaps belong in the expected-failures file, **not** in `[Ignore]`
  attributes scattered across tests. Don't sprinkle `[Ignore]` to hide a gap.
- When you close a gap, **remove the corresponding expected-failure line** —
  do not leave a passing case listed as an expected failure.

## Runner

The runner lives in `src/Ahtola.Tests/Sqltest/`:
`SqltestParser` (parses `@database`, `@skip`, `test NAME { … } expect { … }`),
`SqltestCorpus` (loads the 270 files), `SqltestManagedRunner` (executes against
the managed engine).

## Expected-failures file

`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt` — one
line per known gap, format `<file>::<test> | <summary>` (see the header
comment in the file itself). When the engine does not yet satisfy a case, add
a line here with a short summary of why. When the engine catches up, delete
the line.

## Regenerating

Use the `RegenerateExpectedFailures` filter to regenerate the file from a full
run — but only after you have actually fixed or intentionally accepted the
gaps. Review the diff before committing; a regenerated file that "accidentally"
drops a still-failing case hides a regression.

## Closing a gap

1. Run the specific case: `pwsh ./scripts/Invoke-ManagedTestSuite.ps1 -Framework net10.0 -Filter "FullyQualifiedName~<case>" -MinimumExecutedTests 1`.
2. Fix the engine in `Ahtola.Core` (consult `turso-src/` for the upstream
   semantics — see the `turso-source-ref` skill).
3. Re-run the full conformance lane to confirm you didn't regress another case.
4. Remove the line from `managed-sqltest-expected-failures.txt`.
