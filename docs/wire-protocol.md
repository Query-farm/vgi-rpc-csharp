# Wire protocol

vgi-rpc-csharp implements the same wire protocol as every other vgi-rpc port. The normative,
language-agnostic spec lives in the canonical Python repository, not here:

- `~/Development/vgi-rpc/docs/WIRE_PROTOCOL.md` — byte-level spec (framing, metadata keys, type
  mapping, HTTP endpoints/headers/tokens, SHM header format).
- `~/Development/vgi-rpc/docs/porting-guide.md` — the "port to a new language" checklist this repo
  is being built against.
- `~/Development/vgi-rpc/docs/access-log-spec.md`, `docs/sticky-sessions-spec.md`,
  `docs/proxy-proof-spec.md`, `docs/unauthorized-spec.md` — feature-specific normative specs.

## The one C#-specific wrinkle: Apache.Arrow can't write per-batch `custom_metadata`

Every protocol semantic in vgi-rpc (method name, versions, log/error info, stream continuation
tokens, SHM/external-storage pointers, ...) rides as `vgi_rpc.*` **custom_metadata key/value pairs
on individual Arrow RecordBatch IPC messages** — not in the payload, not in a bespoke header.

The official .NET Arrow library (`apache/arrow-dotnet`, NuGet `Apache.Arrow`) cannot write or read
this. Its `RecordBatch` type has no metadata property, `ArrowStreamWriter.WriteRecordBatch` never
emits a `custom_metadata` field, and its generated FlatBuffers types (`Apache.Arrow.Flatbuf.*`) are
all `internal` with no `InternalsVisibleTo` — unlike Rust's `arrow-ipc`, which re-exports its
generated types as `pub` specifically so `vgi-rpc-rust` can hand-assemble `Message` flatbuffers
with metadata. There is no supported way to reuse Apache.Arrow's internals for this from outside
its own assembly.

**What this repo does about it**: depend on the public `Google.FlatBuffers` NuGet package directly,
and generate/hand-write a small internal set of wire-level FlatBuffers table types (`Message`,
`KeyValue`, `Schema`, `Field`, `RecordBatch`, `FieldNode`, `Buffer`) under
`src/QueryFarm.VgiRpc/Flatbuf/`, sourced from Arrow's own `Schema.fbs`/`Message.fbs`
(see `scripts/fetch-arrow-format.sh`). Everything *above* the outer `Message` wrapper — column
buffer layout, validity bitmaps, offsets, dictionary encoding, all concrete array types — still
uses stock `Apache.Arrow` (`RecordBatch`, `IArrowArray`, `Schema`, `ArrayData`), which is public
and complete. Only the outer framing is hand-rolled. See `src/QueryFarm.VgiRpc/Wire/` for the
implementation and `test/QueryFarm.VgiRpc.Tests/Wire/` for the byte-fidelity tests that guard it
(including a direct byte-for-byte comparison against stock `Apache.Arrow.Ipc.ArrowStreamWriter`
output for the no-metadata case).

## Naming: snake_case wire names vs. PascalCase C#

Service interfaces are declared with idiomatic PascalCase C# members. The wire name defaults to a
deterministic PascalCase→snake_case conversion (with a trailing `Async` suffix stripped first), or
can be overridden explicitly with `[RpcName("wire_name")]`. See
`src/QueryFarm.VgiRpc/Attributes/RpcNameAttribute.cs`.
