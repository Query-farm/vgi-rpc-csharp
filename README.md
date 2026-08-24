<!-- markdownlint-disable MD041 -->
# vgi-rpc-csharp

A C# port of [vgi-rpc](https://github.com/Query-farm/vgi-rpc) — a transport-agnostic RPC framework
that uses Apache Arrow's IPC Streaming Format as its wire protocol, with no IDL/codegen step:
services are plain interfaces, and Arrow schemas are derived from them by reflection. This port
targets **.NET 10** and runs on **Windows and Linux**.

> **Status: early scaffold.** This repository is under active initial development against the
> milestone roadmap in [`docs/roadmap.md`](docs/roadmap.md). Nothing here is ready to depend on
> yet — see that file for what's implemented so far.

## Wire compatibility

vgi-rpc-csharp implements the same byte-level wire protocol as the canonical Python implementation
and its other ports (Go, Rust, TypeScript, Java), so peers written in any of those languages can
interoperate over pipe/stdio, Unix domain socket, TCP, or HTTP. See
[`docs/wire-protocol.md`](docs/wire-protocol.md) for the protocol summary and the one C#-specific
implementation wrinkle (Apache.Arrow's .NET library can't write per-batch `custom_metadata`, so this
repo hand-rolls that one layer of framing).

## Modules

| Package | Purpose |
|---|---|
| `QueryFarm.VgiRpc` | Core: wire framing, reflection-based dispatch, streaming, pipe/stdio/Unix/TCP/SHM transports, access log |
| `QueryFarm.VgiRpc.Http` | ASP.NET Core (Kestrel) HTTP transport, sticky sessions, proxy-proof, bearer/mTLS auth |
| `QueryFarm.VgiRpc.Http.OAuth` | JWT/JWKS validation, OAuth2/PKCE browser flow |
| `QueryFarm.VgiRpc.S3` | S3-backed external storage for large-payload offload |
| `QueryFarm.VgiRpc.Gcs` | GCS-backed external storage for large-payload offload |
| `QueryFarm.VgiRpc.OpenTelemetry` | Tracing/metrics instrumentation |
| `QueryFarm.VgiRpc.Sentry` | Error-capture instrumentation |

## Requirements

.NET 10 SDK. Runs on Windows and Linux.

## Development

See [`CLAUDE.md`](CLAUDE.md) for build/test commands, the conformance-testing workflow, and
cross-language wire-alignment notes.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
