#!/usr/bin/env bash
# The one place a HUMAN installs, inspects and revokes TerraScale's CI/benchmark credentials. Agents never run it.
# Every key is minted inside the Fly org and travels only through a pipe into its destination: never printed, logged or
# written to disk. The two credentials are isolated (own tenant, own key, own destination); each is installed separately.
#
#   morning-credentials.sh status          what is installed (secret NAMES only), service readiness, which tenants exist
#   morning-credentials.sh dry-run         every action, scope and destination; touches nothing
#   morning-credentials.sh install e2e     the Fly e2e suite's key -> GitHub Actions secret TERRABASE_E2E_FLY_API_KEY
#   morning-credentials.sh install bench   the Fly 1M benchmark's key -> Fly secret TS_BENCH_API_KEY on app ts-bench
#   morning-credentials.sh revoke e2e|bench
#   morning-credentials.sh cleanup bench   drop the benchmark's documents (bench-* collections)
#
# e2e:   scripts/e2e/install-ci-credential.sh (owned here).
# bench: benchmark/scripts/repl-fly-tenant.sh (owned by the benchmark owner; terrascale#79).
set -euo pipefail
shopt -s inherit_errexit
set +x

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
readonly e2e="$root/scripts/e2e/install-ci-credential.sh"
readonly bench="$root/benchmark/scripts/repl-fly-tenant.sh"
readonly REPO=TerraScale/terrascale

die() { echo "morning-credentials: $*" >&2; exit 1; }
usage() { die "usage: morning-credentials.sh status | dry-run | install e2e|bench | revoke e2e|bench | cleanup bench"; }

bench_script() {
  [[ -f $bench ]] || die "the benchmark credential script is not on this branch yet (benchmark/scripts/repl-fly-tenant.sh, terrascale#79)"
  echo "$bench"
}

readiness() {
  local region code body
  for region in gru sjc fra; do
    code=$(curl -s -o /dev/null -w '%{http_code}' -m 15 -H "fly-prefer-region: $region" https://ts-auth.fly.dev/readyz || true)
    body=$(curl -s -m 15 -H "fly-prefer-region: $region" https://ts-terrabase.fly.dev/healthz || true)
    echo "readiness $region: ts-auth /readyz $code; TerraBase $(jq -r '"\(.status // "down") (answered as \(.region // "?"))"' <<<"${body:-{\}}" 2> /dev/null || echo down)"
  done
}

status() {
  echo "GitHub Actions secrets on $REPO (names only):"
  gh secret list --repo "$REPO" --json name --jq '.[].name | select(startswith("TERRABASE_E2E_FLY") or startswith("TS_BENCH"))' | sed 's/^/  /'
  echo "GitHub variables (benchmark ids, not secret):"
  gh variable list --repo "$REPO" --json name --jq '.[].name | select(startswith("TS_BENCH_FLY"))' | sed 's/^/  /'
  readiness
  echo "e2e:"
  bash "$e2e" status | sed 's/^/  /'
  echo "bench:"
  if [[ -f $bench ]]; then bash "$bench" status | sed 's/^/  /'; else echo "  script not on this branch yet (terrascale#79)"; fi
}

dry_run() {
  echo "== e2e (Fly e2e suite, post-deploy job) =="
  bash "$e2e" dry-run
  echo "== bench (Fly 1M benchmark) =="
  if [[ -f $bench ]]; then bash "$bench" dry-run; else echo "script not on this branch yet (terrascale#79)"; fi
  echo "== preconditions =="
  readiness
  echo "dry run: nothing was changed"
}

case "${1:-} ${2:-}" in
  "status ") status ;;
  "dry-run ") dry_run ;;
  "install e2e") bash "$e2e" install ;;
  "install bench") bash "$(bench_script)" install ;;
  "revoke e2e") bash "$e2e" revoke ;;
  "revoke bench") bash "$(bench_script)" revoke ;;
  "cleanup bench") bash "$(bench_script)" cleanup ;;
  *) usage ;;
esac
