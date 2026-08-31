#!/usr/bin/env bash
set -euo pipefail

release=.forgejo/workflows/release.yml
promote=.forgejo/workflows/promote.yml
ci=.forgejo/workflows/ci.yml

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
grep -Fq 'deploy/release/test-boundary.sh' "$ci"
grep -Fq 'scripts/validate-studio-assets.sh' "$ci"
grep -Fq 'cargo test --workspace --locked --release' "$ci"
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
  local document_count
  local workflow_contents

  workflow_contents="$(<"$1")" || return 1

  document_count="$(
    yq eval-all '. as $document ireduce (0; . + 1)' - \
      <<<"$workflow_contents" 2>/dev/null
  )" || return 1
  [[ "$document_count" == 1 ]] || return 1

  # Restrict top-level workflow keys to the canonical plain-key form and
  # reject duplicates before yq resolves them. The YAML parser otherwise
  # accepts a later duplicate key and can hide an invalid trigger mapping.
  awk '
    /^[[:space:]]*($|#)/ { next }
    /^[^[:space:]]/ {
      if ($0 !~ /^[A-Za-z][A-Za-z0-9_-]*:[[:space:]]*/) {
        invalid = 1
        exit
      }
      key = $0
      sub(/:.*/, "", key)
      if (seen[key]++) {
        invalid = 1
        exit
      }
    }
    END {
      exit(invalid || seen["on"] != 1 ? 1 : 0)
    }
  ' <<<"$workflow_contents" || return 1

  yq eval --exit-status '
    (.on | type) == "!!map" and
    (.on | length) == 1 and
    (.on | has("push")) and
    (.on.push | type) == "!!map" and
    (.on.push.branches | type) == "!!seq" and
    (.on.push.branches | length) == 2 and
    .on.push.branches[0] == "develop" and
    .on.push.branches[1] == "main"
  ' - <<<"$workflow_contents" >/dev/null 2>&1
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
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  pull_request:' \
  '    branches: [develop, main]')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  pull_request: {}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  "  'pull_request': {}")
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  "pull_request": {}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  pull_request : {}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on: {push: {branches: [develop, main]}, pull_request: {}}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on:' \
  '  push:' \
  '    branches: [develop, main]' \
  '  pull_request_target: {}')
for extra_event in workflow_dispatch schedule repository_dispatch workflow_call; do
  ! validate_trunk_ci_triggers <(printf '%s\n' \
    'on:' \
    '  push:' \
    '    branches: [develop, main]' \
    "  ${extra_event}: {}")
done
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on: {push: {branches: [develop, main]}}' \
  '---' \
  'on: {push: {branches: [develop, main]}, schedule: []}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  'on: {push: {branches: [wrong]}}' \
  'on: {push: {branches: [develop, main]}}')
! validate_trunk_ci_triggers <(printf '%s\n' \
  '"on": {push: {branches: [wrong]}}' \
  'on: {push: {branches: [develop, main]}}')
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
grep -Fq '*[!A-Za-z0-9._-]*)' <<<"$gcc_linker_step"
grep -Fq '${GITHUB_ENV:?GITHUB_ENV is required}' <<<"$gcc_linker_step"
grep -Fq 'CARGO_TARGET_$(printf' <<<"$gcc_linker_step"
grep -Fq "LC_ALL=C tr '[:lower:]-' '[:upper:]_'" <<<"$gcc_linker_step"
grep -Fq '%s=gcc\n' <<<"$gcc_linker_step"
grep -Fq '"$linker_env"' <<<"$gcc_linker_step"
grep -Fq '"$GITHUB_ENV"' <<<"$gcc_linker_step"
! grep -Eq 'CARGO_TARGET_(X86_64|AARCH64|ARM|RISCV|S390X|POWERPC)[[:alnum:]_]*_LINKER' <<<"$gcc_linker_step"
grep -Fq 'go test -mod=readonly -race ./...' "$ci"
! grep -Fq 'python3' "$ci" deploy/release/package.sh deploy/release/reproduce.sh

echo "validated immutable promotion and protobuf baseline contracts"
