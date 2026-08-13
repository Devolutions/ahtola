---
name: pure-managed-closure
description: The enforced "no native/Rust/P-Invoke" invariant of the Ahtola build. Use this before adding any PackageReference, dependency, or interop to shipped library code, and whenever a build fails with a managed-closure error.
---

# Pure-managed closure

Ahtola ships **no native companion, no Rust toolchain, no P/Invoke SDK**. Two
scripts police this automatically and run during normal build/pack/validate
flows. Treat any violation as a hard error, not a warning.

## What is scanned

`build.ps1` → `Assert-ManagedProjectClosure` regex-scans
`Directory.Build.props`/`.targets`, `Ahtola.slnx`, and every `.csproj`/
`.props`/`.targets` under `src/Ahtola.*` and `samples/ManagedPackageConsumer`
against `$NativeLeakPattern`.

`scripts/Validate-ManagedPackageClosure.ps1` validates built `.nupkg` entries,
`project.assets.json`, and publish output against the same idea **plus** a
native-archive-entry pattern (`runtimes/`, `native/`, `Ahtola.Raw.dll`,
`libAhtola_sdk_kit.*`, etc.).

## What the pattern rejects

`$NativeLeakPattern` matches, among others: `Ahtola.Raw`,
`Ahtola.Data.(Native|Sync)`, `Ahtola.Data.Sqlite.(Native*|Sync)`, `cargo`,
`rustc`, `cargo-ndk`, `turso_sdk_kit`, `DirectPInvoke`, `NativeLibrary`,
`DllImport`, `LibraryImport`, `TursoUseStaticNativeLibrary`.

## Rules

- Do **not** add `PackageReference`s to `Ahtola.Raw`, `Ahtola.Data.Native`,
  `Ahtola.Data.Sync`, or any `Turso.*` companion in shipped projects.
- Do **not** add `DllImport`/`LibraryImport` to shipped library code.
- Do **not** add a dependency whose transitive graph brings a
  `runtimes/<rid>/native/` asset into a shipped nupkg.
- The **only** intentional OS P/Invoke is inside `Ahtola.Core/Storage` for
  page/WAL byte-range locks and shared-memory mapping. That is engine code,
  not an SDK binding, and stays — do not spread it elsewhere.

## If the closure check fails

1. Read which file and which pattern matched (the error names both).
2. If it is a real leak (native ref, `DllImport`, Rust tooling), remove it —
   do not widen the pattern.
3. If it is the one allowed P/Invoke in `Ahtola.Core/Storage`, it is already
   exempt; the failure means you added a *new* one. Reconsider whether you
   actually need OS interop (you almost certainly don't).
4. Never silence the check with `#pragma`/`UnconditionalSuppressMessage` or by
   excluding a file from the scan to land a native dependency.

## Intentional Turso companion strings (do not "clean up")

`AhtolaNativeProvider`/`AhtolaReplicaProvider`/`SqliteNativeProvider` load
optional companion assemblies *by name* (`Turso.Data.Native`,
`Turso.Data.Sync`) and fail closed when absent. Those companions are **not
shipped from this repo**. The string match is intentional; renaming is a
product decision, not a refactor.
