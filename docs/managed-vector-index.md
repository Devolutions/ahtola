# The managed vector index method (`USING vector`)

Ahtola ships a pure-managed approximate-nearest-neighbour index method that answers
`ORDER BY vector_distance_*(col, ?) LIMIT n` **exactly**. It is registered as `vector` on the
[managed index-method foundation](managed-index-methods.md) and shares that foundation's lifecycle,
transaction, journal, cost and EXPLAIN machinery.

> **It is not a port of Turso's `toy_vector_sparse_ivf`.** See
> [Divergence from Turso](#divergence-from-turso) for exactly what was and was not carried over.

## SQL surface

```sql
CREATE INDEX docs_knn ON docs USING vector (embedding)
  WITH ( metric  = 'l2',        -- l2 | cosine | dot
         encoding = 'float32',  -- float32 | float64 | float8 | float1bit
         dims    = 768,         -- required
         lists   = 256,
         probes  = 16,
         seed    = 0,
         iters   = 10,
         train_sample = 32768,
         exact   = 1,
         min_rows = 512 );

DROP INDEX docs_knn;   -- runs Destroy
REINDEX docs_knn;      -- retrains centroids and rebuilds postings, atomically
```

Nothing in the parser changed: `CREATE INDEX … USING m (cols) WITH (k = literal)` and the inherited
rejections (UNIQUE, partial, expression column, `DESC`, `COLLATE`, `WITHOUT ROWID`, views,
`sqlite_*`, the reserved `_ahtola_idxm_` infix) were already in place.

### `WITH` keys

Every key is validated **and consumed**. Nothing is accepted and then ignored, and every value whose
behaviour is not implemented is rejected with a message that says so.

| Key | Values | Default | Effect |
| --- | --- | --- | --- |
| `metric` | `l2`, `cosine`, `dot` | `l2` | Binds the index to exactly one `vector_distance_*` call. `jaccard` is rejected: it needs the sparse structure that is not implemented. |
| `encoding` | `float32`, `float64`, `float8`, `float1bit` | `float32` | The column's serialized encoding. `float32_sparse` is rejected for the same reason as `jaccard`. |
| `dims` | 1 … 2048 | **required** | Declared, never inferred: an index whose dimensionality came from row 1 would change meaning when row 1 was deleted. |
| `lists` | 1 … 4096 | `64` | Inverted lists (k-means clusters). Persisted; changing it is a schema change. |
| `probes` | 1 … `lists` | `ceil(sqrt(lists))` | Where the certificate loop *starts*. A speed knob, never a correctness knob. |
| `seed` | int64 | `0` | Training seed. |
| `iters` | 1 … 16 | `10` | Lloyd iterations. |
| `train_sample` | 256 … 65536 | `32768` | Reservoir size, drawn in rowid order. |
| `exact` | `1` | `1` | Exact mode is the only mode shipped. `exact = 0` is **rejected**, not silently upgraded. |
| `min_rows` | ≥ 0 | `512` | Below this many live rows the index declines and the scan wins. |

`l2` over `float1bit` is rejected at `CREATE INDEX`, because the scalar evaluator has no L2 distance
for bit vectors: such an index could only ever serve a query that errors on its first row.
`lists × dims × 4` is capped at 4 MiB and checked before anything is allocated.

### Recognized query shapes

```sql
SELECT id FROM docs ORDER BY vector_distance_l2(embedding, ?) LIMIT 10;   -- KnnLimit
SELECT id FROM docs ORDER BY vector_distance_l2(?, embedding) LIMIT 10;   -- symmetric, same plan
SELECT id, vector_distance_cos(embedding, ?) AS d FROM docs ORDER BY d LIMIT 10;  -- alias form
```

`ORDER BY` terms are resolved against the projections before planning, so the alias form is planned
as the call it names.

Declined — the ordinary scan answers, with identical rows, identical order and identical errors:

- `DESC` ordering, or an explicit `COLLATE` on the ordering term;
- a second `ORDER BY` term;
- a distance function other than the one the index is bound to;
- a query operand that depends on the scanned row;
- a connection-registered scalar shadowing **any** `vector_distance_*` name;
- a residual `WHERE` predicate, `DISTINCT`, `GROUP BY`/`HAVING`, an aggregate, a window, a
  non-literal `LIMIT`/`OFFSET`, a join arm, a subquery source, or a concurrent-MVCC statement;
- **any live row whose indexed column is not a valid vector of the declared encoding and
  dimensionality**, including `NULL` — see [Row validity](#row-validity-and-error-equivalence).

Without a `LIMIT` the shape is `Knn`: it is recognized, it retains every base row, and it is priced
at what it actually produces, so it loses to the scan.

## Structure: IVF-Flat with an exactness certificate

```
centroids : float32[lists][dims]        -- durable, in the catalog state envelope
listOf    : rowId -> (list, slot)       -- derived; the authority for membership
postings  : (rowId, isLive)[] per list  -- derived; a delete clears the liveness flag
radius    : double per list             -- derived; an upper bound on centroid -> member distance
unbounded : rowId[]                     -- rows no inequality covers; always probed
```

A posting slot carries its liveness as a field, never as a sentinel row id: every 64-bit value is a
legal SQLite rowid, `long.MinValue` included, so a magic value would make a real row invisible to
search, drop it from compaction, and double-count it as a hole when it was deleted.

Vectors are **never copied into the index**. Reranking reads the base row through the engine's
snapshot-isolated row source and scores it with `SqliteVectorFunctions.DistanceExact`, which is the
scalar evaluator's own code path. Memory is `O(rows)` at roughly twenty bytes per row plus
`lists × dims × 4` bytes of centroids — not `O(rows × dims)`.

The rowid-to-placement map is the authority for membership, so a deleted rowid that is later reused
cannot resurrect its old assignment.

### Search

1. Decode and validate the query, reproducing the scalar errors first.
2. Compute one provable lower bound per list, sort lists by it ascending, and rerank the
   always-probed bucket.
3. Probe lists cheapest-first, reranking every live member exactly.
4. After at least `probes` lists, compare the bound of the *next* unprobed list against the k-th best
   reported distance so far. Because the order is ascending, that single comparison certifies every
   remaining list at once: if the bound is strictly greater, no unprobed row can enter the top-k.
5. Return every reranked row that ties with or beats the k-th best, **in scan order**.

The engine's own `ORDER BY` then sorts and truncates that set. Its sort is stable — ties resolve by
scan position (`EmbeddedDatabase.cs`, "ORDER BY ties follow the scan order the query produced") — so
emitting candidates in scan order makes the indexed result byte-identical to the unindexed one,
including tie order and the emitted distance values.

### Why it is exact

The method returns a **superset** of the rows the statement keeps, and the engine picks the final
rows with exactly the comparison it would have used on a full scan. The only thing that has to be
proven is that no row is dropped.

| Metric | Inequality | Bound on any member of list *j* |
| --- | --- | --- |
| `l2` | triangle inequality | `max(0, ‖q−c_j‖ − radius_j)` |
| `cosine` | angle is a metric on the unit sphere, and `1 − cos θ` is increasing on `[0, π]` | `1 − cos(max(0, θ(q,c_j) − radius_j))` |
| `dot` (reported negated) | `q·v = q·c + q·(v−c) ≤ q·c + ‖q‖·radius` (Cauchy–Schwarz) | `−(q·c_j + ‖q‖·radius_j)` |
| `float1bit` | Hamming is a metric; reported cosine **is** the Hamming distance and reported dot is `2·hamming − dims` | `max(0, H(q,c_j) − radius_j)`, exactly, in integers |

Each bound is then widened by a floating-point slack before it is compared, because the value the
scalar evaluator *reports* is not the exact real distance:

- `float32` vectors accumulate in single precision, so a `dims`-term sum carries a relative error
  near `dims·2⁻²⁴`. The slack used is `(dims + 8)·2⁻²²`, about eight times that worst case.
- `float64` and `float8` reach their reported value through `double`, so the same expression is used
  at `2⁻⁴⁶`.
- `float1bit` distances are exact integer counts and take no slack at all.
- Cosine's reported form subtracts a near-one ratio from one, so its error is absolute rather than
  relative; its slack is scaled by the full `[0, 2]` range instead of by the bound.
- An absolute floor (`1e-18` for `float32`) covers squared components underflowing to zero.

Radii are stored as **upper** bounds and only ever recomputed downward by `REINDEX`/`Optimize`, so a
stale radius costs probes and never recall.

### When no bound can be proven

The search does not guess. It reads everything and returns every live row (still a correct superset)
when:

- the query has a non-finite component, or has zero norm under cosine clustering;
- the query's **scalar-arithmetic** norm underflows, overflows or is not finite (see below);
- a list's radius is not finite, or its centroid carries no usable direction;
- a reranked row produces a non-finite reported distance (a degenerate `float8` cosine, for example);
- a reranked row makes the scalar distance throw;
- the index is untrained, or any row is unindexable;
- the candidate set reaches one million rows — the cap is enforced as candidates are accumulated,
  not compared against afterwards, so the exhaustive fallback is a decision the search makes before
  it has exceeded the bound rather than a consequence it discovers after paying for it.

Rows the geometry cannot place — a zero-norm vector under cosine, for instance — go into an
always-probed bucket rather than into a list whose radius they would silently invalidate.

### Cosine degeneracy under the exact scalar arithmetic

Every bound above is derived from `double` components, but the value it bounds is produced by
`vector_distance_cos`, which for a `float32` column accumulates its norms in `float`. Those two
arithmetics disagree at the extremes, and the disagreement is not a rounding difference:

| Components | `float` norm accumulator | Reported distance |
| --- | --- | --- |
| `1e-24` | squares are `1e-48`, below the smallest subnormal ⇒ `0` | the degenerate branch: `0` or `1` |
| `1e20` | squares are `1e40`, above `float.MaxValue` ⇒ `+∞` | `1 − dot/∞` ⇒ `1` |

In both cases the widened `double` direction is perfectly ordinary, so an angular bound built from it
is a claim about a number the scalar evaluator never produces — and a list holding such a row can be
pruned while the row belongs in the answer.

The index therefore reproduces the scalar accumulator, in the accumulation width the encoding
implies, and treats a squared norm that is zero, non-finite, below `2⁻⁶³` or above `2⁶³` as unusable
(the window is set so the *product* of two usable norms also neither overflows nor underflows, since
the reported value divides by its square root). `float64` and `float8` use the `double` equivalent
window; `float1bit` cosine is an exact integer Hamming count and needs no gate at all. A row whose
norm is unusable goes to the always-probed bucket; a **query** whose norm is unusable makes every
list unprunable, so the search reads everything and the ordinary comparison decides.

## Row validity and error equivalence

`vector_distance_*` **throws** for a `NULL`, non-vector, wrong-dimension or wrong-type operand; it
does not return `NULL`. An index that quietly skipped such rows would turn an error into a result
set, which is a worse failure than a wrong row.

So the index classifies every live row, and while any row is unindexable `EstimateCost` returns
`null` and the plan is declined outright. The ordinary scan then answers — errors included, raised on
the row and in the order the scan reaches them. Deleting or fixing the offending row makes the plan
available again.

That classification is a **census**, not a rebuild: it decodes one column value per changed row and
rides the same mutation journal the index does, so the steady-state cost is the rows that changed
since the last statement and the full walk only runs when the journal cannot prove otherwise. It
never trains centroids, places postings or publishes anything, which is what lets the planner answer
the validity question without reconciling a cold index (see *Planning*).

Decoding is bounded before it allocates. A serialized vector blob is rejected outright if it is
larger than the managed dimension cap allows (`1 048 576 × 8` bytes, the widest encoding), before the
payload is copied and before any of the per-type length arithmetic that could overflow. The indexing
decode additionally proves the declared encoding and dimensionality against the index's own shape
from the parsed header, so a million-component blob in a four-dimensional index is refused without
being widened into a `double[]`.

For the query operand the plan can be exact without scanning: the index is only ever planned when
every live row already decodes to the declared shape and the table is non-empty, so the column
operand can never be the operand that fails. The adapter's `ValidateArgument` hook therefore
reproduces the scalar checks in the scalar order (parse, then dimensions, then type) before the plan
runs, for either argument order.

## Training

1. **Sample.** Reservoir of `min(liveRows, train_sample)` rows walked in **rowid-ascending** order,
   so neither the insertion order nor the physical layout can change which rows are drawn. Rows are
   offered to the reservoir *as the scan decodes them*, so retention is `O(train_sample × dims)`
   regardless of table size; the eligible population is counted separately rather than inferred
   from what was retained.
2. **Seed.** `xoshiro256**` seeded through `SplitMix64` from `seed` mixed with an FNV-1a fingerprint
   of the index name, metric, encoding, dims, lists, iterations and sample size. No
   `System.Random`, no clock, no string hash code, no thread identity.
3. **k-means++** seeding, then exactly `iters` Lloyd passes. Every tie resolves to the lowest index:
   nearest centroid, farthest point, empty-cluster reseed.
4. Accumulation runs in `double` over a fixed ascending sample order, so the summation order — and
   therefore the rounding — is identical on x64, ARM64, NativeAOT and WebAssembly.
5. Centroids are narrowed to `float32`. They only steer the search: every distance that reaches a
   result is recomputed from the base row, so centroid precision affects how many lists get probed
   and never affects which rows come back.
6. `float1bit` columns cluster their raw 0/1 components and binarize the centroid at one half, so the
   derived radius is an exact Hamming count. `cosine` clusters unit-normalized vectors so Euclidean
   proximity in the clustering space is monotone in angle.

Training over an empty sample produces all-zero centroids, which would send every row to list 0 and
prune nothing. That is not recorded as a trained index: the first populated refresh trains for real.

Two counts come out of training and they are **not** interchangeable. The *sample* is what k-means
saw and is capped by `train_sample`; the *population* is how many live rows were eligible to be
sampled. Both are persisted. Drift is measured against the population, because comparing the live row
count against a capped sample makes the "grown by a factor of four" test true for ever on any table
larger than `4 × train_sample` — which re-ran k-means on every single refresh.

## Maintenance

| Path | Behaviour |
| --- | --- |
| DML | Journal driven. Every `INSERT`/`UPDATE`/`DELETE`/`REPLACE`/upsert, trigger body and foreign-key cascade funnels through the engine's single row-change reporter, so the index sees every touched rowid and reconciles in `O(changed)`. A row that cannot be indexed is recorded, never rejected: DML must not fail because of an index. |
| Refresh | `source.Revision == appliedRevision` ⇒ `O(1)`; a proven journal delta ⇒ `O(changed)`; otherwise a full rebuild of the postings from the base rows. |
| Drift | When the live row count reaches four times, or a quarter of, the **eligible population the centroids were fitted to**, the next refresh retrains. It is never compared against the capped `train_sample` reservoir. Drift never costs recall — the certificate simply stops pruning — but it does cost speed, and the cost model prices that. |
| `REINDEX` | Retrains centroids **and** rebuilds postings into a detached structure, publishing only on success. A throw leaves the previous index live and queryable. |
| `Optimize` (opcode 109) | Compaction only. It reclaims tombstoned posting slots and recomputes radii exactly, which can only shrink an upper bound. It never trains centroids itself; the refresh it performs first follows the ordinary drift rule above. It never runs inline in DML. |
| `DROP INDEX` | Runs `Destroy`, clearing centroids and postings. |
| Rollback / savepoint | Inherited: `Fork()` starts with empty postings and carries the immutable centroids, so a rolled-back statement leaves no assignments behind and does not silently re-cluster. |

## Cost model

```
if (baseRows == 0 || baseRows < min_rows)      -> decline
if (any row is unindexable)                    -> decline
if (the plan retains unranked rows)            -> price at baseRows (loses to the scan)

coldRows     = ceil(baseRows * min(lists, probes * 2) / lists) + unboundedRows
measuredRows = ceil(baseRows * mean observed reranked fraction)
probeRows    = min(max(coldRows, measuredRows), baseRows)
cost         = lists + probeRows * 2 + refreshCost + limit
```

The measurement is the **reranked row count**, not the probe count: one list can hold most of the
table, so a probe count alone would be a fabricated saving. `refreshCost` charges the reconciliation
the next query is forced to perform, including a full k-means pass when the index is cold or drifted.
The planner then refuses anything whose cost does not beat a full scan.

`probeRows` is computed for the structure the plan will actually run against — the reconciliation the
estimate already charges for is the thing that trains it — rather than for the cold structure that
exists at plan time. Pricing an unreconciled index as a permanent full read would make it lose every
comparison, never be selected, and therefore never be reconciled.

The practical consequence is that adversarial data re-prices itself out. On a corpus where no list
can be pruned, the first query is exact but reads everything; the measured fraction rises to one, and
every later query takes the scan instead.

### Planning is deferred

Pricing a candidate never opens it. The planner asks each method index what a pattern would cost
while its derived state is still whatever it happens to be, picks a winner, and only then opens the
winner — which is the point at which reconciliation happens. Concretely:

- `EXPLAIN QUERY PLAN` prices and reports a plan without rebuilding a cold index.
- A table carrying three method indexes rebuilds one of them to answer a query, not three.
- A candidate that loses the comparison is never reconciled at all.

The reconciliation each candidate still owes is reported separately from its cost and is amortized
out of the scan comparison — the same maintenance is owed to a scalar `fts_score()` evaluated on the
plain scan path, so it is a cost of *having* the index rather than of choosing this access path — but
it does break ties, so an already-current index wins over a cold one.

`EXPLAIN QUERY PLAN` reports the method's own plan description verbatim:

```
SEARCH docs USING INDEX METHOD vector INDEX docs_knn
  (pattern=KnnLimit metric=l2 encoding=float32 lists=64 probes=16 scans~150/600 exact=1 rows~5 cost~369)
```

## State envelope

`/*ahtola-index-method:1:<base64>*/` appended to the stored `CREATE INDEX` text — written, rolled
back and recovered by the same pager/WAL transaction as the rest of `sqlite_schema`. Only centroids
and configuration are persisted; assignments, postings and radii are derived.

```
0   u32  magic 'A' 'V' 'I' 'X'
4   u16  state version (1)
6   u8   metric      | 7  u8  encoding
8   u32  dims        | 12 u32 lists
16  u32  iters       | 20 u32 train_sample
24  i64  seed        | 32 u32 trained sample rows
36  u8   exact       | 37 u8[3] reserved
40  u32  probes      | 44 u32 FNV-1a fingerprint of the centroid payload
48  i64  trained population (eligible live rows the sample was drawn from)
56  f32[lists * dims] centroids
```

Every offset above is the end of the previous field, and the header ends exactly where the payload
begins: the fingerprint occupies four bytes at 44 and nothing in the header can reach a centroid. A
previous revision wrote it as an eight-byte value into that four-byte slot, which silently zeroed the
first centroid component on every save — an error no result set could show, because a wrong centroid
only weakens the certificate.

Every check runs **before** the centroid array is allocated:

| Condition | Behaviour |
| --- | --- |
| no envelope | trains from the base rows silently (it is a cache, not the authority) |
| version newer than this build | `index 'x' was written by a newer managed index method (v2)` |
| bad magic / short header | `malformed managed index 'x': truncated state` |
| metric, encoding, dims, lists, iters, train_sample, seed, exact or probes ≠ declaration | `malformed managed index 'x': state <field> does not match the index definition` |
| payload length ≠ `lists × dims × 4` | `malformed managed index 'x': centroid payload length mismatch` |
| fingerprint mismatch | `malformed managed index 'x': centroid checksum mismatch` |
| any non-finite centroid | `malformed managed index 'x': non-finite centroid` |
| negative sample count, or a population smaller than its own sample | `malformed managed index 'x': invalid trained row count` |
| envelope larger than 4 MiB | rejected before decode: `vector index state would exceed 4194304 bytes` |
| lookalike comment on an ordinary index | left untouched |

A database carrying a method index is Ahtola/Turso-only: `CREATE INDEX … USING …` is not parseable by
stock SQLite. That is asserted, not assumed.

## Limits

| Limit | Value |
| --- | --- |
| `dims` | 2048 (the scalar functions keep their own 1 048 576 cap) |
| `lists` | 4096 |
| `train_sample` | 256 … 65536 |
| `iters` | 16 |
| centroid state | 4 MiB |
| reranked candidates per search | 1 000 000, above which the search reads everything instead of truncating |
| indexed columns | exactly 1 |
| worst-case query | one full scan plus one centroid pass — never worse than the plan it replaced |

## Divergence from Turso

Turso's `core/index_method/toy_vector_sparse_ivf.rs` is, despite its name, **not an IVF**. It is a
sparse-component inverted index that:

- accepts only one `vector32_sparse` column and serves only `vector_distance_jaccard`;
- builds two real b-trees (`<index>_inverted_index`, `<index>_stats`), a layout Ahtola cannot port
  because it has no per-statement b-tree write API;
- prunes with three unprincipled knobs (`delta`, `scan_portion`, `scan_order`) that silently drop
  true neighbours with no recall bound and no exactness test;
- has no k-means, no centroids, no training, no rebuild or optimize, no versioned state and no cost
  model.

Ahtola's `vector` method is **an Ahtola design**, not a port of that file. What is carried over is
the method/attachment/cursor shape from `index_method/mod.rs`, the `results_materialized = true` and
`TransactionalBackingStore` declarations, and the `… ORDER BY distance LIMIT ?` query shape in both
argument orders. Turso's heuristic knobs are deliberately not ported.

Honest limits of the Ahtola implementation:

- **Sparse vectors and `jaccard` are not implemented.** Both are rejected at `CREATE INDEX` with a
  message that says why, rather than silently downgraded.
- **Approximate mode is not implemented.** `exact = 0` is rejected. There is therefore no
  configuration in which a `LIMIT`ed KNN query returns a different row set than the same query
  without the index.
- **A single invalid row disables the plan.** A table with even one `NULL` embedding falls back to
  the scan. This is a correctness requirement, not an oversight: the scalar form of the query raises
  on that row.
- **Exactness is not free.** On data with no exploitable cluster structure — uniform noise, or a
  shell where every point is equidistant from the query — the certificate reads everything. The index
  stays exact and the cost model stops choosing it; it does not silently become approximate.
- **`Optimize` is reachable only through opcode 109**, not from SQL: Ahtola has no `PRAGMA optimize`.
- **`lists` does not default to `sqrt(rows)`.** A data-dependent default would make the persisted
  envelope depend on when the index happened to be built, so the default is the constant `64`.

## Tests

| Suite | Covers |
| --- | --- |
| `ManagedVectorIndexSyntaxTests` | accept/reject matrix, every `WITH` key proven observable, inherited method-index rejections, state-size cap |
| `ManagedVectorIndexRecallTests` | every supported encoding × metric against two independent oracles, exact recall@1 across seeds, boundary ties, offsets, adversarial shells |
| `ManagedVectorIndexPlannerTests` | advertised-plan-is-executed-plan, both argument orders, the alias form, every decline, scalar error equivalence, invalid-row decline, joins/subqueries, MVCC |
| `ManagedVectorIndexDeterminismTests` | generator determinism, identical envelopes across databases, insertion-order independence, seed independence of the answer, REINDEX idempotence |
| `ManagedVectorIndexMaintenanceTests` | incremental DML, reused rowids, triggers and cascades, REINDEX, Optimize, DROP, growth-driven retraining, delta vs full rebuild |
| `ManagedVectorIndexTransactionTests` | rollback, nested savepoints, DDL rollback, MVCC declarations, fork emptiness |
| `ManagedVectorIndexDurabilityTests` | reopen, crash, VACUUM/backup, the full corruption matrix, pre-allocation size rejection, stock-SQLite non-interop |
| `ManagedVectorIndexingBridgeTests` | bit-for-bit parity between `DistanceExact` and every `vector_distance_*` across all encodings |
| `ManagedIndexMethodAotSafetyTests` | no reflection, no runtime generics, no ambient randomness or clock reads in the shipped method sources |
| `VdbeIndexMethodOpcodeTests` | opcode numbers unchanged; the vector method drives the same opcodes as FTS |
