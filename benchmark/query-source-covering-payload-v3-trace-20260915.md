# Warm 10,000-document CPU/sample trace supplement

This supplement profiles the existing accepted covering-payload v3 artifact during one
warm 10,000-document HTTP scan run. It is a root-cause measurement, not a new
benchmark matrix or a capacity qualification.

## Identity and effective environment

| Item | Value |
| --- | --- |
| Source root | `.codex/evidence/terrabase-covering-payload-verification-20260915-v3/source` |
| Source manifest | SHA-256 `32df9bf4565ce995083bf3288ff5f29f58889f8aaeba0641c5a467d3cc2b2d95`, 985 files |
| Measurement artifact | SHA-256 `362c52505d0aaf96c07e044f5abb6cde051d172ce388681e606937044cd752f2` |
| Target SDK / declared runtime / RID | SDK `10.0.401`; runtimeconfig declares `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` `10.0.0`; `linux-x64` |
| Target host | `/home/mariogk/.local/share/mise/dotnet-root/dotnet`, selected from the source root's `global.json` |
| OS | NixOS 26.05, x86_64 |
| Target and collector | Effective affinity CPUs 2–3, nice 19, `DOTNET_PROCESSOR_COUNT=2` |
| Cgroup | `cpu.max=max`, `memory.max=max`, `memory.high=max`; baseline `nr_throttled=0`; host cgroup cpuset `0-45` |
| Local trace tool | `dotnet-trace 10.0.745401+cef304c50763bf24f99566cb31d55540842e7ae9`; executable SHA-256 `ceb3853027d2571c9fceae19ddc5cc12d0db4ec40dfd5f2d340d0d30f6127eb8` |

The trace is managed `dotnet-sampled-thread-time` sampling at approximately 100 Hz,
which estimates sampled managed thread/wall-clock time. It is not kernel CPU time.
The target runtime mapping was sampled before the runtime libraries appeared in
`/proc/<pid>/maps`, and the sample-only trace does not identify a loaded runtime
version; loaded runtime is therefore recorded as unknown beyond the runtimeconfig
declaration.

## Collection and boundaries

`dotnet-trace` was absent from PATH. It was installed only under
`.codex/swarm/measurement/query-source-20260915/tools` with an isolated CLI home
and NuGet cache. The first attachment was retained under
`trace-warm10k-20260915`; it failed before collection because this tool version
does not accept the old `cpu-sampling` profile name. It did not alter the artifact
or baseline.

The successful capture is
[`trace-warm10k-20260915-v2`](../../.codex/swarm/measurement/query-source-20260915/trace-warm10k-20260915-v2).
It seeded a fresh 10,000-document fixture with one direct `StorageEngine.ApplyBatchAsync`,
then ran 60 warm requests after three discarded warmups. The collector attached
immediately after the first recorded response, so the trace has 59 subsequent
request intervals estimated fully after the attachment command. The target and
collector both exited 0; target stderr was empty.

The harness timestamp evidence gives:

| Boundary | UTC |
| --- | --- |
| Target process start | 2026-09-15 15:46:06.784262 |
| First recorded request estimated start | 15:46:09.407078 |
| First recorded response | 15:46:09.465595 |
| Collector attach command stamped | 15:46:09.465833 |
| Last recorded response | 15:46:16.096740 |
| Target process finish | 15:46:16.335859 |

The 2,622.816 ms before the first request includes host startup, JIT, and the three
discarded warmups. The 6,689.666 ms from the first request start through the last
response is the HTTP request window. The 239.117 ms after the last response is host
disposal/background shutdown. Request starts were estimated from each flushed JSON
sample's output receipt time minus its harness `elapsedMilliseconds`; EventPipe
session startup latency is not independently timestamped, so the first part of the
first post-attachment request may be absent from the trace.

All 60 trace-run requests were HTTP 200, `plan.access=Scan`, source-path confirmed,
`rowsExamined=10000`, and `workUnits=10002`. The trace run's measured latency was
p50 59.9123 ms, p95 285.3205 ms, p99 315.9870 ms, max 320.4815 ms. These values
include profiler attachment/collection overhead and must not replace the immutable
30-sample baseline (warm10k p50 53.5607 ms).

## Stack attribution

The retained reports were generated with `dotnet-trace report ... topN -n 100`.
The highest inclusive sampled thread-time entries on the accepted source path were:

| Stack/path | Inclusive | Exclusive |
| --- | ---: | ---: |
| `StorageEngine.ExecuteBounded` | 9.26% | 0.60% |
| `StorageEngine.ValidateBoundedEntry` | 4.03% | 0.67% |
| `RuntimeIndexCodec.Matches` | 2.41% | 0.23% |
| `RuntimeIndexCodec.ExtractValues` | 1.45% | 0.46% |
| `StorageRecordCodec.CanonicalKey` | 1.23% | 0.20% |
| Query endpoint/planner chain through `StorageEngine.ExecuteBounded` | about 9.26–9.35% | — |

The full chain is visible as
`DocumentQueryEndpoint -> AutomaticIndexDocumentQueryExecutor ->
AutomaticIndexRuntime.QueryBoundedAsync -> QueryPlanner.QueryAsync ->
IndexRuntimeSourceQueryExecutor.ExecuteAsync -> StorageEngine.ExecuteBounded`.
The accepted source places `ExecuteBounded` at
`StorageEngine.cs:445`, `ValidateBoundedEntry` at
`StorageEngine.cs:2910`, and predicate matching in
`RuntimeIndexCodec.cs:65`. This is consistent with the receipt's unindexed
10,000-row source scan and identifies validation and predicate matching inside the
scan as the measurable application path.

The largest samples overall were
`LowLevelLifoSemaphore.WaitNative` 28.06% exclusive,
`Monitor.Wait` 19.92% exclusive,
a 10.06% missing-symbol stack, and
`Watcher!Interop+Sys.Read` 9.85% exclusive. These are scheduling/runtime or
background samples and explain why this managed sample cannot be read as CPU
percentages for the scan. The query chain's presence is strong evidence that the
scan is active in the request path; its causal dominance over latency remains
medium confidence until kernel CPU samples or source-boundary timings are available.

No allocation conclusion is drawn from these stacks. The existing HTTP boundary
allocation field remains unavailable.

## CPU collection limit and next handoff

`dotnet-trace list-profiles` reports kernel `cpu-sampling` only under
`collect-linux`. The current environment has `/proc/sys/kernel/perf_event_paranoid=2`,
`CapEff=0` (no effective `CAP_PERFMON`/admin capability), and no `perf`
executable. Thus a kernel CPU attribution was not available; the retained
managed sample is the bounded substitute.

The smallest decisive follow-up is one same-artifact warm10k run with
`dotnet-trace collect-linux --profile cpu-sampling` after granting the trace
process the required perf privilege. Keep CPUs 2–3, nice 19, the same fixture and
runtime identity, then compare kernel CPU samples specifically for
`StorageEngine.ExecuteBounded`, `ValidateBoundedEntry`, predicate matching,
and HTTP serialization. No horizontal or vertical scaling was measured here, so
the existing no-capacity-claim limit remains.
