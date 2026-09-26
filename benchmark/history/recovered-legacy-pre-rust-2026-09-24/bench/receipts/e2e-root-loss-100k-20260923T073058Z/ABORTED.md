# 100,000-document RustFS root-loss diagnostic — stopped

This run was intentionally stopped during the first exhaustive relationship verification. It is **not** a completed end-to-end or root-loss qualification. The source root still exists; no backup or source-root deletion phase had started.

| Measurement | Observed result |
| --- | ---: |
| Documents seeded | 100,000 |
| Relationships written | 10,000 |
| Last phase printed | `relationship-verify` |
| Process elapsed before SIGTERM | approximately 8 minutes 40 seconds |
| Last sampled cgroup memory peak | 425,832,448 bytes |
| Last sampled cgroup local writes | 1,006,758,912 bytes |
| Cgroup OOM events at last sample | 0 |
| Process exit | 143 (SIGTERM) |

The benchmark used synced batches of 1,000, a 4 GiB memory cgroup with zero swap, and frozen Release assembly SHA-256 `002ba1e6f69fa3bd62987904117e1eea3d2cc4873cd4958dd3e62072f8549dd8`. It requested 512 point reads, 1,000 updates, 1,000 deletes, 100 queries, and root-loss recovery, but did not reach those phases.

The verification loop calls `ListRelatedAsync` twice and `TraverseRelationshipsAsync` for each of 2,500 users. The current `StorageEngine.ReadConstraintRecordsUnsafe` scans and decodes every internal constraint record for each read. A keyed lookup is being implemented before a fresh full run. The completed 1,000-document RustFS root-loss smoke remains separately recorded under `../e2e-root-loss-smoke-20260923T073010Z/`.
