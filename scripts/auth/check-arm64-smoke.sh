#!/usr/bin/env bash
# Exercise the arm64 smoke script with a commandless image fixture and real
# child processes, without running a container or an arm64 executable.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
sandbox=$(mktemp -d)
cleanup() {
  if [[ -f $sandbox/state/pid ]]; then kill -KILL "$(cat "$sandbox/state/pid")" 2>/dev/null || true; fi
  rm -rf -- "$sandbox"
}
trap cleanup EXIT
mkdir -p "$sandbox/repo/scripts/auth/ci" "$sandbox/bin" "$sandbox/state" "$sandbox/temp" "$sandbox/rootfs/lib"
cp "$repo/scripts/auth/ci/smoke-arm64.sh" "$sandbox/repo/scripts/auth/ci/"
mkdir -p "$sandbox/repo/src/TerraScale.Auth"
cp "$repo/src/TerraScale.Auth/Containerfile" "$sandbox/repo/src/TerraScale.Auth/"
touch "$sandbox/rootfs/lib/ld-linux-aarch64.so.1" "$sandbox/ts-auth"
chmod +x "$sandbox/ts-auth"
cat > "$sandbox/bin/git" <<'STUB'
#!/usr/bin/env bash
printf '%040d\n' 1
STUB
cat > "$sandbox/bin/podman" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$STATE/calls"
case $1 in
  pull) ;;
  create)
    [[ $# -eq 5 && $2 == --platform && $3 == linux/arm64 && $5 == /unused ]] ||
      { echo 'no command specified for commandless image' >&2; exit 1; }
    echo fixture-container ;;
  export) tar -cf - -C "$ROOTFS" . ;;
  rm) [[ $2 == fixture-container ]] ;;
  *) echo "unexpected container execution: $*" >&2; exit 1 ;;
esac
STUB
# env -i removes fixture variables: find state through the binary argument.
printf '#!%s\n' "$(python3 -c 'import sys; print(sys.executable)')" > "$sandbox/bin/qemu-aarch64"
cat >> "$sandbox/bin/qemu-aarch64" <<'STUB'
import os
from pathlib import Path
import signal
import sys
import time

root = Path(sys.argv[2])
state = Path(sys.argv[3]).parent / 'state'
extract = Path(os.environ['DOTNET_BUNDLE_EXTRACT_BASE_DIR'])
mode = (state / 'mode').read_text().strip()

def terminate(signum, frame):
    (state / 'term').write_text('present' if root.is_dir() and extract.is_dir() else 'removed')
    if mode != 'stalled':
        time.sleep(0.1)
        sys.exit(0)

signal.signal(signal.SIGTERM, terminate)
(state / 'pid').write_text(str(os.getpid()))
while True:
    time.sleep(0.1)
STUB
cat > "$sandbox/bin/curl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
# Do not let a healthy response race the child's signal-handler setup.
for _ in {1..200}; do
  [[ ! -f $STATE/pid ]] || break
  sleep 0.01
done
[[ -f $STATE/pid ]]
if [[ ${*: -1} == */readyz ]]; then
  printf 503
elif [[ $(cat "$STATE/mode") == interrupted ]]; then
  kill -TERM "$PPID"
  exit 1
else
  printf '{"version":"0.1.0+000000000000"}\n'
fi
STUB
# At the removal boundary, even a zombie would violate the cleanup contract.
real_rm=$(command -v rm)
cat > "$sandbox/bin/rm" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [[ ${*: -1} == "$TMPDIR/"* && -f $STATE/pid ]]; then
  if kill -0 "$(cat "$STATE/pid")" 2>/dev/null; then
    echo 'removed runtime directories before the child was reaped' >&2
    exit 1
  fi
  echo reaped > "$STATE/removal"
fi
exec "$REAL_RM" "$@"
STUB
chmod +x "$sandbox/bin/"*
export PATH="$sandbox/bin:$PATH" STATE="$sandbox/state" ROOTFS="$sandbox/rootfs" REAL_RM="$real_rm" TMPDIR="$sandbox/temp"
smoke="$sandbox/repo/scripts/auth/ci/smoke-arm64.sh"

for mode in healthy stalled interrupted; do
  rm -f "$STATE/pid" "$STATE/term" "$STATE/removal" "$STATE/calls"
  echo "$mode" > "$STATE/mode"
  status=0
  timeout --kill-after=2 15 bash "$smoke" "$sandbox/ts-auth" > "$sandbox/result" 2>&1 || status=$?
  expected=0
  if [[ $mode == interrupted ]]; then expected=143; fi
  if [[ $status != "$expected" ]]; then
    cat "$sandbox/result" >&2
    echo "$mode smoke exited $status, expected $expected" >&2
    exit 1
  fi
  [[ $(cat "$STATE/term") == present && $(cat "$STATE/removal") == reaped ]]
  [[ -z $(find "$TMPDIR" -mindepth 1 -print -quit) ]]
  grep -Fxq 'rm fixture-container' "$STATE/calls"
done

echo 'arm64 smoke guards passed: commandless image, graceful/stalled child cleanup and interruption'
