#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)

subject_hits_storage_risk() {
  local lower
  lower=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  [[ $lower =~ (^|[^a-z0-9])(corruption|wal|panic|cve)([^a-z0-9]|$) ]]
}

if [[ ${1:-} == --self-test ]]; then
  fail=0
  expect() {
    local want=$1 subject=$2 got=miss
    subject_hits_storage_risk "$subject" && got=hit || true
    if [[ $got != "$want" ]]; then
      printf 'FAIL %s wanted %s got %s\n' "$subject" "$want" "$got" >&2
      fail=1
    fi
  }
  expect hit 'Fix WAL corruption on checkpoint'
  expect hit 'panic while replaying frames'
  expect hit 'CVE-2025-1234 in pager'
  expect hit 'page corruption after restart'
  expect miss 'Add swallow benchmark'
  expect miss 'Refresh wallpaper colors'
  expect miss 'Update changelog'
  [[ $fail -eq 0 ]]
  printf 'filter_fixture_subjects ok\n'
  exit 0
fi

source "$root/benchmark/turso-sync-host/turso-pin.env"
[[ $TURSO_COMMIT =~ ^[0-9a-f]{40}$ ]]
token=${GH_TOKEN:-${GITHUB_TOKEN:-}}
hdr=(-H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28')
if [[ -n $token ]]; then
  hdr+=(-H "Authorization: Bearer $token")
fi
page=1
hits=()
while [[ $page -le 40 ]]; do
  body=$(mktemp)
  curl -fsSL "${hdr[@]}" "https://api.github.com/repos/tursodatabase/turso/compare/${TURSO_COMMIT}...main?per_page=100&page=${page}" -o "$body"
  count=$(jq 'if type == "object" and (.commits | type) == "array" then (.commits | length) else -1 end' "$body")
  if [[ $count -lt 0 ]]; then
    jq -r '.message // "compare request failed"' "$body" >&2
    rm -f "$body"
    exit 1
  fi
  if [[ $count -eq 0 ]]; then
    rm -f "$body"
    break
  fi
  while IFS= read -r subject; do
    if subject_hits_storage_risk "$subject"; then
      hits+=("$subject")
    fi
  done < <(jq -r '.commits[].commit.message | split("\n")[0]' "$body")
  rm -f "$body"
  page=$((page + 1))
done
if ((${#hits[@]} > 0)); then
  printf '%s\n' "${hits[@]}"
  exit 1
fi
printf 'no storage-risk commits since %s\n' "$TURSO_COMMIT"
