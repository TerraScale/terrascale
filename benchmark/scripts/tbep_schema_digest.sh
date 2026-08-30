#!/usr/bin/env bash
# Emit the canonical digest of the TBEP descriptor set.
#
# Source information is excluded so comments and source locations do not
# change the value. The descriptor set is built by buf from the checked-in
# protobuf module, making this a semantic schema digest rather than a digest
# of a particular generated client language.
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
descriptor="$(mktemp)"
trap 'rm -f -- "$descriptor"' EXIT

(cd "$repo_root" && buf build \
    --as-file-descriptor-set \
    --exclude-source-info \
    --output "$descriptor")

digest="$(sha256sum "$descriptor" | awk '{print $1}')"
printf '%s\n' "$digest"

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
        printf '### TBEP schema digest\n\n'
        printf '`%s` (SHA-256 of the source-info-free protobuf descriptor set)\n' "$digest"
    } >>"$GITHUB_STEP_SUMMARY"
fi
