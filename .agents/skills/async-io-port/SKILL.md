---
name: async-io-port
description: How to map Turso's cooperative `IOResult`/`Completion` I/O model to Ahtola's managed I/O without changing semantics. Use this when porting storage/pager/WAL code that uses Turso's state-machine IO.
---

# Async I/O porting

Turso uses cooperative yielding with explicit state machines instead of
Rust `async`/`await`. The core types live in `turso-src/core/io/` (`mod.rs`,
`completions.rs`, `generic.rs`, `memory.rs`, `vfs.rs`, plus platform backends
`unix.rs`/`windows.rs`/`win_iocp.rs`/`io_uring.rs`):

```rust
pub enum IOResult<T> {
    Done(T),
    IO(IOCompletions),     // need I/O; call me again after completions finish
}
```

A function returning `IOResult` is called repeatedly until `Done`. A
`Completion` tracks one I/O; a `CompletionGroup` waits for several.

## Ahtola's equivalent

Ahtola does **not** replicate the `IOResult`/`Completion` state machine.
Storage I/O goes through `IFileSystem`
(`src/Ahtola.Core/Storage/IFileSystem.cs`) with two implementations:
`InMemoryFileSystem.cs` and `PhysicalFileSystem.cs`. The pager
(`SqlitePager.cs`), page store (`SqlitePageStore.cs`), and WAL
(`SqliteWal.cs`) call into `IFileSystem`.

## Porting rules

- **Port the semantics, not the call style.** A read at offset X must return
  the same bytes whether Turso does it via a `Completion` callback or Ahtola
  does it via a synchronous `IFileSystem.Read` / `async` stream. Do not
  cargo-cult `IOResult`/`yield` into C# — use `async`/`await` or a plain
  synchronous call.
- **Preserve partial-read / short-read semantics.** Turso's IO layer has
  well-defined behavior for incomplete I/O; the managed adapter must match
  (fail with the same error, or continue to the same boundary).
- **No native I/O backends.** Do not add `io_uring`/`win_iocp`/`unix` raw
  syscall ports. `PhysicalFileSystem` uses managed file APIs; the only OS
  interop allowed in `Storage` is byte-range locking and the shared-memory
  mapping for the WAL index (see `pure-managed-closure`).
- **Yield points map to `async` suspension or are no-ops.** Turso's
  `yield_hooks`/`yield_points` (in MVCC and the pager) exist for cooperative
  scheduling. In Ahtola they become `await` points (if truly async) or are
  elided (if synchronous). Do not add cooperative-yield state machines that
  the runtime doesn't need.
- **Error parity.** Map Turso I/O errors to the corresponding managed
  exception/error code the storage layer already raises; don't introduce a
  new error taxonomy for one port.

## When to keep it synchronous

The managed engine is often exercised synchronously (tests, in-memory).
Prefer synchronous `IFileSystem` calls for the hot pager/WAL paths unless an
`async` API is already required at the boundary. Don't force `async` viral
through the engine to mirror Turso's yield model.
