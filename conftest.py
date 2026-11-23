"""Locates the vgi-rpc[conformance] Python package for test_csharp_conformance.py.

Mirrors the pattern vgi-rpc-java's run_tests.sh uses: prefer a local checkout's venv (the
canonical Python repo carries unreleased protocol features ahead of what's on PyPI — and the
multiservice work is *only* there), with two escape hatches:

  VGI_RPC_PYTHON  — path to a Python interpreter that already has vgi_rpc[conformance] installed
  VGI_RPC_SITE    — a site-packages directory to add to sys.path directly

Falls back to whatever `vgi_rpc` is importable in the current interpreter (e.g. CI, where
`pip install "vgi-rpc[conformance]"` has already run into the active environment).
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

# The canonical reference is vgi-rpc-python, NOT the similarly named vgi-rpc sibling: that one
# predates the multiservice work (no routing key, flat routes, __describe__ still live) while
# carrying a numerically *higher* version, so it reads as the newer of the two and is not. A
# harness pointed at it fails every namespaced call in a way that looks like a worker bug.
#
# Ordered, not hardcoded: VGI_RPC_SITE and VGI_RPC_PYTHON override, then the canonical checkout,
# then the stale sibling as a last resort, then whatever is already importable. A machine-specific
# absolute path with no override is how the stale pin survived unnoticed in the first place.
_REFERENCE_VENVS = (
    Path.home() / "Development" / "vgi-rpc-python" / ".venv",
    Path.home() / "Development" / "vgi-rpc" / ".venv",
)


def _site_packages(venv: Path) -> Path | None:
    lib = venv / "lib"
    if not lib.is_dir():
        return None
    for entry in lib.iterdir():
        candidate = entry / "site-packages"
        if candidate.is_dir():
            return candidate
    return None


def _configure() -> None:
    if os.environ.get("VGI_RPC_SITE"):
        sys.path.insert(0, os.environ["VGI_RPC_SITE"])
        return

    try:
        import vgi_rpc  # noqa: F401

        return  # already importable (CI: pip install "vgi-rpc[conformance]")
    except ImportError:
        pass

    for venv in _REFERENCE_VENVS:
        site = _site_packages(venv)
        if site is not None:
            sys.path.insert(0, str(site))
            return


_configure()


def pytest_sessionstart(session: object) -> None:
    """Publish the C# conformance worker before any test runs (see publish_worker)."""
    if os.environ.get("PYTEST_XDIST_WORKER"):
        return  # the controller already published it
    from test_csharp_conformance import publish_client_driver, publish_worker

    publish_worker()
    publish_client_driver()


# ---------------------------------------------------------------------------------------------
# Expected skips. A skip whose reason is not listed here fails the run: a skip nobody listed is a
# case nobody ran, and "All tests passed" with a quiet new skip hides a coverage loss. Each entry
# names why this runner cannot run the case.
EXPECTED_SKIP_REASONS: dict[str, str] = {
    # STATE_REF_V1.md §5.4 / §15: single-replica in 3.4, so the cases that need stored tags,
    # cross-replica turns, the cursor rule, codec bytes or tag expiry are capability-gated.
    "single-replica SDK: HTTP stream state is never serialized, so StateRefs are in-memory and inline only "
    "(STATE_REF_V1.md §5.4)": "C# keeps HTTP stream state in server memory in 3.4 (decision 17)",
    # Pre-existing runner gaps (no worker variant for these fixtures yet).
    "runner provides no compression-disabled server": "no compression-disabled worker fixture",
    "runner provides no cache-disabled server": "no call-state cache to disable (state is in memory)",
    "server does not emit X-Request-ID": "X-Request-ID not implemented by this port",
    "runner does not expose an isolated conformance resource-soak target": "no resource-soak fixture",
    "runner provides no conformance_http_serve_start_fail_once_port": "no serve-start-fail-once fixture",
    "runner provides no conformance_transport_kind_probes": "no transport-kind probe fixture",
}

_unexpected_skips: list[str] = []


def _skip_reason(report: object) -> str:
    longrepr = getattr(report, "longrepr", None)
    reason = longrepr[2] if isinstance(longrepr, tuple) and len(longrepr) == 3 else str(longrepr)
    return reason.removeprefix("Skipped: ")


def pytest_runtest_logreport(report: object) -> None:
    """Record every skip whose reason is not in EXPECTED_SKIP_REASONS (runs on the xdist controller too)."""
    if getattr(report, "skipped", False) and not getattr(report, "wasxfail", None):
        reason = _skip_reason(report)
        if reason not in EXPECTED_SKIP_REASONS:
            _unexpected_skips.append(f"{getattr(report, 'nodeid', '?')}: {reason}")


def pytest_sessionfinish(session: object, exitstatus: int) -> None:
    """Fail the run on an unlisted skip."""
    if os.environ.get("PYTEST_XDIST_WORKER") or not _unexpected_skips:
        return
    sys.stderr.write("\nUnexpected skips (not in conftest.EXPECTED_SKIP_REASONS):\n")
    for line in _unexpected_skips:
        sys.stderr.write(f"  {line}\n")
    session.exitstatus = 1  # type: ignore[attr-defined]
