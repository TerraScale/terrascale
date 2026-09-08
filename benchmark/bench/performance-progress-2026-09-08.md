# TerraBase performance progress — 2026-09-08

This is engineering evidence from the current source cohort. It is not a served-boundary qualification and does not replace the retained SE-15 receipts.

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
