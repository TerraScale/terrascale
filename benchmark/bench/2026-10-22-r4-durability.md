# R4: remote-durable acknowledgement and shipper leases — 2026-10-22

The late-ACK deadline bug, observed-loss epoch reuse and missing takeover fence have regression tests and fixes in [PR #381](https://github.com/TerraScale/terrascale/pull/381). The source-linked waiter workload shows lower scheduling/allocation cost; the required five-repeat R0 end-to-end comparison remains incomplete after two HTTP 503 failures. No production or WAN speedup is claimed.

Validation excerpts, failure metadata and local-file checksums are retained in [2026-10-22-r4-durability-evidence.md](2026-10-22-r4-durability-evidence.md).

## Findings

Locations below refer to source baseline `e7fa366d`, unless marked candidate. Severity distinguishes correctness from performance/efficiency. Unresolved findings are retained explicitly; this review does not establish exclusive backup shipping or hardware power-loss qualification.

| File:line | Severity | Evidence | Fix or remaining work |
| --- | --- | --- | --- |
| `Features/Replication/RemoteAckWaiter.cs:122` | P1 correctness | Confirmation was checked before the elapsed deadline. With timeout callbacks held, an ACK after 10 seconds returned HTTP 201 instead of 503. | #381 stores a monotonic registration timestamp and rejects matching ACKs observed at/after the bound. The deterministic integration regression failed before the fix. |
| `Engine/Cluster/ClusterShipperLeases.cs:48` | P1 correctness | Expire the member lease, observe null, recover with unchanged membership revision: the old epoch is reused, allowing the backup worker to retain its previous generation. | #381 increments the database's local term counter after each observed loss. Real-server regression failed before the fix. |
| `Engine/Cluster/ClusterShipperLeases.cs:42` | P1 correctness | Removing the predecessor from the directory backdated the new owner's timer by the full fence; it shipped immediately. Directory disappearance alone does not prove that owner stopped. | #381 always waits the full fence. Real-server regression failed before the fix; existing membership/backup handover assertions retain the no-overlap interval. |
| `Features/Replication/RemoteAckWaiter.cs:77,138` | performance | Every ACK rotated a shared signal, waking unrelated waiters and recreating their timeout/cancellation registrations. Duplicate/wrong-incarnation ACKs also caused work. | #381 completes only matching waiters with one completion and one timeout registration each. Five-repeat measurements below retain noisy and batch workloads. |
| `Features/Replication/RemoteAckWaiter.cs:196` | efficiency | Tenant normalization allocated a record even when its plan was already Hobby. | #381 reuses already-normalized tenants. Other plans still normalize; the benchmark uses Hobby and does not quantify that other-plan cost. |
| `Engine/Cluster/ClusterShipperLeases.cs:25,31` | P1 correctness, unresolved | Expiry and renewal entirely between shipper polls can leave both `HoldsLease` and membership revision unchanged at the next call; the cached epoch survives. | Membership needs an observable continuity/generation identity on lease recovery. The observed-loss regression/fix does not cover this hidden interval. Membership files belong to the runtime lane. |
| `Engine/Cluster/ClusterShipperLeases.cs:21,25` | P1 correctness, unresolved | Different directory views can each name a different live member as primary indefinitely. Waiting a finite fence does not reconcile those views. | Exclusive ownership requires an authoritative fencing/consistency mechanism or a proven membership guarantee; neither is established here. |
| `Features/Backup/ContinuousBackup.cs:599` | P1 correctness, unresolved | `HoldAsync` precedes asynchronous snapshot/log work; uploads and side work can continue after the lease expires. | Backup ownership must cancel work and fence publication under a stale term. A check only before starting the upload is insufficient; outside R4 file ownership. |
| `Engine/Cluster/ClusterShipperLeases.cs:15,22` | efficiency, unresolved | `_terms.GetOrAdd` retains entries for every queried database, including nonowners; entries have no deletion/lifecycle cleanup. | Database churn can grow this dictionary without bound. Cleanup also needs a fresh-term identity after eviction; an arbitrary cap would risk epoch reuse. |

## Reviewed ordering, loss and resource bounds

Decision 0013a's current contract is the local durable copy plus one eligible database-holding machine in another placed region. Decision 0010 is marked superseded; its older all-active-peers/degraded-success behavior is not the implemented contract.

`StorageChangeGroupSink.cs:36–47` awaits each durable transaction commit before returning apply success. `PeerStreamReceiver.cs:506–534` constructs the live ACK after that apply. `PeerStreams.cs:805–840` checks live coverage, placement and incarnation before notifying the waiter. The real ApplyDurability integration tests verify FULL commits for each receiver chunk before ACK. These are implementation/integration observations, not a claim that disk faults or physical power loss are fully qualified; decision 0013a already records broader recovery qualification gaps.

The waiter retains exact `(from,to]` ranges for one incarnation, never treats an ACK as an arbitrary prefix, and handles early/duplicate ACKs within bounded history. Dropping an old retained range can produce outcome unknown, not false success. Existing integration coverage includes held/refused ACKs, wrong incarnation/database/region, cancellation, peer loss, admission bounds and transaction waits. The final local filter passed 46 tests; the existing continuous-backup handover test passed separately. The Make target also passed 22 vendored driver tests; no tests were skipped. A fresh run followed by 20 separate process repetitions also passed all three new regressions (60 repeated executions); per-repeat evidence is retained in the companion evidence file.

Waiter entries disappear in `finally`; admission disposal removes an empty database entry. Bounds remain 4,096 admitted attempts per machine, 256 per database and 16 retained ranges per database. One timeout per pending waiter remains; no timer wheel was introduced. ACK matching still scans up to 256 pending records under the shared gate. Eligibility checks on peer loss run outside that gate to avoid inversion with peer locks. The parallel admission self-check exercises 10,000 attempts, but is not a 10,000-writer throughput or lock-contention benchmark.

Both deadline and takeover elapsed times use each process's local monotonic TimeProvider timestamps. They do not compare wall clocks across nodes. That removes wall-clock adjustment dependence from these waits, but does not solve directory consistency or lease-continuity assumptions.

## Measurement identity and reproduction

All runs used beta: Linux x86-64, AMD Ryzen 5 PRO 6650H, 12 logical CPUs, SDK `11.0.100-rc.1.26425.128`, Release, `nice -n 19`. The host was shared with other replication work. R0 captured load averages around 20–45 during the earlier phases; a post-focused-run snapshot was `11.63 18.42 22.77`. Scheduling/GC variation is visible in the spreads; do not transfer these absolute numbers to production.

| Item | Identity |
| --- | --- |
| A app base | `e7fa366ddb289f5612c93d316b316fb92c568151` (runtime-off-Fly #339 included) |
| B app source commit | `1862bc11` (#381's later `785fac38` changes only the deterministic test timer) |
| Local A + R0 | `a4520b6cd4e4045b2e2e689e6fe3b1ac6cb07724`; TerraBase tree `d1f2f2931dd72b7489e0a85329121c2e6af1ffc7` |
| Local B + R0 | `bfe7d7ccc2edfe566ed4d6d9b5210508cac98804`; TerraBase tree `8471c5cdad65d515cea108eb49821f17874b77c4` |
| R0 harness | `7a96bf45691df2bf378b5f6061a7b1e6b4af45c1`, cherry-picked only into both local benchmark refs, never into the PRs |
| Focused harness | `8e15fa475b2386a1c1c2ea1d662192a166c171be`, [original PR #383, folded into #381](https://github.com/TerraScale/terrascale/pull/381) |
| A waiter SHA-256 | `7a5af1d7f9e62562c7b4a25630c23359626f87433c5d3061a790308b33801972` |
| B waiter SHA-256 | `79f3997aea813efa30b1a7ccd7a322b19f962f36dc15815850d2eddd1208475d` |
| SDK sibling | `ts-dotnet` at `df852e3b4d3a4df324452d46792b20566eea5136` |

Build the focused harness twice, overriding only `AckWaiterSource` to the A/B production file in each checkout; use separate artifact directories. Run the A DLL then the B DLL sequentially. Both builds passed warnings as errors. Both executions passed the self-checks: 10,000 parallel admission attempts admitted exactly 4,096; cancellation plus lease disposal allowed refilling all 256 database slots; attempt 257 was refused below the machine cap. This does not independently inspect the private pending list.

```sh
nice -n 19 dotnet build benchmark/dotnet/RemoteAckBench -c Release --artifacts-path /home/mariogk/.cache/r4-final-micro-before -p:AckWaiterSource="$PWD/.worktrees/r4-before/src/TerraBase/Features/Replication/RemoteAckWaiter.cs"
nice -n 19 dotnet build benchmark/dotnet/RemoteAckBench -c Release --artifacts-path /home/mariogk/.cache/r4-final-micro-after -p:AckWaiterSource="$PWD/.worktrees/r4-after/src/TerraBase/Features/Replication/RemoteAckWaiter.cs"
nice -n 19 dotnet /home/mariogk/.cache/r4-final-micro-before/bin/RemoteAckBench/release/RemoteAckBench.dll baseline
nice -n 19 dotnet /home/mariogk/.cache/r4-final-micro-after/bin/RemoteAckBench/release/RemoteAckBench.dll candidate
```

## Focused waiter results

Final A then B completed at 13:51:14Z. Each process ran five repeats per workload after warmup. The harness links production waiter/admission/marker/tenant types; its refusal record is a benchmark-local copy. It runs no transport, receiver, authentication or disk. All raw repetitions are in [2026-10-22-r4-durability-raw.md](2026-10-22-r4-durability-raw.md).

Latency is registration-to-continuation workload latency: later waiters include registration of remaining waiters and processing of earlier ACKs. The 100 ms profile injects one initial scheduling delay, not a WAN or per-message delay. CPU/allocation totals cover registration, ACK calls, observation continuations and release, divided by all ACK calls. Noisy workloads count wrong-incarnation and duplicate calls in that denominator; compare identical workloads only. Maximum pending is the constructed registration count, 256 or 4,096, not an instrumented production peak. Batch16 has 16 ACK calls for 256 waiters; the 16-database noisy workload has 12,288 calls.

Values are median [minimum, maximum], not confidence intervals. Percentiles are calculated separately within each repetition, then summarized; histograms are not pooled.

| profile | workload | variant | p50 ms | p99 ms | CPU us/ACK | bytes/ACK |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| in-process/no injected delay | 1 DB; individual; noise=False | before | 11.694 [2.367, 16.969] | 16.220 [9.524, 25.562] | 128.266 [57.129, 138.355] | 16606.800 [4136.600, 17386.700] |
| in-process/no injected delay | 1 DB; batch16; noise=False | before | 1.517 [0.979, 3.276] | 1.982 [1.000, 3.291] | 227.750 [105.312, 379.250] | 22050.000 [20363.000, 44767.500] |
| in-process/no injected delay | 1 DB; individual; noise=True | before | 47.270 [41.198, 57.859] | 64.395 [55.470, 82.418] | 103.079 [97.513, 114.410] | 14934.700 [11183.500, 17497.800] |
| in-process/no injected delay | 16 DB; individual; noise=True | before | 490.876 [473.203, 1236.640] | 610.976 [580.371, 1353.932] | 95.931 [88.222, 236.251] | 21615.400 [20722.900, 21980.300] |
| in-process/initial 100 ms delay | 1 DB; individual; noise=False | before | 105.389 [103.476, 107.238] | 106.846 [104.845, 110.138] | 100.398 [69.832, 237.797] | 16720.100 [15747.900, 17875.600] |
| in-process/initial 100 ms delay | 1 DB; batch16; noise=False | before | 100.579 [100.400, 101.281] | 100.743 [100.429, 101.553] | 179.250 [66.062, 537.125] | 27042.000 [23279.000, 39953.000] |
| in-process/initial 100 ms delay | 1 DB; individual; noise=True | before | 106.893 [105.208, 108.231] | 109.740 [107.316, 111.048] | 46.319 [44.660, 67.284] | 7668.900 [7249.600, 8115.600] |
| in-process/initial 100 ms delay | 16 DB; individual; noise=True | before | 511.693 [477.716, 589.317] | 608.115 [566.429, 701.134] | 92.542 [85.155, 97.247] | 21092.100 [20275.300, 21695.700] |
| in-process/no injected delay | 1 DB; individual; noise=False | after | 1.787 [1.659, 1.945] | 2.372 [2.156, 2.471] | 22.773 [20.652, 24.629] | 1029.200 [1029.200, 1029.200] |
| in-process/no injected delay | 1 DB; batch16; noise=False | after | 0.601 [0.524, 0.721] | 0.667 [0.572, 0.759] | 163.625 [138.500, 173.562] | 16466.500 [16466.500, 16466.500] |
| in-process/no injected delay | 1 DB; individual; noise=True | after | 3.792 [2.510, 4.497] | 5.173 [3.409, 5.976] | 12.003 [5.934, 15.049] | 342.900 [342.900, 343.100] |
| in-process/no injected delay | 16 DB; individual; noise=True | after | 70.301 [54.048, 79.916] | 111.350 [82.781, 124.202] | 13.027 [11.254, 16.677] | 341.900 [341.900, 342.000] |
| in-process/initial 100 ms delay | 1 DB; individual; noise=False | after | 102.127 [101.869, 102.852] | 102.691 [102.289, 103.342] | 25.648 [22.668, 380.371] | 1030.200 [1029.700, 1030.700] |
| in-process/initial 100 ms delay | 1 DB; batch16; noise=False | after | 100.588 [100.370, 101.022] | 100.713 [100.379, 101.118] | 761.062 [124.125, 6089.000] | 16475.500 [16475.500, 16498.000] |
| in-process/initial 100 ms delay | 1 DB; individual; noise=True | after | 100.920 [100.466, 101.055] | 101.221 [100.695, 101.367] | 4.460 [2.702, 16.715] | 343.200 [343.200, 343.200] |
| in-process/initial 100 ms delay | 16 DB; individual; noise=True | after | 111.711 [109.513, 113.295] | 120.222 [115.368, 123.127] | 2.761 [2.178, 5.510] | 342.000 [342.000, 342.000] |

At 4,096 pending/no injected delay, the measured median CPU changes from 95.931 to 13.027 us/ACK and allocation from 21,615.4 to 341.9 bytes/ACK. The delayed batch16 CPU median regresses from 179.250 to 761.062 us/ACK with a wide spread; this small denominator and shared-host scheduling prevent a universal CPU improvement claim. The reduction in unnecessary wakeups and allocations is measured in this isolated workload only.

## Required R0 loopback/WAN comparison: incomplete

Both local refs used the supplied command with `SCENARIO=writes N=1000 REPEATS=5`, authenticated gru/sjc/fra as three OS processes, 16 requests in flight and batch1/batch16. Every completed phase listed every region and compared each of 1,000 document hashes; completed-phase logs reported no missing/extra/mismatched documents. HTTP ACK latency includes admission, local commit, remote confirmation and any harness retries. Runtime CPU/allocation counters include background work and visibility probes.

```sh
git fetch origin feat/repl-benchmark-r0
# In each local A/B checkout only:
git cherry-pick 7a96bf456
TS_HEAVY_HOST=beta nice -n19 make -f benchmark/repl.mk bench-repl SCENARIO=writes N=1000 REPEATS=5 OUT=benchmark/bench/r4-loopback-VARIANT
```

The first sequential pair completed A, but B aborted during repeat 3. One full pair retry then aborted on unchanged A during repeat 2. The first A artifact was overwritten on retry and is excluded. Preserve the retained failures; do not report partial repetitions as a five-repeat comparison. After the second failure, reruns stopped and the parent was notified. Root cause is not established: the shared-host load is observed context, not proof that load caused the 503.

| Attempt / failing ref | UTC window | Completed phases before abort | Failure |
| --- | --- | ---: | --- |
| First pair / B | 13:46:13–13:47:41 | 4 (single + batch16, repeats 1–2) | `visibility write sjc failed: HTTP 503`; sjc→gru HTTP 0; sjc→fra HTTP 404; no successful samples |
| Retry / A | 13:49:11–13:50:02 | 2 (single + batch16, repeat 1) | `visibility write fra failed: HTTP 503`; fra→gru HTTP 404; fra→sjc HTTP 0; no successful samples |

R0's native profile is loopback. For WAN simulation, #381 includes a disposable user/network namespace with 25 ms loopback egress delay on **all** traffic (client HTTP, peer streams, metrics and health), approximately 50 ms RTT by configuration. The wrapper does not verify RTT itself and has no external interfaces. A separate five-ping setup check measured 50.053–50.070 ms RTT with no loss (see evidence file). Namespace setup (`r4-netem.sh true`) passed; no R0 WAN measurements ran because the preceding loopback pair aborted. The planned command was `nice -n 19 benchmark/scripts/r4-netem.sh make -f benchmark/repl.mk bench-repl-run SCENARIO=writes N=1000 REPEATS=5 OUT=benchmark/bench/r4-wan-VARIANT`, after builds outside the namespace.

Remaining measurement work: resolve the R0 visibility-probe 503 with its owner, then run successful five-repeat A/B loopback and isolated-delay profiles back-to-back on the same host. This report supplies no end-to-end p50/p99 or CPU/alloc comparison until that evidence exists.
