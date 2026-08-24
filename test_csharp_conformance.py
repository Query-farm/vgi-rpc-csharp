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
import shutil
import subprocess
import sys
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


def _run_vgi_rpc_test(cmd: str, *, filter_pattern: str | None = None) -> dict:
    args = [
        sys.executable,
        "-c",
        "from vgi_rpc.conformance._test_cli import main; main()",
        "--cmd",
        cmd,
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
