#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STACK="$ROOT/scripts/stack.sh"

help="$($STACK --help)"
grep -Fq './scripts/stack.sh test' <<<"$help"
grep -Fq './scripts/stack.sh pre-engine' <<<"$help"

up="$(STACK_DRY_RUN=1 "$STACK" up)"
grep -Fq '3 real members' <<<"$up"
grep -Fq 'proxy Worker dev' <<<"$up"
grep -Fq 'dashboard workerd' <<<"$up"
grep -Fq 'webhook receiver' <<<"$up"
grep -Fq 'readiness probes include /live /startup /ready /internal/metrics' <<<"$up"
if grep -Fq 'SKIP' <<<"$up"; then
  echo 'full stack dry-run must not skip a component' >&2
  exit 1
fi
if grep -Fq 'stub origin' <<<"$up"; then
  echo 'full stack dry-run must never substitute the X-4 stub' >&2
  exit 1
fi

pre="$(STACK_DRY_RUN=1 "$STACK" pre-engine)"
grep -Fq 'X-4 stub' <<<"$pre"
grep -Fq 'go -C' <<<"$pre"

test_plan="$(STACK_DRY_RUN=1 "$STACK" test)"
grep -Fq 'P-3 journey' <<<"$test_plan"
grep -Fq 'X-6 qualification' <<<"$test_plan"

# The stack invokes the live journey in strict mode so missing implementation
# is a visible G1 diagnostic rather than an unconditional skip.
grep -Fq 'TERRASCALE_G1_STRICT' "$ROOT/../ts-typescript/test/journey.test.ts" || {
  echo 'journey must expose strict G1 diagnostics' >&2
  exit 1
}
grep -Fq 'TERRASCALE_E2E_REGION_BASE_URLS' "$STACK" || {
  echo 'stack test must exercise every local Region endpoint' >&2
  exit 1
}
grep -Fq 'TERRASCALE_DASHBOARD_BASE_URL' "$STACK" || {
  echo 'stack test must exercise the local Dashboard endpoint' >&2
  exit 1
}
grep -Fq 'nice -n 19 cargo build' "$STACK" || {
  echo 'stack Rust builds must run at reduced priority' >&2
  exit 1
}

make_plan="$(make -n stack stack-check stack-test stack-pre-engine stack-down)"
grep -Fq './scripts/stack.sh up' <<<"$make_plan"
grep -Fq './scripts/stack.sh check' <<<"$make_plan"
grep -Fq './scripts/stack.sh test' <<<"$make_plan"
grep -Fq './scripts/stack.sh pre-engine' <<<"$make_plan"
grep -Fq './scripts/stack.sh down' <<<"$make_plan"

echo 'stack config dry-run tests passed'
