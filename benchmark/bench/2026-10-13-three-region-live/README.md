# Three-region live replication on Fly: gru, sjc, fra (disposable app)

**Label: EXPLORATORY.** This run tested candidate `9667b7ef`, not main, and the R10a gate stack it carries is still in
review. Latencies were measured from delta (a dev host) through Fly's edge, under no controlled load, so they are not
benchmark numbers. Final acceptance needs the same run at one main SHA (prod-rebuild-runbook E5).

## Latest: pre-freeze run 6 at `e1812ce3` (2026-10-13 06:37–07:59Z)

The candidate is `e1812ce3da125f9d21b4b37cfc261d7d91c5eeef`: R10a re-stacked on main (`feat/r10a-on-main` `6b08aa61`,
which includes main `e30ffdc2`, #144 `_system` FULL, the F3 gate-start fix, F6 bounded anchor writes and F7 majority
opening) merged with #175 (`78226ed0`). The merge was clean. Tests are at `624c219d`: the same tree plus a candidate-only
`Gaps.cs` that keeps only E10, E11, E13 and E15 as expected failures.

The machines were **production size**: shared-cpu-2x, 512 MB, 4 GB swap. Image: `registry.fly.io/ts-terrabase-e2e:e1812ce3-faults@sha256:20ff66a2…`,
the same binary on the test-only faults base.

| Machine | gru | sjc | fra |
| --- | --- | --- | --- |
| id | `82d1d00a7954d8` | `82237ec7e20648` | `2872653f603148` |
| volume | `vol_42k9wd5gxo10zx34` | `vol_4y8zng27xxwkej3r` | `vol_vxmz6q02700g2184` |

| Check | Result | Evidence |
| --- | --- | --- |
| **Genesis by prod-rebuild-runbook r2 B1/B2/B4/B5/B6/B8** (`machine create --machine-config`, created but not started, then started back to back) | **pass.** Every B2 config field matched: image digest, 2 CPUs, 512 MB, 4096 swap, new volume, on-failure, autostart false. All three opened with **no operator file**: gru verified against 2 peers at 06:39:27Z after logging "could not be pulled" while its peers booted, with **no host abort (F3 fixed)**. B6 digests were equal; in B8 each machine was `readyz` 200 within 13–17 s. | `evidence/r6-runbook-genesis.txt` |
| Six directions ×3, machine-pinned | **18/18 pass.** Latency (ms): SJC→GRU 23/30/37, SJC→FRA 294/298/300, GRU→SJC 226/218/215, GRU→FRA 310/291/295, FRA→SJC 223/226/217, FRA→GRU 24/55/28 | `evidence/outcomes-r6-r7.txt` |
| ThreeRegionE2ETests | **14/14 pass** (Bulk 3,000 converged in 3.2 s; burst in 12.5 s) | same |
| ReplicationE2ETests | **6/6 pass** | same |
| LatticeE2ETests | E8 and E14 pass. **E10 and E15 (R8 changes feed) pass outright** ("landed"). **E9 fails 6 of 6** (see B1). E11: `/sql` answers 401 to an API key, so it is not testable on Fly | same |
| Independent 3,000-doc load | count 3000 and digest `80230dee64cfded1e49bf8122188edca7aece2ee23ce6e1b630ce7e74e562d22` in gru, sjc and fra, 7.6 s after the last ack; **unchanged after everything below** | `evidence/live-run-r6.txt` |
| **Partition with same-document conflicts** (fra cut off from gru + sjc for 60 s) | During the cut fra had 301 docs and gru/sjc 601 (the cut was real). After the heal all three had 901 with equal listing digests. The shared document is equal everywhere: `{"base":true,"fra":"fra-edit","gru":"gru-edit","sjc":"sjc-edit","winner":"fra"}`. Every region's own field survived, and the contested field holds one written value in all regions. **But see B2:** fra's version token differs. | `evidence/live-run-r6.txt`, `evidence/live-run-r7.txt` |
| Fault tests (start-after-stopped shim) | **all pass:** Region_down, Writes_acknowledged…, A_restarted_region…, Restart_in_the_middle…, FlyOutage | `evidence/outcomes-r6-r7.txt` |
| SIGKILL, then clean restart, of each machine | **pass ×6.** `readyz` 200 within 12–17 s; gates equal within 5–30 s; 3,000 docs equal in all three after each step; no host aborts | `evidence/live-run-r6.txt` |
| **F7 live proof** (anchored revocation, then gru frozen with SIGSTOP and stopped; Fly event `stopped/crash exit_code=-1 requested_stop=true` 10.5 s later, the abrupt-VM path that lost commits before) | **pass ×2** (07:47Z, 07:53Z), plus a SIGKILL variant. The revocation answered 204. After the start: gate open + trusted with `pending: []` and **no operator step**. The revoked key is refused **401 in gru, sjc and fra**. | `evidence/r6-f7a-rerun.txt`, `evidence/live-run-r6.txt` |
| **F3 live proof** (gru restarted while sjc and fra drop its 6PN traffic, so its first gate pass stalls) | **pass.** The host stayed up (`/healthz` ok), the gate stayed closed (`readyz` 503, pending = both peers), with no abort. It opened 7 s after the traffic was restored, with no operator. | `evidence/live-run-r6.txt` |
| **PITR restore, non-empty** | **pass.** Write 500 docs, then T = 07:54:31.85Z, then 100 later docs. The plan used snapshot 07:53:50Z + 7 segments. The restore answered 201 with database `e2e-pitr-1791878099`. **Restored = 500 docs, digest `53e5a1c8…` = the source's digest at T**; the source now holds 600 (`a9c3e2e7…`), so the later writes are excluded. See B3 for placement. | `evidence/live-run-r7.txt` |
| Catalog digests | equal in 5 of 6 samples taken 5 s apart (the catalog keeps changing) | — |

### Sizing at production size (shared-cpu-2x 512 MB, the whole run, 78 one-minute samples per machine)

| Machine | Busy (user + system), share of 1 vCPU: mean / p50 / p90 / max | Steal mean / max | CPU balance | Min free memory |
| --- | --- | --- | --- | --- |
| gru | 5.6 / 2.1 / 15.4 / 49.8 % | 0.0 / 0.3 % | 10,349 → 40,110 (grew all run) | 165 MB |
| sjc | 5.5 / 2.4 / 13.7 / 33.5 % | 0.0 / 0.2 % | 9,987 → 41,047 | 156 MB |
| fra | 7.4 / 3.6 / 18.5 / 43.3 % | 0.0 / 0.2 % | 9,250 → 32,365 | 165 MB |

The baseline is 12.5 %. **There was no throttling during the full run:** the mean was below the baseline and the credit
balance grew. The earlier candidate (`9667b7ef`) used 24–33 % on performance-1x under nearly the same plan. The likely reason is the
code, but that is an inference: the two runs were not a controlled A/B. The production plan (shared-cpu-2x 512 MB + swap) holds for this workload. The tight
spot is memory: about 160 MB free out of 512 MB.

### New findings (evidence only; owners fix)

| ID | Owner | What | Evidence |
| --- | --- | --- | --- |
| **B1** | R5/R3 (sets) | **E9 add-wins is violated, 6 of 6 times.** gru runs `add tags x` while sjc concurrently runs `remove tags x`, with x already seeded. Every region settles to `{"tags":[]}`; the expected result is `["x"]`. It passed on `9667b7ef` (runs 2a, 2b, 4), so this is a regression in the main-based stack. Likely cause: an add of an element that is already present mints no new dot, so the concurrent remove covers it. | `evidence/outcomes-r6-r7.txt` (r6, r7 E9 ×5) |
| **B2** | R3 | **Equal documents carry different version tokens after convergence.** The partition's shared document is identical in all three regions, but `version` is `tbv1-3d913d8b…` in gru and sjc and `tbv1-75dd31a5…` in fra, unchanged 40 min after the heal. Either fra's lattice state (stamps or losing entries) differs while the values are equal, or the token is per replica. A client using `If-Match` across regions would get spurious conflicts. | `evidence/live-run-r7.txt` |
| B3 | R10a/backup | The PITR restore creates the new database single-region (`homeRegion: gru`, `replicaRegions: null`). It does not inherit the source's sjc/fra placement, so reads pinned to sjc are replayed to gru. | `evidence/live-run-r7.txt` |
| B4 | docs | On the internal listener, `/v1/projects/{p}/databases/{name}/point-in-time` answers `unknown_route`. The operator path is `/v1/management/organizations/{org}/projects/{p}/databases/{id}/point-in-time`. | — |

### Runbook r2 deviations (for PROD-REBUILD)

1. **B2 state.** Right after `machine create`, gru's new machine listed as `stopped`, not `created` (sjc and fra:
   `created`). Expect either state.
2. **No 422 here.** The previews org accepted 21 machines (15 + 3 parked + 3 new). Production's org may differ.
3. **A2: `skopeo inspect` needs `skopeo login` first.** Without it, the digest came back empty.
4. **B5:** `flyctl machine start` printed "failed to release lease … i/o timeout" for gru, but the machine started.
   Check the state rather than the exit message.
5. **E5 proof harness:** `Flyctl.MachineInRegionAsync` requires exactly one machine per region. While the parked old
   machines exist (until C2), every pinned test fails with "found 2". Destroy them (C2), or run the proof before
   parking, or adjust the harness (R3).
6. **The e2e template has `env: null` and `checks: null`** (machines created with `machine run`/`clone`); the
   production template has env. The machine config is in `scripts/runbook-b2-machine-config-gru.json`.

## Not proven yet (as of runs 1–5, before run 6)

- **Point-in-time restore still has not run.** Run 5 wrote 500 documents and then 100 later ones, but the restore-window
  call failed in the script: the internal listener speaks h2c, and the checker used HTTP/1.1 (fixed since). Then F7 closed
  two of the three gates. The restore runs once the cluster is reopened.
- **F7: a hard stop lost anchored catalog commits.** A machine whose shutdown passed Fly's kill timeout came back with its
  catalog head behind its restore anchor. Its gate now waits for an operator, and so does fra's, which waits for it. The
  cluster is stopped in that state (2 of 3 gates closed) for the owners.
- **The synchronous-second-region semantics are not tested.** They wait for R6-FIX.
- **Run 3 (shared-cpu-1x) was invalidated by CPU throttling (F5).** It was stopped. Runs 4 and 5 ran on performance-1x.
- **R8, R9 and R11 are not in the candidate.** E10, E11 and E15 stay expected failures; E11's signature is a 401 on
  `/sql` with an API key, not R9.
- **Two fault tests still fail in run 5.** The cause is the restarted machine's closed gate (F7), not a lost write; see
  Failures.

## What was tested

| | |
| --- | --- |
| App / org | `ts-terrabase-e2e` / `terrascale-previews-515` (disposable; synthetic data only) |
| Commit | `9667b7efa18ee99ef4614bfdad68aac78932dda3`, local branch `test/e2e-live-candidate` |
| Commit contents | R10a `0facba2f` (R6, R7, R10a-1/2/3, review fixes, F1/F2 fixes) + #149 health identity + `e406d61a` + #151 harness + a candidate-only Gaps list. Product code equals `b54bf0a0`; the last two commits touch `tests/` only. |
| Image | Runs 1–3: `registry.fly.io/ts-terrabase-e2e:9667b7ef` (`sha256:8d4cf4c7…`), the self-contained `terrabase` on the chiseled runtime-deps base (`scripts/Containerfile`). Runs 4–5: the **test-only faults image** `:9667b7ef-faults` (`sha256:84260b8d…`): the same binary on debian-slim with `ip6tables` (`scripts/Containerfile.faults`). It exists only for ts-terrabase-e2e, as COS approved, and never in production. |
| Storage format | `4-44d5cdc5b81c` |
| Machine size | runs 1–3: shared-cpu-1x, 512 MB, 1 GB swap. Runs 4–5: **performance-1x, 2 GB** (COS Q-E2E-CPU = A). One 1 GB volume each. |
| Pinning | Every request carries `fly-force-instance-id`. Every answer must name the expected `X-TerraBase-Region` and `X-TerraBase-Machine`. Each machine's `/healthz` `revision` was checked before any write. |
| Tests | `tests/TerraBase.E2E` at `9667b7ef`, plus the independent checker `scripts/counts.py` (its own canonical form and SHA-256 digest) |

### Machines

| Cluster | gru | sjc | fra | How it was created |
| --- | --- | --- | --- | --- |
| run 2a | `7814059c0734d8` | `48e6392a40d798` | `d8d0546a732d58` | `flyctl machine run`, all three at 00:45:14Z, each on a fresh volume |
| run 2b / 3 (G2) | `68379eec015e58` | `18556ddb9d2548` | `7811155eb22568` | prod-rebuild-runbook B4–B9: three clones back to back at 01:07Z, each on a new volume |

The Tigris bucket `terrascale-ts-terrabase-e2e` was reused across every genesis. It already held run 1's and run 2a's
objects, and `/backupz` answered 200 on all three machines (E5g).

## Results

### Runs 4 and 5 on performance-1x (2026-10-13 02:38–03:04Z; `evidence/live-run-r4.txt`, `evidence/live-run-r5.txt`)

| Check | Result |
| --- | --- |
| Six directions ×3, machine-pinned | **18/18 pass.** Latency (ms): SJC→GRU 29/25/26, SJC→FRA 296/620/294, GRU→SJC 251/495/226, GRU→FRA 370/293/297, FRA→SJC 221/218/218, FRA→GRU 38/81/28 |
| ThreeRegionE2ETests | **14/14 pass.** Bulk 3,000 converged in 3.2 s; three-way same field in 0.9 s; burst of 40 overwrites per region in 13 s |
| LatticeE2ETests | E8, E9, E14 **pass**. E10, E11, E15 are expected failures (slices absent) |
| ReplicationE2ETests | **6/6 pass** |
| Independent 3,000-doc load | count 3000 and digest `fd841fae7db7c267f391466a6e7a6c36b5bbe03a9a934b656747f8a495597898` in gru, sjc and fra, 8.8 s after the last ack; unchanged after the fault tests and after every kill and restart below |
| SIGKILL, then clean restart, of each machine | gru: `readyz` 200 after 10 s and 7 s. sjc: 38 s and 8 s. fra: 57 s and 8 s. Each time, gates equal within 5–30 s and the 3,000 docs had the same digest in all 3 regions |
| Region_down_then_back…, FlyOutageE2ETests | pass |
| **Partition (fra cut off from gru + sjc)** | **pass**, see below |
| Fault tests with the start-after-stopped shim (`scripts/shim/flyctl`, H1 workaround outside R3's files) | Writes_acknowledged_just_before… **pass**. A_restarted_region… and Restart_in_the_middle… **fail**: after gru's second stop, its gate answered 503 `catalog_unverified` (F7) |

**Partition and rejoin** (`counts.py partition`). The mechanism uses `ip6tables` on the test-only image, through
`fly ssh console`. On fra, every packet to and from gru's and sjc's 6PN addresses is dropped; on gru and sjc, every
packet to and from fra's. Both directions, both ends. Fly Proxy's client traffic still reaches every machine, so all
three keep accepting writes.

| Step (UTC) | gru | sjc | fra |
| --- | --- | --- | --- |
| 02:59:43 cut; each region writes 300 docs of its own (10.8 s) | | | |
| +30 s, during the cut | 601 docs | 601 docs (same digest as gru) | **301 docs**: only its own 300 + the shared one |
| 03:00:55 healed (72 s after the cut) | | | |
| +10.4 s | 901 | 901 | 901, digest `c3ea48e9…` equal in all three = the union of every write |

This proves the cut was real: no write crossed it for 30 s, in either direction. Concurrent same-document edits under
partition are covered by the ThreeRegion suites (unpartitioned), not by this step.

### Genesis (F1 fix) and the runbook's G2 procedure

| Check | Run 2a (machine run ×3) | Run 2b (runbook G2 clones) |
| --- | --- | --- |
| Gates open with no `catalog-open` or `catalog-bootstrap` file | yes: each "verified against 2 other machines", 00:46:23–25Z | gru and sjc yes (each verified against 1 machine at 01:07:41–42Z). fra crashed on its first start (F3). |
| Platform seed waits for the gate (F1 fix) | yes: seeded 1 s after each gate opened | yes |
| B8: pinned `/healthz` identity, `readyz`, `backupz` | — | 200 ×3; region, machine and revision as expected |
| B9: normal restart settings, one machine at a time | — | each `readyz` 200 within about 6 s; gates open and trusted |
| Catalog digests on all three machines | equal | equal in 11 of 12 samples taken 5 s apart (the catalog keeps changing: usage, probes) |
| All three restarted together (E5h) | — | all opened with no operator step. sjc stayed `readyz` 503 for 5 min 17 s (F4). |

Deviations from the runbook's B6 commands, for PROD-REBUILD (`evidence/g2-genesis.txt`):

1. **fra was cloned from gru's stopped machine.** The previews org was at 18 of its 20 machines, so fra's intermediate
   machine was destroyed first and fra's clone took gru's machine as its source (`--region fra`).
2. **Clones inherit B4's `--restart no`.** fra crashed with F3 on its first start and stayed stopped. It was started by
   hand once (no operator file), then opened by pulling from gru and sjc. Production needs F3 fixed, or B9's restart
   policy applied before B7.

### Six directions (each edge pinned to `region/machine@9667b7ef`)

Run 2b, 3 repeats, `ThreeRegionE2ETests.Write_in_one_region_is_readable_in_the_other_two`. Latency is measured from the
writer's acknowledgement until the reader returns the document.

| Edge | Result ×3 | Latency (ms), runs 1 / 2 / 3 |
| --- | --- | --- |
| SJC→GRU (`18556ddb9d2548` → `68379eec015e58`) | PASS | 111 / 26 / 81 |
| SJC→FRA (`18556ddb9d2548` → `7811155eb22568`) | PASS | 285 / 291 / 292 |
| GRU→SJC (`68379eec015e58` → `18556ddb9d2548`) | PASS | 223 / 231 / 494 |
| GRU→FRA (`68379eec015e58` → `7811155eb22568`) | PASS | 290 / 306 / 603 |
| FRA→SJC (`7811155eb22568` → `18556ddb9d2548`) | PASS | 223 / 226 / 230 |
| FRA→GRU (`7811155eb22568` → `68379eec015e58`) | PASS | 159 / 25 / 26 |

All directions passed in the other runs too:

| Run | Machines | Six-direction runs | Edges passed |
| --- | --- | --- | --- |
| Run 1 | `484db227` machines, deleted | 1 | 6/6 |
| Run 2a | 2a machines | 6 | 36/36 |
| Run 3, before throttling | G2 machines | 3 (latencies 0.6–11 s) | 18/18 |

Every scenario also checks that the write then settled in all three regions.

### Concurrent writes, counts and checksums

| Suite (run 2b) | Result |
| --- | --- |
| `ThreeRegionE2ETests` | **14/14 pass**: same field, different fields, delete vs edits, Bulk_3000 (equal counts and digests in 3 regions), burst of 40 overwrites per region, documents near the size limit, increments, set union |
| `LatticeE2ETests` | E8, E9, E14: **pass**. E10, E11, E15: expected failures (slices not in the candidate) |
| `ReplicationE2ETests` | **6/6 pass** (includes Bulk_1000 counts and checksums) |
| Run 2a `ThreeRegionE2ETests` | 14/14 pass |

Independent checker (`scripts/counts.py`): 3,000 documents written round-robin to gru, sjc and fra with 24 concurrent
writers.

| Load | Count in gru / sjc / fra | SHA-256 digest, all three regions |
| --- | --- | --- |
| run 2a `live-bulk-1` | 3000 / 3000 / 3000 | `5f7c93d0941ea6abaf415fb92516488bc218c1196a85566bab2d06eec725f537` (equals the writer's own digest) |
| run 2b `live-bulk-2b` | 3000 / 3000 / 3000 | `301933980a9526cf64fe145e83ac2c514540ba0301400516fc99c5d9d50de19c` (equals the writer's own digest) |

### Machine kills and restarts (run 2b, `evidence/r2b-restarts*.txt`)

After each step: pinned `/readyz` 200, `/catalog-gate` open + trusted + equal digest on all three, and the 3,000-document
count and digest re-read from every region.

| Step | `readyz` 200 after | Gates equal after | 3,000 docs, same digest in 3 regions |
| --- | --- | --- | --- |
| SIGKILL gru | 16 s | 5 s | yes |
| SIGKILL sjc | 17 s | 115 s | yes |
| SIGKILL fra | 18 s | 5 s | not read: the checker's key, minted on gru seconds earlier, answered 401 on fra; the next step re-read it |
| restart fra (×3) | 71 s (likely F3, unconfirmed) / 15 s / 18 s | 25 s / 5 s / 105 s | yes ×3 |
| restart gru | 15 s | 50 s | yes |
| restart sjc | 107 s | not equal within 120 s (the fra proxy timed out) | read timed out |
| run 2a: gru and sjc stopped/started by the fault tests | — | — | yes (3000, `5f7c93d0…`) |

## Failures and findings (evidence to COS; fixes belong to the owners named)

| ID | Owner | What | Evidence |
| --- | --- | --- | --- |
| **F3** | R10a (CatalogGate) | `CatalogGate.StartAsync` runs `VerifyAsync` under a 5 s `StartBudget`. When the budget cancels a gRPC pull, the call throws `RpcException(StatusCode.Cancelled)`, which is not an `OperationCanceledException`. Neither catch matches, so the host start fails: "Hosting failed to start", SIGABRT. Seen 4 times: gru 00:45:46, 00:46:05, 00:58:09; fra 01:07:56. A fifth is likely at fra's 01:24Z restart (`readyz` 200 only after 71 s) but not confirmed in the logs. With restart policy `no`, the machine stays down. Suggested fix: also catch while `budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested` (and in the per-peer catch). The failing test needs a peer whose pull outlives the budget. | Fly logs; `evidence/g2-genesis.txt` |
| **F4** | R3 / storage | After all three restarted together, sjc held one database's writer for 311 s "between storage calls" (holder: "a write transaction"). Incoming `Replication/Push` durable writes waited 15 s and were cancelled, backups failed with `StorageBusy`, and `readyz` stayed 503 for 5 min. It cleared by itself at 02:03:03Z. This happened while sjc was CPU-throttled (F5). | `evidence/r2b-all-restart.txt` |
| **F5** | COS / capacity | **shared-cpu-1x cannot hold TerraBase's steady CPU.** Measured demand (user + system) is 5–16 % of one vCPU per machine at low load. The baseline is 6.25 %, so the burst balance (about 4,400–5,000 s at start) drains. After that Fly throttles: steal reached 92 % in sjc from 01:31 to 02:26Z and in gru from 02:01Z, while user CPU stayed at about 8 %. Effects: durable commits took 3.5–5 s, peers logged "did not acknowledge a batch in time", streams reconnected, anti-entropy failed, and run-3 latencies rose to 0.6–30 s (burst: 362 s). Production's shared-cpu-2x has a 12.5 % baseline: idle fits, but bursts drain it. | `evidence/cpu-history.txt` (Fly Prometheus `fly_instance_cpu` by mode, `fly_instance_cpu_balance`) |
| **F6** | R10a | An API-key revocation (`DELETE …/api-keys/{id}`, which is anchored) on throttled gru hung for more than 4 minutes with no answer: neither the 500 the invariants describe nor success. The same revocation on sjc answered at once. | `evidence/live-run-r3.out` |
| **F7** | R10a + T1 (durability) | A stop whose shutdown did not finish in time lost catalog commits that were already anchored. gru got SIGINT at 03:02:36Z and SIGTERM at 03:02:41Z, then "Virtual machine exited abruptly" at 03:02:46Z. After the restart: "catalog (head 33972) is behind its anchor (head 33986): it was restored to an older copy". gru's gate stays closed until an operator decides, and fra's gate (pending `68379eec015e58`) stays closed with it: one hard stop takes 2 of 3 regions down. This candidate lacks main's #144 (`_system` synchronous=FULL), which may be why 14 anchored commits were not on disk. Either way the anchor got ahead of local durability. | `evidence/f7-evidence.txt` |
| **F8** | TerraBase / deploy config | Graceful shutdown did not finish within Fly's 5 s SIGINT window, and its SIGTERM window ended in an abrupt VM exit. The first stop of the same machine (03:01:15Z) exited cleanly in 1 s. Either shutdown must bound itself, or `kill_timeout` must be raised. | `evidence/f7-evidence.txt` |
| H1 | R3 (#151 harness) | `Restart_in_the_middle_of_replication_loses_no_write` fails on Fly: `StartAgainAsync` runs `flyctl machine start` while the machine is still stopping ("machine still active, refusing to start"). The harness should wait for `stopped` first. | `evidence/outcomes.txt` (run 2a) |
| H2 | R3 (harness) | `Writes_acknowledged_just_before_their_region_stops…` failed at teardown: gru was crash-looping on F3 after its restart, so the collection `DELETE` got no TerraBase answer. | `evidence/outcomes.txt` |
| — | — | A key minted on gru answered 401 on fra for a few seconds after fra's SIGKILL: the key had not replicated there yet. Catalog changes are eventually consistent across regions, so this is not counted as a failure. | `evidence/r2b-restarts.txt` |

Run 3 (02:05–02:26Z) was stopped on purpose. Its six-direction runs passed (18/18). After that the suites failed on
timeouts while gru and sjc were throttled (F5): ThreeRegion 12/14, Lattice 3/6, Replication 0/6, and every fault test
failed. These are recorded in `evidence/live-run-r3.out` and not counted as replication defects.

## Capacity and production sizing (F5)

| Machine | Mean busy (user + system) during the run | p50 | p90 | Max | Min free memory |
| --- | --- | --- | --- | --- | --- |
| gru, performance-1x | 33 % of 1 vCPU | 20 % | 73 % | 99 % | 1,516 MB of 2 GB |
| sjc, performance-1x | 24 % | 15 % | 48 % | 91 % | 1,475 MB |
| fra, performance-1x | 24 % | 13 % | 54 % | 100 % | 1,463 MB |

These are 26 one-minute samples per machine from Fly's Prometheus, 02:38–03:04Z, under the full test load
(`evidence/cpu-perf1x.txt`). At rest on shared-cpu-1x, demand was 5–8 % (`evidence/cpu-history.txt`).

What shared-cpu-2x 512 MB (production's plan) would have needed:
- **CPU.** Its baseline is 12.5 % of one vCPU (2 × 6.25 %). The test load's mean is 2–2.7× that baseline and its p90 is
  4–6×. On shared-cpu-1x the same load drained a full burst balance in about 25–35 minutes, after which Fly throttled
  the machines (steal up to 92 %). shared-cpu-2x would last longer, roughly twice the balance and baseline, but any
  sustained load above about 12.5 % throttles it the same way.
- **Memory.** Free memory on 2 GB never fell below 1.46 GB, so TerraBase used about 0.5–0.55 GB. On 512 MB that runs
  into swap; on shared-cpu-1x 512 MB, free memory fell to 135 MB.

Recommendation (a user and COS decision, because it is recurring spend):
1. **Dedicated CPU for TerraBase in production:** performance-1x 2 GB per region. List price is $33.00/30 days in iad.
   With the regional multipliers that is $53.5 (gru), $39.3 (sjc) and $38.0 (fra), about $131/month for three. The spend ledger estimates
   shared-cpu-2x 512 MB at about $7.5 per machine.
2. **If shared CPU must stay:** treat 12.5 % of a vCPU as the sustained ceiling. Alert on `fly_instance_cpu_balance`
   falling and on steal; expect throttling under replication catch-up and bulk loads. Use at least 1 GB of memory.

## Credentials (no values anywhere)

- **Master key and bucket keys.** The app's `TERRABASE_MASTER_KEY` was set once (2026-10-12 21:47Z). The bucket keys
  were written by `flyctl storage create`.
- **Run 1 key `e2e-live`.** Revoked by `scripts/revoke-e2e-live-key.sh` at 00:44Z (0 remaining); its local file was
  deleted.
- **Runs 2a and 2b.** Per-run keys were minted through the operator API, held only in memory and in the test process's
  inherited environment, and revoked at exit. 0 were listed on the current tenant after the run.
- **Run 3.** One key, `01M4SG3YY86SS55QKEXBKQJWW8` (24 h expiry, documents read/write on one project), revoked at
  02:30Z through sjc; 0 listed afterwards.
- **Run 6 (pre-freeze).** Session keys `01M4T1MBNS5RD3PWFDSQDQ2E54` (aborted start), `01M4T1NX6N28XMJ8Q7B1HY929Y` and
  `01M4T42HGADNYG05KAQKM6PXM1`, plus 3 short check sessions (`list` ×2, `shared`) and 3 `f7-throwaway` keys revoked by the F7 test itself; all
  were revoked, held in memory only, and documents-only on project `01M4T1K15T2XR1MKCHY3VT9APK`.
- **Runs 4 and 5.** Key `01M4SJ0QJ23AB7CEBD8VTWA2PH` (02:38:41Z) was revoked at 02:55Z; key
  `01M4SK5FV80P67MBQJ4N8Q2GHB` (02:58:46Z) was revoked at 03:03:51Z through sjc. Both were 24 h keys, documents read/write
  on project `01M4SD7D7B2C3CZ4W41F3ZY2FK` only, held in memory only.
- Key ids and expiries are recorded in `.cos/fly-resource-ledger.md`.

## Cost

About 7.5 machine-hours of shared-cpu-1x 512 MB (≈ $0.05), plus performance-1x 2 GB × 3 from 02:37 to 03:05Z (≈ 0.47 h × $0.181/h ≈ $0.09). Total ≈ $0.15. Run 6: shared-cpu-2x 512 MB × 3 from 06:38 to 07:59Z (≈ 1.35 h × $0.024/h ≈ $0.03), plus 3 new 1 GB volumes. Cumulative ≈ $0.20. Volumes peaked at 9 × 1 GB from 01:07 to 02:04Z; 3 GB
remain. Transfer was a few hundred MB. The three machines are stopped. Recurring now: ≈ $0.45/month for volumes plus
stopped rootfs.

## Reproduce

The scripts in `scripts/` were run from a scratch directory next to the candidate worktree. `run.sh` expects
`../e2e-live`, and `flyctl proxy` must listen on 18181–18183 (8081 of gru, sjc and fra).

1. Load the key into the environment: `with-key.sh <cmd>` mints it, keeps it in memory only, and revokes it at exit.
2. Run the plan: `live-run.sh <tag>` covers six directions ×3, the suites, the bulk load, fault tests, kills and
   restarts, and PITR.
3. Run checks on their own: `counts.py load|verify|pitr`.
