#!/usr/bin/env bash
# Runs the E2E suite against ts-terrabase-e2e. Usage: run.sh <log> [tunit args...]
# The key comes only from the inherited environment (with-key.sh); never a file, argv, log or output.
set -euo pipefail; set +x
here=$(cd "$(dirname "$0")" && pwd); log=$1; shift
source "$here/tenant.env"
: "${TERRABASE_E2E_FLY_API_KEY:?run under with-key.sh: the key comes only from the inherited environment}"
export TERRABASE_E2E_FLY_URL=https://ts-terrabase-e2e.fly.dev TERRABASE_E2E_FLY_APP=ts-terrabase-e2e
export TERRABASE_E2E_FLY_PROJECT=$project TERRABASE_E2E_FLY_DATABASE=e2e TERRABASE_E2E_FLY_REVISION=${REVISION:-9667b7efa18ee99ef4614bfdad68aac78932dda3}
cd "$here/../e2e-live"
echo "run $(date -u +%FT%TZ) tests@$(git rev-parse HEAD) dirty=$(git status --porcelain | wc -l) args: $*" > "$here/$log"
status=0
nice -n 19 env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL dotnet run --project tests/TerraBase.E2E --configuration Release --no-build "$@" >> "$here/$log" 2>&1 || status=$?
echo "exit $status $(date -u +%FT%TZ)" >> "$here/$log"
exit $status
