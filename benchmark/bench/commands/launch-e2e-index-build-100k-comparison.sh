#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

# Run the controlled local E2E automatic-index snapshot batch comparison.
# Each lane gets a fresh output/temp root and its own finite systemd cgroup;
# lanes are deliberately run one at a time.  The script writes only under
# ts-benchmark/bench and never changes the TerraBase checkout.
#
# Launch both lanes and wait for them:
#   ./launch-e2e-index-build-100k-comparison.sh --binary-root <frozen-dir>
#
# Verify existing lanes without launching a workload:
#   ./launch-e2e-index-build-100k-comparison.sh --verify --binary-root <frozen-dir>
#
# The default output names match e2e-index-build-100k-comparison-plan.md.
# Pass --output-base when a fresh alternate prefix is needed.

BENCH_ROOT=${BENCH_ROOT:-/home/mariogk/Projects/TerraScale/ts-benchmark}
FINAL_BINARY_ROOT=${TERRABASE_FINAL_BINARY_ROOT:-}
OUTPUT_BASE=${TERRABASE_INDEX_COMPARISON_OUTPUT_BASE:-}
DOTNET_BIN=${TERRABASE_DOTNET_BIN:-/home/mariogk/.local/share/mise/dotnet-root/dotnet}
RUNTIME_MAX=${TERRABASE_INDEX_COMPARISON_RUNTIME_MAX:-8h}
MODE=launch

readonly MEMORY_MAX_BYTES=4294967296
readonly MEMORY_SWAP_MAX_BYTES=0
readonly LOCAL_STORAGE_CAP_BYTES=68719476736
readonly MIN_FREE_SPACE_BYTES=107374182400
readonly DOCUMENTS=100000
readonly BATCH_SIZE=4096
readonly POINT_READS=0
readonly UPDATES=1
readonly DELETES=1
readonly QUERIES=1
readonly RELATIONSHIPS=0
readonly SEED=1337
readonly CHECKPOINT_RECORDS=10000
readonly RESOURCE_SAMPLE_MS=1000
readonly CACHE_BYTES=16777216

ACTIVE_UNIT=
ACTIVE_LAUNCH_ROOT=

usage() {
    cat <<'HELP'
Usage:
  launch-e2e-index-build-100k-comparison.sh [--binary-root DIR] [--output-base PREFIX]
  launch-e2e-index-build-100k-comparison.sh --verify [--binary-root DIR] [--output-base PREFIX]

Options:
  --binary-root DIR  Frozen source-matched Release directory.
  --output-base PATH Fresh output prefix; lanes are PATH-256 and PATH-1024.
  --runtime-max SPAN systemd RuntimeMaxSec (default: 8h).
  --dotnet PATH      .NET runtime (default: frozen-release runtime path).

The fixed local workload is 100,000 documents, batches of 4,096, zero point
reads, one update, one delete, one indexed query, zero relationships, seed
1337, and snapshot build limits 256 then 1024.  Each lane uses a 4 GiB,
zero-swap cgroup, nice 19, a 64 GiB local-storage cap, and a 100 GiB free
space floor.
HELP
}

die() {
    printf 'launch-e2e-index-build-100k-comparison: %s\n' "$*" >&2
    exit 1
}

handle_signal() {
    local signal=$1
    local exit_code=130
    [[ "$signal" == TERM ]] && exit_code=143
    trap - INT TERM
    if [[ -n "${ACTIVE_UNIT:-}" ]]; then
        if [[ -n "${ACTIVE_LAUNCH_ROOT:-}" ]]; then
            {
                printf 'signal=%s\n' "$signal"
                printf 'unit=%s\n' "$ACTIVE_UNIT"
                printf 'stoppingAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
            } >> "$ACTIVE_LAUNCH_ROOT/signal-received.txt"
        fi
        systemctl --user stop "$ACTIVE_UNIT" >/dev/null 2>&1 || true
    fi
    exit "$exit_code"
}

trap 'handle_signal INT' INT
trap 'handle_signal TERM' TERM

for required_command in awk bash date df find jq nice pgrep realpath rg sha256sum sort stat systemctl systemd-run tar; do
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
        --output-base)
            (($# >= 2)) || die "--output-base requires a path"
            OUTPUT_BASE=$2
            shift 2
            ;;
        --output-base=*)
            OUTPUT_BASE=${1#*=}
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
        --dotnet)
            (($# >= 2)) || die "--dotnet requires a path"
            DOTNET_BIN=$2
            shift 2
            ;;
        --dotnet=*)
            DOTNET_BIN=${1#*=}
            shift
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

[[ "$RUNTIME_MAX" =~ ^[1-9][0-9]*(ms|us|s|min|h|d)?$ ]] ||
    die "runtime max must be a finite positive systemd time span such as 8h"

BENCH_ROOT=$(realpath -e -- "$BENCH_ROOT") || die "benchmark root is unavailable: $BENCH_ROOT"
BENCH_RECEIPTS="$BENCH_ROOT/bench/receipts"
[[ -d "$BENCH_RECEIPTS" ]] || die "benchmark receipts directory is missing: $BENCH_RECEIPTS"

resolve_binary_root() {
    if [[ -n "$FINAL_BINARY_ROOT" ]]; then
        FINAL_BINARY_ROOT=$(realpath -e -- "$FINAL_BINARY_ROOT") ||
            die "frozen binary directory is unavailable: $FINAL_BINARY_ROOT"
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
    (( available >= MIN_FREE_SPACE_BYTES )) ||
        die "free space $available is below required floor $MIN_FREE_SPACE_BYTES"

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

reject_symlink_ancestors() {
    local candidate=$1
    [[ "$candidate" == /* ]] || die "path must be absolute for symlink checking: $candidate"
    local current="$candidate"
    while [[ "$current" != "/" ]]; do
        [[ ! -L "$current" ]] || die "refusing a symlinked output-base component: $current"
        current=$(dirname -- "$current")
    done
}

verify_artifact_index() {
    local run_root=$1
    local detailed="$run_root/e2e-volume-receipt.json"
    local detailed_bytes detailed_sha
    detailed_bytes=$(stat -c %s "$detailed")
    detailed_sha=$(sha256sum "$detailed" | awk '{ print $1 }')
    jq -e --arg file "$(basename "$detailed")" --arg sha "$detailed_sha" \
        --argjson bytes "$detailed_bytes" \
        'any(.artifacts[]?; .file == $file and .sha256 == $sha and .bytes == $bytes)' \
        "$run_root/run-receipt.json" >/dev/null ||
        die "run receipt does not index the detailed receipt correctly: $run_root"
}

verify_query_diagnostics() {
    local limit=$1
    local output_root="$OUTPUT_BASE-$limit"
    local detailed="$output_root/e2e-volume-receipt.json"
    local samples="$output_root/indexed-query-samples.jsonl"
    local worst="$output_root/indexed-query-worst.md"

    [[ -s "$samples" ]] || die "indexed query samples are missing or empty: $output_root"
    [[ -s "$worst" ]] || die "indexed query worst-sample report is missing or empty: $output_root"
    [[ "$(wc -l < "$samples")" == 1 ]] ||
        die "indexed query sample count is not exactly one: $output_root"
    jq -s -e '
        length == 1 and
        ([.[].indexName] | unique | length) == 1 and
        (.[0].indexName | type) == "string" and
        (.[0].indexName | startswith("automatic-")) and
        (.[0].indexName | length) > 10 and
        .[0].usedSnapshotFallback == false and
        .[0].query == 0 and .[0].resultCount > 0 and
        .[0].entryReads != null and .[0].fullDocumentReads != null and
        .[0].rowsExamined >= .[0].resultCount and .[0].workUnits > 0' \
        "$samples" >/dev/null ||
        die "indexed query sample does not prove an active automatic index without fallback: $output_root"
    jq -e \
        '.volume.queryDiagnostics.samples == 1 and
         .volume.queryDiagnostics.sampleJsonl == "indexed-query-samples.jsonl" and
         .volume.queryDiagnostics.worstMarkdown == "indexed-query-worst.md" and
         .volume.queryDiagnostics.physicalCountersAvailable == true and
         .volume.queryDiagnostics.totalRowsExamined > 0 and
         .volume.queryDiagnostics.totalWorkUnits > 0 and
         .volume.queryDiagnostics.totalEntryReads > 0 and
         .volume.queryDiagnostics.totalFullDocumentReads > 0' "$detailed" >/dev/null ||
        die "detailed receipt does not prove independent query diagnostics: $output_root"
    rg -Fq '1 deterministic indexed query samples' "$worst" ||
        die "indexed query worst-sample report has the wrong sample count: $output_root"
    rg -Fq '| Query |' "$worst" || die "indexed query worst-sample report has no table: $output_root"
}

verify_lane() {
    local limit=$1
    local output_root="$OUTPUT_BASE-$limit"
    local launch_root="$output_root.launch"
    local detailed="$output_root/e2e-volume-receipt.json"
    local compact="$output_root/run-receipt.json"
    local summary="$output_root/e2e-volume-summary.md"
    local envelope="$output_root/resource-envelope.json"

    [[ -d "$output_root" && ! -L "$output_root" ]] || die "lane output root is missing or a symlink: $output_root"
    [[ -d "$launch_root" && ! -L "$launch_root" ]] || die "lane launch root is missing or a symlink: $launch_root"
    for required_file in "$detailed" "$compact" "$summary" "$envelope" \
        "$output_root/resource-samples.jsonl" "$output_root/progress.jsonl" \
        "$launch_root/launch-manifest.txt" "$launch_root/run-wrapper.sh" \
        "$launch_root/systemd-run.log" "$launch_root/cgroup-runner-status.txt"; do
        [[ -s "$required_file" ]] || die "lane evidence is missing or empty: $required_file"
    done
    cmp -s "$FREEZE_MANIFEST" "$launch_root/freeze-manifest.txt" ||
        die "lane freeze manifest differs from the selected frozen binary: $output_root"
    cmp -s "$CORE_HASHES" "$launch_root/core-hashes.sha256" ||
        die "lane core hashes differ from the selected frozen binary: $output_root"
    rg -Fxq "limit=$limit" "$launch_root/launch-manifest.txt" ||
        die "lane manifest has the wrong snapshot limit: $output_root"
    rg -Fxq "binarySha256=$BENCH_SHA256" "$launch_root/launch-manifest.txt" ||
        die "lane manifest does not pin the selected benchmark assembly: $output_root"
    rg -Fxq "sourceCommit=$SOURCE_COMMIT" "$launch_root/launch-manifest.txt" ||
        die "lane manifest source commit does not match the frozen provenance: $output_root"
    rg -Fxq "memoryMaxBytes=$MEMORY_MAX_BYTES" "$launch_root/launch-manifest.txt" ||
        die "lane manifest does not pin the 4 GiB memory limit: $output_root"
    rg -Fxq "memorySwapMaxBytes=$MEMORY_SWAP_MAX_BYTES" "$launch_root/launch-manifest.txt" ||
        die "lane manifest does not pin zero swap: $output_root"
    rg -Fxq "nice=19" "$launch_root/launch-manifest.txt" ||
        die "lane manifest does not pin nice 19: $output_root"
    [[ "$(awk -F= '$1 == "serviceExit" { print $2; exit }' "$launch_root/launch-manifest.txt")" == 0 ]] ||
        die "lane serviceExit was not zero: $output_root"
    lane_unit=$(awk -F= '$1 == "unit" { print $2; exit }' "$launch_root/launch-manifest.txt")
    [[ "$lane_unit" =~ ^terrabase-e2e-index-build-100k-(256|1024)-[0-9]{8}T[0-9]{15}Z$ ]] ||
        die "lane manifest has no valid transient unit name: $output_root"
    rg -Fxq "Id=$lane_unit" "$launch_root/cgroup-runner-status.txt" ||
        die "lane cgroup status names a different unit: $output_root"
    rg -Fxq 'Result=success' "$launch_root/cgroup-runner-status.txt" ||
        die "lane systemd result was not success: $output_root"
    rg -Fxq 'ExecMainStatus=0' "$launch_root/cgroup-runner-status.txt" ||
        die "lane systemd ExecMainStatus was not zero: $output_root"
    rg -Eq '^ActiveState=(inactive|deactivating)$' "$launch_root/cgroup-runner-status.txt" ||
        die "lane systemd unit did not finish inactive: $output_root"
    manifest_wrapper_sha=$(awk -F= '$1 == "commandWrapperSha256" { print $2; exit }' "$launch_root/launch-manifest.txt")
    [[ "$manifest_wrapper_sha" =~ ^[0-9a-f]{64}$ ]] ||
        die "lane manifest has no valid wrapper digest: $output_root"
    [[ "$(sha256sum "$launch_root/run-wrapper.sh" | awk '{ print $1 }')" == "$manifest_wrapper_sha" ]] ||
        die "lane command wrapper changed after launch: $output_root"

    jq -e --arg output "$output_root" \
        --arg project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        --arg commit "$SOURCE_COMMIT" \
        --arg tree "$SOURCE_TREE_DIGEST" \
        --arg workingTree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        '.schemaVersion == 1 and .workload == "e2e-volume" and .status == "complete" and .exitCode == 0 and
         .outputDirectory == $output and
         .subject.project == $project and .subject.commit == $commit and
         .subject.treeDigest == $tree and .subject.workingTreeDigest == $workingTree and
         .subject.dirty == $dirty and
         (.receiptSha256 | type) == "string" and (.receiptSha256 | test("^[0-9a-f]{64}$"))' \
        "$compact" >/dev/null || die "compact run receipt failed exact provenance/status checks: $output_root"
    verify_artifact_index "$output_root"
    verify_query_diagnostics "$limit"

    jq -e --argjson limit "$limit" --arg output "$output_root" \
        '.status == "complete" and .error == null and
         .configuration.objectTier == "local" and
         .configuration.documents == 100000 and .configuration.batchSize == 4096 and
         .configuration.pointReads == 0 and .configuration.updates == 1 and
         .configuration.deletes == 1 and .configuration.queries == 1 and
         .configuration.relationships == 0 and .configuration.seed == 1337 and
         .configuration.snapshotBuildMaximumDocuments == $limit and
         .configuration.cacheBytes == 16777216 and
         .configuration.maxLocalStorageBytes == 68719476736 and
         .configuration.minimumFreeSpaceBytes == 107374182400 and
         .configuration.requireIsolatedCgroup == true and
         .configuration.outputDirectory == $output and
         (.phases.indexBuild | type) == "string" and
         (.phases.queries | type) == "string" and
         .volume.documentsWritten == 100000 and
         .volume.documentsVerifiedOriginal == 100000 and
         .volume.documentsVerifiedSnapshot == 100000 and
         .volume.collectionsVerified == 1 and
         .volume.documentsVerifiedFullRestore == 100000 and
         .volume.documentsVerifiedPitr == 100000 and
         .volume.pitrAppliedLogEntries >= 1 and
         .volume.documentsVerifiedFinal == 99999 and
         .volume.deletedDocumentsVerifiedAbsent == 1 and
         .volume.documentsVerifiedCompacted == 99999 and
         .volume.queriesVerified == 1 and
         .volume.queryLatency.operations == 1 and
         .volume.queryDiagnostics.samples == 1 and
         .resources.isolatedCgroup == true and .resources.finalCgroupValid == true and
         .resources.maxLocalStorageBytes == 68719476736 and
         .resources.maximumObservedCapOvershootBytes == 0 and
         .resources.peakLocalStorageBytes <= 68719476736 and
         .resources.minimumFreeSpaceBytes == 107374182400 and
         .resources.minimumObservedFreeBytes >= 107374182400 and
         .resources.resourceSampleCount > 0 and
         .resources.outputRoot == $output and
         .resources.swapCurrentBytes == 0 and
         .resources.oomEvents == 0 and .resources.oomKillEvents == 0 and
         .resources.initialOomEvents == .resources.finalOomEvents and
         .resources.initialOomKillEvents == .resources.finalOomKillEvents and
         .resources.samplerError == null and .resources.limitViolation == null and
         .resources.initialCgroup.memoryMax == "4294967296" and
         .resources.initialCgroup.swapMax == "0" and
         .resources.finalCgroup.memoryMax == "4294967296" and
         .resources.finalCgroup.swapMax == "0"' "$detailed" >/dev/null ||
        die "detailed E2E receipt failed exact lane verification: $output_root"

    jq -e \
        '.isolatedCgroup == true and .initial.memoryMax == "4294967296" and
         .initial.swapMax == "0" and .final.memoryMax == "4294967296" and
         .final.swapMax == "0" and .snapshot.finalCgroupValid == true and
         .snapshot.maximumObservedCapOvershootBytes == 0 and
         .snapshot.minimumObservedFreeBytes >= 107374182400 and
         .snapshot.oomEvents == 0 and .snapshot.oomKillEvents == 0 and
         .snapshot.swapCurrentBytes == 0 and .snapshot.samplerError == null and
         .snapshot.limitViolation == null' "$envelope" >/dev/null ||
        die "resource envelope failed exact cgroup/cap verification: $output_root"

    rg -Fq "Automatic index snapshot build batch: **${limit} documents**" "$summary" ||
        die "summary does not record snapshot batch limit ${limit}: $summary"
    rg -Fq '| Indexed queries |' "$summary" || die "summary is missing indexed query phase: $summary"
    printf 'Verified lane: limit=%s output=%s\n' "$limit" "$output_root"
}

write_lane_wrapper() {
    local limit=$1
    local output_root="$OUTPUT_BASE-$limit"
    local launch_root="$output_root.launch"
    local temp_root="$output_root-tmp"
    local wrapper="$launch_root/run-wrapper.sh"
    local nice_bin
    nice_bin=$(command -v nice)
    local -a benchmark_args=(
        --workload e2e-volume
        --object-tier local
        --documents "$DOCUMENTS"
        --batch-size "$BATCH_SIZE"
        --point-reads "$POINT_READS"
        --updates "$UPDATES"
        --deletes "$DELETES"
        --queries "$QUERIES"
        --relationships "$RELATIONSHIPS"
        --seed "$SEED"
        --snapshot-build-maximum-documents "$limit"
        --checkpoint-records "$CHECKPOINT_RECORDS"
        --resource-sample-ms "$RESOURCE_SAMPLE_MS"
        --cache-bytes "$CACHE_BYTES"
        --max-local-storage-bytes "$LOCAL_STORAGE_CAP_BYTES"
        --min-free-space-bytes "$MIN_FREE_SPACE_BYTES"
        --require-isolated-cgroup
        --temp-root "$temp_root"
        --output "$output_root"
    )

    {
        printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' "IFS=\$'\\n\\t'"
        printf 'export TMPDIR=%q TMP=%q TEMP=%q\n' "$temp_root" "$temp_root" "$temp_root"
        printf 'exec %q -n 19 %q %q \\\n' "$nice_bin" "$DOTNET_BIN" "$BINARY_DLL"
        for argument in "${benchmark_args[@]}"; do
            printf '  %q \\\n' "$argument"
        done
        printf '\n'
    } > "$wrapper"
    chmod 700 "$wrapper"

    cp "$FREEZE_MANIFEST" "$launch_root/freeze-manifest.txt"
    cp "$CORE_HASHES" "$launch_root/core-hashes.sha256"
    sha256sum "$BINARY_DLL" > "$launch_root/benchmark-assembly.sha256"
    local wrapper_sha256
    wrapper_sha256=$(sha256sum "$wrapper" | awk '{ print $1 }')
    {
        printf 'schema=terrabase-e2e-index-build-100k-lane-v1\n'
        printf 'createdAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf 'limit=%s\n' "$limit"
        printf 'unit=%s\n' "$UNIT_NAME"
        printf 'runtimeMax=%s\n' "$RUNTIME_MAX"
        printf 'memoryMaxBytes=%s\n' "$MEMORY_MAX_BYTES"
        printf 'memorySwapMaxBytes=%s\n' "$MEMORY_SWAP_MAX_BYTES"
        printf 'oomPolicy=stop\n'
        printf 'nice=19\n'
        printf 'binaryRoot=%s\n' "$FINAL_BINARY_ROOT"
        printf 'binary=%s\n' "$BINARY_DLL"
        printf 'binarySha256=%s\n' "$BENCH_SHA256"
        printf 'sourceRepository=%s\n' "$SOURCE_REPOSITORY"
        printf 'sourceCommit=%s\n' "$SOURCE_COMMIT"
        printf 'sourceTreeDigest=%s\n' "$SOURCE_TREE_DIGEST"
        printf 'sourceWorkingTreeDigest=%s\n' "$SOURCE_WORKTREE_DIGEST"
        printf 'sourceDirty=%s\n' "$SOURCE_DIRTY"
        printf 'outputRoot=%s\n' "$output_root"
        printf 'temporaryRoot=%s\n' "$temp_root"
        printf 'launchRoot=%s\n' "$launch_root"
        printf 'commandWrapper=%s\n' "$wrapper"
        printf 'commandWrapperSha256=%s\n' "$wrapper_sha256"
        printf 'localStorageCapBytes=%s\n' "$LOCAL_STORAGE_CAP_BYTES"
        printf 'minimumFreeSpaceBytes=%s\n' "$MIN_FREE_SPACE_BYTES"
        printf 'benchmarkArgs='
        printf '%q ' "${benchmark_args[@]}"
        printf '\n'
    } > "$launch_root/launch-manifest.txt"
}

run_lane() {
    local limit=$1
    OUTPUT_ROOT="$OUTPUT_BASE-$limit"
    LAUNCH_ROOT="$OUTPUT_ROOT.launch"
    TEMP_ROOT="$OUTPUT_ROOT-tmp"
    UNIT_NAME="terrabase-e2e-index-build-100k-$limit-$(date -u +%Y%m%dT%H%M%S%NZ)"
    WRAPPER="$LAUNCH_ROOT/run-wrapper.sh"

    [[ ! -e "$OUTPUT_ROOT" && ! -L "$OUTPUT_ROOT" ]] || die "lane output root already exists: $OUTPUT_ROOT"
    [[ ! -e "$TEMP_ROOT" && ! -L "$TEMP_ROOT" ]] || die "lane temporary root already exists: $TEMP_ROOT"
    [[ ! -e "$LAUNCH_ROOT" && ! -L "$LAUNCH_ROOT" ]] || die "lane launch root already exists: $LAUNCH_ROOT"
    mkdir -p "$OUTPUT_ROOT" "$TEMP_ROOT" "$LAUNCH_ROOT"
    [[ -z "$(find "$TEMP_ROOT" -mindepth 1 -maxdepth 1 -print -quit)" ]] ||
        die "lane temporary root is not empty: $TEMP_ROOT"
    write_lane_wrapper "$limit"

    local -a systemd_args=(
        --user
        --unit="$UNIT_NAME"
        --property=MemoryMax="$MEMORY_MAX_BYTES"
        --property=MemorySwapMax="$MEMORY_SWAP_MAX_BYTES"
        --property=OOMPolicy=stop
        --property="RuntimeMaxSec=$RUNTIME_MAX"
        --working-directory="$BENCH_ROOT"
    )
    ACTIVE_UNIT="$UNIT_NAME"
    ACTIVE_LAUNCH_ROOT="$LAUNCH_ROOT"
    set +e
    systemd-run "${systemd_args[@]}" --wait /run/current-system/sw/bin/bash "$WRAPPER" \
        > "$LAUNCH_ROOT/systemd-run.log" 2>&1
    local service_exit=$?
    set -e
    ACTIVE_UNIT=
    ACTIVE_LAUNCH_ROOT=
    printf 'serviceExit=%s\n' "$service_exit" >> "$LAUNCH_ROOT/launch-manifest.txt"
    systemctl --user show "$UNIT_NAME" \
        -p Id -p ActiveState -p Result -p ExecMainStatus -p MemoryCurrent \
        -p MemoryPeak -p MemoryMax -p MemorySwapMax -p OOMPolicy \
        > "$LAUNCH_ROOT/cgroup-runner-status.txt" 2>&1 || true
    if [[ "$service_exit" != 0 ]]; then
        die "lane ${limit} service exited with status ${service_exit}; evidence retained at $OUTPUT_ROOT"
    fi
    verify_lane "$limit"
}

write_comparison_receipt() {
    local comparison_json="${OUTPUT_BASE}-comparison.json"
    local comparison_markdown="${OUTPUT_BASE}-comparison.md"
    [[ ! -e "$comparison_json" && ! -e "$comparison_markdown" ]] ||
        die "comparison summary already exists; refusing to overwrite it"
    jq -n \
        --arg schema 'terrabase-e2e-index-build-100k-comparison-v1' \
        --arg finishedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        --arg binary "$BINARY_DLL" \
        --arg binarySha256 "$BENCH_SHA256" \
        --arg repository "$SOURCE_REPOSITORY" \
        --arg commit "$SOURCE_COMMIT" \
        --arg treeDigest "$SOURCE_TREE_DIGEST" \
        --arg workingTreeDigest "$SOURCE_WORKTREE_DIGEST" \
        --argjson sourceDirty "$SOURCE_DIRTY" \
        --slurpfile lane256 "$OUTPUT_BASE-256/e2e-volume-receipt.json" \
        --slurpfile lane1024 "$OUTPUT_BASE-1024/e2e-volume-receipt.json" \
        --slurpfile query256 "$OUTPUT_BASE-256/indexed-query-samples.jsonl" \
        --slurpfile query1024 "$OUTPUT_BASE-1024/indexed-query-samples.jsonl" \
        '{schema:$schema,status:"complete",finishedAtUtc:$finishedAtUtc,
          controls:{documents:100000,batchSize:4096,pointReads:0,updates:1,deletes:1,
            queries:1,relationships:0,seed:1337,objectTier:"local",nice:19,
            memoryMaxBytes:4294967296,memorySwapMaxBytes:0,
            maxLocalStorageBytes:68719476736,minFreeSpaceBytes:107374182400},
          provenance:{binary:$binary,binarySha256:$binarySha256,repository:$repository,
            commit:$commit,treeDigest:$treeDigest,workingTreeDigest:$workingTreeDigest,
            sourceDirty:$sourceDirty},
          lanes:{"256":{status:$lane256[0].status,
            snapshotBuildMaximumDocuments:$lane256[0].configuration.snapshotBuildMaximumDocuments,
            indexBuild:$lane256[0].phases.indexBuild,queries:$lane256[0].phases.queries,
            documentsWritten:$lane256[0].volume.documentsWritten,queriesVerified:$lane256[0].volume.queriesVerified,
            peakRssBytes:$lane256[0].resources.peakRssBytes,
            queryIndexName:$query256[0].indexName,usedSnapshotFallback:$query256[0].usedSnapshotFallback},
            "1024":{status:$lane1024[0].status,
            snapshotBuildMaximumDocuments:$lane1024[0].configuration.snapshotBuildMaximumDocuments,
            indexBuild:$lane1024[0].phases.indexBuild,queries:$lane1024[0].phases.queries,
            documentsWritten:$lane1024[0].volume.documentsWritten,queriesVerified:$lane1024[0].volume.queriesVerified,
            peakRssBytes:$lane1024[0].resources.peakRssBytes,
            queryIndexName:$query1024[0].indexName,usedSnapshotFallback:$query1024[0].usedSnapshotFallback}}}' > "$comparison_json"

    {
        printf '# E2E 100K automatic-index snapshot batch comparison\n\n'
        printf 'Status: **complete**\n\n'
        printf '| Snapshot batch documents | Index build | Indexed queries | Documents written | Queries verified | Peak RSS B |\n'
        printf '| ---: | ---: | ---: | ---: | ---: | ---: |\n'
        for limit in 256 1024; do
            jq -r --argjson limit "$limit" '[
                .configuration.snapshotBuildMaximumDocuments,
                .phases.indexBuild,
                .phases.queries,
                .volume.documentsWritten,
                .volume.queriesVerified,
                (.resources.peakRssBytes // "unavailable")
            ] | @tsv' "$OUTPUT_BASE-$limit/e2e-volume-receipt.json" |
                awk -F '\t' '{ printf "| %s | %s | %s | %s | %s | %s |\n", $1, $2, $3, $4, $5, $6 }'
        done
        printf '\nRaw lane receipts: `%s-256/e2e-volume-receipt.json`, `%s-1024/e2e-volume-receipt.json`\n' "$OUTPUT_BASE" "$OUTPUT_BASE"
        printf 'Comparison receipt: `%s`\n' "$comparison_json"
    } > "$comparison_markdown"
}

resolve_binary_root
verify_binary
printf 'Frozen binary verification passed: %s\n' "$FINAL_BINARY_ROOT"

if [[ -z "$OUTPUT_BASE" ]]; then
    OUTPUT_BASE="$BENCH_RECEIPTS/e2e-index-build-100k-comparison"
fi
if [[ "$OUTPUT_BASE" != /* ]]; then
    OUTPUT_BASE="$(pwd -P)/$OUTPUT_BASE"
fi
reject_symlink_ancestors "$OUTPUT_BASE"
OUTPUT_BASE=$(realpath -m -- "$OUTPUT_BASE")
case "$OUTPUT_BASE" in
    "$BENCH_ROOT/bench"/*) ;;
    *) die "output base must be under $BENCH_ROOT/bench: $OUTPUT_BASE" ;;
esac

if [[ "$MODE" == verify ]]; then
    verify_lane 256
    verify_lane 1024
    printf 'Comparison verification passed for output base: %s\n' "$OUTPUT_BASE"
    exit 0
fi

check_host
[[ ! -e "$OUTPUT_BASE-256" && ! -L "$OUTPUT_BASE-256" ]] || die "output root already exists: $OUTPUT_BASE-256"
[[ ! -e "$OUTPUT_BASE-1024" && ! -L "$OUTPUT_BASE-1024" ]] || die "output root already exists: $OUTPUT_BASE-1024"
[[ ! -e "$OUTPUT_BASE-256-tmp" && ! -L "$OUTPUT_BASE-256-tmp" ]] || die "temporary root already exists: $OUTPUT_BASE-256-tmp"
[[ ! -e "$OUTPUT_BASE-1024-tmp" && ! -L "$OUTPUT_BASE-1024-tmp" ]] || die "temporary root already exists: $OUTPUT_BASE-1024-tmp"
[[ ! -e "$OUTPUT_BASE-256.launch" && ! -L "$OUTPUT_BASE-256.launch" ]] || die "launch root already exists: $OUTPUT_BASE-256.launch"
[[ ! -e "$OUTPUT_BASE-1024.launch" && ! -L "$OUTPUT_BASE-1024.launch" ]] || die "launch root already exists: $OUTPUT_BASE-1024.launch"

run_lane 256
run_lane 1024
write_comparison_receipt

printf 'Comparison output base: %s\n' "$OUTPUT_BASE"
printf 'Comparison receipt: %s-comparison.json\n' "$OUTPUT_BASE"
printf 'Comparison summary: %s-comparison.md\n' "$OUTPUT_BASE"
