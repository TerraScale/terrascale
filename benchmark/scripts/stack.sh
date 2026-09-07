#!/usr/bin/env bash
# E-15 local three-member harness (development only, never shipped).
#
# Boots GRU / SFO / FRA terrabase-server members as local processes
# (no containers), plus every G1 edge piece around them. All pieces are
# mandatory for the normal `up` target: a missing checkout, tool, process, or
# health signal is an error. The X-4 stub is available only through the
# separately named `pre-engine` target and is never a substitute for a member.
#
# Usage:
#   ./scripts/stack.sh up      # boot everything, wait, verify  (default)
#   ./scripts/stack.sh check   # probe running members, exit non-zero on failure
#   ./scripts/stack.sh test    # run the real P-3 journey and X-6 qualification
#   ./scripts/stack.sh pre-engine # members + X-4 stub before E-5/E-6 land
#   ./scripts/stack.sh down    # stop everything started by `up`
#   ./scripts/stack.sh logs [-f] [gru|sfo|fra|proxy|dashboard|webhooks|stub]
#
# Knobs:
#   STACK_FRESH=1          wipe .terrascale/stack (data, certs, configs) before up
#   STACK_DRY_RUN=1        print the plan without creating files or processes
#   STACK_NO_BUILD=1       never invoke cargo (fails if the binary is missing)
#
# Ports (fixed, documented in docs/development/local-stack.md):
#   GRU  api 7101  tbep 8421  obs 9461
#   SFO  api 7102  tbep 8422  obs 9462
#   FRA  api 7103  tbep 8423  obs 9463
#   proxy 7000, stub origin 7080, webhooks 7090, dashboard 7100.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STACK_DIR="$ROOT/.terrascale/stack"
LOG_DIR="$STACK_DIR/logs"
TLS_DIR="$STACK_DIR/tls"
PIDS_FILE="$STACK_DIR/stack.pids"
BOOTSTRAP_KEY_FILE="$STACK_DIR/bootstrap.key"
WEBHOOKS_LOG="$STACK_DIR/webhooks.log"
BIN="$ROOT/target/debug/terrabase-server"

PROXY_DIR="$ROOT/../ts-proxy"
DASHBOARD_DIR="$ROOT/../ts-dashboard"
TYPESCRIPT_DIR="$ROOT/../ts-typescript"
EDGE_RUN="$PROXY_DIR/harness/local-edge/run.ts"
JOURNEY_FILE="$TYPESCRIPT_DIR/test/journey.test.ts"

PROXY_PORT=7000
STUB_PORT=7080
WEBHOOK_PORT=7090
DASHBOARD_PORT=7100

REGIONS="GRU SFO FRA"

api_port() {
  case "$1" in
    GRU) echo 7101 ;;
    SFO) echo 7102 ;;
    FRA) echo 7103 ;;
  esac
}
tbep_port() {
  case "$1" in
    GRU) echo 8421 ;;
    SFO) echo 8422 ;;
    FRA) echo 8423 ;;
  esac
}
obs_port() {
  case "$1" in
    GRU) echo 9461 ;;
    SFO) echo 9462 ;;
    FRA) echo 9463 ;;
  esac
}
lower() {
  case "$1" in
    GRU) echo gru ;;
    SFO) echo sfo ;;
    FRA) echo fra ;;
  esac
}

log() { printf '%s\n' "stack: $*"; }
warn() { printf '%s\n' "stack: WARN: $*" >&2; }
die() {
  printf '%s\n' "stack: ERROR: $*" >&2
  exit 1
}

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "missing required command '$1' ($2)"
}

dry_run() { [ "${STACK_DRY_RUN:-0}" = "1" ]; }

run_or_print() {
  if dry_run; then
    printf 'stack: DRY-RUN:'
    printf ' %q' "$@"
    printf '\n'
    return 0
  fi
  "$@"
}

# --- process bookkeeping -------------------------------------------------

record_pid() {
  printf '%s %s\n' "$1" "$2" >>"$PIDS_FILE"
}

is_alive() { kill -0 "$1" 2>/dev/null; }

stop_all() {
  if [ ! -f "$PIDS_FILE" ]; then
    return 0
  fi
  while read -r name pid; do
    [ -n "${pid:-}" ] || continue
    if is_alive "$pid"; then
      log "stopping $name (pid $pid)"
      kill "$pid" 2>/dev/null || true
      # Every process is its own session (setsid), so terminate descendants
      # such as wrangler/vinext as part of the same deterministic teardown.
      kill -- "-$pid" 2>/dev/null || true
    fi
  done <"$PIDS_FILE"
  sleep 1
  while read -r name pid; do
    [ -n "${pid:-}" ] || continue
    if is_alive "$pid"; then
      warn "$name (pid $pid) ignored SIGTERM, sending SIGKILL"
      kill -9 "$pid" 2>/dev/null || true
      kill -KILL -- "-$pid" 2>/dev/null || true
    fi
  done <"$PIDS_FILE"
  rm -f "$PIDS_FILE"
}

stop_on_failure() {
  status=$?
  if [ "$status" -ne 0 ]; then
    warn "startup failed; tearing down processes started by this stack"
    stop_all || true
  fi
  return "$status"
}

# --- material -------------------------------------------------------------

# NOTE (E-15): the member's PEM gate (`valid_pem` in
# crates/terrabase-server/src/runtime.rs) rejects any base64 body line that
# mixes padding (`=`) with other content — only DER lengths that are a
# multiple of 3 (padding-free base64) are admitted. This is engine logic, so
# the stack does not touch it; instead it mints throwaway dev certificates
# until all three files are padding-free. Engine owners: relaxing the gate
# to accept padded PEM lets this loop go away.
tls_has_padding() {
  grep -q '=' "$TLS_DIR/server.crt" "$TLS_DIR/server.key" "$TLS_DIR/ca.crt" 2>/dev/null
}

gen_tls_once() {
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 \
    -keyout "$TLS_DIR/ca.key" -out "$TLS_DIR/ca.crt" \
    -days 3 -nodes -subj "/CN=terrabase-stack-ca" 2>/dev/null
  openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 \
    -keyout "$TLS_DIR/server.key" -out "$TLS_DIR/server.csr" \
    -nodes -subj "/CN=127.0.0.1" 2>/dev/null
  printf 'subjectAltName=IP:127.0.0.1,DNS:localhost\n' >"$TLS_DIR/san.ext"
  openssl x509 -req -in "$TLS_DIR/server.csr" \
    -CA "$TLS_DIR/ca.crt" -CAkey "$TLS_DIR/ca.key" -CAcreateserial \
    -out "$TLS_DIR/server.crt" -days 3 -extfile "$TLS_DIR/san.ext" 2>/dev/null
  rm -f "$TLS_DIR/server.csr"
  chmod 600 "$TLS_DIR/server.key" "$TLS_DIR/ca.key"
  chmod 644 "$TLS_DIR/server.crt" "$TLS_DIR/ca.crt"
}

ensure_tls() {
  if dry_run; then
    log "DRY-RUN: would mint throwaway TLS material in $TLS_DIR"
    return 0
  fi
  if [ -f "$TLS_DIR/server.crt" ] && [ -f "$TLS_DIR/server.key" ] && [ -f "$TLS_DIR/ca.crt" ] \
    && ! tls_has_padding; then
    return 0
  fi
  need_cmd openssl "install openssl to mint the stack's throwaway dev certificates"
  log "minting throwaway dev TLS material in $TLS_DIR (never production, never shipped)"
  mkdir -p "$TLS_DIR"
  umask 077
  local attempt=0
  while [ "$attempt" -lt 100 ]; do
    attempt=$((attempt + 1))
    gen_tls_once
    if ! tls_has_padding; then
      log "dev TLS material minted after $attempt attempt(s)"
      return 0
    fi
  done
  die "could not mint padding-free dev certificates in 100 attempts"
}

ensure_bootstrap_key() {
  if dry_run; then
    log "DRY-RUN: would create 0600 bootstrap key at $BOOTSTRAP_KEY_FILE"
    return 0
  fi
  if [ -f "$BOOTSTRAP_KEY_FILE" ]; then
    return 0
  fi
  umask 077
  if command -v openssl >/dev/null 2>&1; then
    printf 'tskey_stack_%s' "$(openssl rand -hex 24)" >"$BOOTSTRAP_KEY_FILE"
  else
    printf 'tskey_stack_dev_%d_%d' "$$" "$(date +%s)" >"$BOOTSTRAP_KEY_FILE"
  fi
  chmod 600 "$BOOTSTRAP_KEY_FILE"
  log "minted stack bootstrap API key in $BOOTSTRAP_KEY_FILE"
}

ensure_dashboard_secret() {
  if dry_run; then
    log "DRY-RUN: would create 0600 dashboard secret"
    return 0
  fi
  # Dev-only Better Auth session secret for the local dashboard. Request-time
  # guard only; generated per stack, never committed (gitignored with the
  # rest of .terrascale/stack).
  if [ -f "$STACK_DIR/dashboard-auth-secret" ]; then
    return 0
  fi
  umask 077
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32 >"$STACK_DIR/dashboard-auth-secret"
  else
    printf 'stack-dev-secret-%d-%d-padding-to-32-chars' "$$" "$(date +%s)" >"$STACK_DIR/dashboard-auth-secret"
  fi
  chmod 600 "$STACK_DIR/dashboard-auth-secret"
}

bootstrap_key() {
  tr -d ' \t\r\n' <"$BOOTSTRAP_KEY_FILE"
}

write_config() {
  # $1 = region (GRU|SFO|FRA)
  local region="$1" name tbep obs api
  name="$(lower "$region")"
  tbep="$(tbep_port "$region")"
  obs="$(obs_port "$region")"
  api="$(api_port "$region")"
  local member_dir="$STACK_DIR/$name"
  local config="$STACK_DIR/$name.toml"
  if dry_run; then
    log "DRY-RUN: would write $config and create $member_dir/{engine,data}"
    return 0
  fi
  mkdir -p "$member_dir/engine" "$member_dir/data"
  {
    printf '# E-15 local stack config for %s. Generated by scripts/stack.sh; do not hand-edit.\n' "$region"
    printf '# Development only: throwaway certs, bootstrap key, loopback listeners.\n\n'
    printf '[engine]\n'
    printf 'location = "%s/engine"\n' "$member_dir"
    printf 'tenant = "terrabase-local"\nproject = "local"\ndatabase = "local"\n'
    printf 'instance_id = 1\nregion = "%s"\n\n' "$region"
    printf '[tbep]\nlisten = "127.0.0.1:%s"\n' "$tbep"
    printf 'server_certificate = "%s/server.crt"\n' "$TLS_DIR"
    printf 'server_private_key = "%s/server.key"\n' "$TLS_DIR"
    printf 'client_ca = "%s/ca.crt"\n' "$TLS_DIR"
    printf 'member_principals = ["spiffe://local/stack-member"]\n'
    printf 'max_message_bytes = 16777216\nmax_inflight = 128\n'
    printf 'request_timeout_millis = 30000\ntls_handshake_timeout_millis = 5000\nshutdown_grace_millis = 30000\n\n'
    printf '[observability]\nlisten = "127.0.0.1:%s"\n\n' "$obs"
    printf '[public_api]\nlisten = "127.0.0.1:%s"\n' "$api"
    printf 'data_root = "%s/data"\n' "$member_dir"
    printf 'bootstrap_key_file = "%s"\n' "$BOOTSTRAP_KEY_FILE"
    printf 'regions = ["GRU", "SFO", "FRA"]\n\n'
    printf '[replication]\npeers = [\n'
    for peer in $REGIONS; do
      if [ "$peer" != "$region" ]; then
        printf '  { region = "%s", address = "127.0.0.1:%s" },\n' "$peer" "$(tbep_port "$peer")"
      fi
    done
    printf ']\n'
  } >"$config"
  chmod 600 "$config"
}

ensure_binary() {
  if dry_run; then
    log "DRY-RUN: would use $BIN (or nice -n 19 cargo build --locked -p terrabase-server)"
    return 0
  fi
  if [ -x "$BIN" ]; then
    return 0
  fi
  if [ "${STACK_NO_BUILD:-0}" = "1" ]; then
    die "terrabase-server binary missing at $BIN and STACK_NO_BUILD=1"
  fi
  need_cmd cargo "install the Rust toolchain to build the member binary"
  log "building terrabase-server (first boot only; later boots reuse $BIN)"
  (cd "$ROOT" && nice -n 19 cargo build --locked -p terrabase-server)
}

# --- members ---------------------------------------------------------------

start_member() {
  local region="$1" name api
  name="$(lower "$region")"
  api="$(api_port "$region")"
  log "starting $region member (public API 127.0.0.1:$api)"
  if dry_run; then
    run_or_print setsid "$BIN" --config "$STACK_DIR/$name.toml"
    return 0
  fi
  nohup setsid "$BIN" --config "$STACK_DIR/$name.toml" >>"$LOG_DIR/$name.log" 2>&1 &
  record_pid "member-$name" "$!"
}

wait_for_members() {
  local deadline=$((SECONDS + 90)) region ok
  while [ "$SECONDS" -lt "$deadline" ]; do
    ok=1
    for region in $REGIONS; do
      if ! curl -sf --max-time 2 "http://127.0.0.1:$(obs_port "$region")/live" >/dev/null 2>&1; then
        ok=0
        break
      fi
      if ! curl -sf --max-time 2 -H "Authorization: Bearer $(bootstrap_key)" \
        "http://127.0.0.1:$(api_port "$region")/api/v1/health" >/dev/null 2>&1; then
        ok=0
        break
      fi
      if ! curl -sf --max-time 2 "http://127.0.0.1:$(obs_port "$region")/ready" >/dev/null 2>&1; then
        ok=0
        break
      fi
    done
    if [ "$ok" = "1" ]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_for_members_basic() {
  local deadline=$((SECONDS + 90)) region ok
  while [ "$SECONDS" -lt "$deadline" ]; do
    ok=1
    for region in $REGIONS; do
      curl -sf --max-time 2 "http://127.0.0.1:$(obs_port "$region")/live" >/dev/null 2>&1 || ok=0
      curl -sf --max-time 2 -H "Authorization: Bearer $(bootstrap_key)" \
        "http://127.0.0.1:$(api_port "$region")/api/v1/health" >/dev/null 2>&1 || ok=0
    done
    [ "$ok" = "1" ] && return 0
    sleep 1
  done
  return 1
}

check_processes() {
  local failures=0 name pid
  [ -f "$PIDS_FILE" ] || { warn "no process state at $PIDS_FILE"; return 1; }
  while read -r name pid; do
    [ -n "${pid:-}" ] || continue
    if is_alive "$pid"; then
      log "$name process: ok (pid $pid)"
    else
      warn "$name process FAILED (pid $pid)"
      failures=$((failures + 1))
    fi
  done <"$PIDS_FILE"
  return "$failures"
}

check_member() {
  local region="$1" failures=0 body metrics
  if curl -sf --max-time 5 "http://127.0.0.1:$(obs_port "$region")/live" >/dev/null 2>&1; then
    log "$region operator /live: ok (127.0.0.1:$(obs_port "$region"))"
  else
    warn "$region operator /live FAILED (127.0.0.1:$(obs_port "$region")/live)"
    failures=$((failures + 1))
  fi
  for probe in ready startup; do
    if curl -sf --max-time 5 "http://127.0.0.1:$(obs_port "$region")/$probe" >/dev/null 2>&1; then
      log "$region operator /$probe: ok"
    else
      warn "$region operator /$probe FAILED"
      failures=$((failures + 1))
    fi
  done
  metrics="$(curl -sf --max-time 5 "http://127.0.0.1:$(obs_port "$region")/internal/metrics" 2>/dev/null || true)"
  for metric in \
    'terrabase_ready 1' \
    'terrabase_component_health{component="storage"} 1' \
    'terrabase_component_health{component="replication"} 1' \
    'terrabase_component_health{component="membership"} 1'; do
    if printf '%s\n' "$metrics" | grep -Fq "$metric"; then
      log "$region metrics: $metric"
    else
      warn "$region metrics missing readiness fact: $metric"
      failures=$((failures + 1))
    fi
  done
  body="$(curl -sf --max-time 5 -H "Authorization: Bearer $(bootstrap_key)" \
    "http://127.0.0.1:$(api_port "$region")/api/v1/health" 2>/dev/null || true)"
  case "$body" in
    *"\"region\":\"$region\""*'"healthy":true'*)
      log "$region public /api/v1/health: ok $body" ;;
    *)
      warn "$region public /api/v1/health FAILED: '${body:-no body}'"
      failures=$((failures + 1)) ;;
  esac
  body="$(curl -sf --max-time 5 -H "Authorization: Bearer $(bootstrap_key)" \
    "http://127.0.0.1:$(api_port "$region")/api/v1/regions" 2>/dev/null || true)"
  case "$body" in
    *"\"region\":\"$region\""*'"healthy":true'*) log "$region control/regions: ok" ;;
    *) warn "$region control/regions FAILED: '${body:-no body}'"; failures=$((failures + 1)) ;;
  esac
  body="$(curl -sf --max-time 5 -H "Authorization: Bearer $(bootstrap_key)" \
    "http://127.0.0.1:$(api_port "$region")/api/v1/databases" 2>/dev/null || true)"
  if [ -n "$body" ]; then
    log "$region control catalog: ok"
  else
    warn "$region control catalog FAILED"
    failures=$((failures + 1))
  fi
  body="$(curl -sf --max-time 5 -H "Authorization: Bearer $(bootstrap_key)" \
    "http://127.0.0.1:$(api_port "$region")/api/v1/databases/local/backups" 2>/dev/null || true)"
  case "$body" in
    *'"ready":true'*'"streaming":true'*) log "$region backup readiness: ok" ;;
    *) warn "$region backup readiness FAILED: '${body:-no body}'"; failures=$((failures + 1)) ;;
  esac
  return "$failures"
}

check_members() {
  # Every member must expose process, lifecycle, replication, control-plane,
  # and backup facts. Missing facts are failures, never optional warnings.
  local failures=0 region
  for region in $REGIONS; do
    check_member "$region" || failures=$((failures + $?))
  done
  if curl -sf --max-time 5 "http://127.0.0.1:$WEBHOOK_PORT/health" >/dev/null 2>&1; then
    log "webhook receiver /health: ok (127.0.0.1:$WEBHOOK_PORT)"
  else
    warn "webhook receiver /health FAILED (127.0.0.1:$WEBHOOK_PORT/health)"
    failures=$((failures + 1))
  fi
  check_processes || failures=$((failures + $?))
  return "$failures"
}

# --- edge pieces -------------------------------------------------------------

require_full_stack_inputs() {
  need_cmd node "install Node.js 22+ for the local Snippet and journey"
  need_cmd npm "install npm for the local Workers"
  need_cmd setsid "install util-linux for deterministic process groups"
  [ -d "$PROXY_DIR" ] || die "missing required ts-proxy checkout at $PROXY_DIR"
  [ -d "$PROXY_DIR/node_modules" ] || die "missing $PROXY_DIR/node_modules (run: cd $PROXY_DIR && npm ci)"
  [ -f "$PROXY_DIR/node_modules/.bin/wrangler" ] || die "missing ts-proxy Wrangler (run: cd $PROXY_DIR && npm ci)"
  [ -f "$EDGE_RUN" ] || die "missing required X-6 local edge harness at $EDGE_RUN"
  [ -d "$DASHBOARD_DIR" ] || die "missing required ts-dashboard checkout at $DASHBOARD_DIR"
  [ -d "$DASHBOARD_DIR/node_modules" ] || die "missing $DASHBOARD_DIR/node_modules (run: cd $DASHBOARD_DIR && npm ci)"
  [ -f "$DASHBOARD_DIR/node_modules/.bin/vinext" ] || die "missing dashboard Vinext (run: cd $DASHBOARD_DIR && npm ci)"
  [ -f "$TYPESCRIPT_DIR/package.json" ] || die "missing required ts-typescript checkout at $TYPESCRIPT_DIR"
  [ -d "$TYPESCRIPT_DIR/node_modules" ] || die "missing $TYPESCRIPT_DIR/node_modules (run: cd $TYPESCRIPT_DIR && npm ci)"
  [ -f "$JOURNEY_FILE" ] || die "missing required P-3 journey at $JOURNEY_FILE"
}

require_base_inputs() {
  need_cmd curl "install curl for stack readiness probes"
  need_cmd python3 "install python3 for the local webhook receiver"
  need_cmd openssl "install openssl to mint the stack's throwaway dev certificates"
  need_cmd setsid "install util-linux for deterministic process groups"
  need_cmd ss "install iproute2 so the stack never takes ports owned by another process"
}

ensure_stack_ports_free() {
  local port
  for port in 7000 7080 7090 7100 7101 7102 7103 8421 8422 8423 9461 9462 9463; do
    if ss -H -ltn "sport = :$port" | grep -q .; then
      die "port $port is already owned by another process; stop that stack explicitly before starting this one"
    fi
  done
}

start_proxy() {
  # REST Worker under `wrangler dev` (G1). DEV_ORIGINS in ts-proxy's
  # wrangler.toml points at this stack's three members.
  log "starting REST Worker under wrangler dev on 127.0.0.1:$PROXY_PORT"
  if dry_run; then
    run_or_print npm --prefix "$PROXY_DIR" run dev
    return 0
  fi
  nohup setsid npm --prefix "$PROXY_DIR" run dev >>"$LOG_DIR/proxy.log" 2>&1 &
  record_pid "proxy" "$!"
}

start_stub_origin() {
  # X-4 is explicitly pre-engine only. Full `up` never invokes this function.
  if ! dry_run; then
    need_cmd go "install Go for the separately named pre-engine stub target"
  fi
  [ -d "$PROXY_DIR/stub" ] || die "missing X-4 stub at $PROXY_DIR/stub"
  log "starting X-4 stub origin on 127.0.0.1:$STUB_PORT"
  if dry_run; then
    run_or_print go -C "$PROXY_DIR/stub" run . -addr "127.0.0.1:$STUB_PORT" -verify-cap
    return 0
  fi
  nohup setsid go -C "$PROXY_DIR/stub" run . -addr "127.0.0.1:$STUB_PORT" -verify-cap >>"$LOG_DIR/stub.log" 2>&1 &
  record_pid "stub" "$!"
}

start_dashboard() {
  # Dashboard on workerd via `vinext dev` (D-59: workerd in dev too).
  log "starting dashboard on workerd (127.0.0.1:$DASHBOARD_PORT)"
  # The dashboard requires its runtime base URLs even in dev: first-contact
  # API calls go to the local proxy, the public URL is the local origin.
  # NODE_ENV=development selects the dashboard's own local-dev mode (dev
  # shim, workspace repository, insecure-cookie + local-portal paths); the
  # Owning outcome: ts-dashboard local-dev behavior — dev-shim paths stay dev-only.
  # production guards (auth terms version, 32-char session secret) stay on
  # for real deploys via wrangler.jsonc and orchestrator secrets.
  #
  # The workerd runtime does NOT inherit this shell's environment: the
  # Cloudflare Vite plugin builds worker env from wrangler.jsonc vars plus
  # `.dev.vars`. The stack therefore writes a loopback-only `.dev.vars`
  # (standard wrangler local-dev mechanism; contains no secrets beyond the
  # throwaway dev session key). NOTE for the dashboard owner: `.dev.vars`
  # is not in ts-dashboard/.gitignore yet — adding it there keeps this
  # stack-managed file from showing as untracked noise.
  ensure_dashboard_secret
  if dry_run; then
    run_or_print npm --prefix "$DASHBOARD_DIR" run dev -- --port "$DASHBOARD_PORT"
    return 0
  fi
  {
    printf 'TERRASCALE_API_BASE_URL="http://127.0.0.1:%s"\n' "$PROXY_PORT"
    printf 'TERRASCALE_DASHBOARD_PUBLIC_URL="http://127.0.0.1:%s"\n' "$DASHBOARD_PORT"
    printf 'NODE_ENV="development"\n'
    printf 'TERRASCALE_AUTH_TERMS_VERSION="local-2026-08-30"\n'
    printf 'BETTER_AUTH_SECRET="%s"\n' "$(cat "$STACK_DIR/dashboard-auth-secret")"
  } >"$DASHBOARD_DIR/.dev.vars"
  nohup setsid env \
    NODE_ENV="development" \
    TERRASCALE_API_BASE_URL="http://127.0.0.1:$PROXY_PORT" \
    TERRASCALE_DASHBOARD_PUBLIC_URL="http://127.0.0.1:$DASHBOARD_PORT" \
    TERRASCALE_DASHBOARD_DEV_PORT="$DASHBOARD_PORT" \
    TERRASCALE_AUTH_TERMS_VERSION="local-2026-08-30" \
    BETTER_AUTH_SECRET="$(cat "$STACK_DIR/dashboard-auth-secret")" \
    npm --prefix "$DASHBOARD_DIR" run dev -- --port "$DASHBOARD_PORT" >>"$LOG_DIR/dashboard.log" 2>&1 &
  record_pid "dashboard" "$!"
}

start_webhooks() {
  log "starting webhook receiver on 127.0.0.1:$WEBHOOK_PORT"
  if dry_run; then
    run_or_print python3 "$ROOT/scripts/stack-webhook-receiver.py" --port "$WEBHOOK_PORT" --log "$WEBHOOKS_LOG"
    return 0
  fi
  nohup setsid python3 "$ROOT/scripts/stack-webhook-receiver.py" --port "$WEBHOOK_PORT" --log "$WEBHOOKS_LOG" \
    >>"$LOG_DIR/webhooks.log" 2>&1 &
  record_pid "webhooks" "$!"
}

run_x6() {
  local qualification="${1:-0}" endpoint="http://127.0.0.1:7101/api/v1/reactive/views"
  log "running X-6 local edge harness against real member GRU (qualification=$qualification)"
  if dry_run; then
    log "DRY-RUN: would set TERRABASE_VIEW_ENDPOINT=$endpoint LOCAL_EDGE_QUALIFICATION=$qualification"
    run_or_print node --experimental-strip-types "$EDGE_RUN"
    return 0
  fi
  # The edge harness intentionally strips customer credentials before its
  # origin fetch. This adapter supplies the stack bootstrap credential and
  # preserves the member-issued view capability, while still sending all
  # snapshot/log reads through the real local member (never the X-4 stub).
  local capability_key
  capability_key="$(printf '%s' 'terrascale-reactive-capability-v1' | od -An -tx1 | tr -d ' \n')"
  (cd "$PROXY_DIR" && \
    TERRABASE_VIEW_ENDPOINT="$endpoint" \
    LOCAL_EDGE_QUALIFICATION="$qualification" \
    LOCAL_EDGE_CAPABILITY_KEY="$capability_key" \
    STACK_BOOTSTRAP_KEY="$(bootstrap_key)" \
    node --experimental-strip-types --input-type=module -e '
      import { runLocalEdgeQualification } from "./harness/local-edge/local-edge.ts";
      let issuedCapability;
      const token = process.env.STACK_BOOTSTRAP_KEY;
      const fetchImpl = async (input, init) => {
        const incoming = new Request(input, init);
        const headers = new Headers(incoming.headers);
        headers.set("authorization", `Bearer ${token}`);
        if (incoming.method === "POST" && new URL(incoming.url).pathname === "/api/v1/reactive/views") {
          const request = JSON.parse(await incoming.text());
          const body = JSON.stringify({
            databaseId: "local",
            subscription: "local-edge",
            statement: request.query,
            parameters: Buffer.from(JSON.stringify({})).toString("base64"),
            checkpoint: null,
            entitlement_epoch: request.entitlement_epoch ?? 1,
          });
          const response = await fetch(new Request(incoming, { headers, body }));
          if (response.ok) issuedCapability = (await response.clone().json()).capability;
          return response;
        }
        if (issuedCapability !== undefined) headers.set("x-terrascale-view-capability", issuedCapability);
        return fetch(new Request(incoming, { headers }));
      };
      const report = await runLocalEdgeQualification({ fetchImpl });
      console.log(JSON.stringify(report, null, 2));
    ') >"$LOG_DIR/edge-qualify.log" 2>&1 || {
      tail -n 40 "$LOG_DIR/edge-qualify.log" >&2 || true
      die "X-6 local edge qualification failed against real member GRU"
    }
}

print_summary() {
  log "stack is up:"
  for region in $REGIONS; do
    log "  $region  api=http://127.0.0.1:$(api_port "$region")  tbep=127.0.0.1:$(tbep_port "$region")  obs=http://127.0.0.1:$(obs_port "$region")"
  done
  log "  proxy=http://127.0.0.1:$PROXY_PORT  webhooks=http://127.0.0.1:$WEBHOOK_PORT  dashboard=http://127.0.0.1:$DASHBOARD_PORT"
  log "  journey: export STACK_BOOTSTRAP_KEY=\"\$(cat $BOOTSTRAP_KEY_FILE)\"; curl -H \"Authorization: Bearer \$STACK_BOOTSTRAP_KEY\" http://127.0.0.1:7101/api/v1/health"
  log "  logs: $LOG_DIR  |  stop: ./scripts/stack.sh down"
}

wait_for_http() {
  local name="$1" url="$2" deadline=$((SECONDS + 90))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if curl -sfL --max-time 2 "$url" >/dev/null 2>&1; then
      log "$name: ready ($url)"
      return 0
    fi
    sleep 1
  done
  die "$name did not become ready: $url"
}

check_edge_components() {
  local failures=0 body
  body="$(curl -sfL --max-time 5 "http://127.0.0.1:$PROXY_PORT/api/v1/regions" 2>/dev/null || true)"
  case "$body" in
    *'"hosts"'*'127.0.0.1:7101'*'127.0.0.1:7102'*'127.0.0.1:7103'*)
      log "proxy Worker dev mode: ok ($body)" ;;
    *)
      warn "proxy Worker dev mode FAILED: '${body:-no body}'"
      failures=$((failures + 1)) ;;
  esac
  body="$(curl -sfL --max-time 5 "http://127.0.0.1:$DASHBOARD_PORT/" 2>/dev/null || true)"
  if [ -n "$body" ]; then
    log "dashboard workerd: ok (127.0.0.1:$DASHBOARD_PORT)"
  else
    warn "dashboard workerd FAILED (127.0.0.1:$DASHBOARD_PORT)"
    failures=$((failures + 1))
  fi
  return "$failures"
}

check_full_stack() {
  local failures=0
  check_members || failures=$((failures + $?))
  check_edge_components || failures=$((failures + $?))
  return "$failures"
}

dry_run_plan() {
  log "DRY-RUN: full E-15 stack requires 3 real members, proxy Worker dev, local Snippet/X-6 harness, dashboard workerd, and webhook receiver"
  require_base_inputs || true
  for region in $REGIONS; do write_config "$region"; done
  ensure_binary
  for region in $REGIONS; do start_member "$region"; done
  start_proxy
  start_dashboard
  start_webhooks
  log "DRY-RUN: readiness probes include /live /startup /ready /internal/metrics, public health/regions/catalog/backups, proxy, dashboard, and webhook"
  log "DRY-RUN: use 'make stack-test' for P-3 and X-6 against real GRU member"
}

# --- subcommands -------------------------------------------------------------

cmd_up() {
  if dry_run; then
    dry_run_plan
    return 0
  fi
  require_base_inputs
  require_full_stack_inputs
  trap stop_on_failure EXIT
  if [ "${STACK_FRESH:-0}" = "1" ]; then
    log "STACK_FRESH=1: wiping $STACK_DIR"
    stop_all || true
    rm -rf "$STACK_DIR"
  else
    stop_all || true
  fi
  ensure_stack_ports_free
  mkdir -p "$STACK_DIR" "$LOG_DIR"
  : >"$PIDS_FILE"
  ensure_tls
  ensure_bootstrap_key
  for region in $REGIONS; do
    write_config "$region"
  done
  ensure_binary
  for region in $REGIONS; do
    start_member "$region"
  done
  start_webhooks
  log "waiting for three members to answer /live + /api/v1/health ..."
  if ! wait_for_members; then
    warn "members did not become ready in 90 s; recent log tails follow"
    for region in $REGIONS; do
      warn "--- $(lower "$region").log (tail) ---"
      tail -n 20 "$LOG_DIR/$(lower "$region").log" >&2 || true
    done
    die "stack up failed: members unhealthy"
  fi
  start_proxy
  start_dashboard
  wait_for_http "proxy Worker" "http://127.0.0.1:$PROXY_PORT/api/v1/regions"
  wait_for_http "dashboard workerd" "http://127.0.0.1:$DASHBOARD_PORT/"
  if ! check_full_stack; then
    die "stack up failed: final verification failed"
  fi
  run_x6 0
  trap - EXIT
  print_summary
}

cmd_check() {
  if dry_run; then
    log "DRY-RUN: would check all member, replication/control/backup, proxy, dashboard, and webhook readiness"
    return 0
  fi
  [ -f "$BOOTSTRAP_KEY_FILE" ] || die "no $BOOTSTRAP_KEY_FILE; is the stack up? (./scripts/stack.sh up)"
  require_base_inputs
  require_full_stack_inputs
  if ! check_full_stack; then
    die "stack check failed"
  fi
  run_x6 0
  log "stack check passed: 3/3 real members + proxy + Snippet edge + dashboard + webhook healthy"
}

cmd_test() {
  if dry_run; then
    log "DRY-RUN: would run P-3 journey against $TYPESCRIPT_DIR and X-6 qualification against http://127.0.0.1:7101"
    return 0
  fi
  [ -f "$BOOTSTRAP_KEY_FILE" ] || die "no $BOOTSTRAP_KEY_FILE; is the stack up? (./scripts/stack.sh up)"
  require_base_inputs
  require_full_stack_inputs
  check_full_stack || die "stack test preflight failed"
  log "running P-3 journey against real GRU member"
  (cd "$TYPESCRIPT_DIR" && \
    TERRASCALE_API_BASE_URL="http://127.0.0.1:7101" \
    TERRASCALE_E2E_ACCESS_TOKEN="$(bootstrap_key)" \
    TERRASCALE_G1_STRICT=1 \
    TERRASCALE_E2E_RUN_ID="stack" \
    TERRASCALE_DASHBOARD_BASE_URL="http://127.0.0.1:$DASHBOARD_PORT" \
    TERRASCALE_E2E_REGION_BASE_URLS="GRU=http://127.0.0.1:7101,SFO=http://127.0.0.1:7102,FRA=http://127.0.0.1:7103" \
    npm test -- --run test/journey.test.ts) 2>&1 | tee "$LOG_DIR/p3-journey.log"
  run_x6 1
  log "stack test passed: P-3 journey + X-6 10,000-subscriber qualification"
}

cmd_pre_engine() {
  if dry_run; then
    log "DRY-RUN: would start the pre-engine member/webhook harness and the explicitly named X-4 stub"
    for region in $REGIONS; do write_config "$region"; done
    ensure_binary
    for region in $REGIONS; do start_member "$region"; done
    start_stub_origin
    start_webhooks
    return 0
  fi
  require_base_inputs
  need_cmd go "install Go for the separately named pre-engine stub target"
  [ -d "$PROXY_DIR/stub" ] || die "missing X-4 stub at $PROXY_DIR/stub"
  trap stop_on_failure EXIT
  stop_all || true
  ensure_stack_ports_free
  mkdir -p "$STACK_DIR" "$LOG_DIR"
  : >"$PIDS_FILE"
  ensure_tls
  ensure_bootstrap_key
  for region in $REGIONS; do write_config "$region"; done
  ensure_binary
  for region in $REGIONS; do start_member "$region"; done
  start_stub_origin
  start_webhooks
  wait_for_members_basic || die "pre-engine stack failed: members unhealthy"
  wait_for_http "X-4 stub origin" "http://127.0.0.1:$STUB_PORT/health"
  trap - EXIT
  print_summary
}

cmd_down() {
  stop_all
  log "stack is down (state kept in $STACK_DIR; STACK_FRESH=1 up wipes it)"
}

cmd_logs() {
  local follow="" target="all"
  for arg in "$@"; do
    case "$arg" in
      -f | --follow) follow="-f" ;;
      *) target="$arg" ;;
    esac
  done
  if [ "$target" = "all" ]; then
    # shellcheck disable=SC2086
    tail $follow -n 50 "$LOG_DIR"/*.log
  else
    tail $follow -n 100 "$LOG_DIR/$target.log"
  fi
}

usage() {
  sed -n '2,30p' "$ROOT/scripts/stack.sh" | sed 's/^# \{0,1\}//'
}

cmd="${1:-up}"
case "$cmd" in
  up) shift; cmd_up "$@" ;;
  check) shift; cmd_check "$@" ;;
  down) shift; cmd_down "$@" ;;
  logs) shift; cmd_logs "$@" ;;
  -h | --help | help) usage ;;
  test) shift; cmd_test "$@" ;;
  pre-engine) shift; cmd_pre_engine "$@" ;;
  *) die "unknown subcommand '$cmd' (up|check|test|pre-engine|down|logs)" ;;
esac
