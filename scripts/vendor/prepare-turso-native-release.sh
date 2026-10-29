#!/usr/bin/env bash
# Assemble qualified outputs; publication is a separate, explicit parent action.
set -euo pipefail
x64=${1:?usage: prepare-turso-native-release.sh <x64-output> <arm64-output> <empty-release-directory>}
arm64=${2:?usage: prepare-turso-native-release.sh <x64-output> <arm64-output> <empty-release-directory>}
output=${3:?usage: prepare-turso-native-release.sh <x64-output> <arm64-output> <empty-release-directory>}
mkdir -p "$output"
test -z "$(ls -A "$output")" || { echo 'Release directory must be empty.' >&2; exit 1; }
for rid in linux-x64 linux-arm64; do
  case "$rid" in linux-x64) input=$x64; machine=3e00; host=x86_64 ;; linux-arm64) input=$arm64; machine=b700; host=aarch64 ;; esac
  (cd "$input" && sha256sum --check "SHA256SUMS-$rid")
  library="$input/runtimes/$rid/native/libturso_sdk_kit.so"
  test "$(od -An -tx1 -N6 "$library" | tr -d ' \n')" = 7f454c460201
  test "$(od -An -tx1 -j18 -N2 "$library" | tr -d ' \n')" = "$machine"
  grep -qx "host=$host" "$input/release/$rid-build-receipt.txt"
  grep -qx 'aegis_backend=runtime-dispatched-c' "$input/release/$rid-build-receipt.txt"
  grep -qx "crypto proof: $rid hardware instructions, runtime dispatch and portable fallback present; HAS_HW_AES undefined" "$input/release/$rid-aegis-proof.txt"
  grep -qx 'native smoke: candidate query/cursor/sort/preflight/count use indexed text id seeks; ULIDs, missing hits and empty counts passed' "$input/release/$rid-fts-smoke.txt"
  grep -qx 'native smoke: three-term carrying-slop reader/count/ordered queries refuse scratch; small admitted slop succeeds' "$input/release/$rid-fts-smoke.txt"
  grep -q '^contract1 smoke: marker=1, aegis256 persisted FTS, SQL spoof constraint and page tamper TURSO_CORRUPT passed$' "$input/release/$rid-contract1-smoke.txt"
  grep -q '^wal contract smoke: marker=1, [0-9]* frames with the database file untouched across close, snapshot page reads decrypt under a passive checkpoint, write transactions and bad ranges refused, explicit TRUNCATE resets frames$' "$input/release/$rid-wal-smoke.txt"
  for source in "$input"/release/*; do
    target="$output/$(basename "$source")"
    if [[ -f "$target" ]]; then cmp "$source" "$target"; else cp "$source" "$target"; fi
  done
  cp "$library" "$output/terrascale-turso-$rid.so"
done
{
  printf 'release=turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r2\n'
  printf 'repository_commit=%s\n' "$(git rev-parse HEAD)"
  printf 'workflow_run=%s\n' "${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-TerraScale/terrascale}/actions/runs/${GITHUB_RUN_ID:-not-a-CI-run}"
  printf 'Source, patches, Cargo.lock, recipe, compiler, ABI exports, ELF dependencies and successful smoke logs accompany each architecture.\n'
  printf 'No encryption-only turso-native-v1 assets are used.\n'
} > "$output/PROVENANCE.txt"
(cd "$output" && sha256sum -- *.so > NATIVE-SHA256SUMS)
(cd "$output" && find . -maxdepth 1 -type f ! -name SHA256SUMS -printf '%f\0' | sort -z | xargs -0 sha256sum > SHA256SUMS)
printf 'Prepared %s. Review receipts and copy NATIVE-SHA256SUMS into src/vendor/Turso.Native/SHA256SUMS only after qualification.\n' "$output"
