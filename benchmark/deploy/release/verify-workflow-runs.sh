#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: deploy/release/verify-workflow-runs.sh [--fixtures DIRECTORY] <candidate-run-id> <capability-run-id> <revision> <repository> <ref> <capability-workflow-id>" >&2
  exit 2
}
fixture_directory=
if [[ ${1-} == --fixtures ]]; then
  [[ $# -ge 2 ]] || usage
  fixture_directory=$2
  shift 2
fi
[[ $# -eq 6 ]] || usage
candidate_run_id=$1
capability_run_id=$2
expected_revision=$3
expected_repository=$4
expected_ref=$5
capability_workflow_id=$6

[[ "$candidate_run_id" =~ ^[0-9]+$ && "$capability_run_id" =~ ^[0-9]+$ ]] || {
  echo "workflow run IDs must be decimal integers" >&2
  exit 1
}
[[ "$candidate_run_id" != "$capability_run_id" ]] || {
  echo "candidate and capability workflow runs must be distinct" >&2
  exit 1
}
[[ "$expected_revision" =~ ^[0-9a-f]{40,64}$ ]] || {
  echo "expected revision is not a Git object ID" >&2
  exit 1
}
[[ "$expected_repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || {
  echo "expected repository is not an owner/name pair" >&2
  exit 1
}
[[ "$expected_ref" == refs/heads/main ]] || {
  echo "promotion is permitted only for refs/heads/main" >&2
  exit 1
}
[[ "$capability_workflow_id" == qualification.yml ]] || {
  echo "the trusted capability workflow must be qualification.yml" >&2
  exit 1
}
if [[ -z "$fixture_directory" ]]; then
  : "${FORGEJO_API_URL:?FORGEJO_API_URL is required; workflow provenance cannot be skipped}"
  : "${FORGEJO_API_TOKEN:?FORGEJO_API_TOKEN is required; workflow provenance cannot be skipped}"
  : "${FORGEJO_REPOSITORY:?FORGEJO_REPOSITORY is required; workflow provenance cannot be skipped}"
  [[ "$FORGEJO_REPOSITORY" == "$expected_repository" ]] || {
    echo "Forgejo repository context does not match the expected repository" >&2
    exit 1
  }
  command -v curl >/dev/null || {
    echo "curl is required to authenticate Forgejo workflow runs" >&2
    exit 1
  }
fi
command -v jq >/dev/null || {
  echo "jq is required to authenticate Forgejo workflow runs" >&2
  exit 1
}

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-workflow-runs.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-workflow-runs.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

api_root=${FORGEJO_API_URL-}
fetch_run() {
  local run_id=$1 output=$2
  if [[ -n "$fixture_directory" ]]; then
    [[ -f "$fixture_directory/$run_id.json" && ! -L "$fixture_directory/$run_id.json" ]] || {
      echo "workflow fixture is missing: $run_id" >&2
      exit 1
    }
    cp -- "$fixture_directory/$run_id.json" "$output"
  else
    curl --fail --silent --show-error --location \
      --header 'Accept: application/json' \
      --header "Authorization: token $FORGEJO_API_TOKEN" \
      "$api_root/repos/$FORGEJO_REPOSITORY/actions/runs/$run_id" >"$output"
  fi
  jq -e 'type == "object"' "$output" >/dev/null || {
    echo "Forgejo returned an invalid workflow run document" >&2
    exit 1
  }
}

candidate_json=$work/candidate.json
capability_json=$work/capability.json
fetch_run "$candidate_run_id" "$candidate_json"
fetch_run "$capability_run_id" "$capability_json"

# Forgejo's run document is the trusted source of workflow identity and
# conclusion. Artifact manifests remain useful consistency checks, but cannot
# substitute for this server-side admission.
jq -e \
  --argjson run_id "$candidate_run_id" \
  --arg repository "$expected_repository" \
  --arg revision "$expected_revision" \
  --arg ref "$expected_ref" \
  --arg branch "${expected_ref#refs/heads/}" \
  '
    .id == $run_id and
    .repository.full_name == $repository and
    (
      (
        .commit_sha == $revision and
        (.prettyref == $ref or .prettyref == $branch) and
        .event == "" and
        .trigger_event == "workflow_dispatch" and
        .status == "success" and
        .workflow_id == "release.yml"
      ) or
      (
        .head_repository.full_name == $repository and
        .head_branch == $branch and
        .head_sha == $revision and
        .event == "workflow_dispatch" and
        .status == "completed" and
        .conclusion == "success" and
        ([.workflow_id, .workflow_file] | map(select(. != null)) | length) >= 1 and
        ([.workflow_id, .workflow_file] | map(select(. != null)) | all(. == "release.yml")) and
        (.path == null or .path == ".forgejo/workflows/release.yml")
      )
    )
  ' "$candidate_json" >/dev/null || {
  echo "candidate workflow run is not a successful trusted main release run" >&2
  exit 1
}
jq -e \
  --argjson run_id "$capability_run_id" \
  --arg repository "$expected_repository" \
  --arg revision "$expected_revision" \
  --arg ref "$expected_ref" \
  --arg branch "${expected_ref#refs/heads/}" \
  --arg workflow "$capability_workflow_id" \
  '
    .id == $run_id and
    .repository.full_name == $repository and
    (
      (
        .commit_sha == $revision and
        (.prettyref == $ref or .prettyref == $branch) and
        .event == "" and
        .trigger_event == "workflow_dispatch" and
        .status == "success" and
        .workflow_id == $workflow
      ) or
      (
        .head_repository.full_name == $repository and
        .head_branch == $branch and
        .head_sha == $revision and
        .event == "workflow_dispatch" and
        .status == "completed" and
        .conclusion == "success" and
        ([.workflow_id, .workflow_file] | map(select(. != null)) | length) >= 1 and
        ([.workflow_id, .workflow_file] | map(select(. != null)) | all(. == $workflow)) and
        (.path == null or .path == (".forgejo/workflows/" + $workflow))
      )
    )
  ' "$capability_json" >/dev/null || {
  echo "capability workflow run is not the selected successful trusted qualification run" >&2
  exit 1
}

echo "authenticated candidate and capability workflow runs for $expected_repository@$expected_revision"
