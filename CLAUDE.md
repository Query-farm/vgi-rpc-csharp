# CLAUDE.md

Guidance for working in this repository.

## What this is

A from-scratch C# port of [vgi-rpc](https://github.com/Query-farm/vgi-rpc). The canonical Python
implementation lives at `~/Development/vgi-rpc` on this machine; when Python and this port
disagree on behavior, **Python is the reference** (except where explicitly noted below as an
intentional .NET-specific deviation). Sibling ports for cross-checking idioms:
`~/Development/vgi-rpc-{go,rust,typescript,java}`.

The full implementation plan (context, architecture decisions, milestone roadmap) this repo was
bootstrapped from is summarized in [`docs/roadmap.md`](docs/roadmap.md) and
[`docs/wire-protocol.md`](docs/wire-protocol.md). Read those first.

**Status**: the initial milestone roadmap (M0–M21) is complete — every transport (pipe/stdio,
Unix domain socket, TCP, HTTP, SHM) and every optional subsystem (auth, sticky sessions,
proxy-proof, external storage — including real `.S3`/`.Gcs` backends as of M20, observability)
from the original plan is implemented and passing the real cross-language conformance suite,
**streaming included on every transport** (the unix/tcp streaming gap tracked through M17 was
root-caused and fixed in M18 — see **Known issues** below).
As of M19, the full *unfiltered* reference conformance suite (not just this repo's own
`IMPLEMENTED_FILTER`) passes completely — 106/106, zero failures. That is not the same as
"bug-free" or "production hardened" — see **Known issues** below before
assuming otherwise.

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes --exclude third_party  # third_party/ is vendored, never reformat it
```

SDK version is pinned in `global.json`. Package versions are centrally managed in
`Directory.Packages.props`. Shared MSBuild settings are in `Directory.Build.props` (root) plus a
nested one per `src/`, `test/`, `conformance/`, `benchmark/`, `examples/` folder that sets
`IsPackable` appropriately.

## Solution layout

- `src/` — published packages (`QueryFarm.VgiRpc` core + optional add-ons: `.Http`,
  `.Http.OAuth`, `.OpenTelemetry`, `.Sentry`, `.S3`, `.Gcs`). `.S3`/`.Gcs` (M20) are real
  `IExternalStorage`/`IUploadUrlProvider` implementations (that seam itself lives in `.Http`) —
  `S3Storage`/`GcsStorage`, both presign PUT+GET URL pairs in addition to the plain upload path.
  Conformance still exercises the seam only via a fake in-repo backend (`FakeStorageBackend` in
  `conformance/QueryFarm.VgiRpc.ConformanceWorker/`) — that's deliberate (see M20's roadmap entry:
  the canonical Python reference's own S3/GCS unit tests mock the cloud SDKs the same way, and
  cross-language conformance was never meant to depend on real cloud credentials). `.S3`/`.Gcs`
  are instead verified by `test/QueryFarm.VgiRpc.S3.Tests`/`.Gcs.Tests` — real-protocol
  integration tests against RustFS/fake-gcs-server via Testcontainers, Docker-gated, deliberately
  **not** wired into the default `dotnet build`/`dotnet test` gate (mirrors vgi-rpc-java's own
  separate `integrationTest` Gradle source set) — run them explicitly:
  `dotnet test test/QueryFarm.VgiRpc.S3.Tests test/QueryFarm.VgiRpc.Gcs.Tests`.
- `test/` — xUnit unit/integration tests, one project per `src/` project (`QueryFarm.VgiRpc.Tests`,
  `.Http.Tests`, `.Http.OAuth.Tests`, `.OpenTelemetry.Tests`, `.Sentry.Tests`), plus the two
  Docker-gated `.S3.Tests`/`.Gcs.Tests` projects described above (not in `vgi-rpc-csharp.slnx`).
- `conformance/` — shared `IConformanceService` interface/types + the runnable conformance worker.
  Not published.
- `benchmark/` — mirrors `conformance/`'s shape for perf testing. Small and honestly scoped (a
  real measured unary-call throughput/latency number over in-process pipe dispatch), not a
  BenchmarkDotNet-based suite. Not published.
- `examples/` — scaffold only (`Directory.Build.props`, no actual sample apps yet) — the
  `ProjectReference`-based sample-app track from the original plan was never built out.

## Cross-language conformance

The acceptance gate for this port is the same one every other port uses: install
`vgi-rpc[conformance]` from the canonical Python package and drive the built
`QueryFarm.VgiRpc.ConformanceWorker` binary against it.

- **Locally**: point at the sibling repo's venv via `VGI_RPC_SITE` (path to its `site-packages`) or
  `VGI_RPC_PYTHON` (path to its Python interpreter), read by `conftest.py` — mirrors
  `vgi-rpc-java`'s `run_tests.sh`/`tests/conftest.py` pattern, since the locally checked-out Python
  repo may have unreleased protocol features ahead of what's on PyPI.
- **In CI**: `pip install "vgi-rpc[conformance,http,external]" pytest cryptography` — the `http`/
  `external` extras and the two ad-hoc packages are all real dependencies of specific test groups
  (mTLS cert generation, S3-style external-storage fetch); if a Docker-based pre-push check passes
  with a different (more permissive) local install line than `.github/workflows/ci.yml` actually
  uses, trust `ci.yml`, not the Docker run — this has bitten this repo twice (see git history /
  `docs/roadmap.md` M9, M13).
- Run via `./run_tests.sh [transport-or-keyword-filter]`, or directly:
  `python -m pytest test_csharp_conformance.py -v`. Re-run a single failing case with
  `./inspect.sh <test_id>`.
- The matrix covers every transport (pipe, Unix domain socket, TCP, HTTP, SHM-over-pipe) and the
  full implemented feature surface, streaming included on every transport — a real bug briefly
  excluded producer/exchange streaming from unix/tcp's CI coverage (`UNIX_TCP_FILTER`); it's been
  root-caused, fixed, and the carve-out retired. See **Known issues**.

## Cross-language wire alignment

Filled in as each piece lands. Key decisions so far:

- **Wire framing**: `src/QueryFarm.VgiRpc/Wire/` is a thin layer over a *vendored, patched*
  `Apache.Arrow` (`third_party/apache-arrow-dotnet/`) that adds per-batch `custom_metadata`
  support the stock NuGet package lacks — not a from-scratch FlatBuffers implementation. See
  `docs/wire-protocol.md` and `third_party/apache-arrow-dotnet/README.md`. That vendoring carries
  a *second*, self-authored patch on top (not from the upstream custom_metadata PR): a message
  whose declared body length exceeds what this reader can materialize throws
  `ArrowIpcBodyTooLargeException` (carrying the declared length) instead of a bare
  `OverflowException` with the body left unread — see that same README's "Second patch" section
  and `QueryFarm.VgiRpc.Wire.WireReader`/`QueryFarm.VgiRpc.Errors.PayloadTooLargeException` for
  how it's used (draining the unread body so the connection survives a refusal).
- **`protocol_version`** (the wire semver constant) is completely independent of this repo's NuGet
  package version (`Directory.Build.props`'s `<Version>`). Never conflate the two.
- **State-token crypto** (HTTP streaming continuation tokens): AES-256-GCM, not Python's
  XChaCha20-Poly1305 — see the "Other load-bearing decisions" rationale preserved in the original
  plan (Windows platform-support gap + .NET has no XChaCha20). This is safe because these tokens
  are transport-implementation-internal, not part of the cross-language wire contract (confirmed:
  Rust uses HMAC-signed tokens, Java uses CBOR+HMAC — every port already picked its own envelope).
- **Naming**: PascalCase C# API + `[RpcName]` attribute for wire-name overrides, default
  PascalCase→snake_case conversion. See `docs/wire-protocol.md`.
- **Width overrides**: no general `Annotated[T, ArrowType(...)]`-equivalent exists. Every scalar
  width already has a distinct CLR type to key off (`sbyte`→int8, `short`→int16, ... — see
  `SchemaDerivation`'s own type-mapping doc comment), so the only gap was `pa.large_string()`/
  `pa.large_binary()` (64-bit offsets) vs. the default `string`/`byte[]` mapping — closed with a
  narrow `[QueryFarm.VgiRpc.Reflection.LargeWidth]` parameter/return attribute, not a general
  mechanism. Don't reach for a bigger abstraction here unless a second real caller shows up.
- **`frozenset[T]`/set types**: `HashSet<T>` (`FrozenSet<T>` was considered — closer to Python's
  frozenset semantically, but has no public constructor `Activator.CreateInstance` can drive, so
  its extraction side would need bespoke plumbing `HashSet<T>` doesn't). Wire shape is `list`
  either way (Arrow has no native set type — Rust's own port makes the same simplification, just
  using `Vec<T>`), so the CLR container choice only affects the server-side build path, not
  interop. See `SchemaDerivation`/`ValueCodec`'s doc comments and `docs/roadmap.md`'s M19 entry.
- **`pa.RecordBatch` as a field value** (not a service method's own top-level param/return —
  that's unary echo, unrelated): always `binary` (embedded IPC bytes), never subject to the
  nested-dataclass two-tier struct/embedded-IPC rule — a RecordBatch isn't a dataclass-equivalent
  with properties to reflect over. See `SchemaDerivation`'s `RecordBatch` special case and
  `ValueCodec.BuildRecordBatchBinaryArray`/`ExtractRecordBatchFromBinary`.

## Platform notes

- SHM transport (`System.IO.MemoryMappedFiles.MemoryMappedFile`): resolved (M14). Named,
  backing-file-less `CreateOrOpen` throws `PlatformNotSupportedException` on Linux (confirmed in
  Docker) — this port uses an explicit `/dev/shm/<name>`-backed `CreateFromFile` path there
  instead (Windows still uses `CreateOrOpen`/`OpenExisting`; macOS gets a plain-temp-file fallback
  that only self-interoperates within this port, not with the Python reference — SHM conformance
  is skipped on macOS for that reason, not a real limitation of the Linux/Windows split
  CLAUDE.md/the plan actually targets).
- `System.Security.Cryptography.ChaCha20Poly1305` is unavailable on Windows Server versions before
  Windows Server 2022 / Windows 11 (CNG gap) — this is *why* state tokens use AES-GCM instead, not
  an oversight. Don't "fix" this back toward matching Python without re-reading this note.
- **A managed `byte[]`/`string` cannot hold more than `Array.MaxLength` (~2^31-57) elements on any
  .NET runtime**, regardless of available RAM or allocator — this is a hard CLR ceiling, not a
  configurable cap. `QueryFarm.VgiRpc.Reflection.ValueCodec`'s `ExtractLargeBinaryValue` guards
  this explicitly rather than letting it surface as an opaque `OverflowException`; keep that
  precedent in mind before adding any new code path that materializes a wire value as a single
  managed array/string.
- **`AWSSDK.S3` v4 + a custom `ServiceURL` (any non-real-AWS S3-compatible store: MinIO,
  LocalStack, RustFS, ...) has three non-obvious traps**, all confirmed directly against a real
  RustFS instance (a raw `curl --aws-sigv4` request with the identical credentials succeeded the
  whole time, proving these were never a credentials problem):
  1. Setting `AmazonS3Config.RegionEndpoint` *together with* `ServiceURL` makes **every** request
     fail `403 InvalidAccessKeyId`, even `ListBucketsAsync` (the simplest possible signed call)
     with genuinely correct credentials. Use `AuthenticationRegion` (a plain region string)
     instead whenever `ServiceURL` is set; `RegionEndpoint` alone is still correct for real AWS
     S3 (no `ServiceURL` override).
  2. `AmazonS3Config.UseHttp` does **not** affect presigned-URL generation — a plain-http
     `ServiceURL` still gets `https://` presigned URLs by default, which then fail the TLS
     handshake against a server that was never speaking TLS. Set `Protocol` on each individual
     `GetPreSignedUrlRequest` instead.
  3. Setting `GetPreSignedUrlRequest.ContentType` becomes part of what's signed — fine for a
     presign-then-immediately-upload call this port fully controls (`S3Storage.UploadAsync`), but
     wrong for a presigned URL handed to an external caller (`GenerateUploadUrlAsync`): it forces
     that caller to send the exact same `Content-Type` header or SigV4 rejects the PUT outright.
     See `QueryFarm.VgiRpc.S3.S3Storage`'s constructor/`NewPresignRequest` comments for all three.

## Known issues

- **No client-side subprocess transport, and no typed streaming or HTTP client proxy** (found
  while writing M21's examples). `RpcClientProxy<T>`/`RpcConnection<T>` only ever marshal calls
  over an already-connected `IRpcTransport` and only support unary calls
  (`RpcClientProxy<T>.Invoke` calls `CallUnaryAsync` unconditionally) — there's no
  `SubprocessTransport` to spawn a child process from the client side, and no client that consumes
  an `RpcStream<TState>` or that speaks HTTP through a typed proxy rather than raw
  `WireWriter`/`WireReader` calls. None of this is a correctness bug — server-side dispatch for
  all of these is fully implemented and conformance-tested (streaming across every transport since
  M18; every real HTTP call in this repo's own test suite already goes through `RpcHttpEndpoints`)
  — it's a client-side API surface gap, and a C# client isn't the only way to close it: a server
  built with this package is already fully drivable by any other vgi-rpc port's client (the
  conformance suite does exactly that, over every transport, via the Python reference client) —
  a from-scratch C# client for these three cases is not in scope of this gap, just one possible
  future addition. `examples/03-subprocess` and `examples/04-http` show the small amount of code
  needed to fill each gap directly in C# today; promoting either into the library proper
  (`QueryFarm.VgiRpc.Transport.SubprocessTransport`, an `IAsyncEnumerable`-based stream client, an
  HTTP `RpcConnection`-equivalent) is natural future work.
- **(RESOLVED, M18) Real streaming (producer/exchange) used to hang against the Python reference
  client over a `NetworkStream`-backed transport — both Unix domain socket AND TCP.** Root cause: a
  `RecordBatch` message body is legitimately zero bytes long whenever the batch has no buffers —
  exactly the shape of every producer-stream tick (`_TICK_BATCH` in `vgi_rpc/rpc/_types.py` is a
  permanent zero-row, zero-column batch). `Apache.Arrow`'s `StreamExtensions.ReadFullBufferAsync`/
  `ReadFullBuffer` called `stream.ReadAsync`/`stream.Read` with that zero-length buffer
  unconditionally; over a real socket-backed `NetworkStream` a zero-byte read does not complete
  immediately the way it logically should — it blocks as if waiting for the peer to send
  *something*, instead of trivially returning `0` — so the very first tick always blocked until the
  client gave up. `MemoryStream` doesn't have this quirk (a zero-length read there really is a
  no-op), which is why a first hypothesis blaming pyarrow's write side looked plausible before being
  directly disproved by `vgi-rpc-go` passing the identical case with the identical real client.
  Fixed with a third patch to the vendored Arrow fork (`third_party/apache-arrow-dotnet/README.md`)
  — both read helpers now short-circuit `buffer.Length == 0` before ever touching the stream,
  matching how Go's `io.ReadFull` already special-cases a zero-length buffer. Reproduces with the
  **stock** `Apache.Arrow` NuGet package too, so it's a real upstream `apache/arrow-dotnet` bug
  independent of this port, worth reporting there separately. Verified against the real
  `ConformanceWorker` and the real Python reference suite: unix/tcp now run the full
  `IMPLEMENTED_FILTER`, streaming included, same as every other transport — the `UNIX_TCP_FILTER`
  carve-out in `test_csharp_conformance.py` is retired. Full write-up: `docs/roadmap.md`'s M18
  entry.
- `examples/` has a scoped set of runnable sample apps (`01-hello-world` through `04-http`) — see
  the root README's Examples table. Streaming and a typed HTTP client aren't covered by an example
  yet (see this file's streaming/HTTP client notes above and in `docs/roadmap.md`).

## Release process

Bump `Directory.Build.props`'s root `<Version>` (one version for every package, moving in
lockstep), tag (`v<Version>`), publish a GitHub Release — `release.yml` verifies the tag matches
`<Version>` and publishes all 7 packages to NuGet.org via Trusted Publishing (OIDC, no stored API
key). Proven end-to-end: `<Version>` 0.2.0 was tagged, released, and all 7 packages are live on
NuGet.org under the `rustyconover` account. The tag-push/publish step is still a deliberately
manual, human-triggered action (hard to reverse once packages land on NuGet.org), not something to
do autonomously even under a broad "finish everything" instruction.
