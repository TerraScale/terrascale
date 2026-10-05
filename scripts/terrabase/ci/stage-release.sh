#!/usr/bin/env bash
# Publish terrabase as the self-contained single-file linux-x64 and linux-arm64
# executables (make publish) and stage them as DIST_DIR/terrabase-linux-amd64 and
# DIST_DIR/terrabase-linux-arm64. The amd64 bytes are smoke-run: /healthz must
# report this build's version, /readyz must be ready, and an unknown setting
# must stop the process with exit code 2. The x86_64 runner cannot run arm64,
# so those bytes are only checked to be an ELF64 little-endian aarch64
# executable.
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

# publish RUNTIME E_MACHINE: publish RUNTIME into $work/RUNTIME and check that
# the executable is ELF64 (class 02), little-endian (01) with e_machine (offset
# 18, little-endian) E_MACHINE.
publish() {
  local binary="$work/$1/terrabase"
  make --no-print-directory publish RUNTIME="$1" PUBLISH_DIR="$work/$1"
  [[ -x $binary ]] || { echo "publish for $1 produced no terrabase executable" >&2; exit 1; }
  [[ $(od -An -tx1 -N6 "$binary" | tr -d ' \n') == 7f454c460201 ]] ||
    { echo "$binary is not an ELF64 little-endian executable" >&2; exit 1; }
  [[ $(od -An -tx1 -j18 -N2 "$binary" | tr -d ' \n') == "$2" ]] ||
    { echo "$binary is not built for the $1 machine (e_machine $2)" >&2; exit 1; }
}
publish linux-x64 3e00
publish linux-arm64 b700
binary="$work/linux-x64/terrabase"

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
body=$(curl -fsS "http://127.0.0.1:$port/readyz" 2>/dev/null) || body=
[[ $body == *"\"status\":\"ready\""* && $body == *"\"region\":\"ci\""* ]] || {
  echo "smoke run failed: /readyz answered '${body:-nothing}'" >&2
  cat "$work/smoke.log" >&2
  exit 1
}
kill "$pid"
wait "$pid" 2>/dev/null || true
pid=

mkdir -p -- "$dist"
install -m 0755 -- "$staged" "$dist/terrabase-linux-amd64"
install -m 0755 -- "$work/linux-arm64/terrabase" "$dist/terrabase-linux-arm64"
printf 'staged terrabase %s (%s) for amd64 (smoke-ran) and arm64 in %s\n' "$expected_version" "${revision:0:12}" "$dist"
