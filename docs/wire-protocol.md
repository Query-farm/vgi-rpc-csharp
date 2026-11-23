# Wire protocol

vgi-rpc-csharp implements the same wire protocol as every other vgi-rpc port. The normative,
language-agnostic spec lives in the canonical Python repository, not here.

**Which Python repository.** The canonical one is
[`vgi-rpc-python`](https://github.com/Query-farm/vgi-rpc-python) — locally
`~/Development/vgi-rpc-python`. A `~/Development/vgi-rpc` checkout is a *different, older* tree:
it predates the multiservice work, so it has no routing key, flat rather than protocol-namespaced
routes, and a live `__describe__`. Its version number is numerically **higher**, which makes it
read as the newer of the two; it is not. Reading a spec there describes a protocol this port no
longer speaks. Set `VGI_RPC_PYTHON` to pick the interpreter the test harness uses (see
`conftest.py`), rather than relying on whichever sibling checkout happens to exist.

- `~/Development/vgi-rpc-python/docs/WIRE_PROTOCOL.md` — byte-level spec (framing, metadata keys, type
  mapping, HTTP endpoints/headers/tokens, SHM header format).
- `~/Development/vgi-rpc-python/docs/porting-guide.md` — the "port to a new language" checklist this repo
  is being built against.
- `~/Development/vgi-rpc-python/docs/access-log-spec.md`, `docs/sticky-sessions-spec.md`,
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

The *protocol* name follows the same declare-or-derive shape one level up: it defaults to the
contract type's name with C#'s `I` prefix stripped (`IGreeter` → `Greeter`), and
`[ProtocolName("vgi.v2")]` declares it instead. Both the server (which hosts under the name) and
the clients (which address it) go through `WireNaming.ForProtocol`, so the two cannot drift. A
declared name is checked against the §3.1 grammar and refused if it claims the reserved `vgi_rpc.`
prefix; the check runs where the name is resolved, i.e. at construction. See
`src/QueryFarm.VgiRpc/Attributes/ProtocolNameAttribute.cs`.

Declaring matters because a derived name is an accident of the local type system, which differs per
language: six implementations of the VGI protocol derived four different names (`VgiProtocol`,
`VgiService`, `Service`, `vgi`), which stayed invisible until `vgi_rpc.protocol` became a required
routing key. A dot-qualified name like `vgi.v2` is additionally unspellable as a C# identifier.

## Protocol 3.4: StateRef, argument references, the object store

The contract is the reference's `tools/cross-port/specs/STATE_REF_V1.md`. This port is
**single-replica in 3.4** (§5.4): HTTP stream state stays in server memory (`StreamCallRegistry`)
and the stream token seals only a 16-byte call id, so:

- `ICallContext.StateRef(byte[])` / `StateRef<T>(T)` and `Resolve(ref)` / `Resolve<T>(ref)` have
  the spec's shape, so worker code is portable, but a reference is always held in memory
  (`Form == "memory"`): nothing is encoded, stored, sealed per turn or fetched. `StateRef` is valid
  only on a stream method's own context; every turn (the producer tick folded into `/init`
  included), cancel and unary call throws `state_ref_misuse`. Within one init, byte-equal values
  (or the same record instance) give the same reference.
- A reference presented as bytes (`StateRef.FromEncoded`) resolves when it is inline
  (`0x01 || codec || body`, `TokenCodec`); a stored tag (`0x02`) or an unknown form is
  `invalid_state_ref`, answered HTTP 400.
- The cursor rule, tag expiry and the call token's `schemas_ref` do not apply. A
  `max_request_bytes` below 16384 logs the spec's startup warning; `VGI_RPC_STATE_REF_INLINE_BYTES`
  is validated (positive integer) though no reference is ever encoded.
- **Compression before sealing:** no token this port seals carries a payload (call id, sticky
  session id, argument reference), so the codec applies only to inline `StateRef` bytes.
- **Maximum stream duration:** `MapVgiRpc(tokenTtl:)`, default 21600 s from `/init` (zero
  disables), alongside the 10-minute idle eviction.

Implemented in full:

- **Object store** (`IObjectStore.PutObjectAsync` / `GetAsync(objectId, maxBytes)`,
  `ObjectMissingException`), a separate interface beside `IExternalStorage`; `S3Storage`,
  `GcsStorage` and the conformance fake storage implement both. `ExternalizationOptions.ObjectStore`
  overrides which one resolves references (default: the storage, else the upload-URL provider).
- **Grant columns:** `__upload_url__/init` appends `object_id` and `ref`. `ref` is sealed with the
  token key under `"vgi_rpc.argref.v1\0" || ComputeCallAad(identity, "\0server")`, the identity
  the grant was authenticated with by the same delegate as every protocol method (peer-evidence
  binding included). Providers must return `UploadUrl.ObjectId`; a grant without one is refused.
  The envelope is this port's AES-256-GCM `Crypto`, not XChaCha20: references never cross ports.
- **Argument-reference resolution:** `ICallContext.ResolveArgumentRefAsync(ref, size, sha256)`
  (and the primitive `ArgumentRefs.ResolveAsync`) opens the ref for the caller, checks the sealed
  size and SHA-256 against the claimed ones, then fetches through the object cache and verifies.
  Failures are `invalid_arguments_ref`, `arguments_ref_integrity`, `arguments_ref_missing` and
  `storage_unavailable`. Byte-stream transports vend no grants and refuse it.
- **Object cache** (`ObjectCache.Default`): keyed by `object_id`, single-flight, byte-bounded
  LRU (`VGI_RPC_OBJECT_CACHE_BYTES`, default `min(1 GiB, 40%` of the cgroup or available memory)),
  entries older than one day are misses.
- **Fix 1:** with an upload-URL provider, `VGI-Max-Upload-Bytes` is always advertised and never
  exceeds the fetch cap: unset, it is the fetch cap; above an explicit `FetchConfig.MaxFetchBytes`,
  `MapVgiRpc` refuses to start; above the default fetch cap, the fetch cap is raised to it.

The shared conformance cases that need serialized state skip with `SINGLE_REPLICA_SKIP_REASON`;
`conftest.py`'s `EXPECTED_SKIP_REASONS` lists every allowed skip, and any other fails the run.
