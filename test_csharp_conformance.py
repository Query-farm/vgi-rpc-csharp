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


# M6 (in progress — see docs/roadmap.md): the HTTP transport serves unary calls only so far,
# streaming (/init, /exchange) isn't implemented yet — a subset of IMPLEMENTED_FILTER's
# categories, with every stream-shaped one dropped.
HTTP_UNARY_FILTER = ",".join(
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
        "dataclass.echo_all_types",
        "dataclass.echo_all_types_with_nulls",
    ]
)


@pytest.fixture
def http_worker(worker_binary: Path) -> Iterator[str]:
    """Spawns the worker in --http mode and yields its base URL.

    vgi-rpc-test drives HTTP over an already-running server (`--url`), unlike pipe/unix/tcp's
    spawn-and-drive `--cmd` — see `docs/porting-guide.md`'s `--http` contract: the worker prints
    exactly `PORT:<port>\\n` on stdout, then flushes, once bound.
    """
    proc = subprocess.Popen(  # noqa: S603
        [str(worker_binary), "--http"],
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


def test_http_unary_subset_conformant(http_worker: str) -> None:
    """The HTTP transport's unary subset (see docs/roadmap.md M6 — streaming isn't implemented
    over HTTP yet) must pass 100% against the real Python reference client, driven via `--url`
    (unlike pipe/unix/tcp's spawn-and-drive `--cmd` — HTTP tests an already-running server)."""
    report = _run_vgi_rpc_test(url=http_worker, filter_pattern=HTTP_UNARY_FILTER)
    failed = [t for t in report["results"] if not t["passed"] and not t["skipped"]]
    assert not failed, "HTTP conformance failures in the unary subset:\n" + "\n".join(
        f"  {t['name']}: {t.get('error', '')}" for t in failed
    )
    assert report["passed"] > 0, "Expected at least one HTTP test to run."
