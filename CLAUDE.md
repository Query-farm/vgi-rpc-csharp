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

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

SDK version is pinned in `global.json`. Package versions are centrally managed in
`Directory.Packages.props`. Shared MSBuild settings are in `Directory.Build.props` (root) plus a
nested one per `src/`, `test/`, `conformance/`, `benchmark/`, `examples/` folder that sets
`IsPackable` appropriately.

## Solution layout

- `src/` — published packages (`QueryFarm.VgiRpc` core + optional add-ons).
- `test/` — xUnit unit/integration tests, one project per `src/` project.
- `conformance/` — shared `IConformanceService` interface/types + the runnable conformance worker.
  Not published.
- `benchmark/` — mirrors `conformance/`'s shape for perf testing. Not published.
- `examples/` — `ProjectReference`-based sample apps.

## Cross-language conformance

The acceptance gate for this port is the same one every other port uses: install
`vgi-rpc[conformance]` from the canonical Python package and drive the built
`QueryFarm.VgiRpc.ConformanceWorker` binary against it.

- **Locally**: point at the sibling repo's venv via `VGI_RPC_SITE` (path to its `site-packages`) or
  `VGI_RPC_PYTHON` (path to its Python interpreter), read by `conftest.py` — mirrors
  `vgi-rpc-java`'s `run_tests.sh`/`tests/conftest.py` pattern, since the locally checked-out Python
  repo may have unreleased protocol features ahead of what's on PyPI.
- **In CI**: `pip install "vgi-rpc[conformance]"` from PyPI.
- Run via `./run_tests.sh [transport-or-keyword-filter]`, or directly:
  `python -m pytest test_csharp_conformance.py`. Re-run a single failing case with
  `./inspect.sh <test_id>`.
- The conformance matrix grows incrementally as milestones land (pipe-only until M2; +streaming at
  M3; +unix/tcp at M4; +http at M6; ...) — see `docs/roadmap.md`.

## Cross-language wire alignment

Filled in as each piece lands. Key decisions so far:

- **Wire framing**: hand-rolled outer `Message`/`custom_metadata` layer (`src/QueryFarm.VgiRpc/Wire/`)
  because `Apache.Arrow`'s stock IPC writer/reader can't express per-batch metadata. See
  `docs/wire-protocol.md`.
- **`protocol_version`** (the wire semver constant) is completely independent of this repo's NuGet
  package version (`Directory.Build.props`'s `<Version>`). Never conflate the two.
- **State-token crypto** (HTTP streaming continuation tokens): AES-256-GCM, not Python's
  XChaCha20-Poly1305 — see the "Other load-bearing decisions" rationale preserved in the original
  plan (Windows platform-support gap + .NET has no XChaCha20). This is safe because these tokens
  are transport-implementation-internal, not part of the cross-language wire contract (confirmed:
  Rust uses HMAC-signed tokens, Java uses CBOR+HMAC — every port already picked its own envelope).
- **Naming**: PascalCase C# API + `[RpcName]` attribute for wire-name overrides, default
  PascalCase→snake_case conversion. See `docs/wire-protocol.md`.

## Platform notes

- SHM transport (`System.IO.MemoryMappedFiles.MemoryMappedFile`): named, backing-file-less
  `CreateOrOpen` needs empirical verification on Linux before committing to one code path vs. an
  explicit `/dev/shm/vgi-rpc-<name>`-backed `CreateFromFile` fallback. Not yet resolved — see
  `docs/roadmap.md` M14.
- `System.Security.Cryptography.ChaCha20Poly1305` is unavailable on Windows Server versions before
  Windows Server 2022 / Windows 11 (CNG gap) — this is *why* state tokens use AES-GCM instead, not
  an oversight. Don't "fix" this back toward matching Python without re-reading this note.

## Release process

Bump `Directory.Build.props`'s root `<Version>` (one version for every package, moving in
lockstep), tag, publish a GitHub Release — `release.yml` verifies the tag matches `<Version>` and
publishes to NuGet.org. Don't publish until Milestone 2 (first green unary conformance run).
