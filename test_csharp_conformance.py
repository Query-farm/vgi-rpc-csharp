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
import tempfile
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
# support lands, M4+ adds http_response_cap; M17 closes the remaining large_payload gap (see
# docs/roadmap.md) — echo_binary_4mib (a real 4 MiB round trip, catching short-write bugs) and the
# mandatory echo_binary_over_int32_max (2^31+1 bytes, which no managed byte[]/reader buffer on any
# .NET runtime can hold — this port answers with a typed PayloadTooLargeException refusal that
# drains the oversized body first so the connection survives, which the reference's own
# _accept_typed_refusal helper explicitly sanctions).
IMPLEMENTED_FILTER = ",".join(
    [
        "large_payload.*",
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
    """A plain --http worker, no auth. --max-response-bytes is bumped from the worker's own 64
    KiB default to comfortably fit large_payload.echo_binary_4mib's real 4 MiB response (M17) —
    safe to raise here because http_response_cap.unary_strict_fail/exchange_strict_fail (also in
    IMPLEMENTED_FILTER) read the server's own advertised cap via caps.max_response_bytes and
    request 4x *that*, so they still trigger the same strict-fail path at any cap value."""
    yield from _spawn_http_worker(worker_binary, "--max-response-bytes", str(8 * 1024 * 1024))


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


def _spawn_http_worker_port(worker_binary: Path, *extra_args: str) -> Iterator[int]:
    """Like _spawn_http_worker, but yields the bound port as an int — the shape the canonical
    vgi_rpc.conformance._pytest_suite.TestSticky group's fixtures expect (it does its own
    http://127.0.0.1:{port} URL-building internally)."""
    gen = _spawn_http_worker(worker_binary, *extra_args)
    url = next(gen)
    try:
        yield int(url.rsplit(":", 1)[1])
    finally:
        next(gen, None)


# M10 (see docs/roadmap.md): sticky sessions. Unlike CORS/mTLS (no canonical pytest counterpart),
# the reference repo ships a full TestSticky group in vgi_rpc.conformance._pytest_suite — imported
# directly below (mirroring vgi-rpc-java/tests/test_java_conformance.py's own
# `from vgi_rpc.conformance._pytest_suite import *` pattern, narrowed to just this one class since
# this port doesn't implement the suite's other groups yet). Its fixtures are named
# conformance_http_port / conformance_http_sticky_short_ttl_port / _peer_ports / _auth_port,
# defined here rather than in the imported module (spec §9.1: a port claiming sticky support must
# supply all three failure-path fixtures).
@pytest.fixture
def conformance_http_port(worker_binary: Path) -> Iterator[int]:
    """Primary HTTP worker shared by sticky and compression conformance groups."""
    yield from _spawn_http_worker_port(
        worker_binary, "--conformance-sticky", "--max-response-bytes", str(8 * 1024 * 1024)
    )


@pytest.fixture
def conformance_http_sticky_short_ttl_port(worker_binary: Path) -> Iterator[int]:
    """A sticky worker whose default TTL (1s) is short enough for
    test_expired_session_surfaces_session_lost to outwait."""
    yield from _spawn_http_worker_port(worker_binary, "--sticky-ttl", "1")


@pytest.fixture
def conformance_http_sticky_peer_ports(worker_binary: Path) -> Iterator[tuple[int, int]]:
    """Two sticky workers sharing one AEAD token key, for the wrong-worker check
    (test_token_from_other_worker_rejected). RpcServer mints a random server_id per process, so
    the two peers differ without any extra flag — see Program.cs's --token-key comment."""
    shared_key = "5f" * 32
    gen_a = _spawn_http_worker_port(worker_binary, "--conformance-sticky", "--token-key", shared_key)
    gen_b = _spawn_http_worker_port(worker_binary, "--conformance-sticky", "--token-key", shared_key)
    port_a = next(gen_a)
    try:
        port_b = next(gen_b)
        try:
            yield port_a, port_b
        finally:
            next(gen_b, None)
    finally:
        next(gen_a, None)


@pytest.fixture
def conformance_http_sticky_auth_port(worker_binary: Path) -> Iterator[int]:
    """A sticky worker that authenticates the X-Conformance-Principal header — for
    test_cross_principal_replay_rejected."""
    yield from _spawn_http_worker_port(worker_binary, "--sticky-auth")


# M11 (see docs/roadmap.md): proxy proof. Like M10 sticky sessions, the canonical Python repo
# ships a full TestProxyProof (+ TestProxyProofOffMode) group in vgi_rpc.conformance._pytest_suite
# — imported directly below, mirroring vgi-rpc-java/tests/test_java_conformance.py's own
# proof_worker_factory fixture. The suite owns the test matrix; this only has to know how to spawn
# one worker for a given vgi_rpc.conformance.proof_harness.ProofWorkerConfig.
@pytest.fixture
def proof_worker_factory(worker_binary: Path):
    """Returns a callable(ProofWorkerConfig) -> context manager yielding a ProofWorker."""
    import contextlib

    from vgi_rpc.conformance.proof_harness import ProofWorker

    @contextlib.contextmanager
    def spawn(config):
        args = [
            "--proof-mode",
            config.mode,
            "--proof-origin-id",
            config.origin_id,
            "--proof-secrets",
            config.secrets,
            "--proof-skew",
            str(config.skew_seconds),
        ]
        if not config.replay_cache:
            args.append("--proof-no-replay-cache")
        gen = _spawn_http_worker_port(worker_binary, *args)
        port = next(gen)
        try:
            yield ProofWorker(port=port, prefix="", config=config)
        finally:
            next(gen, None)

    yield spawn


# M12 (see docs/roadmap.md): token introspection. Same import-the-canonical-suite pattern as
# M10/M11. TestTokenIntrospection needs one worker with the fixed constants
# vgi_rpc.conformance._pytest_suite requires; TestTokenIntrospectionOffMode reuses
# conformance_http_port (the M10 sticky fixture, no introspect resolver configured).
@pytest.fixture
def conformance_http_introspect_port(worker_binary: Path) -> Iterator[int]:
    """A worker with token introspection enabled — for TestTokenIntrospection."""
    yield from _spawn_http_worker_port(worker_binary, "--introspect")


@pytest.fixture
def conformance_http_small_request_cap_port(worker_binary: Path) -> Iterator[int]:
    """Worker used by the canonical encoded/decoded request-cap regression matrix."""
    yield from _spawn_http_worker_port(
        worker_binary, "--max-request-bytes", "4096", "--max-response-bytes", str(8 * 1024 * 1024)
    )


_CORS_ALLOWED_ORIGIN = "https://allowed.example.com"


@pytest.fixture
def cors_worker(worker_binary: Path) -> Iterator[str]:
    """A --http worker with CORS enabled for a single allowed origin — see
    docs/roadmap.md M9 CORS and Cors.cs's --conformance-cors-origin flag."""
    yield from _spawn_http_worker(worker_binary, "--conformance-cors-origin", _CORS_ALLOWED_ORIGIN)


@pytest.fixture
def mtls_worker(worker_binary: Path) -> Iterator[str]:
    """A --http worker with MtlsAuth.FromSubject() installed as the authenticate delegate — see
    docs/roadmap.md M9 mTLS and Mtls.cs's --conformance-mtls-subject flag."""
    yield from _spawn_http_worker(worker_binary, "--conformance-mtls-subject")


# M13 (see docs/roadmap.md): external storage. Same import-the-canonical-suite pattern as
# M10/M11/M12 — TestExternalLocation/TestExternalizedResponseCap (vgi_rpc.conformance._pytest_suite)
# and TestExternalInputRoutes/TestExternalFetchFailures/TestExternalFetchSecurity/
# TestExternalStorageUrlPair (vgi_rpc.conformance._external_pytest) are imported below. Every
# variant needs a fake-storage HTTP service — run in-process on a background thread, mirroring the
# canonical repo's own conftest.py fixture, so the C# worker subprocess can reach it over loopback.
@pytest.fixture(scope="session")
def conformance_fake_storage() -> Iterator[str]:
    """In-process fake object-storage service (vgi_rpc.conformance.fake_storage) for
    external-location conformance tests. Yields its base URL."""
    from vgi_rpc.conformance.fake_storage import serve_in_thread

    base_url, shutdown = serve_in_thread()
    try:
        yield base_url
    finally:
        shutdown()


@pytest.fixture
def conformance_http_with_storage_port(worker_binary: Path, conformance_fake_storage: str) -> Iterator[int]:
    """HTTP worker wired against the fake storage, at the worker's own 4 KiB default externalize
    threshold, so tests can trigger externalization without megabyte payloads."""
    yield from _spawn_http_worker_port(worker_binary, "--fake-storage", conformance_fake_storage)


@pytest.fixture
def conformance_http_with_zstd_storage_port(worker_binary: Path, conformance_fake_storage: str) -> Iterator[int]:
    """Same, with zstd compression enabled on externalized batches."""
    yield from _spawn_http_worker_port(
        worker_binary, "--fake-storage", conformance_fake_storage, "--compression", "zstd"
    )


@pytest.fixture
def conformance_http_external_security_port(worker_binary: Path, conformance_fake_storage: str) -> Iterator[int]:
    """The canonical external-fetch security configuration: independent encoded (4 KiB) and
    decoded (8 KiB) caps, plus a redirect-hop validator admitting only 127.0.0.1 (so the
    fixture's own storage URLs work but a redirect to `localhost` is rejected before it's ever
    fetched)."""
    yield from _spawn_http_worker_port(
        worker_binary,
        "--fake-storage",
        conformance_fake_storage,
        "--max-request-bytes",
        "1048576",
        "--max-fetch-bytes",
        "4096",
        "--max-decompressed-fetch-bytes",
        "8192",
        "--reject-localhost-redirects",
    )


# Tight external cap, *generous* request/response body caps: an externalised payload leaves only
# a pointer batch on the wire, so if the body caps were tight too they'd fail first and
# TestExternalizedResponseCap would pass while proving nothing about the external channel. Mirrors
# vgi-rpc-rust/test_rust_conformance.py's own _EXT_CAP_MAX_* constants and _start_rust_http_with_
# storage(...) call for its "externalized_cap" variant — the closest existing precedent for a
# single-worker (not two-script) port unifying what the reference repo splits across
# serve_conformance_http.py / serve_conformance_http_strict.py.
_EXT_CAP_MAX_EXTERNALIZED_BYTES = 64 * 1024
_EXT_CAP_MAX_RESPONSE_BYTES = 8 * 1024 * 1024


@pytest.fixture
def conformance_http_externalized_cap_port(worker_binary: Path, conformance_fake_storage: str) -> Iterator[int]:
    """A worker whose *external-channel* cap is the one that bites: tight
    (max_externalized_response_bytes = 64 KiB) while max_request_bytes/max_response_bytes stay
    generous (8 MiB) so the body caps are never what fails. --externalize-threshold stays at the
    worker's own 4 KiB default so a modest payload still externalizes, backing the group's
    under-cap control case."""
    yield from _spawn_http_worker_port(
        worker_binary,
        "--fake-storage",
        conformance_fake_storage,
        "--max-request-bytes",
        str(_EXT_CAP_MAX_RESPONSE_BYTES),
        "--max-response-bytes",
        str(_EXT_CAP_MAX_RESPONSE_BYTES),
        "--max-externalized-response-bytes",
        str(_EXT_CAP_MAX_EXTERNALIZED_BYTES),
    )


def _make_test_cert(cn: str = "test-client", *, days_valid: int = 365, not_before_offset=None) -> str:
    """Generates a self-signed certificate and returns it URL-encoded PEM, ready to drop straight
    into an X-SSL-Client-Cert header — mirrors the canonical Python repo's
    tests/test_mtls.py::_make_test_cert + _cert_to_header exactly (same shape, so the two repos'
    mTLS coverage stays comparable)."""
    import datetime
    from urllib.parse import quote

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.UTC)
    not_before = now + not_before_offset if not_before_offset else now - datetime.timedelta(hours=1)
    not_after = not_before + datetime.timedelta(days=days_valid)
    subject = issuer = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, cn)])
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(not_before)
        .not_valid_after(not_after)
        .sign(key, hashes.SHA256())
    )
    pem = cert.public_bytes(serialization.Encoding.PEM).decode()
    return quote(pem)


def _spawn_unix_worker(worker_binary: Path, *extra_args: str) -> Iterator[str]:
    """Spawns the worker in --unix mode (plus any extra flags) and yields its socket path.

    Unlike --cmd's spawn-and-drive-over-stdio model (where vgi-rpc-test itself owns the
    subprocess), --unix/--tcp run the worker as an independently-listening background process
    vgi-rpc-test connects to afterward — see _spawn_http_worker's own docstring for the same
    distinction, which this mirrors.
    """
    sock_path = tempfile.mktemp(prefix="vgi-rpc-test-", suffix=".sock")
    proc = subprocess.Popen(  # noqa: S603
        [str(worker_binary), "--unix", sock_path, *extra_args],
        cwd=REPO_ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    try:
        assert proc.stdout is not None
        line = proc.stdout.readline()
        assert line.startswith("UNIX:"), f"Worker did not print a UNIX:<path> discovery line (got: {line!r})"
        # The discovery line prints just before the listener actually binds (see Program.cs) — poll
        # for the socket file itself rather than trusting the print alone as proof it's up yet.
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and not Path(sock_path).exists():
            time.sleep(0.05)
        assert Path(sock_path).exists(), f"Worker never created the unix socket at {sock_path}"
        yield sock_path
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        Path(sock_path).unlink(missing_ok=True)


def _spawn_tcp_worker(worker_binary: Path, *extra_args: str) -> Iterator[str]:
    """Spawns the worker in --tcp mode against an OS-assigned ephemeral port (avoids picking a
    fixed port that could collide) and yields the actual bound "host:port" it reports back."""
    proc = subprocess.Popen(  # noqa: S603
        [str(worker_binary), "--tcp", "127.0.0.1:0", *extra_args],
        cwd=REPO_ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    try:
        assert proc.stdout is not None
        deadline = time.monotonic() + 10
        discovery_line = ""
        while time.monotonic() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            if line.startswith("TCP:"):
                discovery_line = line
                break
        match = re.fullmatch(r"TCP:(.+):(\d+)\s*", discovery_line)
        assert match, f"Worker did not print a TCP:<host>:<port> discovery line within 10s (got: {discovery_line!r})"
        yield f"{match.group(1)}:{match.group(2)}"
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


@pytest.fixture
def unix_worker(worker_binary: Path) -> Iterator[str]:
    yield from _spawn_unix_worker(worker_binary)


@pytest.fixture
def tcp_worker(worker_binary: Path) -> Iterator[str]:
    yield from _spawn_tcp_worker(worker_binary)


def _run_vgi_rpc_test(
    cmd: str | None = None,
    *,
    url: str | None = None,
    unix: str | None = None,
    tcp: str | None = None,
    filter_pattern: str | None = None,
    shm_size: int | None = None,
) -> dict:
    modes = [m for m in (cmd, url, unix, tcp) if m is not None]
    assert len(modes) == 1, "pass exactly one of cmd, url, unix, or tcp"
    assert shm_size is None or cmd is not None, "--shm only applies to --cmd (pipe transport)"
    if cmd is not None:
        transport_args = ["--cmd", cmd]
    elif url is not None:
        transport_args = ["--url", url]
    elif unix is not None:
        transport_args = ["--unix", unix]
    else:
        assert tcp is not None
        transport_args = ["--tcp", tcp]

    args = [
        sys.executable,
        "-c",
        "from vgi_rpc.conformance._test_cli import main; main()",
        *transport_args,
        "--format",
        "json",
    ]
    if filter_pattern:
        args += ["--filter", filter_pattern]
    if shm_size is not None:
        args += ["--shm", str(shm_size)]

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


@pytest.mark.skipif(
    sys.platform == "darwin",
    reason=(
        "macOS's multiprocessing.shared_memory backs segments via true POSIX shm_open, not a "
        "discoverable file path the way Linux places them at exactly /dev/shm/<name> — this "
        "port's ShmSegment (see docs/roadmap.md M14) only implements the Linux/Windows split "
        "CLAUDE.md actually targets, with a plain-temp-file macOS fallback that only "
        "self-interoperates within this port, not with the reference Python client. Verified "
        "working end-to-end against the real Python client in a linux/amd64 Docker container "
        "before this test was added; CI's own conformance job runs on ubuntu-latest only, so "
        "this still gates every real push."
    ),
)
def test_shm_transport_implemented_subset_fully_conformant(worker_binary: Path) -> None:
    """The same implemented subset, driven over the SHM side channel (M14, docs/roadmap.md) —
    the client owns an 8 MiB segment and both directions (request params, unary/stream results)
    transparently offload batches large enough to cross the offload threshold, falling back to
    inline pipe transmission for everything smaller. Mirrors the pattern vgi-rpc-go's own
    conformance suite established for its "shm" transport variant — the closest existing
    precedent for exercising this over the *whole* implemented filter rather than a hand-picked
    smoke subset."""
    report = _run_vgi_rpc_test(str(worker_binary), filter_pattern=IMPLEMENTED_FILTER, shm_size=8 * 1024 * 1024)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "SHM-transport conformance failures in the implemented subset:\n" + "\n".join(
        f"  {t['name']}: {t.get('error', '')}" for t in failed
    )
    assert report["passed"] > 0, "Expected at least one test to run."


def test_unix_transport_implemented_subset_fully_conformant(unix_worker: str) -> None:
    """The full IMPLEMENTED_FILTER, streaming included, driven over a Unix domain socket (M17,
    docs/roadmap.md) — RpcServer's core dispatch loop (ServeAsync/ServeOneAsync) is
    transport-agnostic, but a NetworkStream-backed transport exercises real partial-read behavior
    a pipe's own OS buffering can mask (see WireReader/PayloadTooLargeException, added for exactly
    this transport family), so this is a genuine additional check, not just the same test rerun
    for coverage's sake. Streaming was excluded here for a time (a real bug, not a deliberate
    scope cut — see docs/roadmap.md's M17/M18 entries for the root cause and fix: a zero-length
    RecordBatch body blocked forever on a NetworkStream-backed ReadAsync instead of completing
    immediately) and is now included like every other transport."""
    report = _run_vgi_rpc_test(unix=unix_worker, filter_pattern=IMPLEMENTED_FILTER)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "Unix-transport conformance failures in the implemented subset:\n" + "\n".join(
        f"  {t['name']}: {t.get('error', '')}" for t in failed
    )
    assert report["passed"] > 0, "Expected at least one test to run."


def test_tcp_transport_implemented_subset_fully_conformant(tcp_worker: str) -> None:
    """Same as test_unix_transport_implemented_subset_fully_conformant, over TCP loopback."""
    report = _run_vgi_rpc_test(tcp=tcp_worker, filter_pattern=IMPLEMENTED_FILTER)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "TCP-transport conformance failures in the implemented subset:\n" + "\n".join(
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


# M9 (see docs/roadmap.md): mTLS, like CORS, has no vgi-rpc-test coverage — it depends on a
# reverse-proxy-injected header the CLI never sends. Verified directly against real certificates
# generated with `cryptography` (the same library the canonical Python repo's own
# tests/test_mtls.py uses), driving Mtls.cs's real PEM parsing / X509Certificate2 code path.
class TestMtls:
    """Verifies Mtls.cs's MtlsAuth.FromSubject() against a worker started with
    --conformance-mtls-subject (see the mtls_worker fixture)."""

    def test_valid_cert_is_accepted(self, mtls_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{mtls_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={
                "Content-Type": "application/vnd.apache.arrow.stream",
                "X-SSL-Client-Cert": _make_test_cert("rpc-client"),
            },
        )
        assert resp.status_code == 200

    def test_missing_header_is_proxy_required(self, mtls_worker: str) -> None:
        import httpx2

        resp = httpx2.post(
            f"{mtls_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={"Content-Type": "application/vnd.apache.arrow.stream"},
        )
        assert resp.status_code == 401
        assert resp.headers["VGI-Auth-Reason"] == "proxy_required"

    def test_malformed_header_is_invalid_credential(self, mtls_worker: str) -> None:
        import httpx2
        from urllib.parse import quote

        resp = httpx2.post(
            f"{mtls_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={
                "Content-Type": "application/vnd.apache.arrow.stream",
                "X-SSL-Client-Cert": quote("not a certificate"),
            },
        )
        assert resp.status_code == 401
        assert resp.headers["VGI-Auth-Reason"] == "invalid_credential"

    def test_expired_cert_is_still_accepted_without_check_expiry(self, mtls_worker: str) -> None:
        """MtlsAuth.FromSubject() defaults checkExpiry=false (matching Python's default) — an
        expired-but-otherwise-well-formed certificate is still accepted unless the operator opts
        into expiry checking."""
        import datetime

        import httpx2

        resp = httpx2.post(
            f"{mtls_worker}/echo_string",
            content=_arrow_request_body("echo_string"),
            headers={
                "Content-Type": "application/vnd.apache.arrow.stream",
                "X-SSL-Client-Cert": _make_test_cert(
                    "expired-client", days_valid=0, not_before_offset=datetime.timedelta(days=-2)
                ),
            },
        )
        assert resp.status_code == 200
        assert "VGI-Auth-Proxy-Required" not in resp.headers


# M10: the canonical TestSticky group, collected directly against the fixtures defined above
# (conformance_http_port et al.) — see the comment above those fixtures for why this is imported
# rather than hand-written like TestUnauthorized/TestCors/TestMtls.
from vgi_rpc.conformance._pytest_suite import TestSticky  # noqa: E402,F401

# M11: the canonical TestProxyProof (+ TestProxyProofOffMode) groups, collected against the
# proof_worker_factory fixture defined above. TestProxyProofOffMode reuses conformance_http_port
# (the M10 sticky fixture) — a sticky-enabled worker with no proxy-proof gate configured still
# satisfies "unconfigured worker accepts without a proof", which is exactly the property under test.
from vgi_rpc.conformance._pytest_suite import TestProxyProof, TestProxyProofOffMode  # noqa: E402,F401

# M12: the canonical TestTokenIntrospection (+ TestTokenIntrospectionOffMode) groups, collected
# against conformance_http_introspect_port (above) and conformance_http_port (M10's sticky
# fixture) respectively.
from vgi_rpc.conformance._pytest_suite import TestTokenIntrospection, TestTokenIntrospectionOffMode  # noqa: E402,F401

# M13: the canonical TestExternalLocation + TestExternalizedResponseCap groups
# (vgi_rpc.conformance._pytest_suite), collected against conformance_http_with_storage_port /
# conformance_http_with_zstd_storage_port / conformance_http_externalized_cap_port above.
from vgi_rpc.conformance._pytest_suite import TestExternalLocation, TestExternalizedResponseCap  # noqa: E402,F401
from vgi_rpc.conformance._pytest_suite import TestHttpCompressionNegotiationConformance  # noqa: E402,F401

# Response negotiation and compressed request limits are protocol-level compatibility/security
# gates. The canonical tests require both zstd and gzip, check VGI-vs-generic header precedence,
# and prove that a small encoded body cannot expand beyond max_request_bytes.
from vgi_rpc.conformance._request_limits_pytest import TestCompressedHttpRequestCap  # noqa: E402,F401

# M13: the canonical external-fetch groups (vgi_rpc.conformance._external_pytest) — a small
# raw-HTTP driver separate from _pytest_suite because these tests place external-location pointer
# batches on inbound request routes directly, which the ordinary RPC proxy deliberately hides.
from vgi_rpc.conformance._external_pytest import (  # noqa: E402,F401
    TestExternalFetchFailures,
    TestExternalFetchSecurity,
    TestExternalInputRoutes,
    TestExternalStorageUrlPair,
)
