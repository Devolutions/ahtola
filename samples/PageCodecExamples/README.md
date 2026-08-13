# Page codec / encryption examples

Ahtola ports of Turso sample-style programs for local encryption and external
page codecs (Turso PR [#8183](https://github.com/tursodatabase/turso/pull/8183)
/ core [#8095](https://github.com/tursodatabase/turso/pull/8095)).

## What this runs

1. **Built-in encryption** — adapted from `turso-src/examples/dotnet/Encryption.cs`.
   Turso’s sample uses `AEGIS256`; managed Ahtola ships **AES-256-GCM** only, so
   the connection string uses `Encryption Cipher=aes256gcm`.
2. **External `IPageCodec`** — XOR sample equivalent to Turso’s in-tree
   `XorPageCodec` tests. Upstream has no dedicated .NET external-codec sample
   yet (bindings expose a C ABI `PageCodec` hook only).

## Run

From the repo root:

```powershell
dotnet run --project samples/PageCodecExamples/PageCodecExamples.csproj -c Debug -f net10.0
```
