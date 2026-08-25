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

**Status**: the initial milestone roadmap (M0–M17) is complete — every transport (pipe/stdio,
Unix domain socket, TCP, HTTP, SHM) and every optional subsystem (auth, sticky sessions,
proxy-proof, external storage, observability) from the original plan is implemented and passing
the real cross-language conformance suite. That is not the same as "bug-free" or "production
hardened" — see **Known issues** below before assuming otherwise, especially the unix/tcp
streaming item, which is a real, unresolved correctness gap, not a documentation nit.

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
  `.Http.OAuth`, `.OpenTelemetry`, `.Sentry`). `.S3`/`.Gcs` are still **empty scaffolds** — the
  `IExternalStorage`/`IUploadUrlProvider` seam they'd implement lives in `.Http` and is exercised
  by conformance today only via a fake in-repo backend (`FakeStorageBackend` in
  `conformance/QueryFarm.VgiRpc.ConformanceWorker/`), not a real S3/GCS client. Don't assume these
  two packages do anything — check before depending on them.
- `test/` — xUnit unit/integration tests, one project per `src/` project (`QueryFarm.VgiRpc.Tests`,
  `.Http.Tests`, `.Http.OAuth.Tests`, `.OpenTelemetry.Tests`, `.Sentry.Tests`).
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
- The matrix now covers every transport (pipe, Unix domain socket, TCP, HTTP, SHM-over-pipe) and
  essentially the full implemented feature surface — **except** real streaming (producer/exchange)
  calls over Unix-domain-socket/TCP, which are excluded from those two transports' CI coverage
  because they currently hang against the real reference client (`UNIX_TCP_FILTER` in
  `test_csharp_conformance.py`). See **Known issues**.

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

## Known issues

- **Real streaming (producer/exchange) calls hang against the Python reference client over a
  `NetworkStream`-backed transport — both Unix domain socket AND TCP** (ruling out a TCP-only
  Nagle/buffering theory), reproducing on the very first tick. Pipe and HTTP streaming are
  unaffected; SHM's own streaming conformance (which rides over pipe) is unaffected too. This was
  found during M17 and investigated extensively (temporary per-turn diagnostics through
  `RpcServer.ServeStreamAsync`, an `lldb`-attached thread dump showing every thread genuinely idle/
  blocked on I/O rather than spinning, two from-scratch reproductions using this port's own client
  against a real `SocketTransport.ServeUnixAsync` listener — one in-process, one against the real
  published worker binary as a separate OS process — both completing in under 150ms with **no**
  hang). So `RpcServer`/`SocketTransport` are not provably broken on their own; the exact
  byte-level interaction the *real* Python client triggers remains unresolved. Leading unconfirmed
  hypothesis: `vgi_rpc/rpc/_client.py`'s `StreamSession._write_batch` keeps the input IPC stream's
  writer open across ticks (never closing/EOS-ing between turns), and something about how this
  port's server handles that framing differs for a socket vs. a pipe. Next step for whoever picks
  this up: capture the real Python client's exact socket-level writes (a `tee`/proxy capture) and
  diff against what this port's own client sends for the identical call — much faster than
  re-deriving the above from scratch. Full write-up: `docs/roadmap.md`'s M17 entry. Tracked in
  `test_csharp_conformance.py` as `UNIX_TCP_FILTER` deliberately excluding
  `producer_stream`/`exchange_stream`/`cancel`/`*_header`/`dynamic_schema_producer`/
  `error_recovery`.
- `QueryFarm.VgiRpc.S3`/`QueryFarm.VgiRpc.Gcs` are empty scaffolds — no real backend exists yet
  (see **Solution layout** above).
- `examples/` has no actual sample apps yet.
- Not yet published to NuGet.org (see **Release process**).

## Release process

Bump `Directory.Build.props`'s root `<Version>` (one version for every package, moving in
lockstep), tag, publish a GitHub Release — `release.yml` verifies the tag matches `<Version>` and
publishes to NuGet.org. Packaging itself is verified as of `<Version>` 0.2.0 (`dotnet pack -c
Release` succeeds cleanly for all 7 publishable packages) but no tag has been pushed and nothing
has been published yet — the tag-push/publish step is a deliberately manual, human-triggered
action (hard to reverse once packages land on NuGet.org), not something to do autonomously even
under a broad "finish everything" instruction.
