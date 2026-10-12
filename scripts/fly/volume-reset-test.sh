#!/usr/bin/env bash
# Self-check for scripts/fly/volume-reset.sh against a fake flyctl; CI runs it in the `test` job.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
# One machine m1 (gru, vol_old) until the clone adds m2 (vol_new) and the destroy removes m1.
echo "$*" >>"$STATE/calls"
machine() { printf '{"id":"%s","region":"gru","config":{"mounts":[{"volume":"%s"}]}}' "$1" "$2"; }
case "$1 $2" in
  "machine list")
    if [[ -e $STATE/destroyed ]]; then echo "[$(machine m2 vol_new)]"
    elif [[ -e $STATE/cloned ]]; then echo "[$(machine m1 vol_old),$(machine m2 vol_new)]"
    else echo "[$(machine m1 vol_old)]"; fi ;;
  "volumes show")
    if [[ -e $STATE/destroyed ]]; then echo '{"size_gb":1,"attached_machine_id":null}'; else echo '{"size_gb":1,"attached_machine_id":"m1"}'; fi ;;
  "volumes snapshots")
    case $3 in
      create) touch "$STATE/snapshot" ;;
      list)
        if [[ -e $STATE/snapshot ]]; then echo '[{"id":"vs_old","status":"created","size":5},{"id":"vs_new","status":"created","size":7}]'
        else echo '[{"id":"vs_old","status":"created","size":5}]'; fi ;;
    esac ;;
  "logs -a") cat "$STATE/logs" ;;
  "machine clone") touch "$STATE/cloned" ;;
  "machine destroy") touch "$STATE/destroyed" ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
chmod +x "$tmp/bin/flyctl"
export PATH="$tmp/bin:$PATH"
refusal="terrabase: data directory was written by another TerraBase layout or storage format; start with an empty data directory (no automatic migration)"

# run <name> <guard yes|no> <log line> <args...>: a fresh repo copy and fake state; prints the output, returns the exit code.
run() {
  local name=$1 guard=$2 log=$3
  shift 3
  export STATE=$tmp/$name
  mkdir -p "$STATE/repo/scripts/fly" "$STATE/repo/ops/fly"
  cp "$here/volume-reset.sh" "$STATE/repo/scripts/fly/"
  if [[ $guard == yes ]]; then touch "$STATE/repo/ops/fly/greenfield-no-customer-data"; fi
  echo "$log" >"$STATE/logs"
  "$STATE/repo/scripts/fly/volume-reset.sh" ts-terrabase gru "$@" >"$STATE/out" 2>&1
}
fail() { echo "volume-reset-test: $*" >&2; exit 1; }
# Nothing that changes Fly state ran: no snapshot, clone or destroy.
untouched() { [[ ! -e $tmp/$1/calls ]] || ! grep -qE "snapshots create|clone|destroy" "$tmp/$1/calls" || fail "$1 changed Fly state: $(cat "$tmp/$1/calls")"; }

! run no-guard no "$refusal" --approve-volume-reset gru || fail "ran without the greenfield guard"
grep -q "greenfield-no-customer-data is gone" "$tmp/no-guard/out" || fail "no-guard: $(cat "$tmp/no-guard/out")"
[[ ! -e $tmp/no-guard/calls ]] || fail "no-guard called flyctl"

! run no-refusal yes "started" --approve-volume-reset gru || fail "reset a machine that never refused its data"
untouched no-refusal

! run wrong-region yes "$refusal" --approve-volume-reset sjc || fail "accepted an approval for another region"
untouched wrong-region

! run no-approval yes "$refusal" || fail "ran without an approval"
untouched no-approval

run dry-run yes "$refusal" --dry-run || fail "dry run failed: $(cat "$tmp/dry-run/out")"
grep -q "Cost: +1 GB volume in gru" "$tmp/dry-run/out" || fail "dry run printed no cost: $(cat "$tmp/dry-run/out")"
untouched dry-run

run approved yes "$refusal" --approve-volume-reset gru || fail "approved reset failed: $(cat "$tmp/approved/out")"
[[ $(grep -oE "snapshots create|clone|destroy" "$tmp/approved/calls" | paste -sd,) == "snapshots create,clone,destroy" ]] \
  || fail "approved reset ran out of order: $(cat "$tmp/approved/calls")"
! grep -qE "volumes (destroy|delete)" "$tmp/approved/calls" || fail "approved reset deleted a volume"
grep -q "machine m1 -> m2, volume vol_old (kept, detached) -> vol_new, snapshot vs_new" "$tmp/approved/out" \
  || fail "approved reset recorded: $(cat "$tmp/approved/out")"
echo "volume-reset: all checks passed"
