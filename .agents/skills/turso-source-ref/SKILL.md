---
name: turso-source-ref
description: How to find and cross-reference the original Turso Rust sources in the read-only `turso-src/` submodule when porting or comparing behavior in the Ahtola C# engine. Use this whenever you are implementing, debugging, or reviewing a feature that must stay aligned with Turso's design and capabilities.
---

# Referencing the Turso source

Ahtola's goal is to stay as aligned as possible with Turso's design and
capabilities — it is a pure-managed C# port of Turso's Rust core, not an
independent engine. The `turso-src/` git submodule (pinned to a release tag,
currently `v0.7.2`) is the source of truth for "how does Turso do this?".
**Always consult the Rust source before designing or changing a behavior that
has an upstream counterpart**, and mirror Turso's structure, naming, and
semantics where the managed/AOT constraints allow.

## First, make sure the submodule is present

```powershell
git submodule update --init --recursive
```

`turso-src/` is **read-only reference material**. Never edit files there, never
add it to a shipped project's item groups, and never package anything from it.
It is not compiled or shipped by Ahtola.

## Where things live in Turso → Ahtola

Use this map to jump to the right Rust source for a C# type you are working on:

| Ahtola (C#) area | Turso (Rust) source |
| --- | --- |
| `Ahtola.Core/Storage` (pager, WAL, b-tree, allocator, overflow, encryption) | `turso-src/core/storage/` |
| `Ahtola.Core/Parsing` | `turso-src/sqlite/parser/` and `turso-src/core/dialect/` |
| `Ahtola.Core/Compilation` (VDBE-style program build) | `turso-src/core/vdbe/` and `turso-src/core/translate/` |
| `Ahtola.Core/Execution` | `turso-src/core/vdbe/` (execution loop) |
| MVCC / transaction layer | `turso-src/core/mvcc/` |
| Indexes / skiplists / index methods | `turso-src/core/skiplist/`, `turso-src/core/index_method/` |
| Built-in functions / numeric / json / vector / time | `turso-src/core/functions/`, `turso-src/core/numeric/`, `turso-src/core/json/`, `turso-src/core/vector/`, `turso-src/core/time/` |
| Incremental vacuum | `turso-src/core/incremental/` |
| I/O model (pager, IO traits) | `turso-src/core/io/` |
| Sync / replication engine | `turso-src/sync/engine/` |
| SDK kit (native companion surface) | `turso-src/sync/sdk-kit/` (reference only — Ahtola has no native companion) |
| Extension / ext | `turso-src/core/ext/` |

The on-disk SQLite file format Turso implements is the same format
`Ahtola.Core/Storage` must read/write — see `docs/wal-interoperability-contract.md`
for the WAL interop target.

## How to search effectively

Prefer grepping the submodule directly over fetching Turso sources from the
web:

```powershell
# Find the Rust type/function a C# port mirrors
rg -n 'struct Page'      turso-src/core/storage
rg -n 'fn btree_init'    turso-src/core
rg -n 'VdbeOp'           turso-src/core/vdbe

# Find every upstream symbol named in a C# doc comment
rg -n '<symbol-in-the-doc-comment>' turso-src
```

When a C# type's doc comment or the WAL contract names an upstream Rust
symbol, treat that as the primary citation: open the Rust file, read the
surrounding impl, and mirror the invariants (page sizes, header offsets,
opcodes, error conditions) in the C# port.

## Stay aligned, then adapt to managed constraints

When porting, preserve Turso's **behavior and on-disk format** exactly where
the contract requires it (file format, WAL framing, VDBE opcode semantics,
SQL dialect). Adapt the **implementation language** to C# / .NET idioms only
where there is no format/semantic contract:

- Replace Turso's cooperative `IOResult`/`Completion` state-machine IO with
  `async`/`await` or synchronous managed IO — the *semantics* (what a read at
  offset X must return) must match, not the call style.
- Keep `Ahtola.Core` NativeAOT/trim-clean (see AGENTS.md). Do not port Rust
  traits/patterns that require runtime reflection or dynamic codegen.
- Do **not** introduce a native companion. Turso types under
  `turso-src/sync/sdk-kit/` reference the native `turso_sdk_kit` — those are
  reference only. Ahtola's intentional `Turso.*` companion-load strings
  (`Turso.Data.Native`, `Turso.Data.Sync`) load optional assemblies by name
  and fail closed; that is a product decision, not something to "port over".

## Bump the submodule when you port a newer release

If you need behavior from a newer Turso release, bump the submodule to that
tag in the same change that ports the behavior, and note the new tag in the
commit message and in AGENTS.md:

```powershell
cd turso-src
git fetch --tags origin
git checkout <new-tag>        # e.g. v0.8.0
cd ..
git add turso-src             # records the new Subproject commit
```

Prefer release tags over `main` so the reference is reproducible.

## Quick checklist before landing a port

- [ ] Found and read the matching Rust source in `turso-src/` (cite the file
      and symbol in the PR description or a C# doc comment).
- [ ] Mirrored the upstream invariants (format, offsets, opcode/error
      semantics) — not just "produces the same output on the happy path".
- [ ] No native/Rust toolchain, no `DllImport`/`LibraryImport`, no AOT/trim
      breakage introduced (see AGENTS.md).
- [ ] If you bumped `turso-src/`, the new tag is recorded in the commit
      message and AGENTS.md.
