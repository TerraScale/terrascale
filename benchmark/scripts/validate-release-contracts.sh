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
generated_header_normalizer=scripts/normalize-generated-protoc-version.sh
generated_header_helper=terrabase-go/cmd/normalize-generated-protoc-version/main.go

# This digest is the reviewed-tree trust boundary for the CI shell surface.
# Semantic checks below document the required invariants, while byte identity
# prevents an additional or obfuscated shell command from escaping them.
readonly reviewed_ci_sha256='cd9daeb15df9c1345640892ec491011ecaa1c8acf85fb7983529ff32caf0ab33'
readonly reviewed_generated_header_normalizer_sha256='3b5d9c2023949c23b03e3513a1c640d54427e9b177f0ed5624c5931606b81dc0'
readonly reviewed_generated_header_helper_sha256='64d72dec26ab1a019ffbbb75d87166ad049edacf6f744fc423317d878a3aad84'

validate_reviewed_ci_bytes() {
  local actual_sha256
  actual_sha256="$(sha256sum "$1")"
  [[ "${actual_sha256%% *}" == "$reviewed_ci_sha256" ]]
}

validate_reviewed_ci_bytes "$ci"

validate_reviewed_generated_header_normalizer_bytes() {
  local actual_sha256
  [[ -f "$1" && ! -L "$1" ]]
  actual_sha256="$(sha256sum "$1")"
  [[ "${actual_sha256%% *}" == "$reviewed_generated_header_normalizer_sha256" ]]
}

validate_reviewed_generated_header_normalizer_bytes "$generated_header_normalizer"

validate_reviewed_generated_header_helper_bytes() {
  local actual_sha256
  [[ -f "$1" && ! -L "$1" ]]
  actual_sha256="$(sha256sum "$1")"
  [[ "${actual_sha256%% *}" == "$reviewed_generated_header_helper_sha256" ]]
}

validate_reviewed_generated_header_helper_bytes "$generated_header_helper"

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

validate_job_checkout_binding() {
  local job=$1
  local checkout_count
  local checkout_ref_count
  local binding_count
  local binding_step_name
  local binding_run
  case "$job" in
    checks) binding_step_name='Verify checks checkout revision' ;;
    go-client) binding_step_name='Verify Go client checkout revision' ;;
    *) return 1 ;;
  esac
  checkout_count="$(YQ_JOB="$job" yq -r '
    [.jobs[env(YQ_JOB)].steps[] | select(.uses == "https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803")] | length
  ' "$ci")"
  checkout_ref_count="$(YQ_JOB="$job" yq -r '
    [.jobs[env(YQ_JOB)].steps[] | select(.uses == "https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803") | select(.with.ref == "${{ forgejo.sha }}")] | length
  ' "$ci")"
  binding_count="$(YQ_JOB="$job" YQ_BINDING_NAME="$binding_step_name" yq -r '
    [.jobs[env(YQ_JOB)].steps[] | select(.name == env(YQ_BINDING_NAME))] | length
  ' "$ci")"
  binding_run="$(YQ_JOB="$job" YQ_BINDING_NAME="$binding_step_name" yq -r '
    .jobs[env(YQ_JOB)].steps[] | select(.name == env(YQ_BINDING_NAME)) | .run
  ' "$ci")"
  [[ "$checkout_count" -eq 1 ]]
  [[ "$checkout_ref_count" -eq 1 ]]
  [[ "$binding_count" -eq 1 ]]
  grep -Fq "event_sha='\${{ forgejo.sha }}'" <<<"$binding_run"
  grep -Fq 'checked_out_head="$(git rev-parse --verify HEAD)"' <<<"$binding_run"
  grep -Fq '[[ "$event_sha" == "$checked_out_head" ]]' <<<"$binding_run"
}

validate_job_checkout_binding checks
validate_job_checkout_binding go-client

validate_fips_perl_bootstrap() {
  local perl_step_count perl_step_index first_fips_index fips_step_count fips_step_index
  local perl_run perl_expected fips_run fips_expected
  perl_step_count="$(yq -r '[.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS")] | length' "$ci")"
  fips_step_count="$(yq -r '[.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS")] | length' "$ci")"
  [[ "$perl_step_count" -eq 1 && "$fips_step_count" -eq 1 ]]
  [[ "$(yq -o=json -I=0 '.env' "$ci")" == '{"CC":"gcc","CXX":"g++","CARGO_TERM_COLOR":"always","CARGO_INCREMENTAL":0,"RUST_BACKTRACE":1,"RUSTDOCFLAGS":"-D warnings"}' ]]
  yq eval --exit-status '(.jobs.checks | has("env")) | not' "$ci" >/dev/null
  yq eval --exit-status '(.jobs.checks | has("if")) | not' "$ci" >/dev/null
  yq eval --exit-status '(.jobs.checks | has("continue-on-error")) | not' "$ci" >/dev/null
  [[ "$(yq -o=json -I=0 '.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS") | keys | sort' "$ci")" == '["name","run"]' ]]
  [[ "$(yq -o=json -I=0 '.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | keys | sort' "$ci")" == '["name","run"]' ]]
  perl_step_index="$(yq -r '.jobs.checks.steps | to_entries[] | select(.value.name == "Install pinned Perl for AWS-LC FIPS") | .key' "$ci")"
  fips_step_index="$(yq -r '.jobs.checks.steps | to_entries[] | select(.value.name == "Build production server with AWS-LC FIPS") | .key' "$ci")"
  first_fips_index="$(yq -r '.jobs.checks.steps | to_entries | [.[] | select((.value.run // "") | contains("production-fips"))][0].key' "$ci")"
  [[ "$perl_step_index" -lt "$fips_step_index" && "$fips_step_index" -eq "$first_fips_index" ]]

  perl_run="$(yq -r '.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS") | .run' "$ci")"
  perl_expected="$(cat <<'EOF'
set -euo pipefail
readonly perl_flake='github:NixOS/nixpkgs/c5c4a43b0e8056328ec4529f735cabdb8f1942bb?narHash=sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei%2FuiU9vrMqOiwWP1Y%3D'
readonly perl_link="$GITHUB_WORKSPACE/.ci-tools/perl"
[[ "$(uname -m)" == x86_64 ]]
install -d -m 0755 "$(dirname "$perl_link")"
nix build --out-link "$perl_link" "$perl_flake#perl^out"
readonly perl_root="$(readlink -f "$perl_link")"
[[ "$perl_root" == /nix/store/*-perl-5.42.0 ]]
"$perl_root/bin/perl" -e 'die "unexpected Perl $^V\n" unless $^V eq v5.42.0'
printf '%s\n' "$perl_root/bin" >> "$GITHUB_PATH"
EOF
)"
  [[ "$perl_run" == "$perl_expected" ]]

  fips_run="$(yq -r '.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run' "$ci")"
  fips_expected="$(cat <<'EOF'
set -euo pipefail
readonly perl_root=/nix/store/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5.42.0
readonly fips_target_dir="$(mktemp -d "${RUNNER_TEMP:?}/terrabase-fips.XXXXXX")"
trap 'rm -rf -- "$fips_target_dir"' EXIT
[[ "$(readlink -f "$GITHUB_WORKSPACE/.ci-tools/perl")" == "$perl_root" ]]
[[ -x "$perl_root/bin/perl" ]]
export PATH="$perl_root/bin:$PATH"
export PERL="$perl_root/bin/perl"
export CARGO_TARGET_DIR="$fips_target_dir"
unset PERL5OPT PERL5LIB PERLLIB
[[ "$(type -P perl)" == "$perl_root/bin/perl" ]]
[[ "$(readlink -f "$(type -P perl)")" == "$perl_root/bin/perl" ]]
deploy/release/require-fips-toolchain.sh
cargo build -p terrabase-server --locked --release --no-default-features --features production-fips
mapfile -d '' perl_caches < <(find "$fips_target_dir/release/build" -type f -path '*/aws-lc-fips-sys-*/out/build/CMakeCache.txt' -print0)
[[ "${#perl_caches[@]}" -eq 1 ]]
grep -Fxq "PERL_EXECUTABLE:FILEPATH=$perl_root/bin/perl" "${perl_caches[0]}"
cargo test -p terrabase --locked --no-default-features --features production-fips --test encryption production_profile_links_an_operational_aws_lc_fips_module --exact
cargo test -p terrabase-server --locked --no-default-features --features production-fips
cargo test -p terrabase-member-transport --locked --no-default-features --features production-fips
cargo test -p terrabase-tbep --locked --no-default-features --features production-fips
cargo clippy -p terrabase --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-member-transport --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-tbep --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-server --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo doc -p terrabase-member-transport -p terrabase-tbep -p terrabase-server --no-deps --locked --no-default-features --features production-fips
cargo test --manifest-path crates/terrabase-studio/Cargo.toml --locked --no-default-features --features production-fips
cargo clippy --manifest-path crates/terrabase-studio/Cargo.toml --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo doc --manifest-path crates/terrabase-studio/Cargo.toml --no-deps --locked --no-default-features --features production-fips
EOF
)"
  [[ "$fips_run" == "$fips_expected" ]]
}

validate_fips_perl_bootstrap

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

weaken_generated_header_normalization() {
  local changed=false
  local line

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$changed" == false && \
      "$line" == '          scripts/normalize-generated-protoc-version.sh "$direct_dir"' ]]; then
      printf '%s\n' "$line || true"
      changed=true
    else
      printf '%s\n' "$line"
    fi
  done <"$ci"
  [[ "$changed" == true ]]
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
yq eval '.' <(weaken_generated_header_normalization) >/dev/null
! validate_reviewed_ci_bytes <(weaken_generated_header_normalization)

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
[[ $(grep -Fc "readonly perl_flake='github:NixOS/nixpkgs/c5c4a43b0e8056328ec4529f735cabdb8f1942bb?narHash=sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei%2FuiU9vrMqOiwWP1Y%3D'" "$ci") -eq 1 ]]
[[ $(grep -Fc 'nix build --out-link "$perl_link" "$perl_flake#perl^out"' "$ci") -eq 1 ]]
[[ $(grep -Fc 'readonly perl_root="$(readlink -f "$perl_link")"' "$ci") -eq 1 ]]
[[ $(grep -Fc 'unless $^V eq v5.42.0' "$ci") -eq 1 ]]
[[ $(grep -Fc 'deploy/release/require-fips-toolchain.sh' "$ci") -eq 2 ]]
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
grep -Fq 'go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@v1.6.2' "$ci"
! grep -Fq 'grpc_generator_version' "$ci"
grep -Fq 'schema_digest=' "$ci"
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

yq eval --exit-status '
  ([.jobs.checks.steps[] | select(.name == "Schema and generated-binding drift")] | length) == 1
' "$ci" >/dev/null
schema_generation_step="$(yq eval -r '
  .jobs.checks.steps[] |
  select(.name == "Schema and generated-binding drift") |
  .run
' "$ci")"
grep -Fq 'direct_dir="$schema_temp_root/direct"' <<<"$schema_generation_step"
grep -Fq 'buf_dir="$schema_temp_root/buf"' <<<"$schema_generation_step"
grep -Fq -- '--output "$buf_dir"' <<<"$schema_generation_step"
grep -Fq -- '--path crates/terrabase-tbep/proto/terrabase/v1/engine.proto' \
  <<<"$schema_generation_step"
grep -Fq -- '--path crates/terrabase-tbep/proto/terrabase/v1/family.proto' \
  <<<"$schema_generation_step"
grep -Fq 'crates/terrabase-tbep/proto/terrabase/v1/family.proto' \
  <<<"$schema_generation_step"
[[ $(grep -Fc 'scripts/normalize-generated-protoc-version.sh ' \
  <<<"$schema_generation_step") -eq 2 ]]
grep -Fq 'scripts/normalize-generated-protoc-version.sh "$direct_dir"' \
  <<<"$schema_generation_step"
grep -Fq 'scripts/normalize-generated-protoc-version.sh "$buf_dir/terrabase-go"' \
  <<<"$schema_generation_step"
grep -Fq 'expected_generated_inventory="$(printf' <<<"$schema_generation_step"
grep -Fq 'engine.pb.go engine_grpc.pb.go family.pb.go' <<<"$schema_generation_step"
grep -Fq 'terrabase-go/terrabase/v1' <<<"$schema_generation_step"
[[ $(grep -Fc 'diff -u "$direct_dir/terrabase/v1/$generated_file"' \
  <<<"$schema_generation_step") -eq 2 ]]
grep -Fq 'schema_temp_root="$(mktemp -d)"' <<<"$schema_generation_step"
grep -Fq 'trap cleanup_schema_temp EXIT' <<<"$schema_generation_step"
! grep -Fq '|| true' <<<"$schema_generation_step"

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
if grep -Fq 'python3' "$ci" deploy/release/package.sh deploy/release/reproduce.sh; then
  printf 'Python helpers are forbidden in the CI and release shell surface\n' >&2
  exit 1
fi

if [[ "$fixture_only" == false ]]; then
  bash scripts/test-validate-release-contracts.sh
fi

echo "validated immutable promotion and protobuf baseline contracts"
