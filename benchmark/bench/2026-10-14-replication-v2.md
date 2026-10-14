# V2: 3-region shape clients converge under faults (2026-10-14)

Same product commit, image and cluster as [V5](2026-10-14-replication-v5.md): `a439e1d1`, `sha256:b2267cc0…`,
`ts-vtest-tb` (gru `891e70eb4d0378`, sjc `6835466b44e258`, fra `48e4967a225338`, shared-cpu-2x 512 MB).

Harness: `tests/TerraBase.E2E/ShapeReader.cs`, a protocol-v1 shape client (`docs/terrabase/shape-reads.md` sections 3,
7, 8): `offset=-1` → 307 → snapshot pages → chunks until a fresh `up_to_date`; `409 must_refetch` discards the view and
restarts at `-1` (no resume until FR-23); `429`/`503` keep the view and retry; `404 unknown_collection` is an empty view.
`ShapeConvergenceE2ETests` keeps one reader per region and, after each fault, waits until **every region's view equals
the documents that region lists** and every region lists the expected digest.

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
