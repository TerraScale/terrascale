# Live local replica lag: six measured cells passed

GRU was the home. FRA ran first, three back-to-back repeats, followed by three SJC repeats. Each cell used 500 sequential writes through the public API and required every successful read to come from its replica region and every write to have an attributed revision notice. All 3,000 reads were local and all 3,000 notices carried pushed pages. No client 409 retry was used. The SJC benchmark reported HTTP 503 when deleting its database after all measured writes; account deletion subsequently removed the synthetic resources.

| Replica | Repeat | Writes | Local | Notices | Commit→visible p50 ms | p99 ms | Max ms | Commit→notice p99 ms | Notice→visible p99 ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| fra | 1 | 500 | 500 | 500 | 112.918 | 164.698 | 384.907 | 115.079 | 52.522 |
| fra | 2 | 500 | 500 | 500 | 113.109 | 136.861 | 374.688 | 104.559 | 36.032 |
| fra | 3 | 500 | 500 | 500 | 113.341 | 147.022 | 378.550 | 107.026 | 37.729 |
| sjc | 1 | 500 | 500 | 500 | 116.921 | 164.288 | 371.931 | 116.973 | 50.921 |
| sjc | 2 | 500 | 500 | 500 | 116.947 | 148.353 | 370.095 | 107.797 | 29.984 |
| sjc | 3 | 500 | 500 | 500 | 116.570 | 147.107 | 400.803 | 113.204 | 27.672 |

Percentiles use nearest rank within each 500-write cell. Commit timestamps come from the home revision notice; notice arrival and visibility use the replica benchmark clock. Raw JSONL and every notice/read/write trace are retained as deterministic gzip files.

All six nodes passed authenticated internal health/readiness checks before and after the proof at revision `55fd5cb60be844ed31a157775d1c6c18ed985cdc`, storage format `23-41a88901325a`. This includes the WAN pull-lock fix `71511c0eb`, checkpoint/reader/maintenance changes, and initial home-lease confirmation `21834b712`. The benchmark binary SHA-256 and the fabric-freeze deadline are recorded in `deployment.json`. Founder orchestrator PIDs/start timestamps match before and after; each founder TerraBase process also started before the proof, as recorded in the runtime snapshots.

The Free synthetic account `lag-55fd-1105` was deleted and both remote credential files were removed. Previous interrupted proof accounts were also deleted.

Restart durability remains tracked separately in [#1104](https://github.com/TerraScale/terrascale/issues/1104): two pushed ts-orch/NATS restarts lost notices and delayed visibility by 7.4/7.2 seconds. Those incomplete runs and the concurrent-rollout forwarding failure are retained in sibling evidence directories.

- FRA: 2026-11-05T17:17:42.898721+00:00 through 2026-11-05T17:23:08.931399+00:00, 1,500 measured writes.
- SJC: 2026-11-05T17:23:35.736260+00:00 through 2026-11-05T17:29:11.789392+00:00, 1,500 measured writes.

The coordinator completed both benchmarks successfully, then timed out while copying the SJC trace. The complete trace was recovered through a compressed transfer and its SHA-256 matched the remote source; both raw sample files and both complete traces were checked against remote hashes. Cleanup in the coordinator’s `finally` block succeeded despite the transfer error. Every trace contains all 1,500 measured write timings, and every successful measured poll is local.
