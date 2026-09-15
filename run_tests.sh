#!/usr/bin/env bash
# Runs the cross-language conformance suite (see test_csharp_conformance.py, CLAUDE.md).
#
# Usage:
#   ./run_tests.sh                  # run everything
#   ./run_tests.sh -k echo_string    # keyword-filter, forwarded to pytest -k
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The reference this port tracks is vgi-rpc-python. The similarly named vgi-rpc sibling is an
# older tree with none of the multiservice work — no routing key, flat routes, __describe__ still
# live — and a numerically *higher* version that makes it look newer. Running against it fails
# every namespaced call in a way that reads as a worker bug, so it is a last resort, not a peer.
# Override with VGI_RPC_PYTHON rather than editing this list.
if [ -n "${VGI_RPC_PYTHON:-}" ]; then
  PYTHON="$VGI_RPC_PYTHON"
else
  PYTHON="python3"
  for candidate in "$HOME/Development/vgi-rpc-python/.venv/bin/python" \
                   "$HOME/Development/vgi-rpc/.venv/bin/python"; do
    if [ -x "$candidate" ]; then PYTHON="$candidate"; break; fi
  done
fi

"$PYTHON" -m pytest "$REPO_ROOT/test_csharp_conformance.py" -v "$@"
