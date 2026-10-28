# Deployed TerraBase runtime budgets and GC configuration, gru/sjc/fra (read-only audit)

Diagnostic only. This report records deployment constraints for a later decision. It changes nothing, launches no
experiment and makes no production policy decision. The original target, server overhead <10% over the engine with the
engine as denominator, is unchanged and **still unmet**. Nothing here shows that a runtime or GC adjustment can meet it.
Server overhead is ~84–112 µs over a 4–7 µs engine
([common profile](https://github.com/TerraScale/terrascale/blob/944447a26bd5d3d309452fad425566934968095b/benchmark/bench/2026-10-28-perf-api-common-profile.md)
branch `fix/api-common-overhead-evidence`, frozen source `93b873792`), so meeting the target needs ≤0.4–0.7 µs of overhead.
This audit establishes no GC change or other adjustment that reaches that target.

## Scope and method

- **When.** 2026-10-28, 12:50:34–12:52:53 UTC: three passive snapshots per host, ~60 s apart.
- **Access.** Only `ts-orch netbird ssh`, from delta, through the operator wrapper the earlier read-only diagnostics used.
  Hosts: peers `ts-fra-1`, `ts-gru-1` and `ts-sjc-1`. gru's peer reports hostname **`ts-gru-2`**. Each region has one host
  here. Other hosts in a region, if any, were not inspected.
- **Reduction on the host.** A Python reducer ran with `sudo -n python3 -` on each host. It returned only the following:
  - `/proc/<pid>/status` sizes and thread counts, plus thread-name histograms (digits collapsed).
  - `/proc/<pid>/stat` CPU and start time.
  - The cgroup v2 chain from leaf to root: `cpu.max`, `cpu.weight`, `cpuset.cpus.effective`, `memory.{max,high,current,peak,swap.max}`,
    `pids.max`, plus selected `cpu.stat` and `memory.stat` keys.
  - The `/proc/<pid>/mountinfo` cgroup entries.
  - Environment **names** matched against a `DOTNET_|COMPlus_` GC/processor/tiering allowlist, case-insensitive. Values were
    printed only for matches; every other variable was counted and dropped.
  - The `System.GC.*` and `System.Runtime.Tiered*` properties and the `0.1.0+<sha>` informational version, scanned in place
    in `/proc/<pid>/exe`. Nothing was copied off the host.
  - `ctr -n terrascale containers ls` (ID and image) and `ctr -n terrascale tasks ls` (status).
  - Loopback `GET :8080/healthz` and `GET :8080/readyz` (status field only), and `:9091/metrics` filtered to `dotnet_*` and
    four `terrabase_*` memory gauges.
- **Not done.** No profiler, EventPipe or diagnostic client, no restart, deploy or write, no tenant, no peer or other
  credential, no journals, no command lines, no whole environments and no raw secret files. The held database
  `api-perf-01m610ttq4n8ew5y4q25aj5sc2` was not looked up.
- **Stale sources not used as evidence.** The local ts-orchestrator checkout `4b66d3cdb` (sample profiles: CPU 1, memory 4 GiB)
  was read only for the NetBird vault ID. The live CPU 1 / 4 GiB values below come from cgroupfs, not from that checkout.

## Deployed revision, image and health

| Region | Host | TerraBase revision (embedded) | Image (running container) | Process age | healthz / readyz |
| --- | --- | --- | --- | ---: | --- |
| fra | ts-fra-1 | `6931b0b57cef86e7852966409238a734f0584b45` (#823) | `ghcr.io/terrascale/terrabase@sha256:dd8b4ebef9722210e88d5836f2f1d2bd40e02f5e76c68930a97ec2fe90d68982` | 7,240 s | 200 ok / 200 ready |
| gru | ts-gru-2 | `6931b0b57cef86e7852966409238a734f0584b45` (#823) | `…@sha256:dd8b4ebef9722210e88d5836f2f1d2bd40e02f5e76c68930a97ec2fe90d68982` | 7,177 s | 200 ok / 200 ready |
| sjc | ts-sjc-1 | `e3a778f7c18a2b05338517bbcc795d6384b6bbf8` (#818) | `…@sha256:697917b65e851fe4f7d0d3e6bf4274eec08daa71d5633dd1e7ee1f662ed4c9ce` | 5,768 s | 200 ok / 200 ready |

- The regions run mixed revisions. `e3a778f7c` is two commits after `6931b0b57`: #777 (Postgres statements) and #818.
- The files that set these budgets are identical from both deployed revisions to `origin/main` `05e7adb5b`:
  `TerraBase.csproj`, `TerraBaseOptions.cs`, `Telemetry.cs` and `Dockerfile`.
- Exactly one `terrabase` process runs on each host. Older `terrabase` containers are listed by `ctr`, but none has a running
  task.

## Budget facts per region (do not equate these layers)

| Layer | fra | gru (ts-gru-2) | sjc |
| --- | --- | --- | --- |
| Host online CPUs / MemTotal / swap | 2 / 11.65 GiB / 0 | 4 / 23.41 GiB / 0 | 2 / 11.65 GiB / 0 |
| Affinity and `cpuset.cpus.effective` | 0-1 | 0-3 | 0-1 |
| Leaf cgroup `cpu.max` (quota) | `100000 100000` = **1 CPU** | **1 CPU** | **1 CPU** |
| Parent `/terrascale` and `/system.slice` `cpu.max` | `max` (no hierarchical cap) | `max` | `max` |
| Leaf `memory.max` / `memory.high` / `swap.max` / `pids.max` | **4 GiB** / max / 0 / 256 | 4 GiB / max / 0 / 256 | 4 GiB / max / 0 / 256 |
| Leaf `memory.current` / `memory.peak` | 2.08 / 2.29 GB | 2.80 / 3.04 GB | 1.96 / 2.10 GB |
| `memory.stat` anon / file (incl. shmem) | 1.56 / 0.51 (0.13) GB | 2.30 / 0.44 (0.14) GB | 1.51 / 0.40 (0.13) GB |
| Process RssAnon / VmHWM / threads | 1.52 GB / 1.80 GB / 52 | 2.25 GB / 2.57 GB / 59 | 1.47 GB / 1.77 GB / 50 |
| GC committed (`dotnet_gc_last_collection_memory_committed_size_bytes`) | 0.154 GB | 0.178 GB | 0.135 GB |
| Runtime `dotnet_process_cpu_count` (= `Environment.ProcessorCount`) | **2** | **4** | **2** |
| App `terrabase_pressure_memory_budget_bytes` (`options.MemoryBudget`) | 3,126,672,384 | **6,284,313,600** | 3,126,672,384 |
| App `terrabase_memory_reserved_bytes` | 746 MB | 11 MB | 715 MB |
| CPU used since start (CPU-s per wall s) | 0.975 | 0.978 | 0.987 |
| CFS periods throttled since start | 94.2% | 96.7% | 96.6% |

### What the layers show

**No cgroup filesystem is visible in the container.**
- The container has no cgroup mount (`mountinfo` has no `cgroup` entries), and `/sys/fs/cgroup/*` is absent in its root.
  Its cgroup namespace is the host's.
- The observed runtime CPU count and application memory budget do not reflect those limits:
  - `ProcessorCount` equals the host CPUs (2/4), not the 1-CPU quota.
  - The memory budget is exactly MemTotal/4 on every host. For example, fra: 12,213,564 kB × 1024 / 4 = 3,126,672,384.
- The default `DefaultMemoryBudget` path (`TotalAvailableMemoryBytes / 4`) is consistent with a runtime view of **host RAM**.
  The exact match does not independently prove that this default path was taken: no `TERRABASE_*` value or launch argument
  was read. The reported budget exceeding the enforced limit on gru is directly observed regardless of its derivation.
- One inference is not observed: the GC probably has no container-derived heap hard limit either. No metric exposes that limit.

**Budget mismatch on gru (objection, not fixed here).**
- gru's app memory budget (6.28 GB) is higher than its cgroup `memory.max` (4.29 GB).
- Pressure utilisation is computed against the 6.28 GB figure: working set 2.62 GB, so 42%. The cgroup is already at 65–71%.
- A budget above the kernel limit cannot alone provide headroom against that limit. Whether the kernel OOM path or an
  application refusal occurs first also depends on actual reservations, untracked memory and other pressure mechanisms;
  this audit does not establish the next failure path.
- fra and sjc budgets (3.13 GB) are 73% of `memory.max`; this observation does not establish that budget derivation accounts
  for the container limit.

**`StorageThreads` default.** It is `Math.Clamp(Environment.ProcessorCount, 1, 1024)`, so 2 (fra/sjc) and 4 (gru) storage
threads share a 1-CPU quota. This is inferred from source plus the observed `ProcessorCount`. No configured value was read.

**CPU-bound under quota.**
- Every TerraBase has used ~0.93–0.99 CPU-s per wall second since start, and 94–97% of 100 ms periods were throttled.
- The workload behind that CPU was not identified. It is not attributed to API requests.

**Host sharing.** The `/terrascale` children compete on top of `system.slice` (fra: 6.7 GB current, mostly page cache).
Quotas are limits, not reservations, so oversubscription is possible:

| Host | Containers under `/terrascale` | Sum of running quotas |
| --- | --- | --- |
| fra | TerraBase (1 CPU / 4 GiB), ts-dashboard (2 / 2 GiB), app-canary and whoami (0.05 / 32 MiB each) | 3.1 CPUs on 2 |
| sjc | Same set as fra | 3.1 CPUs on 2 |
| gru | TerraBase, ts-auth (2 / 2 GiB, 0.14 GB current), ts-dashboard (2 / 2 GiB), whoami, one running netbird-server (1 / 4 GiB), two `CREATED` netbird-server, one container-less cgroup (0.5 / 2 GiB, 4 KB) | 6.05 CPUs on 4 |

ts-auth ran only on the gru host among the three inspected.

**Memory accounting.** The GC committed metric at the last collection is ~0.13–0.18 GB, under 10% of anonymous RSS
(1.5–2.3 GB). These are different measurements and observation boundaries, not a complete managed/native decomposition.
The gap is consistent with substantial native/runtime memory, but is not attributed to Turso/SQLite, temporary storage,
stacks or code heaps here. Any GC experiment must measure total `memory.current`, not only the managed metric.

## GC configuration: requested/configured vs observed

| Item | Evidence | Label |
| --- | --- | --- |
| `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=true`, `TieredPGO=true`, `PublishReadyToRun=true` | `src/TerraBase/TerraBase.csproj:19-24` on `05e7adb5b` | source |
| csproj comment "a 512 MB machine gets no per-core server heaps" | `TerraBase.csproj:22` | source; the comment does not match deployment (1 CPU, 4 GiB) |
| Embedded JSON contains `System.GC.Server=false`, `System.GC.Concurrent=true`, `System.Runtime.TieredPGO=true` | bytes in the running `/proc/<pid>/exe`, all three regions | consistent with the source configuration; the collector scans matching JSON and does not parse the bundle's runtimeconfig entry |
| No `DOTNET_*`/`COMPlus_*` GC, processor or tiering variable (0 matches; 22 other names omitted) | allowlisted environment view, all three regions | **configured**: no override |
| One `.NET BGC` thread, no other GC-named threads, 50–59 threads total | thread-name histogram | **passive indication** consistent with workstation concurrent GC |
| `GCSettings.IsServerGC` | no existing metric or endpoint exposes it | **unverified** |

A missing override is not proof of the runtime mode. The thread-name observation fits workstation concurrent GC: server GC
would create per-heap GC threads, and with `ProcessorCount` at 2/4 there would be more than one. It is still not a
`GCSettings` read.

**Dockerfile, CI vs deployment.**
- `Dockerfile:11-16` sets `DOTNET_PROCESSOR_COUNT=8` and `DOTNET_GCHeapCount=8` in `base`.
- The comment says publish/runtime stages "have their own base". That holds for the runtime stage (`runtime-deps` chiseled,
  `Dockerfile:152`) but not for publish: `publish-terrabase` derives from `source → restored → base`, so the publish-time SDK
  processes run with those variables.
- This affects build-time processes only. The deployed binary's runtimeconfig has no heap-count property, and the deployed
  environment has neither variable. The CI test containers (8 CPUs, 8 heaps requested) are therefore not the deployment
  envelope. Comment accuracy objection only; nothing changed.

## Deployed GC and CPU signals (passive counters, not per request)

| Region | Window | CPU-s per wall s | Alloc MB/s | Alloc MB per CPU-s | GC pause share of wall | GCs/s (gen2/s) | Mean pause |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fra | since start, 7,240 s | 0.975 | 84.4 | 86.6 | 5.68% | 22.0 (0.46) | 2.58 ms |
| fra | 12:51:41–12:52:48 | 0.94 | 96.5 | 103 | 6.3% | 24.1 | — |
| gru | since start, 7,177 s | 0.978 | 57.0 | 58.2 | 7.01% | 14.4 (0.30) | 4.88 ms |
| gru | 12:51:47–12:52:49 | 0.93 | 39.7 | 42.8 | 5.2% | 10.9 | — |
| sjc | since start, 5,768 s | 0.987 | 80.6 | 81.7 | 6.21% | 21.0 (0.45) | 2.96 ms |
| sjc | 12:51:55–12:52:53 | 0.92 | 85.4 | 93.1 | 7.2% | 21.5 | — |

- **Window precision.** Window bounds are the reducer start times. Metrics were scraped seconds later, after the in-place
  executable scan, so window rates are ±~10%. The since-start rows use exact process start times.
- **Pause time is wall time.** CFS throttling can extend it. Mean pauses of 2.6–4.9 ms (profile p50: 1.5 ms on unthrottled
  CPUs) cannot be split into GC work and throttle wait with these counters, which are not joined per collection.
- **Gen2 collections run every ~2–3 s** (2.1–2.6% of GCs). Their cause (budget, induced or native pressure) was not determined.
- **Lock contentions.** gru recorded ~2.0 M lock contentions since start; fra and sjc recorded ~59–77 k. Not diagnosed.

## How the profiled 10-CPU loopback differs from deployment

| Dimension | Common profile (`93b873792`, synthetic) | Deployed (`6931b0b57` / `e3a778f7c`) |
| --- | --- | --- |
| CPU | `taskset -c 2-11`: 10 CPUs, no quota; c=16 used 6.9–7.1 CPUs | Visible 2/4 CPUs, **1-CPU quota**, 94–97% of periods throttled |
| Runtime view | Not recorded in the profile | Blind to its cgroup: `ProcessorCount` 2/4, memory budget from host RAM |
| Memory | No `memory.max` recorded | `memory.max` 4 GiB, swap 0, `pids.max` 256; anon 1.5–2.3 GB mostly native |
| Process topology | Client and server in one process (client ~7.6 KB/req, 35–37 thread-µs/req) | Server alone; clients remote through the edge |
| Load | Read-only synthetic loop, c=1/16 | Unknown mix; ~1 CPU continuously |
| GC pause share | c=16: 16–20% of wall; c=1: 1.6% | 5.2–7.2% of wall, with throttling mixed in |

- The profile's c=16 regime (~7 CPUs, ~34k ops/s, ~1 GB/s allocation) cannot occur on one deployed node: one CPU caps the
  node first.
- Under a 1-CPU quota, fewer allocated bytes may reduce GC work, but their net CPU effect still needs measurement. More
  active GC threads would share that quota; the effect on throttling and latency is not measured here.
- A GC result from the 10-CPU loopback therefore does not transfer to deployment.

## Proposed offline experiment acceptance matrix (not launched, not authorized here)

**Invariants for every arm:**
- The same server binary. Hash `terrabase`/`terrabase.dll` and the runtimeconfig, and record them.
- The same harness commit, workload, warmup, window, concurrency set and engine-p50 denominator.
- Arm order alternates (A0 B A0 B …), with at least 3 repeats per arm on a quiet, reserved host.
- Only GC runtime environment variables differ between arms. They are diagnostic arms in a test binary's process
  environment, never a shipped setting or deployment knob.
- Any adopted mode would be a reviewed code change with one correct value.

**Envelopes:**

| Envelope | CPU | Memory | Runtime view | Purpose |
| --- | --- | --- | --- | --- |
| **E1 current deployment diagnostic** | Affinity to 2 CPUs, 1-CPU CFS quota | `memory.max` 4 GiB, swap 0, `pids.max` 256 | No cgroup mount in the process view, matching this deployment snapshot | Reproduce the observed envelope; insufficient alone for a production decision |
| E1-gru | Affinity to 4 CPUs, 1-CPU quota | Same as E1 | Same as E1 | gru's `ProcessorCount=4` |
| E0 profile-continuity | `taskset -c 2-11`, no quota | Same as E1 | Same as E1 | Comparison with the common profile only; never decision-grade |

The load client runs in a separate process on CPUs outside the server's set, so client allocation is not counted against
the server. This requires a separately qualified harness; the existing in-process loopback benchmark does not provide
that topology. A resource-discovery fix would also require a new matched baseline with the corrected runtime view.
These are design prerequisites, not an executable or approved experiment plan.

**Arms:**

| Arm | Environment difference | Question |
| --- | --- | --- |
| A0 | none (workstation concurrent, as deployed) | baseline |
| A1 | `DOTNET_GCgen0size` raised (one value, pre-registered) | Do fewer gen0 GCs per allocated byte cut pause share and CPU under quota? |
| A2 | `DOTNET_gcServer=1` (runtime defaults, including DATAS) | Server GC cost under quota, and memory and threads with heaps derived from 2/4 visible CPUs |
| A3 | `DOTNET_gcServer=1`, `DOTNET_GCHeapCount=1` | Server GC without extra parallel GC threads competing for one CPU |

**Record per run:**
- ops/s and ops per CPU-s (cgroup `usage_usec`), and server-over-engine p50/p99 with the unchanged engine denominator.
- Engine p50, as a control that must stay within ±5% of A0.
- Pause share, GCs/s, mean/p99 pause, and allocated bytes per request.
- `nr_throttled`/`nr_periods`.
- `memory.peak`, anon, file, GC committed, RssAnon, thread count and errors.

**Acceptance for calling an arm "worth a code proposal":** every one of these must hold in E1 *and* E1-gru against
interleaved A0.
1. Overhead p99 improves with non-overlapping ranges across repeats, and p50 does not regress beyond A0's range.
2. ops per CPU-s does not regress by more than 2%.
3. `memory.peak` stays at or below A0 + 256 MiB and below 75% of `memory.max` (3 GiB), with native memory included.
4. Threads stay at or below A0 + 8 and well below 256.
5. Zero errors, and the engine-p50 control holds.

A pass shows only that the arm deserves a reviewed proposal. Even a passing arm leaves the <10% server-over-engine target
unmet. The discovery issue and its corrected envelope must be resolved before using this matrix for a production decision.
Recording target attainment separately is required.

## Findings and objections

1. **Runtime limit blindness.** The containers have no cgroup mount. .NET sees 2/4 CPUs and host RAM rather than 1 CPU and
   4 GiB; the observed budget matches host RAM/4 and the default worker count would follow the host CPU count. gru's budget is 6.28 GB
   against `memory.max` 4 GiB. This is a deployment-contract issue (ts-orch mounts and/or TerraBase budget derivation) and
   should be decided before any GC experiment. Nothing changed.
2. **Saturated, throttled quota.** All three nodes use ~1 CPU continuously and are throttled in 94–97% of periods. The load
   source is unknown. Any live comparison would be confounded until it is known.
3. **Stale comment.** The csproj workstation-GC comment cites 512 MB machines. Deployment is 1 CPU / 4 GiB on 2–4 CPU, 12–24 GB
   hosts. The Dockerfile comment about publish having its own base is inaccurate, with build-time effect only.
4. **Mode not observed.** `GCSettings.IsServerGC` is not observed. Configured values and thread names are consistent with
   workstation concurrent GC.
5. **Limited host coverage.** Only one host per region was inspected. Revisions are mixed: sjc runs `e3a778f7c`, fra and gru
   run `6931b0b57`.
6. The CORS 500 B and cached-open 356 B allocation cuts stay **HELD**. This audit adds no evidence for or against them.

## Publication provenance

The original report remains at `89a7e530f22fe5adf3101a8a3497ba4aab926995`. Publication edits narrow inference and
experiment claims; none changes the recorded limits, metrics or historical snapshots. Root independently compared the
reduced CPU, budget, mount, environment and health fields above and confirmed the four cited source files are identical
between both deployed revisions and main `05e7adb5b`. No additional live probe, experiment or test ran for publication.

Reduced artifacts remain private on delta in `/tmp/rtbudget/`. The initial region files contain a JSON object followed by
`=== EXTRA` and another JSON object; the `.2.json` files contain one object. They are not all single-document JSON files.

| Artifact | SHA-256 |
|---|---|
| `probe.py` | `6767884045289bcd32fee7f1457aac21479348ad3ddc4d4cae0bae9e04538c70` |
| `probe2.py` | `4fd95ab32809c3ff6f832071ca6c60fa6d036fecc7b6fa9f9faae7d82be7323f` |
| `fra.json` | `2c87ef4007b2fc516423a19d40447bcdca87479cd98258cb563dcd04e10e983b` |
| `fra.2.json` | `2f12b2b3dd53497525d45bbd0371145d9622715e5e5a5eac947a500e66e27fd7` |
| `gru.json` | `caeae7a6a323d75a1b7cfad1a26cc636a4dcf442208a252c8a5e6c170f89d444` |
| `gru.2.json` | `3b3cfcc939a4aa31f830ffc4b085fce406b87ab9dbf54e4487ad198fe53c3a3e` |
| `sjc.json` | `a0909654574c9da5a37c913797217604404d50427283158ed2ef23646ad4497e` |
| `sjc.2.json` | `804263e055779b40decd502fd3e5d6c94bb0a0861882b20243e00cfe9938d33a` |
