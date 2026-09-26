#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

# Freeze one immutable Release dependency closure for all subsequent TerraBase
# volume benchmarks. This script writes only under ts-benchmark and /tmp; it
# never edits the TerraBase checkout and it never launches a workload.
#
# Run after the strict 1M run and source test gates have finished:
#
#   SOURCE_ROOT=/home/mariogk/Projects/TerraScale/terrabase \
#   BENCH_ROOT=/home/mariogk/Projects/TerraScale/ts-benchmark \
#   ./freeze-final-source-matched-release.sh

SOURCE_ROOT=${SOURCE_ROOT:-/home/mariogk/Projects/TerraScale/terrabase}
BENCH_ROOT=${BENCH_ROOT:-/home/mariogk/Projects/TerraScale/ts-benchmark}
STAMP=$(date -u +%Y%m%dT%H%M%SZ)

die() {
    printf 'freeze-final-source-matched-release: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Usage:
  freeze-final-source-matched-release.sh

Environment:
  SOURCE_ROOT   TerraBase checkout (default: /home/mariogk/Projects/TerraScale/terrabase)
  BENCH_ROOT    ts-benchmark checkout (default: /home/mariogk/Projects/TerraScale/ts-benchmark)
  FINAL_ROOT    Fresh frozen Release closure destination under ts-benchmark/bench/binaries

The command builds and freezes a source-matched Release closure. It does not
launch a benchmark workload.
EOF
}

case "${1:-}" in
    '')
        ;;
    --help|-h)
        usage
        exit 0
        ;;
    *)
        die "unknown argument: $1 (use --help)"
        ;;
esac

[[ -d "$SOURCE_ROOT/.git" ]] || die "SOURCE_ROOT is not a git checkout: $SOURCE_ROOT"
[[ -d "$BENCH_ROOT" ]] || die "BENCH_ROOT does not exist: $BENCH_ROOT"
BENCH_ROOT=$(git -C "$BENCH_ROOT" rev-parse --show-toplevel 2>/dev/null) ||
    die "BENCH_ROOT is not a git checkout: $BENCH_ROOT"
PROJECT="$BENCH_ROOT/terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj"
FINAL_ROOT=${FINAL_ROOT:-"$BENCH_ROOT/bench/binaries/terrabase-final-source-matched-release-$STAMP"}
[[ -f "$PROJECT" ]] || die "benchmark project is missing: $PROJECT"
[[ ! -e "$FINAL_ROOT" ]] || die "refusing to overwrite existing final directory: $FINAL_ROOT"
for required_command in git dotnet jq rg pgrep sha256sum sort xargs systemctl; do
    command -v "$required_command" >/dev/null 2>&1 || die "required command is missing: $required_command"
done

# AGENTS.md is intentionally never edited. If a human has changed it, that
# content remains part of the captured working-tree identity.

# A concurrent run invalidates the quiet-window performance and disk evidence.
# The parent may override the check explicitly for a diagnostic freeze, but a
# qualification freeze must use the default quiet window.
if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] &&
   systemctl --user list-units --type=service --state=running --no-legend 2>/dev/null |
       rg -qi 'terrabase.*(strict|e2e|state-map)|state-map.*terrabase'; then
    die "an active TerraBase strict/E2E/state-map service is running; wait for the quiet window"
fi
if [[ "${ALLOW_ACTIVE_RUNS:-0}" != 1 ]] && pgrep -af 'TerraBase\.Benchmarks(\.dll|/)' >/dev/null 2>&1; then
    die "an active TerraBase benchmark process is running; wait for the quiet window"
fi

BUILD_ROOT=$(mktemp -d "/tmp/terrabase-final-source-matched-build.XXXXXX")
SNAPSHOT_ROOT=$(mktemp -d "/tmp/terrabase-final-source-matched-source.XXXXXX")
cleanup() {
    rm -rf -- "$BUILD_ROOT" "$SNAPSHOT_ROOT"
}
trap cleanup EXIT

# This aggregate is a change detector. The exact working-tree digest is
# captured by the benchmark's RunReceipt after the final binary is copied;
# the pre/post aggregate prevents source edits during the build and freeze.
snapshot_source() {
    local prefix=$1
    git -C "$SOURCE_ROOT" status --porcelain=v1 --untracked-files=all > "$prefix.status"
    git -C "$SOURCE_ROOT" diff --binary HEAD > "$prefix.diff"
    git -C "$SOURCE_ROOT" ls-files --others --exclude-standard -z > "$prefix.untracked"
    # Tracked files plus untracked, nonignored files are the reproducible
    # source set. Ignored bin/obj/build output is excluded by --exclude-standard.
    git -C "$SOURCE_ROOT" ls-files -co --exclude-standard -z > "$prefix.source-paths"
    : > "$prefix.untracked.sha256"
    while IFS= read -r -d '' relative_path; do
        sha256sum -- "$SOURCE_ROOT/$relative_path" >> "$prefix.untracked.sha256"
    done < "$prefix.untracked"
    : > "$prefix.source.sha256"
    while IFS= read -r -d '' relative_path; do
        case "$relative_path" in
            /*|run/*) die "refusing to archive unsafe source path: $relative_path" ;;
        esac
        if git -C "$SOURCE_ROOT" check-ignore --no-index -q -- "$relative_path"; then
            die "refusing ignored source path: $relative_path"
        fi
        sha256sum -- "$SOURCE_ROOT/$relative_path" >> "$prefix.source.sha256"
    done < "$prefix.source-paths"
    # Hash the component digests, not sha256sum's filename columns: pre-build
    # and post-build snapshots live under different temporary path prefixes.
    sha256sum "$prefix.status" "$prefix.diff" "$prefix.untracked.sha256" \
        "$prefix.source-paths" "$prefix.source.sha256" |
        awk '{print $1}' | sha256sum | awk '{print $1}' > "$prefix.aggregate"
}

# Capture only the ts-benchmark files that participate in the benchmark
# project build. The repository also contains benchmark receipts and workload
# output under bench/; including those would make the freeze depend on
# unrelated run artifacts and would not describe the source used by this
# binary. The project directory and its two MSBuild imports are the complete
# benchmark-side input set for TerraBase.Benchmarks.csproj.
# TerraBase's project and package/toolchain files are captured by the existing
# terrabase-source archive. The optional sibling ts-dotnet project is an
# external repository and remains outside this ts-benchmark source archive.
snapshot_benchmark_source() {
    local prefix=$1
    git -C "$BENCH_ROOT" status --porcelain=v1 --untracked-files=all -- \
        terrabase/TerraBase.Benchmarks \
        terrabase/Directory.Build.props \
        terrabase/Directory.Build.targets > "$prefix.status"
    git -C "$BENCH_ROOT" diff --binary HEAD -- \
        terrabase/TerraBase.Benchmarks \
        terrabase/Directory.Build.props \
        terrabase/Directory.Build.targets > "$prefix.diff"
    git -C "$BENCH_ROOT" rev-parse HEAD > "$prefix.commit"
    git -C "$BENCH_ROOT" rev-parse 'HEAD^{tree}' > "$prefix.tree"
    git -C "$BENCH_ROOT" ls-files --others --exclude-standard -z -- \
        terrabase/TerraBase.Benchmarks \
        terrabase/Directory.Build.props \
        terrabase/Directory.Build.targets > "$prefix.untracked"
    : > "$prefix.source-paths"
    git -C "$BENCH_ROOT" ls-files -co --exclude-standard -z -- \
        terrabase/TerraBase.Benchmarks \
        terrabase/Directory.Build.props \
        terrabase/Directory.Build.targets > "$prefix.source-paths"
    : > "$prefix.untracked.sha256"
    while IFS= read -r -d '' relative_path; do
        sha256sum -- "$BENCH_ROOT/$relative_path" >> "$prefix.untracked.sha256"
    done < "$prefix.untracked"
    : > "$prefix.source.sha256"
    while IFS= read -r -d '' relative_path; do
        case "$relative_path" in
            /*|../*|*/../*|run/*) die "refusing to archive unsafe benchmark source path: $relative_path" ;;
        esac
        if git -C "$BENCH_ROOT" check-ignore --no-index -q -- "$relative_path"; then
            die "refusing ignored benchmark source path: $relative_path"
        fi
        sha256sum -- "$BENCH_ROOT/$relative_path" >> "$prefix.source.sha256"
    done < "$prefix.source-paths"
    if [[ -s "$prefix.status" ]]; then
        printf 'true\n' > "$prefix.dirty"
    else
        printf 'false\n' > "$prefix.dirty"
    fi
    # Hash the component digests, not sha256sum's filename columns: pre-build
    # and post-build snapshots live under different temporary path prefixes.
    sha256sum "$prefix.status" "$prefix.diff" "$prefix.commit" "$prefix.tree" \
        "$prefix.untracked.sha256" "$prefix.source-paths" "$prefix.source.sha256" |
        awk '{print $1}' | sha256sum | awk '{print $1}' > "$prefix.aggregate"
}

archive_source() {
    local path_list=$1
    local archive=$2
    tar --create \
        --file="$archive" \
        --directory="$SOURCE_ROOT" \
        --null \
        --verbatim-files-from \
        --format=posix \
        --files-from="$path_list"
}

snapshot_source "$SNAPSHOT_ROOT/pre-build"
snapshot_benchmark_source "$SNAPSHOT_ROOT/benchmark-pre-build"

nice -n 19 dotnet restore "$PROJECT" \
    --artifacts-path "$BUILD_ROOT" \
    --disable-parallel

nice -n 19 dotnet build "$PROJECT" \
    -c Release \
    --no-restore \
    --artifacts-path "$BUILD_ROOT" \
    -p:UseSharedCompilation=false \
    -p:BuildInParallel=false

snapshot_source "$SNAPSHOT_ROOT/post-build"
snapshot_benchmark_source "$SNAPSHOT_ROOT/benchmark-post-build"
cmp -s "$SNAPSHOT_ROOT/pre-build.aggregate" "$SNAPSHOT_ROOT/post-build.aggregate" ||
    die "the TerraBase working tree changed during the build"
cmp -s "$SNAPSHOT_ROOT/benchmark-pre-build.aggregate" "$SNAPSHOT_ROOT/benchmark-post-build.aggregate" ||
    die "the ts-benchmark source changed during the build"

BUILD_OUTPUT="$BUILD_ROOT/bin/TerraBase.Benchmarks/release"
for required in \
    TerraBase.Benchmarks.dll \
    TerraBase.dll \
    TerraBase.ZoneTree.dll \
    TerraBase.Benchmarks.runtimeconfig.json \
    TerraBase.Benchmarks.deps.json; do
    [[ -f "$BUILD_OUTPUT/$required" ]] || die "build output is missing $required"
done

mkdir -p "$(dirname "$FINAL_ROOT")"
mkdir "$FINAL_ROOT"
cp -a "$BUILD_OUTPUT/." "$FINAL_ROOT/"

# Archive the exact current source set, including dirty tracked files and
# untracked nonignored files. The path list is generated by git, so ignored
# bin/obj/build output and anything outside this checkout cannot enter it.
SOURCE_ARCHIVE="$FINAL_ROOT/terrabase-source.tar"
archive_source "$SNAPSHOT_ROOT/post-build.source-paths" "$SOURCE_ARCHIVE"
cp "$SNAPSHOT_ROOT/post-build.source-paths" "$FINAL_ROOT/terrabase-source-files.null"
tr '\0' '\n' < "$SNAPSHOT_ROOT/post-build.source-paths" > "$FINAL_ROOT/terrabase-source-files.txt"
cp "$SNAPSHOT_ROOT/post-build.source.sha256" "$FINAL_ROOT/terrabase-source-files.sha256"
SOURCE_FILE_COUNT=$(tr -cd '\0' < "$SNAPSHOT_ROOT/post-build.source-paths" | wc -c)
SOURCE_FILE_BYTES=$(
    cd "$SOURCE_ROOT"
    du -cb --files0-from="$SNAPSHOT_ROOT/post-build.source-paths" | tail -n 1 | awk '{print $1}'
)
printf '%s  %s\n' \
    "$(sha256sum "$SOURCE_ARCHIVE" | awk '{print $1}')" \
    "$(basename "$SOURCE_ARCHIVE")" \
    > "$FINAL_ROOT/terrabase-source.tar.sha256"

BENCHMARK_SOURCE_ARCHIVE="$FINAL_ROOT/ts-benchmark-source.tar"
tar --create \
    --file="$BENCHMARK_SOURCE_ARCHIVE" \
    --directory="$BENCH_ROOT" \
    --null \
    --verbatim-files-from \
    --format=posix \
    --files-from="$SNAPSHOT_ROOT/benchmark-post-build.source-paths"
cp "$SNAPSHOT_ROOT/benchmark-post-build.source-paths" "$FINAL_ROOT/ts-benchmark-source-files.null"
tr '\0' '\n' < "$SNAPSHOT_ROOT/benchmark-post-build.source-paths" > "$FINAL_ROOT/ts-benchmark-source-files.txt"
cp "$SNAPSHOT_ROOT/benchmark-post-build.source.sha256" "$FINAL_ROOT/ts-benchmark-source-files.sha256"
BENCHMARK_SOURCE_FILE_COUNT=$(tr -cd '\0' < "$SNAPSHOT_ROOT/benchmark-post-build.source-paths" | wc -c)
BENCHMARK_SOURCE_FILE_BYTES=$(
    cd "$BENCH_ROOT"
    du -cb --files0-from="$SNAPSHOT_ROOT/benchmark-post-build.source-paths" | tail -n 1 | awk '{print $1}'
)
printf '%s  %s\n' \
    "$(sha256sum "$BENCHMARK_SOURCE_ARCHIVE" | awk '{print $1}')" \
    "$(basename "$BENCHMARK_SOURCE_ARCHIVE")" \
    > "$FINAL_ROOT/ts-benchmark-source.tar.sha256"

snapshot_source "$SNAPSHOT_ROOT/post-archive"
cmp -s "$SNAPSHOT_ROOT/pre-build.aggregate" "$SNAPSHOT_ROOT/post-archive.aggregate" ||
    die "the TerraBase working tree changed during source archiving"
snapshot_benchmark_source "$SNAPSHOT_ROOT/benchmark-post-archive"
cmp -s "$SNAPSHOT_ROOT/benchmark-pre-build.aggregate" "$SNAPSHOT_ROOT/benchmark-post-archive.aggregate" ||
    die "the ts-benchmark source changed during source archiving"

# RunReceipt's subject capture is the authoritative source identity. The
# invalid object-tier argument stops before any storage or S3 work, writes a
# receipt, and exercises the same metadata path used by real workloads.
PROVENANCE_ROOT="$FINAL_ROOT/source-provenance"
set +e
dotnet "$FINAL_ROOT/TerraBase.Benchmarks.dll" \
    --workload e2e-volume \
    --object-tier invalid \
    --output "$PROVENANCE_ROOT" \
    > "$FINAL_ROOT/source-provenance.log" 2>&1
PROVENANCE_EXIT=$?
set -e
[[ "$PROVENANCE_EXIT" == 2 ]] || die "source provenance preflight exited $PROVENANCE_EXIT; expected 2"
PROVENANCE_RECEIPT="$PROVENANCE_ROOT/run-receipt.json"
[[ -f "$PROVENANCE_RECEIPT" ]] || die "source provenance receipt was not written"

snapshot_source "$SNAPSHOT_ROOT/post-provenance"
cmp -s "$SNAPSHOT_ROOT/pre-build.aggregate" "$SNAPSHOT_ROOT/post-provenance.aggregate" ||
    die "the TerraBase working tree changed during provenance capture"
snapshot_benchmark_source "$SNAPSHOT_ROOT/benchmark-post-provenance"
cmp -s "$SNAPSHOT_ROOT/benchmark-pre-build.aggregate" "$SNAPSHOT_ROOT/benchmark-post-provenance.aggregate" ||
    die "the ts-benchmark source changed during provenance capture"

EXPECTED_COMMIT=$(git -C "$SOURCE_ROOT" rev-parse HEAD)
EXPECTED_TREE=$(git -C "$SOURCE_ROOT" rev-parse 'HEAD^{tree}')
BENCHMARK_COMMIT=$(cat "$SNAPSHOT_ROOT/benchmark-post-provenance.commit")
BENCHMARK_TREE=$(cat "$SNAPSHOT_ROOT/benchmark-post-provenance.tree")
BENCHMARK_WORKING_TREE_DIGEST=$(cat "$SNAPSHOT_ROOT/benchmark-post-provenance.aggregate")
BENCHMARK_DIRTY=$(cat "$SNAPSHOT_ROOT/benchmark-post-provenance.dirty")
RECEIPT_PROJECT=$(jq -er '.subject.project' "$PROVENANCE_RECEIPT")
RECEIPT_COMMIT=$(jq -er '.subject.commit' "$PROVENANCE_RECEIPT")
RECEIPT_TREE=$(jq -er '.subject.treeDigest' "$PROVENANCE_RECEIPT")
RECEIPT_WORKING_TREE=$(jq -er '.subject.workingTreeDigest' "$PROVENANCE_RECEIPT")
EXPECTED_PROJECT="$SOURCE_ROOT/src/TerraBase/TerraBase.csproj"
[[ "$RECEIPT_PROJECT" == "$EXPECTED_PROJECT" ]] || die "provenance project mismatch"
[[ "$RECEIPT_COMMIT" == "$EXPECTED_COMMIT" ]] || die "provenance commit mismatch"
[[ "$RECEIPT_TREE" == "$EXPECTED_TREE" ]] || die "provenance tree mismatch"

sha256sum \
    "$FINAL_ROOT/TerraBase.Benchmarks.dll" \
    "$FINAL_ROOT/TerraBase.dll" \
    "$FINAL_ROOT/TerraBase.ZoneTree.dll" \
    > "$FINAL_ROOT/core-hashes.sha256"

(
    cd "$FINAL_ROOT"
    find . -type f \
        ! -name 'closure-sha256.txt' \
        ! -name 'freeze-manifest.txt' \
        -print0 |
        sort -z |
        xargs -0 sha256sum
) > "$FINAL_ROOT/closure-sha256.txt"

{
    printf 'schema=terrabase-final-source-matched-release-v1\n'
    printf 'frozenAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'repository=%s\n' "$SOURCE_ROOT"
    printf 'benchmarkProject=%s\n' "$PROJECT"
    printf 'commit=%s\n' "$RECEIPT_COMMIT"
    printf 'treeDigest=%s\n' "$RECEIPT_TREE"
    printf 'workingTreeDigest=%s\n' "$RECEIPT_WORKING_TREE"
    printf 'sourceDirty=%s\n' "$(jq -er '.subject.dirty' "$PROVENANCE_RECEIPT")"
    printf 'buildConfiguration=Release\n'
    printf 'buildOutput=%s\n' "$BUILD_OUTPUT"
    printf 'binaryClosure=%s\n' "$FINAL_ROOT"
    printf 'dotnetVersion=%s\n' "$(dotnet --version)"
    printf 'sourceArchive=%s\n' "$SOURCE_ARCHIVE"
    printf 'sourceArchiveSha256=%s\n' "$(awk '{print $1}' "$FINAL_ROOT/terrabase-source.tar.sha256")"
    printf 'sourceFileCount=%s\n' "$SOURCE_FILE_COUNT"
    printf 'sourceFileBytes=%s\n' "$SOURCE_FILE_BYTES"
    printf 'benchmarkRepository=%s\n' "$BENCH_ROOT"
    printf 'benchmarkCommit=%s\n' "$BENCHMARK_COMMIT"
    printf 'benchmarkTreeDigest=%s\n' "$BENCHMARK_TREE"
    printf 'benchmarkWorkingTreeDigest=%s\n' "$BENCHMARK_WORKING_TREE_DIGEST"
    # Keep the historical field name as an alias for consumers of the earlier
    # larger-memory freeze manifest.
    printf 'benchmarkWorkingTreeStatusDigest=%s\n' "$BENCHMARK_WORKING_TREE_DIGEST"
    printf 'benchmarkDirty=%s\n' "$BENCHMARK_DIRTY"
    printf 'benchmarkSourceArchive=%s\n' "$BENCHMARK_SOURCE_ARCHIVE"
    printf 'benchmarkSourceArchiveSha256=%s\n' "$(awk '{print $1}' "$FINAL_ROOT/ts-benchmark-source.tar.sha256")"
    printf 'benchmarkSourceFileCount=%s\n' "$BENCHMARK_SOURCE_FILE_COUNT"
    printf 'benchmarkSourceFileBytes=%s\n' "$BENCHMARK_SOURCE_FILE_BYTES"
    printf 'restoreCommand=nice -n 19 dotnet restore %s --artifacts-path %s --disable-parallel\n' "$PROJECT" "$BUILD_ROOT"
    printf 'buildCommand=nice -n 19 dotnet build %s -c Release --no-restore --artifacts-path %s -p:UseSharedCompilation=false -p:BuildInParallel=false\n' "$PROJECT" "$BUILD_ROOT"
    printf 'provenanceReceipt=%s\n' "$PROVENANCE_RECEIPT"
    printf 'provenanceExit=%s\n' "$PROVENANCE_EXIT"
    printf '\ncoreHashes:\n'
    cat "$FINAL_ROOT/core-hashes.sha256"
} > "$FINAL_ROOT/freeze-manifest.txt"

printf 'Frozen source-matched Release closure: %s\n' "$FINAL_ROOT"
printf 'Working-tree digest: %s\n' "$RECEIPT_WORKING_TREE"
cat "$FINAL_ROOT/core-hashes.sha256"
