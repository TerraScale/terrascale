#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_ENV:?GITHUB_ENV is required}"
archive="$(dirname -- "${BASH_SOURCE[0]}")/producer-contract-inputs.tar.gz"
printf '%s  %s\n' \
  8169fab845e3130b54378ff9a064ff212d1ff59fe15eb82c857718ad919db7e7 \
  "${archive}" | sha256sum --check --strict

producer_dir="$(mktemp -d "${RUNNER_TEMP:-/tmp}/terrascale-producer-inputs.XXXXXX")"
tar -xzf "${archive}" -C "${producer_dir}"
printf 'TERRASCALE_CURRENT_OPENAPI=%s/crates/tb-server/assets/client-openapi.json\n' \
  "${producer_dir}" >> "${GITHUB_ENV}"
printf 'TERRASCALE_ATOMIC_VECTORS=%s/crates/tb-server/tests/support/document/atomic_vectors.json\n' \
  "${producer_dir}" >> "${GITHUB_ENV}"

# The unchanged SDK contract guard verifies both member digests next.
# The archive's producer-inputs-v1.json retains the frozen overlay provenance.
cat "${producer_dir}/producer-inputs-v1.json"
