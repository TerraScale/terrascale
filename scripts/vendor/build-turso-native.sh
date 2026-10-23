#!/usr/bin/env bash
# Run through ts-heavy-any on a dev host; CI consumes verified prebuilt artifacts.
set -euo pipefail
patchelf=$(command -v patchelf)
test "$("$patchelf" --version)" = 'patchelf 0.15.2'
rid=${1:?usage: build-turso-native.sh linux-x64|linux-arm64}
case "$rid" in
  linux-x64) target=x86_64-unknown-linux-gnu ;;
  linux-arm64) target=aarch64-unknown-linux-gnu ;;
  *) echo 'Only the two Linux release architectures are supported.' >&2; exit 2 ;;
esac
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(git -C "$script_dir" rev-parse --show-toplevel)
source_commit=5c168be5ef6a32ac966daf5983502a4f694b37ad
toolchain=1.88.0
patch="$repo_root/src/vendor/Turso.Native/patches/0001-preserve-page-authentication-damage.patch"
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/terrascale-turso-native.XXXXXXXX")
output="$repo_root/artifacts/turso-native/$rid"
printf 'Retained native build source: %s\n' "$build_dir"
git -C "$build_dir" init --quiet
git -C "$build_dir" remote add origin https://github.com/tursodatabase/turso.git
git -C "$build_dir" fetch --quiet --depth=1 origin "$source_commit"
git -C "$build_dir" -c advice.detachedHead=false checkout --quiet --detach FETCH_HEAD
test "$(git -C "$build_dir" rev-parse HEAD)" = "$source_commit"
git -C "$build_dir" apply --check "$patch"
git -C "$build_dir" apply "$patch"
rustup toolchain install "$toolchain" --profile minimal --no-self-update
rustup target add --toolchain "$toolchain" "$target"
cd "$build_dir"
# Matches the pinned upstream .NET release's package and optimization profile.
cargo "+$toolchain" build --locked --profile release-official --package turso_sync_sdk_kit --target "$target"
mkdir -p "$output"
compiler_output="target/$target/release-official/libturso_sync_sdk_kit.so"
# Never replace an earlier compiler output with a different build.
cp -n "$compiler_output" "$output/libturso_sdk_kit.compiler.so"
cmp "$compiler_output" "$output/libturso_sdk_kit.compiler.so"
cp "$output/libturso_sdk_kit.compiler.so" "$output/libturso_sdk_kit.so"
readelf -d "$output/libturso_sdk_kit.compiler.so" > "$output/elf-dynamic-original.txt"
"$patchelf" --remove-rpath "$output/libturso_sdk_kit.so"
readelf -d "$output/libturso_sdk_kit.so" > "$output/elf-dynamic.txt"
readelf --version-info "$output/libturso_sdk_kit.so" > "$output/elf-version-info.txt"
if rg 'RPATH|RUNPATH|Shared library: \[/' "$output/elf-dynamic.txt"; then
  echo 'Native artifact retains a runtime path or absolute dependency.' >&2
  exit 1
fi
nm -D --defined-only "$output/libturso_sdk_kit.so" | awk '{ print $3 }' > "$output/exports.txt"
rg -x 'turso_terrascale_storage_error_contract' "$output/exports.txt"
rg -x 'turso_sync_database_new' "$output/exports.txt"
{
  printf 'source_commit=%s\ntoolchain=%s\nprofile=release-official\npackage=turso_sync_sdk_kit\ntarget=%s\n' "$source_commit" "$toolchain" "$target"
  printf 'retained_source=%s\n' "$build_dir"
  "$patchelf" --version
  sha256sum "$patch" "$build_dir/Cargo.lock" "$output/libturso_sdk_kit.compiler.so" "$output/libturso_sdk_kit.so"
  printf 'max_required_glibc=%s\n' "$(rg -o 'GLIBC_[0-9.]+' "$output/elf-version-info.txt" | sort -Vu | tail -1)"
  cat "$output/elf-dynamic.txt" "$output/elf-version-info.txt"
  rustc "+$toolchain" --version --verbose
} > "$output/build-receipt.txt"
sha256sum "$output/libturso_sdk_kit.so"
printf 'Native artifact and receipt: %s\n' "$output"
