#!/usr/bin/env bash
set -euo pipefail

fixture_only=false
case $# in
  0) ;;
  1)
    if [[ "$1" == '--fixture-only' ]]; then
      fixture_only=true
    else
      printf 'usage: %s [--fixture-only]\n' "$0" >&2
      exit 64
    fi
    ;;
  *)
    printf 'usage: %s [--fixture-only]\n' "$0" >&2
    exit 64
    ;;
esac

release=.forgejo/workflows/release.yml
promote=.forgejo/workflows/promote.yml
ci=.forgejo/workflows/ci.yml

# This digest is the reviewed-tree trust boundary for the CI shell surface.
# Semantic checks below document the required invariants, while byte identity
# prevents an additional or obfuscated shell command from escaping them.
readonly reviewed_ci_sha256='24c7f9d826711e3d0122408a938f37c881b63471b67494c936b52429efb525f5'

validate_reviewed_ci_bytes() {
  local actual_sha256
  actual_sha256="$(sha256sum "$1")"
  [[ "${actual_sha256%% *}" == "$reviewed_ci_sha256" ]]
}

validate_reviewed_ci_bytes "$ci"

validate_workflow_inventory() {
  local workflow_dir=$1
  local workflow_entry_count

  [[ -d "$workflow_dir" && ! -L "$workflow_dir" ]]
  workflow_entry_count="$(
    find "$workflow_dir" -mindepth 1 -maxdepth 1 -printf . | wc -c
  )"
  [[ "$workflow_entry_count" -eq 3 ]]

  local workflow
  for workflow in "$ci" "$release" "$promote"; do
    [[ -f "$workflow" && ! -L "$workflow" ]]
  done
}

validate_workflow_inventory .forgejo/workflows

inject_checks_run_step() {
  local bypass_command="$1"
  local injected=false
  local line

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$injected" == false && "$line" == '      - name: Test AWS-LC FIPS selection' ]]; then
      printf '%s\n' \
        '      - name: Rejected workspace-test bypass' \
        '        run: |' \
        "          $bypass_command"
      injected=true
    fi
    printf '%s\n' "$line"
  done <"$ci"
  [[ "$injected" == true ]]
}

mutate_canonical_workspace_test() {
  local changed=false
  local line

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$changed" == false && "$line" == '          cargo test --workspace --locked -- \' ]]; then
      printf '%s\n' '          cargo  test --workspace --locked -- \'
      changed=true
    else
      printf '%s\n' "$line"
    fi
  done <"$ci"
  [[ "$changed" == true ]]
}

add_trigger_event() {
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == 'permissions:' ]]; then
      printf '%s\n' '  pull_request: {}'
    fi
    printf '%s\n' "$line"
  done <"$ci"
}

add_trigger_filter() {
  local added=false
  local line
  while IFS= read -r line || [[ -n "$line" ]]; do
    printf '%s\n' "$line"
    if [[ "$added" == false && "$line" == '    branches: [develop, main]' ]]; then
      printf '%s\n' "    tags: ['*']"
      added=true
    fi
  done <"$ci"
  [[ "$added" == true ]]
}

add_yaml_document() {
  while IFS= read -r line || [[ -n "$line" ]]; do
    printf '%s\n' "$line"
  done <"$ci"
  printf '%s\n' '---' 'name: rejected-second-document'
}

workspace_test_bypasses=(
  'cargo  test --workspace --locked'
  $'cargo\ttest --workspace --locked'
  '"cargo" test --workspace --locked'
  'c\argo test --workspace --locked'
  'cargo "test" --workspace --locked'
  'cargo t\est --workspace --locked'
  'command cargo test --workspace --locked'
  'env cargo test --workspace --locked'
)
for workspace_test_bypass in "${workspace_test_bypasses[@]}"; do
  yq eval '.' <(inject_checks_run_step "$workspace_test_bypass") >/dev/null
  ! validate_reviewed_ci_bytes <(inject_checks_run_step "$workspace_test_bypass")
done

yq eval '.' <(mutate_canonical_workspace_test) >/dev/null
! validate_reviewed_ci_bytes <(mutate_canonical_workspace_test)
yq eval '.' <(add_trigger_event) >/dev/null
! validate_reviewed_ci_bytes <(add_trigger_event)
yq eval '.' <(add_trigger_filter) >/dev/null
! validate_reviewed_ci_bytes <(add_trigger_filter)
yq eval-all '.' <(add_yaml_document) >/dev/null
! validate_reviewed_ci_bytes <(add_yaml_document)

legacy_language_files="$({
  find . -type f \
    \( -name '*.cs' -o -name '*.csproj' -o -name '*.sln' \) \
    -not -path './.git/*' \
    -not -path './target/*'
} | sort)"
if [[ -n "$legacy_language_files" ]]; then
  printf 'retired language artifacts are forbidden:\n%s\n' "$legacy_language_files" >&2
  exit 1
fi

outside_go_files="$({
  find . -type f -name '*.go' \
    -not -path './.git/*' \
    -not -path './target/*' \
    -not -path './terrabase-go/*' \
    -not \( -path './.claude/worktrees/*' -a -exec git check-ignore -q -- {} \; \) \
    -print
} | sort)"
if [[ -n "$outside_go_files" ]]; then
  printf 'Go source is allowed only under terrabase-go:\n%s\n' "$outside_go_files" >&2
  exit 1
fi

retired_protocol_files="$({
  find . -type f \
    \( -iname '*messagepack*' -o -iname '*msgpack*' -o -iname '*websocket*' -o -iname '*flatbuffer*' \) \
    -not -path './.git/*' \
    -not -path './target/*'
} | sort)"
if [[ -n "$retired_protocol_files" ]]; then
  printf 'retired protocol artifacts are forbidden:\n%s\n' "$retired_protocol_files" >&2
  exit 1
fi

[[ $(grep -Fc 'name: terrabase-unsigned-amd64' "$release") -eq 1 ]]
[[ $(grep -Fc 'name: terrabase-unsigned-arm64' "$release") -eq 1 ]]
[[ $(grep -Fc 'name: terrabase-unsigned-amd64' "$promote") -eq 1 ]]
[[ $(grep -Fc 'name: terrabase-unsigned-arm64' "$promote") -eq 1 ]]
[[ $(grep -Fc 'name: terrabase-production-capability' "$promote") -eq 2 ]]
! grep -F 'name: terrabase-unsigned-' "$release" "$promote" | grep -Fq '${{ forgejo.sha }}'
! grep -F 'name: terrabase-production-capability' "$promote" | grep -Fq '${{ forgejo.sha }}'
[[ $(grep -Fc 'run-id: ${{ inputs.candidate_run_id }}' "$promote") -eq 2 ]]
[[ $(grep -Fc 'run-id: ${{ inputs.capability_run_id }}' "$promote") -eq 2 ]]
[[ $(grep -Fc 'deploy/release/verify-source-binding.sh' "$promote") -eq 2 ]]
[[ $(grep -Fc 'deploy/release/require-fips-toolchain.sh' "$release") -eq 2 ]]
grep -Fq '"$script_dir/require-fips-toolchain.sh"' deploy/release/package.sh
grep -Fq 'build_arguments+=(--no-default-features --features production-fips)' deploy/release/package.sh
grep -Fq 'terrabase feature "production-fips"' deploy/release/package.sh
grep -Fq 'aws-lc-rs feature "fips"' deploy/release/package.sh
grep -Fq 'terrabase feature "local-crypto"' deploy/release/package.sh
grep -Fq 'aws-lc-rs feature "non-fips"' deploy/release/package.sh
grep -Fq 'fipsFeatureGraph' deploy/release/package.sh
grep -Fq 'fipsFeatureGraph' deploy/release/verify.sh
! grep -Fq -- '--all-features' deploy/release/package.sh

! grep -Fq 'GITHUB_BASE_REF' "$ci"
! grep -Fq 'GITHUB_EVENT_NAME' "$ci"
grep -Fq 'git rev-parse --verify HEAD^' "$ci"
grep -Fq 'buf lint' "$ci"
grep -Fq 'buf breaking --against ".git#ref=$against_commit"' "$ci"
grep -Fq 'buf generate --template buf.gen.yaml' "$ci"
grep -Fq 'schema_digest=' "$ci"
[[ $(grep -Fc 'scripts/normalize-generated-protoc-version.sh' "$ci") -eq 2 ]]
grep -Fq 'engine.pb.go.normalized' "$ci"
grep -Fq 'engine_grpc.pb.go.normalized' "$ci"
grep -Fq 'deploy/release/test-boundary.sh' "$ci"
grep -Fq 'scripts/validate-studio-assets.sh' "$ci"
grep -Fq 'cargo test --workspace --locked --release' "$ci"
[[ $(grep -Fc 'cargo test --workspace' "$ci") -eq 2 ]]

expected_dev_workspace_test="$(printf '%s\n' \
  'cargo test --workspace --locked -- \' \
  '  --skip benchmarks::lane_run::tests::probe_lane_operation_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_read_subcall_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_write_durability_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_concurrent_synced_writes')"
expected_release_workspace_test="$(printf '%s\n' \
  'cargo test --workspace --locked --release -- \' \
  '  --skip benchmarks::lane_run::tests::probe_lane_operation_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_read_subcall_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_write_durability_costs \' \
  '  --skip benchmarks::lane_run::tests::probe_concurrent_synced_writes')"

validate_workspace_test_command() {
  [[ "$1" == "$2" ]]
}

invalid_skip_suffixes=(
  '  --skip benchmarks::'
  '  --skip=benchmarks::'
  '  --skip benchmarks'
  '  --skip=benchmarks'
  '  --skip bench'
  '  --skip=bench'
  '  --skip correctness::critical_test'
  '  --skip=correctness::critical_test'
  "  '--skip' correctness::critical_test"
  '  "--skip" correctness::critical_test'
  '  --s\kip correctness::critical_test'
  '  --skip benchmarks::lane_run::tests::probe_lane_operation_costs'
)

for test_step_name in 'Test (dev profile)' 'Test'; do
  case "$test_step_name" in
    'Test (dev profile)')
      expected_workspace_test="$expected_dev_workspace_test"
      unfiltered_workspace_test='cargo test --workspace --locked'
      ;;
    'Test')
      expected_workspace_test="$expected_release_workspace_test"
      unfiltered_workspace_test='cargo test --workspace --locked --release'
      ;;
  esac

  yq eval --exit-status \
    "([.jobs.checks.steps[] | select(.name == \"$test_step_name\")] | length) == 1" \
    "$ci" >/dev/null
  workspace_test="$(yq eval -r \
    ".jobs.checks.steps[] | select(.name == \"$test_step_name\") | .run" "$ci")"
  validate_workspace_test_command "$workspace_test" "$expected_workspace_test"

  for invalid_skip_suffix in "${invalid_skip_suffixes[@]}"; do
    ! validate_workspace_test_command \
      "$workspace_test"$'\n'"$invalid_skip_suffix" \
      "$expected_workspace_test"
  done

  commented_expected="$(sed 's/^/# /' <<<"$expected_workspace_test")"
  ! validate_workspace_test_command \
    "$unfiltered_workspace_test"$'\n'"$commented_expected" \
    "$expected_workspace_test"
done
grep -Fq 'cargo clippy --workspace --all-targets --locked -- -D warnings' "$ci"
grep -Fq 'cargo doc --workspace --no-deps --locked' "$ci"
grep -Fq 'cargo check --workspace --all-targets --locked' "$ci"
grep -Fq 'cargo check --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'cargo test --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'cargo clippy --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'cargo run --manifest-path bench/Cargo.toml --locked -- --render-history' "$ci"
grep -Fq 'git diff --exit-code -- docs/evidence/benchmarks/benchmark-history.md' "$ci"
grep -Fq 'cargo fmt --manifest-path crates/terrabase-studio/Cargo.toml -- --check' "$ci"
grep -Fq 'cargo test --manifest-path crates/terrabase-studio/Cargo.toml --locked' "$ci"
grep -Fq 'cargo clippy --manifest-path crates/terrabase-studio/Cargo.toml --all-targets --locked -- -D warnings' "$ci"
grep -Fq 'cargo doc --manifest-path crates/terrabase-studio/Cargo.toml --no-deps --locked' "$ci"
grep -Fq 'cargo deny --all-features --locked check' "$ci"
grep -Fq 'cargo deny --manifest-path bench/Cargo.toml --config bench/deny.toml --locked check' "$ci"
grep -Fq 'cargo deny --manifest-path crates/terrabase-studio/Cargo.toml --config crates/terrabase-studio/deny.toml --locked check' "$ci"
grep -Fq 'cargo test --manifest-path deploy/release/sbom/Cargo.toml --locked' "$ci"
grep -Fq 'cargo clippy --manifest-path deploy/release/sbom/Cargo.toml --all-targets --locked -- -D warnings' "$ci"
grep -Fq 'cargo doc --manifest-path deploy/release/sbom/Cargo.toml --no-deps --locked' "$ci"
grep -Fq 'deploy/release/test-sbom.sh' "$ci"
grep -Fq 'gofmt -d -e' "$ci"
grep -Fq 'go vet -mod=readonly ./...' "$ci"
grep -Fq 'go install github.com/mikefarah/yq/v4@v4.45.1' "$ci"
! grep -Fq '.git#branch=${GITHUB_REF_NAME}' "$ci"
validate_trunk_ci_triggers() {
  local workflow
  workflow="$(<"$1")"

  yq eval-all --exit-status \
    '([documentIndex] | length) == 1' <<<"$workflow" >/dev/null 2>&1 &&
    yq eval --exit-status '
      (. | type) == "!!map" and
      ([. | keys | .[] | select(. == "on")] | length) == 1 and
      (.on | type) == "!!map" and
      (.on | length) == 1 and
      (.on | has("push")) and
      (.on.push | type) == "!!map" and
      (.on.push | length) == 1 and
      (.on.push | has("branches")) and
      (.on.push.branches | type) == "!!seq" and
      (.on.push.branches | length) == 2 and
      .on.push.branches[0] == "develop" and
      .on.push.branches[1] == "main"
    ' <<<"$workflow" >/dev/null 2>&1
}

validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  # pull_request: {}' \
  '  push:' \
  '    branches: [develop, main]' \
  'env:' \
  '  CONTRACT_TEXT: "pull_request: {}"' \
  '  TARGET_TEXT: "pull_request_target: {}"')
validate_trunk_ci_triggers <(printf '%s\n' \
  'on: {push: {branches: [develop, main]}}')
for extra_event in pull_request pull_request_target schedule workflow_dispatch; do
  ! validate_trunk_ci_triggers <(printf '%s\n' \
    'on:' \
    '  push:' \
    '    branches: [develop, main]' \
    "  ${extra_event}: {}")
done
for extra_push_filter in tags tags-ignore paths paths-ignore; do
  ! validate_trunk_ci_triggers <(printf '%s\n' \
    'on:' \
    '  push:' \
    '    branches: [develop, main]' \
    "    ${extra_push_filter}: ['*']")
done
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  "'on':" \
  '  push:' \
  '    branches: [develop, main]')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  push:' \
  '    branches: [develop, main]')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '---' \
  'name: hidden-second-document')
validate_trunk_ci_triggers "$ci"

yq eval --exit-status '
  .env.CC == "gcc" and
  .env.CXX == "g++" and
  .jobs.checks."runs-on" == "terrascale-builder" and
  .jobs."go-client"."runs-on" == "terrascale-builder" and
  ([.jobs.checks.steps[] | select(.name == "Select explicit GCC host linker")] | length) == 1
' "$ci" >/dev/null
gcc_linker_step="$(yq eval -r '.jobs.checks.steps[] | select(.name == "Select explicit GCC host linker") | .run' "$ci")"
grep -Fq 'rustc -vV' <<<"$gcc_linker_step"
grep -Fq 's/^host: //p' <<<"$gcc_linker_step"
grep -Fq 'CARGO_TARGET_$(printf' <<<"$gcc_linker_step"
grep -Fq "LC_ALL=C tr '[:lower:]-' '[:upper:]_'" <<<"$gcc_linker_step"
grep -Fq '%s=gcc\n' <<<"$gcc_linker_step"
grep -Fq '"$linker_env"' <<<"$gcc_linker_step"
grep -Fq '"$GITHUB_ENV"' <<<"$gcc_linker_step"
! grep -Eq 'CARGO_TARGET_(X86_64|AARCH64|ARM|RISCV|S390X|POWERPC)[[:alnum:]_]*_LINKER' <<<"$gcc_linker_step"
grep -Fq 'go test -mod=readonly -race ./...' "$ci"
! grep -Fq 'python3' "$ci" deploy/release/package.sh deploy/release/reproduce.sh

if [[ "$fixture_only" == false ]]; then
  bash scripts/test-validate-release-contracts.sh
fi

echo "validated immutable promotion and protobuf baseline contracts"
