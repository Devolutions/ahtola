# Managed vector scalar functions

Ahtola implements Turso-compatible vector scalar functions without a native
companion. The implementation mirrors the serialized BLOB layouts and scalar
semantics in Turso commit `277ddd050b1243bc19792e845c77f1ccd31896c8`:

- `core/vector/vector_types.rs` and `core/vector/operations/serialize.rs`
  define the dense float32/float64, sparse float32, 1-bit, and 8-bit formats.
- `core/vector/mod.rs` defines SQL argument, type, and error behavior.
- `core/vector/operations/` defines extraction, conversion, concat/slice, and
  cosine, L2, Jaccard, and negated-dot distance semantics.
- `core/function.rs`, `core/dialect/sqlite.rs`, and `core/vdbe/execute.rs`
  define the built-in names, arities, and function dispatch.

## Functions

| Function | Result |
| --- | --- |
| `vector(value)`, `vector32(value)` | Dense float32 BLOB |
| `vector64(value)` | Dense float64 BLOB |
| `vector32_sparse(value)` | Sparse float32 BLOB |
| `vector1bit(value)` | Sign-quantized 1-bit BLOB |
| `vector8(value)` | Affine-quantized 8-bit BLOB |
| `vector_extract(blob)` | Canonical bracketed text |
| `vector_concat(left, right)` | Same-type concatenated vector BLOB |
| `vector_slice(vector, start, end)` | Same-type half-open slice |
| `vector_distance_cos(left, right)` | Cosine distance |
| `vector_distance_l2(left, right)` | Euclidean distance |
| `vector_distance_jaccard(left, right)` | Jaccard distance |
| `vector_distance_dot(left, right)` | Negated dot product |

Inputs may be bracketed text or an encoded vector BLOB unless a function
specifically requires a BLOB (`vector_extract`). Distance operands must have
the same dimensions and encoded type. Text constructors reject non-finite
values. Results that are NaN under Turso's operation rules become SQL `NULL`,
matching SQLite's real-value behavior.

The implementation is scalar managed code and is NativeAOT/trimming safe.

A dense vector index is available as `CREATE INDEX … USING vector (col) WITH (…)`, built on the
managed index-method foundation and documented in
[managed-vector-index.md](managed-vector-index.md). It answers
`ORDER BY vector_distance_*(col, ?) LIMIT n` exactly — the same rows, in the same order, with the
same distance values as the unindexed query — for the `float32`, `float64`, `float8` and `float1bit`
encodings under the `l2`, `cosine` and `dot` metrics. Sparse vectors and `vector_distance_jaccard`
have no index support and are rejected at `CREATE INDEX`; they still work through the scalar
functions.
