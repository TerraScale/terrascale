#!/usr/bin/env bash
# Tests scripts/handoff/morning-credentials.sh and the e2e credential script against fake flyctl, gh and curl
# (testdata/fakes): nothing reaches Fly, GitHub or TerraBase. The fake key must land only in the fake GitHub secret store.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
readonly SECRET=tbk_FAKE_never_shown_0123456789
failures=0

# A fresh copy of the scripts (so the bench script's presence is controlled) with its own fake state.
setup() {
  work=$(mktemp -d)
  mkdir -p "$work/repo/scripts/handoff" "$work/repo/scripts/e2e" "$work/repo/benchmark/scripts" "$work/state"
  cp "$root/scripts/handoff/morning-credentials.sh" "$work/repo/scripts/handoff/"
  cp "$root/scripts/e2e/install-ci-credential.sh" "$work/repo/scripts/e2e/"
  export FAKE_DIR="$work/state" FAKE_LOG="$work/calls.log" FAKE_SECRET=$SECRET PATH="$here/testdata/fakes:$PATH" HOME="$work"
  export TERRABASE_PEER_SECRET
  TERRABASE_PEER_SECRET=$(openssl rand -hex 32)
  : > "$FAKE_LOG"
}
run() { (cd "$work" && bash "$work/repo/scripts/handoff/morning-credentials.sh" "$@" 2>&1); }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
contains() { grep -qF -- "$2" <<<"$1" || fail "$3: expected '$2' in: $1"; }
# The key may exist only in the fake GitHub secret store: never in output, the call log or any other file.
no_leak() {
  grep -qF "$SECRET" <<<"$1" && fail "$2: the key was printed"
  grep -qF "$SECRET" "$FAKE_LOG" && fail "$2: the key was on a command line"
  grep -qF "$TERRABASE_PEER_SECRET" <<<"$1" && fail "$2: the peer credential was printed"
  grep -qF "$TERRABASE_PEER_SECRET" "$FAKE_LOG" && fail "$2: the peer credential was on a command line"
  local file
  while IFS= read -r file; do
    [[ $file == "$FAKE_DIR/secrets/TERRABASE_E2E_FLY_API_KEY" ]] || fail "$2: the key was written to $file"
  done < <(grep -rlF "$SECRET" "$work" || true)
}

test_missing_peer_credential_refuses_before_proxy() {
  setup
  local out
  if out=$(unset TERRABASE_PEER_SECRET; run install e2e); then
    fail "install without a peer credential succeeded"
  fi
  contains "$out" "TERRABASE_PEER_SECRET must contain 64 hexadecimal characters" missing-peer
  grep -q 'flyctl proxy' "$FAKE_LOG" && fail "missing peer credential opened a proxy"
  rm -rf "$work"
}

test_dry_run_touches_nothing() {
  setup
  local out; out=$(run dry-run)
  contains "$out" "documents:read documents:write, project e2e only, never expires" dry-run
  contains "$out" "GitHub Actions secret TERRABASE_E2E_FLY_API_KEY" dry-run
  contains "$out" "script not on this branch yet" dry-run
  contains "$out" "readiness gru: ts-auth /readyz 200" dry-run
  grep -qE 'curl (POST|PUT|DELETE)|gh secret (set|delete)|flyctl proxy' "$FAKE_LOG" && fail "dry-run changed or opened something"
  [[ -e $FAKE_DIR/secrets ]] && [[ -n $(ls "$FAKE_DIR/secrets") ]] && fail "dry-run set a secret"
  rm -rf "$work"
}

test_install_status_rotate_revoke_e2e() {
  setup
  local out
  out=$(run install e2e); no_leak "$out" install
  contains "$out" "installed: TERRABASE_E2E_FLY_API_KEY" install
  [[ $(cat "$FAKE_DIR/secrets/TERRABASE_E2E_FLY_API_KEY") == "$SECRET" ]] || fail "install: the secret store does not hold the key"
  [[ $(cat "$FAKE_DIR/secrets/TERRABASE_E2E_FLY_DATABASE") == e2e ]] || fail "install: database id secret"
  [[ $(jq '.orgs | length' "$FAKE_DIR/tb.json") == 1 && $(jq '.keys | length' "$FAKE_DIR/tb.json") == 1 ]] || fail "install: tenant or key not created once"
  [[ $(jq -c '.keys[0] | [.expiresAt, .scopes]' "$FAKE_DIR/tb.json") == '[null,["documents:read","documents:write"]]' ]] || fail "install: key must be documents-only with no expiry"
  out=$(run install e2e); no_leak "$out" rotate
  [[ $(jq '.orgs | length' "$FAKE_DIR/tb.json") == 1 ]] || fail "rotate: created a second tenant"
  [[ $(jq '.keys | length' "$FAKE_DIR/tb.json") == 1 ]] || fail "rotate: the previous key was not revoked"
  out=$(run status); no_leak "$out" status
  contains "$out" "TERRABASE_E2E_FLY_API_KEY" status
  contains "$out" "key:      01FAKE" status
  contains "$out" "expires never" status
  out=$(run revoke e2e)
  contains "$out" "revoked: TERRABASE_E2E_FLY_API_KEY (+ _PROJECT, _DATABASE) removed" revoke
  [[ -n $(ls "$FAKE_DIR/secrets") ]] && fail "revoke: secrets still set: $(ls "$FAKE_DIR/secrets")"
  [[ $(jq '.orgs | length' "$FAKE_DIR/tb.json") == 1 ]] || fail "revoke: the tenant must stay"
  [[ $(jq '.keys | length' "$FAKE_DIR/tb.json") == 0 ]] || fail "revoke: key still active"
  rm -rf "$work"
}

test_cleanup_e2e_leaves_nothing() {
  setup
  local out
  run install e2e > /dev/null
  out=$(run cleanup e2e); no_leak "$out" cleanup
  contains "$out" "cleanup: e2e database, project and organization deleted" cleanup
  [[ -n $(ls "$FAKE_DIR/secrets") ]] && fail "cleanup: secrets still set"
  [[ $(jq '[.orgs, .projects, .dbs, .keys | length] | add' "$FAKE_DIR/tb.json") == 0 ]] || fail "cleanup: e2e state left: $(cat "$FAKE_DIR/tb.json")"
  out=$(run cleanup e2e); contains "$out" "cleanup: no e2e tenant" cleanup-again
  rm -rf "$work"
}

test_bench_is_delegated_and_isolated() {
  setup
  local out
  out=$(run install bench || true)
  contains "$out" "terrascale#79" bench-missing
  printf '#!/usr/bin/env bash\necho "bench called with $*"\n' > "$work/repo/benchmark/scripts/repl-fly-tenant.sh"
  for command in "install bench:install" "revoke bench:revoke" "cleanup bench:cleanup"; do
    out=$(run ${command%%:*}); contains "$out" "bench called with ${command#*:}" "bench ${command%%:*}"
  done
  grep -q 'gh secret set TERRABASE_E2E' "$FAKE_LOG" && fail "bench touched the e2e secret"
  out=$(run install nothing || true); contains "$out" "usage:" usage
  rm -rf "$work"
}

test_dry_run_touches_nothing
test_install_status_rotate_revoke_e2e
test_cleanup_e2e_leaves_nothing
test_bench_is_delegated_and_isolated
test_missing_peer_credential_refuses_before_proxy
[[ $failures == 0 ]] || { echo "$failures failure(s)"; exit 1; }
echo "morning-credentials: 5 tests passed"
