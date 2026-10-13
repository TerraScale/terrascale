#!/usr/bin/env bash
# Self-check for scripts/fly/deploy.sh against a fake flyctl, docker, curl and volume-reset.sh; CI runs it in the `test`
# job. The storage-format gate: equal formats roll out normally, a different format is reset as a planned step, a machine
# that does not answer rolls out and a refusal after it is reset, and any other failure stops the rollout.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
# One machine m-<region> per region, running $IMAGE; a reset region's clone pins it by digest, as Fly does. start-* setups
# make gru initially stopped; a start attempt sets its listed state (or makes the subsequent lookup fail).
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >>"$STATE/calls"
case "$1 $2" in
  "machine list")
    state=started
    for setup in "$STATE"/start-*; do
      [[ -e $setup ]] || continue
      state=stopped
      if [[ -e $STATE/attempted-start ]]; then
        state=${setup##*/start-}
        [[ $state != lookup-failed ]] || exit 1
      fi
    done
    jq -cn --arg image "$IMAGE" --arg state "$state" --arg files "$(ls "$STATE")" \
      '[("gru", "sjc", "fra") as $r | select($r != "gru" or $state != "missing") | {id: "m-\($r)", region: $r,
        state: (if $r == "gru" then $state else "started" end),
        config: {image: (if any($files | split("\n")[]; . == "reset-" + $r) then "\($image)@sha256:0f" else $image end)}}]' ;;
  "machine start")
    touch "$STATE/attempted-start"
    [[ -e $STATE/start-normal ]] && { mv "$STATE/start-normal" "$STATE/started-normally"; exit 0; }
    echo "fake flyctl: start rejected" >&2; exit 1 ;;
  "deploy -a") ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
# The new image writes format 2-new; $STATE/no-docker makes reading it fail.
cat >"$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >>"$STATE/calls"
[[ ! -e $STATE/no-docker ]] && echo 2-new
FAKE
# A region serves the format in $STATE/format-<region> (2-new when absent) until it is reset. The machines named in
# $STATE/down time out (exit 28, like the 02:45Z gru run) until their region is reset.
cat >"$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
id=$(grep -oE 'fly-force-instance-id: m-[a-z]+' <<<"$*" | cut -d' ' -f2)
region=${id#m-}
echo "curl $id" >>"$STATE/calls"
if grep -qx "$id" "$STATE/down" && [[ ! -e $STATE/reset-$region ]]; then exit 28; fi
format=2-new
if [[ -e $STATE/format-$region && ! -e $STATE/reset-$region ]]; then format=$(cat "$STATE/format-$region"); fi
jq -cn --arg r "$region" --arg f "$format" '{status: "ok", region: $r, storageFormat: $f}'
FAKE
chmod +x "$tmp/bin/flyctl" "$tmp/bin/docker" "$tmp/bin/curl"
export PATH="$tmp/bin:$PATH" IMAGE=registry.fly.io/ts-terrabase:abc

# run <name> <app> <down machines> <setup...>: deploy.sh from a repo copy with a fake volume-reset.sh; returns its exit
# code. Setup words: refuse (the reset refuses), no-docker, <region>=<format> (the format the region serves),
# start-normal (an explicit start succeeds), start-<state> (the start fails, then lists that state for gru).
run() {
  export STATE=$tmp/$1
  local app=$2 down=$3 word
  shift 3
  mkdir -p "$STATE/repo/scripts/fly"
  cp "$here/deploy.sh" "$STATE/repo/scripts/fly/"
  cat >"$STATE/repo/scripts/fly/volume-reset.sh" <<'FAKE'
#!/usr/bin/env bash
echo "reset $2${3:+ $3}" >>"$STATE/calls"
[[ -e $STATE/refuse ]] && exit 1
touch "$STATE/reset-$2"
FAKE
  chmod +x "$STATE/repo/scripts/fly/volume-reset.sh"
  tr ' ' '\n' <<<"$down" >"$STATE/down"
  for word in "$@"; do
    case $word in
      *=*) echo "${word#*=}" >"$STATE/format-${word%%=*}" ;;
      *) touch "$STATE/$word" ;;
    esac
  done
  "$STATE/repo/scripts/fly/deploy.sh" "$app" fly.toml "$IMAGE" >"$STATE/out" 2>&1
}
fail() { echo "deploy-test: $*" >&2; exit 1; }
# The deploys and resets, in order.
steps() { grep -oE "^deploy .*--regions [a-z]+|^reset [a-z]+( --image)?" "$tmp/$1/calls" | sed -E 's/^deploy .*--regions /deploy /' | paste -sd, -; }

run equal ts-terrabase "" || fail "equal formats failed: $(cat "$tmp/equal/out")"
[[ $(steps equal) == "deploy gru,deploy sjc,deploy fra" ]] || fail "equal: $(steps equal)"
grep -q "runs abc, storage format 2-new" "$tmp/equal/out" || fail "equal recorded: $(cat "$tmp/equal/out")"

run different ts-terrabase "" sjc=1-old fra=unversioned || fail "different formats failed: $(cat "$tmp/different/out")"
[[ $(steps different) == "deploy gru,reset sjc --image,deploy sjc,reset fra --image,deploy fra" ]] \
  || fail "different: $(steps different)"

# The 02:45Z failure: gru does not answer (no format to compare), its deploy passes and the live check keeps polling
# until it times out; the logs show the refusal, so the reset runs and the checks pass after it.
run timeout ts-terrabase "m-gru" || fail "a refused gru was not reset: $(cat "$tmp/timeout/out")"
[[ $(steps timeout) == "deploy gru,reset gru,deploy sjc,deploy fra" ]] || fail "timeout: $(steps timeout)"

# Unknown failure: sjc does not answer and its logs hold no refusal (the reset refuses): the rollout stops at sjc.
! run unknown ts-terrabase "m-sjc" refuse || fail "kept rolling out after an unknown failure"
[[ $(steps unknown) == "deploy gru,deploy sjc,reset sjc" ]] || fail "unknown: $(steps unknown)"

# A planned reset that fails stops the rollout before the region's deploy.
! run planned-refused ts-terrabase "" sjc=1-old refuse || fail "kept rolling out after a failed planned reset"
[[ $(steps planned-refused) == "deploy gru,reset sjc --image" ]] || fail "planned-refused: $(steps planned-refused)"

! run no-docker ts-terrabase "" no-docker || fail "rolled out without the new image's format"
[[ -z $(steps no-docker) ]] || fail "no-docker: $(steps no-docker)"

# ts-auth has no volume and no format: no gate, and a failed live check stops the rollout without a reset.
! run auth ts-auth "m-gru" || fail "ts-auth kept rolling out after a failed live check"
[[ $(steps auth) == "deploy gru" ]] || fail "auth: $(steps auth)"
! grep -q "^docker" "$tmp/auth/calls" || fail "auth read a storage format"

run start-normal ts-auth "" start-normal || fail "normal explicit start failed: $(cat "$tmp/start-normal/out")"
[[ $(steps start-normal) == "deploy gru,deploy sjc,deploy fra" ]] || fail "normal start stopped the rollout"
grep -q '^machine start m-gru ' "$tmp/start-normal/calls" || fail "stopped machine was not started"

for state in starting started; do
  run "race-$state" ts-auth "" "start-$state" || fail "concurrent $state failed: $(cat "$tmp/race-$state/out")"
  [[ $(steps "race-$state") == "deploy gru,deploy sjc,deploy fra" ]] || fail "concurrent $state stopped the rollout"
  grep -q '^curl m-gru$' "$tmp/race-$state/calls" || fail "concurrent $state bypassed the pinned health check"
done

run terrabase-race ts-terrabase "" start-starting || fail "TerraBase concurrent start failed: $(cat "$tmp/terrabase-race/out")"
[[ $(steps terrabase-race) == "deploy gru,deploy sjc,deploy fra" ]] || fail "TerraBase race caused a volume reset"

# A concurrent start still has to become healthy. It never turns a failed health check into success.
! run race-unhealthy ts-auth "m-gru" start-starting || fail "concurrent start bypassed a failed health check"
[[ $(steps race-unhealthy) == "deploy gru" ]] || fail "unhealthy concurrent start continued the rollout"

# Other regions remain started: accepting any started machine instead of the rejected ID would hide these failures.
for state in stopped crashed missing lookup-failed; do
  ! run "start-$state" ts-auth "" "start-$state" || fail "accepted failed start: $state"
  [[ $(steps "start-$state") == "deploy gru" ]] || fail "failed $state start continued the rollout"
  ! grep -q '^curl' "$tmp/start-$state/calls" || fail "failed $state start reached the health check"
done
echo "deploy: all checks passed"
