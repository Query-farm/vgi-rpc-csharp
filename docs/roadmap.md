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
- [~] **M6 — Plain HTTP (unary + streaming dispatch landed; C# client remains).**
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
      message was expected. **Response compression is now implemented too** (this was M7 scope
      pulled forward): `ContentEncodingNegotiation` mirrors Python's
      `_CompressionMiddleware._pick_response_encoding`/`parse_encoding_list` — `X-VGI-Accept-Encoding`
      takes precedence over the generic `Accept-Encoding` (some HTTP client libraries inject their
      own `Accept-Encoding: deflate, gzip, br, zstd` listing gzip before zstd, which would
      silently override a VGI-aware caller's stated zstd-first preference), an explicit
      `identity` first in the list wins outright, and the codec actually picked is stamped on
      `Content-Encoding` or `X-VGI-Content-Encoding` depending on which header the client's
      choice came from. Empty bodies are never compressed. **Response compression only ever
      picks gzip, never zstd** — an initial zstd-preferring version passed everywhere locally
      (macOS, Python 3.14) but broke CI outright (every HTTP unary test: `OSError: Invalid IPC
      stream: negative continuation token`), root-caused by reproducing the exact CI environment
      in a Linux x86_64 container (Docker `--platform linux/amd64`, `python:3.13-slim`): the
      reference client advertises zstd support whenever the third-party `zstandard` package is
      importable, but httpx2 2.12's own response auto-decompression stopped using that package
      and now needs Python 3.14's stdlib `compression.zstd` or a separate `backports.zstd`
      package — neither installed by `vgi-rpc[http]`. Request compression is unaffected (vgi_rpc
      calls `zstandard` directly, never through httpx2's decoder), so this is a response-only,
      real bug in the *published Python client's* dependency story on Python ≤3.13, not specific
      to this port — confirmed the fix (gzip-only) 6/6 green in the same reproduced container
      environment before pushing. See `RpcHttpEndpoints.s_producibleEncodings`'s comment for the
      full trail; revisit once the ecosystem's zstd support stabilizes. Confirmed manually
      against the real Python client with response-header capture (`Content-Encoding: gzip`) and
      covered by 12 new `ContentEncodingNegotiationTests`.
      **`/init`/`/exchange` streaming dispatch is now implemented too** — `HandleStreamInitAsync`/
      `HandleStreamExchangeAsync` in `RpcHttpEndpoints.cs`. Uses the AES-GCM primitive
      (`QueryFarm.VgiRpc.Http.Crypto.Seal`/`Open` — AES-256-GCM, 12-byte nonce, substituted for
      Python's XChaCha20-Poly1305 per the plan's reasoning: `ChaCha20Poly1305` throws
      `PlatformNotSupportedException` on pre-2022 Windows Server, and .NET has no XChaCha20 at
      all) but with a deliberately simpler *framing* than the canonical Python server's
      split call/cursor tokens (which carry the serialized `StreamState` bytes so any request can
      land on any stateless worker process): `StreamCallRegistry` keeps the live
      `IRpcStream`/`StreamState` object server-side in a `ConcurrentDictionary`, keyed by a
      random 16-byte call id that the AEAD token just seals and the client echoes back — avoiding
      the need for generic reflection-based `StreamState` (de)serialization, at the cost of a
      stream not surviving a process restart or resuming on a different node (fine for a
      single-process deployment, which is all this port has — no multi-worker HTTP hosting yet).
      Also deliberately simpler on the *dispatch* side: exactly one lockstep turn per
      `POST /exchange` (matching the pipe transport's model), not Python's producer loop that
      accumulates turns until `max_response_bytes` or finish into one HTTP response — simpler,
      and (unlike accumulate-until-cap) makes mid-stream cancel trivial. Response *shape* still
      matches the real client's expectations exactly, verified empirically against
      `vgi_rpc.http._client`: an exchange turn's refreshed continuation token rides on the same
      data batch's own metadata (`HttpStreamSession.exchange()` reads one terminal batch and
      pulls `vgi_rpc.stream_state#b64` off it directly); a producer turn's token rides on a
      *separate* zero-row sentinel batch (`__iter__`/`next_with_token` treat that shape as a
      distinct "there's more" signal). `/init`'s response is the same for both kinds — an
      optional header stream plus a zero-row sentinel batch carrying the sealed token on both
      `vgi_rpc.stream_state#b64` and `vgi_rpc.call_state#b64` — since the real client's init-
      response reader is generic (it just sees zero data batches this turn, which is valid).
      Verified against the real Python reference client and `vgi-rpc-test` (driven via `--url`,
      not `--cmd` — HTTP tests an already-running server, unlike pipe/unix/tcp's spawn-and-drive
      model; see `test_csharp_conformance.py`'s `http_worker` fixture and
      `test_http_subset_conformant`, now reusing the *same* `IMPLEMENTED_FILTER` gated over
      stdio): 99/105 tests green over real HTTP — every streaming category 100% (including all
      7 `cancel.*` tests, confirming the one-turn-per-request simplification's cancel story holds
      up), the only failure the already-tracked `echo_large_binary` gap, and the 5 skips all
      legitimate (M7 response-cap tests, M13 external-storage tests, and one `large_payload` test
      explicitly restricted to pipe/unix/tcp transports upstream). Confirmed in the same
      Linux x86_64 + Python 3.13 container that caught the earlier zstd regression, before
      pushing. `QueryFarm.VgiRpc.Http.Crypto` itself is covered by 10
      `test/QueryFarm.VgiRpc.Http.Tests/CryptoTests.cs` tests (round-trip, wrong-key, wrong-AAD,
      tampered-ciphertext, truncated/wrong-version, key normalization). Remaining for M6: an
      HTTP-based C# client (`HttpClient`) — not needed for conformance (`vgi-rpc-test` drives our
      server with the Python client), same position as the pipe-transport client noted under M3.
- [~] **M7 — Response caps, capability headers, content-encoding negotiation.**
      Content-encoding negotiation (both directions) landed early, folded into M6 above since
      request decompression turned out to be a hard M6 prerequisite rather than optional M7
      scope. **`max_response_bytes` capability discovery + enforcement now landed too**:
      `OPTIONS {prefix}/health` (`RpcHttpEndpoints.HandleCapabilitiesAsync`) advertises
      `VGI-Max-Response-Bytes`, `VGI-Externalization-Enabled: false`, `VGI-Upload-URL-Support: false`,
      `VGI-Supported-Encodings: gzip` — matching `vgi_rpc.http._client.http_capabilities`'s
      discovery contract exactly (this — not a generic "CapabilityHeaderWriter" — turned out to
      be the real scope; the plan doc's framing suggested something larger than what the actual
      Python implementation has). Unary and exchange responses that exceed the configured cap are
      discarded and replaced with an in-band `RuntimeError: HTTP body exceeds max_response_bytes
      (N > cap) for method '<name>'` batch (exact message format matches the porting guide's
      documented wire contract), 200+`X-VGI-RPC-Error`. Producer turns are deliberately **not**
      capped yet — Python's own wire cap is *soft* there (a continuation token carries the
      overshoot to the next turn, which this port's one-turn-per-request model doesn't need but
      also doesn't implement the capping variant of); matches
      `_skip_if_no_wire_cap`'s own reasoning for why producer isn't tested this way either.
      Added the two conformance methods this needed (`oversized_unary`, `exchange_oversized` —
      neither existed yet) and wired the conformance worker to advertise a 64 KiB cap for `--http`
      (not a mandatory CLI flag; a fixed value only this worker needs, so the response-cap tests
      can run instead of skip). Verified: `http_response_cap.unary_strict_fail` and
      `.exchange_strict_fail` now genuinely PASS (not skip) — added to `IMPLEMENTED_FILTER`,
      confirmed skipped (not failed) over stdio via the `transports=("http",)` restriction and
      passing over HTTP, 6/6 in both the local run and the Linux x86_64 container. The other two
      `http_response_cap.*` tests (`producer_external_strict_fail`, `externalized_strict_fail`)
      correctly still skip — they need M13 (external storage), not implemented.
      Remaining for M7: whatever capability-header surface exists beyond what's now covered (the
      `VGI-Max-Request-Bytes`/`VGI-Max-Upload-Bytes` headers exist in Python but aren't exercised
      by any conformance test yet, so not implemented — re-check
      `vgi_rpc/http/server/_middleware.py` before assuming more scope than that), and
      `max_request_bytes` enforcement (413 for oversized inline request bodies) if a conformance
      test ever needs it.
- [x] **M8 — Unauthorized-response spec + bearer auth.** `QueryFarm.VgiRpc.Http.Unauthorized.cs`
      implements the full cross-language contract in
      `~/Development/vgi-rpc/docs/unauthorized-spec.md` — the generic reason-code/JSON-envelope
      machinery every later auth feature (M9 mTLS/JWT, M11 proxy proof) reuses, built once as the
      plan called for. `AuthReason` (the closed 6-code set), `AuthFailure` (thrown by an
      authenticator to reject with a specific reason), `UnauthorizedResponseWriter` (headers +
      §4.2 content negotiation — `Accept: text/html` → styled page, everything else including
      absent/`*/*` → the §4.3 JSON envelope — + `Cache-Control: no-store` + the
      `VGI-Auth-Proxy-Required`/`proxy_hint` pair per §5), and `BearerAuth` (the first concrete
      authenticator: `Authorization: Bearer <token>`, `MissingCredential`/`InvalidCredential` per
      §3). `RpcHttpEndpoints.MapVgiRpc` gained an `authenticate`/`proxyHint` seam invoked before
      every unary/init/exchange dispatch (never `/health` — mandatory and auth-exempt per the
      porting guide); any exception an authenticate delegate throws becomes a 401 (`AuthFailure`
      classified by its `Reason`, anything else falls to `Unauthorized` with an empty detail —
      never leaking the exception's own message, per §2's anti-oracle rule).
      Verified against the real spec, not self-consistently: `~/Development/vgi-rpc/docs/unauthorized-spec.md`
      §7's own `TestUnauthorized` table has its own pytest-fixture wiring this repo doesn't hook
      into, so `test_csharp_conformance.py`'s `TestUnauthorized` class checks the same 12
      properties directly against real HTTP responses (`httpx2`) from a worker started with the
      new `--conformance-auth-reason` flag (every RPC call 401s, reason driven by the
      conformance-only `X-Conformance-Auth-Reason` request header — mirrors the reference repo's
      `tests/serve_conformance_http_auth.py`) and `--conformance-proxy-hint TEXT` (for the proxy-
      note tests) — 20/20 in both the local run and the Linux x86_64 + Python 3.13 container.
      Confirmed the two deliberately-non-requestable reasons hold: `proxy_required` never comes
      from the request (only from `--conformance-proxy-hint` being configured at all), and an
      unrecognised requested reason correctly falls through to `unauthorized` rather than being
      silently accepted. 17 new `UnauthorizedTests`/`BearerAuth` xunit tests cover the response
      writer and bearer extraction directly. Remaining for auth generally: mTLS/JWT (M9), sticky
      sessions' principal binding (M10), proxy proof (M11), token introspection (M12) — this
      milestone was scoped to the shared machinery + the one concrete authenticator, not the
      full auth surface.
- [x] **M9 — mTLS + JWT + CORS (all landed).** `QueryFarm.VgiRpc.Http.OAuth.JwtAuth.Create` builds
      an `AuthenticateDelegate` that validates Bearer JWTs against a JWKS endpoint discovered via
      OIDC discovery (`{issuer}/.well-known/openid-configuration`), with automatic key-set
      refresh on an unrecognised `kid` — `Microsoft.IdentityModel.Protocols.ConfigurationManager<T>`
      handles both natively via `TokenValidationParameters.ConfigurationManager`, which is most of
      why this is far shorter than Python's hand-rolled thread-safe cache-with-refresh in
      `_oauth_jwt.py`: the framework already provides it. Built directly on M8's
      `AuthFailure`/`AuthReason` — exactly the "every later auth feature reuses it" the plan
      called for. A validation failure's `AuthReason` is classified narrowly
      (`SecurityTokenExpiredException` → `ExpiredCredential`, everything else → `InvalidCredential`)
      and its detail is one of a small set of coarse, safe phrases ("JWT signature did not
      verify", "JWT is expired", etc.) — never the framework's raw exception text, which can name
      internal state (`docs/unauthorized-spec.md` §2's anti-oracle rule, same as M8).
      Verified end-to-end against **real** cryptography and a **real** HTTP JWKS/OIDC-discovery
      round trip, not a mocked key resolver: `JwtAuthTests` spins up an in-process Kestrel host
      serving both endpoints, mints actual RS256 JWTs against a generated 2048-bit RSA key, and
      exercises accept / missing-header / wrong-audience / expired / tampered-signature — 6/6,
      plus a direct assertion that no rejection detail leaks the exception's raw text. Two real
      framework traps found and worked around, both documented at the call sites: (1)
      `HttpDocumentRetriever` refuses plain-HTTP discovery URLs by default (correct behavior for
      production; needed a `configurationManager` testability seam on `JwtAuth.Create` so a
      plain-HTTP test fixture can inject a relaxed one without weakening the real default); (2)
      `JsonWebKeySet.ToString()` is *not* a JSON serializer (just the default `object.ToString()`
      — its real writer is `internal`), so the test JWKS endpoint hand-builds the standard RFC
      7517 shape instead. Only the OIDC-discovery flow is supported — a caller-supplied raw
      `jwks_uri` bypassing discovery (Python's `jwt_authenticate(jwks_uri=...)`) isn't wired up.
      CORS landed as `QueryFarm.VgiRpc.Http.Cors`, resolving exactly the architecture wrinkle
      flagged when this milestone started: `RpcHttpEndpoints.MapVgiRpc` only receives an
      `IEndpointRouteBuilder`, but ASP.NET Core's CORS needs service registration
      (`IServiceCollection.AddCors`) and middleware (`IApplicationBuilder.UseCors`), neither of
      which that signature can reach. Resolved with three separate pieces rather than widening
      `MapVgiRpc` itself: `Cors.AddVgiRpcCors(services, policyName, origins, ...)` registers the
      policy on `builder.Services` before `Build()` (methods GET/HEAD/POST/OPTIONS, any header,
      an exposed-headers list computed by `Cors.ExposedHeaders` — the same conditional-append
      pattern as Python's `_factory.py`: a header is exposed only if the corresponding feature,
      `maxResponseBytes`/`proxyHint`, is actually configured — and a 2-hour preflight
      `Access-Control-Max-Age`, matching Python's default; every vgi-rpc call is preflighted since
      the Arrow content type isn't CORS-safelisted, so this matters more here than for a typical
      REST API); `Cors.UseVgiRpcCorsExtras(app)` is middleware run alongside the framework's own
      `app.UseCors()` that sets `Cross-Origin-Resource-Policy: cross-origin` on every response
      (ASP.NET Core's CORS middleware doesn't set this itself, and a page opted into cross-origin
      isolation needs it even though `Access-Control-Max-Age` doesn't need a Python-style second
      middleware — ASP.NET Core's CORS middleware already emits that one from
      `CorsPolicy.PreflightMaxAge`); and `MapVgiRpc` gained a `corsPolicyName: string?` parameter
      that applies `.RequireCors(name)` to all five of its routes (health, capabilities, unary,
      init, exchange) when non-null, leaving CORS fully opt-in. As flagged, this has no
      `vgi-rpc-test` coverage (CORS is browser-enforced; a plain HTTP client never sends
      `Origin`/`Access-Control-Request-*`), so it was verified the same way M8/JWT were: 7 new
      `CorsTests` xunit tests (exposed-header-list conditionals, real `IOptions<CorsOptions>`
      policy-shape assertions off a real `ServiceCollection`, and the `Cross-Origin-Resource-Policy`
      middleware behavior via a minimal `IApplicationBuilder` stub), plus 5 new `TestCors` httpx2
      tests in `test_csharp_conformance.py` against a real worker started with a new
      `--conformance-cors-origin` flag — real preflight `OPTIONS` requests and real actual
      requests, checking `Access-Control-Allow-Origin`/`-Methods`/`-Max-Age` on preflight,
      `Access-Control-Expose-Headers`/`Cross-Origin-Resource-Policy` on the actual response, that a
      disallowed origin gets none of the above, and that a worker started without the flag emits no
      CORS headers at all (opt-in verified both ways). All 25 Python conformance tests and 70
      xunit tests (18+46+6 core/Http/OAuth) pass; manually curl-verified against a running worker
      before writing the automated tests, matching the established habit.
      mTLS landed as `QueryFarm.VgiRpc.Http.MtlsAuth`, a full port of `vgi_rpc/http/_mtls.py`'s
      two header conventions: PEM-in-header (`FromHeader`/`FromFingerprint`/`FromSubject`, parsing
      URL-encoded PEM from `X-SSL-Client-Cert`/`X-Amzn-Mtls-Clientcert`-style headers) and XFCC
      (`Xfcc`, parsing Envoy's `x-forwarded-client-cert` structured header — comma-separated
      elements respecting quoted values, semicolon-separated key=value pairs, URL-decoded
      `Cert`/`URI`/`By` fields). One real simplification over Python: Python gates the PEM path
      behind an optional `cryptography` dependency (`pip install vgi-rpc[mtls]`) because it has no
      certificate parser in its standard library; .NET's `X509Certificate2.CreateFromPem` ships in
      the base class library, so this port needed no extra package at all. Absent header → always
      `AuthReason.ProxyRequired` (never `MissingCredential` — the header is the proxy's to inject,
      so its absence points at the deployment, not the caller, matching Python's documented
      rationale exactly), matching Python's anti-oracle posture but going one step further at the
      PEM-parse-failure site: Python's own example interpolates the raw parse exception into the
      rejection detail; this port deliberately doesn't (`docs/unauthorized-spec.md` §2, same
      posture already established for JWT). One real architecture divergence, documented at the
      top of `Mtls.cs`: Python's authenticate callbacks return an `AuthContext` that flows into RPC
      dispatch (so a method body can read `ctx.auth.principal`); this port's
      `AuthenticateDelegate` is (per M8/M9's established shape) a pure accept/reject gate with no
      context-propagation mechanism yet, so the extracted `MtlsIdentity` is stashed on
      `HttpContext.Items` instead — reachable by application code sharing the same
      `HttpContext`, but not yet wired into `RpcServer` dispatch itself (a gap to close if/when
      M10's sticky-session principal-binding needs real propagation). Verified with 33 new
      `MtlsTests` xunit tests (a direct, near-1:1 port of `tests/test_mtls.py`'s coverage: valid
      cert acceptance, missing/malformed header, custom header name, validate-callback rejection,
      `checkExpiry` in both directions, fingerprint lookup incl. unsupported-algorithm-at-
      construction-time, subject-CN extraction with an allow-list, and the full `_parse_xfcc`
      table — quoted commas/semicolons, multi-value `DNS`, URL-decoded `URI`/`By`/`Cert`, empty
      header, `select_element` first-vs-last), plus 4 new `TestMtls` httpx2 tests in
      `test_csharp_conformance.py` against a real worker started with a new
      `--conformance-mtls-subject` flag, using real certificates generated with the `cryptography`
      library (the same one Python's own test suite uses) — accepted valid cert, `proxy_required`
      on a missing header, `invalid_credential` on a malformed one, and (since `FromSubject`
      defaults `checkExpiry=false`, matching Python) an expired-but-otherwise-well-formed
      certificate still accepted unless the operator opts in. Manually curl-verified against a
      running worker (both `echo_string` accepted-with-cert and rejected-without) before writing
      the automated tests. All 29 Python conformance tests and 103 xunit tests (18+79+6
      core/Http/OAuth) pass, both locally and in a linux/amd64 Docker container matching CI's
      ubuntu-latest path. M9 is now fully complete.
- [x] **M10 — Sticky sessions.** Full port of `vgi_rpc/http/server/_sticky.py` (873 lines) —
      `QueryFarm.VgiRpc.Http.StickySessions`/`StickySessionRegistry`/`StickySessionEntry`, plus new
      HTTP-only members on `ICallContext` (`Session`, `SessionId`, `OpenSession`, `CloseSession`).
      Flagged in the original plan as "the single largest implementation surface" — it was, by a
      wide margin, of everything landed so far.
      **Architecture note (the one genuine simplification over Python):** Python threads sticky
      state through `contextvars` because its WSGI middleware has no explicit per-call context
      object to carry it on — `CallContext` reads ambient state a Falcon middleware installed
      before dispatch. This port's `ICallContext` is already an explicit object threaded through
      every `InvokeAsync` call (unary and per stream turn, since M2/M3), so `RpcHttpEndpoints`
      just resolves the session, builds a concrete `StickyCallState`-backed call context carrying
      it directly, and reads back what the method did (minted token / closed flag) after the call
      returns — no contextvar equivalent needed at all. Every other piece is a faithful, no-shortcuts
      port: the AEAD session-token envelope (same plaintext frame layout as Python's — `created_at |
      server_id_len | server_id | session_id(12) | expires_at` — sealed via the existing AES-GCM
      `Crypto.Seal`/`Open` from M6, with the exact same AAD prefix `vgi_rpc.state.v4\0` +
      principal-binding tail spec §3.1 requires: `\x01 domain \0 principal` for authenticated
      requests, the literal `\0anonymous` tail otherwise — cross-principal replay fails at the AEAD
      layer, not via a post-decrypt comparison); the per-session `SemaphoreSlim`-based lock
      (spec §5's "same-session calls serialize, different-session calls run in parallel" — `SemaphoreSlim`
      rather than a reentrant `lock`/`Monitor` because dispatch is `await`-based and holding a `lock`
      across an `await` is unsafe); the reaper (`System.Threading.Timer` ticking every 1s, evicting
      + disposing past-TTL entries); drain (`StickySessionRegistry.Drain()`/`Shutdown()` — exposed
      directly on the registry the caller already constructs and holds, which is simpler than
      Python's `drain_handle(app)` indirection — Python needs to *find* the middleware instance
      post-hoc by walking Falcon's middleware list because Falcon's app construction doesn't hand
      back named component references; this port's caller already has the registry, no lookup
      needed); echo headers (`VGI-Echo-<name>`, emitted once on the session-opening response only);
      the `DELETE {prefix}/__session__` idempotent-teardown endpoint (200 on every failure mode —
      missing header, malformed/tampered/wrong-principal/wrong-worker token, registry miss — so a
      stolen token can't be used to probe session existence); and capability advertisement
      (`VGI-Sticky-Enabled`/`VGI-Sticky-Default-TTL`/`VGI-Sticky-Echo-Headers` on `OPTIONS /health`).
      **Two real bugs found and fixed along the way, both pre-existing (not introduced by this
      milestone) and both now covered by regression tests:**
      1. `RpcServer.ServeStreamAsync` (pipe/unix/tcp) and `RpcHttpEndpoints.HandleStreamExchangeAsync`
         (HTTP) both gated per-turn `ICallContext` construction (`turnContext`) on
         `info.HasContextParameter` — the *outer* RPC method's own signature flag (correctly used to
         decide whether to append `ctx` to that method's own reflection-invoke args). But
         `StreamState.ProduceAsync`/`ExchangeAsync`/`ProcessAsync` always accept an `ICallContext?`
         as part of their own fixed abstract contract, independent of whether the constructor method
         itself declared a `ctx` parameter — so a producer/exchange method with no `ctx` parameter of
         its own (like `stream_session_counter(long count)`) silently got a `null` turn context,
         and any `StreamState` reading `ctx.Session` inside `ProduceAsync`/`ExchangeAsync` saw nothing
         bound even with a live, correctly-resolved session. Latent until now because no existing
         `StreamState` implementation ever read `ctx` at all (the logging ones call
         `output.ClientLog(...)` directly instead) — `SessionCounterProducerState`/
         `SessionCounterExchangeState` are the first to actually need it. Fixed by always
         constructing the per-turn context in both places.
      2. `error_type` on the wire silently diverged from Python for any `RpcException`-derived type
         whose C# class name doesn't literally match Python's class name — which is *every* one,
         since C# convention names them `...Exception` (`SessionLostException`) while Python
         names them `...Error` (`SessionLostError`), and `LogMessage.FromException` (both this
         milestone's addition and — it turns out — every prior exception-serialization path) used
         `exception.GetType().Name` unconditionally, never consulting the already-existing
         `RpcException.ErrorType` property at all. Confirmed against the Rust port, which
         hardcodes the literal wire string `"SessionLostError"` for exactly this reason despite its
         own internal type being named differently — this is a real, intentional part of the
         cross-language error vocabulary, not a Python-ism this port can ignore. Fixed by having
         `LogMessage.FromException` prefer `RpcException.ErrorType` when set (plain `Exception`
         subclasses — e.g. the conformance worker's `ValueError`/`RuntimeError`/`TypeError`,
         already correctly named to match Python's builtins — are unaffected), and by giving
         `SessionLostException`/`ServerDrainingException` (plus the three framework-level
         `RuntimeError`-on-the-wire cases sticky sessions itself introduces — no
         `VGI-Session-Accept: true`, "session already active", "not available on this transport",
         all raised by `ICallContext`/`StickyCallState` as `RpcException("RuntimeError", ...)`
         rather than a CLR `InvalidOperationException`, since Python raises its own built-in
         `RuntimeError` for exactly these three cases) their correct wire type strings explicitly.
      Conformance methods added: `open_counter`/`increment_counter`/`close_counter` (unary
      lifecycle) and `stream_session_counter`/`exchange_session_counter` (producer/exchange streams
      sharing the session across the multi-request shape of streaming RPCs) — a faithful port of
      Python's own `_StickyCounter`/`SessionCounterProducerState`/`SessionCounterExchangeState`.
      Wired into the conformance worker via `--conformance-sticky` (enables sticky, default 300s
      TTL, the fixed `x-vgi-conformance-echo: conformance-fixed-marker` echo header), `--sticky-ttl
      <seconds>` (overrides TTL, implies sticky), `--sticky-auth` (installs an `X-Conformance-Principal`
      → `AuthIdentity` authenticate delegate — absent header stays anonymous, never rejected — and
      implies sticky), `--token-key <hex>` (fixed AEAD key, shared by stream-call and session
      tokens), and a test-only `/__test_drain__` admin endpoint (`POST` sets the drain flag,
      `DELETE` clears it — not part of the real wire surface, mirrors the reference repo's own
      `_TestDrainResource`).
      Verified by directly collecting the canonical Python repo's own `TestSticky` conformance
      group (`from vgi_rpc.conformance._pytest_suite import TestSticky`) into
      `test_csharp_conformance.py` — all 19 tests, unmodified, including the three failure-path
      fixtures spec §9.1 requires a sticky-claiming port to supply (`conformance_http_sticky_short_ttl_port`,
      `conformance_http_sticky_peer_ports` — two workers sharing one AEAD key with distinct
      `server_id`s, `conformance_http_sticky_auth_port`) — rather than hand-written httpx2 tests
      like M8/M9's CORS/mTLS groups, since (unlike those) a complete canonical suite already existed
      to import. This is a different, higher-fidelity verification pattern than every other HTTP
      milestone so far and worth preferring whenever `vgi_rpc.conformance._pytest_suite` has a
      matching group. Plus 20 new `StickySessionsTests` xunit tests covering the registry lifecycle
      (open/resume/close/expire/drain/shutdown, wrong-principal and unknown-session misses,
      suppressed-dispose-exception) and token codec (round-trip, malformed/wrong-key/wrong-AAD
      rejection, AAD determinism) directly. All 48 Python conformance tests and 123 xunit tests
      (18+99+6 core/Http/OAuth) pass, both locally and in linux/amd64 Docker containers matching
      CI's ubuntu-latest + Python 3.13 + pip install "vgi-rpc[conformance,http]" pytest cryptography
      path exactly. Manually verified end-to-end against the real Python client
      (`http_connect`/`with_session_token()`) before writing the automated tests, per the
      established habit — this is also how the two bugs above were actually found.
      Not implemented: cookie emission and a pluggable session store (both explicitly out of scope
      per spec §10); client-side sticky-session support in this port's own `RpcClientProxy` (not
      needed for conformance — every sticky test drives this port's HTTP *server* from the real
      Python client, never the reverse — but a future C#-to-C# deployment wanting sticky sessions
      would need it added).
- [x] **M11 — Proxy proof.** Full port of `vgi_rpc.http._proof` —
      `QueryFarm.VgiRpc.Http.ProxyProof`/`ProxyProofConfig`/`NonceCache`. HMAC-SHA256 evidence
      that a request arrived through a trusted proxy: `VGI-Proxy-Proof: v1.<kid>.<ts>.<nonce>.<mac>`,
      verified per spec §6's exact 9-step order (cheap charset/format checks before any MAC is
      computed, so an unparseable header costs a few regex matches rather than a hash), with a
      two-sided timestamp window (checking only the upper bound would let a far-future timestamp
      pass forever — spec explicitly calls this out as a real defect seen elsewhere) and a
      constant-time MAC compare (`CryptographicOperations.FixedTimeEquals`) selected by `kid`
      (public, so branching on it is safe — only the one resulting MAC needs the constant-time
      path). `NonceCache` is a direct port of Python's `OrderedDict`-based bounded replay cache
      (TTL + hard capacity cap, oldest-evicted-on-overflow, `System.Collections.Generic.OrderedDictionary`
      standing in for `collections.OrderedDict` — same "uniform TTL means insertion order is
      expiry order" sweep-from-front trick). `ProxyProofConfig` validates eagerly at construction
      (mirrors Python's `__post_init__`: `require`/`allow` mode with no secrets, no `origin_id`,
      a wrong-length secret, or a non-positive skew all throw immediately — "a shared secret spans
      two independently deployed processes; a lax parse means require mode becomes a 100%
      rejection outage with no diagnostic").
      **Architecture note (the one genuine simplification over Python, and a bigger one than
      M10's):** Python needs a distinct `PreconditionGate`/`require_all` combinator system because
      its `chain_authenticate` is an OR-combinator that swallows `ValueError` to try the next
      credential, so a precondition gate must raise a distinguished exception type
      (`PermissionError`) to avoid being silently skipped by that combinator. This port's
      `AuthenticateDelegate` has no OR-combinator at all — there is exactly one authenticate
      delegate per `MapVgiRpc` call — so plain sequential `async`/`await` composition
      (`ProxyProof.RequireAll(gate, inner)`, three lines: await the gate, then await inner if
      given) already gives the exact "gate first, only call inner on success" behavior spec §8
      requires, with nothing around to swallow the gate's exception. No distinguished exception
      type needed at all — `AuthFailure` (the same type every other authenticator in this port
      already throws) is sufficient.
      Exemptions (spec §2.3 — `OPTIONS` on any path, `/.well-known/*`, `{prefix}/health` reachable
      without a proof in every mode) needed **zero extra code**: this port's authenticate delegates
      are only ever invoked from inside the unary/init/exchange dispatch handlers, never for
      `/health` or `OPTIONS /health`; `/.well-known/*` has no routes in this port at all; and CORS
      preflight `OPTIONS` requests are intercepted by ASP.NET Core's own CORS middleware before
      reaching any mapped endpoint. All three exemptions were already structurally true before
      this milestone existed.
      Capability header (`VGI-Proxy-Proof-Required`, `require` mode only) required touching both
      `HandleCapabilitiesAsync` (`OPTIONS /health`, matching sticky's existing discovery contract)
      *and*, unlike sticky, `HandleHealthAsync` (plain `GET`/`HEAD /health`) — the conformance
      suite's own capability check (`_health_headers`) probes a plain GET, not OPTIONS, so both
      needed the header. `MapVgiRpc` gained a `proxyProofRequired: bool` parameter, operator-set
      like `proxyHint` rather than derived — `authenticate` is an opaque (possibly
      `RequireAll`-composed) callback, so `MapVgiRpc` has no way to introspect whether it enforces
      proxy proof or in which mode (spec §2.2 makes this normative, not a shortcut: "a port that
      tries to infer it from the callback will get it wrong for `require_all(gate, inner)` — which
      is the shape every real deployment uses").
      Attribution (spec §9: verified proof surfaces in claims, never in `domain`/`principal`) uses
      the same `HttpContext.Items` convention as `MtlsIdentity`/`AuthIdentity` from M9/M10
      (`ProxyProofResult.SetOn`/`GetFrom`) — this port's now-established, documented answer to not
      having a full claims-propagation-to-dispatch mechanism yet.
      Wired into the conformance worker via `--proof-mode off|allow|require`, `--proof-origin-id`,
      `--proof-secrets kid:hex,...`, `--proof-skew <seconds>`, `--proof-no-replay-cache` — composed
      with whatever authenticate delegate an existing flag (`--sticky-auth`, `--conformance-mtls-subject`,
      …) already selected via `RequireAll`, rather than replacing it, so the composition stays
      correct if a future conformance fixture needs both at once.
      Verified by directly collecting the canonical Python repo's own `TestProxyProof` +
      `TestProxyProofOffMode` groups (`from vgi_rpc.conformance._pytest_suite import TestProxyProof,
      TestProxyProofOffMode`) — the same higher-fidelity pattern M10 established over hand-written
      httpx2 tests, since a complete canonical suite already existed. `TestProxyProofOffMode`
      reuses M10's own `conformance_http_port` fixture (a sticky-enabled worker with no proxy-proof
      gate configured) — no new fixture needed, and a nice confirmation that "opt-in, off by
      default" composes cleanly across milestones. All 22 `TestProxyProof`/`TestProxyProofOffMode`
      tests passed on the **first real run against the real worker** — no bugs found this time
      (unlike M9's mTLS/M10's sticky sessions, each of which surfaced at least one real bug before
      going green), which is itself a signal the design fidelity to the Python reference held up.
      Plus 42 new `ProxyProofTests` xunit tests covering the token codec (mint/verify round-trip,
      a fixed canonical-string vector, every §6 rejection reason individually, wrong-origin
      audience binding, both timestamp-window bounds), the nonce replay cache (fresh/replay/
      overflow-eviction/TTL-sweep), `ParseSecrets`/`ProxyProofConfig` validation, the gate/`RequireAll`
      composition (including the anti-oracle assertion that a rejection detail never echoes the
      caller-controlled `kid` or internal reason code), and `DeriveSecret`. All 70 Python
      conformance tests and 165 xunit tests (18+141+6 core/Http/OAuth) pass, both locally and in a
      linux/amd64 Docker container matching CI's ubuntu-latest path. Manually curl/httpx-verified
      against a running worker (health exemption, unauthenticated rejection, valid-proof
      acceptance, wrong-origin rejection) before writing the automated tests, per the established
      habit.
      Not implemented: distributed/cross-process replay tracking and secret distribution via a
      fetched trust document (both explicitly out of scope per spec §13); the "AND with a
      configured user authenticator" composition is implemented and documented
      (`ProxyProof.RequireAll`) but not exercised by a dedicated conformance fixture today (the
      imported `TestProxyProof` suite doesn't currently test that combination against any port).
- [x] **M12 — Token introspection.** Full port of `vgi_rpc.http.server._introspect` —
      `QueryFarm.VgiRpc.Http.TokenIntrospection`/`TokenIdentity`/`AuthUnavailableException`/
      `IntrospectionRateLimiter`. `POST {prefix}/__introspect_token__` resolves an opaque bearer
      credential to a principal, for a reverse proxy that terminates the only public listener and
      must know which principal a credential authenticates as before it can authorize anything.
      No standalone spec doc for this feature (unlike M9–M11) — the normative source is
      `docs/porting-guide.md`'s "HTTP token introspection" section, followed exactly, including
      its "why the guards are hard requirements" list: the response is a closed set
      (`principal`/`token_name`/`ttl_seconds`, never claims — asserted as a closed key set by the
      conformance suite itself, `test_response_carries_no_claims`); the route is mandatory and
      always mounted (a worker that doesn't implement it MUST still answer `404 not_enabled` —
      never fall through to the generic route's `415`, which a caller classifying 401/403/404 as
      definitive and everything else as transient would read as "retry later" and loop forever
      against a worker that will never support the feature); the introspector allowlist has no
      permissive default (`TokenIntrospection.NormalizePrincipals` throws on an empty/missing
      allowlist, matching Python's `_normalise_principals`); a JWS-shaped subject
      (`^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$`) is rejected without ever reaching the
      resolver; every rejection is uniform (unknown/expired/malformed all produce byte-identical
      404 responses — asserted directly, `test_rejections_are_indistinguishable`); the credential
      never appears in a response.
      **Definitive vs. transient, carried through exactly**: the endpoint's own "did not resolve"
      is `404` (a caller may negative-cache it), so a resolver whose backing store is down must
      not borrow that shape — it throws `AuthUnavailableException` instead, mapped to `503` +
      `Retry-After` (never `Cache-Control: no-store`, which is for definitive rejections only).
      `AuthUnavailableException`'s doc comment is explicit about *not* needing Python's "must not
      be a `ValueError`" workaround: that exists solely so Python's OR-combinator
      (`chain_authenticate`) doesn't misread an outage as "not my credential, try the next" — this
      port has no such combinator (see M11's `ProxyProof.RequireAll` note), so a distinct
      exception type here is just good API hygiene (an outage can't be mistaken for "did not
      resolve" at the call site), not a workaround for anything.
      Caller authorization runs through the SAME `authenticate` delegate every other route already
      uses (`TryRejectUnauthenticatedAsync`, unchanged) — introspection layers its allowlist check
      on top of normal auth, never instead of it, reading the `AuthIdentity` whatever authenticator
      already resolved. The route is unconditionally mounted by `MapVgiRpc` (mirroring Python's
      `app.add_route` always running, branching only on which resource class); a `null`
      `introspectResolver` makes every request answer the mandatory `404 not_enabled`.
      `IntrospectionRateLimiter` is a direct port of Python's `_RateLimiter` (fixed-window, keyed
      by caller, whole-map reset at the window boundary rather than per-key ageing) — present
      because the endpoint is a credential→identity oracle even when correctly restricted: an
      allowlisted caller whose own credential leaks can still test guesses, and rate limiting
      bounds (does not close) that.
      Wired into the conformance worker via `--introspect`, sharing the exact same
      `X-Conformance-Principal` → `AuthIdentity` authenticate delegate `--sticky-auth` already
      established (the caller-identity resolution really is identical — both need "an authenticated
      principal from a request header, for conformance-fixture purposes only"), plus a fixed
      resolver matching the conformance suite's exact required constants
      (`conformance-opaque-subject-token` → `subject@conformance.example`,
      `conformance-unavailable-token` → throws `AuthUnavailableException`; the JWS trap token
      needs no entry at all, since the shape guard rejects it before the resolver is ever called —
      registering it would be the bug the trap exists to catch).
      Verified by directly collecting the canonical Python repo's own `TestTokenIntrospection` +
      `TestTokenIntrospectionOffMode` groups — the same pattern M10/M11 established. All 17 tests
      passed on the **first real run against the real worker**, no bugs found (matching M11's
      proxy proof, not M9/M10's mTLS/sticky — a second data point that porting-guide-driven
      features with this much explicit "why" documentation port cleanly the first time). Plus 21
      new `TokenIntrospectionTests` xunit tests covering the full decision tree directly (valid
      resolve, no-claims closed-key-set assertion, indistinguishable-rejection byte-for-byte
      comparison, unauthenticated/non-introspector refusal, the JWS-guard-blocks-the-resolver
      assertion via a resolver that records whether it was called, credential-never-echoed across
      both success and failure, the 503/Retry-After outage path, malformed-body handling, rate
      limiting, and `NormalizePrincipals`/`TokenDigest` directly). All 87 Python conformance tests
      and 186 xunit tests (18+162+6 core/Http/OAuth) pass, both locally and in a linux/amd64
      Docker container matching CI's ubuntu-latest path. Manually curl-verified every response
      shape (200/404/403/503, the disabled-worker fallback, the capability header) against a
      running worker before writing the automated tests.
      Not implemented: nothing scoped out — this milestone is a complete, faithful port of the
      guide's contract.
- [x] **M13 — External storage (S3/GCS).** Full port of `vgi_rpc/external.py` +
      `vgi_rpc/external_fetch.py` — `QueryFarm.VgiRpc.Http.ExternalLocation`/`ExternalFetch`/
      `RequestCap`. The ExternalLocation pointer-batch protocol (a zero-row batch carrying
      `vgi_rpc.location`/`vgi_rpc.location.sha256` custom_metadata) lets a large batch be uploaded
      to remote storage and replaced with a pointer the other side transparently re-fetches — the
      unary result path, both directions of a producer/exchange turn's single emitted data batch,
      and every inbound HTTP data route (`/echo`, `/init`, `/exchange`) all wired.
      **Two scope narrowings versus Python, both documented in code**: (1) log batches within a
      producer/exchange turn always stay inline — only the turn's one data batch is ever a
      candidate for externalization, whereas Python's `maybe_externalize_collector` also
      externalizes the log-batch bundle; (2) the fetch side is a simplified single streaming GET
      with manual per-hop-validated redirects (`ExternalFetch`), not Python's parallel
      Range-request/HEAD-probing machinery — every `TestExternalFetchSecurity` conformance case
      passes against it, since the conformance payloads stay in the tens-of-KB range, but a
      genuinely large externalized object would want the parallel-fetch behavior back.
      `max_externalized_response_bytes` is enforced **hard, with no continuation escape valve, on
      every method type** — unary, producer, and exchange alike — via a predict-then-refuse split
      (`ExternalLocation.PredictExternalizeBytes` before `MaybeExternalizeAsync`) so a
      cap-violating upload is refused before the storage round-trip ever happens, matching the
      spec's explicit warning that at least one port shipped the cap advertised-but-unenforced.
      This is deliberately *not* soft the way the unrelated `max_response_bytes` wire cap stays
      soft/unenforced for producer turns (M7) — bytes already uploaded cannot be un-uploaded.
      `max_request_bytes` (413, including a `CappedStream` that catches a chunked body with no
      declared `Content-Length` mid-read, not just a `Content-Length` pre-check) and the synthetic
      `POST {prefix}/__upload_url__/init` control route (client-to-server externalization: the
      server vends a pre-signed upload/download URL pair via `IUploadUrlProvider`, the client PUTs
      directly to storage, then re-POSTs a pointer batch) are both wired through
      `ExternalizationOptions`, a single bundling record `MapVgiRpc` takes so a caller wanting only
      request-side pointer resolution doesn't have to thread five independent parameters.
      **Two real bugs found and fixed during conformance verification, both by the same class of
      mistake** (an Apache.Arrow (.NET) API defaulting silently instead of erroring):
      (1) `TimestampArray.Builder()`'s parameterless constructor defaults to `TimeUnit.Millisecond`
      while `__upload_url__/init`'s response schema declared its `expires_at` field
      `TimeUnit.Microsecond` — the builder never checked its own output against the field it was
      building for, so every vended `expires_at` came back 1000x wrong (an upload URL "expiring"
      at 1970-01-21 instead of an hour from now) until manual curl/Python-client verification
      caught it; fixed by constructing the builder with the schema's exact unit explicit.
      (2) `Schema` (Apache.Arrow) never overrides `object.Equals` — `ExternalLocation.ResolveAsync`
      compared a fetched batch's schema against the original pointer's schema via `.Equals()`,
      which is reference equality by default and so always failed for two independently-built
      schemas of identical shape, surfacing as every request-side pointer resolution raising
      "Schema mismatch" even when the schemas printed identically. Fixed by exposing
      `ValueCodec.SchemasEqual` (previously `private`, used internally by `CoerceBatch`'s own
      fast-path check) as `public` and using it for the structural (name/type-id) comparison this
      needs, rather than each call site hand-rolling its own.
      **One real pre-M13 gap found and fixed along the way**: `HandleStreamInitAsync` never ran a
      producer's first tick at all — every producer stream's `/init` response carried zero data,
      deferring the entire first `ProduceAsync` call to the client's first `/exchange` round trip.
      The canonical Python implementation instead folds a producer's first tick into `/init`
      itself (`_run_http_producer_turn`, invoked from the init handler) — found because
      `TestExternalInputRoutes::test_stream_init_resolves_external_input` is the first conformance
      case that inspects a raw `/init` response body directly rather than driving it through the
      client's own tolerant iteration protocol. Fixed by running one produce tick during `/init`
      for producer streams (mirroring the shape already established in the exchange handler's own
      producer branch: logs, then data, then a token sentinel — or, if the stream finishes on that
      very first tick, no token at all, matching Python's "no continuation possible, nothing to
      hand the client" behavior) — exchange streams are unaffected, since nothing is produced until
      the client sends its first exchange turn. This changes tick-count timing only (one fewer
      HTTP round trip for a producer that emits on tick one), not correctness; the full existing
      suite (113 Python + 191 xunit tests) was re-run afterward specifically to catch any
      regression in `producer_stream.*`/`TestSticky`'s streaming-counter cases, and none appeared.
      Two new conformance-worker-only methods needed for the imported suites to have something to
      call: `echo_large_string` (wire-named to match Python's `pa.large_string()`-typed method, but
      implemented over plain `Utf8Type` — this port has no attribute-based Arrow-type-width
      override yet, so there's no distinct CLR type to hang `large_string` off of the way
      `decimal`/`timestamp` hang off their own attribute; functionally equivalent for every payload
      size the suite exercises) and `ProduceOversizedBatchAsync`/`OversizedProducerState` (a
      producer analog of the existing `OversizedUnaryAsync`/`ExchangeOversizedAsync`, needed by
      `TestExternalizedResponseCap::test_producer_gets_no_continuation_escape`). Reading a
      Python-client-sent `pa.large_string()` column also required a genuine `ValueCodec` gap fix
      unrelated to width-override attributes: `ExtractSingleValue` had no case for
      `LargeStringArray` at all (only `StringArray`), so the very first cross-language call threw
      `NotSupportedException` — fixed by reading it as a plain string, same as `StringArray`.
      `FakeStorageBackend` (conformance-worker-only, mirrors Python's own conformance
      `FakeStorageBackend` adapter) implements both `IExternalStorage` and `IUploadUrlProvider`
      against the canonical `vgi_rpc.conformance.fake_storage` HTTP service's `POST /alloc` + `PUT`
      wire contract, wired in via new worker flags: `--fake-storage <url>`,
      `--externalize-threshold`, `--max-request-bytes`, `--compression none|zstd`,
      `--max-fetch-bytes`, `--max-decompressed-fetch-bytes`, `--reject-localhost-redirects`,
      `--max-response-bytes` (an override — this worker previously hardcoded 65536 unconditionally
      for M7's strict-fail tests), `--max-externalized-response-bytes`. Unlike Python, which splits
      this across two scripts (`serve_conformance_http.py` / `serve_conformance_http_strict.py`),
      this port's one worker binary unifies both — the same shape the Rust port's own
      `--http-with-storage`/`--strict` flags already converged on independently, and the closest
      existing precedent for a single-worker port.
      Verified by directly collecting the canonical Python repo's own `TestExternalLocation` +
      `TestExternalizedResponseCap` (`vgi_rpc.conformance._pytest_suite`) and
      `TestExternalInputRoutes` + `TestExternalFetchFailures` + `TestExternalFetchSecurity` +
      `TestExternalStorageUrlPair` (`vgi_rpc.conformance._external_pytest` — a separate raw-HTTP
      driver module since these tests place pointer batches on inbound routes directly, which the
      ordinary RPC proxy deliberately hides) groups — the same pattern M10/M11/M12 established.
      All 26 tests passed after fixing the three bugs above. Plus 29 new `ExternalLocationTests`
      xunit tests covering `GetTotalBufferSize`/`MakePointerBatch`/`IsExternalLocationBatch`/
      `PredictExternalizeBytes`/`MaybeExternalizeAsync` directly (including a full externalize→
      resolve round trip against a real loopback `HttpListener`, not a mock), `ExternalFetch`'s
      validator/redaction/redirect-loop/cap-enforcement/pre-fetch-rejection behavior, and
      `RequestCap`'s declared-vs-observed 413 paths (including the chunked-body case). All 113
      Python conformance tests and 191 xunit tests (18+167+6 core/Http/OAuth) pass, both locally
      and in a linux/amd64 Docker container matching CI's ubuntu-latest path. Manually curl/Python-
      client-verified end-to-end (small-payload-stays-inline, large-payload-externalizes,
      capability advertisement, `request_upload_urls` PUT/GET round-trip) before writing the
      automated tests, per the established habit — which is exactly what caught the `expires_at`
      timestamp-unit bug before any automated test had a chance to encode the wrong value as
      "expected."
      Not implemented: S3/GCS `IExternalStorage` backends themselves (`QueryFarm.VgiRpc.S3`/`.Gcs`
      remain empty scaffold projects — the pointer-batch protocol and the storage seam are complete
      and backend-agnostic; wiring an actual AWS/GCS SDK behind `IExternalStorage` is separable,
      lower-risk work with no conformance dependency, since the suite only requires *an*
      HTTP-addressable backend, not a specific one).
- [ ] **M14 — SHM transport.**
- [ ] **M15 — OAuth2/PKCE browser flow.**
- [ ] **M16 — Observability (OpenTelemetry, Sentry).**
- [ ] **M17 — Full-suite conformance in CI across every transport, 2GiB payload test, perf pass,
      packaging/release, docs pass.**

Full rationale for each milestone's sequencing lives in the plan this repo was bootstrapped from;
see `CLAUDE.md` for where cross-language wire-alignment decisions are recorded as they're made.
