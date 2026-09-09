# TerraBase performance progress — 2026-09-08

This is engineering evidence from the current source cohort. It is not a served-boundary qualification and does not replace the retained SE-15 receipts.

The document batch bound is now 8,192 operations so a durable request can
amortize one FULL WAL flush while remaining bounded. On the rebuilt debug
member, 8,192-operation batches completed at **2,290–2,679 logical ops/s**;
larger batches alone do not reach 5k because row/index/accounting work grows
with the batch. Parallel shard lanes remain required for the target.

The durability attribution probe confirms the tradeoff: on the current debug
host, `Buffered` measured **156 ops/s**, `Written` (WAL with
`synchronous=NORMAL`) **163 ops/s**, and `Synced` (`synchronous=FULL`)
**108 ops/s**. `NORMAL` is enabled for the explicitly weaker `Written` tier;
synced acknowledgements continue to use `FULL` so a power loss cannot be
reported as durable before the WAL flush.

Shard consolidation now has a bounded implementation in
`src/shard_split/compaction.rs`: it attaches each shard, recreates schema,
copies rows idempotently, checkpoints and fsyncs a temporary output, then
atomically renames it into place. The focused overlapping-shard test passes.

## In-process operation-cost probe

Command: `nice -n 19 cargo test --release --locked --lib benchmarks::lane_run::tests::probe_lane_operation_costs -- --nocapture`

The test passed (1 test, 1,257 filtered). Release artifact was rebuilt from the current checkout; no executable digest or schema receipt was emitted by this probe.

| Operation | Throughput (ops/s) | Approx. latency |
|---|---:|---:|
| Serial point reads | 8,783 | 113.9 µs/op |
| Serial indexed queries | 6,652 | 150.3 µs/op |
| Serial synced writes | 126 | 7,951.0 µs/op |
| 16-thread point reads | 241,995 | probe-only |
| 16-thread indexed queries | 2,023,006 | probe-only |

The previous commissioning evidence recorded 3,469 / 3,414 / 3,201 point reads/s and 82 / 73 / 62 writes/s under a different commissioning harness. The settings and measurement boundary differ, so this table records the new values without claiming a direct speedup ratio. The current probe confirms that synced durability remains the dominant cost.

## Changes in this cohort

- SQL execution reuses cached prepared statements for repeated query shapes.
- Each write transaction snapshots TTL and merge-schema declarations once, removing repeated per-document catalog lookups.
- Origin sequence allocation caches the durable three-source maximum per `(tenant, origin)` within a commit wave and increments subsequent intents in memory. Conflict rollback restores the cached value.

All changes retain WAL, atomic transactions, and `synchronous=FULL` for `LocalDurability::Synced`.

## Served-boundary probe from the rebuilt member

The member was restarted from release artifact SHA-256
`6e6e233e8b6cf17f35b26de4f6fdc20c9909b337a2c83160dde1bcbdd9b5ebc5` under
the `2ocpu/12GiB` envelope (cpuset `0-1`). The probe used the local HTTP API,
one member, and the `local` database.

| Workload | Concurrency | Throughput (ops/s) | p50 latency |
|---|---:|---:|---:|
| Point read | 1 | 1,093–1,123 | 0.75–0.76 ms |
| Indexed query | 1 | 795–915 | 1.00–1.05 ms |
| Synced PUT | 1 | 93–107 | 8.59–9.44 ms |
| Point read | 16 | 1,601–1,738 | 9.01–9.62 ms |
| Indexed query | 16 | 1,259–1,354 | 11.61–12.16 ms |
| Synced PUT | 16 | 72–74 | 205–211 ms |

The served boundary is therefore below the in-process numbers because each
request also pays authentication, admission, lifecycle, session, and HTTP
encoding work. A 1,000-document atomic batch completed in 0.21–0.27 s
(3,665–4,744 logical operations/s); this confirms batching is useful, but the
current 1,000-operation API cap and durable commit still leave headroom before
10k/s.

The concurrent individual-write run confirms that adding clients does not
raise durable throughput: requests serialize during lifecycle admission and
the single SQLite writer still performs FULL-synchronous commits. Widening the
coalescing timer without moving that admission work would add tail latency
without proving a gain, so it was deliberately left unchanged.

## Admission-cache follow-up

The engine now caches confirmed lifecycle pause state and primary-file shard
identity per tenant. The cache is refreshed only from authoritative lifecycle
operations or reads, and pause checks occur after the origin barrier is held.
This removes repeated pre-queue storage-mutex reads while retaining pause
fencing. The in-process 16-thread synced-write probe improved to **185–195
ops/s** (about 5.1–5.4 ms amortized per operation), versus the earlier
~126-op/s serial probe. This is a useful improvement but still far below the
tens-of-thousands target because one SQLite writer and one FULL flush remain.

The commit wait is now detached from the engine storage mutex: batches are
enqueued into the SQLite committer and their completion receiver is awaited
after the mutex is released. The concurrent probe measured **192 ops/s** after
this change, confirming correctness and removing lock occupancy, but showing
that the dominant limit is still SQLite's single FULL-synchronous writer.
