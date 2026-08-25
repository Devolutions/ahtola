# Managed embedded replica bootstrap publication

This document is the behavioral contract for the two durable publications that
decide whether a managed embedded replica may be **exposed** to a connection at
all: the bootstrap itself (including the mandatory post-bootstrap MVCC logical
catch-up it may owe), and the remote-base snapshot that a partial replica
republishes when it becomes fully materialized.

> **Why the catch-up is not optional.** An MVCC-logical remote ships the
> bootstrap as a raw page image of the last durable *generation base*: the
> server deliberately never checkpoints for a bootstrap
> (`turso-src/sync/engine/src/database_sync_operations.rs`). The image is
> therefore stale by construction, and every commit since the last natural
> checkpoint lives only in the retained logical log. A replica that skipped the
> catch-up is not "slightly behind": it silently serves pre-checkpoint data, and
> a later open sees a perfectly ordinary `(database, metadata)` pair with no way
> to tell.

## 1. Durable state

| Artifact | Suffix | Meaning |
| --- | --- | --- |
| Bootstrap publication marker | `.ahtola-replica-bootstrap` | Append-only 48-byte status records (`AHTLBP01` magic, little-endian status, record length, SHA-256 of the first 16 bytes). The **last** record is the current status. |
| Metadata sidecar | `.ahtola-replica-meta` | Revision, image fingerprint, client id, protocol, table map, journal watermark, and `remote_base_sha256`. |
| Remote-base snapshot | `.ahtola-replica-base` | The image a conflict rebase rebuilds from. Its bytes must always hash to the `remote_base_sha256` metadata records. |
| Superseded base snapshot | `.ahtola-replica-base.previous` | The copy the previous publication displaced, retained only across the window in which metadata still names it. |
| Lazy-page state | `.ahtola-replica-lazy` | Present ⇔ the image is still sparse and pages are faulted on demand. |

All of these are listed in `ManagedReplicaBootstrapper.GetLocalArtifactPaths`, so
`EnsureDeleted()`/`DatabaseCreator.Delete` remove the whole footprint.

## 2. Bootstrap publication state machine

Statuses are **appended**, never rewritten, so an older prefix of the marker stays
byte-identical and readable and a torn tail is simply an incomplete record.

```
                     ┌──────────────────────────── install begins
                     v
  Empty ────────> InProgress
                     │
        page protocol│                     │MVCC-logical protocol
                     v                     v
              Complete /              CatchUpRequired /
              CompletePartial         CatchUpRequiredPartial
              (exposable)             (NOT exposable)
                                           │           │
                                           │           │ sparse image completed
                                           │           v
                                           │      CatchUpRequired
                                           │           │
                     catch-up metadata durable         │
                                           v           v
                                  Complete / CompletePartial
                                        (exposable)
```

* `IsComplete` is the single exposability gate and is true only for
  `Complete`/`CompletePartial`. `RequiresCatchUp` distinguishes the one
  non-exposable state that must be repaired by *finishing* work rather than by
  discarding the installed pair.
* A **page-protocol** bootstrap owes no catch-up and is exposable the instant it
  is published — unchanged from before this state machine existed.
* `CatchUpRequired*` carries exactly the same durable-artifact obligations as its
  `Complete*` counterpart, so `BootstrapPublication.Acquire` treats a missing
  database, metadata sidecar, or (for the partial variants) lazy-page state as
  requiring recovery in both.
* Completing a sparse image while a catch-up is still owed transitions
  `CatchUpRequiredPartial → CatchUpRequired`; it never makes the replica
  exposable one publication too early.

## 3. Crash behavior

| Crash point | Durable state left behind | Next open |
| --- | --- | --- |
| Before the marker is completed | `InProgress` | `Acquire` reports recovery; the partial install is deleted and the bootstrap retried. |
| After bootstrap publication, before catch-up | `CatchUpRequired*` + installed pair | Not exposable. The catch-up is resumed **against the installed base** — the bootstrap image is never re-downloaded. |
| During the catch-up apply | Whatever the apply's own compensation left | Same as above: still `CatchUpRequired*`, so the catch-up is retried. |
| After catch-up metadata is durable, before the marker is retired | `CatchUpRequired*` + advanced revision | The catch-up pull is repeated from the **advanced** revision, so it is a no-op resume-token request: nothing is replayed twice. The marker is then retired. |
| During the rollback of a failed catch-up (see below) | `CatchUpRequired*` with sidecars already partly deleted | Not resumable: `CanResumeRequiredCatchUp` requires the artifacts, not just the flag, so this falls through to a bootstrap whose own recovery removes the residue and reinstalls cleanly. |
| After the marker is retired | `Complete*` | Ordinary open. |

The rollback deliberately deletes the sidecars **before** the marker. The reverse
order would, if interrupted, leave a complete-looking `(database, metadata)` pair
with no marker at all — which every open accepts — re-introducing exactly the
never-caught-up exposure this state machine exists to close.

Two compensation rules keep this safe:

* A catch-up that fails **in the same call that performed the bootstrap** rolls
  the whole `(database, metadata)` pair back, but only after re-acquiring the
  apply lease and re-checking that the on-disk revision is still the exact
  generation it set out to undo — a concurrent publisher's newer, valid revision
  is never destroyed.
* A catch-up that fails **while resuming** an already-published bootstrap leaves
  that durable state alone. It predates the attempt and is exactly as safe
  (installed, non-exposable) as it was before it, so a transient network failure
  costs a retry rather than a full re-download. This applies only when the
  resume was genuinely possible — a marker whose artifacts are gone is never
  resumed in the first place.

## 4. Remote-base snapshot publication ordering

The snapshot published at bootstrap is a copy of the image that was installed —
for a partial bootstrap, the **sparse** one. The moment that image is fully
materialized, it no longer describes the base the metadata is about to
fingerprint, so completion republishes it using the same three ordered steps
every other base publication uses:

1. **Replace.** Stage a copy of the completed image and `File.Replace` it over
   `.ahtola-replica-base`, displacing the old copy to
   `.ahtola-replica-base.previous`.
2. **Record.** Atomically publish metadata naming the new hash (and preserving
   the journal watermark, revert state, client id, protocol, and table map).
3. **Retire.** Delete the superseded `.previous` copy.

This ordering is what makes "the recorded hash always describes bytes that are
on disk" hold across a crash at any point:

* Crash between (1) and (2) — metadata still names the superseded hash, which the
  retained `.previous` copy still matches. `ResolveRemoteBaseSnapshot` resolves
  it, and the next publication's `NormalizeRemoteBaseSnapshot` moves it back into
  place before republishing.
* Crash between (2) and (3) — metadata names the new hash, which the active
  snapshot matches; the stale `.previous` is dropped by the next normalization.

Without step (1), metadata would durably claim a completed-image base hash while
the file on disk still held the pre-completion bytes. Nothing repairs that: the
next process that needs the base for a conflict rebase resolves it by hash, finds
no matching copy, and fails closed permanently.

### The base is the server's image, never the local one

A protected apply — the path taken whenever pending local changes exist — builds
the next remote base in its own file: a copy of the previous base advanced by the
changes the server has already acknowledged and by this pull's remote
transactions, and *nothing else*. The still-unpushed journal entries are
deliberately not replayed into it, even though they are replayed into the image
that becomes the live database.

That distinction is load-bearing because the published base is the image every
*later* protected rebase copies before it replays the journal again. A base that
already carried the pending replay would apply the same local statements a second
time, duplicating rows on the next ordinary re-sync — and failing outright on any
table with a uniqueness guarantee. The metadata's `remote_base_sha256` is computed
from that same file, so the recorded hash and the published snapshot can never
disagree.

## 5. Wire compatibility

None of this changes the pull protocol. A prefix bootstrap still selects pages
with `server_pages_selector` (tag 5) and a query bootstrap still sends
`server_query_selector` (tag 7) alone, in exactly one round trip; the query is
never persisted and never resent, and later faults are plain revision-pinned
page-id selectors. Resuming an owed catch-up issues the ordinary logical pull
(`client_revision`, tag 3) and never re-runs page or query selection.
