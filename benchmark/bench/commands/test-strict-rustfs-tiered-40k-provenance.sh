#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly TEST_SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly TEST_SCRIPT_DIR="$(dirname "$TEST_SCRIPT_PATH")"
readonly TEST_WORK_DIR="$(mktemp -d /tmp/strict-rustfs-tiered-40k-provenance-test.XXXXXX)"

cleanup() {
    rm -rf -- "$TEST_WORK_DIR"
}
trap cleanup EXIT

source "$TEST_SCRIPT_DIR/strict-rustfs-tiered-40k.sh"

DOTNET_BIN="$DEFAULT_DOTNET_BIN"
BENCH_DLL="$(readlink -f "$TEST_SCRIPT_DIR/../../terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll")"
[[ -x "$DOTNET_BIN" && -f "$BENCH_DLL" ]] || {
    printf 'benchmark receipt verifier is unavailable\n' >&2
    exit 1
}

manifest="$TEST_WORK_DIR/freeze-manifest.txt"
receipt="$TEST_WORK_DIR/receipt.json"
printf 'schema=fixture\n' > "$manifest"
manifest_sha="$(sha256sum "$manifest" | awk '{print $1}')"
closure="$TEST_WORK_DIR/closure"
mkdir "$closure"
project="$TEST_WORK_DIR/terrabase/src/TerraBase/TerraBase.csproj"
repository="$TEST_WORK_DIR/terrabase"
commit=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
tree=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
worktree=cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
benchmark_sha=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd

jq -n \
    --arg project "$project" \
    --arg repository "$repository" \
    --arg commit "$commit" \
    --arg tree "$tree" \
    --arg worktree "$worktree" \
    --arg manifest "$manifest" \
    --arg manifest_sha "$manifest_sha" \
    --arg closure "$closure" \
    --arg benchmark_sha "$benchmark_sha" \
    '{Subject:{
        Project:$project, Repository:$repository, Commit:$commit,
        TreeDigest:$tree, WorkingTreeDigest:$worktree, Dirty:true,
        Frozen:{Project:$project,Repository:$repository,Commit:$commit,
          TreeDigest:$tree,WorkingTreeDigest:$worktree,Dirty:true,
          ManifestPath:$manifest,ManifestSha256:$manifest_sha,
          BinaryClosure:$closure,BenchmarkAssemblySha256:$benchmark_sha},
        Live:{Project:$project,Repository:$repository,Commit:$commit,
          TreeDigest:$tree,WorkingTreeDigest:("e" * 64),Dirty:true}}}' \
    > "$receipt"

verify_frozen_provenance "$receipt" "$manifest" "$manifest_sha" "$closure" \
    "$project" "$repository" "$commit" "$tree" "$worktree" true "$benchmark_sha"

jq -n \
    --arg output "$TEST_WORK_DIR/output" \
    --arg project "$project" \
    --arg repository "$repository" \
    --arg commit "$commit" \
    --arg tree "$tree" \
    --arg worktree "$worktree" \
    --arg manifest "$manifest" \
    --arg manifest_sha "$manifest_sha" \
    --arg closure "$closure" \
    --arg benchmark_sha "$benchmark_sha" \
    '{schemaVersion:1,workload:"tiered-segment",status:"complete",exitCode:0,
      outputDirectory:$output,receiptSha256:("f" * 64),
      subject:{project:$project,repository:$repository,commit:$commit,
        treeDigest:$tree,workingTreeDigest:$worktree,dirty:true,
        frozen:{project:$project,repository:$repository,commit:$commit,
          treeDigest:$tree,workingTreeDigest:$worktree,dirty:true,
          manifestPath:$manifest,manifestSha256:$manifest_sha,
          binaryClosure:$closure,benchmarkAssemblySha256:$benchmark_sha},
        live:{project:$project,repository:$repository,commit:$commit,
          treeDigest:$tree,workingTreeDigest:("e" * 64),dirty:true}},
      artifacts:[{file:"tiered-rustfs-receipt.json",bytes:3,sha256:""}]}' \
    > "$TEST_WORK_DIR/compact.json"
printf '{}\n' > "$TEST_WORK_DIR/tiered-rustfs-receipt.json"
detailed_sha="$(sha256sum "$TEST_WORK_DIR/tiered-rustfs-receipt.json" | awk '{print $1}')"
jq --arg sha "$detailed_sha" \
    '.artifacts[0].sha256 = $sha' "$TEST_WORK_DIR/compact.json" > "$TEST_WORK_DIR/compact.tmp"
mv -- "$TEST_WORK_DIR/compact.tmp" "$TEST_WORK_DIR/compact.json"
compact_digest="$($DOTNET_BIN "$BENCH_DLL" --receipt-digest "$TEST_WORK_DIR/compact.json")"
jq --arg digest "$compact_digest" \
    '.receiptSha256 = $digest' "$TEST_WORK_DIR/compact.json" > "$TEST_WORK_DIR/compact.tmp"
mv -- "$TEST_WORK_DIR/compact.tmp" "$TEST_WORK_DIR/compact.json"
verify_compact_receipt "$TEST_WORK_DIR/compact.json" "$TEST_WORK_DIR/tiered-rustfs-receipt.json" \
    "$TEST_WORK_DIR/output" "$project" "$repository" "$commit" "$tree" "$worktree" true \
    "$manifest" "$manifest_sha" "$closure" "$benchmark_sha"
if jq '.outputDirectory += "/tampered"' "$TEST_WORK_DIR/compact.json" > "$TEST_WORK_DIR/compact-tampered.json" &&
   verify_compact_receipt "$TEST_WORK_DIR/compact-tampered.json" "$TEST_WORK_DIR/tiered-rustfs-receipt.json" \
       "$TEST_WORK_DIR/output" "$project" "$repository" "$commit" "$tree" "$worktree" true \
       "$manifest" "$manifest_sha" "$closure" "$benchmark_sha"; then
    printf 'expected compact receipt with stale digest to fail\n' >&2
    exit 1
fi
if jq 'del(.subject.frozen)' "$TEST_WORK_DIR/compact.json" > "$TEST_WORK_DIR/compact-missing.json" &&
   verify_compact_receipt "$TEST_WORK_DIR/compact-missing.json" "$TEST_WORK_DIR/tiered-rustfs-receipt.json" \
       "$TEST_WORK_DIR/output" "$project" "$repository" "$commit" "$tree" "$worktree" true \
       "$manifest" "$manifest_sha" "$closure" "$benchmark_sha"; then
    printf 'expected compact receipt without frozen provenance to fail\n' >&2
    exit 1
fi

jq 'del(.Subject.Frozen)' "$receipt" > "$receipt.tmp"
mv -- "$receipt.tmp" "$receipt"
if verify_frozen_provenance "$receipt" "$manifest" "$manifest_sha" "$closure" \
    "$project" "$repository" "$commit" "$tree" "$worktree" true "$benchmark_sha"; then
    printf 'expected missing frozen provenance to fail\n' >&2
    exit 1
fi

jq --arg wrong_manifest_sha "$(printf wrong | sha256sum | awk '{print $1}')" \
    '.Subject.Frozen = {Project:.Subject.Project,Repository:.Subject.Repository,
      Commit:.Subject.Commit,TreeDigest:.Subject.TreeDigest,
      WorkingTreeDigest:.Subject.WorkingTreeDigest,Dirty:.Subject.Dirty,
      ManifestPath:"wrong",ManifestSha256:$wrong_manifest_sha,
      BinaryClosure:"wrong",BenchmarkAssemblySha256:"wrong"}' \
    "$TEST_WORK_DIR/receipt.json" > "$TEST_WORK_DIR/mismatch.json"
if verify_frozen_provenance "$TEST_WORK_DIR/mismatch.json" "$manifest" "$manifest_sha" "$closure" \
    "$project" "$repository" "$commit" "$tree" "$worktree" true "$benchmark_sha"; then
    printf 'expected mismatched frozen provenance to fail\n' >&2
    exit 1
fi

printf 'strict-rustfs-tiered-40k provenance fixture: ok\n'
