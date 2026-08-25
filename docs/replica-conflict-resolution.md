# Managed embedded replica push-conflict resolution

This document is the behavioral contract for what an Ahtola managed embedded
replica does when the remote server **rejects** a push of durably journaled
local changes.

> **This is an Ahtola managed extension, not a port.** Turso's own sync engine
> treats a push conflict as terminal: `turso-src/sync/engine/src/database_sync_operations.rs`
> (`wal_push`) maps the rejection to `Error::DatabaseSyncEngineConflict` and
> returns. There is no upstream rebase, no upstream conflict classification, and
> no upstream resolution policy to mirror. Everything below is Ahtola-specific
> behavior layered on top of the ported push/pull machinery, and it is
> deliberately conservative: it never merges an ambiguous write and never
> guesses which local change is safe.

## 1. Durable state

| Artifact | Suffix | Meaning |
| --- | --- | --- |
| Change journal | `.ahtola-replica-journal` | Append-only, strictly monotonic `Sequence`; `Watermark` is the exclusive boundary a **remote server** has confirmed; a durable discard record explains every gap in the retained set. |
| Journal staging | `.ahtola-replica-journal.staging.tmp` | Deterministic sibling temp file used by one atomic journal replace. Never data at rest. |
| Conflict marker | `.ahtola-replica-conflict` | Present ⇔ an unresolved push conflict is open. Records the rejected batch range, the reported conflict identity, and the still-unresolved sequences. |
| Conflict-marker staging | `.ahtola-replica-conflict.staging.tmp` | Deterministic sibling temp file used by one atomic marker replace. Never data at rest. |

All four sidecars are listed in `ManagedReplicaBootstrapper.GetLocalArtifactPaths`,
so `EnsureDeleted()`/`DatabaseCreator.Delete` remove the whole footprint and no
later bootstrap can find a half-deleted replica.

Both durable files are written with the same idiom (stage into the deterministic
sibling temp file opened `WriteThrough` with `FileShare.None`, flush to disk,
atomic replace), so a crash can only ever leave the previous content or the new
content. The staging names are deterministic rather than random precisely so an
interrupted persist can leave **at most one** leftover, which is part of the
declared artifact set and is deleted again by the next open/read (validated
startup cleanup), by the next persist, and by every delete path. Content that
does not validate — bad magic/version, truncation, trailing bytes, an unknown
conflict kind, an empty or unordered unresolved set, a sequence outside the
recorded batch, or a journal gap with no recorded discard — raises
`InvalidDataException`. It is never ignored.

### Journal format 7: gaps are proven, not assumed

Format 6 relaxed the "last retained entry is the highest ever assigned"
invariant so a discard could remove entries anywhere in the retained range.
Format 7 keeps that relaxation and adds the missing evidence: the discarded
sequences themselves are persisted, so the file can prove that **every** gap
between the retention base and the assigned high-water mark is an explicit
discard.

`ManagedReplicaChangeJournal.Open` enforces the resulting completeness
invariant: from the lowest sequence still represented through the assigned
high-water mark, each sequence is either retained or recorded as discarded, and
never both. Appends assign contiguous sequences, a discard moves one entry from
retained to discarded, and pruning removes a contiguous prefix from both, so any
other shape means the file lost or duplicated evidence and fails closed.

### Journal format 8: a row names the statement that replays it

One SQL statement can invoke the update hook for many rows, and only the first
of those journal entries carries the statement text. Until format 8 that
relationship was implied by adjacency, so a discard, a prune, or a batch
boundary could separate a row from the statement that would have transmitted it
— and the next batch's acknowledgement watermark then retired the orphan as if
the remote had received it.

Format 8 records `StatementSequence` on every entry: the sequence of the entry
that carries the replayable SQL for that row's statement (itself, for an entry
that carries SQL; `0` when no replayable statement is known). Three rules follow
and are enforced rather than assumed:

- **Batch selection never splits a statement.** `ReadBatch(maximumChanges)`
  extends past the limit to the end of the statement it lands in, so the limit
  is a floor rather than a ceiling.
- **Discards are group-closed.** `DiscardUnacknowledged` refuses a request that
  would remove part of a statement, in either direction, before anything is
  written.
- **Acknowledgement is proven, never assumed.** Before a push is attempted, the
  batch must be fully replayable: every entry either carries its own SQL, is
  covered by a statement replayed in that batch, or is covered by a statement an
  earlier batch already transmitted *and had acknowledged*. A statement that was
  discarded never covers its trailing rows, so those rows fail closed instead of
  being retired silently.

A pre-format-8 file reconstructs the grouping on read using exactly the rule the
older formats wrote with: an entry with SQL opens a group and the following
entries without SQL belong to it, and a gap ends the run. A trailing row whose
leader is no longer retained therefore keeps `StatementSequence == 0`, which is
precisely the state a push refuses.

Every consumer is therefore gap-aware:

| Consumer | Behavior with an interior hole |
| --- | --- |
| `ReadBatch(max)` | Skips the hole; the acknowledgement watermark spans it. |
| `ReadBatch(first, watermark)` (protected-push recovery) | Accepts the range when the retained entries plus the recorded discards in it exactly cover it; anything else fails closed. |
| `Acknowledge` / `ReadAcknowledged` | Unchanged; the acknowledged history may contain holes. |
| `AdvanceJournalBaseWatermark` | Requires a strictly ascending run starting at or after the recorded base, not a contiguous one — `Open` already proved file-level completeness. |
| `PruneAcknowledged` | Retires retained entries and discard records together, so the record stays bounded. |
| `RetentionBase` | The lowest sequence still represented (retained, discarded, or the watermark). |

Backward compatibility: formats 1–5 keep the strict tail check and can never
contain a gap. A format-6 file may contain an unexplained gap; the only
interpretation consistent with how the journal is written is "an explicit
discard removed it", so those gaps are adopted as recorded discards on open and
become exact on the next persist. That reconstruction is bounded up front: the
assigned high-water mark is a header field the file's own length does not
constrain, so a header implying an implausible gap span (or a saturated
sequence) is rejected as corruption rather than walked.

## 2. Recording a conflict

`ManagedReplicaConnectionHost.PushLocalChangesAsync` catches any push failure
that `AhtolaReplicaPushFailure.Classify` maps to
`AhtolaReplicaPushFailureKind.Conflict` and then, **in this order**:

1. If a revert-WAL bundle was captured for this push, restores the exact
   pre-push database image (`ManagedReplicaRevertWal.RestorePendingCheckpoint`).
   The revert phase graph is unchanged; no new phase was added.
2. Durably publishes the conflict marker for the exact batch that was rejected
   (`BatchFirstSequence`, `BatchWatermark`, the server-reported sequence, and
   the conservatively classified unresolved subset).
3. Rethrows the original `AhtolaReplicaConflictException`.

A crash between (1) and (2) leaves **no** marker. That is safe: the journal was
never acknowledged, so the next push re-attempts the same batch and re-observes
the same conflict, and the ordinary revert-phase recovery handles the bundle
exactly as it does today. Recording before restoring would instead risk blocking
synchronization while the database is still mid-restore.

The journal watermark is **not** moved. A conflicting push is never an
acknowledgement.

### Publication is serialized by the physical apply lease

Every push step that mutates durable local state — selecting the batch,
publishing the push intent (`MarkPushStarted`), restoring the pre-push image,
acknowledging the journal, and publishing the conflict marker — runs while
`ManagedReplicaApplyLock`'s exclusive lease is held. That lease is keyed by
**physical file identity** (so a symlink, junction, hard link, or Windows 8.3
short name resolves to the same key as the canonical path) and is backed by an
OS byte-range lock on a carrier file, so a second **process** serializes too.
Explicit conflict resolution takes the same lease around its journal discard and
marker retirement.

`ManagedReplicaLockCarrier` names that carrier from the database's physical
identity (volume/device plus file/inode id) inside one stable, per-user lock
directory — `AHTOLA_REPLICA_LOCK_DIRECTORY` overrides it for a deployment that
shares one replica between operating-system users. It is deliberately **not** a
sibling of the database: a hard link is the one alias no textual normalization
can collapse (both names are equally real directory entries for one inode), and
two hard links to one file may live in different directories, so a per-directory
or suffix-derived carrier would split them into two mutually invisible locks. A
replica whose file does not exist yet (its first bootstrap, where hard links are
impossible) falls back to the parent directory's physical identity plus the file
name; when neither can be proven, resolution **fails closed** rather than
handing back a carrier that cannot guarantee exclusion.

The change journal takes its own, separate lease
(`ManagedReplicaJournalLock`, carrier kind `journal`) around every append,
acknowledgement, discard, prune, and around `Open`'s staging cleanup. It has to
be a real OS lock for the same reason: the journal publishes by rewriting the
*whole* file, so two instances persisting from their own in-memory snapshots
would silently drop each other's durable entries. Each mutation re-reads the
durable file under the lease first, turning that lost update into an ordinary
merge. The carrier is separate from the apply lease's rather than a second byte
range on it because macOS byte-range locks are process-associated POSIX locks,
where closing any descriptor for a file drops every lock the process holds on
it. Lock order is always apply lease first, journal lease second — the journal
lease is only ever taken as a leaf — so the two can never deadlock.

The network round trips are deliberately **outside** the lease: holding a
physical lock across unbounded remote I/O would let one stalled replica block
every other participant indefinitely. Releasing it means local state can move
underneath a push, so every re-acquisition re-validates the generation it was
negotiated against — the metadata revision, the revert phase, and the journal's
durable shape (`ReplicaJournalGeneration`: assigned sequence, acknowledgement
watermark, retained count, discard count). A mismatch fails closed with
`AhtolaReplicaPushFailureKind.InvalidLocalState` rather than persisting a stale
in-memory journal over another writer's work. The single benign exception is
"another participant already acknowledged at least this batch", which completes
as a no-op and adopts the durable journal.

The acknowledgement and conflict-recording steps are deliberately uncancellable:
by the time either runs, the remote has already committed or definitively
refused the batch, so abandoning the local record would re-push writes the
server already holds, or silently re-push a batch it has refused. Cancellation
is observed before those boundaries, never inside them.

## 3. Fail-closed synchronization

While the marker exists, `SyncAsync`, the post-open catch-up pull, and the
automatic sync loop all throw `AhtolaReplicaConflictPendingException` **before**
attempting any push — the same shape as
`ManagedReplicaRevertWal.EnsureSynchronizationReady`'s refusal while a
checkpoint recovery bundle is pending. `IsTransientAutomaticSyncFailure` treats
both `AhtolaReplicaConflictException` and
`AhtolaReplicaConflictPendingException` as non-retryable, so the automatic loop
stops rather than re-pushing a rejected batch.

## 4. Classification (`ManagedReplicaConflictClassifier`)

A pure function of `(journaled batch, conflict kind, reported sequence)` — no
I/O, no ambient state — which is what makes resolution idempotent across
crashes and retries. Every rule fails toward "not eligible":

| Situation | Result |
| --- | --- |
| `AhtolaReplicaConflictKind.Unknown` | Every entry `Conflicting`. |
| Reported sequence absent from the batch (stale/foreign), or no sequence reported | Every entry `Conflicting`. |
| The reported entry itself | `Conflicting`. |
| Row conflict: another write to the same `(database, table, rowid)` | `RequiresManualResolution`. |
| Row conflict: a schema change targeting the conflicting row's table | `RequiresManualResolution`. |
| Schema conflict: every other schema entry | `RequiresManualResolution`. |
| A row write on a table whose schema entry is undecided | `RequiresManualResolution`. |
| A later write to a row whose earlier write is undecided (causal chain, any depth) | `RequiresManualResolution`. |
| A schema statement whose target cannot be parsed (e.g. `CREATE TRIGGER`) | Treated as targeting **every** table. |
| Anything else | `Eligible`. |

## 5. Explicit resolutions

```csharp
Task<AhtolaReplicaConflictReport?> AhtolaConnection.InspectReplicaConflictAsync(CancellationToken);

Task<AhtolaReplicaConflictResolutionResult> AhtolaConnection.ResolveReplicaConflictAsync(
    AhtolaReplicaConflictResolution resolution,
    AhtolaReplicaConflictResolutionOptions? options = null,
    CancellationToken cancellationToken = default);
```

`InspectReplicaConflictAsync` is a pure read: no network access and no local
mutation. It returns `null` when nothing is recorded, and an immutable
`AhtolaReplicaConflictReport` otherwise (entries carry only the durable
sequence, change kind, target table, row id, and eligibility — the internal
journal representation is never exposed).

### `PullAndRebaseEligible`

1. Requires the MVCC logical pull protocol and refuses when any quarantined
   entry is a schema change (both fail closed before any request).
2. Settles any recovery bundle a previous protected apply left behind, using
   the same `PrepareSynchronization` → `CompletePreparedCheckpoint` pair the
   push path uses when it has nothing to push. No phase is skipped or invented.
   These are durable local publications, so they run under the physical apply
   lease, which is released again before the pull (which takes the same
   non-reentrant lease internally around its own apply).
3. Calls the ordinary `ManagedReplicaBootstrapper.CheckForUpdatesAsync`, which
   acquires the existing exclusive apply lease internally and applies through
   the existing transactional logical replay and its artifact-backup
   compensation. The **full** pending set is still handed in as the staleness
   baseline for `TryUseCurrentLocalStateAsPullBase` (otherwise the retry loop
   could never converge); only the replay set is filtered. That staleness check
   reloads and compares metadata (revision **and** recorded journal base), the
   pending set, **and** the acknowledged history — refreshing only the first two
   would leave an entry another participant acknowledged mid-flight in neither
   replay list, and the rebuild would silently drop its row.
4. The rebase always takes the protected path, which rebuilds the local image
   from the durable remote base snapshot and replays only the eligible entries.
   Quarantined row writes therefore lose to the freshly pulled base while
   remaining durably journaled as evidence. This holds even when the pull
   returns **no transactions and the same revision**: that is the ordinary shape
   of a rebase against an already-current remote, and short-circuiting it as
   `UpToDate` would leave the quarantined writes materialized while reporting a
   rebase that never happened.
5. `RebasedChangeCount` is the number of entries the apply actually replayed,
   reported by the apply itself — never a count derived from the journal before
   the apply decided whether, and how, to run.
6. **The marker is retained.** Eligible entries were replayed locally but never
   pushed, and unresolved entries are still quarantined, so synchronization
   stays blocked. `ConflictCleared` is `false` and `RemainingConflict` describes
   what is left.

Because classification is a pure function of untouched durable state, a failed
or cancelled attempt is retried identically. A crash before metadata publication
is compensated by the existing `artifactBackup`/`metadataPublished` logic, and
the marker — never touched until after publication — is byte-identical.

### `DiscardUnresolvedChanges`

Requires `AhtolaReplicaConflictResolutionOptions.AcknowledgeDataLoss`, checked
before any I/O. It removes exactly the recorded unresolved sequences from the
journal via `ManagedReplicaChangeJournal.DiscardUnacknowledged`, then retires
the marker. No network access, no metadata change.

`DiscardUnacknowledged` is deliberately **not** `Acknowledge` and deliberately
does not move the watermark: the watermark's only meaning is "confirmed by a
remote server", so a journal discard always stays distinguishable from a
remote-confirmed push in an audit and in the staleness comparison. Discarded
sequences stop being retained but are durably **recorded** as discarded; the
assigned high-water mark stays put, so no future change can reuse one of them.
This is why the journal on-disk format moved to version 7, and to version 8 for
the statement identity a discard must keep whole.

A crash between the journal replace and the marker delete is recovered
idempotently — but only on evidence. Because the replace is atomic, "none of the
recorded unresolved sequences are retained **and every one of them is recorded
as discarded**" can only mean the discard already landed, so the next resolution
simply retires the marker (`DiscardedChangeCount == 0`,
`ConflictCleared == true`). A sequence that is neither retained nor recorded as
discarded is *missing evidence*, not a completed discard: that fails closed with
`InvalidDataException` and **never** clears the marker, so synchronization stays
blocked for an operator to inspect. A partial mix of retained and recorded
sequences is legitimate and converges: only the still-retained ones are
discarded.

Cancellation has a single, deterministic boundary. It is checked when the
publication slot is taken (before any host is closed, where cancelling is
completely free of consequence) and again under the apply lease before the
discard. Once the journal replace is durable the resolution has irreversibly
happened, so marker retirement always completes rather than leaving a marker
naming sequences that no longer exist.

Once every unresolved entry is resolved or discarded, the marker is removed and
the remaining eligible entries push on the next ordinary `SyncAsync`.

## 6. What stays manual, always

1. `AhtolaReplicaConflictKind.Unknown` — the whole batch.
2. Any schema conflict, and any quarantined DDL (a rebase fails closed rather
   than dropping or replaying a statement whose fate is undecided).
3. Row writes sharing `(database, table, rowid)` with the rejected write.
4. Any write causally chained after an undecided write on the same row.
5. Page-protocol replicas, and a protocol-2 remote that answers a rebase pull
   with a raw page stream: raw pages cannot rebase journaled SQL and cannot hold
   a quarantined subset back.
6. A reported sequence that is not in the recorded batch.

For all of these the paths forward are: reconcile manually against the newly
pulled base with fresh local writes (which get new sequences), then
`DiscardUnresolvedChanges` to drop the stale originals; or
`DiscardUnresolvedChanges` outright with explicit data-loss acknowledgement.

Note that discarding **without** first rebasing leaves the local rows exactly as
they are — the discard only removes journal entries. The next pull that touches
those rows resolves them to the remote value.

## 7. Publication reopen failures

A publication closes every registered host, runs the staged operation, and then
reopens them. If a reopen fails, the host clears and disposes its adapter and
tells the attached `AhtolaConnection` to drop the disposed managed database, so
`State` reports `Closed` rather than advertising a connection whose every
command would fail with an opaque `ObjectDisposedException`. The original reopen
failure is still surfaced to the caller.

## 8. Tests

`src/Ahtola.Tests/ManagedReplicaConflictRebaseTests.cs` covers the classifier,
marker round-trip and every corruption/staleness mode, journal discard
semantics, gap-aware push/acknowledge/recovery across an interior discard,
format-7 completeness validation and format-6 upgrade, sync blocking (explicit,
reopen, and automatic), eligible-only rebase, same-revision empty-pull rebase and
its replay count, acknowledged-history refresh on a staleness retry,
unresolved-marker persistence, explicit discard, eligible push after resolution,
fail-closed behavior on missing discard evidence, push publication lease
ownership and generation validation, cancellation at the publication boundary
and after a durable discard, deterministic staging artifacts, publication reopen
failure, crash points before metadata publication and before marker retirement,
the page-protocol and schema-quarantine fail-closed paths, and artifact cleanup.
