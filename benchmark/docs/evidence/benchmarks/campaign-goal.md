# Benchmark campaign goal (TB-12 + TB-17 only)

Status: active as of 2026-08-30. This file extracts the benchmark-only scope
from the workspace authority (`../../GOAL.md` at the TerraScale root, tasks
TB-12 and TB-17 and the section-3 performance targets). It adds no decisions;
it is the working plan for the performance campaign. Everything else in the
workspace goal is explicitly out of scope here.

## The bars (from workspace GOAL.md, section 3)

Beat RavenDB and PostgreSQL-with-JSONB on comparable document workload
families — higher throughput AND lower p99 per family, like-for-like
boundaries, same hardware:

| family | postgresql 18.6 | ravendb 7.2.5 | (ops/s / p50 µs / p99 µs) |
| --- | --- | --- | --- |
| read | 99,595 / 148 / 279 | 12,468 / 446 / 22,518 |
| write | 2,577 / 6,000 / 8,978 | 3,058 / 4,967 / 8,499 |
| query | 94,621 / 153 / 300 | 3,192 / 1,236 / 34,080 |
| mixed | 8,184 / 154 / 6,394 | 3,547 / 1,765 / 21,236 |

Boundary honesty (TB-12): engine-vs-engine over equivalent transports, and
full-stack vs full-stack — never raw-TCP Postgres against mTLS-gRPC
TerraBase. A family still losing at gate time goes to the owner: block, or
waive in writing in the benchmark history.

## Current state (2026-08-30, this host)

- Reference sides are pinned and recorded (`se15-reference-run-2026-08-30.md`,
  `bench/results/{postgresql,ravendb}-*.json`, rendered in
  `benchmark-history.md`).
- TerraBase side: latest recorded runs show read p50 2.6 ms at best
  (2,650 ops/s) and 46 ms at worst (358 ops/s) — 1–2 orders of magnitude
  behind Postgres — and **no TerraBase row is rendered in the history
  table** yet. Known driver bug: `config operation counts must be divisible
  by concurrency` (warm-up 1,000 vs concurrency 16).

## Plan

1. **Baseline honestly.** Fix the divisibility bug, run all four families on
   the TerraBase side through the served runner at
   `LaneConfig::RECORD_MINIMUM`, render them into `benchmark-history.md`.
   Every number is a fact about this host at that moment; failures stay
   visible.
2. **Attribute before optimizing (TB-17.1).** Per-layer latency budget for
   the read path: engine read, TBEP/gRPC hop, client. Measure each layer
   separately so regressions are attributable; publish the budget in the
   benchmark history.
3. **Attack the read path** (TB-17.2): caches, bloom filters, memtable
   lookup cost, allocation pressure, syscall batching, connection/stream
   reuse. Then the **write path** (TB-17.3): WAL group commit, batch ack,
   backpressure. Then **query** and **mixed**.
4. **Every optimization lands with a before/after benchmark entry.** No
   optimization may weaken correctness, durability, isolation, or any
   existing test invariant.
5. **Trajectory is the deliverable.** `benchmark-history.md` shows the path
   from baseline to the bars; a losing family at the end gets an honest
   owner-decision line, never a hand-edited number.

## Commands

```text
# build the served runner once, then all lanes:
cargo build --release --bin terrabase-bench-served
TERRABASE_BENCH_SERVED_RUNNER=target/release/terrabase-bench-served \
  cargo run --release --manifest-path bench/Cargo.toml -- \
  --target terrabase --boundary-class diagnostic-qualification \
  --workload {read,write,query,mixed} --out bench/results

# render the history table (never hand-edit it):
cargo run --release --manifest-path bench/Cargo.toml -- \
  --render-history bench/results docs/evidence/benchmarks/benchmark-history.md
```

## Out of scope

Everything the workspace goal assigns elsewhere: RF=2 production boundary
admission (TB-12's packaged-member lane waits on TB-8), replication WAN
lanes (TB-13), metering (TB-14), multi-core scaling receipts on ARM64
hardware we do not have here. This campaign runs the diagnostic
single-member boundary on this host and says so in every row.
