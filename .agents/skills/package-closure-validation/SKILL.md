---
name: package-closure-validation
description: How the Ahtola packages are structured and validated (Ahtola.Data embedding, EFCore version-range constraint, validate-package/validate-packed-closure gates). Use this before changing package layout, csproj packaging metadata, or EF Core dependencies.
---

# Package closure and validation

The shipped packages are `Devolutions.Ahtola.Core`,
`Devolutions.Ahtola.Data.Sqlite`, and
`Devolutions.Ahtola.EntityFrameworkCore.Sqlite`. Package layout is
intentional and enforced by two gates.

## The embedding: Ahtola.Data is not a standalone package

`Ahtola.Data` (ADO.NET core, provider dispatch, Hrana remote client) has
`IsPackable=false`. It is **embedded into `Devolutions.Ahtola.Data.Sqlite`**
via an `AddProjectReferencesToPackage` `BuildOutputInPackage` target — its
output is compiled *into* the shipped provider nupkg. Consequences:

- Do **not** turn `Ahtola.Data` into its own nupkg.
- Anything `Ahtola.Data` references ends up in the shipped provider, so it
  must be pure-managed and AOT/trim-clean (see `pure-managed-closure` and
  `nativeaot-trim-safe`).
- Do not add a `PackageReference` to `Ahtola.Data` from a consumer; depend on
  `Devolutions.Ahtola.Data.Sqlite`.

## EF Core version constraint

`Directory.Build.props` centralizes:
- `$(AhtolaEntityFrameworkCoreVersion)` = `10.0.10` on net10.0 / `9.0.9` elsewhere
- `$(AhtolaEntityFrameworkCoreVersionRange)` = `[10.0.0,11.0.0)` on net10.0 /
  `[9.0.9,10.0.0)` on net8.0/net9.0

The closure validator enforces that
`Devolutions.Ahtola.EntityFrameworkCore.Sqlite` declares exactly **one**
`Microsoft.EntityFrameworkCore.Sqlite.Core` dependency per framework with the
matching range. If you bump EF Core, update the props and the validator's
expectation together in one change.

## The gates

```powershell
./build.ps1 validate-package          # pack + consumer restore/build/run/publish across net8/9/10
./build.ps1 validate-project-closure   # regex-scan project files for native/Rust refs
./build.ps1 validate-packed-closure    # validate built .nupkg contents/assets/publish
```

`validate-project-closure` runs `Assert-ManagedProjectClosure` (project-file
scan). `validate-packed-closure` runs
`scripts/Validate-ManagedPackageClosure.ps1` against built `.nupkg` entries,
`project.assets.json`, and publish output — catching native
`runtimes/`/`native/` assets that slip in transitively.

## Rules

- Keep `IsPackable=false` on `Ahtola.Data`. If you think it should ship
  standalone, that is a design change — discuss first.
- Don't add `Content`/`None` includes that pull `runtimes/<rid>/native/`
  assets or the `turso-src/` submodule into a shipped project.
- The packaged-consumer gate `samples/ManagedPackageConsumer` exercises the
  real nupkg restore/build/run/publish path — don't bypass it by testing only
  project references.
