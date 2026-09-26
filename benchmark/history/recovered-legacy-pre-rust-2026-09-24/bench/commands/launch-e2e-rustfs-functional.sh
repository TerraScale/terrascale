#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

# Source-matched RustFS functional E2E lane.
#
# The default mode is preflight and never starts a workload.  Launch is an
# explicit operation; it creates a fresh output root and sibling launch root,
# runs E2eVolumeWorkload in an isolated user-systemd cgroup, and leaves the
# copied freeze manifest beside the run.  Verify requires the compact and
# detailed receipts, the complete E2E phase evidence, strict remote source
# acknowledgement, and the resource/cgroup proof emitted by the workload.
#
# Examples:
#   ./launch-e2e-rustfs-functional.sh preflight --manifest F/freeze-manifest.txt --documents 100K
#   ./launch-e2e-rustfs-functional.sh launch --manifest F/freeze-manifest.txt --documents 1M --detach
#   ./launch-e2e-rustfs-functional.sh verify --run-dir bench/receipts/e2e-functional-rustfs-1m-...

readonly SCRIPT_PATH="$(readlink -f "$BASH_SOURCE")"
readonly SCRIPT_DIR="$(dirname "$SCRIPT_PATH")"
readonly BENCH_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly RECEIPTS_ROOT="$BENCH_ROOT/bench/receipts"
readonly DEFAULT_DOTNET_BIN="/home/mariogk/.local/share/mise/dotnet-root/dotnet"
readonly DEFAULT_CREDENTIALS_FILE="/run/user/1000/terrabase-rustfs-credentials.sh"
readonly DEFAULT_RUSTFS_CONTAINER="terrabase-rustfs-20260922"
readonly DEFAULT_RUSTFS_ENDPOINT="http://127.0.0.1:19000"
readonly DEFAULT_RUSTFS_DATA_PATH="/home/mariogk/.local/share/containers/storage/volumes/f4cd87ff0e1f288eeafc4a03a1f73feed02c53806d7406f24d124759daa769d7/_data"
readonly MEMORY_MAX_BYTES=4294967296
readonly MEMORY_SWAP_MAX_BYTES=0
readonly MIN_FREE_BYTES=107374182400
readonly LOCAL_STORAGE_CAP_BYTES=68719476736
readonly BUCKET_PREFIX="terrabase-e2e-"

MODE=preflight
MANIFEST=""
BINARY_ROOT=""
RUN_DIR=""
TEMP_ROOT=""
DOCUMENTS_INPUT=""
DOCUMENTS=""
DOCUMENTS_LABEL=""
RUN_LABEL=""
DETACH=0
RUNTIME_MAX="${RUNTIME_MAX:-12h}"
DOTNET_BIN="${TERRABASE_DOTNET_BIN:-$DEFAULT_DOTNET_BIN}"
CREDENTIALS_FILE="${TERRABASE_RUSTFS_CREDENTIAL_FILE:-$DEFAULT_CREDENTIALS_FILE}"
RUSTFS_CONTAINER="${TERRABASE_RUSTFS_CONTAINER:-$DEFAULT_RUSTFS_CONTAINER}"
S3_ENDPOINT="${TERRABASE_S3_ENDPOINT:-$DEFAULT_RUSTFS_ENDPOINT}"
S3_REGION="${TERRABASE_S3_REGION:-us-east-1}"
RUSTFS_DATA_PATH="${RUSTFS_DATA_PATH:-$DEFAULT_RUSTFS_DATA_PATH}"

BATCH_SIZE=8192
POINT_READS=10000
UPDATES=1000
DELETES=1000
QUERIES=1000
RELATIONSHIPS=""
SEED=1337
SNAPSHOT_BATCH=1024
CHECKPOINT_RECORDS=""
RESOURCE_SAMPLE_MS=5000
CACHE_BYTES=16777216

SOURCE_REPOSITORY=""
SOURCE_COMMIT=""
SOURCE_TREE_DIGEST=""
SOURCE_WORKTREE_DIGEST=""
SOURCE_DIRTY=""
SOURCE_DOTNET_VERSION=""
BENCH_DLL=""
BENCH_SHA256=""
SOURCE_MANIFEST_SHA256=""
UNIT=""
LAUNCH_ROOT=""
DETAIL_RECEIPT=""
COMPACT_RECEIPT=""

die() {
    printf 'launch-e2e-rustfs-functional: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Usage:
  launch-e2e-rustfs-functional.sh preflight --manifest PATH --documents 100K|1M [options]
  launch-e2e-rustfs-functional.sh launch    --manifest PATH --documents 100K|1M [options]
  launch-e2e-rustfs-functional.sh verify    --run-dir PATH [--manifest PATH] [options]

The default mode is preflight. It never creates a run or starts systemd.

Options:
  --manifest PATH             Source-matched freeze-manifest.txt (required for launch/preflight)
  --binary-root PATH          Resolve the manifest from this frozen closure
  --documents 100K|1M         Exact functional corpus size (required for launch/preflight)
  --run-dir PATH              Existing run directory for verify
  --output-root PATH          Fresh receipt root under ts-benchmark/bench/receipts
  --temp-root PATH            Fresh sibling temporary root (default: <output>-tmp)
  --run-label LABEL           Label used in the generated unit name
  --dotnet PATH               .NET runtime (default: frozen-machine runtime)
  --credentials-file PATH     Private RustFS credential script
  --rustfs-container NAME     Local RustFS container name
  --endpoint URL              RustFS endpoint
  --region NAME               S3 signing region
  --rustfs-data-path PATH     RustFS data root included in resource accounting
  --runtime-max SPAN          systemd RuntimeMaxSec (default: 12h)
  --batch-size N              E2E committed write batch size (default: 8192)
  --point-reads N             Verified source point reads (default: 10000)
  --updates N                 Documents updated before incremental backup (default: 1000)
  --deletes N                 Documents deleted before incremental backup (default: 1000)
  --queries N                 Verified indexed queries on the restored engine (default: 1000)
  --relationships N           Related users/orders (default: 1000 for 100K, 10000 for 1M)
  --snapshot-batch N          Automatic index snapshot batch size (default: 1024)
  --checkpoint-records N      Resource checkpoint interval (default: corpus size)
  --resource-sample-ms N      Resource sample interval (default: 5000)
  --cache-bytes N             SSD segment cache budget (default: 16777216)
  --detach                    Start and return without waiting for the unit
  --help                      Show this help

Verification requires a complete run-receipt.json and e2e-volume-receipt.json,
all E2eVolumeWorkload phases, a verified root-loss checkpoint, strict remote
acknowledgement durability, and a valid no-swap cgroup receipt.
EOF
}

require_commands() {
    local command_name
    for command_name in awk cmp curl date df dirname du find git jq nice pgrep readlink realpath rg sha256sum sort stat systemctl systemd-run tar podman; do
        command -v "$command_name" >/dev/null 2>&1 || die "required command is missing: $command_name"
    done
}

manifest_value() {
    local key=$1
    awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); found = 1; exit } END { if (!found) exit 1 }' "$MANIFEST"
}

required_manifest_value() {
    local key=$1
    local value
    value="$(manifest_value "$key" 2>/dev/null || true)"
    [[ -n "$value" ]] || die "freeze manifest is missing required key: $key"
    printf '%s\n' "$value"
}

launch_value() {
    local key=$1
    awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); found = 1; exit } END { if (!found) exit 1 }' "$LAUNCH_ROOT/launch-manifest.txt"
}

required_launch_value() {
    local key=$1
    local value
    value="$(launch_value "$key" 2>/dev/null || true)"
    [[ -n "$value" ]] || die "launch manifest is missing required key: $key"
    printf '%s\n' "$value"
}

normalize_documents() {
    local normalized
    normalized="$(tr '[:lower:]' '[:upper:]' <<< "$DOCUMENTS_INPUT")"
    case "$normalized" in
        100K|100000)
            DOCUMENTS=100000
            DOCUMENTS_LABEL=100k
            RELATIONSHIPS="${RELATIONSHIPS:-1000}"
            CHECKPOINT_RECORDS="${CHECKPOINT_RECORDS:-100000}"
            ;;
        1M|1000000)
            DOCUMENTS=1000000
            DOCUMENTS_LABEL=1m
            RELATIONSHIPS="${RELATIONSHIPS:-10000}"
            CHECKPOINT_RECORDS="${CHECKPOINT_RECORDS:-500000}"
            ;;
        *)
            die "--documents must be exactly 100K or 1M"
            ;;
    esac
}

parse_positive_integer() {
    local name=$1 value=$2
    [[ "$value" =~ ^[1-9][0-9]*$ ]] || die "--$name must be a positive integer"
}

parse_nonnegative_integer() {
    local name=$1 value=$2
    [[ "$value" =~ ^[0-9]+$ ]] || die "--$name must be a non-negative integer"
}

parse_options() {
    while (($# > 0)); do
        case "$1" in
            preflight|launch|verify)
                MODE=$1
                shift
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            --manifest)
                (($# >= 2)) || die "--manifest requires a path"
                MANIFEST=$2
                shift 2
                ;;
            --manifest=*) MANIFEST=${1#*=}; shift ;;
            --binary-root)
                (($# >= 2)) || die "--binary-root requires a path"
                BINARY_ROOT=$2
                shift 2
                ;;
            --binary-root=*) BINARY_ROOT=${1#*=}; shift ;;
            --documents)
                (($# >= 2)) || die "--documents requires 100K or 1M"
                DOCUMENTS_INPUT=$2
                shift 2
                ;;
            --documents=*) DOCUMENTS_INPUT=${1#*=}; shift ;;
            --run-dir)
                (($# >= 2)) || die "--run-dir requires a path"
                RUN_DIR=$2
                shift 2
                ;;
            --run-dir=*) RUN_DIR=${1#*=}; shift ;;
            --output-root)
                (($# >= 2)) || die "--output-root requires a path"
                RUN_DIR=$2
                shift 2
                ;;
            --output-root=*) RUN_DIR=${1#*=}; shift ;;
            --temp-root)
                (($# >= 2)) || die "--temp-root requires a path"
                TEMP_ROOT=$2
                shift 2
                ;;
            --temp-root=*) TEMP_ROOT=${1#*=}; shift ;;
            --run-label)
                (($# >= 2)) || die "--run-label requires a label"
                RUN_LABEL=$2
                shift 2
                ;;
            --run-label=*) RUN_LABEL=${1#*=}; shift ;;
            --dotnet)
                (($# >= 2)) || die "--dotnet requires a path"
                DOTNET_BIN=$2
                shift 2
                ;;
            --dotnet=*) DOTNET_BIN=${1#*=}; shift ;;
            --credentials-file)
                (($# >= 2)) || die "--credentials-file requires a path"
                CREDENTIALS_FILE=$2
                shift 2
                ;;
            --credentials-file=*) CREDENTIALS_FILE=${1#*=}; shift ;;
            --rustfs-container)
                (($# >= 2)) || die "--rustfs-container requires a name"
                RUSTFS_CONTAINER=$2
                shift 2
                ;;
            --rustfs-container=*) RUSTFS_CONTAINER=${1#*=}; shift ;;
            --endpoint)
                (($# >= 2)) || die "--endpoint requires a URL"
                S3_ENDPOINT=$2
                shift 2
                ;;
            --endpoint=*) S3_ENDPOINT=${1#*=}; shift ;;
            --region)
                (($# >= 2)) || die "--region requires a name"
                S3_REGION=$2
                shift 2
                ;;
            --region=*) S3_REGION=${1#*=}; shift ;;
            --rustfs-data-path)
                (($# >= 2)) || die "--rustfs-data-path requires a path"
                RUSTFS_DATA_PATH=$2
                shift 2
                ;;
            --rustfs-data-path=*) RUSTFS_DATA_PATH=${1#*=}; shift ;;
            --runtime-max)
                (($# >= 2)) || die "--runtime-max requires a span"
                RUNTIME_MAX=$2
                shift 2
                ;;
            --runtime-max=*) RUNTIME_MAX=${1#*=}; shift ;;
            --batch-size)
                (($# >= 2)) || die "--batch-size requires a number"
                BATCH_SIZE=$2
                shift 2
                ;;
            --batch-size=*) BATCH_SIZE=${1#*=}; shift ;;
            --point-reads)
                (($# >= 2)) || die "--point-reads requires a number"
                POINT_READS=$2
                shift 2
                ;;
            --point-reads=*) POINT_READS=${1#*=}; shift ;;
            --updates)
                (($# >= 2)) || die "--updates requires a number"
                UPDATES=$2
                shift 2
                ;;
            --updates=*) UPDATES=${1#*=}; shift ;;
            --deletes)
                (($# >= 2)) || die "--deletes requires a number"
                DELETES=$2
                shift 2
                ;;
            --deletes=*) DELETES=${1#*=}; shift ;;
            --queries)
                (($# >= 2)) || die "--queries requires a number"
                QUERIES=$2
                shift 2
                ;;
            --queries=*) QUERIES=${1#*=}; shift ;;
            --relationships)
                (($# >= 2)) || die "--relationships requires a number"
                RELATIONSHIPS=$2
                shift 2
                ;;
            --relationships=*) RELATIONSHIPS=${1#*=}; shift ;;
            --snapshot-batch)
                (($# >= 2)) || die "--snapshot-batch requires a number"
                SNAPSHOT_BATCH=$2
                shift 2
                ;;
            --snapshot-batch=*) SNAPSHOT_BATCH=${1#*=}; shift ;;
            --checkpoint-records)
                (($# >= 2)) || die "--checkpoint-records requires a number"
                CHECKPOINT_RECORDS=$2
                shift 2
                ;;
            --checkpoint-records=*) CHECKPOINT_RECORDS=${1#*=}; shift ;;
            --resource-sample-ms)
                (($# >= 2)) || die "--resource-sample-ms requires a number"
                RESOURCE_SAMPLE_MS=$2
                shift 2
                ;;
            --resource-sample-ms=*) RESOURCE_SAMPLE_MS=${1#*=}; shift ;;
            --cache-bytes)
                (($# >= 2)) || die "--cache-bytes requires a number"
                CACHE_BYTES=$2
                shift 2
                ;;
            --cache-bytes=*) CACHE_BYTES=${1#*=}; shift ;;
            --detach)
                DETACH=1
                shift
                ;;
            *) die "unknown argument: $1" ;;
        esac
    done

    if [[ "$MODE" != verify ]]; then
        [[ -n "$DOCUMENTS_INPUT" ]] || die "--documents is required for $MODE"
        normalize_documents
    fi
    [[ "$RUNTIME_MAX" =~ ^[1-9][0-9]*(ms|us|s|min|h|d)?$ ]] ||
        die "--runtime-max must be a finite positive systemd span"
    parse_positive_integer batch-size "$BATCH_SIZE"
    parse_nonnegative_integer point-reads "$POINT_READS"
    parse_nonnegative_integer updates "$UPDATES"
    parse_nonnegative_integer deletes "$DELETES"
    parse_nonnegative_integer queries "$QUERIES"
    parse_nonnegative_integer relationships "${RELATIONSHIPS:-0}"
    parse_positive_integer snapshot-batch "$SNAPSHOT_BATCH"
    (( SNAPSHOT_BATCH <= 4096 )) || die "--snapshot-batch must be <= 4096"
    parse_positive_integer checkpoint-records "${CHECKPOINT_RECORDS:-1}"
    parse_positive_integer resource-sample-ms "$RESOURCE_SAMPLE_MS"
    parse_nonnegative_integer cache-bytes "$CACHE_BYTES"
    if [[ -n "$DOCUMENTS" ]]; then
        (( UPDATES + DELETES <= DOCUMENTS )) || die "updates plus deletes exceed documents"
        (( UPDATES + DELETES > 0 )) || die "updates plus deletes must be non-zero for backup/PITR coverage"
    fi
}

resolve_manifest() {
    if [[ -z "$MANIFEST" && -n "$BINARY_ROOT" ]]; then
        MANIFEST="$BINARY_ROOT/freeze-manifest.txt"
    fi
    [[ -n "$MANIFEST" ]] || die "--manifest or --binary-root is required"
    [[ -f "$MANIFEST" && ! -L "$MANIFEST" ]] || die "freeze manifest is missing or a symlink: $MANIFEST"
    MANIFEST="$(readlink -e -- "$MANIFEST")"
}

load_frozen_manifest() {
    resolve_manifest
    local schema closure provenance receipt_project receipt_commit receipt_tree receipt_worktree
    schema="$(required_manifest_value schema)"
    [[ "$schema" == terrabase-final-source-matched-release-v1 ]] || die "unsupported freeze manifest schema: $schema"
    [[ "$(required_manifest_value buildConfiguration)" == Release ]] || die "freeze manifest is not a Release build"
    SOURCE_REPOSITORY="$(required_manifest_value repository)"
    SOURCE_COMMIT="$(required_manifest_value commit)"
    SOURCE_TREE_DIGEST="$(required_manifest_value treeDigest)"
    SOURCE_WORKTREE_DIGEST="$(required_manifest_value workingTreeDigest)"
    SOURCE_DIRTY="$(required_manifest_value sourceDirty)"
    SOURCE_DOTNET_VERSION="$(required_manifest_value dotnetVersion)"
    closure="$(required_manifest_value binaryClosure)"
    [[ -d "$closure" && ! -L "$closure" ]] || die "frozen binary closure is unavailable: $closure"
    [[ -z "$BINARY_ROOT" || "$(readlink -e -- "$BINARY_ROOT")" == "$(readlink -e -- "$closure")" ]] || die "--binary-root does not match manifest binaryClosure"
    BINARY_ROOT="$(readlink -e -- "$closure")"
    BENCH_DLL="$BINARY_ROOT/TerraBase.Benchmarks.dll"
    [[ -f "$BENCH_DLL" && ! -L "$BENCH_DLL" ]] || die "frozen benchmark assembly is missing"
    [[ "$SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]] || die "freeze commit is invalid"
    [[ "$SOURCE_TREE_DIGEST" =~ ^[0-9a-fA-F]{40}$ ]] || die "freeze tree digest is invalid"
    [[ "$SOURCE_WORKTREE_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]] || die "freeze working-tree digest is invalid"
    [[ "$SOURCE_DIRTY" == true || "$SOURCE_DIRTY" == false ]] || die "freeze sourceDirty is invalid"
    for required_file in \
        "$BINARY_ROOT/core-hashes.sha256" \
        "$BINARY_ROOT/closure-sha256.txt" \
        "$BINARY_ROOT/terrabase-source.tar" \
        "$BINARY_ROOT/terrabase-source.tar.sha256" \
        "$BINARY_ROOT/source-provenance/run-receipt.json"; do
        [[ -f "$required_file" && ! -L "$required_file" ]] || die "frozen artifact is missing: $required_file"
    done
    (cd "$BINARY_ROOT" && sha256sum --quiet -c core-hashes.sha256) || die "core assembly hash verification failed"
    (cd "$BINARY_ROOT" && sha256sum --quiet -c closure-sha256.txt) || die "frozen closure hash verification failed"
    (cd "$BINARY_ROOT" && sha256sum --quiet -c terrabase-source.tar.sha256) || die "source archive hash verification failed"
    tar -tf "$BINARY_ROOT/terrabase-source.tar" >/dev/null || die "source archive cannot be listed"
    provenance="$BINARY_ROOT/source-provenance/run-receipt.json"
    receipt_project="$(jq -er '.subject.project' "$provenance")" || die "provenance project is missing"
    receipt_commit="$(jq -er '.subject.commit' "$provenance")" || die "provenance commit is missing"
    receipt_tree="$(jq -er '.subject.treeDigest' "$provenance")" || die "provenance tree digest is missing"
    receipt_worktree="$(jq -er '.subject.workingTreeDigest' "$provenance")" || die "provenance working-tree digest is missing"
    [[ "$receipt_project" == "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" ]] || die "provenance project does not match freeze manifest"
    [[ "$receipt_commit" == "$SOURCE_COMMIT" ]] || die "provenance commit does not match freeze manifest"
    [[ "$receipt_tree" == "$SOURCE_TREE_DIGEST" ]] || die "provenance tree digest does not match freeze manifest"
    [[ "$receipt_worktree" == "$SOURCE_WORKTREE_DIGEST" ]] || die "provenance working-tree digest does not match freeze manifest"
    jq -e '.status == "invalid" and .exitCode == 2' "$provenance" >/dev/null || die "provenance receipt is not an invalid preflight"
    BENCH_SHA256="$(sha256sum "$BENCH_DLL" | awk '{print $1}')"
    SOURCE_MANIFEST_SHA256="$(sha256sum "$MANIFEST" | awk '{print $1}')"
    [[ -x "$DOTNET_BIN" ]] || die ".NET runtime is not executable: $DOTNET_BIN"
    [[ "$($DOTNET_BIN --version)" == "$SOURCE_DOTNET_VERSION" ]] || die ".NET runtime mismatch"
}

reject_symlink_ancestors() {
    local candidate=$1 current
    [[ "$candidate" == /* ]] || die "path must be absolute: $candidate"
    current="$candidate"
    while [[ "$current" != "/" ]]; do
        [[ ! -L "$current" ]] || die "refusing a symlinked path component: $current"
        current="$(dirname -- "$current")"
    done
}

check_host() {
    [[ -r /sys/fs/cgroup/cgroup.controllers ]] || die "cgroup v2 is unavailable"
    systemctl --user show-environment >/dev/null 2>&1 || die "user systemd manager is unavailable"
    [[ -r "$CREDENTIALS_FILE" && ! -L "$CREDENTIALS_FILE" ]] || die "RustFS credentials file is unreadable"
    [[ "$(podman inspect --format '{{.State.Status}}' "$RUSTFS_CONTAINER" 2>/dev/null || true)" == running ]] || die "RustFS container is not running: $RUSTFS_CONTAINER"
    [[ -d "$RUSTFS_DATA_PATH" ]] || die "RustFS data root is unavailable: $RUSTFS_DATA_PATH"
    reject_symlink_ancestors "$(readlink -m -- "$RUSTFS_DATA_PATH")"
    local status free
    status="$(curl --connect-timeout 2 --max-time 5 --silent --show-error -o /dev/null -w '%{http_code}' "$S3_ENDPOINT" || true)"
    case "$status" in 200|400|403|405) ;; *) die "RustFS endpoint returned HTTP $status: $S3_ENDPOINT" ;; esac
    free="$(df -B1 --output=avail "$RECEIPTS_ROOT" | awk 'NR == 2 { print $1 }')"
    [[ "$free" =~ ^[0-9]+$ && "$free" -ge "$MIN_FREE_BYTES" ]] || die "free space is below the 100 GiB floor"
    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
       systemctl --user list-units --type=service --state=running --no-legend --no-pager 2>/dev/null |
           rg -qi 'terrabase.*(strict|e2e|state-map)|state-map.*terrabase'; then
        die "an active TerraBase benchmark service is running"
    fi
    if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] && pgrep -af 'TerraBase\.Benchmarks(\.dll|/)' >/dev/null 2>&1; then
        die "an active TerraBase benchmark process is running"
    fi
}

preflight() {
    require_commands
    load_frozen_manifest
    check_host
    printf 'preflight=ok\nmanifest=%s\nmanifestSha256=%s\nbinary=%s\ndocuments=%s\nrustfsEndpoint=%s\nrustfsContainer=%s\nrustfsDataPath=%s\ncgroupMemoryMaxBytes=%s\ncgroupMemorySwapMaxBytes=%s\n' \
        "$MANIFEST" "$SOURCE_MANIFEST_SHA256" "$BENCH_DLL" "$DOCUMENTS" "$S3_ENDPOINT" "$RUSTFS_CONTAINER" "$RUSTFS_DATA_PATH" "$MEMORY_MAX_BYTES" "$MEMORY_SWAP_MAX_BYTES"
}

prepare_launch_paths() {
    local stamp
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    if [[ -z "$RUN_DIR" ]]; then
        RUN_DIR="$RECEIPTS_ROOT/e2e-functional-rustfs-$DOCUMENTS_LABEL-$stamp"
    fi
    RUN_DIR="$(readlink -m -- "$RUN_DIR")"
    [[ "$RUN_DIR" == "$RECEIPTS_ROOT/"* ]] || die "--output-root must be under $RECEIPTS_ROOT"
    [[ -n "$RUN_LABEL" ]] || RUN_LABEL="e2e-functional-rustfs-$DOCUMENTS_LABEL-$stamp"
    [[ "$RUN_LABEL" =~ ^[[:alnum:]_.-]+$ ]] || die "run label contains unsafe characters"
    [[ -z "$TEMP_ROOT" ]] && TEMP_ROOT="$RUN_DIR-tmp"
    TEMP_ROOT="$(readlink -m -- "$TEMP_ROOT")"
    LAUNCH_ROOT="$RUN_DIR.launch"
    UNIT="terrabase-$RUN_LABEL"
    [[ ! -e "$RUN_DIR" && ! -e "$TEMP_ROOT" && ! -e "$LAUNCH_ROOT" ]] || die "run, temp, or launch root already exists"
    reject_symlink_ancestors "$RUN_DIR"
    reject_symlink_ancestors "$TEMP_ROOT"
    reject_symlink_ancestors "$LAUNCH_ROOT"
    mkdir -m 700 "$LAUNCH_ROOT" "$TEMP_ROOT"
}

write_wrapper() {
    local wrapper="$LAUNCH_ROOT/run-wrapper.sh"
    local bucket_hint="terrabase-e2e-functional-${DOCUMENTS_LABEL}-$(date -u +%Y%m%d%H%M%S)"
    local -a bench_args=(
        --workload e2e-volume
        --object-tier s3
        --documents "$DOCUMENTS"
        --batch-size "$BATCH_SIZE"
        --point-reads "$POINT_READS"
        --updates "$UPDATES"
        --deletes "$DELETES"
        --queries "$QUERIES"
        --snapshot-build-maximum-documents "$SNAPSHOT_BATCH"
        --relationships "$RELATIONSHIPS"
        --seed "$SEED"
        --checkpoint-records "$CHECKPOINT_RECORDS"
        --resource-sample-ms "$RESOURCE_SAMPLE_MS"
        --cache-bytes "$CACHE_BYTES"
        --max-local-storage-bytes "$LOCAL_STORAGE_CAP_BYTES"
        --min-free-space-bytes "$MIN_FREE_BYTES"
        --rustfs-data-path "$RUSTFS_DATA_PATH"
        --require-isolated-cgroup
        --root-loss-recovery
        --temp-root "$TEMP_ROOT"
        --output "$RUN_DIR"
    )
    {
        printf '%s\n' '#!/usr/bin/env bash' 'set -Eeuo pipefail'
        printf 'source %q\n' "$CREDENTIALS_FILE"
        printf 'export TERRABASE_S3_ENDPOINT=%q TERRABASE_S3_REGION=%q TERRABASE_S3_BUCKET=%q\n' "$S3_ENDPOINT" "$S3_REGION" "$bucket_hint"
        printf 'export TERRABASE_FROZEN_SOURCE_PROJECT=%q TERRABASE_FROZEN_SOURCE_REPOSITORY=%q\n' "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" "$SOURCE_REPOSITORY"
        printf 'export TERRABASE_FROZEN_SOURCE_COMMIT=%q TERRABASE_FROZEN_SOURCE_TREE_DIGEST=%q\n' "$SOURCE_COMMIT" "$SOURCE_TREE_DIGEST"
        printf 'export TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST=%q TERRABASE_FROZEN_SOURCE_DIRTY=%q\n' "$SOURCE_WORKTREE_DIGEST" "$SOURCE_DIRTY"
        printf 'export TERRABASE_FROZEN_MANIFEST_PATH=%q TERRABASE_FROZEN_MANIFEST_SHA256=%q\n' "$LAUNCH_ROOT/source-manifest.txt" "$SOURCE_MANIFEST_SHA256"
        printf 'export TERRABASE_FROZEN_BINARY_CLOSURE=%q TERRABASE_FROZEN_BENCHMARK_ASSEMBLY_SHA256=%q\n' "$BINARY_ROOT" "$BENCH_SHA256"
        printf 'export TMPDIR=%q TMP=%q TEMP=%q\n' "$TEMP_ROOT" "$TEMP_ROOT" "$TEMP_ROOT"
        printf 'exec /usr/bin/nice -n 19 %q %q \\\n' "$DOTNET_BIN" "$BENCH_DLL"
        local argument
        for argument in "${bench_args[@]}"; do
            printf '  %q \\\n' "$argument"
        done
        printf '\n'
    } > "$wrapper"
    chmod 700 "$wrapper"
    cp -- "$MANIFEST" "$LAUNCH_ROOT/source-manifest.txt"
    sha256sum "$LAUNCH_ROOT/source-manifest.txt" > "$LAUNCH_ROOT/source-manifest.sha256"
    sha256sum "$BENCH_DLL" > "$LAUNCH_ROOT/benchmark-assembly.sha256"
    local wrapper_sha256
    wrapper_sha256="$(sha256sum "$wrapper" | awk '{print $1}')"
    {
        printf 'schema=terrabase-e2e-rustfs-functional-launch-v1\n'
        printf 'createdAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf 'unit=%s\n' "$UNIT"
        printf 'runtimeMax=%s\n' "$RUNTIME_MAX"
        printf 'memoryMaxBytes=%s\n' "$MEMORY_MAX_BYTES"
        printf 'memorySwapMaxBytes=%s\n' "$MEMORY_SWAP_MAX_BYTES"
        printf 'oomPolicy=stop\n'
        printf 'documents=%s\n' "$DOCUMENTS"
        printf 'documentsLabel=%s\n' "$DOCUMENTS_LABEL"
        printf 'batchSize=%s\n' "$BATCH_SIZE"
        printf 'pointReads=%s\n' "$POINT_READS"
        printf 'updates=%s\n' "$UPDATES"
        printf 'deletes=%s\n' "$DELETES"
        printf 'queries=%s\n' "$QUERIES"
        printf 'relationships=%s\n' "$RELATIONSHIPS"
        printf 'snapshotBatch=%s\n' "$SNAPSHOT_BATCH"
        printf 'checkpointRecords=%s\n' "$CHECKPOINT_RECORDS"
        printf 'resourceSampleMs=%s\n' "$RESOURCE_SAMPLE_MS"
        printf 'cacheBytes=%s\n' "$CACHE_BYTES"
        printf 'localStorageCapBytes=%s\n' "$LOCAL_STORAGE_CAP_BYTES"
        printf 'minimumFreeBytes=%s\n' "$MIN_FREE_BYTES"
        printf 'binaryRoot=%s\n' "$BINARY_ROOT"
        printf 'binary=%s\n' "$BENCH_DLL"
        printf 'binarySha256=%s\n' "$BENCH_SHA256"
        printf 'freezeManifest=%s\n' "$MANIFEST"
        printf 'freezeManifestSha256=%s\n' "$SOURCE_MANIFEST_SHA256"
        printf 'sourceRepository=%s\n' "$SOURCE_REPOSITORY"
        printf 'sourceCommit=%s\n' "$SOURCE_COMMIT"
        printf 'sourceTreeDigest=%s\n' "$SOURCE_TREE_DIGEST"
        printf 'sourceWorkingTreeDigest=%s\n' "$SOURCE_WORKTREE_DIGEST"
        printf 'outputRoot=%s\n' "$RUN_DIR"
        printf 'temporaryRoot=%s\n' "$TEMP_ROOT"
        printf 'launchRoot=%s\n' "$LAUNCH_ROOT"
        printf 'credentialFile=%s\n' "$CREDENTIALS_FILE"
        printf 's3Endpoint=%s\n' "$S3_ENDPOINT"
        printf 's3Region=%s\n' "$S3_REGION"
        printf 'rustfsContainer=%s\n' "$RUSTFS_CONTAINER"
        printf 'rustfsDataPath=%s\n' "$RUSTFS_DATA_PATH"
        printf 'bucketPrefix=%s\n' "$BUCKET_PREFIX"
        printf 'commandWrapper=%s\n' "$wrapper"
        printf 'commandWrapperSha256=%s\n' "$wrapper_sha256"
        printf 'benchmarkArgs='; printf '%q ' "${bench_args[@]}"; printf '\n'
    } > "$LAUNCH_ROOT/launch-manifest.txt"
}

launch() {
    preflight >/dev/null
    prepare_launch_paths
    write_wrapper
    local -a systemd_args=(
        --user
        --unit="$UNIT"
        --property=MemoryMax="$MEMORY_MAX_BYTES"
        --property=MemorySwapMax="$MEMORY_SWAP_MAX_BYTES"
        --property=OOMPolicy=stop
        --property="RuntimeMaxSec=$RUNTIME_MAX"
        --working-directory="$BENCH_ROOT"
    )
    local service_exit=0
    set +e
    if (( DETACH == 1 )); then
        systemd-run "${systemd_args[@]}" --no-block /usr/bin/bash "$LAUNCH_ROOT/run-wrapper.sh" > "$LAUNCH_ROOT/systemd-run.log" 2>&1
        service_exit=$?
    else
        systemd-run "${systemd_args[@]}" --wait /usr/bin/bash "$LAUNCH_ROOT/run-wrapper.sh" > "$LAUNCH_ROOT/systemd-run.log" 2>&1
        service_exit=$?
    fi
    set -e
    printf 'serviceExit=%s\n' "$service_exit" >> "$LAUNCH_ROOT/launch-manifest.txt"
    printf 'launchRoot=%s\noutputRoot=%s\nunit=%s\nserviceExit=%s\n' "$LAUNCH_ROOT" "$RUN_DIR" "$UNIT" "$service_exit"
    if (( DETACH == 0 && service_exit == 0 )); then
        verify_run
    fi
    return "$service_exit"
}

load_run_metadata() {
    [[ -n "$RUN_DIR" ]] || die "--run-dir is required for verify"
    [[ -d "$RUN_DIR" && ! -L "$RUN_DIR" ]] || die "run directory is missing or a symlink: $RUN_DIR"
    RUN_DIR="$(readlink -e -- "$RUN_DIR")"
    LAUNCH_ROOT="$RUN_DIR.launch"
    [[ -f "$LAUNCH_ROOT/launch-manifest.txt" && ! -L "$LAUNCH_ROOT/launch-manifest.txt" ]] || die "launch manifest is missing"
    [[ "$(required_launch_value schema)" == terrabase-e2e-rustfs-functional-launch-v1 ]] || die "unsupported launch manifest schema"
    [[ "$(required_launch_value outputRoot)" == "$RUN_DIR" ]] || die "launch outputRoot does not match --run-dir"
    UNIT="$(required_launch_value unit)"
    DOCUMENTS="$(required_launch_value documents)"
    DOCUMENTS_INPUT="$DOCUMENTS"
    normalize_documents
    BATCH_SIZE="$(required_launch_value batchSize)"
    POINT_READS="$(required_launch_value pointReads)"
    UPDATES="$(required_launch_value updates)"
    DELETES="$(required_launch_value deletes)"
    QUERIES="$(required_launch_value queries)"
    RELATIONSHIPS="$(required_launch_value relationships)"
    SNAPSHOT_BATCH="$(required_launch_value snapshotBatch)"
    CHECKPOINT_RECORDS="$(required_launch_value checkpointRecords)"
    RESOURCE_SAMPLE_MS="$(required_launch_value resourceSampleMs)"
    CACHE_BYTES="$(required_launch_value cacheBytes)"
    RUNTIME_MAX="$(required_launch_value runtimeMax)"
    local launch_manifest_path copied_manifest expected_manifest_sha actual_manifest_sha
    launch_manifest_path="$(required_launch_value freezeManifest)"
    copied_manifest="$LAUNCH_ROOT/source-manifest.txt"
    [[ -f "$copied_manifest" && ! -L "$copied_manifest" ]] || die "copied source manifest is missing"
    expected_manifest_sha="$(required_launch_value freezeManifestSha256)"
    actual_manifest_sha="$(sha256sum "$copied_manifest" | awk '{print $1}')"
    [[ "$actual_manifest_sha" == "$expected_manifest_sha" ]] || die "copied source manifest hash mismatch"
    if [[ -n "$MANIFEST" ]]; then
        [[ -f "$MANIFEST" ]] || die "explicit source manifest is missing: $MANIFEST"
        [[ "$(sha256sum "$MANIFEST" | awk '{print $1}')" == "$expected_manifest_sha" ]] || die "explicit manifest does not match copied source manifest"
    fi
    MANIFEST="$copied_manifest"
    [[ -f "$launch_manifest_path" && ! -L "$launch_manifest_path" ]] || die "launch manifest source path is unavailable"
    [[ "$(sha256sum "$launch_manifest_path" | awk '{print $1}')" == "$expected_manifest_sha" ]] || die "launch manifest source path changed"
}

jq_require_phase() {
    local phase=$1
    jq -e --arg phase "$phase" '.phases[$phase] | type == "string" and length > 0' "$DETAIL_RECEIPT" >/dev/null || die "receipt is missing completed phase: $phase"
}

verify_run() {
    load_run_metadata
    load_frozen_manifest
    [[ -f "$LAUNCH_ROOT/benchmark-assembly.sha256" ]] || die "benchmark hash receipt is missing"
    [[ "$(awk '{print $1}' "$LAUNCH_ROOT/benchmark-assembly.sha256")" == "$BENCH_SHA256" ]] || die "benchmark assembly hash changed"
    [[ -f "$RUN_DIR/e2e-volume-receipt.json" && ! -L "$RUN_DIR/e2e-volume-receipt.json" ]] || die "detailed E2E receipt is missing"
    [[ -f "$RUN_DIR/run-receipt.json" && ! -L "$RUN_DIR/run-receipt.json" ]] || die "compact run receipt is missing"
    [[ -f "$RUN_DIR/root-loss-recovery-checkpoint.json" && ! -L "$RUN_DIR/root-loss-recovery-checkpoint.json" ]] || die "root-loss checkpoint is missing"
    [[ -f "$RUN_DIR/resource-envelope.json" && ! -L "$RUN_DIR/resource-envelope.json" ]] || die "resource envelope is missing"
    [[ -f "$RUN_DIR/rustfs-bucket.txt" && ! -L "$RUN_DIR/rustfs-bucket.txt" ]] || die "RustFS bucket receipt is missing"
    DETAIL_RECEIPT="$RUN_DIR/e2e-volume-receipt.json"
    COMPACT_RECEIPT="$RUN_DIR/run-receipt.json"
    local active_state
    active_state="$(systemctl --user show "$UNIT" -p ActiveState --value 2>/dev/null || true)"
    case "$active_state" in active|activating|deactivating) die "benchmark unit is still active: $UNIT" ;; esac
    jq -e --arg output "$RUN_DIR" --arg project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" --arg repository "$SOURCE_REPOSITORY" \
        --arg frozen_manifest "$LAUNCH_ROOT/source-manifest.txt" --arg frozen_manifest_sha "$SOURCE_MANIFEST_SHA256" \
        --arg frozen_closure "$BINARY_ROOT" --arg frozen_sha "$BENCH_SHA256" \
        --arg commit "$SOURCE_COMMIT" --arg tree "$SOURCE_TREE_DIGEST" --arg worktree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        '.status == "complete" and .workload == "e2e-volume" and .exitCode == 0 and .outputDirectory == $output and
         (.receiptSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
         .subject.project == $project and .subject.commit == $commit and .subject.treeDigest == $tree and
         .subject.workingTreeDigest == $worktree and .subject.dirty == $dirty and
         .subject.frozen.project == $project and .subject.frozen.repository == $repository and
         .subject.frozen.commit == $commit and .subject.frozen.treeDigest == $tree and
         .subject.frozen.workingTreeDigest == $worktree and .subject.frozen.dirty == $dirty and
         .subject.frozen.manifestPath == $frozen_manifest and .subject.frozen.manifestSha256 == $frozen_manifest_sha and
         .subject.frozen.binaryClosure == $frozen_closure and
         .subject.frozen.benchmarkAssemblySha256 == $frozen_sha and
         .subject.live.project == $project' "$COMPACT_RECEIPT" >/dev/null ||
        die "compact receipt is incomplete or source-mismatched"
    jq -e --arg project "$SOURCE_REPOSITORY/src/TerraBase/TerraBase.csproj" --arg repository "$SOURCE_REPOSITORY" \
        --arg frozen_manifest "$LAUNCH_ROOT/source-manifest.txt" --arg frozen_manifest_sha "$SOURCE_MANIFEST_SHA256" \
        --arg frozen_closure "$BINARY_ROOT" --arg frozen_sha "$BENCH_SHA256" \
        --arg commit "$SOURCE_COMMIT" --arg tree "$SOURCE_TREE_DIGEST" --arg worktree "$SOURCE_WORKTREE_DIGEST" \
        --argjson dirty "$SOURCE_DIRTY" \
        '.subject.project == $project and .subject.repository == $repository and .subject.commit == $commit and
         .subject.treeDigest == $tree and .subject.workingTreeDigest == $worktree and .subject.dirty == $dirty and
         .subject.frozen.project == $project and .subject.frozen.repository == $repository and
         .subject.frozen.commit == $commit and .subject.frozen.treeDigest == $tree and
         .subject.frozen.workingTreeDigest == $worktree and .subject.frozen.dirty == $dirty and
         .subject.frozen.manifestPath == $frozen_manifest and .subject.frozen.manifestSha256 == $frozen_manifest_sha and
         .subject.frozen.binaryClosure == $frozen_closure and .subject.frozen.benchmarkAssemblySha256 == $frozen_sha and
         .subject.live.project == $project' "$DETAIL_RECEIPT" >/dev/null ||
        die "detailed E2E receipt is incomplete or source-mismatched"
    jq -e --arg output "$RUN_DIR" --argjson docs "$DOCUMENTS" --argjson updates "$UPDATES" --argjson deletes "$DELETES" \
        --argjson queries "$QUERIES" --argjson relationships "$RELATIONSHIPS" --argjson reads "$POINT_READS" \
        '.status == "complete" and .error == null and .configuration.objectTier == "s3" and
         .configuration.documents == $docs and .configuration.updates == $updates and .configuration.deletes == $deletes and
         .configuration.queries == $queries and .configuration.relationships == $relationships and
         .configuration.pointReads == $reads and .configuration.outputDirectory == $output and
         .volume.documentsWritten == $docs and .volume.documentsVerifiedOriginal == $docs and
         .volume.documentsVerifiedFullRestore == $docs and .volume.documentsVerifiedPitr == $docs and
         .volume.documentsVerifiedFinal == ($docs - $deletes) and .volume.deletedDocumentsVerifiedAbsent == $deletes and
         .volume.documentsVerifiedCompacted == ($docs - $deletes) and .volume.documentsVerifiedColdRemote == ($docs - $deletes) and
         .volume.documentsVerifiedSnapshot == $docs and .volume.collectionsVerified == 3 and
         .volume.queriesVerified == $queries and .volume.relationshipVerifyPasses >= 1 and
         .volume.relationshipEndpointsVerified >= ($relationships * 4) and
         .volume.fullArtifactBytes > 0 and .volume.incrementalArtifactBytes > 0 and
         (.volume.fullArtifactSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
         (.volume.incrementalArtifactSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
         .volume.pointReadLatency.operations == $reads and .volume.queryLatency.operations == $queries and
         .volume.queryDiagnostics.samples == $queries and .volume.remoteSegmentObjects >= 1 and
         .volume.coldRemoteCollectionsVerified == 3 and .volume.compactionRecordsWritten > 0 and
         .volume.compactionTombstonesRetained == $deletes' "$DETAIL_RECEIPT" >/dev/null ||
        die "detailed receipt does not prove the requested functional corpus"
    local phase
    for phase in write relationshipWrite relationshipVerify verifyReadback pointReads fullBackup updates deletes incrementalBackup \
        fullRestore fullRestoreVerify pitrRestore pitrRestoreVerify finalRestore finalRestoreVerify snapshotPaging collectionListing \
        coldRemoteReopen indexBuild queries compaction compactionVerify; do
        jq_require_phase "$phase"
    done
    jq -e --argjson docs "$DOCUMENTS" --argjson deletes "$DELETES" \
        '.rootLossRecovery != null and .rootLossRecovery.sourceRootDeleted == true and
         .rootLossRecovery.documentsVerified == ($docs - $deletes) and
         .rootLossRecovery.deletedDocumentsVerifiedAbsent == $deletes and
         .rootLossRecovery.collectionsVerified == 3 and .rootLossRecovery.remoteSegmentObjects >= 1 and
         .rootLossRecovery.artifactsVerified >= 2 and .rootLossRecovery.artifactBytesVerified > 0 and
         .rootLossRecovery.journalSourceRecoveryVerified == true and
         .rootLossRecovery.journalSourceDocumentsVerified == ($docs - $deletes) and
         .rootLossRecovery.journalSourceFrontier > 0 and
         (.rootLossRecovery.checkpointPath | type == "string" and length > 0)' "$DETAIL_RECEIPT" >/dev/null ||
        die "root-loss recovery proof is incomplete"
    jq -e '.restoreTargetCleanup != null and .restoreTargetCleanup.finalTargetDeleted == true and
           .restoreTargetCleanup.remainingPhysicalBytes == 0 and
           (.restoreTargetCleanup.remainingEntries | length) == 0' "$DETAIL_RECEIPT" >/dev/null ||
        die "restore-target cleanup proof is incomplete"
    jq -e '.status == "complete" and .phase == "verified" and .sourceRootDeleted == true and .failure == null' \
        "$RUN_DIR/root-loss-recovery-checkpoint.json" >/dev/null || die "root-loss checkpoint is not verified"
    jq -e --argjson memory "$MEMORY_MAX_BYTES" --argjson swap "$MEMORY_SWAP_MAX_BYTES" \
        --argjson cap "$LOCAL_STORAGE_CAP_BYTES" --argjson minFree "$MIN_FREE_BYTES" \
        '.resources != null and .resources.isolatedCgroup == true and .resources.finalCgroupValid == true and
         .resources.initialCgroup.memoryMax == ($memory | tostring) and .resources.finalCgroup.memoryMax == ($memory | tostring) and
         .resources.initialCgroup.swapMax == ($swap | tostring) and .resources.finalCgroup.swapMax == ($swap | tostring) and
         .resources.swapCurrentBytes == 0 and .resources.oomEvents == .resources.initialOomEvents and
         .resources.oomKillEvents == .resources.initialOomKillEvents and .resources.samplerError == null and
         .resources.limitViolation == null and .resources.peakLocalStorageBytes <= $cap and
         .resources.minimumObservedFreeBytes >= $minFree and .resources.remote.objectCount >= 1 and
         .resources.remote.totalBytes > 0' "$DETAIL_RECEIPT" >/dev/null || die "resource or cgroup receipt failed"
    local bucket
    bucket="$(tr -d '[:space:]' < "$RUN_DIR/rustfs-bucket.txt")"
    [[ "$bucket" =~ ^terrabase-e2e-[[:alnum:]-]+$ ]] || die "RustFS bucket is not an isolated generated bucket: $bucket"
    jq -e '.sourceAckDurability != null and .sourceAckDurability.strictSourceAckDurability == true and
           .sourceAckDurability.remoteJournalFrontier > 0 and .sourceAckDurability.remoteJournalFrameObjects > 0 and
           (.sourceAckDurability.remoteJournalBytes // 0) > 0' "$DETAIL_RECEIPT" >/dev/null ||
        die "strict remote source acknowledgement proof is missing"
    printf 'verify=ok\nrunDir=%s\ndocuments=%s\nsourceCommit=%s\nsourceWorkingTreeDigest=%s\nbucket=%s\n' \
        "$RUN_DIR" "$DOCUMENTS" "$SOURCE_COMMIT" "$SOURCE_WORKTREE_DIGEST" "$bucket"
}

parse_options "$@"
if [[ "$MODE" == verify ]]; then
    require_commands
    verify_run
elif [[ "$MODE" == launch ]]; then
    launch
else
    preflight
fi
