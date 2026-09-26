#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

port="${PORT:-5481}"
export TS_DASHBOARD_PRODUCTION_DEMO=1
export DASHBOARD_ADDR="${DASHBOARD_ADDR:-0.0.0.0:${port}}"
export DASHBOARD_DEV=1
export DASHBOARD_DEMO_ADMIN_EMAIL="${DASHBOARD_DEMO_ADMIN_EMAIL:-e2e-admin-seed@example.test}"
export DASHBOARD_DEMO_ADMIN_PASSWORD="${DASHBOARD_DEMO_ADMIN_PASSWORD:-E2e-admin-seed-123!}"
export GOFLAGS="${GOFLAGS:--mod=vendor}"

# The Go build embeds web/static/js, so it must exist before air starts.
npm run build:js
npm run watch:css &
css_pid=$!
npm run watch:js &
js_pid=$!
runner_pid=""
runner_pgid=""

cleanup() {
  if [[ -n "$runner_pgid" ]]; then
    kill -TERM -- "-$runner_pgid" 2>/dev/null || true
    wait "$runner_pid" 2>/dev/null || true
  fi
  kill "$css_pid" "$js_pid" 2>/dev/null || true
  wait "$css_pid" "$js_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

air_bin="$(command -v air || true)"
air_version=""
if [[ -n "$air_bin" ]]; then
  air_version="$("$air_bin" -v 2>&1 || true)"
fi
if [[ "$air_version" != *v1.67.4* ]]; then
  GOFLAGS=-mod=mod go install github.com/air-verse/air@v1.67.4
  gobin="$(go env GOBIN)"
  if [[ -z "$gobin" ]]; then
    gobin="$(go env GOPATH)/bin"
  fi
  air_bin="$gobin/air"
fi
setsid "$air_bin" -c .air.toml &
runner_pid=$!
runner_pgid=$runner_pid
wait "$runner_pid"
