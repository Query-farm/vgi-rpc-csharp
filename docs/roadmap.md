# Roadmap

This repo targets full feature parity with the other [vgi-rpc](https://github.com/Query-farm/vgi-rpc)
ports (Go, Rust, TypeScript, Java). Everything below is in scope — this list orders *risk and
dependency*, not what eventually ships. See `~/Development/vgi-rpc/docs/porting-guide.md` (in the
canonical Python repo) for the language-agnostic porting checklist this plan is built on.

- [x] **M0 — Wire spike.** A thin `QueryFarm.VgiRpc.Wire` layer over a vendored, patched
      `Apache.Arrow` (`third_party/apache-arrow-dotnet/`) that adds per-batch `custom_metadata`
      support the stock NuGet package lacks — see `docs/wire-protocol.md`.
- [x] **M1 — Core unary RPC engine.** Reflection-based schema derivation, `RpcServer`/
      `RpcConnection`/`DispatchProxy` client over the in-process pipe transport. (`__describe__`
      itself is not yet implemented — see the note under M2.)
- [~] **M2 — Unary conformance (in progress).** 56/105 tests in the real cross-language
      `vgi-rpc-test` suite pass against the C# worker over stdio — the full scalar/void/
      complex_types/optional/multi_param/errors/logging/boundary_values/protocol_version
      categories, plus `dataclass.echo_point`/`echo_bounding_box`/`inspect_point` and
      `annotated.*`. Confirmed empirically (not just self-consistently) against the canonical
      Python reference client. Remaining for this milestone: `dataclass.echo_all_types(_with_nulls)`
      and the wide-Arrow-type methods (need list-of-struct + temporal/decimal support in
      `ValueCodec`), `__describe__`, and `large_payload.*`. See `test_csharp_conformance.py`
      for the exact implemented-subset filter, which grows as more lands.
- [~] **M3 — Streaming (in progress).** Producer streams (5/5) and the core of exchange streams
      (7/7: scale/echo/accumulate/with_logs/error_first/error_nth/empty_session) pass the real
      conformance suite. `RpcServer.ServeStreamAsync`'s lockstep dispatch loop (one continuous
      output IPC stream + one continuous input/tick stream for the call's lifetime) handles both
      shapes uniformly — confirmed against the real Python client, not just self-consistently.
      Remaining: the `exchange_stream.cast_*` tests (need input-batch type coercion — Python's
      `_coerce_input_batch`, not yet ported), headers, cancellation, dynamic schema, and
      client-side stream consumption (`RpcConnection`/`RpcClientProxy` — not needed for
      conformance, since `vgi-rpc-test` drives our server with its own Python client, but needed
      before any C#-to-C# streaming test can be written).
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
