#!/usr/bin/env bash
set -euo pipefail

flatc_version="25.12.19"
linux_archive_sha256="9f87066dc5dfa7fe02090b55bab5f3e55df03e32c9b0cdf229004ade7d091039"
linux_archive_url="https://github.com/google/flatbuffers/releases/download/v25.12.19/Linux.flatc.binary.g%2B%2B-13.zip"

tool_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
schema_path="$tool_dir/../../schema/tbep.fbs"
committed_path="$tool_dir/../../src/generated/fbs/tbep_generated.rs"
mode="${1:---write}"

case "$mode" in
    --check | --write) ;;
    *)
        echo "usage: $0 [--check|--write]" >&2
        exit 2
        ;;
esac

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tbep-fbs.XXXXXXXX")"
cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

resolve_flatc() {
    if [[ -n "${TBEP_FLATC:-}" ]]; then
        printf '%s\n' "$TBEP_FLATC"
        return
    fi
    if command -v flatc >/dev/null 2>&1; then
        command -v flatc
        return
    fi

    if [[ "$(uname -s):$(uname -m)" != "Linux:x86_64" ]]; then
        echo "flatc $flatc_version is required; set TBEP_FLATC to its path" >&2
        exit 1
    fi
    command -v curl >/dev/null 2>&1 || {
        echo "curl is required to fetch the pinned flatc archive" >&2
        exit 1
    }
    command -v unzip >/dev/null 2>&1 || {
        echo "unzip is required to unpack the pinned flatc archive" >&2
        exit 1
    }

    cache_dir="$tool_dir/target/pinned-flatc/$flatc_version"
    archive_path="$cache_dir/Linux.flatc.binary.g++-13.zip"
    mkdir -p "$cache_dir"
    if [[ ! -f "$archive_path" ]] || ! printf '%s  %s\n' \
        "$linux_archive_sha256" "$archive_path" | sha256sum --check --status; then
        download_path="$archive_path.download"
        curl --fail --location --retry 3 --silent --show-error \
            "$linux_archive_url" --output "$download_path"
        printf '%s  %s\n' "$linux_archive_sha256" "$download_path" \
            | sha256sum --check --status
        mv "$download_path" "$archive_path"
    fi
    printf '%s  %s\n' "$linux_archive_sha256" "$archive_path" \
        | sha256sum --check --status

    extracted_dir="$work_dir/flatc"
    mkdir -p "$extracted_dir"
    unzip -q "$archive_path" -d "$extracted_dir"
    printf '%s\n' "$extracted_dir/flatc"
}

flatc_bin="$(resolve_flatc)"
actual_version="$("$flatc_bin" --version)"
expected_version="flatc version $flatc_version"
if [[ "$actual_version" != "$expected_version" ]]; then
    echo "expected '$expected_version', got '$actual_version' from $flatc_bin" >&2
    exit 1
fi

generated_dir="$work_dir/generated"
mkdir -p "$generated_dir"
"$flatc_bin" --rust -o "$generated_dir" "$schema_path"
generated_path="$generated_dir/tbep_generated.rs"

if [[ "$mode" == "--check" ]]; then
    if ! cmp -s "$committed_path" "$generated_path"; then
        diff -u "$committed_path" "$generated_path" || true
        echo "FlatBuffers Rust bindings drifted; run $0 --write" >&2
        exit 1
    fi
    echo "FlatBuffers Rust bindings match flatc $flatc_version regeneration"
else
    cp "$generated_path" "$committed_path"
    echo "wrote $committed_path with flatc $flatc_version"
fi
