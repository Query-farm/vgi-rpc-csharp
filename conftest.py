"""Locates the vgi-rpc[conformance] Python package for test_csharp_conformance.py.

Mirrors the pattern vgi-rpc-java's tests/conftest.py uses: prefer a local checkout's venv (the
canonical Python repo may carry unreleased protocol features ahead of what's on PyPI), with two
escape hatches:

  VGI_RPC_PYTHON  — path to a Python interpreter that already has vgi_rpc[conformance] installed
  VGI_RPC_SITE    — a site-packages directory to add to sys.path directly

Falls back to whatever `vgi_rpc` is importable in the current interpreter (e.g. CI, where
`pip install "vgi-rpc[conformance]"` has already run into the active environment).
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

_SIBLING_VENV = Path.home() / "Development" / "vgi-rpc" / ".venv"


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

    site = _site_packages(_SIBLING_VENV)
    if site is not None:
        sys.path.insert(0, str(site))


_configure()
