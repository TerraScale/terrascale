#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Stable per-worktree port; binding all interfaces also admits the Tailscale network.
dashboard_dev_port=$(python3 -c 'import hashlib,os; print(5500 + int(hashlib.sha256(os.getcwd().encode()).hexdigest()[:8],16) % 1000)')
node src/TerraScale.Dashboard/web/build/build-css.mjs --watch --minify &
dashboard_css_pid=$!
node src/TerraScale.Dashboard/web/build/build-js.mjs --watch --minify &
dashboard_js_pid=$!
# Own the watch process group, including CLI/compiler/app children, so stopping
# the launcher also stops the server rather than leaving an idle watcher behind.
setsid dotnet watch --project src/TerraScale.Dashboard/TerraScale.Dashboard.csproj --non-interactive run --no-launch-profile -- --environment Development --urls "http://0.0.0.0:$dashboard_dev_port" &
dashboard_host_pid=$!
cleanup() {
  trap - EXIT INT TERM
  kill -TERM -- "-$dashboard_host_pid" "$dashboard_css_pid" "$dashboard_js_pid" 2>/dev/null || true
  sleep 0.5
  kill -KILL -- "-$dashboard_host_pid" 2>/dev/null || true
  wait "$dashboard_host_pid" "$dashboard_css_pid" "$dashboard_js_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
wait "$dashboard_host_pid"
