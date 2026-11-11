set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
e2e=$(dirname "$here")
repo=$(dirname "$e2e")
export E2E_STATE=${E2E_STATE:-$e2e/.state}
export E2E_TOOLS=${E2E_TOOLS:-${XDG_CACHE_HOME:-$HOME/.cache}/terrascale-e2e}
export E2E_BUILD=${E2E_BUILD:-$E2E_STATE/build}
export E2E_SDK_BUILD=${E2E_SDK_BUILD:-$E2E_STATE/sdk}
export NODE_EXTRA_CA_CERTS=$E2E_STATE/pki/ca.pem E2E_CA_FILE=$E2E_STATE/pki/ca.pem
holder=$E2E_STATE/namespace.pid
prepare() {
  export E2E_REVISION=${E2E_REVISION:-$(git -C "$repo" rev-parse HEAD)}
  mkdir -p "$E2E_STATE"
  chmod 700 "$E2E_STATE"
  bash "$here/tools.sh"
  for service in terrabase ts-auth dashboard; do bash "$here/build.sh" "$repo" "$E2E_BUILD" "$service"; done
  for sdk in dotnet go rust python js; do bash "$e2e/sdk/build.sh" "$sdk"; done
  npm ci --prefix "$e2e" --no-audit --no-fund
  (cd "$e2e" && npx playwright install chromium)
  bash "$here/pki.sh"
}
start() {
  rm -rf "$E2E_STATE/namespace"
  unshare --user --map-root-user --net --mount bash "$here/namespace.sh" > "$E2E_STATE/namespace.log" 2>&1 &
  echo $! > "$holder"
  until [ -f "$E2E_STATE/namespace/ready" ]; do
    kill -0 "$(cat "$holder")" 2> /dev/null || { cat "$E2E_STATE/namespace.log" >&2; exit 1; }
    sleep 0.1
  done
}
inside() {
  nsenter --target "$(cat "$holder")" --user --net --mount --preserve-credentials --wd="$PWD" \
    env SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt "$@"
}
stop() {
  echo > "$E2E_STATE/namespace/stop"
  local pid
  pid=$(cat "$holder")
  while kill -0 "$pid" 2> /dev/null; do sleep 0.1; done
  rm -f "$holder"
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
