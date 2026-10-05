#!/usr/bin/env bash
# Publish terrabase as the self-contained single-file linux-x64 executable
# (make publish), stage it as DIST_DIR/terrabase-linux-amd64 and smoke-run the
# staged bytes: /healthz must report this build's version, and an unknown
# setting must stop the process with exit code 2.
set -euo pipefail

dist=${1:-}
[[ -n $dist && $# -eq 1 ]] || { echo "usage: $0 DIST_DIR" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"
for tool in curl python3; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done

revision=$(git rev-parse HEAD)
[[ -z ${GITHUB_SHA:-} || $revision == "$GITHUB_SHA" ]] ||
  { echo "checkout $revision is not the event revision $GITHUB_SHA" >&2; exit 1; }

work=$(mktemp -d)
pid=
cleanup() {
  [[ -z $pid ]] || kill "$pid" 2>/dev/null || true
  rm -rf -- "$work"
}
trap cleanup EXIT

make --no-print-directory publish PUBLISH_DIR="$work/publish"
binary="$work/publish/terrabase"
[[ -x $binary ]] || { echo "publish produced no terrabase executable" >&2; exit 1; }
[[ $(od -An -tx1 -N4 "$binary" | tr -d ' \n') == 7f454c46 ]] || { echo "$binary is not an ELF executable" >&2; exit 1; }

staged="$work/terrabase-linux-amd64"
install -m 0755 -- "$binary" "$staged"

# Smoke-run the exact staged bytes.
set +e
"$staged" --no-such-setting=1 >/dev/null 2>&1
status=$?
set -e
[[ $status == 2 ]] || { echo "an unknown setting exited with $status, expected 2" >&2; exit 1; }

expected_version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/TerraScale.TerraBase/TerraScale.TerraBase.csproj)
port=$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
mkdir -p "$work/data"
"$staged" --region=ci --admin-port="$port" --data-directory="$work/data" >"$work/smoke.log" 2>&1 &
pid=$!
body=
for _ in $(seq 1 100); do
  body=$(curl -fsS "http://127.0.0.1:$port/healthz" 2>/dev/null) && break
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.1
done
[[ $body == *"\"status\":\"ok\""* && $body == *"\"version\":\"$expected_version\""* && $body == *"\"region\":\"ci\""* ]] || {
  echo "smoke run failed: /healthz answered '${body:-nothing}'" >&2
  cat "$work/smoke.log" >&2
  exit 1
}
kill "$pid"
wait "$pid" 2>/dev/null || true
pid=

mkdir -p -- "$dist"
install -m 0755 -- "$staged" "$dist/terrabase-linux-amd64"
printf 'staged and smoke-ran terrabase %s (%s) in %s\n' "$expected_version" "${revision:0:12}" "$dist"
