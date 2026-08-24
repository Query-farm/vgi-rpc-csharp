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
- [x] **M2 — Unary conformance.** Every unary conformance category passes against the real
      `vgi-rpc-test` tool over stdio, confirmed empirically (not just self-consistently) against
      the canonical Python reference client: scalar/void/complex_types/optional/multi_param/
      errors/logging/boundary_values/protocol_version/annotated, `dataclass.*` (including
      `echo_all_types(_with_nulls)`, unblocked by M3's list-of-struct work). `__describe__` is
      still a stub (not yet needed by conformance — deferred).
- [x] **M2, continued — wide Arrow types.** `echo_int8/int16/uint8/uint16/uint32/uint64` (already
      natively CLR-type-mapped in `SchemaDerivation` — `sbyte`/`short`/`byte`/`ushort`/`uint`/
      `ulong`, no new work needed there), `echo_date` (`DateOnly`), `echo_timestamp` (naive
      `DateTime`, `pa.timestamp("us")`, no tz) / `echo_timestamp_utc` (`DateTimeOffset`,
      `pa.timestamp("us", tz="UTC")` — two distinct CLR types for two distinct wire shapes, fixing
      a prior bug where both mapped to the UTC-tagged form), `echo_time` (new: `TimeOnly` →
      `Time64Type`, not previously mapped), `echo_duration` (`TimeSpan`), `echo_decimal`
      (`decimal` → `Decimal128Type(20, 4)`, hardcoded to the one shape the conformance protocol
      currently exercises — a real per-field precision/scale override needs an attribute
      mechanism, deferred). **Not yet exercised by any `vgi-rpc-test --filter` category** — these
      methods exist only in the `__describe__` test's expected-methods set and the
      not-yet-conformance-tested `echo_wide_types` composite upstream — so verified directly
      instead: `test_csharp_conformance.py::test_wide_arrow_types_round_trip` drives all twelve
      through the real Python client via `SubprocessTransport` (not a hand-rolled `Popen` — an
      unbuffered raw pipe read can come up short of what Arrow's schema-length prefix promised,
      which looks exactly like a server-side framing bug until you notice the client didn't wrap
      it in a `BufferedReader`; cost real debugging time to rule out). Still deferred:
      `echo_large_string`/`echo_large_binary`/`echo_fixed_binary`/`echo_dict_encoded_string`
      (need an attribute-based Arrow-type-override mechanism — `string`/`byte[]` already mean the
      default-width shape, so there's no distinct CLR type to hang large/fixed/dict-encoded
      variants off the way there was for the integer widths), `pack_nested_containers`/
      `echo_status_list` (need a `frozenset`→Arrow-list-or-set mapping and, for
      `NestedContainers.tagged_batch`, an embedded-`RecordBatch`-as-field mechanism distinct from
      the existing embedded-dataclass one), `echo_embedded_arrow`/`echo_deep_nested`/
      `echo_container_wide_types`.
- [x] **M3 — Streaming.** 94/105 tests pass in the gated conformance subset
      (`test_csharp_conformance.py`) — every category except `large_payload.*` (2, a known
      2GiB+ transport-level gap, documented below) and `http_response_cap.*` (4, needs HTTP —
      M6+) is fully green: producer streams, exchange streams (including the `cast_*`
      input-batch-coercion tests — `ValueCodec.CoerceBatch`, strict on field set, tolerant of
      column order and int32/int64/float32→float64 widening, mirrors Python's
      `_coerce_input_batch`), cancellation, error recovery, stream headers (both
      `ConformanceHeader` and the multi-type `RichHeader`), and dynamic per-call output schemas.
      `RpcServer.ServeStreamAsync`'s lockstep dispatch loop handles producer/exchange/cancel/
      headers uniformly. Also landed along the way: list-of-struct support in `ValueCodec`
      (`ArrowArrayConcatenator`-based, since Arrow's own `ListArray.Builder` factory doesn't
      support struct elements). Remaining: client-side stream consumption
      (`RpcConnection`/`RpcClientProxy` — not needed for conformance, since `vgi-rpc-test` drives
      our server with its own Python client, but needed before any C#-to-C# streaming test can
      be written).
- **Spec-drift catch-up (2026-08-24).** CI's conformance job had been silently broken (missing
      `pytest` in the workflow, fixed alongside this) since M4, so it never caught that the
      published `vgi-rpc[conformance]` PyPI package (0.43.0) had grown new conformance surface
      past what this repo's `IConformanceService` implemented: `echo_optional_point`,
      `echo_annotated_optional_int`, `echo_outer_optional_non_null` (three `optional.*`
      sub-tests), and `produce_tick_metadata` (`producer_stream.tick_metadata` — reports the
      `vgi.conformance.tick` custom_metadata observed on each producer tick; needed a `StreamState`
      implemented directly rather than via `ProducerState`, since only `ProcessAsync` sees the
      input batch's metadata). All four are now implemented and green. Along the way, found and
      confirmed (via an isolated `--filter` run) a real transport-desync bug that these new tests
      exposed but did not cause: when a client believes a call is stream-shaped but the server
      doesn't recognize the method, the server correctly writes one error response and returns —
      but a real stream-shaped Python client (headerless streams defer error detection to the
      first `tick()`/`exchange()`/`close()`, and `close()` unconditionally writes a final
      IPC stream on the input channel first) can still write bytes the server never reads,
      corrupting the framing for every call after it on that connection. This is the same failure
      class as the already-known `large_payload.echo_binary_over_int32_max` gap below (both wedge
      the shared connection for the rest of that worker process's run) — implementing the missing
      methods sidesteps it here; the underlying "unknown method the client believes is a stream"
      case remains unhardened. Still-unimplemented and NOT in `IMPLEMENTED_FILTER` (so not
      gating CI): `dataclass.nested_container_types` (`pack_nested_containers`) and
      `large_payload.echo_binary_4mib`/`echo_large_binary`.
- [~] **M4 — Non-HTTP transports + CLI (in progress).** `SocketTransport` (Unix domain socket +
      TCP, both server accept-loop and client dial) plus the conformance worker's `--unix`/
      `--tcp [host:]port` CLI flags (printing the `UNIX:<path>`/`PORT:<port>` discovery lines
      the porting guide's contract expects) and SIGTERM/SIGINT graceful shutdown. Unary calls
      are confirmed working over both transports against the real `vgi-rpc-test` tool (verified
      manually — not yet in the automated gate, which still targets stdio only) and covered by
      `SocketTransportTests`. **Known gap**: a *streaming* call over `--unix`/`--tcp`, driven by
      the real Python client specifically, hangs — a from-scratch minimal C# reproduction (server
      + client both in-process, same `RpcServer`/`WireWriter`/`WireReader` code, same message
      sequence) completes correctly over the same transport, which rules out the server-side
      dispatch/wire-framing logic and points at something specific to the Python client's
      socket-file-object handling for the tick/exchange loop that pipe/stdio doesn't exercise.
      Not yet root-caused; flagged for follow-up rather than blocking further progress, since it
      doesn't affect the stdio-based conformance gate this repo's CI runs.
- [x] **M5 — Access log.** `IAccessLogSink`/`AccessLogRecord`/`JsonlAccessLogSink` wired into
      both `RpcServer.ServeOneAsync` and `ServeStreamAsync`; the conformance worker's
      `--access-log PATH`/`--access-log-debug` flags mirror the porting guide's mandatory CLI
      contract. Validated against the real `vgi_rpc.access_log_conformance.validate_access_logs`
      schema validator (not just self-consistently) via `test_csharp_conformance.py`'s
      `test_access_log_conforms[info|debug]`, both postures: at INFO, unary records carry
      `truncated: "payload_omitted"` + `original_request_bytes` (a pure function of the request
      batch's serialized length, computed without base64-encoding it); at DEBUG
      (`--access-log-debug`), unary records instead carry the full `request_data` — a
      self-contained Arrow IPC stream re-framed from the already-parsed request batch (mirrors
      Python's `_request_wire_bytes` fallback path) — round-trip-verified via
      `--require-request-data`, which decodes it with `pyarrow.ipc.open_stream`. Stream calls
      carry a per-call `stream_id` (`Guid.NewGuid("N")`, matching Python's `uuid.uuid4().hex`)
      on every exit path, including the pre-dispatch error path. Error records carry
      `error_message` (`exception.Message`, matching Python's `str(exc)`), satisfying the
      schema's `status=error requires error_message` rule.
- [~] **M6 — Plain HTTP (unary landed; streaming + client + tokens remain).**
      `QueryFarm.VgiRpc.Http` (`RpcHttpEndpoints.MapVgiRpc`) maps an `RpcServer` onto ASP.NET
      Core minimal-API routes matching the canonical Python repo's Falcon resources exactly:
      `POST {prefix}/{method}` for unary calls (`__describe__` included, since it dispatches
      through the same registered-method path once implemented), `GET`/`HEAD {prefix}/health`.
      `POST {prefix}/{method}/init`/`/exchange` are registered (so the routing contract is
      structurally complete) but answer 501 — streaming over HTTP is real future work, not a
      stub to forget about. Dispatch here is a genuinely separate code path from
      `RpcServer.ServeOneAsync` (a few `internal` accessors — `Methods`, `Implementation`,
      `ServerId`, `AccessLog` — added to `RpcServer` via `InternalsVisibleTo`), matching why
      Python's own `_app_unary.py` doesn't call into `_server.py`'s `serve_one` either: an HTTP
      request/response body has no persistent connection to drive a serve loop over. The
      conformance worker's `--http [--host HOST] [--port PORT]` now actually binds Kestrel
      (`WebApplication`, `builder.Logging.ClearProviders()` so Kestrel's own logging doesn't
      interleave with the mandatory `PORT:<port>` discovery line) instead of the earlier stub.
      **Real, non-obvious finding**: the reference Python HTTP client compresses every request
      body with zstd *by default* (`compression_level: 1`, not opt-in) — so request
      decompression (zstd via `ZstdSharp.Port`, gzip via `System.IO.Compression`, based on
      `Content-Encoding`) turned out to be a hard prerequisite for M6's *unary* slice, not an M7
      refinement as originally scoped; discovered by capturing the raw request bytes against a
      throwaway Python `http.server` and finding a zstd magic number where an Arrow IPC schema
      message was expected. Response compression (server → client) is not yet implemented — the
      client tolerates a plain, uncompressed body when `Content-Encoding` is absent. Verified
      against the real Python reference client and `vgi-rpc-test` (driven via `--url`, not
      `--cmd` — HTTP tests an already-running server, unlike pipe/unix/tcp's spawn-and-drive
      model; see `test_csharp_conformance.py`'s `http_worker` fixture and
      `test_http_unary_subset_conformant`): 60/60 unary tests green over real HTTP, and a full
      unfiltered run confirms the *only* failures are the expected streaming categories (already
      known to be unimplemented) plus the two pre-existing gaps (`dataclass.nested_container_types`,
      `large_payload.*`) — no new failures, no hangs, no cascades (each HTTP request is
      independent, unlike the shared-connection desync class of bug documented under M3).
      Remaining for M6: `/init`/`/exchange` streaming dispatch, and an HTTP-based C# client
      (`HttpClient`). The state-token AEAD primitive itself is done — `QueryFarm.VgiRpc.Http.Crypto`
      (`Seal`/`Open`) is AES-256-GCM (12-byte nonce), substituted for Python's `vgi_rpc.crypto`
      module's XChaCha20-Poly1305 (24-byte nonce) per the plan's reasoning (`ChaCha20Poly1305`
      throws `PlatformNotSupportedException` on pre-2022 Windows Server; .NET has no XChaCha20 at
      all) — same envelope shape (`version || nonce || ciphertext || tag`), same API shape
      (`seal_bytes`/`open_bytes` → `Seal`/`Open`, `normalize_key` → `NormalizeKey`,
      `SealError` → `SealException`). Safe because state tokens are transport-internal, not part
      of the cross-language wire contract (every port already picked its own envelope). Covered
      by `test/QueryFarm.VgiRpc.Http.Tests/CryptoTests.cs` (round-trip, wrong-key, wrong-AAD,
      tampered-ciphertext, truncated/wrong-version, key normalization — 10/10 passing). Still
      needed before this is usable: the actual token *framing* (Python's two-token split — a
      call token minted once by `/init` carrying the frozen schemas + `call_id`, and a cursor
      token re-minted every turn carrying just the advancing `StreamState` — plus the
      `_CallStateCache` and zstd-before-seal payload compression) is real, separate work for
      the `/init`/`/exchange` dispatch itself, not yet started.
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
