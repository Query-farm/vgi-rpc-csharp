#!/usr/bin/env bash
# Runs the cross-language conformance suite (see test_csharp_conformance.py, CLAUDE.md).
#
# Usage:
#   ./run_tests.sh                  # run everything
#   ./run_tests.sh -k echo_string    # keyword-filter, forwarded to pytest -k
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIBLING_VENV_PYTHON="$HOME/Development/vgi-rpc/.venv/bin/python"

if [ -n "${VGI_RPC_PYTHON:-}" ]; then
  PYTHON="$VGI_RPC_PYTHON"
elif [ -x "$SIBLING_VENV_PYTHON" ]; then
  # Prefer the sibling canonical repo's venv locally — it may carry unreleased protocol
  # features ahead of what's on PyPI. CI won't have this; falls through to python3 there.
  PYTHON="$SIBLING_VENV_PYTHON"
else
  PYTHON="python3"
fi

"$PYTHON" -m pytest "$REPO_ROOT/test_csharp_conformance.py" -v "$@"
