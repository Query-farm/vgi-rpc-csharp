# Vendored: Apache Arrow .NET (patched for per-batch `custom_metadata`)

This directory vendors `Apache.Arrow` and `Apache.Arrow.Scalars` from
[apache/arrow-dotnet](https://github.com/apache/arrow-dotnet), **with five small patches applied
on top of the `v23.0.0` release tag** (one cherry-picked from an upstream PR, four authored directly
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

## Fourth patch: `childFields != null` assertion on a zero-field `struct</union>` (vgi-rpc-csharp-specific, found via vgi-csharp)

Also not cherry-picked from any upstream PR — found while building `vgi-csharp` (VGI, the
application protocol layered on top of this RPC framework — see `~/Development/vgi-csharp`) M4,
whose table-in-out/table-buffering wire shapes routinely encode a genuinely zero-field
`struct<>` (e.g. a "no extra arguments beyond the TABLE parameter" marker). Root cause:
`MessageSerializer.GetFieldArrowType`'s `Struct_`/`Union` cases asserted `childFields != null`
instead of null-checking like every sibling case in the same switch (`List`/`LargeList`/`Map`/etc.
all already treat `childFields == null` as an error condition to check, not an invariant to assert)
— and `childFields` is `null`, not an empty array, whenever the FlatBuffers children vector is
absent, which is exactly what a real zero-field struct produces on the wire. `Debug.Assert` failure
in a console app with no debugger attached calls `Environment.FailFast`, immediately aborting the
process (SIGABRT) — uncatchable by any caller's `try`/`catch`, and compiled out entirely in Release
builds (so this was silently invisible there, masking the bug in exactly the config most testing
happens in). Fixed by treating a `null` `childFields` as `Array.Empty<Field>()` for both cases,
matching how every other case in the switch already treats it.

If/when this vendoring is removed (see below), this fix would need to move to whatever reader the
real `Apache.Arrow` package ships — worth reporting upstream to `apache/arrow-dotnet` independently
of this port, since a genuinely empty `struct<>`/`union<>` is a legal Arrow type regardless of the
custom_metadata patch above.

## Fifth patch: `DictionaryCollector` used a batch's own schema instead of the writer's canonical one (vgi-rpc-csharp-specific, found via vgi-csharp)

Also not cherry-picked from any upstream PR — found while building `vgi-csharp` M(table-in-out
echo) fixtures, whose whole job is re-emitting an incoming `RecordBatch` unchanged
(`output.Emit(input)`). Root cause: `ArrowStreamWriter.WriteRecordBatchInternal` writes the schema
once via `WriteSchema(Schema)` (the writer's own canonical `Schema` property — the one schema every
batch in an IPC stream must conform to), assigning each dictionary-typed field an id by walking
`Schema`'s own field tree recursively (`GetDictionaryOffset` → `DictionaryMemo.GetOrAssignId(field)`
at every nesting level, including struct children). But `DictionaryCollector.Collect` — called
separately, once per batch actually written — read `recordBatch.Schema` instead, i.e. the SPECIFIC
BATCH's own schema object, and walked THAT field tree to assign/look up ids. `DictionaryMemo`'s
`_fieldToId` is a `Dictionary<Field, long>`, and `Field` has no value-equality override — it's keyed
by CLR reference identity. For a batch whose schema was constructed independently of the writer's
configured `Schema` (exactly the echo case: `input` was just decoded fresh off the incoming wire
stream, a completely separate object graph even though structurally identical), `Collect` finds no
existing entry for its own (different-instance) `Field` objects and allocates BRAND NEW ids — out of
sync with whatever id the schema message the reader already received actually promised. The reader
then either can't find the dictionary batch matching the id its schema said to expect, or receives
the wrong dictionary's data. This is invisible for a TOP-LEVEL dictionary column in most real usage
(the top-level `Schema`/`RecordBatch.Schema` field objects often happen to be the literal same
instance in practice) but reliably wrong for a dictionary-encoded (ENUM) column NESTED INSIDE A
STRUCT, where `WalkChildren`'s recursion derived the nested `Field` list from `arrayData.DataType`
(the actual batch's own struct type) rather than ever consulting the canonical schema's parallel
type tree. Fixed by threading the writer's canonical `Schema`'s `Field` objects through
`DictionaryCollector`'s entire recursive walk (`Collect`/`CollectDictionary`/`WalkChildren` all now
take a `Field`/`Schema` from the canonical tree, using `recordBatch`'s arrays only for the actual
data at each matching position) — mirroring exactly what `WriteSchema`'s own recursion already does,
so both passes now resolve to the identical `Field` instances at every nesting level.

If/when this vendoring is removed (see below), this fix would need to move to whatever writer the
real `Apache.Arrow` package ships — worth reporting upstream to `apache/arrow-dotnet` independently
of this port, since it reproduces with any RecordBatch whose schema wasn't literally derived from
the same `Schema` object the writer holds, regardless of the custom_metadata patch above.

## What's NOT vendored

Only `src/Apache.Arrow/` and `src/Apache.Arrow.Scalars/` (Arrow's own dependency of the former).
No Flight, no Parquet/C-data-interface bindings, no test projects, no multi-targeting (retargeted
to `net10.0` only — see the `.csproj` diffs against upstream for the small set of changes, mostly
just `<TargetFrameworks>` collapsing to `<TargetFramework>net10.0</TargetFramework>` and dropping
now-unneeded `netstandard2.0`/`net462` polyfill package references).

## Published as QueryFarm.Arrow / QueryFarm.Arrow.Scalars

`src/Apache.Arrow/Apache.Arrow.csproj` and `src/Apache.Arrow.Scalars/Apache.Arrow.Scalars.csproj`
are published to nuget.org under the package ids `QueryFarm.Arrow` / `QueryFarm.Arrow.Scalars` —
**not** `Apache.Arrow`/`Apache.Arrow.Scalars`, which are the real, official, unpatched packages
apache/arrow-dotnet itself publishes. This distinction is load-bearing, not cosmetic: an earlier
version of this packaging left the vendored `Apache.Arrow.csproj` unpackaged (`IsPackable=false`)
and referenced it from `QueryFarm.VgiRpc.csproj` via a plain `ProjectReference` — which builds
fine within this repo's own solution (where the ProjectReference's actual compiled output is used
directly), but `dotnet pack`'s default behavior for *any* ProjectReference (packable or not) is to
emit an inter-package `<dependency>` derived from the referenced project's own identity — and
since the vendored project file is literally named `Apache.Arrow.csproj`, that identity defaulted
to package id "Apache.Arrow", version "1.0.0" (the SDK's unset-`<Version>` fallback). The
published `QueryFarm.VgiRpc` package on nuget.org silently declared a dependency on the real,
years-old, unpatched official `Apache.Arrow` 1.0.0 — which lacks types this codebase needs
entirely aside from the custom_metadata patch (confirmed via a clean-restore reproduction:
`CS0246` on `Decimal128Type`/`IntegerType`/`Decimal128Array`, `CS1503` on `IArrowArray`/`Array`).
Anyone building against this repo's own solution (sibling checkout, or CI here) never saw this;
anyone installing `QueryFarm.VgiRpc` from NuGet with no local vendored source did, and got a
broken build. Found via `vgi-csharp`'s own NuGet-publishing prep, which was the first time
anything actually restored `QueryFarm.VgiRpc` from nuget.org without also having this repo's
source checked out alongside it.

The types genuinely are part of `QueryFarm.VgiRpc`'s public API (`RecordBatch`, `Schema`, etc.
appear directly in its method signatures), so hiding the reference entirely (`PrivateAssets="all"`
+ embedding the DLL into `QueryFarm.VgiRpc`'s own package) isn't right either — that pattern is
for a private implementation-detail dependency that never leaks into your own public surface, and
using it here broke the *local* multi-project solution build (downstream projects like
`QueryFarm.VgiRpc.Http` could no longer see `RecordBatch`/`Schema` at all, since `PrivateAssets`
also suppresses compile-time flow to sibling `ProjectReference`s within the same solution, not
just to consumers of the packed output). The fix that's actually correct: give the vendored fork
its own real, non-colliding, correctly-versioned package identity, so a plain `ProjectReference`
naturally produces a plain, resolvable inter-package dependency — the ordinary case NuGet is
designed for. `AssemblyName`/root namespace stay `Apache.Arrow` (unchanged from upstream, default
from the `.csproj` filename), so every file in this repo's own source that does `using
Apache.Arrow;` keeps compiling completely unmodified — only the NuGet-level package id changed.
This mirrors `vgi-go`'s equivalent fork (`github.com/Query-farm/arrow-go`, substituted in via a
`go.mod` `replace` directive); .NET has no exact equivalent of a module-level replace directive,
so the split here is: NuGet resolves the correct, unique package identity, while the assembly
inside keeps its original name for source compatibility.

Version is `23.0.0-queryfarm.1` — the upstream tag this fork is based on (`v23.0.0`), with a
prerelease suffix that can never collide with any real official Apache.Arrow release (present or
future), independent of `QueryFarm.VgiRpc`'s own version. Published under Authors "The Apache
Software Foundation; Query Farm LLC" with `LICENSE.txt`/`NOTICE.txt` (carried over from upstream,
unmodified) embedded in the package — standard Apache-2.0 redistribution-of-a-modified-work
practice: same license, original copyright/notice preserved, changes documented (this README).

**Practical tradeoff worth knowing about**: because the published assembly keeps the name
`Apache.Arrow.dll` (for source compatibility, as above), an application that references *both*
`QueryFarm.Arrow` (transitively, via `QueryFarm.VgiRpc`/`QueryFarm.Vgi`) *and* the real official
`Apache.Arrow` package directly would end up with two different assemblies of the same name/
namespace in its dependency graph — a real but pre-existing risk inherent to the
"vendor-a-patched-fork-until-upstream-catches-up" strategy generally (this repo already accepted
that tradeoff by vendoring in the first place); this packaging fix doesn't introduce it, it only
makes the *published* package correctly buildable, which it wasn't before.

## Removing this vendoring later

Once [#283](https://github.com/apache/arrow-dotnet/pull/283) (or an equivalent) merges upstream
and ships in an official `Apache.Arrow` NuGet release: delete this directory, remove the
`ProjectReference` to it from `src/QueryFarm.VgiRpc/QueryFarm.VgiRpc.csproj`, add back a normal
`<PackageReference Include="Apache.Arrow" />` (pin the version in `Directory.Packages.props`), stop
publishing `QueryFarm.Arrow`/`QueryFarm.Arrow.Scalars` (deprecate the existing versions on
nuget.org rather than unlisting — deprecation keeps them resolvable for anyone already pinned to
one), and update `docs/wire-protocol.md` accordingly. No other code in this repo should need to
change — the wire layer only depends on the public `WriteRecordBatch(RecordBatch,
IReadOnlyDictionary<string, string>)` and `LastBatchCustomMetadata` surface, which is exactly what
the real PR adds.

## License

Apache License 2.0, same as the rest of this repo — see `LICENSE.txt`/`NOTICE.txt` in this
directory (carried over from upstream) and this repo's own `NOTICE`.
