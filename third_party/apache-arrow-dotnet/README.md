# Vendored: Apache Arrow .NET (patched for per-batch `custom_metadata`)

This directory vendors `Apache.Arrow` and `Apache.Arrow.Scalars` from
[apache/arrow-dotnet](https://github.com/apache/arrow-dotnet), **with three small patches applied
on top of the `v23.0.0` release tag** (one cherry-picked from an upstream PR, two authored directly
in this vendoring — see below for each).

## Why this exists

Every protocol semantic in vgi-rpc (method name, versions, log/error info, stream continuation
tokens, ...) rides as `vgi_rpc.*` custom_metadata key/value pairs on individual Arrow RecordBatch
IPC messages. The stock `Apache.Arrow` NuGet package cannot write or read this: `RecordBatch` has
no metadata property, and `ArrowStreamWriter.WriteRecordBatch`/`ArrowStreamReader` never
surface a RecordBatch message's `custom_metadata` field. See `../../docs/wire-protocol.md` for the
full story, including why hand-rolling the FlatBuffers framing ourselves (the original plan) was
abandoned in favor of this approach once an existing, tested, minimal upstream patch was found.

## What's patched

Three commits cherry-picked cleanly (no conflicts) from
[apache/arrow-dotnet#283](https://github.com/apache/arrow-dotnet/pull/283)
(`cmettler/arrow-dotnet@feature/ipc-message-custom-metadata`) onto the `v23.0.0` tag:

| Commit | Summary |
|---|---|
| `a66301f` (orig `7d9c4fe`) | Expose IPC Message `custom_metadata` on `ArrowStreamReader` (`LastBatchCustomMetadata` property) |
| `2ea8afd` (orig `7c1828c`) | Add `WriteRecordBatch(RecordBatch, IReadOnlyDictionary<string,string>)` overload |
| `4cf40ba` (orig `fd4ff38`) | Cross-language round-trip tests against pyarrow (not vendored here — see below) |

The diff is small and surgical (2 files touched for the reader, 1 for the writer; ~90 net lines) and
reuses all of Apache.Arrow's existing internal serialization machinery (buffer alignment, body
writing, schema-level metadata helpers) — it only threads a `customMetadata` parameter through to
the FlatBuffers `Message.CreateMessage(...)` call that already accepts a `custom_metadata` vector
but wasn't being given one for RecordBatch messages. See the PR for the full description and
review discussion (as of writing, open — the reviewer's only requested change is updating
`ArrowFileWriter`/`FlightDataStream`, neither of which this repo uses).

The third commit's cross-language pythonnet tests were **not** vendored into this copy (no test
project here — see `test/QueryFarm.VgiRpc.Tests/Wire/` in this repo for our own coverage of the
same round-trip, exercised through `QueryFarm.VgiRpc`'s wire layer instead).

## Second patch: `ArrowIpcBodyTooLargeException` (vgi-rpc-csharp-specific, not from an upstream PR)

Unlike the custom_metadata patch above, this one is **not** cherry-picked from any upstream PR —
it's authored directly in this vendoring, for a narrower, vgi-rpc-csharp-specific reason (M17, see
`docs/roadmap.md`): the reader's own message-body-length handling threw a bare `OverflowException`
(async path) or a similarly bare one (sync path) when a message's declared body length exceeds
`int.MaxValue` — with the message *header* already fully consumed from the stream but the *body*
(potentially gigabytes) left completely unread. A caller catching that exception generically (as
`QueryFarm.VgiRpc.Server.RpcServer.ServeOneAsync` used to) has no way to tell "the stream is fine,
just refuse this one oversized message" apart from "the stream is genuinely broken" — so it always
had to assume the worse case and tear down the whole connection.

`ArrowIpcBodyTooLargeException` (`src/Apache.Arrow/Ipc/ArrowIpcBodyTooLargeException.cs`) replaces
both throw sites in `ArrowStreamReaderImplementation.cs` (`ReadMessageAsync` and `ReadRecordBatch`)
and carries the declared body length, so `QueryFarm.VgiRpc.Wire.WireReader` can drain exactly that
many bytes off the stream (keeping it in sync with the sender) before the server replies with a
normal typed wire error and keeps serving. This is what backs the conformance suite's mandatory
`large_payload.echo_binary_over_int32_max` test (a 2^31+1-byte payload — larger than any managed
`byte[]`/reader buffer on this or any .NET runtime can hold, so a typed refusal is the only
correct answer; the reference's own `_accept_typed_refusal` helper exists for exactly this case).

If/when this vendoring is removed (see below), this behavior would need to move to whatever reader
the real `Apache.Arrow` package ships — either by re-adding an equivalent patch, or by having
`WireReader` peek the message header itself before delegating to the stock reader.

## Third patch: zero-length `ReadFullBufferAsync`/`ReadFullBuffer` fast path (vgi-rpc-csharp-specific)

Also not cherry-picked from any upstream PR — authored directly in this vendoring for the M17/M18
unix/tcp streaming hang (see `docs/roadmap.md`'s M18 entry for the full investigation). Root cause:
`StreamExtensions.ReadFullBufferAsync`/`ReadFullBuffer` called `stream.ReadAsync`/`stream.Read`
with a **zero-length** buffer whenever a message's body is legitimately empty — which a
`RecordBatch` with no buffers (e.g. a zero-column schema) always is. `MemoryStream` treats a
zero-length read as the trivial no-op it logically is, but a real socket-backed `NetworkStream`
does not: a zero-byte `ReadAsync`/`Read` blocks as if waiting for the peer to send *something* (or
close), instead of returning `0` immediately. In a lockstep protocol where the peer is itself
waiting on *our* response, nothing else is ever coming, so this blocked forever — confirmed via a
zero-vgi-rpc-dependency reproducer (stock `Apache.Arrow` NuGet package, no vendored fork, real
`pyarrow` client) and pinned to the exact syscall via `strace`: the full message body had already
arrived and been read correctly; the *next*, spurious zero-length read is what hung. Go's own IPC
reader never hits this because `io.ReadFull` special-cases a zero-length buffer and returns without
issuing a read syscall at all (documented Go stdlib behavior) — this vendoring now does the same:
both `ReadFullBufferAsync` and `ReadFullBuffer` return `0` immediately when `buffer.Length == 0`,
before ever touching `stream`.

If/when this vendoring is removed (see below), this fix would need to move to whatever reader the
real `Apache.Arrow` package ships — worth reporting upstream to `apache/arrow-dotnet` independently
of this port, since it reproduces with the stock NuGet package and has nothing to do with the
custom_metadata patch above.

## What's NOT vendored

Only `src/Apache.Arrow/` and `src/Apache.Arrow.Scalars/` (Arrow's own dependency of the former).
No Flight, no Parquet/C-data-interface bindings, no test projects, no multi-targeting (retargeted
to `net10.0` only — see the `.csproj` diffs against upstream for the small set of changes, mostly
just `<TargetFrameworks>` collapsing to `<TargetFramework>net10.0</TargetFramework>` and dropping
now-unneeded `netstandard2.0`/`net462` polyfill package references).

## Removing this vendoring later

Once [#283](https://github.com/apache/arrow-dotnet/pull/283) (or an equivalent) merges upstream
and ships in an official `Apache.Arrow` NuGet release: delete this directory, remove the
`ProjectReference` to it from `src/QueryFarm.VgiRpc/QueryFarm.VgiRpc.csproj`, add back a normal
`<PackageReference Include="Apache.Arrow" />` (pin the version in `Directory.Packages.props`), and
update `docs/wire-protocol.md` accordingly. No other code in this repo should need to change — the
wire layer only depends on the public `WriteRecordBatch(RecordBatch, IReadOnlyDictionary<string,
string>)` and `LastBatchCustomMetadata` surface, which is exactly what the real PR adds.

## License

Apache License 2.0, same as the rest of this repo — see `LICENSE.txt`/`NOTICE.txt` in this
directory (carried over from upstream) and this repo's own `NOTICE`.
