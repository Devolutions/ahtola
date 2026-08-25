# Ahtola page-encryption contract

This document is the normative description of Ahtola's encrypted page format and
of the cryptographic primitives behind it. Everything here is an on-disk or
security contract: changing any constant changes the file format, and changing
any invariant weakens the guarantee.

The format is Turso's, pinned at the `turso-src` submodule
(`v0.8.0-pre.7`, commit `277ddd050`), primarily
`turso-src/core/storage/encryption.rs`.

## 1. Cipher table

Format version 0 defines eight ciphers. The numeric value of
`Ahtola.Core.Storage.AhtolaEncryptionCipher` **is** the on-disk cipher id, so the
enum and the header byte can never drift apart.

| Cipher id | Name | Key | Nonce | Tag | Reserved bytes / page |
| --------: | ---- | --: | ----: | --: | --------------------: |
| 1 | AES-128-GCM | 16 | 12 | 16 | 28 |
| 2 | AES-256-GCM | 32 | 12 | 16 | 28 |
| 3 | AEGIS-256   | 32 | 32 | 16 | 48 |
| 4 | AEGIS-256X2 | 32 | 32 | 16 | 48 |
| 5 | AEGIS-256X4 | 32 | 32 | 16 | 48 |
| 6 | AEGIS-128L  | 16 | 16 | 16 | 32 |
| 7 | AEGIS-128X2 | 16 | 16 | 16 | 32 |
| 8 | AEGIS-128X4 | 16 | 16 | 16 | 32 |

Reserved bytes are always `tag + nonce`. **The tag is always 16 bytes.** AEGIS
also defines 256-bit tags, but Turso instantiates every AEGIS cipher as
`::<16>`; accepting a 32-byte tag would silently change the reserved-byte
arithmetic and produce files Turso cannot read. Ahtola rejects any other tag
length.

Accepted connection-string spellings match Turso's `TryFrom<&str>`:
`aes128gcm` / `aes-128-gcm` / `aes_128_gcm`, and likewise `aegis256`,
`aegis256x2`, `aegis256x4`, `aegis128l`, `aegis128x2`, `aegis128x4`.

### ChaCha20-Poly1305 is remote-only, by design

`ChaCha20Poly1305` appears in `AhtolaRemoteEncryptionCipher` because Turso Cloud
accepts it as a **server-side** cipher name. It has no `CipherMode` member, no
cipher id, no page-1 header byte, and no page framing anywhere in the pinned Rust
engine: the key travels in the `x-turso-encryption-key` header and the server
does the crypto.

Ahtola therefore:

* keeps it as a valid remote descriptor (28 reserved bytes, wire name
  `chacha20poly1305`), and
* **fails closed** in `ManagedReplicaEncryption.EnsureSupportedCipher`, because a
  managed embedded replica has to decode pages locally.

Do not assign it a local cipher id. Any value picked here would collide with a
future upstream assignment and create files no Turso build could read.

## 2. Page framing

Every encrypted page is exactly `PageSize` bytes and is framed as:

```
non-header page: | ciphertext (PageSize - metadata) | tag (16) | nonce (N) |
page 1:          | header (100) | ciphertext (PageSize - 100 - metadata) | tag (16) | nonce (N) |
```

Page 1's visible 100-byte header is:

| Offset | Bytes | Content |
| -----: | ----: | ------- |
| 0  | 5  | `AHTLA` magic (Turso writes `Turso`; this 5-byte magic is Ahtola's one intentional divergence) |
| 5  | 1  | format version, always `0` |
| 6  | 1  | cipher id |
| 7  | 9  | reserved, must be zero |
| 16 | 84 | SQLite header bytes 16..100, copied verbatim |

That whole 100-byte header is the AEAD **associated data** for page 1. Every
other page has empty associated data. Decryption restores the plaintext SQLite
magic and zero-fills the reserved tail.

A fresh random nonce is drawn for every write (`RandomNumberGenerator.Fill`),
mirroring Turso's `generate_secure_nonce`. Nonces are never derived from the page
number: AEGIS nonce reuse allows state recovery and forgery, which is strictly
worse than the equivalent GCM failure.

## 3. Validation, and the no-fallback rule

`AhtolaEncryptedPageFormat.ValidateEncryptedHeader` rejects, in order:

1. a header shorter than 16 bytes;
2. a missing `AHTLA` magic (including a plaintext SQLite database);
3. any format version other than 0;
4. a cipher id outside 1..8;
5. a cipher id that is not the configured one;
6. non-zero bytes at offsets 7..15;
7. a reserved-space byte (offset 20) that disagrees with the configured cipher's
   metadata size.

Check 7 exists because a reserved-byte mismatch is otherwise silent at open time
and only surfaces as a per-page authentication failure much later.

There is **no** cipher inference and **no** plaintext fallback. If the configured
cipher does not match the file, the open fails.

## 4. AEGIS implementation

`src/Ahtola.Core/Storage/Crypto` contains a pure-managed AEGIS implementation:

| File | Contents |
| ---- | -------- |
| `AhtolaAesRound.cs` | the AES round function and its three implementations |
| `AegisParameters.cs` | `C0`/`C1`, context separators, lane load/store |
| `Aegis128X.cs` | AEGIS-128L (degree 1), AEGIS-128X2, AEGIS-128X4 |
| `Aegis256X.cs` | AEGIS-256 (degree 1), AEGIS-256X2, AEGIS-256X4 |
| `AhtolaAead.cs` | `IAhtolaAead`, the AES-GCM wrapper, the AEGIS wrapper, the factory |

The algorithms follow the CFRG specification
[`draft-irtf-cfrg-aegis-aead`](https://datatracker.ietf.org/doc/draft-irtf-cfrg-aegis-aead/)
("The AEGIS Family of Authenticated Encryption Algorithms"). That document
defines AEGIS-128X and AEGIS-256X at degree 1 to be exactly AEGIS-128L and
AEGIS-256, because the per-lane context separator `Byte(0) || Byte(0)` is all
zero, so one implementation covers three ciphers per family.

X2 and X4 are **not** "AEGIS run twice". They are parallel evaluations with
per-lane context separators XOR-ed into two state blocks before every
initialization update, and the per-lane tags are XOR-folded into one 128-bit tag.

### AES round implementations

`AESRound(in, rk)` is `SubBytes -> ShiftRows -> MixColumns -> AddRoundKey`
(FIPS-197 5.1). Three implementations are selected inside the method body so the
JIT and ILC can constant-fold the `IsSupported` probes:

* **x86/x64** — `AESENC`, which is
  `ShiftRows -> SubBytes -> MixColumns -> AddRoundKey`. `SubBytes` is byte-wise so
  it commutes with `ShiftRows`, making `AESENC` exactly `AESRound`.
* **Arm** — `AESE` applies `AddRoundKey` *first*, so it must be called with a
  zero round key and the real key XOR-ed in after `AESMC`:
  `MixColumns(AESE(state, 0)) ^ rk`. Passing the round key straight to `AESE`
  silently produces wrong output.
* **Software** — a table-free bitsliced round using the Boyar-Peralta
  combinational S-box (eprint 2009/191) over eight 16-bit bit planes, with
  `ShiftRows` and `MixColumns` as fixed bit permutations. A table-driven AES would
  turn every machine *without* AES instructions into a cache-timing oracle, which
  is exactly the machine class that needs the protection most.

Both paths are proven byte-identical by the suite; the software path is not a
"best effort" fallback.

## 5. Security invariants

These are testable properties, not aspirations.

1. **Fixed-time tag comparison.** Only
   `CryptographicOperations.FixedTimeEquals`. Never `SequenceEqual`, never an
   early return on a partial mismatch.
2. **Unverified plaintext is never released.** `IAhtolaAead.TryDecrypt` zeroes the
   destination and returns `false`; the page layer turns that into
   `InvalidDataException`. This is required by the AEGIS specification's
   "Implementation Security" section.
3. **No secret-dependent branches or indexing.** The software AES round performs
   no array lookup indexed by a secret byte and no data-dependent branch. The
   AEGIS modes branch only on lane counts and buffer lengths.
4. **Key material is zeroed.** Every AEAD holds its key in a `byte[]` zeroed on
   `Dispose`. AEGIS state, keystream and length blocks live in `stackalloc`
   buffers cleared in a `finally`. Key and state buffers are never pooled: pool
   reuse leaks material across callers.
5. **Random nonces only.** See section 2. The fixed-nonce seam used by the format
   fixtures lives in the test project and is never public.
6. **No secrets in diagnostics.** Keys, nonces, tags and plaintext are never
   logged or included in exception messages, mirroring Turso's `Debug` redaction.
7. **Little-endian length encoding.** AEGIS absorbs
   `LE64(ad_len_bits) || LE64(msg_len_bits)` through `BinaryPrimitives`, never
   `BitConverter`, so the implementation is correct on big-endian hosts. SQLite
   header fields stay big-endian, as SQLite defines them.
8. **Thread safety is explicit.** One `EncryptionPageCodec` is shared by every
   pager thread, so `IAhtolaAead` implementations keep no mutable per-operation
   state. The AEGIS AEAD is stateless apart from its key. The AES-GCM wrapper
   constructs a fresh `AesGcm` per call, because on Unix the BCL type holds a
   mutable OpenSSL cipher context.
9. **NativeAOT and trimming.** No reflection, no `MakeGenericType`, no dynamic
   codegen. The two AES-round policies are empty structs behind a
   `static abstract` interface, so the constrained call devirtualizes and ILC
   keeps both statically reachable.

## 6. Vector provenance

`src/Ahtola.Tests/AegisKnownAnswerTests.cs` pins the "Test Vectors" appendix of
`draft-irtf-cfrg-aegis-aead`: the `AESRound` vector, the AEGIS-128L and AEGIS-256
vectors 1-5, their negative vectors 6-9, and the AEGIS-128X2, AEGIS-128X4,
AEGIS-256X2 and AEGIS-256X4 vectors. Those are authoritative and independent of
both Turso and this port -- nothing in the suite was generated by Ahtola, by
`cargo`, or by the `aegis` Rust crate.

`src/Ahtola.Tests/AegisReferenceImplementation.cs` is a second, deliberately
different transcription of the same pseudocode (byte arrays and a table-driven
S-box instead of `Vector128` and a bitsliced circuit). It exists only so
`AegisDifferentialTests` can compare the two on random inputs at every block
alignment. It is test-only scaffolding and must never move into production, since
its S-box lookup is precisely the timing oracle production avoids.

## 7. Browser behaviour

SubtleCrypto exposes AES-GCM only, so:

* cipher ids 1 and 2 keep going through Web Crypto with an extractable-key-free
  handle -- unchanged behaviour;
* cipher ids 3 through 8 go through `AhtolaManagedAegisPageCipher`, which wraps
  the same pure-managed AEGIS core and completes its `ValueTask` synchronously.
  There is no JavaScript interop on that path.

**Performance note.** WebAssembly has no AES round instruction: both
`System.Runtime.Intrinsics.X86.Aes.IsSupported` and its Arm counterpart are false
there, and `Vector128.IsHardwareAccelerated` only maps to wasm SIMD when
`WasmEnableSIMD` is on. AEGIS in the browser therefore runs on the bitsliced
software round and is materially slower than AES-GCM through Web Crypto, which
reaches native code. Choose AEGIS in the browser for on-disk compatibility with an
AEGIS database, not for throughput; prefer AES-256-GCM when the format is yours to
pick.

## 8. MVCC logical log

The MVCC logical log has its own `MVTX` chunk framing with a fixed 16-byte tag
and 12-byte nonce baked into every payload-size and CRC computation. Turso
defines no logical-log framing for the wider AEGIS nonces, so rather than invent
one, `MvccLogicalLog` refuses any cipher whose nonce is not 12 bytes. That is a
deliberate fail-closed boundary, not an oversight.

The refusal is applied at the `PRAGMA journal_mode` boundary — before
journal-mode header 255 is persisted and before any transaction produces a frame
— so a database is never left switched into a mode it can never commit in.
`MvccLogicalLog.ThrowIfMvccUnsupported` resolves the reason from the file system
in use:

- `AhtolaEncryptionFileSystem` reports its configured cipher directly.
- A backend that encrypts *out of band* — notably the browser mirror, which
  encrypts on its way to OPFS rather than through `AhtolaEncryptionFileSystem` —
  declares the restriction through the `IMvccJournalModePolicy` capability.

Both produce the same `NotSupportedException` text, naming the cipher and its
nonce width, so desktop and browser cannot disagree about which databases may
enter MVCC.
