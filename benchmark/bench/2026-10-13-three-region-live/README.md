# Three-region live replication on Fly: gru, sjc, fra (disposable app)

**Label: EXPLORATORY.** This run tested candidate `9667b7ef`, not main, and the R10a gate stack it carries is still in
review. Latencies were measured from delta (a dev host) through Fly's edge, under no controlled load, so they are not
benchmark numbers. Final acceptance needs the same run at one main SHA (prod-rebuild-runbook E5).

## Not proven yet (read first)

- **Run 3 was invalidated by CPU throttling (F5).** Partway through, Fly throttled the shared-cpu-1x machines to their
  baseline. The run was stopped and no result from that point on is counted.
- **Point-in-time restore did not run.** It is prepared (`scripts/counts.py pitr`) and waits for a run on machines that
  are not throttled.
- **Partition and rejoin did not run.** Fly cannot cut 6PN between two machines, and the harness skips this case on Fly
  by design. A partition would need a faults image with `ip6tables` (runbook in `docs/terrabase/e2e-disposable-app.md`),
  which is not approved.
- **R8 (changes feed), R9 (SQL writes) and R11 are not in the candidate.** E10, E11, E13 and E15 stay expected failures.
  E11's failure signature is a 401 on `/sql` with an API key, not R9.
- **Two fault tests failed (run 2a).** Their causes are a harness timing gap and F3; see Failures.

## What was tested

| | |
| --- | --- |
| App / org | `ts-terrabase-e2e` / `terrascale-previews-515` (disposable; synthetic data only) |
| Commit | `9667b7efa18ee99ef4614bfdad68aac78932dda3`, local branch `test/e2e-live-candidate` |
| Commit contents | R10a `0facba2f` (R6, R7, R10a-1/2/3, review fixes, F1/F2 fixes) + #149 health identity + `e406d61a` + #151 harness + a candidate-only Gaps list. Product code equals `b54bf0a0`; the last two commits touch `tests/` only. |
| Image | `registry.fly.io/ts-terrabase-e2e:9667b7ef` (`sha256:8d4cf4c7…`). Self-contained `terrabase` built locally on the chiseled runtime-deps base (`scripts/Containerfile`). |
| Storage format | `4-44d5cdc5b81c` |
| Machine size | shared-cpu-1x, 512 MB, 1 GB swap, one 1 GB volume each |
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
| H1 | R3 (#151 harness) | `Restart_in_the_middle_of_replication_loses_no_write` fails on Fly: `StartAgainAsync` runs `flyctl machine start` while the machine is still stopping ("machine still active, refusing to start"). The harness should wait for `stopped` first. | `evidence/outcomes.txt` (run 2a) |
| H2 | R3 (harness) | `Writes_acknowledged_just_before_their_region_stops…` failed at teardown: gru was crash-looping on F3 after its restart, so the collection `DELETE` got no TerraBase answer. | `evidence/outcomes.txt` |
| — | — | A key minted on gru answered 401 on fra for a few seconds after fra's SIGKILL: the key had not replicated there yet. Catalog changes are eventually consistent across regions, so this is not counted as a failure. | `evidence/r2b-restarts.txt` |

Run 3 (02:05–02:26Z) was stopped on purpose. Its six-direction runs passed (18/18). After that the suites failed on
timeouts while gru and sjc were throttled (F5): ThreeRegion 12/14, Lattice 3/6, Replication 0/6, and every fault test
failed. These are recorded in `evidence/live-run-r3.out` and not counted as replication defects.

## Credentials (no values anywhere)

- **Master key and bucket keys.** The app's `TERRABASE_MASTER_KEY` was set once (2026-10-12 21:47Z). The bucket keys
  were written by `flyctl storage create`.
- **Run 1 key `e2e-live`.** Revoked by `scripts/revoke-e2e-live-key.sh` at 00:44Z (0 remaining); its local file was
  deleted.
- **Runs 2a and 2b.** Per-run keys were minted through the operator API, held only in memory and in the test process's
  inherited environment, and revoked at exit. 0 were listed on the current tenant after the run.
- **Run 3.** One key, `01M4SG3YY86SS55QKEXBKQJWW8` (24 h expiry, documents read/write on one project), revoked at
  02:30Z through sjc; 0 listed afterwards.
- Key ids and expiries are recorded in `.cos/fly-resource-ledger.md`.

## Cost

About 7.5 machine-hours of shared-cpu-1x 512 MB (≈ $0.05 at $0.0058–0.0083/h). Volumes peaked at 9 × 1 GB from 01:07 to 02:04Z; 3 GB
remain. Transfer was a few hundred MB. The three machines are stopped. Recurring now: ≈ $0.45/month for volumes plus
stopped rootfs.

## Reproduce

The scripts in `scripts/` were run from a scratch directory next to the candidate worktree. `run.sh` expects
`../e2e-live`, and `flyctl proxy` must listen on 18181–18183 (8081 of gru, sjc and fra).

1. Load the key into the environment: `with-key.sh <cmd>` mints it, keeps it in memory only, and revokes it at exit.
2. Run the plan: `live-run.sh <tag>` covers six directions ×3, the suites, the bulk load, fault tests, kills and
   restarts, and PITR.
3. Run checks on their own: `counts.py load|verify|pitr`.
