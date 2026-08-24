#!/usr/bin/env bash
# Re-runs one conformance test (or a glob) in isolation, verbosely.
# Usage: ./inspect.sh scalar_echo.echo_string
#        ./inspect.sh 'complex_types.*'
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PATTERN="${1:?usage: ./inspect.sh <category.name-or-glob>, e.g. scalar_echo.echo_string}"
SIBLING_VENV_PYTHON="$HOME/Development/vgi-rpc/.venv/bin/python"

if [ -n "${VGI_RPC_PYTHON:-}" ]; then
  PYTHON="$VGI_RPC_PYTHON"
elif [ -x "$SIBLING_VENV_PYTHON" ]; then
  PYTHON="$SIBLING_VENV_PYTHON"
else
  PYTHON="python3"
fi

WORKER_OUTPUT="$REPO_ROOT/artifacts/conformance-worker"
dotnet publish "$REPO_ROOT/conformance/QueryFarm.VgiRpc.ConformanceWorker" -c Release -o "$WORKER_OUTPUT"

"$PYTHON" -c "from vgi_rpc.conformance._test_cli import main; main()" \
  --cmd "$WORKER_OUTPUT/QueryFarm.VgiRpc.ConformanceWorker" \
  --filter "$PATTERN" \
  --verbose
