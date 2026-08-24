# Roadmap

This repo targets full feature parity with the other [vgi-rpc](https://github.com/Query-farm/vgi-rpc)
ports (Go, Rust, TypeScript, Java). Everything below is in scope — this list orders *risk and
dependency*, not what eventually ships. See `~/Development/vgi-rpc/docs/porting-guide.md` (in the
canonical Python repo) for the language-agnostic porting checklist this plan is built on.

- [ ] **M0 — Wire spike.** Hand-rolled Arrow IPC framing with per-batch `custom_metadata`
      (`Apache.Arrow`'s stock writer/reader can't do this — see `docs/wire-protocol.md`).
- [ ] **M1 — `__describe__` round-trip.** Reflection-based schema derivation, `RpcServer`/
      `RpcConnection`/`DispatchProxy` client over the in-process pipe transport.
- [ ] **M2 — Full unary conformance.** All `IConformanceService` unary methods; `RpcException`
      hierarchy; first NuGet dry-run pack in CI.
- [ ] **M3 — Streaming.** Producer + exchange streams, headers, cancellation.
- [ ] **M4 — Non-HTTP transports + CLI.** stdio (default), `--unix`, `--tcp`; graceful shutdown.
- [ ] **M5 — Access log.** JSONL sink matching `access_log.schema.json`.
- [ ] **M6 — Plain HTTP.** Kestrel server + `HttpClient` client; stream state tokens (AES-GCM).
- [ ] **M7 — Response caps, capability headers, content-encoding negotiation.**
- [ ] **M8 — Unauthorized-response spec + bearer auth.**
- [ ] **M9 — mTLS + JWT, CORS.**
- [ ] **M10 — Sticky sessions.**
- [ ] **M11 — Proxy proof.**
- [ ] **M12 — Token introspection.**
- [ ] **M13 — External storage (S3/GCS).**
- [ ] **M14 — SHM transport.**
- [ ] **M15 — OAuth2/PKCE browser flow.**
- [ ] **M16 — Observability (OpenTelemetry, Sentry).**
- [ ] **M17 — Full-suite conformance in CI across every transport, 2GiB payload test, perf pass,
      packaging/release, docs pass.**

Full rationale for each milestone's sequencing lives in the plan this repo was bootstrapped from;
see `CLAUDE.md` for where cross-language wire-alignment decisions are recorded as they're made.
