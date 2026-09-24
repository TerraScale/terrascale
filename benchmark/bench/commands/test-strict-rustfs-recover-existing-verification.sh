#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_RECOVERY_SCRIPT="$TEST_SCRIPT_DIR/strict-rustfs-recover-existing.sh"
readonly TEST_LAUNCH_ENV_PARSER="$TEST_SCRIPT_DIR/load-launch-env.sh"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-recovery-verification-test.XXXXXX)"

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

readonly TEST_BIN="$TEST_WORK_DIR/bin"
readonly TEST_SOURCE="$TEST_WORK_DIR/source"
readonly TEST_PASS="$TEST_WORK_DIR/pass"
mkdir -p -- "$TEST_BIN" "$TEST_SOURCE" "$TEST_PASS/engine"

# The verifier only needs an inactive systemd status. Keeping this command in
# the temporary fixture lets the test exercise the real verify entry point.
printf '%s\n' \
    '#!/usr/bin/env bash' \
    'if [[ "$*" == *" show "* || "$*" == show* ]]; then printf "inactive\\n"; fi' \
    > "$TEST_BIN/systemctl"
chmod 700 "$TEST_BIN/systemctl"

jq -n \
    '{RemoteAck:{AcknowledgedRecords:4},RemoteValidatedFrontier:4}' \
    > "$TEST_SOURCE/tiered-rustfs-receipt.json"
SOURCE_RECEIPT_SHA="$(sha256sum "$TEST_SOURCE/tiered-rustfs-receipt.json" | awk '{ print $1 }')"
printf 'wal-fixture\n' > "$TEST_PASS/engine/wal"
printf 'summary fixture\n' > "$TEST_PASS/tiered-rustfs-recovery-summary.md"
printf 'exitCode=0\n' > "$TEST_PASS/benchmark-exit-status"

{
    printf 'OUTPUT=%q\n' "$TEST_PASS"
    printf 'RUN_DIR=%q\n' "$TEST_PASS"
    printf 'UNIT=%q\n' 'terrabase-test.service'
    printf 'SOURCE_RUN_DIR=%q\n' "$TEST_SOURCE"
    printf 'SOURCE_RECEIPT=%q\n' "$TEST_SOURCE/tiered-rustfs-receipt.json"
    printf 'SOURCE_RECEIPT_SHA=%q\n' "$SOURCE_RECEIPT_SHA"
    printf 'SOURCE_ENGINE_BYTES=%q\n' 1
    printf 'SOURCE_FREE_BYTES=%q\n' 200000000000
    printf 'SOURCE_FREE_AFTER_RECLAIM_BYTES=%q\n' 200000000001
    printf 'PREFLIGHT_FREE_BYTES=%q\n' 268435456000
    printf 'RECORDS=%q\n' 3
    printf 'EXACT_SCAN_REQUESTED=%q\n' 0
    printf 'VERIFICATION_SCOPE=%q\n' sampled-remote-journal-recovery
    printf 'CACHE_BYTES=%q\n' 16777216
    printf 'MAX_LOCAL_STORAGE_BYTES=%q\n' 85899345920
    printf 'MIN_FREE_SPACE_BYTES=%q\n' 107374182400
    printf 'BENCH_DLL=%q\n' ''
    printf 'BENCH_SHA=%q\n' 5555555555555555555555555555555555555555555555555555555555555555
    printf 'BINARY_CLOSURE=%q\n' /frozen/closure
    printf 'SOURCE_REPOSITORY=%q\n' /source/repository
    printf 'SOURCE_COMMIT=%q\n' 1111111111111111111111111111111111111111
    printf 'SOURCE_TREE_DIGEST=%q\n' 2222222222222222222222222222222222222222
    printf 'SOURCE_WORKTREE_DIGEST=%q\n' 3333333333333333333333333333333333333333333333333333333333333333
    printf 'SOURCE_DIRTY=%q\n' false
    printf 'FROZEN_MANIFEST_PATH=%q\n' /frozen/manifest.txt
    printf 'FROZEN_MANIFEST_SHA256=%q\n' 4444444444444444444444444444444444444444444444444444444444444444
    printf 'DOTNET_BIN=%q\n' ''
    printf 'PREFIX=%q\n' fixture
    printf 'BATCH=%q\n' 1
    printf 'PAYLOAD_BYTES=%q\n' 1
    printf 'PAYLOAD_MODE=%q\n' fixture
    printf 'SEED=%q\n' 1
    printf 'REGION=%q\n' us-east-1
    printf 'ENDPOINT=%q\n' http://127.0.0.1:19000
    printf 'BUCKET=%q\n' terrabase-tiered
} > "$TEST_PASS/launch.env"

# Exercise the exact launch metadata contract consumed by the systemd wrapper.
# Nonempty provenance values must survive nonexecuting parsing and be mapped to
# the frozen source environment exported to the benchmark process.
source "$TEST_LAUNCH_ENV_PARSER"
load_launch_env_file "$TEST_PASS/launch.env" \
    OUTPUT RUN_DIR UNIT SOURCE_RUN_DIR SOURCE_RECEIPT SOURCE_RECEIPT_SHA \
    SOURCE_ENGINE_BYTES SOURCE_FREE_BYTES SOURCE_FREE_AFTER_RECLAIM_BYTES \
    PREFLIGHT_FREE_BYTES CACHE_BYTES MAX_LOCAL_STORAGE_BYTES \
    MIN_FREE_SPACE_BYTES EXACT_SCAN_REQUESTED VERIFICATION_SCOPE \
    BENCH_DLL BENCH_SHA BINARY_CLOSURE SOURCE_REPOSITORY \
    SOURCE_COMMIT SOURCE_TREE_DIGEST SOURCE_WORKTREE_DIGEST SOURCE_DIRTY \
    FROZEN_MANIFEST_PATH FROZEN_MANIFEST_SHA256 DOTNET_BIN PREFIX RECORDS \
    BATCH PAYLOAD_BYTES PAYLOAD_MODE SEED REGION ENDPOINT BUCKET
TERRABASE_FROZEN_SOURCE_REPOSITORY="$SOURCE_REPOSITORY"
TERRABASE_FROZEN_SOURCE_COMMIT="$SOURCE_COMMIT"
TERRABASE_FROZEN_SOURCE_TREE_DIGEST="$SOURCE_TREE_DIGEST"
TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST="$SOURCE_WORKTREE_DIGEST"
TERRABASE_FROZEN_SOURCE_DIRTY="$SOURCE_DIRTY"
[[ "$TERRABASE_FROZEN_SOURCE_REPOSITORY" == /source/repository ]]
[[ "$TERRABASE_FROZEN_SOURCE_COMMIT" == 1111111111111111111111111111111111111111 ]]
[[ "$TERRABASE_FROZEN_SOURCE_TREE_DIGEST" == 2222222222222222222222222222222222222222 ]]
[[ "$TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST" == 3333333333333333333333333333333333333333333333333333333333333333 ]]
[[ "$TERRABASE_FROZEN_SOURCE_DIRTY" == false ]]
rg -F 'export TERRABASE_FROZEN_SOURCE_REPOSITORY="$SOURCE_REPOSITORY"' "$TEST_RECOVERY_SCRIPT" >/dev/null
rg -F 'export TERRABASE_FROZEN_SOURCE_COMMIT="$SOURCE_COMMIT"' "$TEST_RECOVERY_SCRIPT" >/dev/null
rg -F 'export TERRABASE_FROZEN_SOURCE_TREE_DIGEST="$SOURCE_TREE_DIGEST"' "$TEST_RECOVERY_SCRIPT" >/dev/null
rg -F 'export TERRABASE_FROZEN_SOURCE_WORKING_TREE_DIGEST="$SOURCE_WORKTREE_DIGEST"' "$TEST_RECOVERY_SCRIPT" >/dev/null
rg -F 'export TERRABASE_FROZEN_SOURCE_DIRTY="$SOURCE_DIRTY"' "$TEST_RECOVERY_SCRIPT" >/dev/null

cat > "$TEST_PASS/resource-samples.jsonl" <<'EOF'
{"utc":"2026-09-24T00:00:00Z","localStorageBytes":100,"ssdCacheBytes":10,"availableFreeSpaceBytes":200000000000,"workingSetBytes":1,"peakRssBytes":2,"cgroup":{"MemoryMax":"4294967296","SwapMax":"0","SwapCurrent":"0","MemoryEvents":"low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 0"}}
{"utc":"2026-09-24T00:00:01Z","localStorageBytes":200,"ssdCacheBytes":20,"availableFreeSpaceBytes":199999999000,"workingSetBytes":3,"peakRssBytes":4,"cgroup":{"MemoryMax":"4294967296","SwapMax":"0","SwapCurrent":"0","MemoryEvents":"low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 0"}}
EOF

jq -n \
    '{Path:"/sys/fs/cgroup/test.service",MemoryMax:"4294967296",MemoryPeak:"4",SwapMax:"0",SwapCurrent:"0",MemoryEvents:"low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 0",MemoryCurrent:"4",IoStat:""}' \
    > "$TEST_PASS/resource-envelope.json"

jq -n \
    --arg samples "$TEST_PASS/resource-samples.jsonl" \
    --arg source_repository /source/repository \
    --arg source_commit 1111111111111111111111111111111111111111 \
    --arg source_tree 2222222222222222222222222222222222222222 \
    --arg source_worktree 3333333333333333333333333333333333333333333333333333333333333333 \
    --arg frozen_manifest /frozen/manifest.txt \
    --arg frozen_manifest_sha 4444444444444444444444444444444444444444444444444444444444444444 \
    --arg frozen_closure /frozen/closure \
    --arg frozen_sha 5555555555555555555555555555555555555555555555555555555555555555 \
    '{Status:"complete",Mode:"recover-existing",VerificationScope:"sampled-remote-journal-recovery",
      ExpectedRecords:3,SourceRootDeleted:true,SourceReceiptUnchanged:true,RecoveredWalBytes:10,
      SourceRemoteAckRecords:4,SourceInternalAuthorityRecords:1,RecoveredFrontier:4,
      RemoteValidatedFrontier:4,RemoteFrontierUnchanged:true,SampleCount:1,
      SampleReadsByPhase:{"first-reopen":1,"second-reopen":1},ExactScan:"skipped",
      Subject:{Project:($source_repository + "/src/TerraBase/TerraBase.csproj"),Repository:$source_repository,
        Commit:$source_commit,TreeDigest:$source_tree,WorkingTreeDigest:$source_worktree,Dirty:false,
        Frozen:{Project:($source_repository + "/src/TerraBase/TerraBase.csproj"),Repository:$source_repository,
          Commit:$source_commit,TreeDigest:$source_tree,WorkingTreeDigest:$source_worktree,Dirty:false,
          ManifestPath:$frozen_manifest,ManifestSha256:$frozen_manifest_sha,BinaryClosure:$frozen_closure,
          BenchmarkAssemblySha256:$frozen_sha},
        Live:{Project:($source_repository + "/src/TerraBase/TerraBase.csproj")}},
      ResourceSamplesPath:$samples,PeakLocalStorageBytes:200,LatestLocalStorageBytes:200,
      LatestCacheBytes:20,MaxLocalStorageBytes:85899345920,MinimumFreeSpaceBytes:107374182400,
      MinimumObservedFreeSpaceBytes:199999999000,ResourceSamplerError:null,
      FinalCgroup:{MemoryMax:"4294967296",SwapMax:"0",SwapCurrent:"0",
        MemoryEvents:"low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 0"}}' \
    > "$TEST_PASS/tiered-rustfs-recovery-receipt.json"

run_verify() {
    PATH="$TEST_BIN:$PATH" "$TEST_RECOVERY_SCRIPT" verify --run-dir "$1"
}

run_verify "$TEST_PASS" > "$TEST_WORK_DIR/pass.log"
jq -e '
    .schema == "terrabase-rustfs-recovery-resource-v1"
    and .sampleCount == 2
    and .recordedPeakLocalStorageBytes == 200
    and .observedPeakCacheBytes == 20
    and .oomAndSwap == "zero"
' "$TEST_PASS/resource-verification.json" >/dev/null
[[ -s "$TEST_PASS/resource-envelope-verification.json" ]]
[[ "$(wc -l < "$TEST_PASS/verified-sha256sums.txt")" == 4 ]]

# Keep the negative cases explicit and local to the generated fixture. They
# cover the cap, sampler error, and cgroup swap gates without a benchmark run.
cache_fixture="$TEST_WORK_DIR/cache-cap"
cp -a -- "$TEST_PASS" "$cache_fixture"
jq --arg samples "$cache_fixture/resource-samples.jsonl" \
    '.ResourceSamplesPath = $samples' \
    "$cache_fixture/tiered-rustfs-recovery-receipt.json" \
    > "$cache_fixture/tiered-rustfs-recovery-receipt.json.tmp"
mv -- "$cache_fixture/tiered-rustfs-recovery-receipt.json.tmp" \
    "$cache_fixture/tiered-rustfs-recovery-receipt.json"
sed 's/"ssdCacheBytes":20/"ssdCacheBytes":16777217/' "$cache_fixture/resource-samples.jsonl" > "$cache_fixture/resource-samples.jsonl.tmp"
mv -- "$cache_fixture/resource-samples.jsonl.tmp" "$cache_fixture/resource-samples.jsonl"
if run_verify "$cache_fixture" > "$TEST_WORK_DIR/cache-cap.log" 2>&1; then
    printf 'expected cache cap verification failure\n' >&2
    exit 1
fi

sampler_error_fixture="$TEST_WORK_DIR/sampler-error"
cp -a -- "$TEST_PASS" "$sampler_error_fixture"
jq --arg samples "$sampler_error_fixture/resource-samples.jsonl" \
    '.ResourceSamplesPath = $samples' \
    "$sampler_error_fixture/tiered-rustfs-recovery-receipt.json" \
    > "$sampler_error_fixture/tiered-rustfs-recovery-receipt.json.tmp"
jq '.ResourceSamplerError = "synthetic sampler failure"' \
    "$sampler_error_fixture/tiered-rustfs-recovery-receipt.json.tmp" \
    > "$sampler_error_fixture/tiered-rustfs-recovery-receipt.json"
rm -- "$sampler_error_fixture/tiered-rustfs-recovery-receipt.json.tmp"
if run_verify "$sampler_error_fixture" > "$TEST_WORK_DIR/sampler-error.log" 2>&1; then
    printf 'expected sampler error verification failure\n' >&2
    exit 1
fi

swap_fixture="$TEST_WORK_DIR/swap"
cp -a -- "$TEST_PASS" "$swap_fixture"
jq --arg samples "$swap_fixture/resource-samples.jsonl" \
    '.ResourceSamplesPath = $samples' \
    "$swap_fixture/tiered-rustfs-recovery-receipt.json" \
    > "$swap_fixture/tiered-rustfs-recovery-receipt.json.tmp"
mv -- "$swap_fixture/tiered-rustfs-recovery-receipt.json.tmp" \
    "$swap_fixture/tiered-rustfs-recovery-receipt.json"
sed 's/"SwapCurrent":"0"/"SwapCurrent":"1"/' "$swap_fixture/resource-samples.jsonl" > "$swap_fixture/resource-samples.jsonl.tmp"
mv -- "$swap_fixture/resource-samples.jsonl.tmp" "$swap_fixture/resource-samples.jsonl"
if run_verify "$swap_fixture" > "$TEST_WORK_DIR/swap.log" 2>&1; then
    printf 'expected swap verification failure\n' >&2
    exit 1
fi

printf 'strict-rustfs-recover-existing verification fixture: ok\n'
