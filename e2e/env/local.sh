set -euo pipefail
[ -z "${E2E_TARGET+set}" ] || { echo "local.sh refuses to run: E2E_TARGET is set; it runs the local target only, and env/production.sh is the only way to production" >&2; exit 2; }
here=$(cd "$(dirname "$0")" && pwd)
e2e=$(dirname "$here")
repo=$(dirname "$e2e")
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-4}"
export E2E_STATE=${E2E_STATE:-$e2e/.state}
export E2E_TOOLS=${E2E_TOOLS:-${XDG_CACHE_HOME:-$HOME/.cache}/terrascale-e2e}
export E2E_BUILD=${E2E_BUILD:-$E2E_STATE/build}
export E2E_SDK_BUILD=${E2E_SDK_BUILD:-$E2E_STATE/sdk}
export NODE_EXTRA_CA_CERTS=$E2E_STATE/pki/ca.pem E2E_CA_FILE=$E2E_STATE/pki/ca.pem
case ${E2E_REGIONS:-1} in 1|2) ;; *) echo 'E2E_REGIONS must be 1 or 2' >&2; exit 2 ;; esac
case ${E2E_NODES_PER_REGION:-1} in 1|2) ;; *) echo 'E2E_NODES_PER_REGION must be 1 or 2' >&2; exit 2 ;; esac
[ "${E2E_NODES_PER_REGION:-1}" = 1 ] || [ "${E2E_REGIONS:-1}" = 2 ] || { echo 'two nodes per region require two regions' >&2; exit 2; }
holder=$E2E_STATE/namespace.pid
init=$E2E_STATE/namespace.init
orderly_seconds=30
kill_seconds=10
prepare() {
  export E2E_REVISION=${E2E_REVISION:-$(git -C "$repo" rev-parse HEAD)}
  local missing=() tool
  for tool in certutil psql; do
    command -v "$tool" > /dev/null || missing+=("$tool")
  done
  if [ ${#missing[@]} -gt 0 ]; then
    echo "missing ${missing[*]}: nix shell nixpkgs#nss.tools nixpkgs#postgresql -c bash e2e/env/local.sh" >&2
    exit 1
  fi
  mkdir -p "$E2E_STATE" "$E2E_BUILD" "$E2E_SDK_BUILD"
  chmod 700 "$E2E_STATE"
  local pids=() pid failed=0
  (
    unset DOTNET_PROCESSOR_COUNT
    dotnet restore "$repo/TerraScale.slnx" --locked-mode
    export E2E_SLN_RESTORED=1
    for service in terrabase ts-platform ts-auth dashboard; do
      bash "$here/build.sh" "$repo" "$E2E_BUILD" "$service"
    done
    bash "$e2e/sdk/build.sh" dotnet
  ) &
  pids+=($!)
  (
    bash "$here/tools.sh"
    bash "$e2e/sdk/build.sh" python
  ) &
  pids+=($!)
  bash "$e2e/sdk/build.sh" go &
  pids+=($!)
  bash "$e2e/sdk/build.sh" rust &
  pids+=($!)
  (
    bash "$e2e/sdk/build.sh" js
    e2e_lock=$(sha256sum "$e2e/package-lock.json" | cut -d' ' -f1)
    if [ ! -d "$e2e/node_modules/@playwright/test" ] \
      || [ ! -f "$e2e/node_modules/.e2e-lock.sha256" ] \
      || [ "$(cat "$e2e/node_modules/.e2e-lock.sha256")" != "$e2e_lock" ]; then
      npm ci --prefix "$e2e" --no-audit --no-fund
      printf '%s\n' "$e2e_lock" > "$e2e/node_modules/.e2e-lock.sha256"
    fi
    pw_ver=$(node -e 'process.stdout.write(require(process.argv[1] + "/node_modules/@playwright/test/package.json").version)' "$e2e")
    pw_exe=$(node -e 'process.stdout.write(require(process.argv[1] + "/node_modules/@playwright/test").chromium.executablePath() || "")' "$e2e")
    if [ ! -f "$E2E_STATE/.playwright-chromium.version" ] \
      || [ "$(cat "$E2E_STATE/.playwright-chromium.version")" != "$pw_ver" ] \
      || [ -z "$pw_exe" ] \
      || [ ! -x "$pw_exe" ]; then
      (cd "$e2e" && npx playwright install chromium)
      printf '%s\n' "$pw_ver" > "$E2E_STATE/.playwright-chromium.version"
    fi
    status_lock=$(sha256sum "$repo/sites/status/package-lock.json" | cut -d' ' -f1)
    if [ ! -x "$repo/sites/status/node_modules/.bin/wrangler" ] \
      || [ ! -f "$repo/sites/status/node_modules/.status-lock.sha256" ] \
      || [ "$(cat "$repo/sites/status/node_modules/.status-lock.sha256")" != "$status_lock" ]; then
      npm ci --prefix "$repo/sites/status" --no-audit --no-fund
      printf '%s\n' "$status_lock" > "$repo/sites/status/node_modules/.status-lock.sha256"
    fi
  ) &
  pids+=($!)
  bash "$here/pki.sh" &
  pids+=($!)
  for pid in "${pids[@]}"; do
    if ! wait "$pid"; then
      failed=1
    fi
  done
  if [ "$failed" -ne 0 ]; then
    return 1
  fi
}
start() {
  printf '%s\n' "${E2E_REGIONS:-1}" > "$E2E_STATE/regions"
  printf '%s\n' "${E2E_NODES_PER_REGION:-1}" > "$E2E_STATE/nodes-per-region"
  rm -rf "$E2E_STATE/namespace"
  unshare --user --map-root-user --net --mount --pid --fork --mount-proc --kill-child bash "$here/namespace.sh" > "$E2E_STATE/namespace.log" 2>&1 &
  echo $! > "$holder"
  until [ -f "$E2E_STATE/namespace/ready" ]; do
    kill -0 "$(cat "$holder")" 2> /dev/null || { cat "$E2E_STATE/namespace.log" >&2; exit 1; }
    sleep 0.05
  done
  pgrep -P "$(cat "$holder")" > "$init"
}
inside() {
  nsenter --target "$(cat "$init")" --user --net --mount --pid --preserve-credentials --wd="$PWD" \
    env SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt E2E_REGIONS="$(cat "$E2E_STATE/regions")" E2E_NODES_PER_REGION="$(cat "$E2E_STATE/nodes-per-region")" "$@"
}
ended_within() {
  local deadline=$((SECONDS + $1))
  while kill -0 "$(cat "$holder")" 2> /dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.05
  done
}
stop() {
  kill -TERM "$(cat "$init")" 2> /dev/null || true
  if ! ended_within "$orderly_seconds"; then
    echo "the stack did not stop within $orderly_seconds s: killing every process in its namespace" >&2
    kill -KILL "$(cat "$init")" 2> /dev/null || true
    ended_within "$kill_seconds" || { echo "the namespace still has processes $kill_seconds s after its init was killed" >&2; exit 1; }
  fi
  rm -f "$holder" "$init"
}
case ${1:-test} in
  test)
    prepare
    start
    status=0
    { inside bash "$here/guard.sh" && inside bash "$here/up.sh" && (cd "$e2e" && inside npx playwright test); } || status=$?
    stop
    exit "$status"
    ;;
  prepare) prepare ;;
  start) start ;;
  exec)
    shift
    inside "$@"
    ;;
  stop) stop ;;
esac
