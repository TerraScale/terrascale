# TerraBase Rust acceptance matrix v2

**Registered 2026-10-01, before any v2 candidate evaluation.** Source contract: `terrabase/GOAL.md` section 10 at engine revision `e9baf97a24bcff906dd9f6f1d0d30d9e8d6b8b39`, which replaces the v1 win rule with a **0.40x SQLite throughput floor**. This table is the required-case register, not a result. v2 supersedes [v1](terrabase-rust-acceptance-v1.md) for every new verdict; v1 stays unchanged as the historical register. No v1 row was ever measured, so no recorded result changes status.

## What changed from v1

| Rule | v1 | v2 (GOAL.md section 10) |
| --- | --- | --- |
| Throughput verdict | Win only when the paired CI95 lower bound of TerraBase/SQLite exceeds 1.05 | `MEETS_FLOOR` when the paired CI95 lower bound is at least 0.40; `BELOW_FLOOR` when the upper bound is below 0.40; otherwise `INCONCLUSIVE`. Ratios above 1.0 meet the floor without cap or throttling |
| Latency, startup, recovery | Win/loss against 0.95/1.05 ratio bounds | Reported separately with distributions and intervals; no SQLite-relative latency SLO and no floor verdict |
| Resources | TerraBase must use no more than the matching SQLite total resource amount | Declared hard budgets stay hard gates (4 vCPU, 8,192 MiB guest, equal configured cache and temporary-disk allowances); resource efficiency and cost per useful workload are measured and published, not judged against SQLite's total |
| Status vocabulary | win, loss, statistically inconclusive, unsupported, not comparable | `MEETS_FLOOR`, `BELOW_FLOOR`, `INCONCLUSIVE`, `UNSUPPORTED`, `NOT_COMPARABLE`, each with a reason |
| Practical significance | 5% | Not a separate threshold; the 0.40 floor is the registered criterion |

## Fixed comparison contract

The environment, fairness, and evidence rules of v1 are unchanged. The acceptance environment is one Firecracker microVM per database node with **4 vCPUs, 8,192 MiB RAM, swap off**; the load generator, RustFS, and Von each use separate identical microVMs when applicable. Preserve exact host/guest image hashes, CPU topology, SSD/filesystem, cache state, networking, source and benchmark revisions, compiler flags, SQLite version, binary hash, logical fixture hash, seed, and raw samples in each run receipt. SQLite is bundled `3.53.2` through pinned `rusqlite 0.40.2` with WAL, prepared statements, case-appropriate indexes, bounded page cache, and `synchronous=FULL` for local durable commits; `NORMAL` is diagnostic only. Use JSONB for matching document-field cases. Match batching and indexes to the TerraBase case. A local SQLite commit does not model TerraBase replicated or object durability. Shared CI machines may detect gross regressions but cannot certify a result near the floor.

Run paired trials in alternating engine order on reset, equivalent VMs and volumes. Use at least **7 pairs**, fixed published seeds, equal completed logical operations, equal payloads, and equivalent isolation and durability. Record errors, timeouts, retries, and failures.

## Throughput floor rule

Throughput is successful completed logical operations or transactions per second of elapsed wall time, preregistered per case. For pair *i*, `S_i` and `T_i` are the SQLite and TerraBase throughputs; both lanes must complete the same logical operations. The estimator is `mean(T_i) / mean(S_i)`. Its 95% interval is the 2.5th/97.5th percentile (sorted replicate indices 249 and 9,749) of 10,000 whole-pair bootstrap resamples with replacement from one recorded ChaCha20 seed.

| Status | Condition |
| --- | --- |
| `MEETS_FLOOR` | CI95 lower bound >= 0.40 |
| `BELOW_FLOOR` | CI95 upper bound < 0.40 |
| `INCONCLUSIVE` | Interval contains 0.40; the case stays open |
| `UNSUPPORTED` | A required TerraBase feature or real caller is absent; the case stays open |
| `NOT_COMPARABLE` | Semantic or failure-domain mismatch with SQLite, with an explicit reason; TerraBase's own gates still apply |

`terra-rust`'s `throughput-floor` executable computes this verdict from a paired-trial JSON file and refuses fewer than 7 pairs, unequal completed work, a non-throughput case, or another matrix version:

```sh
terra-rust/target/release/throughput-floor --input pairs.json --output verdict.json
```

```json
{"schema_version": 1, "matrix_version": "v2", "case_id": "M01/insert/c1/throughput",
 "bootstrap_seed": 2026100101,
 "pairs": [{"id": "01",
            "sqlite": {"receipt": "...", "operations_completed": 100000, "elapsed_ns": 1000000000},
            "terrabase": {"receipt": "...", "operations_completed": 100000, "elapsed_ns": 2000000000}}]}
```

It writes the point ratio, interval, status, and per-pair throughputs to a fresh JSON report and prints one Markdown result row. A correctness, guarantee, or declared hard-resource-gate failure keeps the case from `MEETS_FLOOR` regardless of throughput; record it as the reason. Every listed axis combination and throughput row is its own case, named `<ID>/<axis values>/throughput`. No aggregate may hide a `BELOW_FLOOR` case, and a below-floor case may not be removed, re-registered under weaker guarantees, or closed by retuning only TerraBase.

## Required scoreboard

`Open` rows carry `UNSUPPORTED` or `NOT_COMPARABLE` until measured. Floor rows receive a throughput verdict; reported rows are published with distributions and intervals but have no SQLite-relative pass/fail. `driver` rows have a SQLite-only lane in `terra-rust`; they remain unsupported for comparison until the real adapter exists. Every `driver` point case runs at 1 and 4 client connections, 256-byte and 8-KiB bodies, and hot and cold cache states when the harness supports them.

| ID | Area and required case/axes | Floor rows | Reported rows | SQLite baseline | Status and reason |
| --- | --- | --- | --- | --- | --- |
| P01 | PK hit, random/sequential keys; small/large bodies; full/projection/nested field; hot/cold RAM and SSD cache | throughput | p50, p95, p99, resource | driver: random full read, single client | UNSUPPORTED: TerraBase caller and other axes pending |
| P02 | PK miss, random/sequential keys; hot/cold; c1/c4 | throughput | p50, p95, p99, resource | driver: random miss, single client | UNSUPPORTED: TerraBase caller and other axes pending |
| P03 | Focused storage scan: 16 ordered rows from 1,024 and 50,000 primary rows; 256-byte and 8-KiB values; no-forced and explicit checkpoint fixtures with observed physical state; hot/cold, c1 | throughput | p50, p95, p99, per-scan CPU, RSS/PSS, admitted bytes, WAL bytes, metadata reads, row/point probes | driver: primary-key ordered range c1 | UNSUPPORTED until guest trace counters and matched physical state are verified |
| P04 | Deleted-history scan: 1,024, 50,000 and 100,000 tombstones, `row_limit=1`, empty or sparse-tail result, 30-second per-scan timeout | none (latency case) | empty/sparse scan latency and timeout, examined history, metadata reads, CPU, peak RSS/PSS, WAL bytes | SQLite delete/checkpoint state with equivalent logical emptiness | UNSUPPORTED until guest work counters are verified; no comparison if retention semantics differ |
| M01 | Insert, update, delete, upsert; single and batch durable commit; c1/c4 | throughput | p50, p95, p99, resource | driver: single insert/update/delete/upsert, c1 | UNSUPPORTED: TerraBase caller and batch/c4 pending |
| M02 | Read-modify-write, atomic conflict/abort; c1/c4 | throughput | p50, p95, p99, error rate | driver: c1 RMW | UNSUPPORTED: TerraBase caller and conflict/c4 pending |
| M03 | Concurrent read/write mix, increasing offered rate through saturation and overload | throughput | p50, p95, p99, p99.9, error rate, saturation | pending | UNSUPPORTED: open-loop scheduler and real caller pending |
| M04 | Unique and relationship constraints, positive and rejected mutations | throughput | latency, correctness | pending | UNSUPPORTED: real caller pending |
| Q01 | Indexed equality, short selective ordered range; covering/composite/partial/multikey, skew/hot keys, pagination | throughput | p50, p95, p99, resource | driver: equality and ordered range c1 | UNSUPPORTED: real caller and additional index forms pending |
| Q02 | First queries and workload shift: no index, deterministic initial index, Von/JEV index, adaptive index; held-out queries | sustained throughput | first-query latency, coverage, build/catch-up time, resources and cost | pending | UNSUPPORTED: real caller and advisor paths pending |
| O01 | Advertised scan, join, sort, aggregation, full-text, spill; selective and broad result sets | throughput | p50, p95, p99, resource | pending | UNSUPPORTED until the operator is advertised and a real caller exists |
| S01 | Foreground reads/writes during checkpoint, compaction, index build, backup, long snapshot and reclamation | sustained throughput | p95, p99, p99.9, resource | pending | UNSUPPORTED: maintenance caller pending |
| S02 | Data and index sets below RAM, above RAM, and above bounded SSD cache; cold/hot; cache exhaustion | sustained throughput | p99, resource, read/write I/O | pending | UNSUPPORTED: large fixture and tier caller pending |
| T01 | Pinned Turso-backed local operation; SSD/S3 tiered reads and writes; cold and warm access; workload shifts; queries and transactions crossing tier states | throughput (SSD-local against SQLite) | p50, p95, p99, S3 requests/bytes, cache hit ratio, resource | SQLite local only; S3 tier not comparable | UNSUPPORTED: tier caller pending |
| R01 | Empty/populated open, clean restart, dirty restart/WAL replay | none | startup and time-to-useful-query, p99 first query, recovery correctness | pending | UNSUPPORTED: process/crash harness pending |
| R02 | Cold object-backed open, SSD loss, backup restore and recovered state | none | recovery time, time-to-useful-query, resource and correctness | Not comparable | NOT_COMPARABLE: SQLite has no native S3/object-durable semantics; TerraBase budget and correctness gates still required |
| L01 | v1 Bf-Tree/ZoneTree-inspired layouts and online transition | none | none | none | UNSUPPORTED: GOAL.md section 6 selects the pinned Turso substrate instead of TerraBase-owned layouts; kept so the v1 row is not silently dropped |
| D01 | Local durable against SQLite FULL, plus separate replicated/object profiles | throughput (local durable only) | p50, p95, p99, RPO and RTO | SQLite FULL only for local durable | UNSUPPORTED: real local caller; replicated/object profiles are NOT_COMPARABLE to local SQLite durability |
| N01 | Local/LAN/WAN replication at latency/loss; offline peer, heal, bootstrap, repair, shard move | none | throughput, p99, durable/apply lag, recovery and resources | Not comparable | NOT_COMPARABLE: SQLite has no native multi-region replication; TerraBase gates still required |
| E01 | Idle and many-database open overhead within the 4-vCPU/8-GiB guest | none | RSS/PSS/container memory, CPU, SSD bytes | pending | UNSUPPORTED: process caller pending |
| E02 | Steady/peak/receded memory, allocator live/retained and bytes allocated/copied per op | none | RSS/PSS/container memory, allocations/copies | pending | UNSUPPORTED: complete instrumentation pending |
| E03 | SSD/local and S3/remote bytes, read/write amplification, request counts/transfer, cache hit ratios, wear and cost per workload/GiB-month | none | bytes and cost per useful workload under declared price assumptions | SQLite local only; S3 tier not comparable | UNSUPPORTED: tier telemetry and dated price schedule pending |

## Driver scope and receipts

The driver scope, receipt contents, and P03/P04 diagnostic hypotheses of v1 still apply. The existing `analyze-p04` analyzer remains bound to its preregistered [P04 repeat protocol](p04-repeat-protocol-20260924.md); its provisional latency classifications are diagnostic and are not v2 scoreboard verdicts. Save completed raw JSON receipts, `throughput-floor` reports, and human-readable result tables in `bench/` with the matrix version, case ID, trial, and source identities. Changes to cases or the floor require a new matrix version registered before evaluating a candidate.
