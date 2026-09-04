#!/usr/bin/env bash
# E-15 local three-member harness (development only, never shipped).
#
# Boots GRU / SFO / FRA terrabase-server members as local processes
# (no containers), plus the G1 edge pieces around them:
#   - REST Worker under `wrangler dev`  (../ts-proxy, opportunistic)
#   - Snippet under the local edge harness (../ts-proxy/harness, opportunistic)
#   - dashboard on workerd (../ts-dashboard, opportunistic)
#   - local webhook receiver (always; scripts/stack-webhook-receiver.py)
#
# Members are the gate: `up` and `check` fail closed unless all three answer
# both their operator probe (GET /live, unauthenticated) and their public
# probe (GET /api/v1/health, bootstrap Bearer key). Edge pieces never fail
# the stack: when a sibling checkout or tool is absent the script prints a
# SKIP line telling the operator exactly what would enable it.
#
# Usage:
#   ./scripts/stack.sh up      # boot everything, wait, verify  (default)
#   ./scripts/stack.sh check   # probe running members, exit non-zero on failure
#   ./scripts/stack.sh down    # stop everything started by `up`
#   ./scripts/stack.sh logs [-f] [gru|sfo|fra|proxy|dashboard|webhooks|stub]
#
# Knobs:
#   STACK_FRESH=1          wipe .terrascale/stack (data, certs, configs) before up
#   STACK_MEMBERS_ONLY=1   boot members + webhook receiver only, skip the edge
#   STACK_EDGE_QUALIFY=1   run one local-edge qualification pass against the stub
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
    fi
  done <"$PIDS_FILE"
  sleep 1
  while read -r name pid; do
    [ -n "${pid:-}" ] || continue
    if is_alive "$pid"; then
      warn "$name (pid $pid) ignored SIGTERM, sending SIGKILL"
      kill -9 "$pid" 2>/dev/null || true
    fi
  done <"$PIDS_FILE"
  rm -f "$PIDS_FILE"
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
    printf 'gateway_principals = ["spiffe://local/stack-gateway"]\n'
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
  if [ -x "$BIN" ]; then
    return 0
  fi
  if [ "${STACK_NO_BUILD:-0}" = "1" ]; then
    die "terrabase-server binary missing at $BIN and STACK_NO_BUILD=1"
  fi
  need_cmd cargo "install the Rust toolchain to build the member binary"
  log "building terrabase-server (first boot only; later boots reuse $BIN)"
  (cd "$ROOT" && cargo build --locked -p terrabase-server)
}

# --- members ---------------------------------------------------------------

start_member() {
  local region="$1" name api
  name="$(lower "$region")"
  api="$(api_port "$region")"
  log "starting $region member (public API 127.0.0.1:$api)"
  # shellcheck disable=SC2086
  nohup "$BIN" --config "$STACK_DIR/$name.toml" >>"$LOG_DIR/$name.log" 2>&1 &
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
    done
    if [ "$ok" = "1" ]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

check_members() {
  # Probes every member exactly the way `up` gates on. Returns non-zero with
  # the failing member named when anything does not answer.
  local failures=0 region body
  for region in $REGIONS; do
    if curl -sf --max-time 5 "http://127.0.0.1:$(obs_port "$region")/live" >/dev/null 2>&1; then
      log "$region operator /live: ok (127.0.0.1:$(obs_port "$region"))"
    else
      warn "$region operator /live FAILED (127.0.0.1:$(obs_port "$region")/live)"
      failures=$((failures + 1))
      continue
    fi
    body="$(curl -sf --max-time 5 -H "Authorization: Bearer $(bootstrap_key)" \
      "http://127.0.0.1:$(api_port "$region")/api/v1/health" 2>/dev/null || true)"
    case "$body" in
      *'"healthy":true'*)
        log "$region public /api/v1/health: ok (127.0.0.1:$(api_port "$region")) $body"
        ;;
      *)
        warn "$region public /api/v1/health FAILED (127.0.0.1:$(api_port "$region")): '${body:-no body}'"
        failures=$((failures + 1))
        ;;
    esac
  done
  if curl -sf --max-time 5 "http://127.0.0.1:$WEBHOOK_PORT/health" >/dev/null 2>&1; then
    log "webhook receiver /health: ok (127.0.0.1:$WEBHOOK_PORT)"
  else
    warn "webhook receiver /health FAILED (127.0.0.1:$WEBHOOK_PORT/health)"
    failures=$((failures + 1))
  fi
  return "$failures"
}

# --- edge pieces (opportunistic; never fail the stack) ----------------------

start_proxy() {
  # REST Worker under `wrangler dev` (G1). DEV_ORIGINS in ts-proxy's
  # wrangler.toml already points at this stack's 7101/7102/7103.
  if [ ! -d "$PROXY_DIR" ]; then
    warn "SKIP proxy: no ts-proxy checkout at $PROXY_DIR"
    return 0
  fi
  if ! command -v wrangler >/dev/null 2>&1 && [ ! -x "$PROXY_DIR/node_modules/.bin/wrangler" ]; then
    if [ ! -d "$PROXY_DIR/node_modules" ]; then
      warn "SKIP proxy: wrangler not found; run (cd $PROXY_DIR && npm ci) then re-run stack up"
      return 0
    fi
  fi
  if [ ! -d "$PROXY_DIR/node_modules" ]; then
    warn "SKIP proxy: $PROXY_DIR/node_modules missing; run (cd $PROXY_DIR && npm ci) then re-run stack up"
    return 0
  fi
  log "starting REST Worker under wrangler dev on 127.0.0.1:$PROXY_PORT"
  (cd "$PROXY_DIR" && nohup npm run dev >>"$LOG_DIR/proxy.log" 2>&1 & echo "proxy $!" >>"$PIDS_FILE")
  if curl -sf --max-time 2 "http://127.0.0.1:$PROXY_PORT/api/v1/regions" >/dev/null 2>&1; then
    log "proxy /api/v1/regions: ok"
  else
    warn "proxy not answering yet; tail $LOG_DIR/proxy.log (wrangler can take ~30 s on first boot)"
  fi
}

start_stub_origin() {
  # X-4 stub origin for the local edge harness. Only started so the Snippet
  # gate and the X-6 harness have something deterministic to point at.
  if [ ! -d "$PROXY_DIR/stub" ]; then
    warn "SKIP stub origin: no stub at $PROXY_DIR/stub"
    return 0
  fi
  if ! command -v go >/dev/null 2>&1; then
    warn "SKIP stub origin: go toolchain not found"
    return 0
  fi
  # The stub is its own Go module (stub/go.mod) sharing the harness's
  # insecure dev capability key, so the Snippet gate verifies end to end
  # with no extra configuration. Loopback only, never exposed.
  log "starting X-4 stub origin on 127.0.0.1:$STUB_PORT"
  (cd "$PROXY_DIR/stub" && nohup go run . -addr "127.0.0.1:$STUB_PORT" -verify-cap >>"$LOG_DIR/stub.log" 2>&1 & echo "stub $!" >>"$PIDS_FILE")
}

start_dashboard() {
  # Dashboard on workerd via `vinext dev` (D-59: workerd in dev too).
  if [ ! -d "$DASHBOARD_DIR" ]; then
    warn "SKIP dashboard: no ts-dashboard checkout at $DASHBOARD_DIR"
    return 0
  fi
  if [ ! -d "$DASHBOARD_DIR/node_modules" ]; then
    warn "SKIP dashboard: $DASHBOARD_DIR/node_modules missing; run (cd $DASHBOARD_DIR && npm ci) then re-run stack up"
    return 0
  fi
  log "starting dashboard on workerd (127.0.0.1:$DASHBOARD_PORT)"
  # The dashboard requires its runtime base URLs even in dev: first-contact
  # API calls go to the local proxy, the public URL is the local origin.
  # NODE_ENV=development selects the dashboard's own local-dev mode (dev
  # shim, workspace repository, insecure-cookie + local-portal paths); the
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
  {
    printf 'TERRASCALE_API_BASE_URL="http://127.0.0.1:%s"\n' "$PROXY_PORT"
    printf 'TERRASCALE_DASHBOARD_PUBLIC_URL="http://127.0.0.1:%s"\n' "$DASHBOARD_PORT"
    printf 'NODE_ENV="development"\n'
    printf 'TERRASCALE_AUTH_TERMS_VERSION="local-2026-08-30"\n'
    printf 'BETTER_AUTH_SECRET="%s"\n' "$(cat "$STACK_DIR/dashboard-auth-secret")"
  } >"$DASHBOARD_DIR/.dev.vars"
  (cd "$DASHBOARD_DIR" && nohup env \
    NODE_ENV="development" \
    TERRASCALE_API_BASE_URL="http://127.0.0.1:$PROXY_PORT" \
    TERRASCALE_DASHBOARD_PUBLIC_URL="http://127.0.0.1:$DASHBOARD_PORT" \
    TERRASCALE_DASHBOARD_DEV_PORT="$DASHBOARD_PORT" \
    TERRASCALE_AUTH_TERMS_VERSION="local-2026-08-30" \
    BETTER_AUTH_SECRET="$(cat "$STACK_DIR/dashboard-auth-secret")" \
    npm run dev >>"$LOG_DIR/dashboard.log" 2>&1 & echo "dashboard $!" >>"$PIDS_FILE")
  warn "dashboard boot is warn-only; tail $LOG_DIR/dashboard.log and open http://127.0.0.1:$DASHBOARD_PORT"
}

start_webhooks() {
  need_cmd python3 "install python3 to run the local webhook receiver"
  log "starting webhook receiver on 127.0.0.1:$WEBHOOK_PORT"
  nohup python3 "$ROOT/scripts/stack-webhook-receiver.py" --port "$WEBHOOK_PORT" --log "$WEBHOOKS_LOG" \
    >>"$LOG_DIR/webhooks.log" 2>&1 &
  record_pid "webhooks" "$!"
}

run_edge_qualification() {
  if [ "${STACK_EDGE_QUALIFY:-0}" != "1" ]; then
    return 0
  fi
  if [ ! -f "$PROXY_DIR/harness/local-edge/run.ts" ]; then
    warn "SKIP edge qualification: no harness at $PROXY_DIR/harness/local-edge/run.ts"
    return 0
  fi
  log "running local-edge qualification (32 readers) against the stub origin"
  (cd "$PROXY_DIR" && TERRABASE_VIEW_ENDPOINT="http://127.0.0.1:$STUB_PORT" node --experimental-strip-types harness/local-edge/run.ts) 2>&1 | tee "$LOG_DIR/edge-qualify.log" || warn "edge qualification failed; see $LOG_DIR/edge-qualify.log"
}

print_summary() {
  log "stack is up:"
  for region in $REGIONS; do
    log "  $region  api=http://127.0.0.1:$(api_port "$region")  tbep=127.0.0.1:$(tbep_port "$region")  obs=http://127.0.0.1:$(obs_port "$region")"
  done
  log "  proxy=http://127.0.0.1:$PROXY_PORT  stub=http://127.0.0.1:$STUB_PORT  webhooks=http://127.0.0.1:$WEBHOOK_PORT  dashboard=http://127.0.0.1:$DASHBOARD_PORT"
  log "  journey: export STACK_BOOTSTRAP_KEY=\"\$(cat $BOOTSTRAP_KEY_FILE)\"; curl -H \"Authorization: Bearer \$STACK_BOOTSTRAP_KEY\" http://127.0.0.1:7101/api/v1/health"
  log "  logs: $LOG_DIR  |  stop: ./scripts/stack.sh down"
}

# --- subcommands -------------------------------------------------------------

cmd_up() {
  need_cmd curl "install curl for the stack readiness probes"
  if [ "${STACK_FRESH:-0}" = "1" ]; then
    log "STACK_FRESH=1: wiping $STACK_DIR"
    stop_all || true
    rm -rf "$STACK_DIR"
  else
    stop_all || true
  fi
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
  if [ "${STACK_MEMBERS_ONLY:-0}" = "1" ]; then
    log "STACK_MEMBERS_ONLY=1: skipping proxy/stub/dashboard/edge"
  else
    start_stub_origin
    start_proxy
    start_dashboard
    run_edge_qualification
  fi
  if ! check_members; then
    die "stack up failed: final verification failed"
  fi
  print_summary
}

cmd_check() {
  [ -f "$BOOTSTRAP_KEY_FILE" ] || die "no $BOOTSTRAP_KEY_FILE; is the stack up? (./scripts/stack.sh up)"
  if ! check_members; then
    die "stack check failed"
  fi
  log "stack check passed: 3/3 members + webhook receiver healthy"
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
  *) die "unknown subcommand '$cmd' (up|check|down|logs)" ;;
esac
