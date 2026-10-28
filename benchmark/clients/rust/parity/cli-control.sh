#!/usr/bin/env bash
# Offline CLI controls for the built runner. Every case is rejected before any network call:
# credentials, output and the peers file are checked before a client is built or a request sent.
set -euo pipefail
bin=$(realpath "${1:?usage: cli-control.sh RUNNER}")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
unset TS_PEERS_FILE
# Synthetic, well-formed but never-issued key and project.
id=01ARZ3NDEKTSV4RRFFQ69G5FAV
printf '{"api_key":"%s_%s","project":"%s"}\n' "$id" "$(printf 'a%.0s' {1..52})" "$id" >"$work/credentials.json"

expect() { # CODE MESSAGE ARGS...
  local code=$1 message=$2 got=0
  shift 2
  "$bin" "$@" 2>"$work/stderr" || got=$?
  if [ "$got" != "$code" ] || ! grep -qF "$message" "$work/stderr"; then
    echo "FAIL ($got): $(cat "$work/stderr")" >&2
    exit 1
  fi
  echo "ok exit=$code $message"
}

creds=(--credentials "$work/credentials.json")
expect 2 "usage:"
expect 2 "usage:" --out "$work/a.jsonl" "${creds[@]}"
expect 1 "credentials could not be loaded" --credentials "$work/missing.json" --out "$work/b.jsonl"
[ ! -e "$work/b.jsonl" ]
printf 'kept\n' >"$work/existing.jsonl"
expect 1 "output must be a new writable file" "${creds[@]}" --out "$work/existing.jsonl"
[ "$(cat "$work/existing.jsonl")" = kept ]
expect 1 "TS_PEERS_FILE could not be read" "${creds[@]}" --out "$work/c.jsonl"
[ "$(stat -c %a "$work/c.jsonl")" = 600 ] && [ ! -s "$work/c.jsonl" ]
export TS_PEERS_FILE=$work/peers
printf 'one gru 100.64.0.1\ntwo sjc 100.64.0.2\n' >"$TS_PEERS_FILE"
expect 1 "a required serving region is absent" "${creds[@]}" --out "$work/d.jsonl"
printf 'one gru\n' >"$TS_PEERS_FILE"
expect 1 "peer catalog is malformed" "${creds[@]}" --out "$work/e.jsonl"
ln -s "$bin" "$work/runner"
bin=$work/runner expect 2 "usage:"
echo "cli controls passed"
