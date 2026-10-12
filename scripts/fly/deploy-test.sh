#!/usr/bin/env bash
# Self-check for scripts/fly/deploy.sh against a fake flyctl, curl and volume-reset.sh; CI runs it in the `test` job.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
# One started machine m-<region> per region, running $IMAGE.
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >>"$STATE/calls"
case "$1 $2" in
  "machine list")
    jq -cn --arg image "$IMAGE" '[("gru", "sjc", "fra") | {id: "m-\(.)", region: ., state: "started", config: {image: $image}}]' ;;
  "deploy -a") ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
# Times out (exit 28, like the 02:45Z gru run) for the machines named in $STATE/down until their region is reset.
cat >"$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
id=$(grep -oE 'fly-force-instance-id: m-[a-z]+' <<<"$*" | cut -d' ' -f2)
echo "curl $id" >>"$STATE/calls"
if grep -qx "$id" "$STATE/down" && [[ ! -e $STATE/reset-${id#m-} ]]; then exit 28; fi
FAKE
chmod +x "$tmp/bin/flyctl" "$tmp/bin/curl"
export PATH="$tmp/bin:$PATH" IMAGE=registry.fly.io/ts-terrabase:abc

# run <name> <app> <down machines> [refuse]: deploy.sh from a repo copy with a fake volume-reset.sh; returns its exit code.
run() {
  export STATE=$tmp/$1
  mkdir -p "$STATE/repo/scripts/fly"
  cp "$here/deploy.sh" "$STATE/repo/scripts/fly/"
  cat >"$STATE/repo/scripts/fly/volume-reset.sh" <<'FAKE'
#!/usr/bin/env bash
echo "reset $2" >>"$STATE/calls"
[[ -e $STATE/refuse ]] && exit 1
touch "$STATE/reset-$2"
FAKE
  chmod +x "$STATE/repo/scripts/fly/volume-reset.sh"
  tr ' ' '\n' <<<"$3" >"$STATE/down"
  if [[ ${4:-} == refuse ]]; then touch "$STATE/refuse"; fi
  "$STATE/repo/scripts/fly/deploy.sh" "$2" fly.toml "$IMAGE" >"$STATE/out" 2>&1
}
fail() { echo "deploy-test: $*" >&2; exit 1; }
# The deploys, resets and checked machines, in order.
steps() { grep -oE "^deploy .*--regions [a-z]+|^reset [a-z]+|^curl m-[a-z]+" "$tmp/$1/calls" | sed -E 's/^deploy .*--regions /deploy /' | uniq | paste -sd, -; }

run healthy ts-terrabase "" || fail "healthy rollout failed: $(cat "$tmp/healthy/out")"
[[ $(steps healthy) == "deploy gru,curl m-gru,deploy sjc,curl m-sjc,deploy fra,curl m-fra" ]] || fail "healthy: $(steps healthy)"

# The 02:45Z failure: flyctl deploy passed, the live check timed out. The reset runs and the checks pass after it.
run refused ts-terrabase "m-gru" || fail "a refused gru was not reset: $(cat "$tmp/refused/out")"
[[ $(steps refused) == "deploy gru,curl m-gru,reset gru,curl m-gru,deploy sjc,curl m-sjc,deploy fra,curl m-fra" ]] \
  || fail "refused: $(steps refused)"

! run reset-refused ts-terrabase "m-sjc" refuse || fail "kept rolling out after the reset refused"
[[ $(steps reset-refused) == "deploy gru,curl m-gru,deploy sjc,curl m-sjc,reset sjc" ]] || fail "reset-refused: $(steps reset-refused)"

# ts-auth has no volume: a failed live check stops the rollout without a reset.
! run auth ts-auth "m-gru" || fail "ts-auth kept rolling out after a failed live check"
[[ $(steps auth) == "deploy gru,curl m-gru" ]] || fail "auth: $(steps auth)"
echo "deploy: all checks passed"
