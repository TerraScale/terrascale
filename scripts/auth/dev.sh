#!/usr/bin/env bash
# Development server for this worktree: `dotnet watch` (hot reload and browser
# refresh) on a free loopback port, published over HTTPS on the tailnet with
# `tailscale serve` on its own free HTTPS port. Settings come from .env when it
# exists (gitignored), otherwise from throwaway Development values. The script
# only ever adds and removes its own serve mapping.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

free_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

# HTTPS ports already published by `tailscale serve`, from any worktree.
served_ports() {
  tailscale serve status --json | python3 -c '
import json, sys
status = json.load(sys.stdin) or {}
ports = set((status.get("TCP") or {}).keys())
ports |= {host.rsplit(":", 1)[1] for host in (status.get("Web") or {}) if ":" in host}
print("\n".join(sorted(ports)))'
}

app_port=${TS_AUTH_DEV_PORT:-$(free_port)}

# Choosing a port must not interleave with another worktree doing the same.
# Under one host-wide lock this run reserves its port (a file naming its PID);
# the lock is released before the slow build and startup, and other runs skip
# ports reserved by a live process. Lock and reservations live in a directory
# only this user can write, never at a predictable shared path.
lock_dir="${XDG_RUNTIME_DIR:-$HOME/.cache}/ts-auth"
mkdir -p -m 700 "$lock_dir"
[[ -d $lock_dir && -O $lock_dir && ! -L $lock_dir ]] || { echo "$lock_dir must be a directory owned by you" >&2; exit 1; }
# mkdir -m only applies to a new directory; enforce the mode on an existing one too.
chmod 700 "$lock_dir"
exec {lock_fd}>>"$lock_dir/dev-serve.lock"
flock -w 30 "$lock_fd" || { echo "timed out waiting for the dev-serve lock" >&2; exit 1; }

reservations="$lock_dir/reservations"
mkdir -p -m 700 "$reservations"
for file in "$reservations"/*; do
  [[ -f $file ]] || continue
  kill -0 "$(cat "$file")" 2>/dev/null || rm -f "$file"
done
# Fail closed: if the current Serve configuration cannot be read, nothing is free.
served=$(served_ports) || { echo "could not read tailscale serve status" >&2; exit 1; }
occupied=$(printf '%s\n' "$served"; ls "$reservations")
https_port=
if [[ -n ${TS_AUTH_DEV_HTTPS_PORT:-} ]]; then
  # A plain decimal port only: the value is also a file name under reservations/.
  [[ $TS_AUTH_DEV_HTTPS_PORT =~ ^[0-9]{1,5}$ ]] && (( 10#$TS_AUTH_DEV_HTTPS_PORT >= 1 && 10#$TS_AUTH_DEV_HTTPS_PORT <= 65535 )) ||
    { echo "TS_AUTH_DEV_HTTPS_PORT must be a port number between 1 and 65535" >&2; exit 1; }
  TS_AUTH_DEV_HTTPS_PORT=$((10#$TS_AUTH_DEV_HTTPS_PORT))
  if grep -Fxq -- "$TS_AUTH_DEV_HTTPS_PORT" <<<"$occupied"; then
    echo "tailnet HTTPS port $TS_AUTH_DEV_HTTPS_PORT is already served by another process; pick another" >&2
    exit 1
  fi
  https_port=$TS_AUTH_DEV_HTTPS_PORT
else
  for _ in $(seq 1 50); do
    candidate=$(shuf -i 10000-19999 -n 1)
    if ! grep -Fxq -- "$candidate" <<<"$occupied"; then
      https_port=$candidate
      break
    fi
  done
  [[ -n $https_port ]] || { echo "no free tailnet HTTPS port found" >&2; exit 1; }
fi
reservation="$reservations/$https_port"
echo $$ >"$reservation"
flock -u "$lock_fd"

dns_name=$(tailscale status --json | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')
# 443 is the default HTTPS port, which the app's normalised public origin omits.
if [[ $https_port == 443 ]]; then origin="https://${dns_name}"; else origin="https://${dns_name}:${https_port}"; fi
target="http://127.0.0.1:${app_port}"

export TS_AUTH_ENVIRONMENT=${TS_AUTH_ENVIRONMENT:-Development}
export TS_AUTH_LISTEN="$target"
export TS_AUTH_PUBLIC_ORIGIN="$origin"
export TS_AUTH_TERRASCALE_API_URL=${TS_AUTH_TERRASCALE_API_URL:-http://127.0.0.1:8420}
export TS_AUTH_TERRASCALE_API_KEY=${TS_AUTH_TERRASCALE_API_KEY:-dev-only}
export TS_AUTH_DATABASE=${TS_AUTH_DATABASE:-ts-auth-dev-$(basename "$repo")}
export TS_AUTH_MASTER_KEY=${TS_AUTH_MASTER_KEY:-$(head -c 32 /dev/urandom | base64)}
export TS_AUTH_OPERATOR_KEY=${TS_AUTH_OPERATOR_KEY:-$(head -c 24 /dev/urandom | base64 | tr -d '/+=')}

watch_pid=
published=
cleanup() {
  trap - EXIT
  if [[ -n $watch_pid ]]; then
    # dotnet watch ignores SIGTERM and stops cleanly on Ctrl+C, so interrupt its
    # whole process group (watch, the build host and the app), then escalate.
    kill -INT -- "-$watch_pid" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 -- "-$watch_pid" 2>/dev/null || break
      sleep 0.5
    done
    kill -TERM -- "-$watch_pid" 2>/dev/null || true
    sleep 1
    kill -KILL -- "-$watch_pid" 2>/dev/null || true
    wait "$watch_pid" 2>/dev/null || true
  fi
  # `serve --https=<port> off` drops the whole port, so remove it only while this
  # run's proxy is the port's one and only handler.
  if [[ -n $published ]] && tailscale serve status --json | python3 -c '
import json, sys
port, target = sys.argv[1], sys.argv[2]
status = json.load(sys.stdin) or {}
handlers = [h for host, web in (status.get("Web") or {}).items() if host.endswith(":" + port)
            for h in (web.get("Handlers") or {}).values()]
sys.exit(0 if handlers and all(h.get("Proxy") == target for h in handlers) else 1)' "$https_port" "$target"; then
    tailscale serve --https="$https_port" off >/dev/null 2>&1 || true
  elif [[ -n $published ]]; then
    echo "left tailnet port $https_port in place: it now carries other handlers" >&2
  fi
  rm -f "$reservation"
}
# Cleanup always runs on exit; a signal ends the script instead of resuming it.
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Run watch as a background child so TERM/INT reach the traps immediately. Job
# control gives it its own process group and keeps SIGINT enabled for it; a
# non-interactive shell otherwise starts background jobs with SIGINT ignored.
set -m
dotnet watch --project src/TerraScale.Auth --non-interactive run &
watch_pid=$!
set +m

# Publish only once this run's own server answers on the port: its OpenAPI
# document names this run's unique origin, so an unrelated listener that
# already held the port is never exposed to the tailnet.
for _ in $(seq 1 300); do
  kill -0 "$watch_pid" 2>/dev/null || { echo "dotnet watch exited before the server started" >&2; exit 1; }
  served=$(curl -fsS --connect-timeout 1 --max-time 3 "$target/openapi/v1.json" 2>/dev/null |
    python3 -c 'import json,sys; print(json.load(sys.stdin)["servers"][0]["url"])' 2>/dev/null || true)
  [[ $served == "$origin" ]] && break
  sleep 1
done
[[ $served == "$origin" ]] || { echo "the dev server did not start on $target" >&2; exit 1; }

# Re-read Serve right before publishing: another user or a manual `tailscale serve`
# may have taken the port during the build. Never replace someone else's handler.
# tailscale offers no conditional update, so a writer outside these scripts can
# still race this check by milliseconds; launches through dev.sh are coordinated
# by the reservation directory.
current=$(served_ports) || { echo "could not read tailscale serve status" >&2; exit 1; }
if grep -Fxq -- "$https_port" <<<"$current"; then
  echo "tailnet HTTPS port $https_port was taken while the server started; run again" >&2
  exit 1
fi

# Mark first: a signal during publication must still let cleanup check and remove it.
published=1
tailscale serve --bg --https="$https_port" "$target" >/dev/null
echo "ts-auth dev server: $origin (local $target)"
wait "$watch_pid"
