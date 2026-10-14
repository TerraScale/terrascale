#!/usr/bin/env bash
# Self-check for scripts/fly/deploy.sh against a fake flyctl, docker, curl and volume-reset.sh; CI runs it in the `test`
# job. Equal formats roll out normally. A different format anywhere fails before mutation; a machine that does not
# answer can roll out, but any failure stops the rollout. Deployment must never invoke a volume reset.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
# One machine m-<region> per region, running $IMAGE. start-* setups
# make gru initially stopped; a start attempt sets its listed state (or makes the subsequent lookup fail).
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >>"$STATE/calls"
case "$1 $2" in
  "machine list")
    [[ ! -e $STATE/list-failed ]] || exit 1
    state=started
    image=$IMAGE
    [[ ! -e $STATE/digest ]] || image="$IMAGE@sha256:0f"
    for setup in "$STATE"/start-*; do
      [[ -e $setup ]] || continue
      state=stopped
      if [[ -e $STATE/attempted-start ]]; then
        state=${setup##*/start-}
        [[ $state != lookup-failed ]] || exit 1
      fi
    done
    jq -cn --arg image "$image" --arg state "$state" \
      '[("gru", "sjc", "fra") as $r | select($r != "gru" or $state != "missing") | {id: "m-\($r)", region: $r,
        state: (if $r == "gru" then $state else "started" end),
        config: {image: $image}}]' ;;
  "machine start")
    touch "$STATE/attempted-start"
    [[ -e $STATE/start-normal ]] && { mv "$STATE/start-normal" "$STATE/started-normally"; exit 0; }
    echo "fake flyctl: start rejected" >&2; exit 1 ;;
  "deploy -a") [[ ! -e $STATE/fail-deploy ]] ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
# The new image writes format 2-new; $STATE/no-docker makes reading it fail.
cat >"$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >>"$STATE/calls"
[[ ! -e $STATE/no-docker ]] && echo 2-new
FAKE
# A region serves the format in $STATE/format-<region> (2-new when absent). Machines named in $STATE/down time out.
cat >"$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
id=$(grep -oE 'fly-force-instance-id: m-[a-z]+' <<<"$*" | cut -d' ' -f2)
region=${id#m-}
echo "curl $id" >>"$STATE/calls"
if grep -qx "$id" "$STATE/down"; then exit 28; fi
format=2-new
if [[ -e $STATE/format-$region ]]; then format=$(cat "$STATE/format-$region"); fi
jq -cn --arg r "$region" --arg f "$format" '{status: "ok", region: $r, storageFormat: $f}'
FAKE
chmod +x "$tmp/bin/flyctl" "$tmp/bin/docker" "$tmp/bin/curl"
export PATH="$tmp/bin:$PATH" IMAGE=registry.fly.io/ts-terrabase:abc

# run <name> <app> <down machines> <setup...>: deploy.sh from a repo copy with a fake volume-reset.sh; returns its exit
# code. Setup words: no-docker, <region>=<format> (the format the region serves),
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
echo 'ERROR: automatic volume reset invoked' >&2
exit 97
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
run digest ts-terrabase "" digest || fail "reset clone's digest-pinned image failed normal retry"
[[ $(steps digest) == "deploy gru,deploy sjc,deploy fra" ]] || fail "digest retry changed rollout order"

for region in gru sjc fra; do
  ! run "different-$region" ts-terrabase "" "$region=1-old" || fail "accepted different $region format"
  ! grep -qE '^(deploy|machine (start|stop|update|clone|destroy)|reset) ' "$tmp/different-$region/calls" \
    || fail "different $region mutated the cluster before refusal"
  grep -q 'rollout refused before mutation' "$tmp/different-$region/out" || fail "missing format refusal diagnostic"
done
! run unversioned ts-terrabase "" fra=unversioned || fail "accepted an unversioned region"
[[ -z $(steps unversioned) ]] || fail "unversioned: $(steps unversioned)"

# An unavailable gru can be repaired by deployment, but failed health must never trigger a reset.
! run timeout ts-terrabase "m-gru" || fail "continued after gru health failed"
[[ $(steps timeout) == "deploy gru" ]] || fail "timeout: $(steps timeout)"

# Unknown failure in sjc stops before fra, without invoking reset recovery.
! run unknown ts-terrabase "m-sjc" || fail "kept rolling out after an unknown failure"
[[ $(steps unknown) == "deploy gru,deploy sjc" ]] || fail "unknown: $(steps unknown)"
! run deploy-failed ts-terrabase "" fail-deploy || fail "continued after Fly deploy failed"
[[ $(steps deploy-failed) == "deploy gru" ]] || fail "failed deploy: $(steps deploy-failed)"

! run no-docker ts-terrabase "" no-docker || fail "rolled out without the new image's format"
[[ -z $(steps no-docker) ]] || fail "no-docker: $(steps no-docker)"
! run list-failed ts-terrabase "" list-failed || fail "accepted a failed machine lookup"
[[ -z $(steps list-failed) ]] || fail "failed lookup mutated the cluster"
grep -q 'could not inspect' "$tmp/list-failed/out" || fail "failed lookup was not reported"

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
! grep -q '^reset ' "$tmp"/*/calls || fail "an automatic rollout attempted a volume reset"
echo "deploy: all checks passed"
