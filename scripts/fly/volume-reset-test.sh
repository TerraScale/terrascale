#!/usr/bin/env bash
# Self-check for scripts/fly/volume-reset.sh against a fake flyctl; CI runs it in the `test` job.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
# One machine m1 (gru, vol_old) until the clone adds m2 (vol_new; failing checks when $STATE/unhealthy exists) and the
# machine destroy removes m1.
echo "$*" >>"$STATE/calls"
machine() {
  printf '{"id":"%s","region":"gru","state":"started","checks":[{"status":"%s"}],"config":{"image":"registry.fly.io/ts-terrabase:current","mounts":[{"volume":"%s"}]}}' "$@"
}
m2=$(machine m2 "$([[ -e $STATE/unhealthy ]] && echo warning || echo passing)" vol_new)
case "$1 $2" in
  "machine list")
    if [[ -e $STATE/destroyed ]]; then echo "[$m2]"
    elif [[ -e $STATE/cloned ]]; then echo "[$(machine m1 critical vol_old),$m2]"
    else echo "[$(machine m1 critical vol_old)]"; fi ;;
  "volumes show")
    if [[ -e $STATE/destroyed ]]; then echo '{"size_gb":1,"attached_machine_id":null}'; else echo '{"size_gb":1,"attached_machine_id":"m1"}'; fi ;;
  "volumes snapshots")
    case $3 in
      create) touch "$STATE/snapshot" ;;
      list)
        if [[ -e $STATE/snapshot ]]; then echo '[{"id":"vs_old","status":"created","size":5},{"id":"vs_new","status":"created","size":7}]'
        else echo '[{"id":"vs_old","status":"created","size":5}]'; fi ;;
    esac ;;
  "volumes destroy") ;;
  "logs -a") cat "$STATE/logs" ;;
  "machine stop" | "machine update") ;;
  "machine clone") touch "$STATE/cloned" ;;
  "machine destroy") touch "$STATE/destroyed" ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
cat >"$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >>"$STATE/calls"
[[ ! -e $STATE/no-docker ]] || exit 1
cat "$STATE/target-format"
FAKE
chmod +x "$tmp/bin/flyctl" "$tmp/bin/docker"
export PATH="$tmp/bin:$PATH"
refusal="terrabase: data directory has storage format 1-00000000000f; this build writes 2-51772a611d03; start with an empty data directory (no automatic migration)"

# run <name> <guard|no-guard|unhealthy> <log line> <args...>: a fresh repo copy and fake state; returns the exit code.
run() {
  local name=$1 setup=$2 log=$3
  shift 3
  export STATE=$tmp/$name
  mkdir -p "$STATE/repo/scripts/fly" "$STATE/repo/ops/fly"
  cp "$here/volume-reset.sh" "$STATE/repo/scripts/fly/"
  if [[ $setup != no-guard ]]; then
    echo 'storage-format=2-51772a611d03' >"$STATE/repo/ops/fly/greenfield-no-customer-data"
  fi
  echo '2-51772a611d03' >"$STATE/target-format"
  case $setup in
    empty-guard) : >"$STATE/repo/ops/fly/greenfield-no-customer-data" ;;
    duplicate-guard) echo 'storage-format=2-51772a611d03' >>"$STATE/repo/ops/fly/greenfield-no-customer-data" ;;
    later-format) echo '3-51772a611d03' >"$STATE/target-format" ;;
    different-schema) echo '2-00000000000f' >"$STATE/target-format" ;;
    no-docker) touch "$STATE/no-docker" ;;
  esac
  if [[ $setup == unhealthy ]]; then touch "$STATE/unhealthy"; fi
  echo "$log" >"$STATE/logs"
  "$STATE/repo/scripts/fly/volume-reset.sh" ts-terrabase gru "$@" >"$STATE/out" 2>&1
}
fail() { echo "volume-reset-test: $*" >&2; exit 1; }
# The calls that change Fly state, in order.
changes() { [[ ! -e $tmp/$1/calls ]] || grep -oE "machine stop|snapshots create|machine update .*--image [^ ]+|machine clone|machine destroy|volumes destroy [a-z_]+" "$tmp/$1/calls" | paste -sd, -; }
untouched() { [[ -z $(changes "$1") ]] || fail "$1 changed Fly state: $(changes "$1")"; }

! run no-guard no-guard "$refusal" || fail "ran without the greenfield guard"
grep -q "greenfield-no-customer-data is gone" "$tmp/no-guard/out" || fail "no-guard: $(cat "$tmp/no-guard/out")"
[[ ! -e $tmp/no-guard/calls ]] || fail "no-guard called flyctl"

for setup in empty-guard duplicate-guard later-format different-schema no-docker; do
  ! run "$setup" "$setup" "$refusal" --image registry.fly.io/ts-terrabase:new \
    || fail "$setup accepted an unapproved format"
  untouched "$setup"
done
! run recovery-later-format later-format "$refusal" || fail "recovery accepted an unapproved format"
untouched recovery-later-format

! run no-refusal guard "started" || fail "reset a machine that never refused its data"
untouched no-refusal

! run bad-usage guard "$refusal" --approve gru || fail "accepted unknown arguments"
untouched bad-usage
! run no-image guard "$refusal" --image || fail "accepted --image without an image"
untouched no-image

run dry-run guard "$refusal" --dry-run || fail "dry run failed: $(cat "$tmp/dry-run/out")"
grep -q "Cost: a new 1 GB volume replaces vol_old" "$tmp/dry-run/out" || fail "dry run printed no cost: $(cat "$tmp/dry-run/out")"
untouched dry-run

! run unhealthy unhealthy "$refusal" || fail "kept going with a clone whose checks fail"
[[ $(changes unhealthy) == "machine stop,snapshots create,machine clone" ]] || fail "unhealthy clone changed: $(changes unhealthy)"

run reset guard "$refusal" || fail "reset failed: $(cat "$tmp/reset/out")"
[[ $(changes reset) == "machine stop,snapshots create,machine clone,machine destroy,volumes destroy vol_old" ]] \
  || fail "reset ran: $(changes reset)"

# Planned by the deploy gate (formats differ): no refusal needed; the stopped machine gets the new image before the clone.
run planned guard "started" --image registry.fly.io/ts-terrabase:new || fail "planned reset failed: $(cat "$tmp/planned/out")"
[[ $(changes planned) == "machine stop,snapshots create,machine update m1 -a ts-terrabase --image registry.fly.io/ts-terrabase:new,machine clone,machine destroy,volumes destroy vol_old" ]] \
  || fail "planned reset ran: $(changes planned)"
grep -q "machine m1 -> m2, volume vol_old (deleted) -> vol_new (1 GB), snapshot vs_new" "$tmp/reset/out" \
  || fail "reset recorded: $(cat "$tmp/reset/out")"
# The output states what the reset deleted, platform data included, and where it still is.
grep -q "^Data lost: everything stored in gru's TerraBase" "$tmp/dry-run/out" || fail "dry run did not state the data lost"
grep -q "platform-auth (ts-auth's signing keys" "$tmp/reset/out" || fail "reset did not name the platform data: $(cat "$tmp/reset/out")"
grep -q "Recoverable only from snapshot of vol_old vs_new." "$tmp/reset/out" || fail "reset did not name the snapshot: $(cat "$tmp/reset/out")"
echo "volume-reset: all checks passed"
