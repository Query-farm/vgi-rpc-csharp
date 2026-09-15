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
