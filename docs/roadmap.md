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
