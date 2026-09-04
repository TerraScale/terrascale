# Benchmarks (stub)

> L-5 launch-post stub. Numbers land from E-13, measured at the public API
> boundary under the one-host ceiling; nothing here publishes a number without
> a measurement behind it (D-61).

- Point read/write, indexed query, and bulk ingest vs PostgreSQL-with-JSONB
  and RavenDB, like-for-like (single-node and RF=2 families).
- Auto-indexing vs RavenDB: time-to-active, regressions after activation
  (must be zero), write throughput at 0/5/12 indexes, zero stale reads.
- CDN fan-out gate: 1M concurrent readers of one shared view at 16 writes/s.
