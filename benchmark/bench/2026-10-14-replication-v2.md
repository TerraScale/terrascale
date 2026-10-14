# V2: 3-region shape clients converge under faults (2026-10-14)

Same product commit, image and cluster as [V5](2026-10-14-replication-v5.md): `a439e1d1`, `sha256:b2267cc0…`,
`ts-vtest-tb` (gru `891e70eb4d0378`, sjc `6835466b44e258`, fra `48e4967a225338`, shared-cpu-2x 512 MB).

Harness: `tests/TerraBase.E2E/ShapeReader.cs`, a protocol-v1 shape client (`docs/terrabase/shape-reads.md` sections 3,
7, 8): `offset=-1` → 307 → snapshot pages → chunks until a fresh `up_to_date`; `409 must_refetch` discards the view and
restarts at `-1` (no resume until FR-23); `429`/`503` keep the view and retry; `404 unknown_collection` is an empty view.
`ShapeConvergenceE2ETests` keeps one reader per region and, after each fault, waits until **every region's view equals
the documents that region lists** and every region lists the expected digest.

## S3 and S4 on Fly at `3c459a5f` (2026-10-14 19:45–20:05Z): both PASS

Image `registry.fly.io/ts-vtest-tb:vtest-3c459a5fa160@sha256:96573646bbd6557de5891ba356be031a9d6ce02e4e1aa5d6369df20cbff6f0fe`
(owner's deploy of `fix/shape-load-abort-vtest` = main + #307 + #309 + #314), same three machines, harness `c0a4218e`
(S3) / `cc50c613` (S4) on this branch, pinned fault runs from beta.

| Row | Scenario | Result | Numbers | Run (UTC) | Raw log (sha256) |
| --- | --- | --- | --- | --- | --- |
| S3 | sjc genuinely loses an acknowledged document (`Cluster.LoseDocumentAsync`: machine idled with `sleep infinity`, its `_tb_state` row deleted with sqlite3, terrabase started again; terrabase holds the file lock, so editing it live fails with `database is locked`) | **PASS** | `deleted 1 from _tb_state` in the e2e database file; sjc ready again 43.8 s after the loss began; the document **absent on sjc** right after (asserted); all 3 shape views = listings (30 docs, `B020B407…`) **123.7 s** after sjc was back (anti-entropy rounds); 0 refetches | 20:02:53Z | `2026-10-14-replication-vtest/evidence/raw/3c459a5f-ShapeConvergenceE2ETests-Shape_clients_converge_after_anti_entropy_repairs_a_lost_document.txt` (`3c0fd6e7…`) |
| S4 | Clock skew on real VMs: `date -s` as root steps sjc +2,000 ms and fra −2,000 ms (no chronyd/ntpd in the VM), both cases concurrently; shared document written skewed → gru → skewed; clocks stepped back in `finally` | **PASS ×2** | steps: sjc `+2000.0` / back `-2000.0`, fra `-2000.0` / back `+2000.0` (each step lags by ≈1 ms, so the residual is ≈2 ms per machine); 31 docs, views = listings in 42.9 s (sjc case, `DA7CC1ED…`) and 42.9 s (fra case, `492A452F…`) vs 50 ms locally; 0 refetches | 19:45:58Z | `2026-10-14-replication-vtest/evidence/raw/3c459a5f-ShapeConvergenceE2ETests-Shape_clients_converge_under_clock_skew.txt` (`2907c8b3…`) |

Failed attempts kept as evidence: S3 attempt 1 (SIGSTOP + edit: `database is locked`, nothing deleted, the test's
"lost" assertion failed; `…/raw/3c459a5f-attempt1-S3-file-locked.txt`, `b589515f…`) and attempt 2 (a merged
`--machine-config` cannot clear the entrypoint override, so sjc stayed idle until restored by hand at ≈20:00Z;
`…/raw/3c459a5f-attempt2-S3-entrypoint-not-restored.txt`, `afee7d73…`). sjc's config now names `/app/terrabase` as its
entrypoint explicitly (the image's own). Open question for the owner: S4's 42.9 s convergence under ±2 s skew vs ≈1.6 s
unskewed on Fly; the harness does not show where the time goes.

## Rerun at `7bf8dd3a` (2026-10-14 18:44Z): S1 and S2 PASS on Fly

`ts-vtest-tb` redeployed by the V-wave owner (not by this lane) at `7bf8dd3a2a845240aa74e675eda23457adc67e56`
(temp branch `fix/shape-frontier-stuck-vtest` = #307 shape memory fix + #309 stuck-frontier fix + vtest scripts), image
`sha256:b968b9965c7dc30a0a90a1f8fe8d3f9bdfcc78a0cfa6cfceff0c163f2d09ceb6`, same three machines. Harness
`6f16344a` (this branch), pinned run from beta, faults with `TERRABASE_E2E_FLY_OUTAGE=1`. After the run `ip6tables -S`
on all three machines shows only the ACCEPT policies (healed).

| Row | Scenario | Where | Result | Numbers | Run (UTC) | Raw log (sha256) |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | Partition (ip6tables fra–gru cut, both write, shared document edited on both sides), heal | Fly @ `7bf8dd3a` | **PASS** | seed: 30 docs, views = listings in 1,574 ms; after heal: 71 docs (`401595D1…`) views = listings in all 3 in 3,942 ms; 0 refetches; 9–10 responses per reader | 18:44:33Z | `2026-10-14-replication-vtest/evidence/raw/7bf8dd3a-ShapeConvergenceE2ETests-Shape_clients_in_every_region_converge_after_a_partition_heals.txt` (`e1d8173b…`) |
| S2 | Collection drop and re-create | Fly @ `7bf8dd3a` | **PASS** | before: 30 docs in 1,583 ms; after drop: 0 in 582 ms; after re-create: 30 docs (`07985613…`) in 1,562 ms; 0 refetches | 18:44:16Z | `2026-10-14-replication-vtest/evidence/raw/7bf8dd3a-ShapeConvergenceE2ETests-Shape_clients_follow_a_collection_drop_and_re_create.txt` (`1d6dd896…`) |
| V5 R1 (regression check) | 201 `remote_durable`, another region serves the first read | Fly @ `7bf8dd3a` | PASS 3/3 | ack gru 266 / sjc 398 / fra 474 ms; both other regions held it on the first read (716–797 ms after send) | 18:45:34Z | `2026-10-14-replication-vtest/evidence/raw/7bf8dd3a-WaveAE2ETests-A_201_is_remote_durable.txt` (`82a40986…`) |

S3 and S4 ran on Fly at `3c459a5f` (section above); S5 stays BLOCKED on FR-23. The `a439e1d1` rows below are kept as history:
F-V2-1 (`429 shape_busy`) is fixed by #307/#309 on this candidate; it is fixed on main only once both merge.

## History at `a439e1d1`

| Row | Scenario | Where | Result | Numbers | Harness commit / run (UTC) | Raw log (sha256) |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | Partition (fra–gru direct link cut, both write, shared document edited on both sides), heal | local | PASS | 71 docs, views = listings in all 3 (`5EFF6C94…`) 132 ms after the shared document settled; 0 refetches | `bba212ff` 17:14Z | beta `/tmp/v2v5-local-5/ShapeConvergenceE2ETests.log` (`e754dca3…`) |
| S1 | same (ip6tables cut) | Fly | **FAIL** | never reached the cut: at the seed every shape request answered `429 shape_busy` for 60 s+ (views 0 docs, listed 30, 0 responses applied, all 3 regions) | `c4341eeb` 17:33Z | `2026-10-14-replication-vtest/evidence/raw/ShapeConvergenceE2ETests-Shape_clients_in_every_region_converge_after_a_partition_heals.txt` (`3331f059…`) |
| S2 | Collection drop (views → 0) and re-create (30 new docs) | local | PASS | drop: views 0 in 46 ms; re-create: 30 docs (`A81C8E0C…`) in 66 ms; 0 refetches | `bba212ff` 17:14Z | local log above |
| S2 | same | Fly | **FAIL** | same `429 shape_busy` signature before the drop (views 0, listed 30) | `9848dd70` 17:21Z | `…/evidence/raw/ShapeConvergenceE2ETests-Shape_clients_follow_a_collection_drop_and_re_create.txt` (`04f8dc67…`) |
| S3 | Anti-entropy repair of a document deleted from sjc's file while sjc was down | local | PASS | 30 docs, views = listings 120.1 s after sjc's restart (anti-entropy rounds) | `bba212ff` 17:14Z | local log above |
| S3 | same | Fly | BLOCKED | harness has no file tampering on Fly machines | — | — |
| S4 | Clock skew sjc +2000 ms / fra −2000 ms, shared document written skewed → gru → skewed | local | PASS ×2 | 31 docs each; views = listings in 50 / 51 ms | `bba212ff` 17:14Z | local log above |
| S4 | same | Fly | BLOCKED | no clock control on Fly machines (`Cluster.StartAsync` refuses skews on Fly) | — | — |
| S5 | Region-failover resume (FR-23) | — | BLOCKED | #298 / #300 open; every resume answers 409 until then | — | — |

## Finding F-V2-1: shapes answer `429 shape_busy` on production-size machines

`2026-10-14-replication-vtest/scripts/shape-probe.sh` (one new collection, one document, 90 s idle, then `offset=-1` per machine): **429
`shape_busy` on gru, sjc and fra** (`2026-10-14-replication-vtest/evidence/v2-shape-probe-a439e1d1.txt`). gru's metrics at the same time
(`2026-10-14-replication-vtest/evidence/v2-gru-memory-metrics-a439e1d1.txt`): `terrabase_pressure_memory_used_bytes` 358,322,176 against
`terrabase_pressure_memory_budget_bytes` 120,231,936, `terrabase_pressure_mem_util` 1, working set 347 MB, reserved
16,975,561 B. Every shape path maps a memory-reservation refusal (`QosMemoryException`, `IMemoryBudget.TryReserve`) to
`shape_busy`, so the likely cause is the memory budget on a 512 MB machine; not proven (no shape-admission metric names
the reason). Local nodes on beta (12 CPUs, ~9 GB) do not hit it. Owner: FR-17/FR-20 (shape memory admission). Until
fixed, V2 on Fly cannot pass at production size.
