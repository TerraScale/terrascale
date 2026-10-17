# Continuous backup + PITR on real Tigris — 1M scale (the G1 milestone)

Same program, binary, bucket and matrix as the 1K run ([1K results](2026-10-16-backup-scale-1k.md)).
This is the stage the 2026-10-13 program defined as **G1** and never reached (no Tigris data was
ever written before it stopped).

| Item | Value |
| --- | --- |
| Workload | 1,000,000 documents, load-a 400k / load-b 600k, u1/d1/r1 mutations, 8 writers |
| Verdict | **Qualification GREEN**: required successes 7/7 (C0 C2 C5 C6 C7 C8 S*), 0 failures in 20 restores, 7 expected typed refusals, 12 fault cases |
| Source verify | exact at C8: 999,000/999,000, global digest; every Path B cut re-verified after the target restart with matching per-partition version digests |
| Bucket audit | `ok` for database and `_system`: 1 generation, 515 segments, 1 snapshot, 1,152,467,628 B of change log, every object decrypted, authenticated and contiguous |
| Wall time | 3 h 06 m (pins 22:10Z → summary 01:16Z), single node on delta (12 CPU) at nice 19 |

## Measurements

| Measure | Value |
| --- | --- |
| Load rate (8 writers, 1,000-doc txns, shipping concurrently) | ≈ 700 docs/s sustained (load-a 400k in ~12 min, load-b 600k in ~19 min) |
| Path A restore C2 / C5 / C8 (reads Tigris directly) | 131.2 s / 356.3 s / 365.8 s (201, exact) |
| Path B restore C2 / C5 / C6 / C7 (isolated node, byte-exact copy) | 123.7 s / 332.7 s / 339.0 s / 343.0 s (201, exact) |
| Node RSS max (whole run: loads, snapshots, 20 restores) | **3.66 GB** — with the P11 chunked-commit fix; the 2026-10-13 program measured 6.2–8.09 GB at this scale without it |
| Path B target node peak | 1.97 GB |
| Stored | 796 objects, 2.04 GB (sealed, LZ4+AES-GCM) |
| Monitor projected cost | $0.046 first month |

## P11 evidence

The restore path is the same code the fix touched (`TursoCopy.Write`): the 1M run's peak node RSS
(3.66 GB, including two full-database copies per restore and daily snapshots) sits far below the
2.2×-file rule the old binary showed at this scale (6.2–8.09 GB for a 1.2 GB file). The decisive
test is 10M/100M, where the old behavior would need 25–55 GB / 260–550 GB.

Run directory: `/home/mariogk/tb-backup-20261016/runs/1m` (ledger, cuts, audits, monitor samples,
fault-matrix records).
