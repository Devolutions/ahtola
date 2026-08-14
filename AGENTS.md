# Copilot instructions for Ahtola .NET

Ahtola is an experimental, pure-managed (C#) SQLite-compatible database engine
vibe-ported from Turso's Rust core. There is **no native companion, no Rust
toolchain, and no P/Invoke SDK** anywhere in this tree. Treat that invariant as
a hard constraint when changing build/package configuration.

## Referencing the Turso source

A read-only `turso-src` git submodule pins the upstream Turso Rust core at a
specific release tag (currently `v0.7.2`, commit `046e9cbf6`) so agents can
read the original Rust sources while porting or comparing behavior, without
cloning ad hoc or guessing at API shape.

```powershell
git submodule update --init --recursive      # first checkout / fresh clone
git submodule update --remote turso-src      # bump to a newer tag/main (see below)
```

When working in this repo:

- Treat `turso-src/` as **read-only reference material**. Never edit files
  there; it is a vendored snapshot of `tursodatabase/turso`, not part of this
  build. Nothing under `turso-src/` is compiled or shipped by Ahtola.
- Prefer it over fetching Turso sources from the web: grep
  `turso-src/core/`, `turso-src/sqlite/`, `turso-src/sync/`, etc. directly to
  find the Rust type/function a C# port mirrors. Cross-reference when a C#
  type's doc comment or the WAL contract names an upstream symbol.
- The submodule pointer is the source of truth for "which Turso version this
  port targets." If you need a newer release, bump the submodule to that tag
  (see below) in the same change that ports the corresponding behavior, and
  note the new tag in the commit message.
- Keep `turso-src/` out of packaging: it must never appear in a nupkg, a
  `Content`/`None` include, or the managed-closure scan. The closure
  validator's native-archive pattern already rejects `runtimes/`/`native/`
  entries; do not add the submodule to any shipped project's item groups.

### Bumping the submodule to a newer release

```powershell
cd turso-src
git fetch --tags origin
git checkout <new-tag>            # e.g. v0.8.0
cd ..
git add turso-src                  # records the new Subproject commit
```

Then update the tag reference in this file and in any commit message. Prefer
release tags over `main` so the reference is reproducible. Do not commit the
submodule pointing at a moving branch tip in a release.

## Build, test, and lint

`build.ps1` (PowerShell 7+) is the canonical entrypoint; there is no Makefile.

```powershell
./build.ps1 restore                    # dotnet restore for the two package roots
./build.ps1 build                      # closure check + restore + build (Debug)
./build.ps1 test                       # full gate: pack, validate, run suite
./build.ps1 pack                       # pack Release nupkgs -> ./artifacts/managed-packages
./build.ps1 validate-package           # pack + consumer restore/build/run/publish across net8/9/10
./build.ps1 validate-project-closure   # regex-scan project files for native/Rust refs
./build.ps1 validate-packed-closure     # validate built .nupkg contents
./build.ps1 format-check                # dotnet format --verify-no-changes
```

Common parameters: `-Configuration Debug|Release` (default `Debug`),
`-Framework net10.0` (default; also `net8.0`/`net9.0`), `-PackageVersion …`,
`-PackageOutput ./artifacts/managed-packages`, `-MinimumExecutedTests 2500`.

`build.ps1 build` always runs the managed-closure check first; it will fail the
build if any `.csproj`/`.props`/`.targets`/`.slnx` references native Ahtola
packages, P/Invoke, or Rust tooling.

### Scripting: cross-platform PowerShell 7

All shell scripting in this repo is **cross-platform PowerShell 7+**
(`pwsh`), not bash/cmd and not Windows PowerShell 5.x. `build.ps1` and
everything under `scripts/` assume `pwsh` runs identically on Windows, Linux,
and macOS. When writing or editing scripts:

- Target `pwsh` 7+ and avoid Windows-only assumptions (no `cmd.exe` chaining,
  no `$env:ComSpec`, no backslash-only paths in string literals that flow to
  cross-platform tools — use `Join-Path`/`Split-Path`).
- Do not introduce bash/sh scripts as build entrypoints; there is no Makefile
  by design. If a CI lane needs a shell, call `pwsh ./build.ps1 …`.
- Avoid aliases that differ across hosts (`wget`, `curl`, `sed`); use
  PowerShell cmdlets (`Invoke-WebRequest`, `Get-Content`, `-replace`) so the
  same script works everywhere.
- Keep line endings consistent with the repo (CRLF is normal here); don't
  mix LF shell scripts into a CRLF tree.

### Running a single test or filtered subset

Prefer the wrapper so the run is still proven to have executed (it parses TRX
and fails on empty/silent runs):

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
  -Framework net10.0 `
  -Filter "FullyQualifiedName~AhtolaEncryptedStorageTests" `
  -MinimumExecutedTests 1
```

Or directly with the SDK (no execution-count guard):

```bash
dotnet test src/Ahtola.Tests/Ahtola.Tests.csproj -c Debug -f net10.0 \
  --filter "FullyQualifiedName~AhtolaEncryptedStorageTests"
```

The EF Core provider version is pinned. Build the whole graph with:

```bash
dotnet build Ahtola.slnx -c Release
```

`scripts/Invoke-ManagedTestSuite.ps1` supports `-KnownGapFailurePattern` +
`-KnownGapReason` (tolerate a documented platform gap by message),
`-RequirePassingClass`/`-RequireDiscoveredClass`, `-HangTimeoutMinutes`
(`--blame-hang`), and `-DenyNativeToolchain` (shims `cargo`/`rustc` to fail,
proving the managed lane does not shell out to Rust). Use these when adding a
new platform leg, not as a way to silence real failures.

## High-level architecture

Four logical layers, each a project under `src/`:

- **`Ahtola.Core`** — the engine. Subdivided into `Storage` (pager, WAL, b-tree,
  page allocator, overflow, encryption), `Parsing`, `Compilation` (VDBE-style
  program build), and `Execution`. This is the only project that touches the
  on-disk SQLite format. AOT-compatible and trimmable; `AllowUnsafeBlocks`.
- **`Ahtola.Data`** — ADO.NET core (`AhtolaConnection`/`AhtolaCommand`/…,
  connection pooling, local/remote/replica provider dispatch, Hrana remote
  client). `IsPackable=false`: it is **not** shipped as its own nupkg. It is
  embedded into `Devolutions.Ahtola.Data.Sqlite` via a
  `BuildOutputInPackage` target (`AddProjectReferencesToPackage`). Do not turn
  it into a standalone package.
- **`Ahtola.Data.Sqlite`** — the shipped ADO.NET provider and the
  `Microsoft.Data.Sqlite`-compatible facade (`SqliteConnection` etc. in
  namespace `Ahtola.Data.Sqlite`). This is the package consumers add.
- **`Ahtola.EntityFrameworkCore.Sqlite`** — EF Core provider (9.x on net8/net9,
  10.x on net10); entry point is `AhtolaDbContextOptionsBuilderExtensions`
  (`UseAhtola`).

Two non-shipped projects for perf: `src/Benchmarks` and
`src/ConsumerBenchmarks`. `samples/ManagedPackageConsumer` is the packaged-
consumer gate driven by `build.ps1 validate-package`.

The solution file is `Ahtola.slnx` (the XML `.slnx` format, not `.sln`).

### Conformance suite

`conformance/sqlite-sqltests/` is a vendored, read-only corpus of `.sqltest`
files (do not edit to fix tests; fix the engine). The runner lives in
`src/Ahtola.Tests/Sqltest/` (`SqltestParser`, `SqltestCorpus`,
`SqltestManagedRunner`). Cases the managed engine does not yet satisfy are
listed in `src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`
(format: `<file>::<test> | <summary>`). Regenerate that file with the
`RegenerateExpectedFailures` filter (see the header comment in the file
itself). When you close a gap, remove the corresponding line — do not leave
passing cases listed as expected failures.

## Key conventions

### Pure-managed closure is enforced, not aspirational

Two scripts police the "no native/Rust" invariant; both run during normal
build/pack/validate flows:

- `build.ps1` → `Assert-ManagedProjectClosure` regex-scans
  `Directory.Build.props`/`.targets`, `Ahtola.slnx`, and every `.csproj`/`
  .props`/`.targets` under `src/Ahtola.*` and `samples/ManagedPackageConsumer`
  against `$NativeLeakPattern` (matches `Ahtola.Raw`,
  `Ahtola.Data.(Native|Sync)`, `Ahtola.Data.Sqlite.(Native*|Sync)`, `cargo`,
  `rustc`, `cargo-ndk`, `turso_sdk_kit`, `DirectPInvoke`, `NativeLibrary`,
  `DllImport`, `LibraryImport`, `TursoUseStaticNativeLibrary`).
- `scripts/Validate-ManagedPackageClosure.ps1` validates built `.nupkg`
  entries, `project.assets.json`, and publish output against the same idea
  plus a native-archive-entry pattern (`runtimes/`, `native/`,
  `Ahtola.Raw.dll`, `libAhtola_sdk_kit.*`, etc.).

Consequence: do **not** add `PackageReference`s to `Ahtola.Raw`,
`Ahtola.Data.Native`, `Ahtola.Data.Sync`, or any `Turso.*` companion, and do
not add `DllImport`/`LibraryImport` to shipped library code. The only
intentional OS P/Invoke is inside `Ahtola.Core/Storage` for page/WAL locks —
that is engine code, not an SDK binding, and stays.

### NativeAOT and trimming compatibility is required

`Ahtola.Core` (the engine) is **NativeAOT-compatible and trimmable**, and the
rest of the stack must stay that way: the shipped provider and EF Core packages
are expected to publish and trim cleanly on `net8.0`/`net9.0`/`net10.0`. Treat
this as a hard constraint on every change to shipped library code, not an
aspiration. When generating or editing code, do **not** introduce patterns
that break AOT/trim analysis:

- No reflection-based serialization, deserialization, or type discovery that
  the trimmer cannot see (e.g. `Type.GetType` of a name built at runtime,
  unannotated `System.Text.Json` source generators, `Activator.CreateInstance`
  of dynamically named types). Prefer generated/source-based alternatives.
- No `MakeGenericMethod`/`MakeGenericType` over types/methods constructed at
  runtime; use reified generic instantiations the compiler can root.
- Avoid `dynamic`, runtime codegen (`Expression.CompileToDynamicMethod`),
  and `Assembly.Load`/`Type.GetType` string lookups in shipped code paths.
- Annotate reflection that *is* intentional with
  `[DynamicallyAccessedMembers]` so the trimmer can follow it; never suppress
  IL2050/IL2060/IL2070 analyzers with `UnconditionalSuppressMessage` to hide
  a real hole.
- Keep `AllowUnsafeBlocks` usage in `Ahtola.Core` to raw buffer/page work;
  do not add unsafe just to bypass a managed API.
- Do not add dependencies that are known AOT/trim-hostile (e.g. heavy
  reflection-only libraries) to shipped projects. `Ahtola.Data` is embedded
  into the shipped provider, so anything it references must also be AOT-clean.

If a change can only be made to work by disabling AOT or trimming, that is a
design problem — stop and discuss it rather than landing it. The pure-managed
closure scan does not catch AOT/trim violations automatically, so review new
reflection against this list yourself before committing.

### Multi-targeting and version pins

`Directory.Build.props` defines shared properties consumed by every project:

- `$(AhtolaTargetFrameworks)` = `net8.0;net9.0;net10.0` — use this MSBuild
  property in csproj `<TargetFrameworks>`, do not hard-code frameworks.
- `$(AhtolaEntityFrameworkCoreVersion)` defaults to `10.0.10` (net10) / `9.0.9`
  (older TFMs) and `$(AhtolaEntityFrameworkCoreVersionRange)` is
  `[10.0.0,11.0.0)` on net10.0 / `[9.0.9,10.0.0)` elsewhere.
- `Version`, `Company`, `Product`, `Authors`, `Copyright` are centralized here.

The closure validator enforces that `Devolutions.Ahtola.EntityFrameworkCore.Sqlite`
declares exactly one `Microsoft.EntityFrameworkCore.Sqlite.Core` dependency
per framework with the matching range above. If you bump EF Core, update the
props and the validator's expectation together.

### Naming / packaging layers

| Layer | Value |
| --- | --- |
| NuGet PackageId | `Devolutions.Ahtola.*` |
| Assemblies | `Devolutions.Ahtola.*` |
| Namespaces / types | `Ahtola.*` (`AhtolaConnection`, `UseAhtola`, …) |
| Project folders | `src/Ahtola.*` |

Keep types in `Ahtola.*` namespaces. The shipped package IDs are
`Devolutions.Ahtola.Core`, `Devolutions.Ahtola.Data.Sqlite`, and
`Devolutions.Ahtola.EntityFrameworkCore.Sqlite`.

### Tests

- Framework: **NUnit 4.x** with **AwesomeAssertions** (not Shouldly/Fluent).
  `using NUnit.Framework;` is a global `Using` in `Ahtola.Tests.csproj`, so
  test files do not redeclare it.
- The test project multi-targets the same `$(AhtolaTargetFrameworks)` so the
  libraries are exercised on every framework, not just the newest.
- `dotnet test` returns 0 for an empty run; that is why the wrapper parses TRX
  and enforces `-MinimumExecutedTests`. When adding a new test leg, route it
  through `Invoke-ManagedTestSuite.ps1` rather than bare `dotnet test` in CI.
- SQLite conformance gaps belong in the expected-failures file, not in
  `[Ignore]` attributes scattered across tests.

### Intentional Turso references (do not "clean up" blindly)

String references to `Turso.*` appear in a few places on purpose: the WAL
interoperability contract (`docs/wal-interoperability-contract.md`) describes
Turso's Rust engine as the interop *target*, and `AhtolaNativeProvider`/
`AhtolaReplicaProvider`/`SqliteNativeProvider` load optional companion
assemblies by name (`Turso.Data.Native`, `Turso.Data.Sync`) and fail closed
when absent. These companions are **not shipped** from this repo. Changing
those strings is a product decision, not a refactor — confirm before renaming.
