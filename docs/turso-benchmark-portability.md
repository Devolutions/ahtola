# Turso benchmark portability

This inventory maps the benchmark corpus in the read-only Turso
`v0.8.0-pre.7` submodule (`277ddd050`) to Ahtola's managed benchmark suite.
Ports preserve the measured performance question and lifecycle boundary. A
managed adaptation is labeled when ADO.NET necessarily measures more than the
corresponding Rust internal API.

| Turso source | Workload | Ahtola disposition |
| --- | --- | --- |
| `core/benches/write_perf_benchmark.rs` | Index impact, transaction size, key patterns, UPDATE, DELETE, large commits, synchronous modes | Direct SQL port; write-suite priority |
| `core/benches/triggers.rs` | Row count, width, trigger count, BEFORE/AFTER triggers | Direct SQL port |
| `core/benches/create_index_benchmark.rs` | Total and commit-only index creation | Direct SQL port |
| `core/benches/benchmark.rs` | Schema open, ALTER, prepare, reads, inserts, blobs, WAL/MVCC concurrency | Direct SQL plus managed concurrency/fixture adaptation |
| `core/benches/count_benchmark.rs` | Indexed COUNT, filtered COUNT, GROUP BY | Direct SQL port |
| `core/benches/select_star_benchmark.rs` | Wide scans under WAL and MVCC | Direct SQL port |
| `core/benches/fts_benchmark.rs` | Cold/warm search, selectivity, ingest, commit/merge churn | Direct Ahtola index-method port |
| `core/benches/fts_comparison_benchmark.rs` | Ahtola-style FTS versus SQLite FTS5 | Direct SQL port with storage-model labels |
| `core/benches/graph_queries_benchmark.rs` | Analyzed/unanalyzed graph queries | Managed deterministic-fixture adaptation |
| `core/benches/tpc_h_benchmark.rs` | Supported TPC-H query execution | Managed asset and supported-query adaptation |
| `core/benches/json_benchmark.rs` | JSONB conversion and JSON Patch | Direct SQL port |
| `core/benches/mvcc_benchmark.rs` | Transaction lifecycle, reads, updates, invisible versions | Public `BEGIN CONCURRENT` adaptation |
| `core/benches/mvcc_recovery_benchmark.rs` | Logical-log replay by frames, bytes, and operations | Direct managed-MVCC port |
| `core/benches/prepare_benchmark.rs` | Query-shape and analytical-corpus preparation | `DbCommand.Prepare` proxy; includes binding/planning |
| `core/benches/prepare_params_benchmark.rs` | Preparation with 200/500/1,000 parameters | Direct `DbCommand.Prepare` port |
| `core/benches/record_recycling.rs` | Sort, aggregate, window, and MVCC allocation paths | SQL port with Ahtola plan assertions |
| `core/benches/logical_log_serialization.rs` | Logical-log serialization | Narrow managed-internal adaptation |
| `core/benches/sql_functions/datetime.rs` | Date/time functions and modifiers | SQL scalar adaptation |
| `core/benches/sql_functions/likeop.rs` | LIKE/GLOB pattern shapes and cache behavior | SQL scalar adaptation; cache isolation unavailable |
| `core/benches/sql_functions/numeric.rs` | Numeric parsing, conversion, formatting, arithmetic | SQL scalar adaptation |
| `core/benches/sql_functions/value.rs` | String, blob, conditional, math, and value operations | SQL scalar adaptation |
| `sqlite/parser/benches/parser_benchmark.rs` | Lexer/parser complexity and INSERT batches | Prepare proxy; public API includes bind/plan |
| `perf/query-batch/benches/query_batch.rs` | Result materialization at 10/100/1,000 rows | Direct ADO.NET reader port |
| `perf/memory/codspeed/benches/memory_profiles.rs` | WAL/MVCC insert, mixed, scan, recursive, blob, churn | Managed macrobenchmark adaptation |
| `perf/connection/*` | Open, connect, and point-prepare latency | Direct port with explicit open boundary |
| `perf/latency/*` | Multitenant read latency | Task/thread adaptation |
| `perf/throughput/*` | Concurrent write throughput and checkpoint modes | Task/thread and managed-mode adaptation |
| `perf/checkpoint-bench/*` | Checkpoint throughput, latency, conflicts, and file growth | Managed orchestration/reporting adaptation |
| `perf/encryption/*` | Plain versus encrypted mixed transactions | Ahtola encryption-options adaptation |
| `perf/memory/*` | Heap, working set, allocation, and storage growth | .NET GC/process metrics adaptation |
| `core/benches/hash_spill_benchmark.rs` | Rust internal hash table and spill mechanics | Not applicable: no equivalent stable Ahtola API |
| `core/benches/alloc_collections.rs` | Rust allocator-backed collections | Not applicable: Rust implementation comparison |
| `core/benches/struct_union_benchmark.rs` | STRUCT/UNION SQL types | Not applicable until Ahtola implements these types |
| `core/benches/struct_union_profile.rs` | STRUCT/UNION profiling | Not applicable until Ahtola implements these types |

## Measurement contract

- Microsoft.Data.Sqlite is the same-run behavioral and native-performance
  baseline for portable SQL workloads.
- Historical Ahtola results from the same environment class are the regression
  baseline. Cross-machine ratios are informational only.
- Fixtures use fixed seeds and validate row counts or checksums before timing.
- Statement-only, transaction, connection-open, recovery, and end-to-end costs
  are separate benchmark cases.
- Setup, reset, file copying, data generation, and correctness checks remain
  outside timed methods.
- Every benchmark is assigned a category and upstream source reference.
- Rust-only exclusions remain visible here rather than being represented by a
  misleading .NET proxy.
