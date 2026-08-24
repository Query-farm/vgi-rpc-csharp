# Vendored: Apache Arrow .NET (patched for per-batch `custom_metadata`)

This directory vendors `Apache.Arrow` and `Apache.Arrow.Scalars` from
[apache/arrow-dotnet](https://github.com/apache/arrow-dotnet), **with one small patch applied on
top of the `v23.0.0` release tag.**

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
