#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

# Launch the final frozen 1M RustFS E2E workload. The script writes only under
# ts-benchmark and never edits the TerraBase checkout or AGENTS.md.
#
# Verification only:
#   ./launch-e2e-rustfs-1m-final.sh --verify --binary-root <frozen-dir>
#
# Launch and wait for the finite systemd unit:
#   ./launch-e2e-rustfs-1m-final.sh --binary-root <frozen-dir>
#
# Set TERRABASE_FINAL_BINARY_ROOT when --binary-root is omitted. Auto-detection
# is allowed only when exactly one final frozen directory exists.

BENCH_ROOT=${BENCH_ROOT:-/home/mariogk/Projects/TerraScale/ts-benchmark}
FINAL_BINARY_ROOT=${TERRABASE_FINAL_BINARY_ROOT:-}
MODE=launch
DETACH=0
RUNTIME_MAX=${RUNTIME_MAX:-8h}
S3_ENDPOINT=${TERRABASE_S3_ENDPOINT:-http://127.0.0.1:19000}
S3_REGION=${TERRABASE_S3_REGION:-us-east-1}
CREDENTIAL_FILE=${TERRABASE_RUSTFS_CREDENTIAL_FILE:-/run/user/1000/terrabase-rustfs-credentials.sh}
RUSTFS_DATA_PATH=${RUSTFS_DATA_PATH:-/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data}

usage() {
    cat <<'HELP'
Usage:
  launch-e2e-rustfs-1m-final.sh [--binary-root DIR] [--detach]
  launch-e2e-rustfs-1m-final.sh --verify [--binary-root DIR]

Environment:
  TERRABASE_FINAL_BINARY_ROOT  Frozen Release directory, if --binary-root is omitted
  RUNTIME_MAX                  systemd RuntimeMaxSec value (default: 8h)
  TERRABASE_RUSTFS_CREDENTIAL_FILE  Private credential script path
  TERRABASE_S3_ENDPOINT        RustFS endpoint (default: http://127.0.0.1:19000)
  TERRABASE_S3_REGION          S3 region (default: us-east-1)
  RUSTFS_DATA_PATH             RustFS local data root used for accounting
HELP
}

die() {
    printf 'launch-e2e-rustfs-1m-final: %s\n' "$*" >&2
    exit 1
}

for required_command in bash curl find git jq nice pgrep realpath rg sha256sum sort systemctl systemd-run tar; do
    command -v "$required_command" >/dev/null 2>&1 || die "required command is missing: $required_command"
done

while (($# > 0)); do
    case "$1" in
        --help|-h)
            usage
            exit 0
            ;;
        --verify)
            MODE=verify
            shift
            ;;
        --detach)
            DETACH=1
            shift
            ;;
        --binary-root)
            (($# >= 2)) || die "--binary-root requires a directory"
            FINAL_BINARY_ROOT=$2
            shift 2
            ;;
        --binary-root=*)
            FINAL_BINARY_ROOT=${1#*=}
            shift
            ;;
        --runtime-max)
            (($# >= 2)) || die "--runtime-max requires a systemd time span"
            RUNTIME_MAX=$2
            shift 2
            ;;
        --runtime-max=*)
            RUNTIME_MAX=${1#*=}
            shift
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

[[ "$RUNTIME_MAX" =~ ^[1-9][0-9]*(ms|us|s|min|h|d)?$ ]] ||
    die "RUNTIME_MAX must be a finite positive systemd time span such as 8h"

resolve_binary_root() {
    if [[ -n "$FINAL_BINARY_ROOT" ]]; then
        FINAL_BINARY_ROOT=$(realpath -e -- "$FINAL_BINARY_ROOT") || die "frozen binary directory is unavailable"
        return
    fi

    local binary_parent="$BENCH_ROOT/bench/binaries"
    [[ -d "$binary_parent" ]] || die "binary parent is missing: $binary_parent"
    mapfile -t candidates < <(
        find "$binary_parent" -mindepth 1 -maxdepth 1 -type d \
            -name 'terrabase-final-source-matched-release-*' -printf '%p\n' | sort
    )
    case "${#candidates[@]}" in
        1) FINAL_BINARY_ROOT=${candidates[0]} ;;
        0) die "no terrabase-final-source-matched-release-* directory found; pass --binary-root" ;;
        *) die "multiple frozen binary directories found; pass --binary-root explicitly" ;;
    esac
}

resolve_binary_root
[[ -d "$FINAL_BINARY_ROOT" ]] || die "frozen binary directory is missing: $FINAL_BINARY_ROOT"
BINARY_DLL="$FINAL_BINARY_ROOT/TerraBase.Benchmarks.dll"
FREEZE_MANIFEST="$FINAL_BINARY_ROOT/freeze-manifest.txt"
CORE_HASHES="$FINAL_BINARY_ROOT/core-hashes.sha256"
SOURCE_ARCHIVE="$FINAL_BINARY_ROOT/terrabase-source.tar"
SOURCE_ARCHIVE_HASH="$FINAL_BINARY_ROOT/terrabase-source.tar.sha256"
CLOSURE_HASHES="$FINAL_BINARY_ROOT/closure-sha256.txt"
PROVENANCE_RECEIPT="$FINAL_BINARY_ROOT/source-provenance/run-receipt.json"

for required_file in "$BINARY_DLL" "$FREEZE_MANIFEST" "$CORE_HASHES" "$SOURCE_ARCHIVE" "$SOURCE_ARCHIVE_HASH" "$CLOSURE_HASHES" "$PROVENANCE_RECEIPT"; do
    [[ -f "$required_file" ]] || die "frozen binary artifact is missing: $required_file"
done

verify_binary() {
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$CORE_HASHES") ||
        die "core assembly hash verification failed"
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$SOURCE_ARCHIVE_HASH") ||
        die "source archive hash verification failed"
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$CLOSURE_HASHES") ||
        die "frozen closure hash verification failed"
    tar -tf "$SOURCE_ARCHIVE" >/dev/null || die "source archive cannot be listed"
    rg -q '^workingTreeDigest=[0-9a-f]{64}$' "$FREEZE_MANIFEST" ||
        die "freeze manifest has no working tree digest"
    rg -q '^sourceArchiveSha256=[0-9a-f]{64}$' "$FREEZE_MANIFEST" ||
        die "freeze manifest has no source archive hash"
    jq -e '.status == "invalid"' "$PROVENANCE_RECEIPT" >/dev/null ||
        die "frozen benchmark provenance receipt is not an invalid preflight"
    manifest_repository=$(awk -F= '$1 == "repository" {print $2}' "$FREEZE_MANIFEST")
    manifest_commit=$(awk -F= '$1 == "commit" {print $2}' "$FREEZE_MANIFEST")
    manifest_tree=$(awk -F= '$1 == "treeDigest" {print $2}' "$FREEZE_MANIFEST")
    manifest_working_tree=$(awk -F= '$1 == "workingTreeDigest" {print $2}' "$FREEZE_MANIFEST")
    receipt_project=$(jq -er '.subject.project' "$PROVENANCE_RECEIPT") ||
        die "frozen benchmark provenance receipt has no subject project"
    receipt_commit=$(jq -er '.subject.commit' "$PROVENANCE_RECEIPT") ||
        die "frozen benchmark provenance receipt has no subject commit"
    receipt_tree=$(jq -er '.subject.treeDigest' "$PROVENANCE_RECEIPT") ||
        die "frozen benchmark provenance receipt has no subject tree digest"
    receipt_working_tree=$(jq -er '.subject.workingTreeDigest' "$PROVENANCE_RECEIPT") ||
        die "frozen benchmark provenance receipt has no subject working tree digest"
    [[ -n "$manifest_repository" && "$receipt_project" == "$manifest_repository/src/TerraBase/TerraBase.csproj" ]] ||
        die "frozen benchmark provenance project does not match the freeze repository"
    [[ "$receipt_commit" == "$manifest_commit" ]] ||
        die "frozen benchmark provenance commit does not match the freeze manifest"
    [[ "$receipt_tree" == "$manifest_tree" ]] ||
        die "frozen benchmark provenance tree digest does not match the freeze manifest"
    [[ "$receipt_working_tree" == "$manifest_working_tree" ]] ||
        die "frozen benchmark provenance working tree digest does not match the freeze manifest"
    printf 'Frozen binary verification passed: %s\n' "$FINAL_BINARY_ROOT"
    printf 'Working tree identity: %s\n' "$(awk -F= '$1 == "workingTreeDigest" {print $2}' "$FREEZE_MANIFEST")"
    cat "$CORE_HASHES"
}

if [[ "$MODE" == verify ]]; then
    verify_binary
    exit 0
fi

[[ "$DETACH" == 0 || "$DETACH" == 1 ]] || die "invalid detach mode"
verify_binary
[[ -r "$CREDENTIAL_FILE" ]] || die "private RustFS credential script is unreadable: $CREDENTIAL_FILE"
[[ -d "$RUSTFS_DATA_PATH" ]] || die "RustFS data root is unavailable: $RUSTFS_DATA_PATH"

if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
   systemctl --user list-units --type=service --state=running --no-legend 2>/dev/null |
       rg -qi 'terrabase.*(strict|e2e|state-map)|state-map.*terrabase'; then
    die "an active TerraBase strict/E2E/state-map service is running; wait for the quiet window"
fi
if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] && pgrep -af 'TerraBase\.Benchmarks(\.dll|/)' >/dev/null 2>&1; then
    die "an active TerraBase benchmark process is running; wait for the quiet window"
fi

HTTP_STATUS=$(curl --silent --show-error --max-time 5 -o /dev/null -w '%{http_code}' "$S3_ENDPOINT" || true)
case "$HTTP_STATUS" in
    400|403|405) ;;
    *) die "RustFS endpoint preflight returned HTTP $HTTP_STATUS; expected an unauthenticated S3 response" ;;
esac

STAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUTPUT_ROOT=${TERRABASE_E2E_OUTPUT_ROOT:-"$BENCH_ROOT/bench/receipts/e2e-resource-rustfs-1m-final-$STAMP"}
TEMP_ROOT=${TERRABASE_E2E_TEMP_ROOT:-"$OUTPUT_ROOT-tmp"}
LAUNCH_ROOT="$OUTPUT_ROOT.launch"
UNIT_NAME="terrabase-e2e-rustfs-1m-final-$STAMP"

[[ ! -e "$OUTPUT_ROOT" ]] || die "output root already exists; root-loss recovery requires a fresh path: $OUTPUT_ROOT"
[[ ! -e "$TEMP_ROOT" ]] || die "temporary root already exists; use a fresh path: $TEMP_ROOT"
[[ ! -e "$LAUNCH_ROOT" ]] || die "launch metadata root already exists: $LAUNCH_ROOT"
mkdir -p "$LAUNCH_ROOT" "$TEMP_ROOT"

WRAPPER="$LAUNCH_ROOT/run-wrapper.sh"
BENCH_ARGS=(
    --workload e2e-volume
    --object-tier s3
    --documents 1000000
    --batch-size 8192
    --point-reads 10000
    --updates 1000
    --deletes 1000
    --queries 1000
    --relationships 10000
    --seed 1337
    --checkpoint-records 500000
    --resource-sample-ms 10000
    --cache-bytes 16777216
    --max-local-storage-bytes 68719476736
    --min-free-space-bytes 107374182400
    --rustfs-data-path "$RUSTFS_DATA_PATH"
    --require-isolated-cgroup
    --root-loss-recovery
    --temp-root "$TEMP_ROOT"
    --output "$OUTPUT_ROOT"
)

{
    printf '%s\n' '#!/usr/bin/env bash' 'set -euo pipefail'
    printf 'source %q\n' "$CREDENTIAL_FILE"
    printf 'export TERRABASE_S3_ENDPOINT=%q TERRABASE_S3_REGION=%q\n' "$S3_ENDPOINT" "$S3_REGION"
    printf 'export TMPDIR=%q TMP=%q TEMP=%q\n' "$TEMP_ROOT" "$TEMP_ROOT" "$TEMP_ROOT"
    printf 'exec /run/current-system/sw/bin/nice -n 19 dotnet %q \\\n' "$BINARY_DLL"
    for argument in "${BENCH_ARGS[@]}"; do
        printf '  %q \\\n' "$argument"
    done
    printf '\n'
} > "$WRAPPER"
chmod 700 "$WRAPPER"

cp "$FREEZE_MANIFEST" "$LAUNCH_ROOT/freeze-manifest.txt"
cp "$CORE_HASHES" "$LAUNCH_ROOT/core-hashes.sha256"
cp "$SOURCE_ARCHIVE_HASH" "$LAUNCH_ROOT/terrabase-source.tar.sha256"
sha256sum "$BINARY_DLL" > "$LAUNCH_ROOT/benchmark-assembly.sha256"
WRAPPER_SHA256=$(sha256sum "$WRAPPER" | awk '{print $1}')

{
    printf 'schema=terrabase-e2e-rustfs-1m-launch-v1\n'
    printf 'createdAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'unit=%s\n' "$UNIT_NAME"
    printf 'runtimeMax=%s\n' "$RUNTIME_MAX"
    printf 'memoryMaxBytes=4294967296\n'
    printf 'memorySwapMaxBytes=0\n'
    printf 'oomPolicy=stop\n'
    printf 'binaryRoot=%s\n' "$FINAL_BINARY_ROOT"
    printf 'binary=%s\n' "$BINARY_DLL"
    printf 'freezeManifest=%s\n' "$FREEZE_MANIFEST"
    printf 'outputRoot=%s\n' "$OUTPUT_ROOT"
    printf 'temporaryRoot=%s\n' "$TEMP_ROOT"
    printf 'launchRoot=%s\n' "$LAUNCH_ROOT"
    printf 'credentialFile=%s\n' "$CREDENTIAL_FILE"
    printf 's3Endpoint=%s\n' "$S3_ENDPOINT"
    printf 's3Region=%s\n' "$S3_REGION"
    printf 'rustFsDataPath=%s\n' "$RUSTFS_DATA_PATH"
    printf 'commandWrapper=%s\n' "$WRAPPER"
    printf 'commandWrapperSha256=%s\n' "$WRAPPER_SHA256"
    printf 'benchmarkArgs='; printf '%q ' "${BENCH_ARGS[@]}"; printf '\n'
} > "$LAUNCH_ROOT/launch-manifest.txt"

SYSTEMD_ARGS=(
    --user
    --unit="$UNIT_NAME"
    --property=MemoryMax=4294967296
    --property=MemorySwapMax=0
    --property=OOMPolicy=stop
    --property="RuntimeMaxSec=$RUNTIME_MAX"
    --working-directory="$BENCH_ROOT"
)

set +e
if [[ "$DETACH" == 1 ]]; then
    systemd-run "${SYSTEMD_ARGS[@]}" /run/current-system/sw/bin/bash "$WRAPPER" \
        > "$LAUNCH_ROOT/systemd-run.log" 2>&1
else
    systemd-run "${SYSTEMD_ARGS[@]}" --wait /run/current-system/sw/bin/bash "$WRAPPER" \
        > "$LAUNCH_ROOT/systemd-run.log" 2>&1
fi
SERVICE_EXIT=$?
set -e
printf 'serviceExit=%s\n' "$SERVICE_EXIT" >> "$LAUNCH_ROOT/launch-manifest.txt"

if [[ "$DETACH" == 0 && "$SERVICE_EXIT" == 0 ]]; then
    [[ -f "$OUTPUT_ROOT/run-receipt.json" ]] || die "service exited successfully without run-receipt.json"
fi

printf 'Launch metadata: %s\n' "$LAUNCH_ROOT"
printf 'Output root: %s\n' "$OUTPUT_ROOT"
printf 'Unit: %s\n' "$UNIT_NAME"
exit "$SERVICE_EXIT"
