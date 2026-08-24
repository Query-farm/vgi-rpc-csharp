"""Cross-language conformance gate for vgi-rpc-csharp.

Mirrors the pattern every other vgi-rpc port uses (vgi-rpc-go/test_go_conformance.py,
vgi-rpc-rust/test_rust_conformance.py, vgi-rpc-typescript/test_ts_conformance.py,
vgi-rpc-java/tests/test_java_conformance.py): build the worker, then drive it with the
canonical Python package's `vgi-rpc-test` CLI. See conftest.py for how the `vgi_rpc` package is
located, and docs/roadmap.md for what's implemented so far.

Run directly: `python -m pytest test_csharp_conformance.py -v`
"""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import time
from collections.abc import Iterator
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).parent
WORKER_PROJECT = REPO_ROOT / "conformance" / "QueryFarm.VgiRpc.ConformanceWorker"
WORKER_OUTPUT = REPO_ROOT / "artifacts" / "conformance-worker"

# Test categories/methods implemented so far (unary only — see docs/roadmap.md M2). Grows as
# later milestones land: M3 adds producer_stream/exchange_stream/*_header/cancel/dynamic_schema,
# M2-continued adds dataclass.echo_all_types* + the wide-Arrow-type methods once list-of-struct
# support lands, M4+ adds large_payload/http_response_cap.
IMPLEMENTED_FILTER = ",".join(
    [
        "scalar_echo.*",
        "void.*",
        "complex_types.*",
        "optional.*",
        "dataclass.echo_point",
        "dataclass.echo_bounding_box",
        "dataclass.inspect_point",
        "annotated.*",
        "multi_param.*",
        "errors.*",
        "logging.*",
        "boundary_values.*",
        "protocol_version.*",
        "producer_stream.*",
        "exchange_stream.scale",
        "exchange_stream.echo",
        "exchange_stream.accumulate",
        "exchange_stream.with_logs",
        "exchange_stream.error_first",
        "exchange_stream.error_nth",
        "exchange_stream.empty_session",
        "error_recovery.*",
        "cancel.*",
        "producer_header.*",
        "exchange_header.*",
        "rich_header_producer.*",
        "rich_header_exchange.*",
        "dynamic_schema_producer.*",
        "dataclass.echo_all_types",
        "dataclass.echo_all_types_with_nulls",
        "http_response_cap.unary_strict_fail",
        "http_response_cap.exchange_strict_fail",
        "exchange_stream.cast_int32_to_float64",
        "exchange_stream.cast_int64_to_float64",
        "exchange_stream.cast_float32_to_float64",
        "exchange_stream.cast_exact_schema",
        "exchange_stream.cast_incompatible_column_name",
    ]
)


@pytest.fixture(scope="session")
def worker_binary() -> Path:
    """Builds the conformance worker once per test session."""
    if WORKER_OUTPUT.exists():
        shutil.rmtree(WORKER_OUTPUT)

    subprocess.run(
        [
            "dotnet",
            "publish",
            str(WORKER_PROJECT),
            "-c",
            "Release",
            "-o",
            str(WORKER_OUTPUT),
        ],
        cwd=REPO_ROOT,
        check=True,
    )

    exe = WORKER_OUTPUT / "QueryFarm.VgiRpc.ConformanceWorker"
    if not exe.exists():
        # Windows publishes with a .exe suffix.
        exe = WORKER_OUTPUT / "QueryFarm.VgiRpc.ConformanceWorker.exe"
    assert exe.exists(), f"Worker binary not found under {WORKER_OUTPUT}"
    return exe


def _spawn_http_worker(worker_binary: Path, *extra_args: str) -> Iterator[str]:
    """Spawns the worker in --http mode (plus any extra flags) and yields its base URL.

    vgi-rpc-test drives HTTP over an already-running server (`--url`), unlike pipe/unix/tcp's
    spawn-and-drive `--cmd` — see `docs/porting-guide.md`'s `--http` contract: the worker prints
    exactly `PORT:<port>\\n` on stdout, then flushes, once bound.
    """
    proc = subprocess.Popen(  # noqa: S603
        [str(worker_binary), "--http", *extra_args],
        cwd=REPO_ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    try:
        assert proc.stdout is not None
        deadline = time.monotonic() + 10
        port_line = ""
        while time.monotonic() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            if line.startswith("PORT:"):
                port_line = line
                break
        match = re.match(r"PORT:(\d+)", port_line)
        assert match, f"Worker did not print a PORT:<port> discovery line within 10s (got: {port_line!r})"
        yield f"http://127.0.0.1:{match.group(1)}"
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


@pytest.fixture
def http_worker(worker_binary: Path) -> Iterator[str]:
    """A plain --http worker, no auth."""
    yield from _spawn_http_worker(worker_binary)


@pytest.fixture
def http_auth_worker(worker_binary: Path) -> Iterator[str]:
    """A --http worker whose RPC endpoints all 401, reason driven by the
    X-Conformance-Auth-Reason request header — see docs/unauthorized-spec.md §7.1 and
    Program.cs's --conformance-auth-reason flag."""
    yield from _spawn_http_worker(worker_binary, "--conformance-auth-reason")


@pytest.fixture
def http_auth_proxy_worker(worker_binary: Path) -> Iterator[str]:
    """Same as http_auth_worker, plus a static proxy hint — for the docs/unauthorized-spec.md §5
    proxy-note tests."""
    yield from _spawn_http_worker(
        worker_binary,
        "--conformance-auth-reason",
        "--conformance-proxy-hint",
        "This service only accepts requests through its configured reverse proxy, which must "
        "set the X-Forwarded-Client-Cert header.",
    )


_CORS_ALLOWED_ORIGIN = "https://allowed.example.com"


@pytest.fixture
def cors_worker(worker_binary: Path) -> Iterator[str]:
    """A --http worker with CORS enabled for a single allowed origin — see
    docs/roadmap.md M9 CORS and Cors.cs's --conformance-cors-origin flag."""
    yield from _spawn_http_worker(worker_binary, "--conformance-cors-origin", _CORS_ALLOWED_ORIGIN)


def _run_vgi_rpc_test(cmd: str | None = None, *, url: str | None = None, filter_pattern: str | None = None) -> dict:
    assert (cmd is None) != (url is None), "pass exactly one of cmd or url"
    args = [
        sys.executable,
        "-c",
        "from vgi_rpc.conformance._test_cli import main; main()",
        *(["--cmd", cmd] if cmd is not None else ["--url", url]),  # type: ignore[list-item]
        "--format",
        "json",
    ]
    if filter_pattern:
        args += ["--filter", filter_pattern]

    result = subprocess.run(args, capture_output=True, text=True, cwd=REPO_ROOT)
    assert result.stdout, f"vgi-rpc-test produced no output.\nstderr:\n{result.stderr}"
    return json.loads(result.stdout)


def test_implemented_subset_fully_conformant(worker_binary: Path) -> None:
    """The pipe-transport unary subset implemented so far must pass 100%."""
    report = _run_vgi_rpc_test(str(worker_binary), filter_pattern=IMPLEMENTED_FILTER)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "Conformance failures in the implemented subset:\n" + "\n".join(
        f"  {t['name']}: {t.get('error', '')}" for t in failed
    )
    assert report["passed"] > 0, "Expected at least one test to run."


def test_full_suite_status(worker_binary: Path) -> None:
    """Informational: reports full-suite status without failing the build on known gaps
    (streaming, large_payload, and the wide-Arrow-type/list-of-struct dataclass methods —
    see docs/roadmap.md for what each remaining milestone unlocks)."""
    report = _run_vgi_rpc_test(str(worker_binary))
    print(f"\nFull conformance suite: {report['passed']} passed, {report['failed']} failed")


# A small mixed unary/stream/error subset — enough to exercise every access-log schema branch
# (request_data on unary, stream_id on stream, error_message on the error path) without paying
# to re-run the whole IMPLEMENTED_FILTER twice per --access-log posture below.
_ACCESS_LOG_FILTER = "scalar_echo.*,dataclass.echo_point,producer_stream.*,exchange_stream.echo,errors.*"


@pytest.mark.parametrize("debug", [False, True], ids=["info", "debug"])
def test_access_log_conforms(worker_binary: Path, tmp_path: Path, debug: bool) -> None:
    """The JSONL the worker writes via --access-log (and --access-log-debug, which additionally
    requires request_data to round-trip as a self-contained Arrow IPC stream — see
    docs/access-log-spec.md §4.3) must validate against vgi_rpc/access_log.schema.json. See
    docs/roadmap.md M5."""
    from vgi_rpc.access_log_conformance import _filter_access_logs, _parse_json_log_lines, validate_access_logs

    log_path = tmp_path / ("access-debug.jsonl" if debug else "access-info.jsonl")
    cmd = f"{worker_binary} --access-log {log_path}" + (" --access-log-debug" if debug else "")
    args = [
        sys.executable,
        "-c",
        "from vgi_rpc.conformance._test_cli import main; main()",
        "--cmd",
        cmd,
        "--access-log",
        str(log_path),
        "--format",
        "json",
        "--filter",
        _ACCESS_LOG_FILTER,
    ]
    if debug:
        args.append("--require-request-data")

    result = subprocess.run(args, capture_output=True, text=True, cwd=REPO_ROOT)
    assert result.returncode == 0, (
        f"vgi-rpc-test --access-log (debug={debug}) failed:\n"
        f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    assert log_path.exists(), f"worker did not write {log_path}"

    entries = _filter_access_logs(_parse_json_log_lines(log_path.read_text().splitlines()))
    assert entries, "no vgi_rpc.access entries were written"
    violations = validate_access_logs(entries)
    assert not violations, "access log violations:\n" + "\n".join(
        f"  entry {v.entry_index} ({v.method}) {v.path}: {v.message}" for v in violations
    )


def test_wide_arrow_types_round_trip(worker_binary: Path) -> None:
    """The wide-Arrow-type echo methods (int8/16/uint8/16/32/64, date, timestamp[_utc], time,
    duration, decimal — see docs/roadmap.md "M2, continued") round-trip correctly.

    Not (yet) exercised by vgi-rpc-test's own --filter categories: these methods currently exist
    only in the __describe__ conformance test's _EXPECTED_METHODS set and the not-yet-tested
    echo_wide_types composite, so there's no `category.name` to select via IMPLEMENTED_FILTER.
    Verified directly against the real Python reference client instead, via the same
    SubprocessTransport the framework itself uses (a hand-rolled `subprocess.Popen` without its
    BufferedReader wrapping looks like a server-side short-read bug that isn't one — POSIX pipes
    may return fewer bytes than requested, which SubprocessTransport already accounts for).
    """
    import datetime
    import decimal

    from vgi_rpc.conformance._protocol import ConformanceService
    from vgi_rpc.rpc import RpcConnection
    from vgi_rpc.rpc._transport import SubprocessTransport

    transport = SubprocessTransport([str(worker_binary)])
    with RpcConnection(ConformanceService, transport) as svc:
        checks = [
            ("echo_int8", -100, svc.echo_int8(value=-100)),
            ("echo_int16", -30000, svc.echo_int16(value=-30000)),
            ("echo_uint8", 250, svc.echo_uint8(value=250)),
            ("echo_uint16", 60000, svc.echo_uint16(value=60000)),
            ("echo_uint32", 4_000_000_000, svc.echo_uint32(value=4_000_000_000)),
            ("echo_uint64", 18_000_000_000_000_000_000, svc.echo_uint64(value=18_000_000_000_000_000_000)),
            ("echo_date", datetime.date(2026, 8, 24), svc.echo_date(value=datetime.date(2026, 8, 24))),
            (
                "echo_timestamp",
                datetime.datetime(2026, 8, 24, 12, 30, 45, 123456),
                svc.echo_timestamp(value=datetime.datetime(2026, 8, 24, 12, 30, 45, 123456)),
            ),
            (
                "echo_timestamp_utc",
                datetime.datetime(2026, 8, 24, 12, 30, 45, 123456, tzinfo=datetime.timezone.utc),
                svc.echo_timestamp_utc(value=datetime.datetime(2026, 8, 24, 12, 30, 45, 123456, tzinfo=datetime.timezone.utc)),
            ),
            ("echo_time", datetime.time(13, 45, 30, 123456), svc.echo_time(value=datetime.time(13, 45, 30, 123456))),
            (
                "echo_duration",
                datetime.timedelta(hours=3, minutes=15, microseconds=500),
                svc.echo_duration(value=datetime.timedelta(hours=3, minutes=15, microseconds=500)),
            ),
            ("echo_decimal", decimal.Decimal("12345.6789"), svc.echo_decimal(value=decimal.Decimal("12345.6789"))),
        ]
    failed = [f"  {name}: expected {expected!r}, got {actual!r}" for name, expected, actual in checks if expected != actual]
    assert not failed, "Wide-Arrow-type round-trip failures:\n" + "\n".join(failed)


def test_http_subset_conformant(http_worker: str) -> None:
    """The same IMPLEMENTED_FILTER subset gated over stdio (unary + full streaming — /init,
    /exchange, headers, cancel, dynamic schemas) must pass 100% against the real Python reference
    client over HTTP too, driven via `--url` (unlike pipe/unix/tcp's spawn-and-drive `--cmd` —
    HTTP tests an already-running server). See docs/roadmap.md M6: HTTP streaming dispatch runs
    exactly one lockstep turn per `/exchange` call (StreamCallRegistry keeps the live
    IRpcStream/StreamState server-side, keyed by a sealed call-id token) rather than the
    canonical Python server's accumulate-until-response-cap producer loop — simpler, and
    (unlike accumulate-until-cap) it makes mid-stream cancel trivial, which is exactly what
    cancel.* over HTTP exercises."""
    report = _run_vgi_rpc_test(url=http_worker, filter_pattern=IMPLEMENTED_FILTER)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "HTTP conformance failures:\n" + "\n".join(f"  {t['name']}: {t.get('error', '')}" for t in failed)
    assert report["passed"] > 0, "Expected at least one HTTP test to run."


def _arrow_request_body(method: str) -> bytes:
    """Builds a minimal valid Arrow IPC request body for `method` — enough to reach the
    authenticate hook (which runs before method dispatch, so the body's actual content never
    matters for these tests)."""
    import pyarrow as pa
    from vgi_rpc.utils import new_ipc_stream

    buf = __import__("io").BytesIO()
    schema = pa.schema([pa.field("value", pa.utf8())])
    with new_ipc_stream(buf, schema) as writer:
        writer.write_batch(
            pa.RecordBatch.from_pydict({"value": ["x"]}, schema=schema),
            custom_metadata={b"vgi_rpc.method": method.encode(), b"vgi_rpc.request_version": b"1"},
        )
    return buf.getvalue()


# M8 (see docs/roadmap.md): the normative cross-language contract is
# ~/Development/vgi-rpc/docs/unauthorized-spec.md §7's TestUnauthorized table. Its own pytest
# fixtures (conformance_http_auth_port, etc.) are wired through that repo's own conftest
# machinery that this repo doesn't hook into, so these check the same properties directly against
# the real HTTP responses instead — reading straight off the spec doc rather than guessing.
class TestUnauthorized:
    """Mirrors docs/unauthorized-spec.md §7's TestUnauthorized table."""

    def test_reason_header_present(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        assert resp.status_code == 401
        assert "VGI-Auth-Reason" in resp.headers

    def test_reason_in_closed_set(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        closed_set = {
            "missing_credential",
            "invalid_credential",
            "expired_credential",
            "insufficient_scope",
            "proxy_required",
            "unauthorized",
        }
        assert resp.headers["VGI-Auth-Reason"] in closed_set

    def test_json_envelope_for_machine_clients(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream", "Accept": "*/*"},
        )
        assert resp.headers["content-type"].startswith("application/json")
        body = resp.json()
        assert body["error"] == "unauthorized"
        assert body["reason"] == resp.headers["VGI-Auth-Reason"]
        assert "detail" in body

    def test_html_page_for_browsers(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream", "Accept": "text/html"},
        )
        assert resp.headers["content-type"].startswith("text/html")
        assert "VGI-Auth-Reason" in resp.headers

    def test_not_cached(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        assert "no-store" in resp.headers.get("Cache-Control", "")

    def test_no_proxy_note_without_proxy_auth(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        assert "VGI-Auth-Proxy-Required" not in resp.headers
        assert "proxy_hint" not in resp.json()

    def test_proxy_note_when_proxy_required(self, http_auth_proxy_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_proxy_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        assert resp.headers.get("VGI-Auth-Proxy-Required") == "true"
        assert resp.json().get("proxy_hint")

    @pytest.mark.parametrize(
        "reason", ["missing_credential", "invalid_credential", "expired_credential", "insufficient_scope"]
    )
    def test_requested_reason_is_honoured(self, http_auth_worker: str, reason: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream", "X-Conformance-Auth-Reason": reason},
        )
        assert resp.headers["VGI-Auth-Reason"] == reason
        assert resp.json()["reason"] == reason

    def test_unclassified_failure_is_unauthorized(self, http_auth_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream", "X-Conformance-Auth-Reason": "no-such-reason"},
        )
        assert resp.headers["VGI-Auth-Reason"] == "unauthorized"

    def test_reason_codes_are_distinct(self, http_auth_worker: str) -> None:
        import httpx2

        reasons = ["missing_credential", "invalid_credential", "expired_credential", "insufficient_scope"]
        seen = set()
        for reason in reasons:
            resp = httpx2.post(
                f"{http_auth_worker}/echo_string",
                content=_arrow_request_body("echo_string"),
                headers={"Content-Type": "application/vnd.apache.arrow.stream", "X-Conformance-Auth-Reason": reason},
            )
            seen.add(resp.headers["VGI-Auth-Reason"])
        assert len(seen) == len(reasons)

    def test_proxy_required_is_not_request_driven(self, http_auth_worker: str) -> None:
        """A worker with no proxy dependency configured must never emit proxy_required, even
        when a request explicitly asks for it — docs/unauthorized-spec.md §5/§7.1."""
        import httpx2

        resp = httpx2.post(
            f"{http_auth_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream", "X-Conformance-Auth-Reason": "proxy_required"},
        )
        assert resp.headers["VGI-Auth-Reason"] != "proxy_required"


# M9 (see docs/roadmap.md): CORS is a browser-only mechanism — the vgi-rpc-test CLI is a plain
# HTTP client that never sends Origin/Access-Control-Request-* headers, so it can't exercise this
# at all. These check the real preflight + actual-response behavior directly, mirroring
# TestUnauthorized's approach for the same underlying reason (no shared pytest fixture machinery
# with the canonical Python repo for this either).
class TestCors:
    """Verifies Cors.cs's ASP.NET Core wiring against a worker started with
    --conformance-cors-origin (see the cors_worker fixture)."""

    def test_preflight_allowed_origin_gets_cors_headers(self, cors_worker: str) -> None:
        import httpx2

        resp = httpx2.request(
            "OPTIONS",
            f"{cors_worker}/health",
            headers={"Origin": _CORS_ALLOWED_ORIGIN, "Access-Control-Request-Method": "GET"},
        )
        assert resp.headers["Access-Control-Allow-Origin"] == _CORS_ALLOWED_ORIGIN
        assert "GET" in resp.headers["Access-Control-Allow-Methods"]
        assert "POST" in resp.headers["Access-Control-Allow-Methods"]
        assert resp.headers["Access-Control-Max-Age"] == "7200"

    def test_preflight_disallowed_origin_gets_no_cors_headers(self, cors_worker: str) -> None:
        import httpx2

        resp = httpx2.request(
            "OPTIONS",
            f"{cors_worker}/health",
            headers={"Origin": "https://evil.example.com", "Access-Control-Request-Method": "GET"},
        )
        assert "Access-Control-Allow-Origin" not in resp.headers

    def test_actual_response_exposes_headers(self, cors_worker: str) -> None:
        import httpx2

        resp = httpx2.get(f"{cors_worker}/health", headers={"Origin": _CORS_ALLOWED_ORIGIN})
        assert resp.status_code == 200
        assert resp.headers["Access-Control-Allow-Origin"] == _CORS_ALLOWED_ORIGIN
        exposed = resp.headers["Access-Control-Expose-Headers"]
        assert "X-VGI-RPC-Error" in exposed
        assert "VGI-Max-Response-Bytes" in exposed
        assert "Cross-Origin-Resource-Policy" in resp.headers
        assert resp.headers["Cross-Origin-Resource-Policy"] == "cross-origin"

    def test_disallowed_origin_actual_response_has_no_allow_origin(self, cors_worker: str) -> None:
        """ASP.NET Core's CORS middleware doesn't block a simple/actual request server-side (the
        browser is what enforces the same-origin restriction on the *response*) — but it must omit
        Access-Control-Allow-Origin for a disallowed origin, or a browser would let the page read
        the response."""
        import httpx2

        resp = httpx2.get(f"{cors_worker}/health", headers={"Origin": "https://evil.example.com"})
        assert resp.status_code == 200
        assert "Access-Control-Allow-Origin" not in resp.headers

    def test_no_cors_flag_means_no_cors_headers(self, http_worker: str) -> None:
        """A worker started without --conformance-cors-origin (the http_worker fixture) must not
        emit any CORS headers at all — CORS is opt-in, matching corsPolicyName's null default in
        MapVgiRpc."""
        import httpx2

        resp = httpx2.get(f"{http_worker}/health", headers={"Origin": _CORS_ALLOWED_ORIGIN})
        assert "Access-Control-Allow-Origin" not in resp.headers
        assert "Cross-Origin-Resource-Policy" not in resp.headers
        assert "VGI-Auth-Proxy-Required" not in resp.headers
