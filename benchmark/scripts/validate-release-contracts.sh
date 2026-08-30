#!/usr/bin/env bash
set -euo pipefail

release=.forgejo/workflows/release.yml
promote=.forgejo/workflows/promote.yml
ci=.forgejo/workflows/ci.yml

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

grep -Fq 'GITHUB_BASE_REF' "$ci"
grep -Fq 'git rev-parse --verify HEAD^' "$ci"
grep -Fq 'buf breaking --against ".git#ref=$against_commit"' "$ci"
! grep -Fq '.git#branch=${GITHUB_REF_NAME}' "$ci"

echo "validated immutable promotion and protobuf baseline contracts"
