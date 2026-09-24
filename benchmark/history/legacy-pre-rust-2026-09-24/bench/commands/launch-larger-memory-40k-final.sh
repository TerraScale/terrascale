#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

# Launch the final frozen larger-than-memory qualification.  This benchmark is
# local StorageEngine only; it deliberately has no S3 credential or endpoint
# handling.  All output is kept under ts-benchmark/bench.
#
# Verification only:
#   ./launch-larger-memory-40k-final.sh --verify --binary-root <frozen-dir>
#
# Launch and wait for the finite systemd unit:
#   ./launch-larger-memory-40k-final.sh --binary-root <frozen-dir>
#
# Set TERRABASE_FINAL_BINARY_ROOT when --binary-root is omitted.  Auto-detect
# is allowed only when exactly one final frozen directory exists.

BENCH_ROOT=${BENCH_ROOT:-/home/mariogk/Projects/TerraScale/ts-benchmark}
FINAL_BINARY_ROOT=${TERRABASE_FINAL_BINARY_ROOT:-}
OUTPUT_ROOT=${TERRABASE_LARGER_MEMORY_OUTPUT_ROOT:-}
DOTNET_BIN=${TERRABASE_DOTNET_BIN:-/home/mariogk/.local/share/mise/dotnet-root/dotnet}
RUNTIME_MAX=${TERRABASE_LARGER_MEMORY_RUNTIME_MAX:-2h}
MODE=launch

readonly MEMORY_MAX_BYTES=268435456
readonly MEMORY_SWAP_MAX_BYTES=0
readonly MIN_FREE_BYTES=10737418240
readonly DATASET_SIZE=40000
readonly PAYLOAD_SIZE=8192
readonly OPERATIONS=100
readonly WRITES=100
readonly DELETES=1000
readonly WARMUP=100

usage() {
    cat <<'HELP'
Usage:
  launch-larger-memory-40k-final.sh [--binary-root DIR]
  launch-larger-memory-40k-final.sh --verify [--binary-root DIR]

Environment:
  TERRABASE_FINAL_BINARY_ROOT       Frozen Release directory, if omitted
  TERRABASE_LARGER_MEMORY_OUTPUT_ROOT
                                    Fresh output directory override
  TERRABASE_LARGER_MEMORY_RUNTIME_MAX
                                    systemd RuntimeMaxSec (default: 2h)
  TERRABASE_DOTNET_BIN              .NET runtime path

The launch uses the fixed qualification workload:
  storage-engine, 40,000 documents, 8,192-byte incompressible payloads,
  one sample, 100 operations, 100 writes, 1,000 deletes, 100 warmup
  operations, concurrency 1, Zipf exponent 1.2, and read ratio 0.8.

The workload itself verifies point reads, ordered scans, cold reopens, delete
absence and survivor bodies, and compaction.  The launcher additionally checks
the frozen source identity, assembly hashes, receipts, and cgroup envelope.
HELP
}

die() {
    printf 'launch-larger-memory-40k-final: %s\n' "$*" >&2
    exit 1
}

for required_command in awk bash date df find jq nice pgrep realpath rg sha256sum sort systemctl systemd-run tar; do
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
        --output-root)
            (($# >= 2)) || die "--output-root requires a directory"
            OUTPUT_ROOT=$2
            shift 2
            ;;
        --output-root=*)
            OUTPUT_ROOT=${1#*=}
            shift
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

[[ "$RUNTIME_MAX" =~ ^[1-9][0-9]*(ms|us|s|min|h|d)?$ ]] ||
    die "runtime max must be a finite positive systemd time span such as 2h"

BENCH_ROOT=$(realpath -e -- "$BENCH_ROOT") || die "benchmark root is unavailable: $BENCH_ROOT"
BENCH_RECEIPTS="$BENCH_ROOT/bench/receipts"
[[ -d "$BENCH_RECEIPTS" ]] || die "benchmark receipts directory is missing: $BENCH_RECEIPTS"

resolve_binary_root() {
    if [[ -n "$FINAL_BINARY_ROOT" ]]; then
        FINAL_BINARY_ROOT=$(realpath -e -- "$FINAL_BINARY_ROOT") ||
            die "frozen binary directory is unavailable"
        return
    fi

    local binary_parent="$BENCH_ROOT/bench/binaries"
    [[ -d "$binary_parent" ]] || die "binary parent is missing: $binary_parent"
    mapfile -t candidates < <(
        find "$binary_parent" -mindepth 1 -maxdepth 1 -type d \
            -name 'terrabase-final-source-matched-release-*' -printf '%p\n' | sort
    )
    case "${#candidates[@]}" in
        1) FINAL_BINARY_ROOT=$(realpath -e -- "${candidates[0]}") ;;
        0) die "no terrabase-final-source-matched-release-* directory found; pass --binary-root" ;;
        *) die "multiple frozen binary directories found; pass --binary-root explicitly" ;;
    esac
}

manifest_value() {
    local key=$1
    awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); found = 1; exit } END { if (!found) exit 1 }' "$FREEZE_MANIFEST"
}

required_manifest_value() {
    local key=$1
    local value
    value=$(manifest_value "$key" 2>/dev/null || true)
    [[ -n "$value" ]] || die "freeze manifest is missing required key: $key"
    printf '%s\n' "$value"
}

verify_binary() {
    [[ -d "$FINAL_BINARY_ROOT" ]] || die "frozen binary directory is missing: $FINAL_BINARY_ROOT"
    BINARY_DLL="$FINAL_BINARY_ROOT/TerraBase.Benchmarks.dll"
    FREEZE_MANIFEST="$FINAL_BINARY_ROOT/freeze-manifest.txt"
    CORE_HASHES="$FINAL_BINARY_ROOT/core-hashes.sha256"
    SOURCE_ARCHIVE="$FINAL_BINARY_ROOT/terrabase-source.tar"
    SOURCE_ARCHIVE_HASH="$FINAL_BINARY_ROOT/terrabase-source.tar.sha256"
    CLOSURE_HASHES="$FINAL_BINARY_ROOT/closure-sha256.txt"
    PROVENANCE_RECEIPT="$FINAL_BINARY_ROOT/source-provenance/run-receipt.json"

    for required_file in "$BINARY_DLL" "$FREEZE_MANIFEST" "$CORE_HASHES" \
        "$SOURCE_ARCHIVE" "$SOURCE_ARCHIVE_HASH" "$CLOSURE_HASHES" "$PROVENANCE_RECEIPT"; do
        [[ -f "$required_file" ]] || die "frozen binary artifact is missing: $required_file"
    done

    [[ "$(required_manifest_value schema)" == terrabase-final-source-matched-release-v1 ]] ||
        die "freeze manifest does not use terrabase-final-source-matched-release-v1"
    [[ "$(required_manifest_value buildConfiguration)" == Release ]] ||
        die "frozen artifact is not a Release build"

    SOURCE_REPOSITORY=$(required_manifest_value repository)
    SOURCE_COMMIT=$(required_manifest_value commit)
    SOURCE_TREE_DIGEST=$(required_manifest_value treeDigest)
    SOURCE_WORKTREE_DIGEST=$(required_manifest_value workingTreeDigest)
    SOURCE_DIRTY=$(required_manifest_value sourceDirty)
    MANIFEST_DOTNET_VERSION=$(required_manifest_value dotnetVersion)
    MANIFEST_BINARY_CLOSURE=$(required_manifest_value binaryClosure)

    [[ "$MANIFEST_BINARY_CLOSURE" == "$FINAL_BINARY_ROOT" ]] ||
        die "freeze manifest binaryClosure does not match the selected directory"
    [[ "$SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]] || die "freeze manifest commit is invalid"
    [[ "$SOURCE_TREE_DIGEST" =~ ^[0-9a-fA-F]{40}$ ]] || die "freeze manifest treeDigest is invalid"
    [[ "$SOURCE_WORKTREE_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]] || die "freeze manifest workingTreeDigest is invalid"
    [[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]] || die "freeze manifest sourceDirty is invalid"

    BENCH_SHA256=$(sha256sum "$BINARY_DLL" | awk '{ print $1 }')
    MANIFEST_BENCH_SHA256=$(awk '$NF ~ /(^|\/)TerraBase[.]Benchmarks[.]dll$/ { print $1; exit }' "$CORE_HASHES")
    [[ "$BENCH_SHA256" == "$MANIFEST_BENCH_SHA256" ]] ||
        die "TerraBase.Benchmarks.dll does not match core-hashes.sha256"
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$CORE_HASHES") ||
        die "core assembly hash verification failed"
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$SOURCE_ARCHIVE_HASH") ||
        die "source archive hash verification failed"
    (cd "$FINAL_BINARY_ROOT" && sha256sum --quiet -c "$CLOSURE_HASHES") ||
        die "frozen closure hash verification failed"
    tar -tf "$SOURCE_ARCHIVE" >/dev/null || die "source archive cannot be listed"

    jq -e --arg repository "$SOURCE_REPOSITORY" \
        --arg commit "$SOURCE_COMMIT" \
        --arg tree "$SOURCE_TREE_DIGEST" \
        --arg workingTree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        '.status == "invalid" and
         .subject.project == ($repository + "/src/TerraBase/TerraBase.csproj") and
         .subject.commit == $commit and
         .subject.treeDigest == $tree and
         .subject.workingTreeDigest == $workingTree and
         .subject.dirty == $dirty' "$PROVENANCE_RECEIPT" >/dev/null ||
        die "frozen provenance receipt does not match freeze-manifest identity"

    [[ -x "$DOTNET_BIN" ]] || die ".NET runtime is not executable: $DOTNET_BIN"
    [[ "$($DOTNET_BIN --version)" == "$MANIFEST_DOTNET_VERSION" ]] ||
        die ".NET runtime mismatch: manifest=$MANIFEST_DOTNET_VERSION actual=$($DOTNET_BIN --version)"
}

check_host() {
    [[ -r /sys/fs/cgroup/cgroup.controllers ]] || die "cgroup v2 is not available"
    systemctl --user show-environment >/dev/null 2>&1 || die "the user systemd manager is unavailable"
    local available
    available=$(df -B1 --output=avail "$BENCH_RECEIPTS" | awk 'NR == 2 { print $1 }')
    [[ "$available" =~ ^[0-9]+$ ]] || die "unable to read free space for $BENCH_RECEIPTS"
    (( available >= MIN_FREE_BYTES )) || die "free space $available is below required floor $MIN_FREE_BYTES"

    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
       systemctl --user list-units --type=service --state=running --no-legend 2>/dev/null |
           rg -qi 'terrabase.*(strict|e2e|larger-memory|state-map)|state-map.*terrabase'; then
        die "an active TerraBase benchmark service is running; wait for a quiet window"
    fi
    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
       pgrep -af 'TerraBase\.Benchmarks(\.dll|/)' >/dev/null 2>&1; then
        die "an active TerraBase benchmark process is running; wait for a quiet window"
    fi
}

write_wrapper() {
    local nice_bin
    nice_bin=$(command -v nice)
    {
        printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' "IFS=\$'\\n\\t'"
        printf 'OUTPUT_ROOT=%q\n' "$OUTPUT_ROOT"
        printf 'DOTNET_BIN=%q\n' "$DOTNET_BIN"
        printf 'BINARY_DLL=%q\n' "$BINARY_DLL"
        printf 'UNIT_NAME=%q\n' "$UNIT_NAME"
        printf 'NICE_BIN=%q\n' "$nice_bin"
        cat <<'WRAPPER'

CGROUP_REL=$(awk -F: '$1 == "0" { print $3; exit }' /proc/self/cgroup)
CGROUP_PATH="/sys/fs/cgroup${CGROUP_REL}"
for required_cgroup_file in memory.max memory.peak memory.swap.max memory.swap.current memory.events; do
    [[ -r "$CGROUP_PATH/$required_cgroup_file" ]] || {
        printf 'required cgroup file is unavailable: %s\n' "$CGROUP_PATH/$required_cgroup_file" >&2
        exit 78
    }
done

read_cgroup_file() {
    local path=$1
    if [[ -r "$path" ]]; then
        cat -- "$path"
    else
        printf '0\n'
    fi
}

read_event() {
    local name=$1
    awk -v name="$name" '$1 == name { print $2; found = 1; exit } END { if (!found) print 0 }' "$CGROUP_PATH/memory.events"
}

number_or_null() {
    if [[ "$1" =~ ^[0-9]+$ ]]; then
        printf '%s\n' "$1"
    else
        printf 'null\n'
    fi
}

write_resource_envelope() {
    local exit_code=$1
    local memory_max memory_peak memory_current swap_max swap_peak swap_current
    memory_max=$(read_cgroup_file "$CGROUP_PATH/memory.max")
    memory_peak=$(read_cgroup_file "$CGROUP_PATH/memory.peak")
    memory_current=$(read_cgroup_file "$CGROUP_PATH/memory.current")
    swap_max=$(read_cgroup_file "$CGROUP_PATH/memory.swap.max")
    swap_peak=$(read_cgroup_file "$CGROUP_PATH/memory.swap.peak")
    swap_current=$(read_cgroup_file "$CGROUP_PATH/memory.swap.current")
    jq -n \
        --arg schema 'terrabase-larger-memory-resource-v1' \
        --arg finishedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        --arg unit "$UNIT_NAME" \
        --arg controlGroup "$CGROUP_REL" \
        --argjson exitCode "$(number_or_null "$exit_code")" \
        --argjson memoryMax "$(number_or_null "$memory_max")" \
        --argjson memoryPeak "$(number_or_null "$memory_peak")" \
        --argjson memoryCurrent "$(number_or_null "$memory_current")" \
        --argjson memorySwapMax "$(number_or_null "$swap_max")" \
        --argjson memorySwapPeak "$(number_or_null "$swap_peak")" \
        --argjson memorySwapCurrent "$(number_or_null "$swap_current")" \
        --argjson oom "$(number_or_null "$(read_event oom)")" \
        --argjson oomKill "$(number_or_null "$(read_event oom_kill)")" \
        --argjson oomGroupKill "$(number_or_null "$(read_event oom_group_kill)")" \
        --argjson maxEvents "$(number_or_null "$(read_event max)")" \
        '{schema:$schema,finishedAtUtc:$finishedAtUtc,unit:$unit,controlGroup:$controlGroup,
          exitCode:$exitCode,memoryMax:$memoryMax,memoryPeak:$memoryPeak,memoryCurrent:$memoryCurrent,
          memorySwapMax:$memorySwapMax,memorySwapPeak:$memorySwapPeak,memorySwapCurrent:$memorySwapCurrent,
          oom:$oom,oomKill:$oomKill,oomGroupKill:$oomGroupKill,maxEvents:$maxEvents}' \
        > "$OUTPUT_ROOT/resource-envelope.json"
}

run_status=1
if "$NICE_BIN" -n 19 "$DOTNET_BIN" "$BINARY_DLL" \
    --workload larger-memory \
    --backend storage-engine \
    --memory-cap-bytes 268435456 \
    --dataset-size 40000 \
    --payload-size 8192 \
    --payload-mode per-key-incompressible \
    --samples 1 \
    --operations 100 \
    --writes 100 \
    --deletes 1000 \
    --warmup 100 \
    --concurrency 1 \
    --seed 1337 \
    --zipf-exponent 1.2 \
    --read-ratio 0.8 \
    --output "$OUTPUT_ROOT" \
    > "$OUTPUT_ROOT/console.log" 2>&1; then
    run_status=0
else
    run_status=$?
fi
write_resource_envelope "$run_status"
exit "$run_status"
WRAPPER
    } > "$WRAPPER"
    chmod 700 "$WRAPPER"
}

verify_run() {
    [[ -f "$RUN_RECEIPT" ]] || die "run receipt is missing: $RUN_RECEIPT"
    [[ -f "$LARGER_MEMORY_RECEIPT" ]] || die "larger-memory receipt is missing: $LARGER_MEMORY_RECEIPT"
    [[ -f "$RESOURCE_ENVELOPE" ]] || die "resource envelope is missing: $RESOURCE_ENVELOPE"

    jq -e --arg project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        --arg commit "$SOURCE_COMMIT" \
        --arg tree "$SOURCE_TREE_DIGEST" \
        --arg workingTree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        --arg output "$OUTPUT_ROOT" \
        '.status == "complete" and .workload == "larger-memory" and .outputDirectory == $output and
         .subject.project == $project and .subject.commit == $commit and
         .subject.treeDigest == $tree and .subject.workingTreeDigest == $workingTree and
         .subject.dirty == $dirty' "$RUN_RECEIPT" >/dev/null ||
        die "run receipt is incomplete or source identity does not match the frozen manifest"

    jq -e --arg binary "$BINARY_DLL" --arg assemblySha "$BENCH_SHA256" \
        '.status == "complete" and .environment.buildConfiguration == "Release" and
         .environment.artifactPath == $binary and .environment.artifactSha256 == $assemblySha and
         .configuration.backend == 0 and .configuration.datasetSize == 40000 and
         .configuration.payloadSize == 8192 and .configuration.payloadMode == 1 and
         .configuration.memoryCapBytes == 268435456 and .configuration.samples == 1 and
         .configuration.operationsPerSample == 100 and .configuration.sustainedWriteOperations == 100 and
         .configuration.deleteOperations == 1000 and .configuration.warmupOperations == 100 and
         .configuration.concurrency == 1 and .configuration.zipfExponent == 1.2 and
         .configuration.readRatio == 0.8 and .result.status == "complete" and
         .result.backend == "StorageEngine" and .result.datasetSize == 40000 and
         .result.payloadSize == 8192 and .result.payloadMode == "PerKeyIncompressible" and
         .result.deleteOperations == 1000 and
         .result.estimatedLogicalDatasetBytes > .result.configuredMemoryCapBytes and
         .result.seededStorageExceedsMemoryCap == true and (.result.samples | length) == 1 and
         (.result.samples[0].deletes.status == "complete") and
         (.result.samples[0].deletes.requestedCount == 1000) and
         (.result.samples[0].deletes.deletedCount == 1000) and
         (.result.samples[0].deletes.survivingCount == 39000) and
         (.result.samples[0].deletes.deletedAbsenceVerified == 1000) and
         (.result.samples[0].deletes.survivingBodiesVerified == 39000) and
         (.result.samples[0].deletes.coldReopenDeletedAbsenceVerified == 1000) and
         (.result.samples[0].deletes.coldReopenSurvivingBodiesVerified == 39000) and
         (any(.result.samples[0].phases[]; .name == "warm-cache" and .kind == "operations" and .operations == 100)) and
         (any(.result.samples[0].phases[]; .name == "cold-reopen" and .kind == "reopen" and .operations == 0)) and
         (any(.result.samples[0].phases[]; .name == "cold-cache" and .kind == "operations" and .operations == 100)) and
         (any(.result.samples[0].phases[]; .name == "uniform-read-heavy" and .kind == "operations" and .operations == 2000)) and
         (any(.result.samples[0].phases[]; .name == "sustained-writes" and .kind == "operations" and .operations == 100)) and
         (any(.result.samples[0].phases[]; .name == "deletes" and .kind == "delete" and .operations == 1000)) and
         (any(.result.samples[0].phases[]; .name == "delete-cold-reopen" and .kind == "reopen" and .operations == 0)) and
         (any(.result.samples[0].phases[]; .name == "compaction" and .kind == "compaction" and .operations == 1))' \
        "$LARGER_MEMORY_RECEIPT" >/dev/null ||
        die "larger-memory receipt does not prove the fixed workload phases and counts"

    jq -e \
        '.schema == "terrabase-larger-memory-resource-v1" and .exitCode == 0 and
         .memoryMax == 268435456 and .memorySwapMax == 0 and .memoryPeak <= 268435456 and
         .memorySwapPeak == 0 and .memorySwapCurrent == 0 and .oom == 0 and
         .oomKill == 0 and .oomGroupKill == 0' "$RESOURCE_ENVELOPE" >/dev/null ||
        die "resource envelope does not prove a 256 MiB zero-swap, zero-OOM run"

    local unit_status
    unit_status=$(systemctl --user show "$UNIT_NAME" -p ExecMainStatus --value 2>/dev/null || true)
    [[ "$unit_status" == 0 ]] || die "systemd unit ExecMainStatus is not zero: ${unit_status:-missing}"
    systemctl --user show "$UNIT_NAME" \
        -p Id -p ActiveState -p Result -p ExecMainStatus -p MemoryCurrent -p MemoryPeak \
        -p MemoryMax -p MemorySwapMax -p OOMPolicy > "$LAUNCH_ROOT/cgroup-runner-status.txt" 2>&1 ||
        die "unable to write the systemd resource status receipt"
}

resolve_binary_root
verify_binary
printf 'Frozen binary verification passed: %s\n' "$FINAL_BINARY_ROOT"

if [[ "$MODE" == verify ]]; then
    printf 'Working-tree identity: %s\n' "$SOURCE_WORKTREE_DIGEST"
    printf 'Assembly SHA-256: %s\n' "$BENCH_SHA256"
    exit 0
fi

check_host
[[ -n "$OUTPUT_ROOT" ]] || OUTPUT_ROOT="$BENCH_RECEIPTS/larger-memory-final-40k-8k-256m-$(date -u +%Y%m%dT%H%M%S%NZ)"
OUTPUT_ROOT=$(realpath -m -- "$OUTPUT_ROOT")
case "$OUTPUT_ROOT" in
    "$BENCH_ROOT/bench"/*) ;;
    *) die "output root must be under $BENCH_ROOT/bench: $OUTPUT_ROOT" ;;
esac
LAUNCH_ROOT="$OUTPUT_ROOT.launch"
[[ ! -e "$OUTPUT_ROOT" ]] || die "output root already exists; a fresh run directory is required: $OUTPUT_ROOT"
[[ ! -L "$OUTPUT_ROOT" ]] || die "output root must not be a symlink: $OUTPUT_ROOT"
[[ ! -e "$LAUNCH_ROOT" ]] || die "launch metadata root already exists: $LAUNCH_ROOT"
[[ ! -L "$LAUNCH_ROOT" ]] || die "launch metadata root must not be a symlink: $LAUNCH_ROOT"

STAMP=$(date -u +%Y%m%dT%H%M%S%NZ)
UNIT_NAME="terrabase-larger-memory-final-40k-8k-256m-$STAMP"
WRAPPER="$LAUNCH_ROOT/run-wrapper.sh"
RUN_RECEIPT="$OUTPUT_ROOT/run-receipt.json"
LARGER_MEMORY_RECEIPT="$OUTPUT_ROOT/larger-memory-receipt.json"
RESOURCE_ENVELOPE="$OUTPUT_ROOT/resource-envelope.json"

mkdir -p "$OUTPUT_ROOT" "$LAUNCH_ROOT"
write_wrapper
cp "$FREEZE_MANIFEST" "$LAUNCH_ROOT/freeze-manifest.txt"
cp "$CORE_HASHES" "$LAUNCH_ROOT/core-hashes.sha256"
sha256sum "$BINARY_DLL" > "$OUTPUT_ROOT/binary-sha256.txt"
WRAPPER_SHA256=$(sha256sum "$WRAPPER" | awk '{ print $1 }')

{
    printf 'schema=terrabase-larger-memory-40k-launch-v1\n'
    printf 'createdAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'unit=%s\n' "$UNIT_NAME"
    printf 'runtimeMax=%s\n' "$RUNTIME_MAX"
    printf 'memoryMaxBytes=%s\n' "$MEMORY_MAX_BYTES"
    printf 'memorySwapMaxBytes=%s\n' "$MEMORY_SWAP_MAX_BYTES"
    printf 'oomPolicy=stop\n'
    printf 'binaryRoot=%s\n' "$FINAL_BINARY_ROOT"
    printf 'binary=%s\n' "$BINARY_DLL"
    printf 'binarySha256=%s\n' "$BENCH_SHA256"
    printf 'sourceRepository=%s\n' "$SOURCE_REPOSITORY"
    printf 'sourceCommit=%s\n' "$SOURCE_COMMIT"
    printf 'sourceTreeDigest=%s\n' "$SOURCE_TREE_DIGEST"
    printf 'sourceWorkingTreeDigest=%s\n' "$SOURCE_WORKTREE_DIGEST"
    printf 'sourceDirty=%s\n' "$SOURCE_DIRTY"
    printf 'outputRoot=%s\n' "$OUTPUT_ROOT"
    printf 'launchRoot=%s\n' "$LAUNCH_ROOT"
    printf 'commandWrapper=%s\n' "$WRAPPER"
    printf 'commandWrapperSha256=%s\n' "$WRAPPER_SHA256"
    printf 'benchmarkFlags=--workload larger-memory --backend storage-engine --memory-cap-bytes 268435456 --dataset-size 40000 --payload-size 8192 --payload-mode per-key-incompressible --samples 1 --operations 100 --writes 100 --deletes 1000 --warmup 100 --concurrency 1 --seed 1337 --zipf-exponent 1.2 --read-ratio 0.8\n'
} > "$LAUNCH_ROOT/launch-manifest.txt"

SYSTEMD_ARGS=(
    --user
    --unit="$UNIT_NAME"
    --property=MemoryMax="$MEMORY_MAX_BYTES"
    --property=MemorySwapMax="$MEMORY_SWAP_MAX_BYTES"
    --property=OOMPolicy=stop
    --property="RuntimeMaxSec=$RUNTIME_MAX"
    --working-directory="$BENCH_ROOT"
)

set +e
systemd-run "${SYSTEMD_ARGS[@]}" --wait /run/current-system/sw/bin/bash "$WRAPPER" \
    > "$LAUNCH_ROOT/systemd-run.log" 2>&1
SERVICE_EXIT=$?
set -e
printf 'serviceExit=%s\n' "$SERVICE_EXIT" >> "$LAUNCH_ROOT/launch-manifest.txt"

if [[ "$SERVICE_EXIT" != 0 ]]; then
    printf 'Workload service exited with status %s; receipts are retained for diagnosis.\n' "$SERVICE_EXIT" >&2
fi
verify_run

printf 'Launch metadata: %s\n' "$LAUNCH_ROOT"
printf 'Output root: %s\n' "$OUTPUT_ROOT"
printf 'Unit: %s\n' "$UNIT_NAME"
exit "$SERVICE_EXIT"
