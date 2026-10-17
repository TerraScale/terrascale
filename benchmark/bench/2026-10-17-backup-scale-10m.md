# Continuous backup + PITR on real Tigris — 10M scale

Same program and binary as the 1K run ([1K results](2026-10-16-backup-scale-1k.md)); reduced
matrix at this scale (PATHB=0, FAULTS=0 — Path B's byte-exact copy and the fault matrix are
disk-bound here), required set C2 C5 C8.

| Item | Value |
| --- | --- |
| Workload | 10,000,000 documents, load-a 4M / load-b 6M, u1/d1/r1 mutations, 8 writers |
| Verdict | **Qualification GREEN**: required successes 3/3 (C2 C5 C8), 0 failures in 5 restores, 1 expected typed refusal; the bounded C4 cut also restored 201 and verified as a per-stream prefix |
| Source verify | exact at C8: 9,990,000/9,990,000, global digest; C2 4,000,000/4,000,000 and C5 10,000,000/10,000,000 exact |
| Bucket audit | `ok`: 1 generation, 3,958 segments, 1 snapshot, 11,579,324,937 B of change log (40M upserts), every object decrypted, authenticated, contiguous |
| Wall time | 10 h 44 m (01:22Z → 12:05Z), single node on delta at nice 19 |

## Measurements

| Measure | Value |
| --- | --- |
| Load rate (8 writers, shipping concurrently) | ≈ 560 docs/s sustained (load-a 4M in 2 h 00 m; load-b 6M in 2 h 51 m) |
| Path A restore C2 (4M docs) | 26.5 min (201, exact) |
| Path A restore C5 (10M docs) | 75.4 min (201, exact) |
| Path A restore C8 (9.99M + mutations) | 79.1 min (201, exact) |
| Path A bounded C1 / C4 | C1 typed 409 `restore_point_unproven`; C4 restored 201, prefix-verified |
| Node RSS max | 5.09 GB (whole run); per-restore peaks 2.2–4.9 GB |
| Stored | 6,643 objects, 21.2 GB sealed |
| Monitor projected cost | $0.47 first month |

## Reading the numbers

- Restore cost is dominated by replay + the restored database's own first-generation snapshot
  upload (each Path A restore adds a full copy of the database to the bucket: stored grew
  5.7 GB → 21.2 GB across three restores). Restores at this scale are hour-class, not minute-class.
- **P11 stays bounded**: restores at 10M documents (a ~23 GB database file) peaked at 2.2–4.9 GB
  RSS. The unfixed behavior (≈2.2× file) would need ≈50 GB — more than this host has.

Run directory: `/home/mariogk/tb-backup-20261016/runs/10m` (evidence retained; the node's 54 GB
data directory is reclaimable after evidence extraction).
