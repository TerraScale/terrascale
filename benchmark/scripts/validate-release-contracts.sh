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
readonly reviewed_ci_sha256='cf393494869b278a7e3b614770aaddb59bb8ace37e7af4530d030eb36fefdfe6'
readonly reviewed_ci_semantic_sha256='991382d4daa666075f0d33d6455982887618a6a2d049a88085d49f64b1720d79'
readonly reviewed_release_sha256='0ae5067f7c13c03d9c3d7f87bc1dd283890a95419bb31bd94513e3379cdf0aff'
readonly reviewed_release_semantic_sha256='0bba8cfe324359121ba54e24d575c379428b9b877f65c951254cfb75c38d30cb'
readonly reviewed_promote_sha256='0f0e3f05ab10ae45b5480a48b8c76f8d8b4c2b5fc189184b828d28e6265fdae7'
readonly reviewed_promote_semantic_sha256='c0a4f9c933abb9112ef7bfe44106b6efcfb7504420aa59f5df3e380334d6143a'
readonly reviewed_generated_header_normalizer_sha256='3b5d9c2023949c23b03e3513a1c640d54427e9b177f0ed5624c5931606b81dc0'
readonly reviewed_generated_header_helper_sha256='64d72dec26ab1a019ffbbb75d87166ad049edacf6f744fc423317d878a3aad84'
readonly reviewed_rust_test_runner_sha256='4339992f74cde43452528a0b15b32f5002354d35a71cd1393fe275a05b808c38'
readonly reviewed_go_test_runner_sha256='635e3c69eebfadde52073c64a422a168cce9e9701cf6766d72801c75591daac2'
readonly reviewed_native_toolchain_provisioner_sha256='eaf7f39a75790304bb2e679b48581982282d564aa843b2614534906bc9468caf'

validate_reviewed_ci_bytes() {
  local actual_sha256
  actual_sha256="$(sha256sum "$1")"
  [[ "${actual_sha256%% *}" == "$reviewed_ci_sha256" ]]
}

validate_reviewed_ci_bytes "$ci"

validate_reviewed_ci_semantics() {
  local actual_sha256
  actual_sha256="$(yq -o=json -I=0 '.' "$1" | sha256sum)"
  [[ "${actual_sha256%% *}" == "$reviewed_ci_semantic_sha256" ]]
}

validate_reviewed_ci_semantics "$ci"
[[ "$(yq -o=json -I=0 '.jobs | keys | sort' "$ci")" == '["checks","go-client"]' ]]
[[ "$(yq -o=json -I=0 'keys | sort' "$ci")" == '["concurrency","env","jobs","name","on","permissions"]' ]]
[[ "$(yq -o=json -I=0 '.permissions' "$ci")" == '{"contents":"read"}' ]]
[[ "$(yq -o=json -I=0 '.jobs.checks | keys | sort' "$ci")" == '["runs-on","steps","timeout-minutes"]' ]]
[[ "$(yq -o=json -I=0 '.jobs."go-client" | keys | sort' "$ci")" == '["runs-on","steps","timeout-minutes"]' ]]
yq -o=json -I=0 '[.jobs[].steps[]]' "$ci" | jq -e '
  all(.[];
    ((keys - ["env", "name", "run", "uses", "with", "working-directory"]) | length) == 0 and
    ((has("run") and has("uses")) | not) and
    (has("run") or has("uses")) and
    (has("uses") or has("name")) and
    ((has("with") and (has("uses") | not)) | not) and
    ((has("env") and (has("run") | not)) | not) and
    ((has("working-directory") and (has("run") | not)) | not)
  )
' >/dev/null
yq eval --exit-status '([.jobs[] | select(has("if") or has("continue-on-error"))] | length) == 0' "$ci" >/dev/null
yq eval --exit-status '([.jobs[].steps[] | select(has("if") or has("continue-on-error"))] | length) == 0' "$ci" >/dev/null
yq eval --exit-status '.concurrency."cancel-in-progress" == false' "$ci" >/dev/null

validate_reviewed_workflow() {
  local path=$1
  local expected_bytes=$2
  local expected_semantics=$3
  local actual
  [[ -f "$path" && ! -L "$path" ]]
  actual="$(sha256sum "$path")"
  [[ "${actual%% *}" == "$expected_bytes" ]]
  actual="$(yq -o=json -I=0 '.' "$path" | sha256sum)"
  [[ "${actual%% *}" == "$expected_semantics" ]]
  yq eval --exit-status '([.jobs[] | select(has("continue-on-error") or has("defaults"))] | length) == 0' "$path" >/dev/null
  yq eval --exit-status '([.jobs[].steps[] | select(has("if") or has("continue-on-error"))] | length) == 0' "$path" >/dev/null
}

validate_reviewed_workflow "$release" "$reviewed_release_sha256" "$reviewed_release_semantic_sha256"
validate_reviewed_workflow "$promote" "$reviewed_promote_sha256" "$reviewed_promote_semantic_sha256"

validate_reviewed_test_runner() {
  local path=$1
  local expected=$2
  local actual
  [[ -f "$path" && ! -L "$path" && -x "$path" ]]
  actual="$(sha256sum "$path")"
  [[ "${actual%% *}" == "$expected" ]]
  bash -n "$path"
}

validate_reviewed_test_runner scripts/run-checked-rust-tests.sh "$reviewed_rust_test_runner_sha256"
validate_reviewed_test_runner scripts/run-checked-go-tests.sh "$reviewed_go_test_runner_sha256"
validate_reviewed_test_runner deploy/release/provision-native-toolchain.sh "$reviewed_native_toolchain_provisioner_sha256"

# These scripts hold release authority even when a workflow calls them only
# indirectly. Bind their exact reviewed bytes so a digest rebound cannot turn
# packaging, verification, signing, or hostile tests into no-ops.
while read -r expected path; do
  validate_reviewed_test_runner "$path" "$expected"
done <<'REVIEWED_RELEASE_SCRIPTS'
5ecccd6ac133e0aec98b557edf475d3e055b00e4f49669bcc6b0e3034cb860d1 deploy/release/native-toolchain-identity.sh
3fac2aca8f1012dc39e661e282f336ab5c2076e078158deb253cc2ce314c4469 deploy/release/verify-fips-cmake-caches.sh
d3d0c13a74eac7b030523f6a98f05af6d6bc7279c465a544c5156c2dcf3d21e0 deploy/release/require-fips-toolchain.sh
4638f070becafc81188a27cb3bef02f6ba2046169c33395d2431426e520e50aa deploy/release/package.sh
7a695d27a93bc7ef1dece6ebcec4a0890894c82d86527a7ee67a34f7fb80a679 deploy/release/publish-oci.sh
0d9cd338feb7193f8fd5ba67520500828e95c50466d76c9d951458fb6b1a47b1 deploy/release/verify.sh
65ab80f4b96418c635e9469ef1d46cdc14d7fa561ebb0f31446e4eb4d19dfe53 deploy/release/sign.sh
d507e3d9cec6578f8c8c20aaa2fc0e31fa74bbf7af981d4daec2a0248d0bcda1 deploy/release/verify-source-binding.sh
9ad05e247f67feaf67999a396da3983af9ecdd00be9bd4f38655c5c42923c24a deploy/release/verify-oci.sh
3964bbd63061001cd40ddd608bea1853eb83692c899c17c1267eb6eacf3151b3 deploy/release/reproduce.sh
d43c59a7f601e036cb0c80a4fd623fcb365bda910e3e16cb55b9c56ee3d87e69 deploy/release/test-boundary.sh
2a62091ea88448d1a076f0eccc6bdb2270bf6096562e16563f34f5a90941c8c0 deploy/release/test-oci.sh
671f555e2744525813d61ad5e81183d9838ee4d63792eab548ed20f65d540d02 deploy/release/test-publish-oci.sh
6ae7fe1892eae1f69ae3de66b3ed07a286654e1a83fb227d2ae9f0c2ff419844 deploy/release/test-sbom.sh
1636b0cdcd88e0be5c25abdf9ce1599fa5aac4297001116857379d0e62890b66 deploy/release/test-workflow-runs.sh
d555b08c598223b7f6e2466d79e517390ba1e3e66b10cb54050d1f282a63ddc3 deploy/release/verify-workflow-runs.sh
3c33db9749fe454c4f667dcee5fb4d44a829ac30e848b8101140b4439908b4d2 deploy/release/candidate.sh
1f0e46dcbd89fb356988875154c3211f8e509f8440444df065e01d348a9dfc16 deploy/release/require-production-capability.sh
REVIEWED_RELEASE_SCRIPTS

validate_reviewed_non_executable_script() {
  local path=$1 expected=$2 actual
  [[ -f "$path" && ! -L "$path" ]]
  actual=$(sha256sum "$path")
  [[ ${actual%% *} == "$expected" ]]
  bash -n "$path"
}
validate_reviewed_non_executable_script scripts/tbep_schema_digest.sh \
  2a5ec384cdb68352bc80603eb0e0bb88910e0779f45954bad882abb6b724eee6

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
  [[ "$(yq -o=json -I=0 '.env' "$ci")" == '{"CC":"/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0/bin/gcc","CXX":"/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0/bin/g++","CMAKE":"/nix/store/vrsali0sa6hwri27azgn4q0y0kmrglyi-cmake-4.1.6/bin/cmake","CMAKE_GENERATOR":"Ninja","CMAKE_MAKE_PROGRAM":"/nix/store/60z9n8f7cczph0klb7ny1mikhd8m1vkl-ninja-1.13.2/bin/ninja","CARGO_TERM_COLOR":"always","CARGO_INCREMENTAL":0,"RUST_BACKTRACE":1,"RUSTDOCFLAGS":"-D warnings"}' ]]
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
readonly native_flake='github:NixOS/nixpkgs/c5c4a43b0e8056328ec4529f735cabdb8f1942bb?narHash=sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei%2FuiU9vrMqOiwWP1Y%3D'
readonly tools_root="${RUNNER_TEMP:?}/terrabase-ci-tools"
readonly perl_link="$tools_root/perl"
readonly gcc_link="$tools_root/gcc"
readonly cmake_link="$tools_root/cmake"
readonly ninja_link="$tools_root/ninja"
[[ "$(uname -m)" == x86_64 ]]
install -d -m 0755 "$(dirname "$perl_link")"
nix build --out-link "$perl_link" "$native_flake#perl^out"
nix build --out-link "$gcc_link" "$native_flake#gcc^out"
nix build --out-link "$cmake_link" "$native_flake#cmake^out"
nix build --out-link "$ninja_link" "$native_flake#ninja^out"
readonly perl_root="$(readlink -f "$perl_link")"
[[ "$perl_root" == /nix/store/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5.42.0 ]]
[[ "$(readlink -f "$gcc_link")" == /nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0 ]]
[[ "$(readlink -f "$cmake_link")" == /nix/store/vrsali0sa6hwri27azgn4q0y0kmrglyi-cmake-4.1.6 ]]
[[ "$(readlink -f "$ninja_link")" == /nix/store/60z9n8f7cczph0klb7ny1mikhd8m1vkl-ninja-1.13.2 ]]
"$perl_root/bin/perl" -e 'die "unexpected Perl $^V\n" unless $^V eq v5.42.0'
"$CC" --version | grep -F '15.2.0'
"$CXX" --version | grep -F '15.2.0'
"$CMAKE" --version | grep -F 'cmake version 4.1.6'
"$CMAKE_MAKE_PROGRAM" --version | grep -F '1.13.2'
printf '%s\n' "$perl_root/bin" "$(dirname "$CC")" "$(dirname "$CMAKE")" "$(dirname "$CMAKE_MAKE_PROGRAM")" >> "$GITHUB_PATH"
EOF
)"
  [[ "$perl_run" == "$perl_expected" ]]

  fips_run="$(yq -r '.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run' "$ci")"
  fips_expected="$(cat <<'EOF'
set -euo pipefail
readonly perl_root=/nix/store/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5.42.0
readonly tools_root="${RUNNER_TEMP:?}/terrabase-ci-tools"
readonly fips_target_dir="$(mktemp -d "${RUNNER_TEMP:?}/terrabase-fips.XXXXXX")"
trap 'rm -rf -- "$fips_target_dir"' EXIT
[[ "$(readlink -f "$tools_root/perl")" == "$perl_root" ]]
[[ -x "$perl_root/bin/perl" ]]
export PATH="$perl_root/bin:$PATH"
export PERL="$perl_root/bin/perl"
export GO="$(type -P go)"
export CARGO_TARGET_DIR="$fips_target_dir"
unset PERL5OPT PERL5LIB PERLLIB
[[ "$(type -P perl)" == "$perl_root/bin/perl" ]]
[[ "$(readlink -f "$(type -P perl)")" == "$perl_root/bin/perl" ]]
deploy/release/require-fips-toolchain.sh
cargo build -p terrabase-server --locked --release --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-terrabase \
  --allow-ignore binary:terrabase::benchmarks::optimizer_tests::ix04_adhoc_workload_characterization \
  --allow-ignore binary:terrabase::benchmarks::optimizer_tests::ix04_write_path_overhead_characterization \
  --allow-ignore binary:terrabase::benchmarks::tests::terrabase_lane_counter_characterization \
  --skip benchmarks::lane_run::tests::probe_lane_operation_costs \
  --skip benchmarks::lane_run::tests::probe_read_subcall_costs \
  --skip benchmarks::lane_run::tests::probe_write_durability_costs \
  --skip benchmarks::lane_run::tests::probe_concurrent_synced_writes \
  -- -p terrabase --locked --no-default-features --features production-fips
cargo test -p terrabase --locked --no-default-features --features production-fips --test encryption -- production_profile_links_an_operational_aws_lc_fips_module --exact
scripts/run-checked-rust-tests.sh fips-cluster -- -p terrabase-cluster --locked --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-replication-core -- -p terrabase-replication-core --locked --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-server -- -p terrabase-server --locked --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-member-transport -- -p terrabase-member-transport --locked --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-tbep -- -p terrabase-tbep --locked --no-default-features --features production-fips
cargo clippy -p terrabase --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-cluster --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-replication-core --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-member-transport --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-tbep --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo clippy -p terrabase-server --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo doc -p terrabase -p terrabase-cluster -p terrabase-replication-core -p terrabase-member-transport -p terrabase-tbep -p terrabase-server --no-deps --locked --no-default-features --features production-fips
cargo build --manifest-path crates/terrabase-studio/Cargo.toml --locked --release --no-default-features --features production-fips
scripts/run-checked-rust-tests.sh fips-studio -- --manifest-path crates/terrabase-studio/Cargo.toml --locked --no-default-features --features production-fips
cargo clippy --manifest-path crates/terrabase-studio/Cargo.toml --all-targets --locked --no-default-features --features production-fips -- -D warnings
cargo doc --manifest-path crates/terrabase-studio/Cargo.toml --no-deps --locked --no-default-features --features production-fips
deploy/release/verify-fips-cmake-caches.sh "$fips_target_dir"
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
    if [[ "$injected" == false && "$line" == '      - name: Clippy' ]]; then
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
    if [[ "$changed" == false && "$line" == '          scripts/run-checked-rust-tests.sh workspace-dev \' ]]; then
      printf '%s\n' '          scripts/run-checked-rust-tests.sh  workspace-dev \'
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
[[ $(grep -Fc 'ref: ${{ forgejo.sha }}' "$release") -eq 2 ]]
[[ $(grep -Fc 'ref: ${{ forgejo.sha }}' "$promote") -eq 2 ]]
[[ $(grep -Fc 'test "$(git rev-parse HEAD)" = "${{ forgejo.sha }}"' "$release") -eq 2 ]]
[[ $(grep -Fc 'test "$(git rev-parse HEAD)" = "${{ forgejo.sha }}"' "$promote") -eq 2 ]]
[[ $(grep -Fc 'for evidence in cluster-composition encryption-at-rest; do' "$promote") -eq 2 ]]
[[ $(grep -Fc 'deploy/release/publish-oci.sh artifacts/unsigned/terrabase-server-' "$promote") -eq 2 ]]
[[ $(grep -Fc 'deploy/release/publish-oci.sh artifacts/unsigned/terrabase-studio-' "$promote") -eq 2 ]]
[[ $(grep -Fc 'TERRABASE_RELEASE_REGISTRY_TOKEN: ${{ secrets.TERRABASE_RELEASE_REGISTRY_TOKEN }}' "$promote") -eq 2 ]]
grep -Fq 'registry manifest digest differs from the signed candidate' deploy/release/publish-oci.sh
grep -Fq 'deploy/release/test-publish-oci.sh' "$ci"
grep -Fq 'candidate and capability workflow runs must be distinct' deploy/release/verify-workflow-runs.sh
grep -Fq '[[ "$capability_workflow_id" == qualification.yml ]]' deploy/release/verify-workflow-runs.sh
[[ $(grep -Fc 'deploy/release/require-fips-toolchain.sh' "$release") -eq 2 ]]
[[ $(grep -Fc 'deploy/release/provision-native-toolchain.sh x86_64-unknown-linux-gnu "${RUNNER_TEMP:?}/terrabase-native-tools"' "$release") -eq 1 ]]
[[ $(grep -Fc 'deploy/release/provision-native-toolchain.sh aarch64-unknown-linux-gnu "${RUNNER_TEMP:?}/terrabase-native-tools"' "$release") -eq 1 ]]
[[ $(grep -Fc "readonly native_flake='github:NixOS/nixpkgs/c5c4a43b0e8056328ec4529f735cabdb8f1942bb?narHash=sha256-u6z9VTZA4Kf3RkHQo9sQI7NI4Ei%2FuiU9vrMqOiwWP1Y%3D'" "$ci") -eq 1 ]]
[[ $(grep -Fc 'nix build --out-link "$perl_link" "$native_flake#perl^out"' "$ci") -eq 1 ]]
[[ $(grep -Fc 'readonly perl_root="$(readlink -f "$perl_link")"' "$ci") -eq 1 ]]
[[ $(grep -Fc 'unless $^V eq v5.42.0' "$ci") -eq 1 ]]
[[ $(grep -Fc 'deploy/release/require-fips-toolchain.sh' "$ci") -eq 3 ]]
grep -Fq '"$script_dir/require-fips-toolchain.sh"' deploy/release/package.sh
grep -Fq 'build_arguments+=(--no-default-features --features production-fips)' deploy/release/package.sh
[[ $(grep -Fc '"$script_dir/verify-fips-cmake-caches.sh" "$target_directory"' deploy/release/package.sh) -eq 1 ]]
grep -Fq 'terrabase feature "production-fips"' deploy/release/package.sh
grep -Fq 'aws-lc-rs feature "fips"' deploy/release/package.sh
grep -Fq 'terrabase feature "local-crypto"' deploy/release/package.sh
grep -Fq 'aws-lc-rs feature "non-fips"' deploy/release/package.sh
grep -Fq 'fipsFeatureGraph' deploy/release/package.sh
grep -Fq 'fipsFeatureGraph' deploy/release/verify.sh
grep -Fq 'binary sha256: %s' deploy/release/package.sh
grep -Fq -- '--rewrite-timestamp' deploy/release/package.sh
grep -Fq 'FIPS feature evidence is not bound to the native binary' deploy/release/verify.sh
grep -Fq 'OCI image rootfs prefix does not match the pinned base' deploy/release/verify-oci.sh
grep -Fq 'relabeled-wrong-base' deploy/release/test-oci.sh
grep -Fq 'TERRABASE_NATIVE_TOOLCHAIN_REVISION' deploy/release/package.sh
grep -Fq '"nativeTools": $native_tools' deploy/release/package.sh
grep -Fq '.predicate.buildDefinition.internalParameters.nativeTools == $expected_native_tools' deploy/release/verify.sh
[[ $(grep -Fc 'capture_source_state >"$source_state_before"' deploy/release/package.sh) -eq 1 ]]
[[ $(grep -Fc 'capture_source_state >"$source_state_after"' deploy/release/package.sh) -eq 1 ]]
[[ $(grep -Fc 'cmp -s "$source_state_before" "$source_state_after"' deploy/release/package.sh) -eq 1 ]]
grep -Fq 'release components were built with different native tool identities' deploy/release/verify-source-binding.sh
! grep -Fq -- '--all-features' deploy/release/package.sh

! grep -Fq 'GITHUB_BASE_REF' "$ci"
! grep -Fq 'GITHUB_EVENT_NAME' "$ci"
grep -Fq "against_commit='\${{ forgejo.event.before }}'" "$ci"
grep -Fq 'buf lint' "$ci"
grep -Fq 'buf breaking --against ".git#ref=$against_commit"' "$ci"
grep -Fq 'go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@v1.6.2' "$ci"
! grep -Fq 'grpc_generator_version' "$ci"
grep -Fq 'schema_digest=' "$ci"
grep -Fq 'deploy/release/test-boundary.sh' "$ci"
grep -Fq 'scripts/validate-studio-assets.sh' "$ci"
grep -Fq 'cargo clippy --workspace --all-targets --locked -- -D warnings' "$ci"
grep -Fq 'cargo doc --workspace --no-deps --locked' "$ci"
grep -Fq 'cargo check --workspace --all-targets --locked' "$ci"
grep -Fq 'cargo check --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'scripts/run-checked-rust-tests.sh served-bench-tools -- --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'cargo clippy --locked --features bench-tools --bin terrabase-bench-served' "$ci"
grep -Fq 'cargo run --manifest-path bench/Cargo.toml --locked -- --render-history' "$ci"
grep -Fq 'git diff --exit-code -- docs/evidence/benchmarks/benchmark-history.md' "$ci"
grep -Fq 'cargo fmt --manifest-path crates/terrabase-studio/Cargo.toml -- --check' "$ci"
grep -Fq 'scripts/run-checked-rust-tests.sh studio-default -- --manifest-path crates/terrabase-studio/Cargo.toml --locked' "$ci"
grep -Fq 'cargo clippy --manifest-path crates/terrabase-studio/Cargo.toml --all-targets --locked -- -D warnings' "$ci"
grep -Fq 'cargo doc --manifest-path crates/terrabase-studio/Cargo.toml --no-deps --locked' "$ci"
grep -Fq 'cargo deny --workspace --all-features --locked check' "$ci"
grep -Fq 'cargo deny --manifest-path bench/Cargo.toml --config bench/deny.toml --locked check' "$ci"
grep -Fq 'cargo deny --manifest-path crates/terrabase-studio/Cargo.toml --config crates/terrabase-studio/deny.toml --all-features --locked check' "$ci"
grep -Fq 'cargo deny --manifest-path deploy/release/sbom/Cargo.toml --config deny.toml --locked check' "$ci"
grep -Fq 'scripts/run-checked-rust-tests.sh release-sbom -- --manifest-path deploy/release/sbom/Cargo.toml --locked' "$ci"
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
  .env.CC == "/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0/bin/gcc" and
  .env.CXX == "/nix/store/c7c0yarb87cv4jxlyr02dxryrlmyi232-gcc-wrapper-15.2.0/bin/g++" and
  .jobs.checks."runs-on" == "terrascale-builder" and
  .jobs."go-client"."runs-on" == "terrascale-builder" and
  ([.jobs.checks.steps[] | select(.name == "Select explicit GCC host linker")] | length) == 1
' "$ci" >/dev/null
gcc_linker_step="$(yq eval -r '.jobs.checks.steps[] | select(.name == "Select explicit GCC host linker") | .run' "$ci")"
grep -Fq 'rustc -vV' <<<"$gcc_linker_step"
grep -Fq 's/^host: //p' <<<"$gcc_linker_step"
grep -Fq 'CARGO_TARGET_$(printf' <<<"$gcc_linker_step"
grep -Fq "LC_ALL=C tr '[:lower:]-' '[:upper:]_'" <<<"$gcc_linker_step"
grep -Fq '"$CC"' <<<"$gcc_linker_step"
grep -Fq '"$linker_env"' <<<"$gcc_linker_step"
grep -Fq '"$GITHUB_ENV"' <<<"$gcc_linker_step"
! grep -Eq 'CARGO_TARGET_(X86_64|AARCH64|ARM|RISCV|S390X|POWERPC)[[:alnum:]_]*_LINKER' <<<"$gcc_linker_step"
grep -Fq '../scripts/run-checked-go-tests.sh go-race . --race' "$ci"
if grep -Fq 'python3' "$ci" deploy/release/package.sh deploy/release/reproduce.sh; then
  printf 'Python helpers are forbidden in the CI and release shell surface\n' >&2
  exit 1
fi

if [[ "$fixture_only" == false ]]; then
  bash scripts/test-validate-release-contracts.sh
fi

echo "validated immutable promotion and protobuf baseline contracts"
