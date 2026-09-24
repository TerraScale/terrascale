#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

# Strict tiered RustFS ingest runner.
#
# Safe modes:
#   preflight --manifest PATH --records 10M|100M
#   launch    --manifest PATH --records 10M|100M
#   monitor   --run-dir PATH
#   verify    --run-dir PATH
#
# Preflight is the default and never creates a run or starts a unit. Launch is
# explicit. RustFS credentials are sourced only inside the benchmark service.

readonly SCRIPT_PATH="$(readlink -f "$BASH_SOURCE")"
readonly SCRIPT_DIR="$(dirname "$SCRIPT_PATH")"
readonly LAUNCH_ENV_PARSER="$SCRIPT_DIR/load-launch-env.sh"
source "$LAUNCH_ENV_PARSER"
readonly BENCH_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly BASE_DIR="$BENCH_ROOT/bench"
readonly RECEIPTS_DIR="$BASE_DIR/receipts"
readonly DEFAULT_DOTNET_BIN="/home/mariogk/.local/share/mise/dotnet-root/dotnet"
readonly DEFAULT_CREDENTIALS_FILE="/run/user/1000/terrabase-rustfs-credentials.sh"
readonly DEFAULT_RUSTFS_CONTAINER="terrabase-rustfs-20260922"
readonly DEFAULT_RUSTFS_ENDPOINT="http://127.0.0.1:19000"
readonly DEFAULT_RUSTFS_BUCKET="terrabase-tiered"
readonly PREFLIGHT_FREE_BYTES=268435456000
readonly FREE_FLOOR=107374182400
readonly LOCAL_CAP=85899345920
readonly ANON_STOP=3758096384

MODE=preflight
MANIFEST=
RECORDS_INPUT=
RUN_LABEL=
RUN_DIR=
DOTNET_BIN="$DEFAULT_DOTNET_BIN"
CREDENTIALS_FILE="$DEFAULT_CREDENTIALS_FILE"
RUSTFS_CONTAINER="$DEFAULT_RUSTFS_CONTAINER"
RUSTFS_ENDPOINT="$DEFAULT_RUSTFS_ENDPOINT"
RUSTFS_BUCKET="$DEFAULT_RUSTFS_BUCKET"
DRY_RUN=0

EXPECTED_RECORDS=
RECORDS_LABEL=
SOURCE_COMMIT=
SOURCE_TREE_DIGEST=
SOURCE_WORKTREE_DIGEST=
SOURCE_DIRTY=
SOURCE_REPOSITORY=
MANIFEST_DOTNET_VERSION=
BINARY_CLOSURE=
BENCH_DLL=
BENCH_SHA256=
MANIFEST_BENCH_SHA256=
UNIT=
MONITOR_UNIT=
RUN_ID=
OUTPUT=
PREFIX=
LAUNCH_EPOCH=

die() {
    printf 'strict-rustfs-ingest: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Usage:
  strict-rustfs-ingest.sh [preflight] --manifest PATH --records 10M|100M [options]
  strict-rustfs-ingest.sh launch --manifest PATH --records 10M|100M [options]
  strict-rustfs-ingest.sh monitor --run-dir RUN_DIR
  strict-rustfs-ingest.sh verify --run-dir RUN_DIR

Modes:
  preflight  Validate the frozen manifest, host, RustFS, space, and cgroup.
             This is the default and never starts a workload.
  launch     Run preflight, create a fresh receipt directory, start the
             benchmark unit, and start a separate persistent monitor unit.
  monitor    Monitor an existing run. Launch mode starts this persistently.
  verify     Verify an inactive run's receipt, monitor, durability, and source.

Options:
  --manifest PATH       Frozen source-matched Release freeze-manifest.txt.
  --records 10M|100M   Calibration or qualification size.
  --run-label LABEL     Override the generated run label.
  --run-dir PATH        Existing run directory for monitor or verify.
  --dotnet PATH         .NET runtime path.
  --credentials-file P  Private RustFS credential file path.
  --rustfs-container N  RustFS Podman container name.
  --endpoint URL        RustFS endpoint (default: http://127.0.0.1:19000).
  --bucket NAME         RustFS bucket (default: terrabase-tiered).
  --dry-run             Alias for preflight; never starts a unit.
  --help                Show this help.

The manifest must be produced by freeze-final-source-matched-release.sh and
contain schema=terrabase-final-source-matched-release-v1, binaryClosure,
commit, treeDigest, workingTreeDigest, sourceDirty, dotnetVersion, and a
coreHashes entry for TerraBase.Benchmarks.dll.
EOF
}

require_commands() {
    local command_name
    for command_name in \
        awk cmp curl date df dirname du flock jq nice podman pgrep readlink rg \
        sha256sum stat systemctl systemd-run; do
        command -v "$command_name" >/dev/null 2>&1 ||
            die "required command is missing: $command_name"
    done
}

manifest_value() {
    local key="$1"
    awk -v key="$key" '
        index($0, key "=") == 1 {
            print substr($0, length(key) + 2)
            found = 1
            exit
        }
        END { if (!found) exit 1 }
    ' "$MANIFEST"
}

manifest_alias() {
    local key value
    for key in "$@"; do
        value="$(manifest_value "$key" 2>/dev/null || true)"
        if [[ -n "$value" ]]; then
            printf '%s\n' "$value"
            return 0
        fi
    done
    return 1
}

required_manifest_value() {
    local key="$1"
    local value
    value="$(manifest_value "$key" 2>/dev/null || true)"
    [[ -n "$value" ]] || die "manifest is missing required key: $key"
    printf '%s\n' "$value"
}

required_manifest_alias() {
    local value
    value="$(manifest_alias "$@" 2>/dev/null || true)"
    [[ -n "$value" ]] || die "manifest is missing required key: $*"
    printf '%s\n' "$value"
}

normalize_records() {
    local normalized
    normalized="$(tr '[:lower:]' '[:upper:]' <<< "$RECORDS_INPUT")"
    case "$normalized" in
        10M|10000000)
            EXPECTED_RECORDS=10000000
            RECORDS_LABEL=10m
            ;;
        100M|100000000)
            EXPECTED_RECORDS=100000000
            RECORDS_LABEL=100m
            ;;
        *)
            die "--records must be exactly 10M or 100M"
            ;;
    esac
    if [[ -z "$RUN_LABEL" ]]; then
        RUN_LABEL="tiered-rustfs-strict-$RECORDS_LABEL-ingest"
    fi
    [[ "$RUN_LABEL" =~ ^[[:alnum:]_.-]+$ ]] ||
        die "run label must contain only letters, digits, '.', '_' and '-'"
}

load_frozen_manifest() {
    [[ -n "$MANIFEST" ]] || die "--manifest is required for $MODE"
    [[ -f "$MANIFEST" ]] || die "manifest does not exist: $MANIFEST"
    [[ ! -L "$MANIFEST" ]] || die "manifest must not be a symlink: $MANIFEST"
    MANIFEST="$(readlink -f "$MANIFEST")"

    local schema
    schema="$(required_manifest_value schema)"
    [[ "$schema" == terrabase-final-source-matched-release-v1 ]] ||
        die "unsupported frozen manifest schema: $schema"

    BINARY_CLOSURE="$(required_manifest_alias binaryClosure binaryReleaseRoot)"
    [[ -d "$BINARY_CLOSURE" ]] ||
        die "frozen binary closure does not exist: $BINARY_CLOSURE"
    BENCH_DLL="$BINARY_CLOSURE/TerraBase.Benchmarks.dll"
    [[ -f "$BENCH_DLL" ]] ||
        die "frozen benchmark assembly is missing: $BENCH_DLL"

    SOURCE_COMMIT="$(required_manifest_alias commit sourceCommit)"
    SOURCE_TREE_DIGEST="$(required_manifest_alias treeDigest sourceTreeDigest)"
    SOURCE_WORKTREE_DIGEST="$(required_manifest_alias workingTreeDigest sourceWorkingTreeDigest sourceWorktreeDigest)"
    SOURCE_DIRTY="$(required_manifest_alias sourceDirty dirty)"
    SOURCE_REPOSITORY="$(required_manifest_alias repository sourceRepository)"
    MANIFEST_DOTNET_VERSION="$(required_manifest_value dotnetVersion)"

    [[ "$SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]] ||
        die "manifest commit is not a 40-character hexadecimal object id"
    [[ "$SOURCE_TREE_DIGEST" =~ ^[0-9a-fA-F]{40}$ ]] ||
        die "manifest tree digest is not a 40-character hexadecimal digest"
    [[ "$SOURCE_WORKTREE_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]] ||
        die "manifest working-tree digest is not a 64-character hexadecimal digest"
    [[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]] ||
        die "manifest sourceDirty must be true or false"

    BENCH_SHA256="$(sha256sum "$BENCH_DLL" | awk '{ print $1 }')"
    MANIFEST_BENCH_SHA256="$(awk '
        length($1) == 64 && $1 !~ /[^0-9a-fA-F]/ &&
        $NF ~ /(^|\/)TerraBase[.]Benchmarks[.]dll$/ { print $1; exit }
    ' "$MANIFEST")"
    [[ -n "$MANIFEST_BENCH_SHA256" ]] ||
        die "manifest has no coreHashes entry for TerraBase.Benchmarks.dll"
    [[ "$BENCH_SHA256" == "$MANIFEST_BENCH_SHA256" ]] ||
        die "frozen benchmark assembly hash does not match the manifest"
    [[ -f "$BINARY_CLOSURE/core-hashes.sha256" && -f "$BINARY_CLOSURE/closure-sha256.txt" ]] ||
        die "frozen binary closure is missing its hash lists"
    cmp -s "$BINARY_CLOSURE/core-hashes.sha256" \
        <(awk '/^coreHashes:$/ { hashes = 1; next } hashes { print }' "$MANIFEST") ||
        die "frozen core hash list does not match the manifest"
    (cd "$BINARY_CLOSURE" &&
        sha256sum --quiet -c core-hashes.sha256 &&
        sha256sum --quiet -c closure-sha256.txt) ||
        die "frozen binary closure hash verification failed"
}

load_runtime() {
    [[ -x "$DOTNET_BIN" ]] || die ".NET runtime is not executable: $DOTNET_BIN"
    local runtime_version
    runtime_version="$("$DOTNET_BIN" --version)" ||
        die "unable to query .NET runtime version"
    [[ "$runtime_version" == "$MANIFEST_DOTNET_VERSION" ]] ||
        die ".NET runtime mismatch: manifest=$MANIFEST_DOTNET_VERSION actual=$runtime_version"
}

check_space() {
    local available
    available="$(df -B1 --output=avail "$RECEIPTS_DIR" | awk 'NR == 2 { print $1 }')"
    [[ "$available" =~ ^[0-9]+$ ]] || die "unable to read free space for $RECEIPTS_DIR"
    (( available >= PREFLIGHT_FREE_BYTES )) ||
        die "free space $available is below required preflight floor $PREFLIGHT_FREE_BYTES"
}

check_host() {
    [[ -d "$RECEIPTS_DIR" ]] || die "receipts directory is missing: $RECEIPTS_DIR"
    [[ -r "$CREDENTIALS_FILE" ]] ||
        die "RustFS credentials file is not readable: $CREDENTIALS_FILE"
    [[ -r /sys/fs/cgroup/cgroup.controllers ]] ||
        die "cgroup v2 is not available"
    systemctl --user show-environment >/dev/null 2>&1 ||
        die "the user systemd manager is unavailable"

    local rustfs_state endpoint_status
    rustfs_state="$(podman inspect --format '{{.State.Status}}' "$RUSTFS_CONTAINER" 2>/dev/null || true)"
    [[ "$rustfs_state" == running ]] ||
        die "RustFS container is not running: $RUSTFS_CONTAINER"
    endpoint_status="$(curl --connect-timeout 2 --max-time 5 -sS -o /dev/null \
        -w '%{http_code}' "$RUSTFS_ENDPOINT" 2>/dev/null || true)"
    case "$endpoint_status" in
        200|400|403|405)
            ;;
        *)
            die "RustFS endpoint did not return an expected service status: $RUSTFS_ENDPOINT status=$endpoint_status"
            ;;
    esac
    report_quiet_window_contention
}

check_unit_free() {
    local unit_name load_state active_state
    for unit_name in "$UNIT" "$MONITOR_UNIT"; do
        load_state="$(systemctl --user show "$unit_name" -p LoadState --value 2>/dev/null || true)"
        active_state="$(systemctl --user show "$unit_name" -p ActiveState --value 2>/dev/null || true)"
        [[ "$load_state" != loaded ]] ||
            die "systemd unit already exists: $unit_name"
        case "$active_state" in
            active|activating|deactivating)
                die "systemd unit is already active: $unit_name"
                ;;
        esac
    done
}

quiet_window_matches() {
    local service_matches process_matches pid command_line
    service_matches="$(systemctl --user list-units --type=service --state=running --no-legend 2>/dev/null |
        rg -i 'terrabase.*(strict|e2e|larger-memory|state-map|tiered)|state-map.*terrabase' || true)"
    if [[ -n "${MONITOR_UNIT:-}" ]]; then
        service_matches="$(printf '%s\n' "$service_matches" |
            rg -v -F "$MONITOR_UNIT" || true)"
    fi
    [[ -n "$service_matches" ]] && printf 'service:%s\n' "$service_matches"

    while read -r pid command_line; do
        [[ "$pid" =~ ^[0-9]+$ ]] || continue
        [[ "$pid" == "$$" || "$pid" == "$PPID" ]] && continue
        [[ -n "${MONITOR_UNIT:-}" && "$command_line" == *"$MONITOR_UNIT"* ]] && continue
        printf 'process:%s %s\n' "$pid" "$command_line"
    done < <(pgrep -a -f 'TerraBase\.Benchmarks(\.dll|/)' || true)
}

report_quiet_window_contention() {
    local matches
    matches="$(quiet_window_matches)"
    if [[ -n "$matches" ]]; then
        printf 'contention=active-terra-base-workload\n'
        printf '%s\n' "$matches"
    else
        printf 'contention=none\n'
    fi
}

check_quiet_window() {
    local matches
    [[ "${ALLOW_ACTIVE_RUNS:-0}" == 1 ]] && return 0
    matches="$(quiet_window_matches)"
    [[ -z "$matches" ]] || die "an active TerraBase benchmark service or process is running; wait for a quiet window"
}

load_launch_env() {
    [[ -n "$RUN_DIR" ]] || die "--run-dir is required for $MODE"
    [[ -d "$RUN_DIR" ]] || die "run directory does not exist: $RUN_DIR"
    [[ ! -L "$RUN_DIR" ]] || die "run directory must not be a symlink: $RUN_DIR"
    [[ -f "$RUN_DIR/launch.env" ]] || die "launch.env is missing: $RUN_DIR/launch.env"
    load_launch_env_file "$RUN_DIR/launch.env" \
        RUN_ID OUTPUT PREFIX UNIT MONITOR_UNIT MANIFEST FROZEN_MANIFEST_PATH \
        FROZEN_MANIFEST_SHA256 BINARY_CLOSURE BENCH_DLL BENCH_SHA256 \
        SOURCE_REPOSITORY SOURCE_COMMIT SOURCE_TREE_DIGEST SOURCE_WORKTREE_DIGEST \
        SOURCE_DIRTY EXPECTED_RECORDS RUN_LABEL DOTNET_BIN S3_ENDPOINT S3_BUCKET \
        LAUNCH_EPOCH || die "launch.env failed strict metadata validation"
    [[ "$OUTPUT" == "$RUN_DIR" ]] || die "launch.env output does not match --run-dir"
    [[ "$EXPECTED_RECORDS" == 10000000 || "$EXPECTED_RECORDS" == 100000000 ]] ||
        die "launch.env has an invalid EXPECTED_RECORDS value"
    [[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]] ||
        die "launch.env has an invalid SOURCE_DIRTY value"
    [[ -n "$UNIT" && -n "$MONITOR_UNIT" ]] ||
        die "launch.env is missing systemd unit identity"
}

preflight() {
    require_commands
    normalize_records
    load_frozen_manifest
    load_runtime
    check_space
    check_host

    RUN_ID="$RUN_LABEL-preflight"
    OUTPUT="$RECEIPTS_DIR/$RUN_ID"
    PREFIX="$RUN_ID"
    UNIT="terrabase-$RUN_LABEL-preflight.service"
    MONITOR_UNIT="terrabase-$RUN_LABEL-preflight-monitor.service"
    [[ ! -e "$OUTPUT" ]] || die "preflight output path already exists: $OUTPUT"
    check_unit_free

    printf 'preflight=ok\n'
    printf 'records=%s\n' "$EXPECTED_RECORDS"
    printf 'manifest=%s\n' "$MANIFEST"
    printf 'benchmark_dll=%s\n' "$BENCH_DLL"
    printf 'benchmark_sha256=%s\n' "$BENCH_SHA256"
    printf 'source_commit=%s\n' "$SOURCE_COMMIT"
    printf 'source_tree_digest=%s\n' "$SOURCE_TREE_DIGEST"
    printf 'source_worktree_digest=%s\n' "$SOURCE_WORKTREE_DIGEST"
    printf 'source_dirty=%s\n' "$SOURCE_DIRTY"
    printf 'rustfs_endpoint=%s\n' "$RUSTFS_ENDPOINT"
    printf 'rustfs_bucket=%s\n' "$RUSTFS_BUCKET"
}

prepare_launch_identity() {
    local timestamp
    timestamp="$(date -u +%Y%m%dT%H%M%S%NZ)"
    RUN_ID="$RUN_LABEL-$timestamp"
    OUTPUT="$RECEIPTS_DIR/$RUN_ID"
    PREFIX="$RUN_ID"
    UNIT="terrabase-$RUN_ID.service"
    MONITOR_UNIT="terrabase-$RUN_ID-monitor.service"
    [[ ! -e "$OUTPUT" ]] || die "refusing to reuse output path: $OUTPUT"
    check_unit_free
}

write_launch_env() {
    LAUNCH_EPOCH="$(date +%s)"
    mkdir -m 700 "$OUTPUT"
    {
        printf 'RUN_ID=%q\n' "$RUN_ID"
        printf 'OUTPUT=%q\n' "$OUTPUT"
        printf 'PREFIX=%q\n' "$PREFIX"
        printf 'UNIT=%q\n' "$UNIT"
        printf 'MONITOR_UNIT=%q\n' "$MONITOR_UNIT"
        printf 'MANIFEST=%q\n' "$MANIFEST"
        printf 'FROZEN_MANIFEST_PATH=%q\n' "$OUTPUT/freeze-manifest.txt"
        printf 'FROZEN_MANIFEST_SHA256=%q\n' "$(sha256sum "$MANIFEST" | awk '{print $1}')"
        printf 'BINARY_CLOSURE=%q\n' "$BINARY_CLOSURE"
        printf 'BENCH_DLL=%q\n' "$BENCH_DLL"
        printf 'BENCH_SHA256=%q\n' "$BENCH_SHA256"
        printf 'SOURCE_REPOSITORY=%q\n' "$SOURCE_REPOSITORY"
        printf 'SOURCE_COMMIT=%q\n' "$SOURCE_COMMIT"
        printf 'SOURCE_TREE_DIGEST=%q\n' "$SOURCE_TREE_DIGEST"
        printf 'SOURCE_WORKTREE_DIGEST=%q\n' "$SOURCE_WORKTREE_DIGEST"
        printf 'SOURCE_DIRTY=%q\n' "$SOURCE_DIRTY"
        printf 'EXPECTED_RECORDS=%q\n' "$EXPECTED_RECORDS"
        printf 'RUN_LABEL=%q\n' "$RUN_LABEL"
        printf 'DOTNET_BIN=%q\n' "$DOTNET_BIN"
        printf 'S3_ENDPOINT=%q\n' "$RUSTFS_ENDPOINT"
        printf 'S3_BUCKET=%q\n' "$RUSTFS_BUCKET"
        printf 'LAUNCH_EPOCH=%q\n' "$LAUNCH_EPOCH"
    } > "$OUTPUT/launch.env"
    chmod 600 "$OUTPUT/launch.env"
}

start_benchmark_unit() {
    local bash_bin
    bash_bin="$(command -v bash)"
    systemd-run --user --no-block --unit="$UNIT" --collect --quiet \
        --property=MemoryMax=4G \
        --property=MemorySwapMax=0 \
        --property=OOMPolicy=stop \
        --property=RuntimeMaxSec=12h \
        --property=WorkingDirectory="$BASE_DIR" \
        --setenv=PLAN_OUTPUT="$OUTPUT" \
        --setenv=RUSTFS_CREDENTIALS_FILE="$CREDENTIALS_FILE" \
        --setenv=LAUNCH_ENV_PARSER="$LAUNCH_ENV_PARSER" \
        "$bash_bin" -lc '
            set -Eeuo pipefail
            source "$RUSTFS_CREDENTIALS_FILE"
            source "$LAUNCH_ENV_PARSER"
            load_launch_env_file "$PLAN_OUTPUT/launch.env" \
                RUN_ID OUTPUT PREFIX UNIT MONITOR_UNIT MANIFEST FROZEN_MANIFEST_PATH \
                FROZEN_MANIFEST_SHA256 BINARY_CLOSURE BENCH_DLL BENCH_SHA256 \
                SOURCE_REPOSITORY SOURCE_COMMIT SOURCE_TREE_DIGEST SOURCE_WORKTREE_DIGEST \
                SOURCE_DIRTY EXPECTED_RECORDS RUN_LABEL DOTNET_BIN S3_ENDPOINT S3_BUCKET \
                LAUNCH_EPOCH
            export TERRABASE_S3_ENDPOINT="$S3_ENDPOINT"
            export TERRABASE_S3_BUCKET="$S3_BUCKET"
            export TERRABASE_FROZEN_SOURCE_PROJECT="$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj"
            export TERRABASE_FROZEN_SOURCE_REPOSITORY="$SOURCE_REPOSITORY"
            export TERRABASE_FROZEN_SOURCE_COMMIT="$SOURCE_COMMIT"
            export TERRABASE_FROZEN_SOURCE_TREE_DIGEST="$SOURCE_TREE_DIGEST"
            export TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST="$SOURCE_WORKTREE_DIGEST"
            export TERRABASE_FROZEN_SOURCE_DIRTY="$SOURCE_DIRTY"
            export TERRABASE_FROZEN_MANIFEST_PATH="$FROZEN_MANIFEST_PATH"
            export TERRABASE_FROZEN_MANIFEST_SHA256="$FROZEN_MANIFEST_SHA256"
            export TERRABASE_FROZEN_BINARY_CLOSURE="$BINARY_CLOSURE"
            export TERRABASE_FROZEN_BENCHMARK_ASSEMBLY_SHA256="$BENCH_SHA256"
            exec > "$PLAN_OUTPUT/console.log" 2>&1
            if /run/current-system/sw/bin/nice -n 19 "$DOTNET_BIN" "$BENCH_DLL" \
                --workload tiered-segment \
                --records "$EXPECTED_RECORDS" \
                --batch 65536 \
                --payload-bytes 128 \
                --payload-mode per-key-incompressible \
                --updates 0 \
                --deletes 0 \
                --point-reads 0 \
                --checkpoint-records 500000 \
                --cache-bytes 16777216 \
                --ingest-only \
                --max-local-storage-bytes 85899345920 \
                --min-free-space-bytes 107374182400 \
                --resource-sample-ms 500 \
                --require-isolated-cgroup \
                --s3-endpoint "$S3_ENDPOINT" \
                --s3-bucket "$S3_BUCKET" \
                --prefix "$PREFIX" \
                --output "$OUTPUT"; then
                printf "exitCode=0\n" > "$PLAN_OUTPUT/benchmark-exit-status.tmp"
                mv -- "$PLAN_OUTPUT/benchmark-exit-status.tmp" "$PLAN_OUTPUT/benchmark-exit-status"
            else
                exit "$?"
            fi
        '
}

start_monitor_unit() {
    systemd-run --user --no-block --unit="$MONITOR_UNIT" --collect --quiet \
        --property=MemoryMax=256M \
        --property=MemorySwapMax=0 \
        --property=OOMPolicy=stop \
        --property=RuntimeMaxSec=12h \
        --property=WorkingDirectory="$BASE_DIR" \
        "$SCRIPT_PATH" monitor --run-dir "$OUTPUT" >/dev/null
}

launch() {
    preflight >/dev/null
    normalize_records
    load_frozen_manifest
    load_runtime
    check_space
    check_host
    prepare_launch_identity
    check_quiet_window
    write_launch_env
    cp -- "$MANIFEST" "$OUTPUT/freeze-manifest.txt"

    if ! start_benchmark_unit; then
        die "benchmark systemd unit could not be queued"
    fi
    if ! start_monitor_unit; then
        systemctl --user stop "$UNIT" >/dev/null 2>&1 || true
        die "monitor systemd unit could not be queued; benchmark was stopped"
    fi

    printf 'launched=true\n'
    printf 'run_id=%s\n' "$RUN_ID"
    printf 'run_dir=%s\n' "$OUTPUT"
    printf 'unit=%s\n' "$UNIT"
    printf 'monitor_unit=%s\n' "$MONITOR_UNIT"
}

read_event() {
    awk -v key="$1" '$1 == key { print $2; found = 1 } END { if (!found) print 0 }' "$2"
}

read_stat() {
    awk -v key="$1" '$1 == key { print $2; found = 1 } END { if (!found) print 0 }' "$2"
}

read_psi_avg10() {
    awk -v key="$1" '$1 == key { for (field = 2; field <= NF; field++) if ($field ~ /^avg10=/) { value = $field; sub(/^avg10=/, "", value); print value; found = 1 } } END { if (!found) print 0 }' "$2"
}

verify_ingest_performance() {
    local receipt="$OUTPUT/tiered-rustfs-receipt.json"
    local progress="$OUTPUT/progress.jsonl"
    local performance="$OUTPUT/performance-verification.json"
    local temporary="$performance.tmp"
    local progress_analysis
    local progress_error
    local reason
    # Both the calibration and qualification corpora are acceptance runs. Keep
    # the same throughput gates on 10M and 100M so qualification cannot pass
    # with a slower seed than the calibration requirement.
    local enforced=1

    # The progress stream and receipt are one evidence chain. A run with a
    # different progress file or seed metric must not receive a throughput
    # result from this verifier.
    jq -e \
        --arg progress_path "$progress" \
        --argjson expected_records "$EXPECTED_RECORDS" \
        '
        .ProgressPath == $progress_path
        and .TargetRecords == $expected_records
        and (.PhaseMetrics | type) == "array"
        and ([.PhaseMetrics[] | select(.Name == "seed")] | length) == 1
        and ([.PhaseMetrics[] | select(.Name == "seed")][0].CompletedRecords == $expected_records)
        and ([.PhaseMetrics[] | select(.Name == "seed")][0].DurationSeconds > 0)
        ' "$receipt" >/dev/null ||
        die "receipt/progress seed identity does not match the requested corpus"

    progress_analysis="$(mktemp "$OUTPUT/.performance-progress.XXXXXX")"
    progress_error="$(mktemp "$OUTPUT/.performance-progress-error.XXXXXX")"
    if ! jq -s \
        --argjson expected_records "$EXPECTED_RECORDS" \
        '
        map(select(.phase == "seed")) as $seed
        | if ($seed | length) < 4 then
              error("seed progress has fewer than four checkpoints")
          else . end
        | ($seed | map(select(
              (.completed | type) == "number"
              and (.total | type) == "number"
              and (.elapsedSeconds | type) == "number"
              and (.completed | floor) == .completed
              and (.total | floor) == .total
          ))) as $typed
        | if ($typed | length) != ($seed | length) then
              error("seed progress has invalid numeric fields")
          else . end
        | if any($typed[];
              .total != $expected_records
              or .completed <= 0
              or .completed > $expected_records
              or .elapsedSeconds <= 0
          ) then
              error("seed checkpoint is outside the requested corpus")
          else . end
        | [range(1; ($typed | length))
            | select(
                $typed[.].completed <= $typed[.-1].completed
                or $typed[.].elapsedSeconds <= $typed[.-1].elapsedSeconds
            )] as $invalid_order
        | if ($invalid_order | length) != 0 then
              error("seed progress is not strictly increasing")
          else . end
        | ($typed[-1]) as $final
        | if $final.completed != $expected_records or $final.total != $expected_records then
              error("seed progress does not end at a full requested corpus")
          else . end
        | ($typed | to_entries
            | map(select(.key > 0)
                | {
                    fromCompleted: $typed[.key - 1].completed,
                    toCompleted: .value.completed,
                    records: (.value.completed - $typed[.key - 1].completed),
                    elapsedSeconds: (.value.elapsedSeconds - $typed[.key - 1].elapsedSeconds)
                })
            | map(. + {rate: (.records / .elapsedSeconds)})) as $intervals
        | ($intervals | map(.records) | sort | group_by(.) | max_by(length) | .[0]) as $checkpoint_records
        | ($intervals | map(select(.records == $checkpoint_records))) as $full_intervals
        | if ($full_intervals | length) < 3 then
              error("seed progress has fewer than three full checkpoint intervals")
          else . end
        | {
            seedRows: ($typed | length),
            seedFinalCompleted: $final.completed,
            seedFinalTotal: $final.total,
            seedFinalElapsedSeconds: $final.elapsedSeconds,
            checkpointRecords: $checkpoint_records,
            fullCheckpointCount: ($full_intervals | length),
            lastFullIntervals: ($full_intervals | .[-3:])
        }
        ' "$progress" > "$progress_analysis" 2> "$progress_error"; then
        reason="$(tr '\n' ' ' < "$progress_error")"
        rm -f -- "$progress_analysis" "$progress_error"
        die "seed performance progress verification failed: ${reason:-unknown parser error}"
    fi
    rm -f -- "$progress_error"

    if ! jq \
        --arg schema tiered-rustfs-ingest-performance-v1 \
        --arg progress_path "$progress" \
        --argjson expected_records "$EXPECTED_RECORDS" \
        --argjson enforced "$enforced" \
        --slurpfile progress_summary "$progress_analysis" \
        '
        . as $receipt
        | ($progress_summary[0]) as $progress
        | ([.PhaseMetrics[] | select(.Name == "seed")][0]) as $seed
        | ($seed.CompletedRecords / $seed.DurationSeconds) as $overall_rate
        | ($progress.lastFullIntervals | map(.rate)) as $sustained_rates
        | ($sustained_rates | min) as $sustained_minimum
        | {
            schema: $schema,
            expectedRecords: $expected_records,
            enforced: ($enforced == 1),
            status: (if ($enforced == 1)
                     then (if ($overall_rate >= 4000 and $sustained_minimum >= 3000) then "passed" else "failed" end)
                     else "reported"
                     end),
            progressPath: $progress_path,
            receiptSeedRecords: $seed.CompletedRecords,
            receiptSeedDurationSeconds: $seed.DurationSeconds,
            gates: {
                overall: {
                    minimumRecordsPerSecond: 4000,
                    measuredRecordsPerSecond: $overall_rate,
                    passed: ($overall_rate >= 4000)
                },
                sustained: {
                    minimumRecordsPerSecond: 3000,
                    checkpointCount: 3,
                    measuredRecordsPerSecond: $sustained_rates,
                    minimumMeasuredRecordsPerSecond: $sustained_minimum,
                    passed: ($sustained_minimum >= 3000)
                }
            },
            progress: $progress
        }
    ' "$receipt" > "$temporary"; then
        rm -f -- "$progress_analysis" "$temporary"
        die "could not write the seed performance verification report"
    fi
    rm -f -- "$progress_analysis"
    mv -- "$temporary" "$performance"

    if (( enforced )) && ! jq -e '.status == "passed"' "$performance" >/dev/null; then
        jq '.' "$performance" >&2
        die "seed throughput gates failed"
    fi
}

verify_resource_budget() {
    local receipt="$OUTPUT/tiered-rustfs-receipt.json"
    local monitor_log="$OUTPUT/monitor.tsv"
    local resource_report="$OUTPUT/resource-verification.json"
    local temporary="$resource_report.tmp"
    local monitor_max_anon
    local monitor_max_peak

    monitor_max_anon="$(awk -F '\t' '
        NR > 1 && $9 ~ /^[0-9]+$/ {
            if (!seen || $9 > maximum) maximum = $9
            seen = 1
        }
        END {
            if (!seen) exit 1
            print maximum
        }
    ' "$monitor_log")" || die "monitor has no numeric anonymous-memory samples"
    monitor_max_peak="$(awk -F '\t' '
        NR > 1 && $8 ~ /^[0-9]+$/ {
            if (!seen || $8 > maximum) maximum = $8
            seen = 1
        }
        END {
            if (!seen) exit 1
            print maximum
        }
    ' "$monitor_log")" || die "monitor has no numeric memory-peak samples"

    jq -e \
        --argjson monitor_max_anon "$monitor_max_anon" \
        --argjson monitor_max_peak "$monitor_max_peak" \
        '
        .FinalCgroup as $cgroup
        | ($cgroup.MemoryEvents // ""
            | split("\n")
            | map(select(length > 0) | split(" ") | {(.[0]): (.[1] | tonumber)})
            | add // {}) as $events
        | ($cgroup.MemoryMax | tonumber) as $memory_max
        | ($cgroup.MemoryPeak | tonumber) as $memory_peak
        | ($cgroup.SwapMax | tonumber) as $swap_max
        | ($cgroup.SwapCurrent | tonumber) as $swap_current
        | if $events.oom != 0
             or $events.oom_kill != 0
             or $events.oom_group_kill != 0
             or $swap_current != 0
             or $swap_max != 0
             or $monitor_max_anon >= 3758096384 then
              error("resource budget failed")
          else . end
        | {
            schema: "terrabase-strict-ingest-resource-v1",
            status: (if (($events.max // 0) > 0 or ($events.high // 0) > 0)
                     then "passed-with-memory-pressure-events"
                     else "passed"
                     end),
            memory: {
                maxBytes: $memory_max,
                peakBytes: $memory_peak,
                peakOvershootBytes: ([0, ($memory_peak - $memory_max)] | max),
                peakExceedsMax: ($memory_peak > $memory_max),
                observedMonitorPeakBytes: $monitor_max_peak,
                peakPolicy: "reported-only; cgroup memory.peak may overshoot memory.max"
            },
            memoryEvents: $events,
            pressureEventsObserved: (($events.max // 0) > 0 or ($events.high // 0) > 0),
            swap: {
                maxBytes: $swap_max,
                currentBytes: $swap_current,
                passed: ($swap_max == 0 and $swap_current == 0)
            },
            gates: {
                oom: {
                    passed: ($events.oom == 0 and $events.oom_kill == 0 and $events.oom_group_kill == 0)
                },
                anonymousMemory: {
                    maxObservedBytes: $monitor_max_anon,
                    limitBytes: 3758096384,
                    passed: ($monitor_max_anon < 3758096384)
                }
            }
        }
        ' "$receipt" > "$temporary" || {
        rm -f -- "$temporary"
        die "resource budget verification failed (OOM, swap, or anonymous-memory bound)"
    }
    mv -- "$temporary" "$resource_report"
}

stop_benchmark() {
    local reason="$1"
    printf '%s\tSTOP\t%s\n' "$(date -u +%FT%TZ)" "$reason" | tee -a "$MONITOR_LOG" >&2
    systemctl --user stop "$UNIT" >/dev/null 2>&1 || true
}

monitor() {
    require_commands
    load_launch_env
    local receipt="$OUTPUT/tiered-rustfs-receipt.json"
    local progress="$OUTPUT/progress.jsonl"
    local monitor_log="$OUTPUT/monitor.tsv"
    local lock_file="$OUTPUT/monitor.lock"
    local now state control_group cgroup
    local free_bytes local_bytes memory_current memory_peak anon file_bytes
    local psi_some_avg10 psi_full_avg10 oom oom_kill oom_group_kill
    local phase completed progress_mtime
    local last_progress_mtime=0
    local last_progress_epoch="$(date +%s)"
    local anon_high_since=0
    local monitor_started="$(date +%s)"
    local seen_active=0
    MONITOR_LOG="$monitor_log"

    exec 9>"$lock_file"
    flock -n 9 || die "another monitor already owns $OUTPUT"
    if [[ ! -s "$monitor_log" ]]; then
        printf 'utc\tstate\tphase\tcompleted\tfree\tlocal\tmemory_current\tmemory_peak\tanon\tfile\tpsi_some_avg10\tpsi_full_avg10\toom\toom_kill\toom_group_kill\n' > "$monitor_log"
    fi

    while :; do
        state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || printf missing)"
        case "$state" in
            active|activating|deactivating)
                seen_active=1
                ;;
            inactive|failed|missing)
                now="$(date +%s)"
                if (( seen_active == 0 && now - monitor_started < 120 )); then
                    sleep 5
                    continue
                fi
                break
                ;;
        esac

        now="$(date +%s)"
        if (( now - LAUNCH_EPOCH >= 43200 )); then
            stop_benchmark '12-hour wall limit'
            break
        fi

        free_bytes="$(df -B1 --output=avail "$OUTPUT" | awk 'NR == 2 { print $1 }')"
        local_bytes=0
        if [[ -d "$OUTPUT/engine" ]]; then
            local_bytes="$(du -sB1 "$OUTPUT/engine" | awk '{ print $1 }')"
        fi
        if (( free_bytes < FREE_FLOOR )); then
            stop_benchmark "free space $free_bytes below $FREE_FLOOR"
            break
        fi
        if (( local_bytes > LOCAL_CAP )); then
            stop_benchmark "local engine $local_bytes above $LOCAL_CAP"
            break
        fi

        control_group="$(systemctl --user show "$UNIT" -p ControlGroup --value 2>/dev/null || true)"
        if [[ -z "$control_group" || "$control_group" != /* ]]; then
            sleep 5
            continue
        fi
        cgroup="/sys/fs/cgroup$control_group"
        if [[ ! -d "$cgroup" ||
              ! -r "$cgroup/memory.current" ||
              ! -r "$cgroup/memory.events" ||
              ! -r "$cgroup/memory.stat" ||
              ! -r "$cgroup/memory.peak" ||
              ! -r "$cgroup/memory.pressure" ]]; then
            sleep 5
            continue
        fi
        memory_current="$(cat "$cgroup/memory.current")"
        memory_peak="$(cat "$cgroup/memory.peak")"
        anon="$(read_stat anon "$cgroup/memory.stat")"
        file_bytes="$(read_stat file "$cgroup/memory.stat")"
        psi_some_avg10="$(read_psi_avg10 some "$cgroup/memory.pressure")"
        psi_full_avg10="$(read_psi_avg10 full "$cgroup/memory.pressure")"
        oom="$(read_event oom "$cgroup/memory.events")"
        oom_kill="$(read_event oom_kill "$cgroup/memory.events")"
        oom_group_kill="$(read_event oom_group_kill "$cgroup/memory.events")"
        if (( oom > 0 || oom_kill > 0 || oom_group_kill > 0 )); then
            stop_benchmark "OOM counters oom=$oom oom_kill=$oom_kill oom_group_kill=$oom_group_kill"
            break
        fi

        if (( anon >= ANON_STOP )); then
            (( anon_high_since == 0 )) && anon_high_since="$now"
            if (( now - anon_high_since >= 600 )); then
                stop_benchmark 'anonymous memory remained at least 3.5 GiB for 10 minutes'
                break
            fi
        else
            anon_high_since=0
        fi

        phase=starting
        completed=0
        if [[ -s "$receipt" ]]; then
            phase="$(jq -r '.Phase // "unknown"' "$receipt" 2>/dev/null || printf unknown)"
            completed="$(jq -r '.CompletedRecords // 0' "$receipt" 2>/dev/null || printf 0)"
        fi
        local stall_limit=3600
        if [[ "$phase" == ingest-only-remote-validation ]]; then
            stall_limit=7200
        fi
        progress_mtime=0
        [[ -f "$progress" ]] && progress_mtime="$(stat -c %Y "$progress")"
        if (( progress_mtime > last_progress_mtime )); then
            last_progress_mtime="$progress_mtime"
            last_progress_epoch="$now"
        elif (( now - last_progress_epoch >= stall_limit )); then
            stop_benchmark "no progress file update for $stall_limit seconds in phase $phase"
            break
        fi

        if awk -v psi="$psi_full_avg10" -v anon="$anon" \
            'BEGIN { exit !(psi >= 10.0 && anon >= 2147483648) }' \
            && (( now - last_progress_epoch >= 600 )); then
            stop_benchmark "full memory PSI avg10=$psi_full_avg10% with anonymous memory $anon and stalled progress"
            break
        fi

        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
            "$(date -u +%FT%TZ)" "$state" "$phase" "$completed" "$free_bytes" \
            "$local_bytes" "$memory_current" "$memory_peak" "$anon" "$file_bytes" \
            "$psi_some_avg10" "$psi_full_avg10" "$oom" "$oom_kill" "$oom_group_kill" \
            >> "$monitor_log"
        sleep 60
    done
}

verify() {
    require_commands
    load_launch_env

    local run_manifest="$MANIFEST"
    local run_bench_dll="$BENCH_DLL"
    local run_bench_sha="$BENCH_SHA256"
    local run_source_commit="$SOURCE_COMMIT"
    local run_source_tree="$SOURCE_TREE_DIGEST"
    local run_source_worktree="$SOURCE_WORKTREE_DIGEST"
    local run_source_dirty="$SOURCE_DIRTY"
    MANIFEST="$run_manifest"
    load_frozen_manifest
    [[ "$BENCH_DLL" == "$run_bench_dll" ]] || die "benchmark DLL no longer matches frozen manifest"
    [[ "$BENCH_SHA256" == "$run_bench_sha" ]] || die "benchmark SHA no longer matches launch metadata"
    [[ "$SOURCE_COMMIT" == "$run_source_commit" ]] || die "source commit no longer matches launch metadata"
    [[ "$SOURCE_TREE_DIGEST" == "$run_source_tree" ]] || die "source tree digest no longer matches launch metadata"
    [[ "$SOURCE_WORKTREE_DIGEST" == "$run_source_worktree" ]] || die "source worktree digest no longer matches launch metadata"
    [[ "$SOURCE_DIRTY" == "$run_source_dirty" ]] || die "source dirty flag no longer matches launch metadata"
    [[ -f "$FROZEN_MANIFEST_PATH" && ! -L "$FROZEN_MANIFEST_PATH" ]] || die "frozen launch manifest copy is missing"
    [[ "$(sha256sum "$FROZEN_MANIFEST_PATH" | awk '{print $1}')" == "$FROZEN_MANIFEST_SHA256" ]] || die "frozen launch manifest copy changed"

    local receipt="$OUTPUT/tiered-rustfs-receipt.json"
    local monitor_log="$OUTPUT/monitor.tsv"
    local state
    state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || printf missing)"
    case "$state" in
        active|activating|deactivating)
            die "benchmark unit is still running: $UNIT"
            ;;
    esac

    [[ -s "$receipt" ]] || die "receipt is missing or empty: $receipt"
    [[ -s "$OUTPUT/tiered-rustfs-summary.md" ]] || die "summary is missing or empty"
    [[ -s "$OUTPUT/resource-envelope.json" ]] || die "resource envelope is missing or empty"
    [[ -s "$OUTPUT/resource-samples.jsonl" ]] || die "resource samples are missing or empty"
    [[ -s "$OUTPUT/progress.jsonl" ]] || die "progress telemetry is missing or empty"
    [[ -s "$OUTPUT/engine/wal" ]] || die "local WAL is missing or empty"
    [[ -s "$monitor_log" ]] || die "monitor telemetry is missing or empty"
    [[ -f "$OUTPUT/benchmark-exit-status" ]] ||
        die "benchmark process did not record a successful exit"
    [[ "$(cat "$OUTPUT/benchmark-exit-status")" == exitCode=0 ]] ||
        die "benchmark process did not exit successfully"

    awk -F '\t' 'NR > 1 && $2 == "STOP" { found = 1 } END { exit found }' "$monitor_log" ||
        die "monitor recorded a STOP condition"
    awk -F '\t' '
        NR > 1 && $9 ~ /^[0-9]+$/ {
            seen = 1
            if ($9 >= 3758096384) found = 1
        }
        END { exit (!seen || found) }
    ' "$monitor_log" || die "monitor anonymous-memory samples exceed 3.5 GiB or are absent"

    [[ -f "$BENCH_DLL" ]] || die "benchmark assembly from launch.env is missing"
    [[ "$(sha256sum "$BENCH_DLL" | awk '{ print $1 }')" == "$BENCH_SHA256" ]] ||
        die "benchmark assembly changed after launch"

    jq -e \
        --arg expected_sha "$BENCH_SHA256" \
        --arg progress_path "$OUTPUT/progress.jsonl" \
        --argjson expected_records "$EXPECTED_RECORDS" \
        --arg source_repository "$SOURCE_REPOSITORY" \
        --arg source_commit "$SOURCE_COMMIT" \
        --arg source_tree_digest "$SOURCE_TREE_DIGEST" \
        --arg source_worktree_digest "$SOURCE_WORKTREE_DIGEST" \
        --argjson source_dirty "$SOURCE_DIRTY" \
        --arg frozen_manifest "$FROZEN_MANIFEST_PATH" \
        --arg frozen_manifest_sha "$FROZEN_MANIFEST_SHA256" \
        --arg frozen_closure "$BINARY_CLOSURE" \
        '
        .Status == "complete"
        and .Phase == "complete"
        and .Error == null
        and .TargetRecords == $expected_records
        and .CompletedRecords == $expected_records
        and .ProgressPath == $progress_path
        and .Subject.Commit == $source_commit
        and .Subject.TreeDigest == $source_tree_digest
        and .Subject.WorkingTreeDigest == $source_worktree_digest
        and .Subject.Dirty == $source_dirty
        and .Subject.Frozen.Project == ($source_repository + "/src/TerraBase/TerraBase.csproj")
        and .Subject.Frozen.Repository == $source_repository
        and .Subject.Frozen.Commit == $source_commit
        and .Subject.Frozen.TreeDigest == $source_tree_digest
        and .Subject.Frozen.WorkingTreeDigest == $source_worktree_digest
        and .Subject.Frozen.Dirty == $source_dirty
        and .Subject.Frozen.ManifestPath == $frozen_manifest
        and .Subject.Frozen.ManifestSha256 == $frozen_manifest_sha
        and .Subject.Frozen.BinaryClosure == $frozen_closure
        and .Subject.Frozen.BenchmarkAssemblySha256 == $expected_sha
        and .Subject.Live.Project == ($source_repository + "/src/TerraBase/TerraBase.csproj")
        and .IngestOnly == true
        and .RequireIsolatedCgroup == true
        and .IsolatedCgroup == true
        and .BenchmarkAssemblySha256 == $expected_sha
        and .MaxLocalStorageBytes == 85899345920
        and .MinimumFreeSpaceBytes == 107374182400
        and .PeakLocalStorageBytes <= 85899345920
        and .MinimumObservedFreeSpaceBytes >= 107374182400
        and .LocalStorageLimitExceededBytes == null
        and .FreeSpaceLimitViolation == null
        and .ResourceSamplerError == null
        and .LocalWalBytes > 0
        and .RemoteObjects.ObjectCount == 0
        and .RemoteObjects.TotalBytes == 0
        and .RemoteJournalObjects.ObjectCount > 0
        and .RemoteJournalObjects.TotalBytes > 0
        and .RemoteAck.AcknowledgedRecords >= $expected_records
        and .RemoteValidatedFrontier == .RemoteAck.AcknowledgedRecords
        and .FinalCgroup.MemoryMax == "4294967296"
        and .FinalCgroup.SwapMax == "0"
        and .FinalCgroup.SwapCurrent == "0"
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom 0(\\n|$)"))
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_kill 0(\\n|$)"))
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_group_kill 0(\\n|$)"))
        and ((.SkippedPhases | index("indexed-queries")) != null)
        and ((.SkippedPhases | index("backup")) != null)
        and ((.SkippedPhases | index("restore")) != null)
        ' "$receipt" > "$OUTPUT/receipt-verification.json" ||
        die "receipt verification failed"

    verify_resource_budget
    [[ -s "$OUTPUT/resource-verification.json" ]] ||
        die "resource verification report is missing or empty"
    verify_ingest_performance
    [[ -s "$OUTPUT/performance-verification.json" ]] ||
        die "performance verification report is missing or empty"

    sha256sum "$BENCH_DLL" "$receipt" "$OUTPUT/tiered-rustfs-summary.md" \
        "$OUTPUT/resource-verification.json" \
        "$OUTPUT/performance-verification.json" \
        > "$OUTPUT/verified-sha256sums.txt"
    jq '{status:.Status,phase:.Phase,target:.TargetRecords,completed:.CompletedRecords,remoteAck:.RemoteAck,remoteFrontier:.RemoteValidatedFrontier,remoteJournal:.RemoteJournalObjects,peakLocal:.PeakLocalStorageBytes,minimumFree:.MinimumObservedFreeSpaceBytes,finalCgroup:.FinalCgroup}' \
        "$receipt"
    jq '{resourceStatus:.status,memory:.memory,memoryEvents:.memoryEvents,pressureEventsObserved:.pressureEventsObserved,swap:.swap,gates:.gates}' \
        "$OUTPUT/resource-verification.json"
    jq '{performanceStatus:.status,gates:.gates}' \
        "$OUTPUT/performance-verification.json"
    printf 'verification=ok\nrun_dir=%s\n' "$OUTPUT"
}

parse_args() {
    if (($# > 0)); then
        case "$1" in
            preflight|launch|monitor|verify)
                MODE="$1"
                shift
                ;;
        esac
    fi
    while (($# > 0)); do
        case "$1" in
            --manifest)
                (($# >= 2)) || die "--manifest requires a path"
                MANIFEST="$2"
                shift 2
                ;;
            --records)
                (($# >= 2)) || die "--records requires 10M or 100M"
                RECORDS_INPUT="$2"
                shift 2
                ;;
            --run-label)
                (($# >= 2)) || die "--run-label requires a label"
                RUN_LABEL="$2"
                shift 2
                ;;
            --run-dir)
                (($# >= 2)) || die "--run-dir requires a path"
                RUN_DIR="$2"
                shift 2
                ;;
            --dotnet)
                (($# >= 2)) || die "--dotnet requires a path"
                DOTNET_BIN="$2"
                shift 2
                ;;
            --credentials-file)
                (($# >= 2)) || die "--credentials-file requires a path"
                CREDENTIALS_FILE="$2"
                shift 2
                ;;
            --rustfs-container)
                (($# >= 2)) || die "--rustfs-container requires a name"
                RUSTFS_CONTAINER="$2"
                shift 2
                ;;
            --endpoint)
                (($# >= 2)) || die "--endpoint requires a URL"
                RUSTFS_ENDPOINT="$2"
                shift 2
                ;;
            --bucket)
                (($# >= 2)) || die "--bucket requires a name"
                RUSTFS_BUCKET="$2"
                shift 2
                ;;
            --dry-run)
                DRY_RUN=1
                MODE=preflight
                shift
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            *)
                die "unknown argument: $1 (use --help)"
                ;;
        esac
    done
}

main() {
    parse_args "$@"
    case "$MODE" in
        preflight)
            [[ -n "$RECORDS_INPUT" ]] || die "--records is required for preflight"
            preflight
            ;;
        launch)
            [[ -n "$RECORDS_INPUT" ]] || die "--records is required for launch"
            (( DRY_RUN == 0 )) || { preflight; return; }
            launch
            ;;
        monitor)
            monitor
            ;;
        verify)
            verify
            ;;
        *)
            die "unsupported mode: $MODE"
            ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
