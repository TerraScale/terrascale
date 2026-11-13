export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH"
set -euo pipefail
export DOTNET_ROOT=$HOME/.local/share/mise/dotnet-root DOTNET_PROCESSOR_COUNT=4 E2E_REGIONS=2
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
export E2E_STATE="${E2E_STATE:-/tmp/922cbdaa-C7-retention/state}" E2E_BUILD="${E2E_BUILD:-/tmp/922cbdaa-C7-retention/build}"
mkdir -p "$E2E_STATE"
command -v cc > /dev/null
command -v nats-server > /dev/null
export E2E_RETENTION_NODE=$(node -p process.execPath)
test ! -e "$E2E_STATE/namespace.pid"
free -g
test "$(free -g | awk '/^Mem:/ {print $7}')" -ge 6
rm -rf "$E2E_STATE/retained-handoff"
printf '0\n' > "$E2E_STATE/retained-handoff-clock"
printf '0\n' > "$E2E_STATE/retained-handoff-clock-1"
printf '0\n' > "$E2E_STATE/retained-handoff-clock-2"
export E2E_RETENTION_CLOCK_FILE="$E2E_STATE/retained-handoff-clock"
export E2E_RETENTION_CLOCK="$E2E_STATE/retention-tools/clock.so"
mkdir -p "$E2E_STATE/retention-tools"
nice -n 19 cc -shared -fPIC -Wall -Wextra -Werror -O2 e2e/retention/clock.c -o "$E2E_RETENTION_CLOCK"
cat > "$E2E_STATE/retention-tools/node" <<'NODE'
#!/usr/bin/env bash
case "$1" in */r2.mjs) export LD_PRELOAD="$E2E_RETENTION_CLOCK" ;; esac
exec "$E2E_RETENTION_NODE" "$@"
NODE
chmod 700 "$E2E_STATE/retention-tools/node"
export PATH="$E2E_STATE/retention-tools:$PATH"
bash e2e/env/pki.sh
trap 'cd "$root"; bash e2e/env/local.sh stop' EXIT
bash e2e/env/local.sh start
bash e2e/env/local.sh exec bash e2e/env/up.sh
cd e2e
export E2E_PLACEMENT_SELECTED=1
bash env/local.sh exec npx playwright test --project placement-setup --no-deps --output "$E2E_STATE/test-results"
bash env/local.sh exec env LD_PRELOAD="$E2E_RETENTION_CLOCK" node retention/handoff.mjs
