---
name: vdbe-opcode-port
description: How to port or align a VDBE opcode between Turso's Rust `Insn` and Ahtola's C# `VdbeOpcode`, preserving semantics. Use this when implementing, fixing, or reviewing an opcode in the execution engine or the program builder.
---

# VDBE opcode porting

The execution engine runs a VDBE-style bytecode program (Turso calls these
`Insn`; Ahtola calls them `VdbeOpcode`). Programs are built by the
Compilation layer and executed by the Execution layer. **Opcode semantics
must match Turso/SQLite exactly** — the conformance corpus checks the
observable behavior, and the on-disk/transactional invariants depend on it.

## Where things live

| Concern | Ahtola (C#) | Turso (Rust) |
| --- | --- | --- |
| Opcode enum | `src/Ahtola.Core/Execution/VdbeProgram.cs` → `enum VdbeOpcode` | `turso-src/core/vdbe/insn.rs` → `pub enum Insn` |
| Execution loop | `src/Ahtola.Core/Execution/` (`VdbeProgram.cs`, `VdbeArithmetic.cs`, `VdbeTransaction.cs`, `VdbeParameterBinding.cs`) | `turso-src/core/vdbe/execute.rs` |
| Program builder | `src/Ahtola.Core/Compilation/` (`*ProgramBuilder.cs`, `DmlStatementCompiler.cs`, `SelectStatementCompiler.cs`) | `turso-src/core/vdbe/builder.rs` |
| Explain | `src/Ahtola.Core/Execution/VdbeExplain.cs` | `turso-src/core/vdbe/explain.rs` |
| Sorter / rowset / hash table | `Execution/` + `Compilation/BufferedWindowProgramBuilder.cs` | `turso-src/core/vdbe/sorter.rs`, `rowset.rs`, `hash_table.rs`, `bloom_filter.rs` |

## Porting an opcode

1. **Find the Rust `Insn`** in `turso-src/core/vdbe/insn.rs` and its execute
   arm in `execute.rs`. Read the full arm — including the `?`/error paths and
   any `Label`/`jump` resolution.
2. **Find or add the C# `VdbeOpcode`** in `VdbeProgram.cs`. The integer values
   are stable (consumed by the builder and serialized in explain output) —
   do **not** renumber existing opcodes; append new ones.
3. **Mirror the semantics**, not the call style: register/cursor/sorter indices
   map to Ahtola's `Register`/`Cursor`/`Sorter`/`Accumulator` record structs.
   Preserve operand order, jump-target resolution, and the exact error
   conditions. Replace Rust `IOResult`/yield with `async`/sync managed flow
   (see the `async-io-port` skill).
4. **Wire the builder**: if the opcode is emitted by a program builder, mirror
   the builder side in `Compilation/` so the compiler emits it in the same
   situations Turso does.
5. **Exercise it**: add or find a conformance case that hits the opcode, run
   it through `Invoke-ManagedTestSuite.ps1 -Filter ... -MinimumExecutedTests 1`,
   then the full conformance lane.

## Do not

- Do not invent opcodes that have no upstream counterpart without a strong
  reason and a doc comment explaining why. If you must, mark it clearly as an
  Ahtola extension in `VdbeExplain` output.
- Do not change an opcode's observable behavior to "simplify" it. If Turso
  raises an error at step N, Ahtola must raise the same error at the same step.
- Do not break explain output: the opcode names/values are part of the
  debugging contract.
