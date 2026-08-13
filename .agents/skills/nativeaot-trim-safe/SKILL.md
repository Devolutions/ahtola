---
name: nativeaot-trim-safe
description: How to keep Ahtola.Core and the shipped provider/EF Core packages NativeAOT-compatible and trimmable. Use this when writing or reviewing any reflection, generics, serialization, or dynamic dispatch in shipped library code.
---

# NativeAOT and trim-safe code

`Ahtola.Core` is **NativeAOT-compatible and trimmable**, and the shipped
provider (`Devolutions.Ahtola.Data.Sqlite`) and EF Core package
(`Devolutions.Ahtola.EntityFrameworkCore.Sqlite`) must publish and trim
cleanly on `net8.0`/`net9.0`/`net10.0`. This is a hard constraint on every
change to shipped library code. The pure-managed closure scan does **not**
catch AOT/trim violations — review reflection against this list yourself.

## Forbidden in shipped library code

- Reflection-based serialization/deserialization or type discovery the trimmer
  cannot see: `Type.GetType` of a name built at runtime, unannotated
  `System.Text.Json` reflection serialization, `Activator.CreateInstance` of
  dynamically named types. Use source generators (`JsonSerializerContext`) or
  source-generated factories instead.
- `MakeGenericMethod` / `MakeGenericType` over types/methods constructed at
  runtime. Use reified generic instantiations the compiler can root.
- `dynamic`, runtime codegen (`Expression.CompileToDynamicMethod`,
  `DynamicMethod`), `Assembly.Load`, and `Type.GetType` string lookups in
  shipped code paths.
- Heavy reflection-only dependencies. `Ahtola.Data` is embedded into the
  shipped provider via `BuildOutputInPackage`, so anything it references must
  also be AOT-clean.

## When reflection is intentional

- Annotate with `[DynamicallyAccessedMembers(...)]` so the trimmer can follow
  the access. Put the attribute on the type/parameter/member that is reflected.
- Never suppress IL2050 / IL2060 / IL2070 (or related `IL2xxx`) analyzer
  warnings with `UnconditionalSuppressMessage` to hide a real hole. Suppress
  only when the trimmer is provably wrong and the access is statically safe.
- Prefer source-generated/compile-time alternatives over runtime reflection
  even when annotation would make it "legal."

## Unsafe

`Ahtola.Core` has `AllowUnsafeBlocks` for raw buffer/page work in `Storage`
(pointer math over `Span<byte>`, cell layouts, varints). Do not add `unsafe`
just to bypass a managed API or to mirror a Rust `unsafe` block — find the
managed equivalent.

## If a change can only work by disabling AOT/trimming

That is a design problem. Stop and discuss it; do not land it. The engine and
the shipped packages must stay publish-AOT-clean.
