#!/usr/bin/env bash
set -euo pipefail

source_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fixture_root=$(mktemp -d)
trap 'rm -rf -- "$fixture_root"' EXIT

make_fixture() {
  local fixture=$1

  mkdir -p \
    "$fixture/.forgejo/workflows" \
    "$fixture/deploy/release" \
    "$fixture/scripts" \
    "$fixture/.claude/worktrees/ignored/terrabase-go"
  cp "$source_root/scripts/validate-release-contracts.sh" "$fixture/scripts/"
  cp "$source_root/scripts/normalize-generated-protoc-version.sh" "$fixture/scripts/"
  cp "$source_root/scripts/run-checked-rust-tests.sh" "$fixture/scripts/"
  cp "$source_root/scripts/run-checked-go-tests.sh" "$fixture/scripts/"
  cp "$source_root/scripts/tbep_schema_digest.sh" "$fixture/scripts/"
  mkdir -p "$fixture/terrabase-go/cmd/normalize-generated-protoc-version"
  cp "$source_root/terrabase-go/cmd/normalize-generated-protoc-version/main.go" \
    "$fixture/terrabase-go/cmd/normalize-generated-protoc-version/"
  cp "$source_root/.forgejo/workflows/ci.yml" "$fixture/.forgejo/workflows/"
  cp "$source_root/.forgejo/workflows/release.yml" "$fixture/.forgejo/workflows/"
  cp "$source_root/.forgejo/workflows/promote.yml" "$fixture/.forgejo/workflows/"
  local release_script
  for release_script in \
    candidate.sh \
    native-toolchain-identity.sh \
    package.sh \
    publish-oci.sh \
    provision-native-toolchain.sh \
    reproduce.sh \
    require-fips-toolchain.sh \
    require-production-capability.sh \
    sign.sh \
    test-boundary.sh \
    test-oci.sh \
    test-publish-oci.sh \
    test-sbom.sh \
    test-workflow-runs.sh \
    verify-fips-cmake-caches.sh \
    verify-oci.sh \
    verify-source-binding.sh \
    verify-workflow-runs.sh \
    verify.sh; do
    cp "$source_root/deploy/release/$release_script" "$fixture/deploy/release/"
  done
  printf '.claude/\n' >"$fixture/.gitignore"

  git -C "$fixture" init -q
  git -C "$fixture" add .gitignore
  printf 'package ignored\n' >"$fixture/.claude/worktrees/ignored/terrabase-go/client.go"
}

run_contracts() {
  local fixture=$1
  (
    cd "$fixture"
    bash scripts/validate-release-contracts.sh --fixture-only
  )
}

rebind_ci_digest() {
  local fixture=$1
  local digest
  local semantic_digest
  digest="$(sha256sum "$fixture/.forgejo/workflows/ci.yml")"
  digest="${digest%% *}"
  semantic_digest="$(yq -o=json -I=0 '.' "$fixture/.forgejo/workflows/ci.yml" | sha256sum)"
  semantic_digest="${semantic_digest%% *}"
  [[ $(grep -Ec "^readonly reviewed_ci_sha256='[0-9a-f]{64}'$" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  sed -i -E \
    "s/^readonly reviewed_ci_sha256='[0-9a-f]{64}'$/readonly reviewed_ci_sha256='$digest'/" \
    "$fixture/scripts/validate-release-contracts.sh"
  [[ $(grep -Fxc "readonly reviewed_ci_sha256='$digest'" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  [[ $(grep -Ec "^readonly reviewed_ci_semantic_sha256='[0-9a-f]{64}'$" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  sed -i -E \
    "s/^readonly reviewed_ci_semantic_sha256='[0-9a-f]{64}'$/readonly reviewed_ci_semantic_sha256='$semantic_digest'/" \
    "$fixture/scripts/validate-release-contracts.sh"
  [[ $(grep -Fxc "readonly reviewed_ci_semantic_sha256='$semantic_digest'" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  bash -n "$fixture/scripts/validate-release-contracts.sh"
}

rebind_workflow_digest() {
  local fixture=$1 workflow=$2 prefix=$3 digest semantic_digest
  digest=$(sha256sum "$fixture/.forgejo/workflows/$workflow")
  digest=${digest%% *}
  semantic_digest=$(yq -o=json -I=0 '.' "$fixture/.forgejo/workflows/$workflow" | sha256sum)
  semantic_digest=${semantic_digest%% *}
  sed -i -E \
    "s/^readonly reviewed_${prefix}_sha256='[0-9a-f]{64}'$/readonly reviewed_${prefix}_sha256='$digest'/" \
    "$fixture/scripts/validate-release-contracts.sh"
  sed -i -E \
    "s/^readonly reviewed_${prefix}_semantic_sha256='[0-9a-f]{64}'$/readonly reviewed_${prefix}_semantic_sha256='$semantic_digest'/" \
    "$fixture/scripts/validate-release-contracts.sh"
}

rebind_reviewed_script_digest() {
  local fixture=$1
  local relative_path=$2
  local digest
  digest=$(sha256sum "$fixture/$relative_path")
  digest=${digest%% *}
  [[ $(grep -Ec "^[0-9a-f]{64} $relative_path$" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  sed -i -E \
    "s|^[0-9a-f]{64} $relative_path$|$digest $relative_path|" \
    "$fixture/scripts/validate-release-contracts.sh"
  [[ $(grep -Fxc "$digest $relative_path" \
    "$fixture/scripts/validate-release-contracts.sh") -eq 1 ]]
  bash -n "$fixture/scripts/validate-release-contracts.sh"
}

negative_fixture_count=0

expect_rebound_workflow_mutation_rejected() {
  local name=$1 workflow=$2 prefix=$3 expression=$4 message=$5
  local fixture="$fixture_root/$name"
  mkdir -p "$fixture"
  make_fixture "$fixture"
  yq -i "$expression" "$fixture/.forgejo/workflows/$workflow"
  rebind_workflow_digest "$fixture" "$workflow" "$prefix"
  if run_contracts "$fixture" >/dev/null 2>&1; then
    printf '%s\n' "$message" >&2
    exit 1
  fi
  negative_fixture_count=$((negative_fixture_count + 1))
}

expect_rebound_workflow_mutation_rejected \
  release-mutable-checkout release.yml release \
  '(.jobs."unsigned-amd64".steps[0].with.ref) = "refs/heads/main"' \
  'release workflow accepted a mutable checkout after digest rebinding'
expect_rebound_workflow_mutation_rejected \
  promotion-mutable-checkout promote.yml promote \
  '(.jobs."sign-amd64".steps[0].with.ref) = "refs/heads/main"' \
  'promotion workflow accepted a mutable checkout after digest rebinding'
expect_rebound_workflow_mutation_rejected \
  promotion-combined-evidence-name promote.yml promote \
  '(.jobs."sign-amd64".steps[] | select(.name == "Bind downloaded bytes and capability evidence to this commit") | .run) |= sub("cluster-composition encryption-at-rest"; "cluster-composition.encryption-at-rest")' \
  'promotion workflow accepted the broken combined evidence filename'
expect_rebound_workflow_mutation_rejected \
  promotion-missing-server-publication promote.yml promote \
  '(.jobs."sign-amd64".steps[] | select(.name == "Admit, sign, and verify the exact AMD64 package bytes") | .run) |= sub("deploy/release/publish-oci.sh artifacts/unsigned/terrabase-server-amd64"; "true # removed server registry publication")' \
  'promotion workflow accepted missing server registry publication'
expect_rebound_workflow_mutation_rejected \
  promotion-missing-registry-credential promote.yml promote \
  'del(.jobs."sign-arm64".steps[] | select(.name == "Admit, sign, and verify the exact ARM64 package bytes") | .env.TERRABASE_RELEASE_REGISTRY_TOKEN)' \
  'promotion workflow accepted missing protected registry credentials'

expect_rebound_mutation_rejected() {
  local name=$1
  local expression=$2
  local message=$3
  local fixture="$fixture_root/$name"

  mkdir -p "$fixture"
  make_fixture "$fixture"
  yq -i "$expression" "$fixture/.forgejo/workflows/ci.yml"
  if cmp -s "$source_root/.forgejo/workflows/ci.yml" "$fixture/.forgejo/workflows/ci.yml"; then
    printf 'hostile fixture %s made no workflow change\n' "$name" >&2
    exit 1
  fi
  rebind_ci_digest "$fixture"
  if run_contracts "$fixture" >/dev/null 2>&1; then
    printf '%s\n' "$message" >&2
    exit 1
  fi
  ((negative_fixture_count += 1))
}

pass_fixture="$fixture_root/pass"
mkdir -p "$pass_fixture"
make_fixture "$pass_fixture"
run_contracts "$pass_fixture" >/dev/null

source_postflight_fixture="$fixture_root/source-postflight-rebound"
mkdir -p "$source_postflight_fixture"
make_fixture "$source_postflight_fixture"
sed -i 's/cmp -s "$source_state_before" "$source_state_after"/true/' \
  "$source_postflight_fixture/deploy/release/package.sh"
rebind_reviewed_script_digest "$source_postflight_fixture" deploy/release/package.sh
if run_contracts "$source_postflight_fixture" >/dev/null 2>&1; then
  printf 'expected a rebound package script without source postflight to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

late_perl_fixture="$fixture_root/late-perl"
mkdir -p "$late_perl_fixture"
make_fixture "$late_perl_fixture"
yq -i '.jobs.checks.steps |= ([.[] | select(.name != "Install pinned Perl for AWS-LC FIPS")] + [.[] | select(.name == "Install pinned Perl for AWS-LC FIPS")])' \
  "$late_perl_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$late_perl_fixture"
if run_contracts "$late_perl_fixture" >/dev/null 2>&1; then
  printf 'expected Perl provisioning after FIPS commands to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

expect_rebound_mutation_rejected \
  fips-missing-tbep-test \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("scripts/run-checked-rust-tests.sh fips-tbep"; "true # removed TBEP FIPS test")' \
  'expected removal of the TBEP FIPS test to fail validation'
expect_rebound_mutation_rejected \
  fips-local-feature \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("--no-default-features --features production-fips"; "--features local-crypto")' \
  'expected local crypto substitution to fail FIPS validation'
expect_rebound_mutation_rejected \
  fips-no-run \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("fips-server -- -p"; "fips-server -- --no-run -p")' \
  'expected a no-run FIPS test to fail validation'
expect_rebound_mutation_rejected \
  fips-missing-clippy \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("cargo clippy -p terrabase-tbep"; "true # removed TBEP FIPS Clippy")' \
  'expected removal of FIPS Clippy to fail validation'
expect_rebound_mutation_rejected \
  fips-missing-docs \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("cargo doc -p terrabase"; "true # removed FIPS rustdoc")' \
  'expected removal of FIPS rustdoc to fail validation'
expect_rebound_mutation_rejected \
  fips-missing-cmake-proof \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("deploy/release/verify-fips-cmake-caches.sh \"\\$fips_target_dir\""; "true # removed FIPS CMake-cache proof")' \
  'expected removal of FIPS CMake-cache verification to fail validation'
expect_rebound_mutation_rejected \
  fips-cached-target \
  '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) |= sub("readonly fips_target_dir=.*"; "readonly fips_target_dir=\\\"\\$GITHUB_WORKSPACE/target\\\"")' \
  'expected reuse of the cached target directory to fail validation'

unrelated_perl_fixture="$fixture_root/unrelated-perl"
mkdir -p "$unrelated_perl_fixture"
make_fixture "$unrelated_perl_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS") | .name) = "Unrelated pinned literals"' \
  "$unrelated_perl_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$unrelated_perl_fixture"
if run_contracts "$unrelated_perl_fixture" >/dev/null 2>&1; then
  printf 'expected pinned Perl literals in an unrelated step to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

reordered_fips_fixture="$fixture_root/reordered-fips"
mkdir -p "$reordered_fips_fixture"
make_fixture "$reordered_fips_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) = "cargo build -p terrabase-server --locked --release --no-default-features --features production-fips\ndeploy/release/require-fips-toolchain.sh"' \
  "$reordered_fips_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$reordered_fips_fixture"
if run_contracts "$reordered_fips_fixture" >/dev/null 2>&1; then
  printf 'expected a post-build FIPS prerequisite to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

wrong_output_fixture="$fixture_root/wrong-perl-output"
mkdir -p "$wrong_output_fixture"
make_fixture "$wrong_output_fixture"
sed -i 's/"$native_flake#perl\^out"/"$native_flake#perl"/' \
  "$wrong_output_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$wrong_output_fixture"
if run_contracts "$wrong_output_fixture" >/dev/null 2>&1; then
  printf 'expected ambiguous Nix Perl output selection to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

missing_path_fixture="$fixture_root/missing-perl-path"
mkdir -p "$missing_path_fixture"
make_fixture "$missing_path_fixture"
sed -i '/printf '\''%s\\n'\'' "$perl_root\/bin" "$(dirname "$CC")" "$(dirname "$CMAKE")" "$(dirname "$CMAKE_MAKE_PROGRAM")" >> "$GITHUB_PATH"/d' \
  "$missing_path_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$missing_path_fixture"
if run_contracts "$missing_path_fixture" >/dev/null 2>&1; then
  printf 'expected missing pinned Perl PATH publication to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

late_prerequisite_fixture="$fixture_root/late-fips-prerequisite"
mkdir -p "$late_prerequisite_fixture"
make_fixture "$late_prerequisite_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .run) = "cargo build -p terrabase-server --locked --release --no-default-features --features production-fips" | .jobs.checks.steps += [{"name": "Late FIPS prerequisite", "run": "deploy/release/require-fips-toolchain.sh"}]' \
  "$late_prerequisite_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$late_prerequisite_fixture"
if run_contracts "$late_prerequisite_fixture" >/dev/null 2>&1; then
  printf 'expected a prerequisite moved to a later step to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

tampered_runtime_fixture="$fixture_root/tampered-perl-runtime"
mkdir -p "$tampered_runtime_fixture"
make_fixture "$tampered_runtime_fixture"
sed -i 's/unless \$\^V eq v5\.42\.0/unless $^V eq v5.42.1/' \
  "$tampered_runtime_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$tampered_runtime_fixture"
if run_contracts "$tampered_runtime_fixture" >/dev/null 2>&1; then
  printf 'expected a tampered Perl runtime assertion to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

missing_store_assertion_fixture="$fixture_root/missing-perl-store-assertion"
mkdir -p "$missing_store_assertion_fixture"
make_fixture "$missing_store_assertion_fixture"
sed -i '/\[\[ "$perl_root" == \/nix\/store\/jksq0zyk08l29w4alp0g3ycgx6pwwjdk-perl-5\.42\.0 \]\]/d' \
  "$missing_store_assertion_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$missing_store_assertion_fixture"
if run_contracts "$missing_store_assertion_fixture" >/dev/null 2>&1; then
  printf 'expected a missing Perl store-path assertion to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

skipped_perl_fixture="$fixture_root/skipped-perl-step"
mkdir -p "$skipped_perl_fixture"
make_fixture "$skipped_perl_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS") | .if) = "${{ false }}"' \
  "$skipped_perl_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$skipped_perl_fixture"
if run_contracts "$skipped_perl_fixture" >/dev/null 2>&1; then
  printf 'expected a skipped Perl provisioning step to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

skipped_fips_fixture="$fixture_root/skipped-fips-step"
mkdir -p "$skipped_fips_fixture"
make_fixture "$skipped_fips_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .if) = "${{ false }}"' \
  "$skipped_fips_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$skipped_fips_fixture"
if run_contracts "$skipped_fips_fixture" >/dev/null 2>&1; then
  printf 'expected a skipped FIPS build step to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

continued_fips_fixture="$fixture_root/continued-fips-step"
mkdir -p "$continued_fips_fixture"
make_fixture "$continued_fips_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Build production server with AWS-LC FIPS") | .continue-on-error) = true' \
  "$continued_fips_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$continued_fips_fixture"
if run_contracts "$continued_fips_fixture" >/dev/null 2>&1; then
  printf 'expected a continue-on-error FIPS build to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

rebound_perl_env_fixture="$fixture_root/rebound-perl-env"
mkdir -p "$rebound_perl_env_fixture"
make_fixture "$rebound_perl_env_fixture"
yq -i '(.jobs.checks.steps[] | select(.name == "Install pinned Perl for AWS-LC FIPS") | .env.GITHUB_PATH) = "/dev/null"' \
  "$rebound_perl_env_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$rebound_perl_env_fixture"
if run_contracts "$rebound_perl_env_fixture" >/dev/null 2>&1; then
  printf 'expected a rebound Perl environment to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

skipped_checks_job_fixture="$fixture_root/skipped-checks-job"
mkdir -p "$skipped_checks_job_fixture"
make_fixture "$skipped_checks_job_fixture"
yq -i '.jobs.checks.if = "${{ false }}"' \
  "$skipped_checks_job_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$skipped_checks_job_fixture"
if run_contracts "$skipped_checks_job_fixture" >/dev/null 2>&1; then
  printf 'expected a skipped checks job to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

job_env_fixture="$fixture_root/checks-job-env"
mkdir -p "$job_env_fixture"
make_fixture "$job_env_fixture"
yq -i '.jobs.checks.env.BASH_ENV = "/tmp/unreviewed"' \
  "$job_env_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$job_env_fixture"
if run_contracts "$job_env_fixture" >/dev/null 2>&1; then
  printf 'expected a checks-job environment override to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

workflow_env_fixture="$fixture_root/workflow-env"
mkdir -p "$workflow_env_fixture"
make_fixture "$workflow_env_fixture"
yq -i '.env.BASH_ENV = "/tmp/unreviewed"' \
  "$workflow_env_fixture/.forgejo/workflows/ci.yml"
rebind_ci_digest "$workflow_env_fixture"
if run_contracts "$workflow_env_fixture" >/dev/null 2>&1; then
  printf 'expected an unreviewed workflow environment key to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

mode_fixture="$fixture_root/mode"
mkdir -p "$mode_fixture"
make_fixture "$mode_fixture"
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  "printf 'invoked\\n' >>'$mode_fixture/fixture-suite-invoked'" \
  >"$mode_fixture/scripts/test-validate-release-contracts.sh"

run_contracts "$mode_fixture" >/dev/null
if [[ -e "$mode_fixture/fixture-suite-invoked" ]]; then
  printf 'fixture-only validation recursively executed the fixture suite\n' >&2
  exit 1
fi

(
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh >/dev/null
)
if [[ $(wc -l <"$mode_fixture/fixture-suite-invoked") -ne 1 ]]; then
  printf 'normal validation did not execute the fixture suite exactly once\n' >&2
  exit 1
fi

if (
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh --unexpected >/dev/null 2>&1
); then
  printf 'expected an unknown validator argument to fail\n' >&2
  exit 1
fi
if (
  cd "$mode_fixture"
  bash scripts/validate-release-contracts.sh --fixture-only extra >/dev/null 2>&1
); then
  printf 'expected multiple validator arguments to fail\n' >&2
  exit 1
fi

untracked_fixture="$fixture_root/untracked"
mkdir -p "$untracked_fixture"
make_fixture "$untracked_fixture"
printf 'package rogue\n' >"$untracked_fixture/rogue.go"
if output=$(run_contracts "$untracked_fixture" 2>&1); then
  printf 'expected untracked Go source to fail validation\n' >&2
  exit 1
fi
grep -Fq './rogue.go' <<<"$output"
((negative_fixture_count += 1))

tracked_fixture="$fixture_root/tracked"
mkdir -p "$tracked_fixture"
make_fixture "$tracked_fixture"
printf 'package rogue\n' >"$tracked_fixture/.claude/worktrees/ignored/rogue.go"
git -C "$tracked_fixture" add -f .claude/worktrees/ignored/rogue.go
if output=$(run_contracts "$tracked_fixture" 2>&1); then
  printf 'expected tracked nested Go source to fail validation\n' >&2
  exit 1
fi
grep -Fq './.claude/worktrees/ignored/rogue.go' <<<"$output"
((negative_fixture_count += 1))

extra_workflow_fixture="$fixture_root/extra-workflow"
mkdir -p "$extra_workflow_fixture"
make_fixture "$extra_workflow_fixture"
printf 'name: unreviewed\n' >"$extra_workflow_fixture/.forgejo/workflows/extra.yml"
if run_contracts "$extra_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected an extra workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

symlinked_workflow_fixture="$fixture_root/symlinked-workflow"
mkdir -p "$symlinked_workflow_fixture"
make_fixture "$symlinked_workflow_fixture"
mv "$symlinked_workflow_fixture/.forgejo/workflows/ci.yml" \
  "$symlinked_workflow_fixture/ci.yml"
ln -s ../../ci.yml "$symlinked_workflow_fixture/.forgejo/workflows/ci.yml"
if run_contracts "$symlinked_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

symlinked_directory_fixture="$fixture_root/symlinked-directory"
mkdir -p "$symlinked_directory_fixture"
make_fixture "$symlinked_directory_fixture"
mv "$symlinked_directory_fixture/.forgejo/workflows" \
  "$symlinked_directory_fixture/.forgejo/workflows-reviewed"
ln -s workflows-reviewed "$symlinked_directory_fixture/.forgejo/workflows"
if run_contracts "$symlinked_directory_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked workflow directory to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

missing_workflow_fixture="$fixture_root/missing-workflow"
mkdir -p "$missing_workflow_fixture"
make_fixture "$missing_workflow_fixture"
rm "$missing_workflow_fixture/.forgejo/workflows/promote.yml"
if run_contracts "$missing_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a missing workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

non_regular_workflow_fixture="$fixture_root/non-regular-workflow"
mkdir -p "$non_regular_workflow_fixture"
make_fixture "$non_regular_workflow_fixture"
rm "$non_regular_workflow_fixture/.forgejo/workflows/release.yml"
mkdir "$non_regular_workflow_fixture/.forgejo/workflows/release.yml"
if run_contracts "$non_regular_workflow_fixture" >/dev/null 2>&1; then
  printf 'expected a non-regular workflow to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

failing_find_fixture="$fixture_root/failing-find"
mkdir -p "$failing_find_fixture"
make_fixture "$failing_find_fixture"
failing_find_bin="$failing_find_fixture/find-bin"
mkdir -p "$failing_find_bin"
real_find=$(command -v find)
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  "\"$real_find\" \"\$@\"" \
  'exit 1' >"$failing_find_bin/find"
chmod 0755 "$failing_find_bin/find"
if PATH="$failing_find_bin:$PATH" run_contracts "$failing_find_fixture" \
  >/dev/null 2>&1; then
  printf 'expected a failing workflow inventory scan to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

normalizer_mutation_fixture="$fixture_root/normalizer-mutation"
mkdir -p "$normalizer_mutation_fixture"
make_fixture "$normalizer_mutation_fixture"
printf '\n# hostile normalization broadening\n' \
  >>"$normalizer_mutation_fixture/scripts/normalize-generated-protoc-version.sh"
if run_contracts "$normalizer_mutation_fixture" >/dev/null 2>&1; then
  printf 'expected a modified generated-header normalizer to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

normalizer_wrapper_symlink_fixture="$fixture_root/normalizer-wrapper-symlink"
mkdir -p "$normalizer_wrapper_symlink_fixture"
make_fixture "$normalizer_wrapper_symlink_fixture"
mv "$normalizer_wrapper_symlink_fixture/scripts/normalize-generated-protoc-version.sh" \
  "$normalizer_wrapper_symlink_fixture/normalize-generated-protoc-version.sh.real"
ln -s ../normalize-generated-protoc-version.sh.real \
  "$normalizer_wrapper_symlink_fixture/scripts/normalize-generated-protoc-version.sh"
if run_contracts "$normalizer_wrapper_symlink_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked generated-header wrapper to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

normalizer_helper_symlink_fixture="$fixture_root/normalizer-helper-symlink"
mkdir -p "$normalizer_helper_symlink_fixture"
make_fixture "$normalizer_helper_symlink_fixture"
mv "$normalizer_helper_symlink_fixture/terrabase-go/cmd/normalize-generated-protoc-version/main.go" \
  "$normalizer_helper_symlink_fixture/normalize-generated-protoc-version.go.real"
ln -s ../../../../normalize-generated-protoc-version.go.real \
  "$normalizer_helper_symlink_fixture/terrabase-go/cmd/normalize-generated-protoc-version/main.go"
if run_contracts "$normalizer_helper_symlink_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked generated-header helper to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

python_guard_fixture="$fixture_root/python-guard"
mkdir -p "$python_guard_fixture"
make_fixture "$python_guard_fixture"
printf '%s\n' '# python3 must remain forbidden in CI shell steps' \
  >>"$python_guard_fixture/.forgejo/workflows/ci.yml"
if run_contracts "$python_guard_fixture" >/dev/null 2>&1; then
  printf 'expected Python in the CI shell surface to fail validation\n' >&2
  exit 1
fi
((negative_fixture_count += 1))

if [[ $negative_fixture_count -ne 40 ]]; then
  printf 'expected forty negative release-contract fixtures, ran %s\n' \
    "$negative_fixture_count" >&2
  exit 1
fi

normalizer="$source_root/scripts/normalize-generated-protoc-version.sh"
canonical_pb_line=$'// \tprotoc        v7.34.1'
unknown_pb_line=$'// \tprotoc        (unknown)'
canonical_grpc_line='// - protoc             v7.34.1'
unknown_grpc_line='// - protoc             (unknown)'

make_normalizer_fixture() {
  local root=$1
  local pb_line=$2
  local grpc_line=$3

  mkdir -p "$root/terrabase/v1"
  printf '%s\n' \
    '// Code generated by protoc-gen-go. DO NOT EDIT.' \
    '// versions:' \
    $'// \tprotoc-gen-go v1.36.12' \
    "$pb_line" \
    '// source: terrabase/v1/engine.proto' \
    '' \
    'package v1' \
    '' \
    'const fixturePayload = "compiler text in a body stays untouched"' \
    >"$root/terrabase/v1/engine.pb.go"
  printf '%s\n' \
    '// Code generated by protoc-gen-go-grpc. DO NOT EDIT.' \
    '// versions:' \
    '// - protoc-gen-go-grpc v1.6.2' \
    "$grpc_line" \
    '// source: terrabase/v1/engine.proto' \
    '' \
    'package v1' \
    '' \
    'const grpcFixturePayload = "compiler text in a body stays untouched"' \
    >"$root/terrabase/v1/engine_grpc.pb.go"
  printf '%s\n' \
    '// Code generated by protoc-gen-go. DO NOT EDIT.' \
    '// versions:' \
    $'// \tprotoc-gen-go v1.36.12' \
    "$pb_line" \
    '// source: terrabase/v1/family.proto' \
    '' \
    'package v1' \
    '' \
    'const familyFixturePayload = "compiler text in a body stays untouched"' \
    >"$root/terrabase/v1/family.pb.go"
}

normalizer_pass_fixture="$fixture_root/normalizer-pass"
normalizer_expected_fixture="$fixture_root/normalizer-expected"
make_normalizer_fixture "$normalizer_pass_fixture" "$unknown_pb_line" "$unknown_grpc_line"
make_normalizer_fixture \
  "$normalizer_expected_fixture" "$canonical_pb_line" "$canonical_grpc_line"
"$normalizer" "$normalizer_pass_fixture"
cmp "$normalizer_expected_fixture/terrabase/v1/engine.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/engine.pb.go"
cmp "$normalizer_expected_fixture/terrabase/v1/engine_grpc.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/engine_grpc.pb.go"
cmp "$normalizer_expected_fixture/terrabase/v1/family.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/family.pb.go"
"$normalizer" "$normalizer_pass_fixture"
cmp "$normalizer_expected_fixture/terrabase/v1/engine.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/engine.pb.go"
cmp "$normalizer_expected_fixture/terrabase/v1/engine_grpc.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/engine_grpc.pb.go"
cmp "$normalizer_expected_fixture/terrabase/v1/family.pb.go" \
  "$normalizer_pass_fixture/terrabase/v1/family.pb.go"

normalizer_unexpected_fixture="$fixture_root/normalizer-unexpected-version"
make_normalizer_fixture \
  "$normalizer_unexpected_fixture" \
  $'// \tprotoc        v7.35.0' \
  "$unknown_grpc_line"
if "$normalizer" "$normalizer_unexpected_fixture" >/dev/null 2>&1; then
  printf 'expected an unapproved compiler version to fail normalization\n' >&2
  exit 1
fi

normalizer_missing_compiler_fixture="$fixture_root/normalizer-missing-compiler"
make_normalizer_fixture \
  "$normalizer_missing_compiler_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i '4d' "$normalizer_missing_compiler_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_missing_compiler_fixture" >/dev/null 2>&1; then
  printf 'expected a missing compiler-version header to fail normalization\n' >&2
  exit 1
fi

normalizer_duplicate_fixture="$fixture_root/normalizer-duplicate-version"
make_normalizer_fixture \
  "$normalizer_duplicate_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i "5i\\$unknown_pb_line" \
  "$normalizer_duplicate_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_duplicate_fixture" >/dev/null 2>&1; then
  printf 'expected duplicate compiler-version lines to fail normalization\n' >&2
  exit 1
fi

normalizer_missing_marker_fixture="$fixture_root/normalizer-missing-marker"
make_normalizer_fixture \
  "$normalizer_missing_marker_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i '1d' "$normalizer_missing_marker_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_missing_marker_fixture" >/dev/null 2>&1; then
  printf 'expected a missing generated-file marker to fail normalization\n' >&2
  exit 1
fi

normalizer_malformed_marker_fixture="$fixture_root/normalizer-malformed-marker"
make_normalizer_fixture \
  "$normalizer_malformed_marker_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i '1c// Code generated by protoc-gen-go. DO NOT EDIT' \
  "$normalizer_malformed_marker_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_malformed_marker_fixture" >/dev/null 2>&1; then
  printf 'expected a malformed generated-file marker to fail normalization\n' >&2
  exit 1
fi

normalizer_duplicate_marker_fixture="$fixture_root/normalizer-duplicate-marker"
make_normalizer_fixture \
  "$normalizer_duplicate_marker_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i '2i// Code generated by protoc-gen-go. DO NOT EDIT.' \
  "$normalizer_duplicate_marker_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_duplicate_marker_fixture" >/dev/null 2>&1; then
  printf 'expected duplicate generated-file markers to fail normalization\n' >&2
  exit 1
fi

normalizer_misplaced_fixture="$fixture_root/normalizer-misplaced-version"
make_normalizer_fixture \
  "$normalizer_misplaced_fixture" "$unknown_pb_line" "$unknown_grpc_line"
sed -i '4d' "$normalizer_misplaced_fixture/terrabase/v1/engine.pb.go"
printf '%s\n' "$unknown_pb_line" \
  >>"$normalizer_misplaced_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_misplaced_fixture" >/dev/null 2>&1; then
  printf 'expected a compiler-version line outside the header to fail normalization\n' >&2
  exit 1
fi

normalizer_symlink_fixture="$fixture_root/normalizer-symlink"
make_normalizer_fixture \
  "$normalizer_symlink_fixture" "$unknown_pb_line" "$unknown_grpc_line"
mv "$normalizer_symlink_fixture/terrabase/v1/engine.pb.go" \
  "$normalizer_symlink_fixture/engine.pb.go"
ln -s ../../../engine.pb.go \
  "$normalizer_symlink_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_symlink_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked generated binding to fail normalization\n' >&2
  exit 1
fi

normalizer_nested_symlink_fixture="$fixture_root/normalizer-nested-symlink"
make_normalizer_fixture \
  "$normalizer_nested_symlink_fixture" "$unknown_pb_line" "$unknown_grpc_line"
mv "$normalizer_nested_symlink_fixture/terrabase/v1" \
  "$normalizer_nested_symlink_fixture/v1-reviewed"
ln -s ../v1-reviewed "$normalizer_nested_symlink_fixture/terrabase/v1"
if "$normalizer" "$normalizer_nested_symlink_fixture" >/dev/null 2>&1; then
  printf 'expected a symlinked intermediate directory to fail normalization\n' >&2
  exit 1
fi

normalizer_root_symlink_fixture="$fixture_root/normalizer-root-symlink"
make_normalizer_fixture \
  "$normalizer_root_symlink_fixture/real-root" "$unknown_pb_line" "$unknown_grpc_line"
ln -s real-root "$normalizer_root_symlink_fixture/root"
if "$normalizer" "$normalizer_root_symlink_fixture/root" >/dev/null 2>&1; then
  printf 'expected a symlinked binding root to fail normalization\n' >&2
  exit 1
fi

normalizer_partial_fixture="$fixture_root/normalizer-partial-failure"
make_normalizer_fixture \
  "$normalizer_partial_fixture" "$unknown_pb_line" "$unknown_grpc_line"
before_partial="$fixture_root/partial-before.sha256"
sha256sum \
  "$normalizer_partial_fixture/terrabase/v1/engine.pb.go" \
  "$normalizer_partial_fixture/terrabase/v1/engine_grpc.pb.go" \
  "$normalizer_partial_fixture/terrabase/v1/family.pb.go" >"$before_partial"
printf '%s\n' 'not a generated binding' \
  >"$normalizer_partial_fixture/terrabase/v1/family.pb.go"
if "$normalizer" "$normalizer_partial_fixture" >/dev/null 2>&1; then
  printf 'expected a malformed later binding to fail preflight\n' >&2
  exit 1
fi
sha256sum \
  "$normalizer_partial_fixture/terrabase/v1/engine.pb.go" \
  "$normalizer_partial_fixture/terrabase/v1/engine_grpc.pb.go" \
  "$normalizer_partial_fixture/terrabase/v1/family.pb.go" |
  tee "$fixture_root/partial-after.sha256" >/dev/null
cmp <(sed -n '1,2p' "$before_partial") \
  <(sed -n '1,2p' "$fixture_root/partial-after.sha256")

normalizer_no_final_newline_fixture="$fixture_root/normalizer-no-final-newline"
normalizer_no_final_newline_expected="$fixture_root/normalizer-no-final-newline-expected"
make_normalizer_fixture \
  "$normalizer_no_final_newline_fixture" "$unknown_pb_line" "$unknown_grpc_line"
make_normalizer_fixture \
  "$normalizer_no_final_newline_expected" "$canonical_pb_line" "$canonical_grpc_line"
truncate -s -1 "$normalizer_no_final_newline_fixture/terrabase/v1/family.pb.go"
truncate -s -1 "$normalizer_no_final_newline_expected/terrabase/v1/family.pb.go"
before_body=$(tail -c 58 "$normalizer_no_final_newline_fixture/terrabase/v1/family.pb.go")
"$normalizer" "$normalizer_no_final_newline_fixture"
cmp "$normalizer_no_final_newline_expected/terrabase/v1/family.pb.go" \
  "$normalizer_no_final_newline_fixture/terrabase/v1/family.pb.go"
[[ "$(tail -c 1 "$normalizer_no_final_newline_fixture/terrabase/v1/family.pb.go" | od -An -t x1 | tr -d '[:space:]')" != '0a' ]]
after_body=$(tail -c 58 "$normalizer_no_final_newline_fixture/terrabase/v1/family.pb.go")
[[ "$before_body" == "$after_body" ]]

normalizer_missing_file_fixture="$fixture_root/normalizer-missing-file"
make_normalizer_fixture \
  "$normalizer_missing_file_fixture" "$unknown_pb_line" "$unknown_grpc_line"
rm "$normalizer_missing_file_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_missing_file_fixture" >/dev/null 2>&1; then
  printf 'expected a missing generated binding to fail normalization\n' >&2
  exit 1
fi

normalizer_nonfile_fixture="$fixture_root/normalizer-nonfile"
make_normalizer_fixture \
  "$normalizer_nonfile_fixture" "$unknown_pb_line" "$unknown_grpc_line"
rm "$normalizer_nonfile_fixture/terrabase/v1/engine.pb.go"
mkdir "$normalizer_nonfile_fixture/terrabase/v1/engine.pb.go"
if "$normalizer" "$normalizer_nonfile_fixture" >/dev/null 2>&1; then
  printf 'expected a non-file generated binding to fail normalization\n' >&2
  exit 1
fi

if "$normalizer" >/dev/null 2>&1 || \
  "$normalizer" "$normalizer_pass_fixture" extra >/dev/null 2>&1; then
  printf 'expected invalid normalizer arguments to fail\n' >&2
  exit 1
fi

printf 'validated %s negative release-contract fixtures, header-only normalization, and normal/fixture-only dispatch\n' \
  "$negative_fixture_count"
