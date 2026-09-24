# LZ4 one-shot allocation diagnostic — 2026-09-14

This is a historical managed-allocation diagnostic. It is not a throughput benchmark and makes no speedup claim. No benchmark was run during this relocation.

## Measurements

The retained harness creates a deterministic 4 MiB input, calls `LZ4DataCompression.Compress` once at compression level 3, and reads `GC.GetAllocatedBytesForCurrentThread` around that call. The output is kept alive through the sample.

| Mode | Setup before measured call | Input bytes | Output bytes | Allocated current-thread bytes | Reported elapsed ms |
| --- | --- | ---: | ---: | ---: | ---: |
| Cold | First call in a fresh process, as described by the original note | 4,194,304 | 4,194,335 | 12,593,784 | 220.366 |
| Warm | Three warm-up calls, then full GC before the measured call | 4,194,304 | 4,194,335 | 4,194,400 | 182.421 |

There is one retained observation per mode. The elapsed times are context only: the run has no recorded repetition plan, hardware profile, or controlled comparison. The original assessment was that the warm managed allocation is approximately the exact output array plus small overhead.

Raw output: [cold log](lz4-safe-one-shot-allocation-cold-20260914.log), [warm log](lz4-safe-one-shot-allocation-warm-20260914.log). The unchanged harness is [C# source](lz4-safe-one-shot-allocation-harness-20260914.cs) plus [project file](lz4-safe-one-shot-allocation-harness-20260914.csproj).

## Provenance

- Artifact timestamps in the source worktree are 2026-09-14, local time `-03:00`.
- The original implementation note records source revision `fa68755e2c64b5137ebca2945d33c8f850a0944a`, with working-tree hashes `6f4779726aabe85d90782098fe86dd7b4af327b8ffb25364f93d13b7f2f254f4` for `LZ4DataCompression.cs` and `cbc54eb83aa206e5b88166051b700f3f14861a98a957e93a7999cd4217e081bc` for `CompressionIntegrityTests.cs`.
- The harness project targets `net10.0` and contains an absolute `ProjectReference` to `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase.ZoneTree/TerraBase.ZoneTree.csproj`.
- The original evidence does not record the harness invocation, installed SDK/runtime version, host/CPU profile, or a build receipt binding the harness output to source hashes. Treat the retained source and output as historical evidence, not a source-matched reproducibility claim.
- The original focused-test report and provenance addendum are preserved beside the harness. The focused test log reports 14 passing tests from a `--no-build` invocation and explicitly notes that it is not source-matched qualification; it is not a benchmark result.

Original narrative and context: [implementation note](lz4-safe-one-shot-implementation-20260914.md), [provenance addendum](lz4-safe-one-shot-provenance-addendum-20260914.md), and [focused test output](lz4-safe-one-shot-focused-tests-20260914.log).

## Separate cgroup temp-root metadata

[cgroup-bench-tempdir.txt](cgroup-bench-tempdir.txt) records `/tmp/terrabase-bench-cgroup.IcBhkq`. This is associated with the separate 2026-09-14 larger-than-memory run recorded under [`terrabase/receipts/20260914-cgroup256-larger-memory-zonetree-8192-run3/`](../../terrabase/receipts/20260914-cgroup256-larger-memory-zonetree-8192-run3/larger-memory-summary.md) in this repository. The recorded `/tmp` directory no longer exists. This metadata is retained for provenance; the underlying temporary directory was not recreated.

## SHA-256

| Artifact | SHA-256 |
| --- | --- |
| `lz4-safe-one-shot-allocation-harness-20260914.cs` | `cf946f2b27c8c290c009b72ccc8d627df031181120e3c5d9a75fcf3e3b92e6d7` |
| `lz4-safe-one-shot-allocation-harness-20260914.csproj` | `dfe70308855cc637f8e3036c2c45e930ba9df268cf36b7c55f085ee47257e4c2` |
| `lz4-safe-one-shot-allocation-cold-20260914.log` | `264f6f5e3385ea3da1670451d1d5edf46681c5f56b91f7c398db19b9b2ff9748` |
| `lz4-safe-one-shot-allocation-warm-20260914.log` | `b39cb95517071221d11146fd32d533bafaa470bb7b6ab4fd9cffd778d87823f8` |
| `cgroup-bench-tempdir.txt` | `bdb8e719c31ee9860d951c026916edb601ba3bf3bae209c6b58b402fe49c1278` |
| `lz4-safe-one-shot-implementation-20260914.md` | `4bf65ac9d5338436bdbf46ec3e0878ee953e3673b846589bdab7882084e857f4` |
| `lz4-safe-one-shot-provenance-addendum-20260914.md` | `e258019027253fc0dce03c84e0d698dee3b62f6b89b37536e4163f952c535af3` |
| `lz4-safe-one-shot-focused-tests-20260914.log` | `93739025140dbb8657716a212aa97c7d986eb67dc224483771359e2ea0ab42ab` |
