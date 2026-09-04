#!/usr/bin/env bash
# E-13 indexed-query production-boundary family. Each adapter is an
# executable whose stdout is one JSON side document accepted by
# bench/src/indexed_query.rs. Missing/failed adapters remain unavailable and
# cause a non-zero exit; no local or synthetic success is possible.
#
# Required immutable inputs:
# TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST (64 lowercase hex)
# TERRABASE_BENCH_INDEXED_QUERY_TERRABASE_IMAGE
# TERRABASE_BENCH_INDEXED_QUERY_POSTGRESQL_JSONB_IMAGE
# TERRABASE_BENCH_INDEXED_QUERY_RAVENDB_IMAGE
# TERRABASE_BENCH_INDEXED_QUERY_RESOURCE_EVIDENCE (OCI JSON evidence file)
# Optional adapter paths end in _CMD and must be executable files.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT="$REPO_ROOT/bench/indexed-query.md"
EVIDENCE="${TERRABASE_BENCH_INDEXED_QUERY_RESOURCE_EVIDENCE:-}"
TOPOLOGY="${TERRABASE_BENCH_INDEXED_QUERY_TOPOLOGY:-}"

for arg in "$@"; do
  case "$arg" in
    --output=*) OUTPUT="$(printf '%s' "$arg" | cut -d= -f2-)" ;;
    --resource-evidence=*) EVIDENCE="$(printf '%s' "$arg" | cut -d= -f2-)" ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

immutable_image() {
  local image="$1"
  [[ "$image" != *latest* && "$image" != *localhost* && "$image" != *127.0.0.1* \
    && "$image" != *:local* && "$image" != *placeholder* ]] || return 1
  [[ "$image" =~ ^.+@sha256:[0-9a-f]{64}$ ]]
}

RUN_DIGEST="${TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST:-}"
TB_IMAGE="${TERRABASE_BENCH_INDEXED_QUERY_TERRABASE_IMAGE:-}"
PG_IMAGE="${TERRABASE_BENCH_INDEXED_QUERY_POSTGRESQL_JSONB_IMAGE:-}"
RAVEN_IMAGE="${TERRABASE_BENCH_INDEXED_QUERY_RAVENDB_IMAGE:-}"
PREFLIGHT_ERROR=""
if [[ ! "$RUN_DIGEST" =~ ^[0-9a-f]{64}$ ]]; then PREFLIGHT_ERROR="run digest is missing or not a lowercase SHA-256"; fi
if [[ "$TOPOLOGY" != single-node && "$TOPOLOGY" != rf2 ]]; then PREFLIGHT_ERROR="topology must be exactly single-node or rf2"; fi
for image in "$TB_IMAGE" "$PG_IMAGE" "$RAVEN_IMAGE"; do
  if ! immutable_image "$image"; then PREFLIGHT_ERROR="one or more images are not immutable @sha256 references"; break; fi
done
if [[ -z "$EVIDENCE" || ! -f "$EVIDENCE" ]]; then PREFLIGHT_ERROR="OCI launch-member resource evidence file is missing"; fi

WORK="$(mktemp -d /tmp/terrabase-indexed-query.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
declare -a VALIDATOR_ARGS=(--output "$OUTPUT")
[[ -n "$EVIDENCE" ]] && VALIDATOR_ARGS+=(--resource-evidence "$EVIDENCE")
mkdir -p "$(dirname "$OUTPUT")"

run_side() {
  local side="$1" image="$2" command_value="$3"
  local result="$WORK/$side.json"
  local variable
  case "$side" in
    terrabase) variable="TERRABASE_BENCH_INDEXED_QUERY_TERRABASE_CMD" ;;
    postgresql-jsonb) variable="TERRABASE_BENCH_INDEXED_QUERY_POSTGRESQL_JSONB_CMD" ;;
    ravendb) variable="TERRABASE_BENCH_INDEXED_QUERY_RAVENDB_CMD" ;;
    *) variable="adapter command" ;;
  esac
  if [[ -z "$command_value" ]]; then
    VALIDATOR_ARGS+=(--missing "$side=adapter unavailable ($variable is not set)")
    return 0
  fi
  if [[ -n "$PREFLIGHT_ERROR" ]]; then
    VALIDATOR_ARGS+=(--missing "$side=not launched: $PREFLIGHT_ERROR")
    return 0
  fi
  if [[ ! -x "$command_value" ]]; then
    VALIDATOR_ARGS+=(--missing "$side=adapter is not executable: $command_value")
    return 0
  fi
  echo "indexed-query: running $side adapter"
  if ! env TERRABASE_BENCH_INDEXED_QUERY_SIDE="$side" \
      TERRABASE_BENCH_INDEXED_QUERY_IMAGE="$image" \
      TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST="$RUN_DIGEST" \
      TERRABASE_BENCH_INDEXED_QUERY_TOPOLOGY="$TOPOLOGY" \
      TERRABASE_BENCH_INDEXED_QUERY_RESOURCE_EVIDENCE="$EVIDENCE" \
      timeout "2h" "$command_value" >"$result"; then
    VALIDATOR_ARGS+=(--missing "$side=adapter failed or timed out")
    return 0
  fi
  VALIDATOR_ARGS+=(--input "$side=$result")
}

run_side terrabase "$TB_IMAGE" "${TERRABASE_BENCH_INDEXED_QUERY_TERRABASE_CMD:-}"
run_side postgresql-jsonb "$PG_IMAGE" "${TERRABASE_BENCH_INDEXED_QUERY_POSTGRESQL_JSONB_CMD:-}"
run_side ravendb "$RAVEN_IMAGE" "${TERRABASE_BENCH_INDEXED_QUERY_RAVENDB_CMD:-}"
if [[ -n "$PREFLIGHT_ERROR" ]]; then VALIDATOR_ARGS+=(--missing "terrabase=not launched: $PREFLIGHT_ERROR"); fi

VALIDATOR="$WORK/indexed-query-validator"
echo "indexed-query: compiling validator with low priority"
nice -n 19 rustc --edition=2024 "$REPO_ROOT/bench/src/indexed_query.rs" -o "$VALIDATOR"
set +e
"$VALIDATOR" "${VALIDATOR_ARGS[@]}"
RESULT=$?
set -e
if [[ "$RESULT" -ne 0 ]]; then
  echo "indexed-query: failed closed; report written to $OUTPUT" >&2
  exit "$RESULT"
fi
echo "indexed-query: complete report written to $OUTPUT"
