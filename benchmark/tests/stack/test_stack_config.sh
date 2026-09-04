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

make_plan="$(make -n stack stack-check stack-test stack-pre-engine stack-down)"
grep -Fq './scripts/stack.sh up' <<<"$make_plan"
grep -Fq './scripts/stack.sh check' <<<"$make_plan"
grep -Fq './scripts/stack.sh test' <<<"$make_plan"
grep -Fq './scripts/stack.sh pre-engine' <<<"$make_plan"
grep -Fq './scripts/stack.sh down' <<<"$make_plan"

echo 'stack config dry-run tests passed'
