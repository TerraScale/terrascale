# Continuous backup + PITR on real Tigris — 10K scale

Same program, binary, bucket and matrix as the 1K run ([1K results](2026-10-16-backup-scale-1k.md)).

| Item | Value |
| --- | --- |
| Workload | 10,000 documents, load-a 4,000 / load-b 6,000, u1/d1/r1 mutations, 8 writers |
| Verdict | **Qualification GREEN**: required successes 7/7, 0 failures in 20 restores, 7 expected typed refusals, 12 fault cases |
| Source verify | exact at C8: 9,990/9,990 documents, global digest match |
| Bucket audit | `ok` (database and `_system`), all objects decrypt + authenticate + contiguous |

## Measurements

| Measure | Value |
| --- | --- |
| Path A restore C2 / C5 / C8 (10k docs) | 3.69 s / 4.96 s / 9.03 s (201) |
| Path A bounded C1 / C4 | typed 409 `restore_point_unproven` (T inside a commit second; expected) |
| Path B restore C0 / C2 / C5 | 0.74 s / 2.00 s / 3.64 s (201) |
| Peak node RSS | ≤ 342 MiB |

Restore time grows super-linearly with size here (1K→2.6 s, 10K→9.0 s at C8): the C8 restore
replays the whole u1/d1/r1 mutation log on top of the snapshot, and segment download is
8-at-a-time per the restore design.

Run directory: `/home/mariogk/tb-backup-20261016/runs/10k`.
