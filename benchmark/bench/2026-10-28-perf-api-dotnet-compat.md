# .NET benchmark compatibility qualification, 2026-10-28

Offline build/control evidence only. No production request, tenant, latency,
throughput, SDK parity or speedup measurement. The original target is unmet.

On delta through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19`, qualified source
`da84ebd72` plus the three regenerated benchmark lockfiles. The SDK's official
GitHub HTTPS checkout remained clean at v0.0.18,
`36d114877a69d18fba33b27ad362bd3e68e9e7ff`. It is the measured HTTP/2 release.
The benchmark now targets .NET 11 and creates its database through project POST
with allRegions, so a new placement baseline is required. Its separate workload
preflight remains closed for unimplemented lifecycle/measurement work.

| Check | Observed result |
| --- | --- |
| SDK preparation guards | 14 fixture checks passed without network requests |
| Dependency retrieval | Official HTTPS origin, exact revision, clean checkout |
| Restore | Three lockfiles regenerated; subsequent locked restore passed |
| Build and formatting | Existing `make check` passed, including all three projects |
| Benchmark tests | 101 passed, 0 failed, 0 skipped |

Artifacts on delta: managed
`fix-api-dotnet-benchmark-compat-50b311c6628bdbea67fcf6c2c9bbcf8ead09e4d476db7314c7355301c080f0c5`,
`benchmark/bench/api-dotnet-sdk-qualification-a/`: exact script, SDK identity,
prepare/restore/check logs and lockfile copies/hashes. Local checks are not CI
or live proof. AOT publication and authenticated regional parity remain untested.

| Regenerated lockfile | SHA-256 |
| --- | --- |
| src | `aff1c05cd6f85572c5217402298dd9a82eb9eabc9d5737ea05968bc4a9bbf05d` |
| tests | `76d865e3decc1c5a75efe91f75edf24afa15bec220c6f73f90d087c9965fc52b` |
| api-bench | `8ff883cb406cdd07e2ae43db2b5f56c8ca6723da3e0337eea6c5275a1e8f9a36` |
