#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

# RustFS tiered larger-than-memory qualification runner.
#
# Modes:
#   preflight --manifest PATH
#   launch    --manifest PATH
#   verify    --run-dir PATH
#
# The benchmark itself performs two complete local-root loss drills, exact
# scans, mutations, compaction, cache eviction, and cold RustFS reopens. This
# runner keeps the run isolated, ties it to one immutable Release closure, and
# refuses to qualify a receipt unless every required phase is present.
#
# Credentials are never copied into a receipt or launch manifest. They are
# sourced only by the private systemd service immediately before the benchmark
# process starts.

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
readonly DEFAULT_RUSTFS_REGION="us-east-1"
readonly MEMORY_MAX_BYTES=268435456
readonly MEMORY_SWAP_MAX_BYTES=0
readonly MIN_FREE_BYTES=107374182400
readonly LOCAL_CAP_BYTES=4294967296
readonly IDLE_CACHE_BYTES=4194304
readonly SEGMENT_BOUND_BYTES=134217728
readonly STAGING_ALLOCATION_QUANTUM_BYTES=67108864
readonly DATASET_SIZE=40000
readonly PAYLOAD_SIZE=8192
readonly WRITE_BATCH=64
readonly UPDATE_COUNT=100
readonly DELETE_COUNT=1000
readonly POINT_READ_COUNT=32
readonly CHECKPOINT_RECORDS=4096
readonly PAYLOAD_SEED=1337

MODE=preflight
MANIFEST=
RUN_DIR=
OUTPUT_ROOT=
RUN_LABEL="tiered-rustfs-40k-8k-256m"
DOTNET_BIN="$DEFAULT_DOTNET_BIN"
CREDENTIALS_FILE="$DEFAULT_CREDENTIALS_FILE"
RUSTFS_CONTAINER="$DEFAULT_RUSTFS_CONTAINER"
RUSTFS_ENDPOINT="$DEFAULT_RUSTFS_ENDPOINT"
RUSTFS_BUCKET="$DEFAULT_RUSTFS_BUCKET"
RUSTFS_REGION="$DEFAULT_RUSTFS_REGION"
RUNTIME_MAX="4h"

RUN_ID=
OUTPUT=
PREFIX=
UNIT=
LAUNCH_ROOT=
BENCH_DLL=
BINARY_CLOSURE=
BENCH_SHA256=
SOURCE_REPOSITORY=
SOURCE_COMMIT=
SOURCE_TREE_DIGEST=
SOURCE_WORKTREE_DIGEST=
SOURCE_DIRTY=
MANIFEST_DOTNET_VERSION=

die() {
    printf 'strict-rustfs-tiered-40k: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Usage:
  strict-rustfs-tiered-40k.sh preflight --manifest PATH [options]
  strict-rustfs-tiered-40k.sh launch --manifest PATH [options]
  strict-rustfs-tiered-40k.sh verify --run-dir PATH

Modes:
  preflight  Check the frozen Release closure, host, RustFS, space, and cgroup.
             This mode never creates a receipt or starts a workload.
  launch     Run preflight, create a fresh run directory, and queue one
             isolated 256 MiB zero-swap systemd service.
  verify     Verify an inactive run's source identity, resource envelope,
             remote acknowledgement, two root-loss drills, exact scans,
             mutations, compactions, and cold reopens.

Options:
  --manifest PATH          freeze-manifest.txt from the final Release freeze.
  --run-label LABEL        Safe label used in the generated prefix and unit.
  --run-dir PATH           Existing receipt directory for verify mode.
  --output-root PATH       Fresh output directory under bench/receipts.
  --dotnet PATH            .NET runtime (default: source freeze runtime).
  --credentials-file PATH  Private RustFS shell credentials file.
  --rustfs-container NAME  Podman container name.
  --endpoint URL           RustFS endpoint (default: http://127.0.0.1:19000).
  --bucket NAME            RustFS bucket (default: terrabase-tiered).
  --region NAME            S3 signing region (default: us-east-1).
  --runtime-max SPAN       systemd RuntimeMaxSec (default: 4h).

The fixed workload is 40,000 records x 8,192 bytes, batch 64, 100 updates,
1,000 deletes, 32 point reads, 4,096-record scan checkpoints, 4 MiB idle
cache, 4 GiB local engine cap, and a 100 GiB free-space floor.
EOF
}

require_commands() {
    local command_name
    for command_name in \
        awk bash cmp curl date df dirname find jq nice pgrep podman readlink rg sha256sum \
        sort stat systemctl systemd-run tar; do
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

required_manifest_value() {
    local key="$1"
    local value
    value="$(manifest_value "$key" 2>/dev/null || true)"
    [[ -n "$value" ]] || die "manifest is missing required key: $key"
    printf '%s\n' "$value"
}

load_frozen_manifest() {
    [[ -n "$MANIFEST" ]] || die "--manifest is required for $MODE"
    [[ -f "$MANIFEST" ]] || die "manifest does not exist: $MANIFEST"
    [[ ! -L "$MANIFEST" ]] || die "manifest must not be a symlink: $MANIFEST"
    MANIFEST="$(readlink -f -- "$MANIFEST")"

    [[ "$(required_manifest_value schema)" == terrabase-final-source-matched-release-v1 ]] ||
        die "unsupported frozen manifest schema"
    BINARY_CLOSURE="$(required_manifest_value binaryClosure)"
    [[ -d "$BINARY_CLOSURE" && ! -L "$BINARY_CLOSURE" ]] ||
        die "frozen binary closure is unavailable: $BINARY_CLOSURE"
    BINARY_CLOSURE="$(readlink -f -- "$BINARY_CLOSURE")"
    BENCH_DLL="$BINARY_CLOSURE/TerraBase.Benchmarks.dll"
    [[ -f "$BENCH_DLL" && ! -L "$BENCH_DLL" ]] ||
        die "frozen benchmark assembly is unavailable: $BENCH_DLL"

    SOURCE_REPOSITORY="$(required_manifest_value repository)"
    SOURCE_COMMIT="$(required_manifest_value commit)"
    SOURCE_TREE_DIGEST="$(required_manifest_value treeDigest)"
    SOURCE_WORKTREE_DIGEST="$(required_manifest_value workingTreeDigest)"
    SOURCE_DIRTY="$(required_manifest_value sourceDirty)"
    MANIFEST_DOTNET_VERSION="$(required_manifest_value dotnetVersion)"

    [[ "$SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]] || die "manifest commit is invalid"
    [[ "$SOURCE_TREE_DIGEST" =~ ^[0-9a-fA-F]{40}$ ]] || die "manifest tree digest is invalid"
    [[ "$SOURCE_WORKTREE_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]] || die "manifest working-tree digest is invalid"
    [[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]] || die "manifest sourceDirty is invalid"

    BENCH_SHA256="$(sha256sum "$BENCH_DLL" | awk '{ print $1 }')"
    local manifest_bench_sha
    manifest_bench_sha="$(awk '
        length($1) == 64 && $1 !~ /[^0-9a-fA-F]/ &&
        $NF ~ /(^|\/)TerraBase[.]Benchmarks[.]dll$/ { print $1; exit }
    ' "$MANIFEST")"
    [[ -n "$manifest_bench_sha" && "$BENCH_SHA256" == "$manifest_bench_sha" ]] ||
        die "benchmark assembly does not match the freeze manifest"
    [[ -f "$BINARY_CLOSURE/core-hashes.sha256" && -f "$BINARY_CLOSURE/closure-sha256.txt" ]] ||
        die "frozen binary closure is missing hash lists"
    (cd "$BINARY_CLOSURE" &&
        sha256sum --quiet -c core-hashes.sha256 &&
        sha256sum --quiet -c closure-sha256.txt) ||
        die "frozen binary closure hash verification failed"
}

load_runtime() {
    [[ -x "$DOTNET_BIN" ]] || die ".NET runtime is not executable: $DOTNET_BIN"
    local runtime_version
    runtime_version="$($DOTNET_BIN --version)" || die "unable to query .NET runtime version"
    [[ "$runtime_version" == "$MANIFEST_DOTNET_VERSION" ]] ||
        die ".NET runtime mismatch: manifest=$MANIFEST_DOTNET_VERSION actual=$runtime_version"
}

check_space() {
    local available
    available="$(df -B1 --output=avail "$RECEIPTS_DIR" | awk 'NR == 2 { print $1 }')"
    [[ "$available" =~ ^[0-9]+$ ]] || die "unable to read free space for $RECEIPTS_DIR"
    (( available >= MIN_FREE_BYTES )) ||
        die "free space $available is below required floor $MIN_FREE_BYTES"
}

check_host() {
    [[ -d "$RECEIPTS_DIR" ]] || die "receipts directory is missing: $RECEIPTS_DIR"
    [[ -f "$CREDENTIALS_FILE" && ! -L "$CREDENTIALS_FILE" && -r "$CREDENTIALS_FILE" ]] ||
        die "RustFS credentials file is not a readable regular file: $CREDENTIALS_FILE"
    [[ -r /sys/fs/cgroup/cgroup.controllers ]] || die "cgroup v2 is not available"
    systemctl --user show-environment >/dev/null 2>&1 || die "user systemd manager is unavailable"

    local rustfs_state endpoint_status
    rustfs_state="$(podman inspect --format '{{.State.Status}}' "$RUSTFS_CONTAINER" 2>/dev/null || true)"
    [[ "$rustfs_state" == running ]] || die "RustFS container is not running: $RUSTFS_CONTAINER"
    endpoint_status="$(curl --connect-timeout 2 --max-time 5 -sS -o /dev/null \
        -w '%{http_code}' "$RUSTFS_ENDPOINT" 2>/dev/null || true)"
    case "$endpoint_status" in
        200|400|403|405) ;;
        *) die "RustFS endpoint is unavailable: $RUSTFS_ENDPOINT status=$endpoint_status" ;;
    esac

    # Do not overlap the qualification with any TerraBase workload. In
    # particular this protects the shared RustFS bucket and backing disk.
    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
       systemctl --user list-units --type=service --state=running --no-legend 2>/dev/null |
           rg -qi 'terrabase.*(strict|e2e|larger-memory|state-map|tiered)|state-map.*terrabase'; then
        die "an active TerraBase benchmark service is running; wait for a quiet window"
    fi
    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
       pgrep -af 'TerraBase\.Benchmarks(\.dll|/)' >/dev/null 2>&1; then
        die "an active TerraBase benchmark process is running; wait for a quiet window"
    fi
}

check_unit_free() {
    local load_state active_state
    load_state="$(systemctl --user show "$UNIT" -p LoadState --value 2>/dev/null || true)"
    active_state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || true)"
    [[ "$load_state" != loaded ]] || die "systemd unit already exists: $UNIT"
    case "$active_state" in
        active|activating|deactivating) die "systemd unit is already active: $UNIT" ;;
    esac
}

verify_wal_evidence() {
    local run_dir="$1"
    local receipt="$2"
    local wal_path="$run_dir/engine/wal"
    [[ -f "$wal_path" && ! -L "$wal_path" ]] || die "local WAL file is missing or a symlink"

    local wal_bytes receipt_wal_bytes
    wal_bytes="$(stat -c %s "$wal_path")"
    receipt_wal_bytes="$(jq -r '.LocalWalBytes // empty' "$receipt")"
    [[ "$receipt_wal_bytes" =~ ^[0-9]+$ ]] ||
        die "receipt LocalWalBytes is missing or invalid: ${receipt_wal_bytes:-missing}"
    [[ "$wal_bytes" == "$receipt_wal_bytes" ]] ||
        die "local WAL size does not match receipt: file=$wal_bytes receipt=$receipt_wal_bytes"
}

preflight() {
    require_commands
    load_frozen_manifest
    load_runtime
    check_space
    check_host
    printf 'preflight=ok\n'
    printf 'manifest=%s\n' "$MANIFEST"
    printf 'benchmark_dll=%s\n' "$BENCH_DLL"
    printf 'benchmark_sha256=%s\n' "$BENCH_SHA256"
    printf 'source_commit=%s\n' "$SOURCE_COMMIT"
    printf 'source_tree_digest=%s\n' "$SOURCE_TREE_DIGEST"
    printf 'source_worktree_digest=%s\n' "$SOURCE_WORKTREE_DIGEST"
    printf 'rustfs_endpoint=%s\n' "$RUSTFS_ENDPOINT"
    printf 'rustfs_bucket=%s\n' "$RUSTFS_BUCKET"
}

prepare_launch_identity() {
    [[ "$RUN_LABEL" =~ ^[[:alnum:]_.-]+$ ]] || die "run label contains unsafe characters"
    local timestamp
    timestamp="$(date -u +%Y%m%dT%H%M%S%NZ)"
    RUN_ID="$RUN_LABEL-$timestamp-$$"
    OUTPUT="${OUTPUT_ROOT:-$RECEIPTS_DIR/$RUN_ID}"
    OUTPUT="$(readlink -m -- "$OUTPUT")"
    case "$OUTPUT" in
        "$RECEIPTS_DIR"/*) ;;
        *) die "output root must be under $RECEIPTS_DIR: $OUTPUT" ;;
    esac
    PREFIX="$RUN_ID"
    UNIT="terrabase-$RUN_ID.service"
    LAUNCH_ROOT="$OUTPUT.launch"
    [[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || die "refusing to reuse output path: $OUTPUT"
    [[ ! -e "$LAUNCH_ROOT" && ! -L "$LAUNCH_ROOT" ]] || die "refusing to reuse launch path: $LAUNCH_ROOT"
    check_unit_free
}

write_launch_env() {
    FROZEN_MANIFEST_PATH="$LAUNCH_ROOT/freeze-manifest.txt"
    FROZEN_MANIFEST_SHA256="$(sha256sum "$MANIFEST" | awk '{print $1}')"
    mkdir -m 700 "$OUTPUT" "$LAUNCH_ROOT"
    {
        printf 'RUN_ID=%q\n' "$RUN_ID"
        printf 'OUTPUT=%q\n' "$OUTPUT"
        printf 'PREFIX=%q\n' "$PREFIX"
        printf 'UNIT=%q\n' "$UNIT"
        printf 'LAUNCH_ROOT=%q\n' "$LAUNCH_ROOT"
        printf 'MANIFEST=%q\n' "$MANIFEST"
        printf 'BINARY_CLOSURE=%q\n' "$BINARY_CLOSURE"
        printf 'FROZEN_MANIFEST_PATH=%q\n' "$FROZEN_MANIFEST_PATH"
        printf 'FROZEN_MANIFEST_SHA256=%q\n' "$FROZEN_MANIFEST_SHA256"
        printf 'BENCH_DLL=%q\n' "$BENCH_DLL"
        printf 'BENCH_SHA256=%q\n' "$BENCH_SHA256"
        printf 'SOURCE_REPOSITORY=%q\n' "$SOURCE_REPOSITORY"
        printf 'SOURCE_PROJECT=%q\n' "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj"
        printf 'SOURCE_COMMIT=%q\n' "$SOURCE_COMMIT"
        printf 'SOURCE_TREE_DIGEST=%q\n' "$SOURCE_TREE_DIGEST"
        printf 'SOURCE_WORKTREE_DIGEST=%q\n' "$SOURCE_WORKTREE_DIGEST"
        printf 'SOURCE_DIRTY=%q\n' "$SOURCE_DIRTY"
        printf 'DOTNET_BIN=%q\n' "$DOTNET_BIN"
        printf 'S3_ENDPOINT=%q\n' "$RUSTFS_ENDPOINT"
        printf 'S3_BUCKET=%q\n' "$RUSTFS_BUCKET"
        printf 'S3_REGION=%q\n' "$RUSTFS_REGION"
    } > "$OUTPUT/launch.env"
    chmod 600 "$OUTPUT/launch.env"
}

write_wrapper() {
    local bash_bin nice_bin
    bash_bin="$(command -v bash)"
    nice_bin="$(command -v nice)"
    {
        printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail' "IFS=\$'\\n\\t'"
        printf 'OUTPUT=%q\n' "$OUTPUT"
        printf 'LAUNCH_ENV_PARSER=%q\n' "$LAUNCH_ENV_PARSER"
        printf 'DOTNET_BIN=%q\n' "$DOTNET_BIN"
        printf 'BENCH_DLL=%q\n' "$BENCH_DLL"
        printf 'UNIT=%q\n' "$UNIT"
        printf 'NICE_BIN=%q\n' "$nice_bin"
        cat <<'WRAPPER'

# Keep credentials out of the console log and all receipt metadata. The path
# is injected into this private systemd unit and is intentionally not written
# into the launch manifest or wrapper artifact.
: "${TB_TIERED_CREDENTIALS_FILE:?RustFS credential environment is missing}"
source "$TB_TIERED_CREDENTIALS_FILE"
source "$LAUNCH_ENV_PARSER"
load_launch_env_file "$OUTPUT/launch.env" \
    RUN_ID OUTPUT PREFIX UNIT LAUNCH_ROOT MANIFEST BENCH_DLL BENCH_SHA256 \
    SOURCE_REPOSITORY SOURCE_COMMIT SOURCE_TREE_DIGEST SOURCE_WORKTREE_DIGEST \
    SOURCE_DIRTY DOTNET_BIN S3_ENDPOINT S3_BUCKET S3_REGION \
    ?BINARY_CLOSURE ?FROZEN_MANIFEST_PATH ?FROZEN_MANIFEST_SHA256 ?SOURCE_PROJECT
export TERRABASE_S3_ENDPOINT="$S3_ENDPOINT"
export TERRABASE_S3_BUCKET="$S3_BUCKET"
export TERRABASE_S3_REGION="$S3_REGION"
export TERRABASE_FROZEN_SOURCE_PROJECT="$SOURCE_PROJECT"
export TERRABASE_FROZEN_SOURCE_REPOSITORY="$SOURCE_REPOSITORY"
export TERRABASE_FROZEN_SOURCE_COMMIT="$SOURCE_COMMIT"
export TERRABASE_FROZEN_SOURCE_TREE_DIGEST="$SOURCE_TREE_DIGEST"
export TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST="$SOURCE_WORKTREE_DIGEST"
export TERRABASE_FROZEN_SOURCE_DIRTY="$SOURCE_DIRTY"
export TERRABASE_FROZEN_MANIFEST_PATH="$LAUNCH_ROOT/freeze-manifest.txt"
export TERRABASE_FROZEN_MANIFEST_SHA256="$(sha256sum "$LAUNCH_ROOT/freeze-manifest.txt" | awk '{print $1}')"
export TERRABASE_FROZEN_BINARY_CLOSURE="$BINARY_CLOSURE"
export TERRABASE_FROZEN_BENCHMARK_ASSEMBLY_SHA256="$BENCH_SHA256"
exec > "$OUTPUT/console.log" 2>&1

run_status=1
if "$NICE_BIN" -n 19 "$DOTNET_BIN" "$BENCH_DLL" \
    --workload tiered-segment \
    --records 40000 \
    --batch 64 \
    --payload-bytes 8192 \
    --payload-mode per-key-incompressible \
    --updates 100 \
    --deletes 1000 \
    --point-reads 32 \
    --checkpoint-records 4096 \
    --cache-bytes 4194304 \
    --max-local-storage-bytes 4294967296 \
    --min-free-space-bytes 107374182400 \
    --resource-sample-ms 500 \
    --require-isolated-cgroup \
    --s3-endpoint "$S3_ENDPOINT" \
    --s3-bucket "$S3_BUCKET" \
    --s3-region "$S3_REGION" \
    --prefix "$PREFIX" \
    --output "$OUTPUT"; then
    run_status=0
else
    run_status=$?
fi
printf 'exitCode=%s\n' "$run_status" > "$OUTPUT/benchmark-exit-status.tmp"
mv -- "$OUTPUT/benchmark-exit-status.tmp" "$OUTPUT/benchmark-exit-status"
exit "$run_status"
WRAPPER
    } > "$LAUNCH_ROOT/run-wrapper.sh"
    chmod 700 "$LAUNCH_ROOT/run-wrapper.sh"
    printf '%s\n' "$bash_bin" > "$LAUNCH_ROOT/bash-path.txt"
}

launch() {
    preflight >/dev/null
    prepare_launch_identity
    write_launch_env
    write_wrapper
    cp -- "$MANIFEST" "$LAUNCH_ROOT/freeze-manifest.txt"
    cp -- "$BINARY_CLOSURE/core-hashes.sha256" "$LAUNCH_ROOT/core-hashes.sha256"
    sha256sum "$BENCH_DLL" > "$OUTPUT/binary-sha256.txt"
    local wrapper_sha
    wrapper_sha="$(sha256sum "$LAUNCH_ROOT/run-wrapper.sh" | awk '{ print $1 }')"
    {
        printf 'schema=terrabase-rustfs-tiered-40k-launch-v1\n'
        printf 'createdAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf 'unit=%s\n' "$UNIT"
        printf 'runtimeMax=%s\n' "$RUNTIME_MAX"
        printf 'memoryMaxBytes=%s\n' "$MEMORY_MAX_BYTES"
        printf 'memorySwapMaxBytes=%s\n' "$MEMORY_SWAP_MAX_BYTES"
        printf 'oomPolicy=stop\n'
        printf 'binaryRoot=%s\n' "$BINARY_CLOSURE"
        printf 'binary=%s\n' "$BENCH_DLL"
        printf 'binarySha256=%s\n' "$BENCH_SHA256"
        printf 'sourceRepository=%s\n' "$SOURCE_REPOSITORY"
        printf 'sourceCommit=%s\n' "$SOURCE_COMMIT"
        printf 'sourceTreeDigest=%s\n' "$SOURCE_TREE_DIGEST"
        printf 'sourceWorkingTreeDigest=%s\n' "$SOURCE_WORKTREE_DIGEST"
        printf 'sourceDirty=%s\n' "$SOURCE_DIRTY"
        printf 'outputRoot=%s\n' "$OUTPUT"
        printf 'launchRoot=%s\n' "$LAUNCH_ROOT"
        printf 'prefix=%s\n' "$PREFIX"
        printf 'commandWrapper=%s\n' "$LAUNCH_ROOT/run-wrapper.sh"
        printf 'commandWrapperSha256=%s\n' "$wrapper_sha"
        printf 'benchmarkFlags=--workload tiered-segment --records 40000 --batch 64 --payload-bytes 8192 --payload-mode per-key-incompressible --updates 100 --deletes 1000 --point-reads 32 --checkpoint-records 4096 --cache-bytes 4194304 --max-local-storage-bytes 4294967296 --min-free-space-bytes 107374182400 --resource-sample-ms 500 --require-isolated-cgroup --s3-endpoint %s --s3-bucket %s --s3-region %s --prefix %s\n' "$RUSTFS_ENDPOINT" "$RUSTFS_BUCKET" "$RUSTFS_REGION" "$PREFIX"
    } > "$LAUNCH_ROOT/launch-manifest.txt"

    local bash_bin
    bash_bin="$(command -v bash)"
    systemd-run --user --no-block --quiet --unit="$UNIT" \
        --property=MemoryMax="$MEMORY_MAX_BYTES" \
        --property=MemorySwapMax="$MEMORY_SWAP_MAX_BYTES" \
        --property=OOMPolicy=stop \
        --property="RuntimeMaxSec=$RUNTIME_MAX" \
        --working-directory="$BASE_DIR" \
        --setenv=TB_TIERED_OUTPUT="$OUTPUT" \
        --setenv=TB_TIERED_CREDENTIALS_FILE="$CREDENTIALS_FILE" \
        "$bash_bin" "$LAUNCH_ROOT/run-wrapper.sh" ||
        die "systemd could not queue benchmark unit; launch artifacts retained at $LAUNCH_ROOT"

    printf 'launched=true\n'
    printf 'run_id=%s\n' "$RUN_ID"
    printf 'run_dir=%s\n' "$OUTPUT"
    printf 'unit=%s\n' "$UNIT"
    printf 'prefix=%s\n' "$PREFIX"
}

load_launch_env() {
    [[ -n "$RUN_DIR" ]] || die "--run-dir is required for verify"
    [[ -d "$RUN_DIR" && ! -L "$RUN_DIR" ]] || die "run directory is unavailable: $RUN_DIR"
    RUN_DIR="$(readlink -f -- "$RUN_DIR")"
    [[ "$RUN_DIR" == "$RECEIPTS_DIR"/* ]] || die "run directory must be under $RECEIPTS_DIR"
    [[ -f "$RUN_DIR/launch.env" && ! -L "$RUN_DIR/launch.env" ]] || die "launch.env is missing"
    [[ "$(stat -c %a "$RUN_DIR/launch.env")" == 600 ]] || die "launch.env permissions are not 600"
    load_launch_env_file "$RUN_DIR/launch.env" \
        RUN_ID OUTPUT PREFIX UNIT LAUNCH_ROOT MANIFEST BENCH_DLL BENCH_SHA256 \
        SOURCE_REPOSITORY SOURCE_COMMIT SOURCE_TREE_DIGEST SOURCE_WORKTREE_DIGEST \
        SOURCE_DIRTY DOTNET_BIN S3_ENDPOINT S3_BUCKET S3_REGION \
        ?BINARY_CLOSURE ?FROZEN_MANIFEST_PATH ?FROZEN_MANIFEST_SHA256 ?SOURCE_PROJECT \
        || die "launch.env failed strict metadata validation"
    [[ "$OUTPUT" == "$RUN_DIR" ]] || die "launch.env output does not match --run-dir"
    [[ "$PREFIX" == "$RUN_ID" ]] || die "launch.env prefix is not the unique run id"
    [[ "$UNIT" =~ ^terrabase-[[:alnum:]_.-]+[.]service$ ]] || die "launch.env unit is invalid"
    [[ -n "${S3_ENDPOINT:-}" && -n "${S3_BUCKET:-}" && -n "${S3_REGION:-}" ]] ||
        die "launch.env is missing S3 identity"
    RUSTFS_ENDPOINT="$S3_ENDPOINT"
    RUSTFS_BUCKET="$S3_BUCKET"
    RUSTFS_REGION="$S3_REGION"
}

verify_frozen_provenance() {
    local receipt="$1"
    local frozen_manifest="$2"
    local frozen_manifest_sha="$3"
    local frozen_closure="$4"
    local source_project="$5"
    local source_repository="$6"
    local source_commit="$7"
    local source_tree="$8"
    local source_worktree="$9"
    local source_dirty="${10}"
    local benchmark_sha="${11}"

    jq -e \
        --arg project "$source_project" \
        --arg repository "$source_repository" \
        --arg commit "$source_commit" \
        --arg tree "$source_tree" \
        --arg worktree "$source_worktree" \
        --argjson dirty "$source_dirty" \
        --arg manifest "$frozen_manifest" \
        --arg manifest_sha "$frozen_manifest_sha" \
        --arg closure "$frozen_closure" \
        --arg benchmark_sha "$benchmark_sha" \
        '.Subject | type == "object"
         and (.Project == $project)
         and (.Repository == $repository)
         and (.Commit == $commit)
         and (.TreeDigest == $tree)
         and (.WorkingTreeDigest == $worktree)
         and (.Dirty == $dirty)
         and (.Frozen | type == "object")
         and (.Frozen.Project == $project)
         and (.Frozen.Repository == $repository)
         and (.Frozen.Commit == $commit)
         and (.Frozen.TreeDigest == $tree)
         and (.Frozen.WorkingTreeDigest == $worktree)
         and (.Frozen.Dirty == $dirty)
         and (.Frozen.ManifestPath == $manifest)
         and (.Frozen.ManifestSha256 == $manifest_sha)
         and (.Frozen.BinaryClosure == $closure)
         and (.Frozen.BenchmarkAssemblySha256 == $benchmark_sha)
         and (.Live | type == "object")
         and (.Live.Project == $project)
         and (.Live.Repository == $repository)
         and (.Live.Commit == $commit)
         and (.Live.TreeDigest == $tree)
         and (.Live.WorkingTreeDigest | type == "string" and test("^[0-9a-fA-F]{64}$"))' \
        "$receipt" >/dev/null
}

verify_receipt_digest() {
    local receipt="$1"
    [[ -s "$receipt" && ! -L "$receipt" ]] || return 1
    [[ -x "$DOTNET_BIN" && -f "$BENCH_DLL" && ! -L "$BENCH_DLL" ]] || return 1
    "$DOTNET_BIN" "$BENCH_DLL" --verify-run-receipt "$receipt" >/dev/null 2>&1
}

verify_compact_receipt() {
    local compact="$1"
    local detailed="$2"
    local output="$3"
    local project="$4"
    local repository="$5"
    local commit="$6"
    local tree="$7"
    local worktree="$8"
    local dirty="$9"
    local frozen_manifest="${10}"
    local frozen_manifest_sha="${11}"
    local frozen_closure="${12}"
    local benchmark_sha="${13}"
    local detailed_sha detailed_bytes
    detailed_sha="$(sha256sum "$detailed" | awk '{print $1}')"
    detailed_bytes="$(stat -c '%s' "$detailed")"

    jq -e \
        --arg output "$output" \
        --arg project "$project" \
        --arg repository "$repository" \
        --arg commit "$commit" \
        --arg tree "$tree" \
        --arg worktree "$worktree" \
        --argjson dirty "$dirty" \
        --arg manifest "$frozen_manifest" \
        --arg manifest_sha "$frozen_manifest_sha" \
        --arg closure "$frozen_closure" \
        --arg benchmark_sha "$benchmark_sha" \
        --arg detailed_sha "$detailed_sha" \
        --argjson detailed_bytes "$detailed_bytes" \
        '.schemaVersion == 1
         and .workload == "tiered-segment"
         and .status == "complete"
         and .exitCode == 0
         and .outputDirectory == $output
         and (.receiptSha256 | type == "string" and test("^[0-9a-f]{64}$"))
         and .subject.project == $project
         and .subject.repository == $repository
         and .subject.commit == $commit
         and .subject.treeDigest == $tree
         and .subject.workingTreeDigest == $worktree
         and .subject.dirty == $dirty
         and (.subject.frozen | type == "object")
         and .subject.frozen.project == $project
         and .subject.frozen.repository == $repository
         and .subject.frozen.commit == $commit
         and .subject.frozen.treeDigest == $tree
         and .subject.frozen.workingTreeDigest == $worktree
         and .subject.frozen.dirty == $dirty
         and .subject.frozen.manifestPath == $manifest
         and .subject.frozen.manifestSha256 == $manifest_sha
         and .subject.frozen.binaryClosure == $closure
         and .subject.frozen.benchmarkAssemblySha256 == $benchmark_sha
         and (.subject.live | type == "object")
         and .subject.live.project == $project
         and .subject.live.repository == $repository
         and .subject.live.commit == $commit
         and .subject.live.treeDigest == $tree
         and (.subject.live.workingTreeDigest | type == "string" and test("^[0-9a-fA-F]{64}$"))
         and ([.artifacts[]? | select(.file == "tiered-rustfs-receipt.json" and
             .sha256 == $detailed_sha and .bytes == $detailed_bytes)] | length) == 1' \
        "$compact" >/dev/null || return 1

    # jq can validate the receipt shape but cannot reproduce the exact
    # System.Text.Json JsonNode canonicalization used by RunReceipt.Write.
    # Delegate the digest check to the frozen benchmark assembly itself.
    verify_receipt_digest "$compact"
}

verify() {
    require_commands
    load_launch_env
    local launch_manifest="$MANIFEST"
    local launch_bench_dll="$BENCH_DLL"
    local launch_bench_sha="$BENCH_SHA256"
    local launch_commit="$SOURCE_COMMIT"
    local launch_tree="$SOURCE_TREE_DIGEST"
    local launch_worktree="$SOURCE_WORKTREE_DIGEST"
    local launch_dirty="$SOURCE_DIRTY"
    MANIFEST="$launch_manifest"
    load_frozen_manifest
    [[ -f "$LAUNCH_ROOT/freeze-manifest.txt" && ! -L "$LAUNCH_ROOT/freeze-manifest.txt" ]] || die "frozen launch manifest copy is missing"
    cmp -s "$LAUNCH_ROOT/freeze-manifest.txt" "$MANIFEST" || die "frozen launch manifest copy changed"
    load_runtime
    [[ "$BENCH_DLL" == "$launch_bench_dll" ]] || die "benchmark DLL no longer matches launch metadata"
    [[ "$BENCH_SHA256" == "$launch_bench_sha" ]] || die "benchmark assembly changed after launch"
    [[ "$SOURCE_COMMIT" == "$launch_commit" ]] || die "source commit changed after launch"
    [[ "$SOURCE_TREE_DIGEST" == "$launch_tree" ]] || die "source tree digest changed after launch"
    [[ "$SOURCE_WORKTREE_DIGEST" == "$launch_worktree" ]] || die "source worktree digest changed after launch"
    [[ "$SOURCE_DIRTY" == "$launch_dirty" ]] || die "source dirty flag changed after launch"

    local state service_status service_memory_max service_swap_max
    local launch_memory_max launch_swap_max
    state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || printf missing)"
    case "$state" in
        active|activating|deactivating) die "benchmark unit is still running: $UNIT" ;;
    esac
    systemctl --user show "$UNIT" \
        -p Id -p LoadState -p ActiveState -p Result -p ExecMainStatus \
        -p MemoryCurrent -p MemoryPeak -p MemoryMax -p MemorySwapMax -p OOMPolicy \
        > "$RUN_DIR/systemd-status.txt" 2>&1 || die "unable to capture systemd status"
    service_status="$(systemctl --user show "$UNIT" -p ExecMainStatus --value 2>/dev/null || true)"
    [[ "$service_status" == 0 ]] || die "systemd unit ExecMainStatus is not zero: ${service_status:-missing}"
    service_memory_max="$(systemctl --user show "$UNIT" -p MemoryMax --value 2>/dev/null || true)"
    service_swap_max="$(systemctl --user show "$UNIT" -p MemorySwapMax --value 2>/dev/null || true)"
    [[ -f "$LAUNCH_ROOT/launch-manifest.txt" && ! -L "$LAUNCH_ROOT/launch-manifest.txt" ]] ||
        die "launch manifest is missing"
    launch_memory_max="$(awk -F= '$1 == "memoryMaxBytes" { print $2; exit }' "$LAUNCH_ROOT/launch-manifest.txt")"
    launch_swap_max="$(awk -F= '$1 == "memorySwapMaxBytes" { print $2; exit }' "$LAUNCH_ROOT/launch-manifest.txt")"
    [[ "$launch_memory_max" == "$MEMORY_MAX_BYTES" ]] ||
        die "launch manifest memoryMaxBytes is not $MEMORY_MAX_BYTES: ${launch_memory_max:-missing}"
    [[ "$launch_swap_max" == "$MEMORY_SWAP_MAX_BYTES" ]] ||
        die "launch manifest memorySwapMaxBytes is not zero: ${launch_swap_max:-missing}"
    # An inactive systemd service may have already released its cgroup, in
    # which case MemoryMax/MemorySwapMax are reported as infinity. The
    # persisted launch manifest and final cgroup receipt remain authoritative;
    # retain the live systemd checks whenever those properties are available.
    if [[ -n "$service_memory_max" && "$service_memory_max" != infinity && "$service_memory_max" != "[not set]" ]]; then
        [[ "$service_memory_max" == "$MEMORY_MAX_BYTES" ]] ||
            die "systemd MemoryMax is not $MEMORY_MAX_BYTES: $service_memory_max"
    fi
    if [[ -n "$service_swap_max" && "$service_swap_max" != infinity && "$service_swap_max" != "[not set]" ]]; then
        [[ "$service_swap_max" == "$MEMORY_SWAP_MAX_BYTES" ]] ||
            die "systemd MemorySwapMax is not zero: $service_swap_max"
    fi

    local receipt="$RUN_DIR/tiered-rustfs-receipt.json"
    [[ -s "$receipt" ]] || die "tiered receipt is missing or empty"
    [[ -s "$RUN_DIR/tiered-rustfs-summary.md" ]] || die "tiered summary is missing or empty"
    [[ -s "$RUN_DIR/resource-envelope.json" ]] || die "resource envelope is missing or empty"
    [[ -s "$RUN_DIR/resource-samples.jsonl" ]] || die "resource samples are missing or empty"
    [[ -s "$RUN_DIR/progress.jsonl" ]] || die "progress telemetry is missing or empty"
    [[ -f "$RUN_DIR/benchmark-exit-status" ]] || die "benchmark exit receipt is missing"
    [[ "$(cat "$RUN_DIR/benchmark-exit-status")" == exitCode=0 ]] || die "benchmark did not exit successfully"
    verify_wal_evidence "$RUN_DIR" "$receipt"
    verify_frozen_provenance \
        "$receipt" \
        "$LAUNCH_ROOT/freeze-manifest.txt" \
        "$(sha256sum "$LAUNCH_ROOT/freeze-manifest.txt" | awk '{print $1}')" \
        "$BINARY_CLOSURE" \
        "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        "$SOURCE_REPOSITORY" \
        "$SOURCE_COMMIT" \
        "$SOURCE_TREE_DIGEST" \
        "$SOURCE_WORKTREE_DIGEST" \
        "$SOURCE_DIRTY" \
        "$BENCH_SHA256" || die "tiered receipt is missing exact frozen source provenance"
    [[ -s "$RUN_DIR/run-receipt.json" && ! -L "$RUN_DIR/run-receipt.json" ]] || die "compact run receipt is missing"
    verify_compact_receipt \
        "$RUN_DIR/run-receipt.json" \
        "$receipt" \
        "$OUTPUT" \
        "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        "$SOURCE_REPOSITORY" \
        "$SOURCE_COMMIT" \
        "$SOURCE_TREE_DIGEST" \
        "$SOURCE_WORKTREE_DIGEST" \
        "$SOURCE_DIRTY" \
        "$LAUNCH_ROOT/freeze-manifest.txt" \
        "$(sha256sum "$LAUNCH_ROOT/freeze-manifest.txt" | awk '{print $1}')" \
        "$BINARY_CLOSURE" \
        "$BENCH_SHA256" || die "compact run receipt is missing exact frozen provenance or artifact integrity"

    local remote_journal_bytes staging_cap
    remote_journal_bytes="$(jq -r '.RemoteJournalObjects.TotalBytes // empty' "$receipt")"
    [[ "$remote_journal_bytes" =~ ^[0-9]+$ && "$remote_journal_bytes" -gt 0 ]] ||
        die "receipt remote journal bytes are missing or invalid: ${remote_journal_bytes:-missing}"
    staging_cap="$(( (remote_journal_bytes / STAGING_ALLOCATION_QUANTUM_BYTES + 1) * STAGING_ALLOCATION_QUANTUM_BYTES ))"

    local cgroup_verification="$RUN_DIR/cgroup-verification.json"
    [[ -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-cgroup.jq" ]] ||
        die "cgroup verification filter is missing"
    jq -e --argjson configured_max "$MEMORY_MAX_BYTES" \
        -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-cgroup.jq" \
        "$receipt" > "$cgroup_verification.tmp" || {
        rm -f -- "$cgroup_verification.tmp"
        die "tiered receipt failed the cgroup resource gates"
    }
    mv -- "$cgroup_verification.tmp" "$cgroup_verification"

    local durability_verification="$RUN_DIR/durability-verification.json"
    [[ -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-durability.jq" ]] ||
        die "durability verification filter is missing"
    jq -e --argjson minimum_ack 41104 \
        -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-durability.jq" \
        "$receipt" > "$durability_verification.tmp" || {
        rm -f -- "$durability_verification.tmp"
        die "tiered receipt failed the remote durability evidence gates"
    }
    mv -- "$durability_verification.tmp" "$durability_verification"

    local staging_verification="$RUN_DIR/staging-verification.json"
    [[ -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-staging.jq" ]] ||
        die "staging verification filter is missing"
    jq -e --argjson allocation_quantum "$STAGING_ALLOCATION_QUANTUM_BYTES" \
        -f "$SCRIPT_DIR/strict-rustfs-tiered-40k-staging.jq" \
        "$receipt" > "$staging_verification.tmp" || {
        rm -f -- "$staging_verification.tmp"
        die "tiered receipt failed the derived staging bound or final staging cleanup gate"
    }
    mv -- "$staging_verification.tmp" "$staging_verification"

    jq -e \
        --arg expected_sha "$BENCH_SHA256" \
        --arg frozen_manifest "$LAUNCH_ROOT/freeze-manifest.txt" \
        --arg frozen_manifest_sha "$(sha256sum "$LAUNCH_ROOT/freeze-manifest.txt" | awk '{print $1}')" \
        --arg frozen_closure "$BINARY_CLOSURE" \
        --arg project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        --arg repository "$SOURCE_REPOSITORY" \
        --arg commit "$SOURCE_COMMIT" \
        --arg tree "$SOURCE_TREE_DIGEST" \
        --arg working_tree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        --arg binary "$BENCH_DLL" \
        --arg output "$OUTPUT" \
        --arg prefix "$PREFIX" \
        --arg endpoint "$RUSTFS_ENDPOINT" \
        --arg bucket "$RUSTFS_BUCKET" \
        --arg region "$RUSTFS_REGION" \
        --argjson records "$DATASET_SIZE" \
        --argjson payload "$PAYLOAD_SIZE" \
        --argjson batch "$WRITE_BATCH" \
        --argjson updates "$UPDATE_COUNT" \
        --argjson deletes "$DELETE_COUNT" \
        --argjson minimum_ack 41104 \
        --argjson memory_max "$MEMORY_MAX_BYTES" \
        --argjson local_cap "$LOCAL_CAP_BYTES" \
        --argjson cache_cap "$((IDLE_CACHE_BYTES + SEGMENT_BOUND_BYTES))" \
        --argjson staging_cap "$staging_cap" \
        --argjson free_floor "$MIN_FREE_BYTES" \
        '
        def phase($name; $completed):
          any(.PhaseMetrics[]?; .Name == $name and .CompletedRecords == $completed);
        .Status == "complete"
        and .Phase == "complete"
        and .Error == null
        and .TargetRecords == $records
        and .CompletedRecords == $records
        and .PayloadBytes == $payload
        and .PayloadMode == "per-key-incompressible"
        and .PayloadSeed == 1337
        and .Batch == $batch
        and .IngestOnly == false
        and .RequireIsolatedCgroup == true
        and .IsolatedCgroup == true
        and .Subject.Project == $project
        and .Subject.Repository == $repository
        and .Subject.Commit == $commit
        and .Subject.TreeDigest == $tree
        and (.Subject.WorkingTreeDigest | type == "string" and test("^[0-9a-fA-F]{64}$"))
        and .Subject.Dirty == $dirty
        and (has("Subject") and (.Subject | type == "object"))
        and (.Subject.Frozen | type == "object")
        and .Subject.Frozen.Project == $project
        and .Subject.Frozen.Repository == $repository
        and .Subject.Frozen.Commit == $commit
        and .Subject.Frozen.TreeDigest == $tree
        and .Subject.Frozen.WorkingTreeDigest == $working_tree
        and .Subject.Frozen.Dirty == $dirty
        and .Subject.Frozen.ManifestPath == $frozen_manifest
        and .Subject.Frozen.ManifestSha256 == $frozen_manifest_sha
        and .Subject.Frozen.BinaryClosure == $frozen_closure
        and .Subject.Frozen.BenchmarkAssemblySha256 == $expected_sha
        and (.Subject.Live | type == "object")
        and .Subject.Live.Project == $project
        and .Subject.Live.Repository == $repository
        and .Subject.Live.Commit == $commit
        and .Subject.Live.TreeDigest == $tree
        and .BenchmarkAssembly == $binary
        and .BenchmarkAssemblySha256 == $expected_sha
        and .EngineRoot == ($output + "/engine")
        and .Prefix == $prefix
        and .S3Endpoint == $endpoint
        and .S3Bucket == $bucket
        and .Region == $region
        and .RemoteAck.AcknowledgedBatches > 0
        and .RemoteAck.AcknowledgedRecords >= $minimum_ack
        and .RemoteJournalObjects.ObjectCount == .RemoteAck.AcknowledgedBatches
        and .RemoteObjects.ObjectCount > 0
        and .RemoteObjects.TotalBytes > 0
        and .RemoteJournalObjects.ObjectCount > 0
        and .RemoteJournalObjects.TotalBytes > 0
        and .SeedRootLossRecoverySeconds > 0
        and .MutationRootLossRecoverySeconds > 0
        and .ResourceSamplerError == null
        and .FreeSpaceLimitViolation == null
        and .LocalStorageLimitExceededBytes == null
        and .PeakLocalStorageBytes <= $local_cap
        and .MinimumObservedFreeSpaceBytes >= $free_floor
        and .PeakCacheBytes <= $cache_cap
        and .PeakStagingBytes <= $staging_cap
        and .LatestResources.StagingBytes == 0
        and .CacheBytesAfterInitialVerification == 0
        and .CacheBytesAfterVerification == 0
        and ((.SkippedPhases // []) | length) == 0
        and (.FinalCgroup.MemoryMax | tonumber) == $memory_max
        and (.FinalCgroup.MemoryCurrent | tonumber) <= $memory_max
        and (.FinalCgroup.SwapMax | tonumber) == 0
        and (.FinalCgroup.SwapCurrent | tonumber) == 0
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom 0($|\\n)"))
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_kill 0($|\\n)"))
        and (.FinalCgroup.MemoryEvents | test("(^|\\n)oom_group_kill 0($|\\n)"))
        and phase("seed"; 40000)
        and phase("seed-root-loss-recovery"; 40000)
        and phase("initial-compaction-upload"; 40000)
        and phase("initial-exact-scan"; 40000)
        and phase("initial-remote-reopen-scan"; 40000)
        and phase("updates"; 100)
        and phase("deletes"; 1000)
        and phase("mutation-root-loss-recovery"; 39000)
        and phase("final-compaction-upload"; 40000)
        and phase("final-exact-scan"; 39000)
        and phase("final-remote-reopen-scan"; 39000)
        ' "$receipt" > "$RUN_DIR/receipt-verification.json" ||
        die "tiered receipt failed the 40K RustFS qualification gates"

    jq -n \
        --arg schema terrabase-tiered-rustfs-provenance-verification-v2 \
        --arg frozen_manifest "$LAUNCH_ROOT/freeze-manifest.txt" \
        --arg frozen_manifest_sha "$(sha256sum "$LAUNCH_ROOT/freeze-manifest.txt" | awk '{print $1}')" \
        --arg frozen_closure "$BINARY_CLOSURE" \
        --arg frozen_project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" \
        --arg frozen_repository "$SOURCE_REPOSITORY" \
        --arg frozen_commit "$SOURCE_COMMIT" \
        --arg frozen_tree "$SOURCE_TREE_DIGEST" \
        --arg frozen_working_tree "$SOURCE_WORKTREE_DIGEST" \
        --argjson frozen_dirty "$SOURCE_DIRTY" \
        --arg benchmark_sha "$BENCH_SHA256" \
        --arg observed_working_tree "$(jq -r '.Subject.WorkingTreeDigest' "$receipt")" \
        --argjson observed_dirty "$(jq -r '.Subject.Dirty' "$receipt")" \
        --arg observed_project "$(jq -r '.Subject.Project' "$receipt")" \
        --arg observed_repository "$(jq -r '.Subject.Repository' "$receipt")" \
        --arg observed_commit "$(jq -r '.Subject.Commit' "$receipt")" \
        --arg observed_tree "$(jq -r '.Subject.TreeDigest' "$receipt")" \
        '{
          schema: $schema,
          status: (if $observed_working_tree == $frozen_working_tree then "matched" else "live-checkout-drift" end),
          immutableClosure: {
            manifestPath: $frozen_manifest,
            manifestSha256: $frozen_manifest_sha,
            binaryClosure: $frozen_closure,
            benchmarkAssemblySha256: $benchmark_sha
          },
          frozenSource: {
            project: $frozen_project,
            repository: $frozen_repository,
            commit: $frozen_commit,
            treeDigest: $frozen_tree,
            workingTreeDigest: $frozen_working_tree,
            dirty: $frozen_dirty
          },
          observedReceiptSubject: {
            project: $observed_project,
            repository: $observed_repository,
            commit: $observed_commit,
            treeDigest: $observed_tree,
            workingTreeDigest: $observed_working_tree,
            dirty: $observed_dirty
          },
          workingTreeDigestMatches: ($observed_working_tree == $frozen_working_tree)
        }' > "$RUN_DIR/provenance-verification.json"

    sha256sum "$BENCH_DLL" "$receipt" "$RUN_DIR/tiered-rustfs-summary.md" \
        "$cgroup_verification" "$durability_verification" \
        "$staging_verification" \
        "$RUN_DIR/provenance-verification.json" > "$RUN_DIR/verified-sha256sums.txt"
    jq '{status:.Status,phase:.Phase,target:.TargetRecords,completed:.CompletedRecords,remoteAck:.RemoteAck,remoteObjects:.RemoteObjects,remoteJournal:.RemoteJournalObjects,seedRootLossSeconds:.SeedRootLossRecoverySeconds,mutationRootLossSeconds:.MutationRootLossRecoverySeconds,peakLocal:.PeakLocalStorageBytes,peakCache:.PeakCacheBytes,peakStaging:.PeakStagingBytes,finalCgroup:.FinalCgroup}' "$receipt"
    printf 'verification=ok\nrun_dir=%s\nunit=%s\nprefix=%s\n' "$OUTPUT" "$UNIT" "$PREFIX"
}

parse_args() {
    if (($# > 0)); then
        case "$1" in
            preflight|launch|verify)
                MODE="$1"
                shift
                ;;
        esac
    fi
    while (($# > 0)); do
        case "$1" in
            --manifest) (($# >= 2)) || die "--manifest requires a path"; MANIFEST="$2"; shift 2 ;;
            --run-dir) (($# >= 2)) || die "--run-dir requires a path"; RUN_DIR="$2"; shift 2 ;;
            --run-label) (($# >= 2)) || die "--run-label requires a label"; RUN_LABEL="$2"; shift 2 ;;
            --output-root) (($# >= 2)) || die "--output-root requires a path"; OUTPUT_ROOT="$2"; shift 2 ;;
            --dotnet) (($# >= 2)) || die "--dotnet requires a path"; DOTNET_BIN="$2"; shift 2 ;;
            --credentials-file) (($# >= 2)) || die "--credentials-file requires a path"; CREDENTIALS_FILE="$2"; shift 2 ;;
            --rustfs-container) (($# >= 2)) || die "--rustfs-container requires a name"; RUSTFS_CONTAINER="$2"; shift 2 ;;
            --endpoint) (($# >= 2)) || die "--endpoint requires a URL"; RUSTFS_ENDPOINT="$2"; shift 2 ;;
            --bucket) (($# >= 2)) || die "--bucket requires a name"; RUSTFS_BUCKET="$2"; shift 2 ;;
            --region) (($# >= 2)) || die "--region requires a name"; RUSTFS_REGION="$2"; shift 2 ;;
            --runtime-max) (($# >= 2)) || die "--runtime-max requires a span"; RUNTIME_MAX="$2"; shift 2 ;;
            --help|-h) usage; exit 0 ;;
            *) die "unknown argument: $1 (use --help)" ;;
        esac
    done
    [[ "$RUNTIME_MAX" =~ ^[1-9][0-9]*(ms|us|s|min|h|d)?$ ]] ||
        die "runtime max must be a finite positive systemd span"
}

main() {
    parse_args "$@"
    case "$MODE" in
        preflight) preflight ;;
        launch) launch ;;
        verify) verify ;;
        *) die "unsupported mode: $MODE" ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
