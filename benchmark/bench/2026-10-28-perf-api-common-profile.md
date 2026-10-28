# Common API overhead profile (keyed api lane, small reads), 2026-10-28

Diagnostic only. This report attributes the overhead that keyed and keyless requests share (~84–112 µs server over a 4–7 µs
engine; [keyed-keyless](https://github.com/TerraScale/terrascale/blob/d0c2733d034a2ec28c58bcd8861923d2c162f7d1/benchmark/bench/2026-10-28-perf-api-keyed-keyless.md)) to mechanisms. It is not a candidate patch, not a before/after
comparison and not a speedup. The original target, server overhead <10% over the engine with the engine as denominator,
remains unmet by more than two orders of magnitude. No production request, credential or tenant was used.

## Method

- **Binary.** The frozen `keyed-keyless-b1` build was reused unchanged; nothing was rebuilt. It has production source
  `93b873792`, harness `f005f0081` and ts-dotnet `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, cloned from GitHub at that pin.
  The job checked these hashes before every process:
  terrabase.dll `c1175b9eca103e93a8d1e36d83c5c9489c34ae4d3f0c058a71fa72f1e79c7fbf`,
  TerraScale.DotNet.dll `a4f3be7fccbf55ff5b5516cb5d0063982a67771fd47c2d45fe7cb982e641e19a`,
  TerraBase.E2E.dll `04199e143ce2833158a30d91c85ccaa379e2534acf4f4b18538a1ab26410308e`,
  runtimeconfig `5628e9d54e2eae59b08c162e41eedb7bffd95ab6b2646c23a8d49473f590809a`.
  Between `93b873792` and main `427ddf231`, `src` changed only in Turso write-intent retirement (`StorageBusyException`) and
  ts-auth `PlatformGrants`. Neither is on the read path, but this is still not a profile of main's binary.
- **Workload.** Unchanged from the keyed-keyless run: lane `api`, read workload, CONCURRENCY=1,16 and a 45 s window after the
  fixed 10 s warmup. The run used `taskset -c 2-11`, workstation concurrent GC and TieredPGO. The production project is configured for the
  same GC mode (`src/TerraBase/TerraBase.csproj`). The attribution semantics and the engine denominator are unchanged.
  Two fresh processes per run: p1 (harness `5439abd38`) and p2 (harness `dede8f8bc`). Each profiled small c=1 and small c=16;
  the medium cells ran untraced.
- **Inside each profiled window** (`api-common-profile.py`):
  1. A 5 s `/proc` interval with no profiler: per-thread utime/stime, schedstat run and run-queue wait, and context switches.
  2. A 10 s EventPipe CPU trace: SampleProfiler, runtime GC, loader, JIT, contention, exception and thread-pool events, and
     1 s counters.
  3. A 6 s allocation trace: in p1, GC verbose `AllocationTick`. In p2, `AllocationSampling` (0x80000000000), the runtime's
     randomized sampling with a 100 KiB mean. When that keyword is on, the runtime emits it instead of `AllocationTick`.
  `dotnet-trace` 10.0.750501 (`e79b65bb…`) ran on CPUs 0–1. The job rejects any trace that outlasts its window, and none did.
- **PID and roles.** The PID check requires `/proc/PID/exe` to be dotnet with `TerraBase.E2E.dll` in its argv. The analyzer
  rejects events from any other PID; there were 0 foreign events and 0 lost events in every trace.
  Each sample is classified by its whole managed stack:
  - **server:** the `terrabase` module, ASP.NET Core, Microsoft.Extensions, TerraScale libs, `TerraBase.Testing` doubles, or
    the benchmark's attribution frames.
  - **client:** System.Net.Http, `ApiTarget` or the benchmark loop.
  - **mixed:** both. This bucket held 0–1 samples per trace.
  - **runtime:** neither (thread-pool dispatch, the socket event loop, timers).
- **Views.** *Entry* is the root-most frame of the role. *Owner* is the deepest application frame; it includes the
  framework/BCL frames below it. Both views add up within a role. *Inclusive* rows do not add up.
- **Analyzer.** `api-trace-attribution.cs` was built under the repository analyzers (0 warnings, 0 errors) against TraceEvent
  3.2.5 (`92401392…`). Both final analyses used the same source, `fff067dd…` (commit `3f2e53bce`).
- **Quiet control.** Every job held the other three heavy slots, and no job was stopped. External CPU over 456 samples per
  job: p1 mean 3.02%, p95 7.92%, max 91.25%; p2 mean 2.79%, p95 8.91%, max 88.08%. The maxima are single-second T3 `node`
  bursts. The predeclared criteria (mean ≤25%, p95 ≤100%) pass.

## Profiled cells (perturbed: ~10 of 45 s traced, ~6 s more alloc-traced)

| Run | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | Server p50/p99 µs | Engine p50 µs | Overhead p50/p99 µs | errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| p1-a | 1 | 6044 | 154.9/318.5 | 221.2 | 30206 | 91.6/244.2 | 4.1 | 87.5/172.6 | 0 |
| p1-b | 1 | 6022 | 155.4/319 | 222.2 | 30215 | 91.4/235.1 | 4.0 | 87.4/174.2 | 0 |
| p2-a | 1 | 6045 | 154.9/322.1 | 221.6 | 30267 | 90.3/238.4 | 4.1 | 86.1/170.4 | 0 |
| p2-b | 1 | 6038 | 154.7/317.6 | 221.2 | 30253 | 90.2/238.1 | 4.1 | 86.0/169.2 | 0 |
| p1-a | 16 | 34292 | 355.7/2092.9 | 200.0 | 29967 | 115/1633.9 | 5.2 | 109.6/1605.5 | 0 |
| p1-b | 16 | 33838 | 357/2225.6 | 200.4 | 29974 | 114.7/1753.3 | 5.1 | 109.4/1726.8 | 0 |
| p2-a | 16 | 34737 | 352.2/2073.2 | 197.6 | 30025 | 112.5/1586.4 | 5.1 | 107.2/1560.1 | 0 |
| p2-b | 16 | 33804 | 358.2/2199.9 | 201.4 | 30039 | 114.1/1697.6 | 5.1 | 108.7/1662.1 | 0 |

These values sit close to the untraced keyed-keyless arms of the same binary. They are not uninstrumented timings.

## Scheduler and CPU, untraced 5 s interval (/proc schedstat)

| c | Process on-CPU (CPUs of 10) | Run-queue wait (thread-eq.) | TP workers | Involuntary switches/s | Socket thread run-queue wait |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1.36–1.37 | 0.00–0.10 | 8–9 | 128–155 | 0.000 |
| 16 | 6.93–7.05 | 0.68–0.72 | 12 | 16,822–17,585 | 0.216–0.221 |

At c=16, ~0.7 thread-equivalents of process threads wait runnable. Divided by ~34k requests/s, that is ≈20 µs/request of
run-queue wait. The overhead p50 rises by +20–24 µs from c=1 to c=16. The two agree but are not joined per request.

## GC (traces; the alloc traces have no sampler suspensions)

| c | Trace | gen0 GCs/s | GC pause share of wall | Pause p50/p99 ms | Suspend phase share | Promoted from gen0 avg | Runtime time-in-gc |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | alloc (4) | 120–124 | 18.0–19.6% | 1.46–1.63 / 1.96–2.06 | 0.26–0.27% | 218–353 KB | 15.7–16.5% |
| 16 | cpu (2) | 114–117 | 16.4–17.7% | 1.42–1.57 / 1.89–1.95 | 0.18–0.19% | 247–316 KB | 15.1–16.0% |
| 1 | alloc (4) | ~21.7 | 1.55–1.61% | 0.69–0.72 / 1.08–1.17 | 0.02% | 121–133 KB | 1.0% |

- At c=16 the process allocates ~1.0 GB/s (the alloc-rate counter: 0.94–1.0 GB/s, ≈29.5 KB/request).
- The gen0 budget is ~8.2 MB, so a blocking gen0 collection runs every ~8 ms.
- Pauses are GC work, not thread suspension, and they grow with gen0 survivors. Survivors roughly double at c=16, which fits
  in-flight request state.
- About 18% of wall time is world-stopped. The c=16 overhead p99 (1.56–1.73 ms) sits at pause p50 plus one request.
- An inference, not a per-request join: ~(0.11 + 1.5)/8.2 ≈ 20% of requests overlap a pause. This is consistent with GC contributing substantially to the c=16 tail; it does not identify
  which requests paused, and it does not explain the median. At c=1, pauses cover 1.6% of wall time and the p99 stays at ~170 µs.

## CPU: server request-path stage chain (sampled thread-µs per request, inclusive)

Sampled managed thread time, not CPU. It includes runnable-but-preempted time and is missing during GC pauses. Ranges cover
all four processes.

| Stage (inclusive frame) | c=1 | c=16 | Slice to the next row at c=1 (what runs there) |
| --- | ---: | ---: | --- |
| Kestrel `HttpProtocol.ProcessRequests` | 111.9–113.3 | 146.2–150.1 | ~24: parsing, hosting context/activity, identity, private responses, pressure |
| `UseTerraBaseUsage` | 87.6–88.7 | 116.9–120.7 | ~10: bad requests, legacy routes, routing, CORS, listeners, QoS memory, topology |
| `UseTerraBaseCatalogGate` | 77.4–78.4 | 99.0–103.6 | ~22: catalog gate, then `UseAuthentication` (token validation, key path) |
| `UseTerraBaseProjectCheck` (b__8_0) | 55.1–56.4 | 75.6–79.4 | ~13: project check, authorization |
| `UseTerraBaseQos` | 41.9–43.6 | 60.2–62.5 | ~11: QoS admission, project catalog |
| `UseTerraBaseDatabasePlacement` | 31.4–32.2 | 44.4–45.9 | ~11: placement, route validation, endpoint dispatch |
| `GetDocumentEndpoint.HandleAsync` | 19.9–20.6 | 30.0–30.7 | endpoint, including the engine (~4–5 µs wall) |

- From c=1 to c=16, every stage inflates by about the same ×1.33. That matches run-queue and preemption wait (above), not
  extra work in one stage.
- No stage dominates. The largest single c=16 owners (p1-a, thread-µs/request) are:

  | Owner | µs/request |
  | --- | ---: |
  | Kestrel `SocketSender.SendAsync` (the send syscall) | 23.3 |
  | CatalogGate (includes framework code below it) | 17.5 |
  | Usage `CountingStream.WriteAsync` (includes the Kestrel write) | 12.5 |
  | ProjectCheck | 12.1 |
  | `TursoStorageEngine.OpenDatabaseAsync` | 9.1 |
  | `ProjectAccess` | 7.0 |
  | Usage middleware | 6.5 |
  | `ETags.Version` | 6.4 |
  | `RequestLogRecorder.Record` | 5.9 |
  | Auth handler | 5.6 |
  | `Conditional` | 5.3 |
  | `QosAdmission.TryEnter` | 5.2 |
  | Benchmark `AttributedDocuments` | 4.8 |
  | Hosting `CreateContext` | 4.5 |
  | `EntityIds.New` | 4.3 |

- Client CPU was 35–37 thread-µs/request. Unattributed runtime work (thread-pool dispatch and the socket event loop) adds
  ~30 more.

## Allocation: unbiased randomized sampling (p2, sampled mode)

Server allocation is 21.98–22.02 KB/request and client 7.56–7.61 KB/request. Both are the same at c=1 and c=16. Server owners
at c=16 (p2-a; p2-b within ~5%):

| Owner (includes the framework code below it) | B/request |
| --- | ---: |
| `UserAuthenticationHandler.HandleAuthenticateAsync` | 2305 |
| `UseTerraBaseProjectCheck` b__8_1 | 2079 |
| `UserAuthenticationHandler.Success` | 2031 |
| Hosting `StartActivity` (entangled with the benchmark's ActivityListener) | 1608 |
| CatalogGate (DI `ResolveService`, `AuthenticateAsync` below) | 1192 |
| `GetDocumentEndpoint.Conditional` | 927 |
| `UseTerraScaleRequestIdentity` | 783 |
| Usage middleware (+ `Transfer` ctor 292, `Complete` 191) | 748 |
| `QosContext.Enter` (+ `Scope.Dispose` 155) | 545 |
| Kestrel `ParseTarget` (path and query strings) | 535 |
| `EndpointJson.WriteResponseAsync` | 525 |
| `DocumentRoute.CheckParameters` | 492 |
| `ClientCors.IsDataPath` (leaf `String.Split`; inlined as `UseTerraBaseClientCors` b__3_2 in process b) | 448–498 |
| `UseTerraBaseLegacyRoutes` (mostly routing `DfaMatcher` below it) | 459 |
| `OpenSystemDatabaseAsync` (Turso 164 + Stamping decorator 192; closure ~4.5–5.1 objects/request) | 356 |

- The auth/project family (above plus `ProjectAccess`, `KeyWatch` and `TsAuthKeyAuthority`) is ≈8 KB/request, ~36% of
  server allocation.
- By type: strings 4.3 KB, byte[] 1.7 KB, Claim 1.13 KB (14 objects/request), ExecutionContext 0.64 KB (~16/request), and
  AsyncLocal value maps ≥0.56 KB.

The `AllocationTick` view (p1) has the same totals (server 22.2 KB) but misranks small objects at allocation-context
boundaries. It put `<>c__DisplayClass147_0` (32 B) at 1,882–1,972 B/request, about 59 objects. Unbiased sampling gives
143–164 B/request, about 5 objects, so the tick view is ~12× too high. Tick-based per-type rankings, including earlier
allocation reports, should not be trusted for small types.

## Ranked mechanisms

1. **c=16 tail (p99 overhead ~1.6 ms): consistent with blocking gen0 GC pauses, 16–20% of wall time.** The pauses occur with ~30 KB/request spread
   across about 40 owners, and per-pause cost grows with in-flight survivors.
2. **Median overhead at c=1 (~86 µs): request-path work across the middleware pipeline.** The six broad slices between the
   inclusive frames above are about 10–24 sampled thread-µs each; a slice can contain several middleware stages. No single
   owner is above ~25%. The largest slices are hosting/identity, catalog gate plus authentication, and the endpoint.
3. **c=16 median increment (~+21 µs): consistent with scheduler run-queue wait.** The process has ~0.7 thread-equivalents waiting with 12 TP workers on 10
   CPUs and ~17k involuntary switches/s, plus inflation from GC catch-up.
4. **The key path:** ~2.5–4.4 µs and ~1.65 KB/op (keyed-keyless). It is real but small.

## Objections and limits

- **What the samples measure.** SampleProfiler samples are safepoint-biased thread time at 566 (c=16) and 880 (c=1) per thread
  per second, not 1 kHz. They include runnable and preempted time, and none are taken during GC pauses. Leaves are unreliable:
  `Thread.PollGCWorker` and P/Invoke returns take ~38% and ~8% of server leaves. Rankings therefore use owner and inclusive
  frames.
- **Owner semantics.** An owner includes the framework frames below it: LegacyRoutes includes routing, and CatalogGate includes
  `UseAuthentication`. Inclusive frames are not additive.
- **One process, two sides.** Client and server share the process. Thread-pool dispatch and socket-loop work stay
  unattributed. The benchmark's instrumentation is ~0.9% of CPU samples, and hosting Activity creation may be partly caused by
  its listener.
- **Measurement boundaries.** The [source audit](2026-10-28-perf-api-measurement-boundaries.md) records the fixed warmup,
  process-wide CPU/allocation boundaries and per-request reservoir semantics. It supplies no measured correction and does
  not establish the cause of the slow first-cell state observed in a later experiment.
- **Allocation views.** The sampled and tick allocation views come from different processes and intervals, because enabling
  sampling disables ticks. In p2 the analyzer rejects the empty tick view; this is recorded in `rejected.txt`.
- **Scope.** Single host (beta, 10 pinned CPUs, nice 19), not live gru/sjc/fra. The binary is the frozen `93b873792` build, not
  main `427ddf231`. The link from pauses to latency is inferred from coincident magnitudes, not joined per request.
- **The target.** These profiles establish no change that reaches <10% of a ~5 µs engine. That target requires per-request
  overhead of about 0.5 µs on the same denominator.

## Candidates proposed at profile time (not implemented in this experiment)

1. **Make `ClientCors.IsDataPath` allocation-free.** Use span segment matching with the same predicate: ≥7 segments, empty
   first segment, non-empty segments 3 and 5, case-insensitive `v1/projects/*/databases/*/(collections|transactions|changes)`.
   - Supported effect: about −0.45 to −0.50 KB/op (~1.6% of process B/op), on every client request.
   - Expected latency effect: ~1.6% fewer gen0 GCs, so ~−0.3 pp of pause share at c=16. That is likely below this harness's
     latency resolution.
   - Qualification: three quiet same-host B/op pairs, plus integration coverage of CORS on data and non-data paths.
2. **A second isolated candidate:** a non-async cached fast path for `OpenSystemDatabaseAsync` and its Stamping decorator,
   about −0.36 KB/op.
3. **A human decision first:** only an aggregate allocation cut, or a GC budget/mode change, moves mechanism 1. The csproj's
   workstation-GC reason names 512 MB Fly machines, but the platform is now OCI. The decision should be made before any
   benchmark-only GC diagnostic arm.

## Artifacts (beta, managed workspace `fix-api-common-overhead-profile-33d7c422…`, `benchmark/bench/common-profile-{p1,p2}`)

There are ~1 GB of traces in total; they stay on beta. Each directory holds `traces.sha256`, the `proc.jsonl` snapshots,
`cells.md`, the logs, the observer output, the reservation and the analysis outputs (p1 `analysis-10`, p2 `analysis-3`, with
`artifacts.sha256`).

| Artifact | SHA-256 |
| --- | --- |
| p1-a small-c16 CPU / alloc trace | `06682e11a320772777783c8387e45defa55f1c79b984e7c35f92755450765051` / `c4427b386cf93f9eac948446607d50ade45ac3d2cea66504203e6bcdee94e7b7` |
| p1-b small-c16 CPU / alloc trace | `80a2fa37d2677ce22c6f26626a4aba42da88079211d680eddef2c3235b3fb93f` / `d44591fd5438fd8281888038ee4f225e379b14ef4b8ce325af8ac2de04753cf6` |
| p2-a small-c16 CPU / alloc-sampled trace | `606cf3238a20313be334442ea392794fa2b0632f6f61ec4d365126460bb734db` / `f18447d7a0f971080eeac135293af29339256cbc41bcbbef55335934a3e6fb72` |
| p2-b small-c16 CPU / alloc-sampled trace | `d4c87da1a5f7a41b8e2e1b84b15e6f74344fed9e6c818275d7d039fa4169f15c` / `9b2e1dc4fa3345d6e8152581115fcf8835291d191fa1aa3ded1f85e0a6c26817` |
| Analyzer source (both final analyses) | `fff067dd1ece902d4b978cbc7213dd54b4a4d33c73ee9f63d9a05a03b66bd539` |
| p2-a small-c16 sampled / CPU analysis | `100ca6fa1d529f2e6b7943f19b4f73dcb8ac18d622c1267f1390c21a43a69f2e` / `ecf2bab5f44737eda005bc77455245df415101b596dea4c392944161f76dbeda` |

Reproduce with:

- `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-common-profile-20261028.sh <label> --repo terrascale --branch fix/api-common-overhead-profile`
- `… api-common-profile-analyze.sh <label> <attempt> …`

## Publication provenance

This report preserves the data from experiment commit `c400ebc63a77db88c0a199e010b230818f2615ec`.
The measured artifacts and historical source hashes above are unchanged. Publication edits clarify that GC and scheduler
causality were inferred, and that the inclusive slices each contain multiple middleware stages. The publication
branch contains this report and the companion measurement-boundary source audit; it does not integrate the experimental profiling tools.
Their exact sources remain available for review and reproduction:

- [Collection and quiet reservation](https://github.com/TerraScale/terrascale/blob/c400ebc63a77db88c0a199e010b230818f2615ec/benchmark/scripts/api-common-profile-20261028.sh).
- [Within-window collection](https://github.com/TerraScale/terrascale/blob/c400ebc63a77db88c0a199e010b230818f2615ec/benchmark/scripts/api-common-profile.py).
- [Trace attribution analyzer](https://github.com/TerraScale/terrascale/blob/c400ebc63a77db88c0a199e010b230818f2615ec/benchmark/scripts/api-trace-attribution.cs).
- [Analysis build and rejected-view recording](https://github.com/TerraScale/terrascale/blob/c400ebc63a77db88c0a199e010b230818f2615ec/benchmark/scripts/api-common-profile-analyze.sh).

No traces or workloads were rerun while preparing this publication. New before/after
experiments must build their own matched baseline and candidate; the frozen binary is
historical diagnostic evidence.
