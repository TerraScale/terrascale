# Continuous backup + PITR on real Tigris — 100K scale

Same program, binary, bucket and matrix as the 1K run ([1K results](2026-10-16-backup-scale-1k.md)).

| Item | Value |
| --- | --- |
| Workload | 100,000 documents, load-a 40,000 / load-b 60,000, u1/d1/r1 mutations, 8 writers |
| Verdict | **Qualification GREEN**: required successes 7/7, 0 failures in 20 restores, 8 expected typed refusals, 12 fault cases |
| Source verify | exact at C8: 99,900/99,900 documents, global digest match |
| Bucket audit | `ok` for the database and `_system` |

## Measurements

| Measure | Value |
| --- | --- |
| Path A restore C8 (100k docs, reads Tigris directly) | 32.16 s (201) |
| Path B restore C8 (isolated node, byte-exact copy) | 29.79 s (201) |
| Path B verify C6 | exact 100,000/100,000 with 101 acknowledged-before-T batches checked (A-7) |
| Fault case restore (fstray at C8) | 30.01 s (201, exact) |
| Peak node RSS | ~350 MiB |

Scaling so far (C8 restore, Path A): 1K 2.6 s → 10K 9.0 s → 100K 32 s — roughly linear in
documents with a small fixed cost; the log replay dominates, matching the restore design
(snapshot + whole-generation segment replay, 8 segments in flight).

Run directory: `/home/mariogk/tb-backup-20261016/runs/100k`.
