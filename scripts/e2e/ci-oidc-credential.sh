# shellcheck shell=bash
# The Fly e2e suite's credential in CI's e2e job, with no stored secret (docs/auth/github-actions-oidc.md). Sourced by each
# Fly e2e step (`source scripts/e2e/ci-oidc-credential.sh`), never run: the token lives only in that step's shell, never in
# $GITHUB_ENV, a file or a log. The job needs `permissions: id-token: write`. Self-check: ci-oidc-credential-test.sh.
#
# 1. Requests a GitHub Actions OIDC token for the platform issuer (URL-encoded audience), checks it is one compact JWT
#    (three base64url parts, nothing else) and masks it before anything else is printed.
# 2. Exchanges it at ts-auth (GitHub Actions grant, the bound public client) for a 15-minute project token of the bound e2e
#    project, checks its shape the same way and masks it. A refusal prints curl's status line only, never a token or a body.
# 3. Takes the project id from the token's payload. Not trusted: it only names the route; TerraBase verifies the signature
#    and that project_id equals the route's project, so a wrong id answers 404.
# 4. Ensures the fixed database ci-e2e with home gru and replicas sjc and fra, with the documented create call (201
#    created, or 409 name_taken when it exists), then reads its placement back. Wrong replicas are set right with the
#    documented update call at the home region; a wrong home cannot change, so it fails naming the database. Each test owns
#    and deletes its own collection in it.
# 5. Exports TERRABASE_E2E_FLY_PROJECT_TOKEN (SQL), TERRABASE_E2E_FLY_API_KEY (the documents client's bearer: the same
#    project token, so every request of the run is for the one bound project), TERRABASE_E2E_FLY_PROJECT and
#    TERRABASE_E2E_FLY_DATABASE, and drops the ID-token request variables so the tests cannot mint another.
# Mint one per step, after the build: a step must finish within the token's 15 minutes.

ci_oidc_credential() {
  set +x
  local -r issuer=https://auth.terrascale.tech/terrascale
  local -r client=01M4GHA0E2E000000000000000
  local -r grant=urn:terrascale:params:oauth:grant-type:github-actions
  local -r database=ci-e2e home=gru replicas='["fra","sjc"]'
  local -r jwt='^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$' ulid='^[0-9A-HJKMNP-TV-Z]{26}$'
  local github token payload project base body status placed
  : "${ACTIONS_ID_TOKEN_REQUEST_URL:?the e2e job needs permissions: id-token: write}"
  : "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?the e2e job needs permissions: id-token: write}"
  : "${TERRABASE_E2E_FLY_URL:?TERRABASE_E2E_FLY_URL is required}"

  # Under the step's bash -eo pipefail, a failed request leaves an empty value and the message below, not a silent exit.
  # Bearer values go through stdin (-H @-), never the command line.
  github=$(printf 'Authorization: bearer %s' "$ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
    | curl -sS --fail --proto '=https' -H @- "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=$(jq -rn --arg v "$issuer" '$v | @uri')" \
    | jq -r '.value // empty') || github=
  [[ $github =~ $jwt ]] || { echo "ci-oidc-credential: GitHub issued no well-formed OIDC token" >&2; return 1; }
  echo "::add-mask::$github"

  token=$(printf %s "$github" | curl -sS --fail --proto '=https' "$issuer/oauth/token" \
    -d "grant_type=$grant" -d "client_id=$client" --data-urlencode subject_token@- | jq -r '.access_token // empty') || token=
  unset github
  [[ $token =~ $jwt ]] || { echo "ci-oidc-credential: ts-auth issued no well-formed project token" >&2; return 1; }
  echo "::add-mask::$token"

  payload=$(cut -d. -f2 <<< "$token" | tr '_-' '/+')
  while (( ${#payload} % 4 )); do payload+='='; done
  project=$(base64 -d <<< "$payload" 2> /dev/null | jq -r '.project_id // empty' 2> /dev/null) || project=
  [[ $project =~ $ulid ]] || { echo "ci-oidc-credential: the project token names no project" >&2; return 1; }

  base="${TERRABASE_E2E_FLY_URL%/}/v1/projects/$project/databases"
  body=$(mktemp)
  # _ci_oidc_api <method> <path> [json]: sets status; the response body goes to $body, never to the log. The home region answers.
  _ci_oidc_api() {
    status=$(printf 'Authorization: Bearer %s' "$token" \
      | curl -sS --proto '=https' -o "$body" -w '%{http_code}' -X "$1" -H @- -H 'content-type: application/json' \
        -H "fly-prefer-region: $home" ${3:+--data "$3"} "$base$2") || status=000
  }
  # _ci_oidc_fail <message>: the body file goes, the message names only ids, regions and statuses.
  _ci_oidc_fail() { rm -f "$body"; unset -f _ci_oidc_api _ci_oidc_fail _ci_oidc_placement; echo "ci-oidc-credential: $1" >&2; }
  _ci_oidc_placement() { jq -c '[.homeRegion, ((.replicaRegions // []) | sort)]' "$body" 2> /dev/null; }

  _ci_oidc_api POST "" "{\"name\":\"$database\",\"homeRegion\":\"$home\",\"replicaRegions\":$replicas}"
  if ! [[ $status == 201 ]] && ! { [[ $status == 409 ]] && jq -e '.error.code == "name_taken"' "$body" > /dev/null 2>&1; }; then
    _ci_oidc_fail "creating database $database in project $project answered HTTP $status"
    return 1
  fi
  echo "ci-oidc-credential: project $project, database $database (HTTP $status)"

  _ci_oidc_api GET "/$database"
  [[ $status == 200 ]] || { _ci_oidc_fail "reading database $database of project $project answered HTTP $status"; return 1; }
  placed=$(_ci_oidc_placement) || { _ci_oidc_fail "reading database $database of project $project returned invalid placement JSON"; return 1; }
  if [[ $placed != "[\"$home\",$replicas]" ]]; then
    [[ $(jq -r '.homeRegion' "$body" 2> /dev/null) == "$home" ]] \
      || { _ci_oidc_fail "database $database of project $project is placed $placed, not [\"$home\",$replicas]; a home cannot change"; return 1; }
    _ci_oidc_api PATCH "/$database" "{\"replicaRegions\":$replicas}"
    [[ $status == 200 && $(_ci_oidc_placement) == "[\"$home\",$replicas]" ]] \
      || { _ci_oidc_fail "setting database $database of project $project from $placed to [\"$home\",$replicas] answered HTTP $status"; return 1; }
    echo "ci-oidc-credential: database $database placement set from $placed to [\"$home\",$replicas]"
  fi
  rm -f "$body"
  unset -f _ci_oidc_api _ci_oidc_fail _ci_oidc_placement

  export TERRABASE_E2E_FLY_PROJECT_TOKEN=$token TERRABASE_E2E_FLY_API_KEY=$token
  export TERRABASE_E2E_FLY_PROJECT=$project TERRABASE_E2E_FLY_DATABASE=$database
  unset token ACTIONS_ID_TOKEN_REQUEST_URL ACTIONS_ID_TOKEN_REQUEST_TOKEN
}

ci_oidc_credential
