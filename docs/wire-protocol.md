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

The official .NET Arrow library (`apache/arrow-dotnet`, NuGet `Apache.Arrow`) can't write or read
this out of the box: `RecordBatch` has no metadata property, and `ArrowStreamWriter`/
`ArrowStreamReader` never surface a RecordBatch message's `custom_metadata` field.

**What this repo does about it**: rather than hand-rolling FlatBuffers framing ourselves (the
original plan — reinventing schema/message encoding that Apache.Arrow already implements
correctly), this repo vendors `Apache.Arrow`/`Apache.Arrow.Scalars` under
`third_party/apache-arrow-dotnet/` with a small, already-written, already-tested patch applied on
top of the `v23.0.0` release tag: [apache/arrow-dotnet#283](https://github.com/apache/arrow-dotnet/pull/283),
which adds exactly `WriteRecordBatch(RecordBatch, IReadOnlyDictionary<string, string>)` and
`ArrowStreamReader.LastBatchCustomMetadata`. The patch is ~90 net lines across 2 files and reuses
all of Apache.Arrow's own serialization machinery — see
`third_party/apache-arrow-dotnet/README.md` for exactly what's patched and how to drop the
vendoring once the real PR ships in an official release. `src/QueryFarm.VgiRpc/Wire/` is a thin
layer on top of this patched writer/reader (per-batch metadata dictionaries, EOS handling,
`AnnotatedBatch`), not a from-scratch IPC implementation.

## Naming: snake_case wire names vs. PascalCase C#

Service interfaces are declared with idiomatic PascalCase C# members. The wire name defaults to a
deterministic PascalCase→snake_case conversion (with a trailing `Async` suffix stripped first), or
can be overridden explicitly with `[RpcName("wire_name")]`. See
`src/QueryFarm.VgiRpc/Attributes/RpcNameAttribute.cs`.
