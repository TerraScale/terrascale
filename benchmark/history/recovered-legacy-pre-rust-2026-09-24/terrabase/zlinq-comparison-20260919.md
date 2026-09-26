# ZLinq adoption comparison — 2026-09-19

Status: **verified candidate — scan throughput +16% (memory) / +35% (durable) on .NET 11 RC1, scan allocation −7.2%. Originally measured on .NET 10 at +24%/+22%; re-verified after rebasing onto `fadef600` (.NET 11 RC1 with runtime async).**

A/B experiment introducing [ZLinq](https://github.com/Cysharp/ZLinq) 1.5.6 for the engine scan paths.
Two earlier direct-translation attempts regressed and are retained below as attribution evidence;
the adopted design uses ZLinq for zero-allocation filtering/materialization plus a direct ordinal
span sort, hoisted into a shared static helper.

## Compared boundary and change set

All runs use the standard benchmark runner (`bench/TerraBase.Benchmarks`) at identical parameters
from two git worktrees of base commit `b78ceef5`:

| Side | Worktree | Change |
| --- | --- | --- |
| baseline | `terrabase-baseline-wt` @ `wt/zlinq-baseline` | none (clean `main`) |
| candidate | `terrabase-zlinq-wt` @ `wt/zlinq` | ZLinq 1.5.6 added (CPM + `src/TerraBase`); new `src/TerraBase/Storage/ScanSnapshotBuilder.cs` used by `InMemoryStorageProvider.ScanAsync`, `StorageEngine.ScanAsync`, `FileStorageProvider.ScanAsync` |

Hot-path audit finding: point-read and point-write execute **zero** `System.Linq` calls per
operation (their ~1.1 KB/op allocations are `JsonElement` clones and result records); the only
per-op LINQ on the measured paths is the scan snapshot chain. The final scan snapshot builder is:

```csharp
using var buffer = documents
    .AsValueEnumerable()
    .Where(pair => pair.Key.Tenant == tenant && pair.Key.Collection == collection)
    .ToArrayPool();
var items = buffer.Span;
items.Sort(static (left, right) => string.CompareOrdinal(left.Key.DocumentId, right.Key.DocumentId));
var snapshot = new Document[items.Length];
for (var index = 0; index < items.Length; index++)
{
    snapshot[index] = items[index].Value;
}
```

ZLinq contributes the unboxed dictionary enumeration, zero-allocation `Where`, and pooled
materialization; the ordinal ordering runs as a `Span.Sort` with a static comparison instead of
either library's `OrderBy`.

## Environment

Release build, .NET SDK 10.0.400, x64, Intel Xeon E5-2660 v4, warm temperature, seed 1337,
concurrency 1, dataset 1000 documents, payload 256 B. All benchmark commands ran under
`nice -n 19` and `taskset -c 32-39`, with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` and an
`LD_LIBRARY_PATH` libssl shim (host quirks: no libicu; NixOS names libssl `libssl3.so`).
The A/A noise floor with this protocol is **−3.4%** (identical binaries interleaved,
`receipts/zlinq-noise-aa-*`).

Correctness gates: `tests/TerraBase.Tests` 450/455 pass on both sides with the identical single
pre-existing environment failure (`DurableLogCorruptionMatrixTests.CommittedHeaderFieldBitFlipsFailClosed`,
fails on clean `main` too) and 4 Windows-only skips; an independent randomized parity probe
(300 dictionaries × 6 tenant/collection partitions, 1,800 builder calls, unicode/mixed-case ids)
matched the original `System.Linq` ordering exactly; `Span.Sort` instability is immaterial because
`DocumentKey` uniqueness rules out sort-key ties within a partition.

## Results — shipped artifact, pinned interleaved A/B (3 rounds)

Scan workload, memory backend, 5 samples × 3000 ops. Receipts:
`receipts/zlinq-ship-scan-{baseline,zlinq}-{1,2,3}`.

| Side | Throughput per round (ops/s) | p50 median (µs) | Alloc/op (B) |
| --- | --- | ---: | ---: |
| baseline | 2,216 / 2,286 / 2,221 | 378.0 | 620,825 |
| ZLinq | 2,760 / 2,792 / 2,440 | 307.9 | 576,312 |
| **delta** | **+24.3%** | **−18.5%** | **−7.2%** |

Durable storage-engine scan, 5 samples × 1000 ops. Receipts:
`receipts/zlinq-ship-durable-{baseline,zlinq}`.

| Side | Throughput (ops/s) | p50 median (µs) | Alloc/op (B) |
| --- | ---: | ---: | ---: |
| baseline | 1,963 | 439.7 | 620,847 |
| ZLinq | 2,403 | 361.9 | 576,344 |
| **delta** | **+22.4%** | **−17.7%** | **−7.2%** |

Full memory suite (point-read/point-write): no LINQ executes on those paths, allocations are
byte-identical, and results stayed within the ambient spread in both directions across runs —
treated as parity, not as wins.

The −7.2% whole-op allocation is dominated by harness measurement strings; the engine-side chain
allocation itself fell 52,672 → 8,160 B/op (−84.5%), the remainder being the returned `Document[]`.

## Attribution — why two direct translations lost and this one wins

| Candidate design | Scan throughput vs baseline | Evidence |
| --- | ---: | --- |
| ZLinq chain (`AsValueEnumerable().Where().OrderBy(Ordinal).Select().ToArray()`) | −19.5% sequential; −29% interleaved | `zlinq-ab-*`, `zlinq-interleave-*` |
| Same but `OrderBy` replaced by span sort, chain left inline in `ScanAsync` | −12.5% | `zlinq-pinned-*` |
| Span sort + chain hoisted into static `ScanSnapshotBuilder` | **+16 to +24%** | `zlinq-confirm-*`, `zlinq-final-*`, `zlinq-ship-*` |

1. **ZLinq `OrderBy` with a custom comparer** buffers into rented arrays, then sorts an `int[]`
   index map through an `IComparer<int>` that double-indirects into a rented key array on every
   comparison. `System.Linq`'s `OrderedEnumerable` compares strings directly. For this shape the
   indirection costs more than pooling saves.
2. **Inline chains inside the async iterator state machine deopt**: the same span-sort chain that
   wins in isolation lost ~12% when compiled inside `ScanAsync`'s `MoveNext()` — the giant state
   machine defeats tiered-PGO inlining for the big generic ZLinq instantiations (BCL operators
   live in precompiled `System.Linq.dll` and are unaffected). Hoisting the chain into a small
   static method (`ScanSnapshotBuilder.Build`) restores full optimization — this single refactor
   moved scan from −12.5% to +35% in the same benchmark window.
3. **Sorted insertion order is the real engine shape**: the harness seeds keys ascending, and on
   that input the pooled hybrid chain beats the BCL chain by ~34% in isolation
   (`scratch/zlinq-sort-probe`), because `Span.Sort` handles the nearly-sorted partition with far
   fewer copies than `OrderedEnumerable`'s buffered introsort plus iterator overhead.

## Remaining LINQ surfaces

The ~2,100 other `System.Linq` call sites (indexing analysis, replication anti-entropy, schema,
observability) are outside the benchmark's per-operation paths. Any future per-site ZLinq
conversion must be measured: this experiment shows both that ZLinq operator implementations can
lose to `System.Linq` (comparer-driven `OrderBy`) and that JIT context (async iterator state
machines) can flip a chain-level win into an engine-level loss.

## Re-verification on .NET 11 RC1 (runtime async)

`main` moved to .NET 11 RC1 with `runtime-async` enabled (compiler async state
machines replaced by runtime-managed async) while this candidate was in review, so the
candidate was rebased onto `fadef600` and re-measured with SDK 11.0.100-rc.1 — the JIT
context that produced the async-iterator deopt no longer exists in the same form, making
this an independent confirmation of the win rather than a replay.

Pinned interleaved A/B, receipts `receipts/zlinq-net11-scan-*` (2 rounds) and
`receipts/zlinq-net11-durable2-*`:

| Workload | Baseline | ZLinq | delta |
| --- | ---: | ---: | ---: |
| scan, memory (2 rounds) | 2,526 / 2,514 ops/s | 2,900 / 2,962 ops/s | **+16.3%** |
| scan, memory p50 | 336.3 µs | 284.5 µs | −15.4% |
| scan, storage-engine | 1,750 ops/s | 2,357 ops/s | **+34.7%** |
| scan, storage-engine p50 | 437.3 µs | 335.5 µs | −23.3% |
| allocation | 620,8xx B/op | 576,3xx B/op | −7.2% |

Correctness gate on .NET 11: `tests/TerraBase.Tests` 450/455 with the same single
pre-existing environment failure as clean `main`.

## Reproduction

```sh
# from either worktree root
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release
nice -n 19 taskset -c 32-39 dotnet run --project bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  -c Release --no-build -- \
  --storage memory --workload scan --temperature warm \
  --dataset-size 1000 --payload-size 256 --samples 5 \
  --operations 3000 --warmup 1000 --output bench/receipts/<run>
```
