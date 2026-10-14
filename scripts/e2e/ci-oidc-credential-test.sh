#!/usr/bin/env bash
# Self-check for scripts/e2e/ci-oidc-credential.sh against a fake curl (nothing reaches GitHub, ts-auth or TerraBase); CI
# runs it in the `test` job. Each case sources the script under the e2e step's bash -eo pipefail.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
cat >"$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
# Answers from $STATE: the GitHub ID-token endpoint, ts-auth's token endpoint and the databases API. Logs "METHOD URL [data]".
out= method= data= url= write=
while (($#)); do
  case $1 in
    -o) out=$2; shift ;;
    -X) method=$2; shift ;;
    -w) write=$2; shift ;;
    --data) data=$2; shift ;;
    -d | --data-urlencode | -H | --proto) shift ;;
    -*) ;;
    *) url=$1 ;;
  esac
  shift
done
stdin=$(cat)
echo "${method:-GET} $url $data" >>"$STATE/calls"
answer() { # answer <name>: writes $STATE/<name>-body to -o and prints $STATE/<name>-status for -w.
  cat "$STATE/$1-body" >"$out" 2>/dev/null || : >"$out"
  [[ -z $write ]] || cat "$STATE/$1-status"
}
case $url in
  'https://github.invalid/token?api-version=2.0&audience=https%3A%2F%2Fauth.terrascale.tech%2Fterrascale')
    [[ $stdin == 'Authorization: bearer request-secret' ]] || exit 22
    jq -n --rawfile v "$STATE/github" '{value: $v | rtrimstr("\n")}' ;;
  https://auth.terrascale.tech/terrascale/oauth/token)
    [[ $stdin == "$(cat "$STATE/github")" ]] || exit 22
    jq -n --rawfile v "$STATE/access" '{access_token: $v | rtrimstr("\n")}' ;;
  https://terrabase.invalid/v1/projects/*/databases)
    [[ $method == POST && $stdin == "Authorization: Bearer $(cat "$STATE/access")" ]] || exit 22
    answer create ;;
  https://terrabase.invalid/v1/projects/*/databases/ci-e2e)
    case $method in
      GET) [[ -e $STATE/patched ]] && answer patch || answer get ;;
      PATCH) touch "$STATE/patched"; answer patch ;;
      *) exit 22 ;;
    esac ;;
  *) echo "fake curl: unexpected $method $url" >&2; exit 7 ;;
esac
FAKE
chmod +x "$tmp/bin/curl"
export PATH="$tmp/bin:$PATH"

b64url() { base64 -w0 | tr '+/' '-_' | tr -d '='; }
project=01M4XT0FBY2PET185K3HN3V7A8
# The payload's base64url holds - and _, so the script's translation back to base64 is exercised.
payload=$(printf '{"project_id":"%s","x":">>>???"}' "$project" | b64url)
[[ $payload == *[-_]* ]]
access="eyJhbGciOiJSUzI1NiJ9.$payload.c2lnbmF0dXJl-access_secret"
github='eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJyZXBvIn0.Z2l0aHVi-github_secret'
placed() { printf '{"id":"01M4XT0FBY2PET185K3HN3V7B9","name":"ci-e2e","homeRegion":"%s","replicaRegions":%s,"note":"BODY-SECRET"}' "$1" "$2"; }

# run <name> <create status> <create body> [get body] [patch status] [patch body]: a fresh fake state; returns the exit code.
run() {
  export STATE=$tmp/$1
  mkdir -p "$STATE"
  printf %s "${GITHUB_TOKEN_VALUE-$github}" >"$STATE/github"
  printf %s "${ACCESS_TOKEN_VALUE-$access}" >"$STATE/access"
  printf %s "$2" >"$STATE/create-status"
  printf %s "$3" >"$STATE/create-body"
  printf 200 >"$STATE/get-status"
  printf %s "${4-$(placed gru '["sjc","fra"]')}" >"$STATE/get-body"
  printf %s "${5-200}" >"$STATE/patch-status"
  printf %s "${6-$(placed gru '["fra","sjc"]')}" >"$STATE/patch-body"
  : >"$STATE/calls"
  bash -eo pipefail -c '
    export ACTIONS_ID_TOKEN_REQUEST_URL="https://github.invalid/token?api-version=2.0" ACTIONS_ID_TOKEN_REQUEST_TOKEN=request-secret
    export TERRABASE_E2E_FLY_URL=https://terrabase.invalid/
    # shellcheck source=scripts/e2e/ci-oidc-credential.sh
    source "$1"
    printf "%s\n" "$TERRABASE_E2E_FLY_PROJECT_TOKEN" "$TERRABASE_E2E_FLY_API_KEY" "$TERRABASE_E2E_FLY_PROJECT" "$TERRABASE_E2E_FLY_DATABASE" \
      "${ACTIONS_ID_TOKEN_REQUEST_URL-unset} ${ACTIONS_ID_TOKEN_REQUEST_TOKEN-unset} ${token-unset}" "$(type -t _ci_oidc_api || echo none)" >"$STATE/exported"
  ' _ "$here/ci-oidc-credential.sh" >"$STATE/out" 2>&1
}
fail() { echo "ci-oidc-credential-test: $*" >&2; [[ ! -e $STATE/out ]] || sed 's/^/  | /' "$STATE/out" >&2; exit 1; }
# The output never holds a token, the ID-token request token or a response body, except a token in its own ::add-mask:: line.
secret_free() {
  ! grep -v '^::add-mask::' "$STATE/out" | grep -qE 'request-secret|access_secret|github_secret|BODY-SECRET' || fail "$1: a secret or body reached the log"
}
refused() { # refused <name> <message>
  [[ ! -e $STATE/exported ]] || fail "$1: exported a credential"
  grep -qF "$2" "$STATE/out" || fail "$1: no \"$2\""
  secret_free "$1"
}

# Created: both tokens masked before anything else, exports set, ID-token variables and helpers gone, placement read back.
run created 201 "$(placed gru '["fra","sjc"]')" "$(placed gru '["sjc","fra"]')" || fail "created: failed"
[[ $(head -2 "$STATE/out") == "::add-mask::$github"$'\n'"::add-mask::$access" ]] || fail "created: tokens not masked first"
[[ $(cat "$STATE/exported") == "$access"$'\n'"$access"$'\n'"$project"$'\n'"ci-e2e"$'\n'"unset unset unset"$'\n'"none" ]] || fail "created: exports"
grep -qF 'POST https://terrabase.invalid/v1/projects/01M4XT0FBY2PET185K3HN3V7A8/databases {"name":"ci-e2e","homeRegion":"gru","replicaRegions":["fra","sjc"]}' "$STATE/calls" || fail "created: create call"
grep -q '^GET .*/databases/ci-e2e' "$STATE/calls" || fail "created: placement not read back"
! grep -q '^PATCH' "$STATE/calls" || fail "created: patched a placed database"
secret_free created

# Exists (409 name_taken) without fra: the replicas are set at the home region and checked.
run repaired 409 '{"error":{"code":"name_taken","message":"BODY-SECRET"}}' "$(placed gru '["sjc"]')" || fail "repaired: failed"
grep -qF 'PATCH https://terrabase.invalid/v1/projects/01M4XT0FBY2PET185K3HN3V7A8/databases/ci-e2e {"replicaRegions":["fra","sjc"]}' "$STATE/calls" || fail "repaired: no patch"
grep -qF 'placement set from ["gru",["sjc"]] to ["gru",["fra","sjc"]]' "$STATE/out" || fail "repaired: not reported"
secret_free repaired

# Refusals: each fails, exports nothing and prints no secret or body.
run wrong-home 409 '{"error":{"code":"name_taken"}}' "$(placed sjc '["gru","fra"]')" && fail "wrong-home: passed"
refused wrong-home 'a home cannot change'
! grep -q '^PATCH' "$STATE/calls" || fail "wrong-home: patched"
run patch-refused 409 '{"error":{"code":"name_taken"}}' "$(placed gru '[]')" 403 '{"error":{"code":"forbidden","message":"BODY-SECRET"}}' && fail "patch-refused: passed"
refused patch-refused 'answered HTTP 403'
run patch-unplaced 409 '{"error":{"code":"name_taken"}}' "$(placed gru '[]')" 200 "$(placed gru '["sjc"]')" && fail "patch-unplaced: passed"
refused patch-unplaced 'answered HTTP 200'
run create-refused 403 '{"error":{"code":"forbidden","message":"BODY-SECRET"}}' && fail "create-refused: passed"
refused create-refused 'answered HTTP 403'
run other-conflict 409 '{"error":{"code":"conflict","message":"BODY-SECRET"}}' && fail "other-conflict: passed"
refused other-conflict 'answered HTTP 409'
GITHUB_TOKEN_VALUE=$'eyJh.eyJz.c2ln\ngithub_secret' run github-newline 201 '{}' && fail "github-newline: passed"
refused github-newline 'GitHub issued no well-formed OIDC token'
! grep -q 'oauth/token' "$STATE/calls" || fail "github-newline: exchanged a malformed token"
! grep -q 'add-mask' "$STATE/out" || fail "github-newline: echoed the token"
ACCESS_TOKEN_VALUE='eyJh.access_secret' run access-two-parts 201 '{}' && fail "access-two-parts: passed"
refused access-two-parts 'ts-auth issued no well-formed project token'
ACCESS_TOKEN_VALUE="eyJhbGciOiJSUzI1NiJ9.$(printf '{"project_id":"not-a-ulid"}' | b64url).c2ln-access_secret" run no-project 201 '{}' && fail "no-project: passed"
refused no-project 'the project token names no project'
! grep -q 'terrabase.invalid' "$STATE/calls" || fail "no-project: called TerraBase"

echo "ci-oidc-credential-test: ok"
