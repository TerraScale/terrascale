# Replication benchmark: repl-local-100k-put

- Verdict: **PASSED** (every region holds exactly the expected documents, conflicts identical everywhere)
- Kind: local; regions: gru, fra, sjc; host: mariogk-dev
- Commit: `af126ee80a6c`; run 2026-10-12 02:13:32 to 2026-10-12 02:16:00 (UTC)
- Config: `{"batch":1,"concurrency_per_region":16,"conflict_docs":200,"conflict_rounds":5,"delete_pct":1,"docs":100000,"probe_every":"250ms","seed":1791771212824261586,"sizes":"256:70,1024:25,8192:5"}`
- Collection: `bench-01m4py5z0yr8424v8sqe96hbqg`

## Writes (load wall time 120.4 s)

| Region | Created | docs/s | per-second avg / max | Request p50 | p95 | p99 | max (ms) | Delete p50 / p99 | Conflict patch p50 / p99 | 429 | Retried | Failed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| gru | 33334 | 277 | 280 / 639 | 54.9 | 85.6 | 110.7 | 230.3 | 54.2 / 112.8 | 56.3 / 119.1 | 0 | 0 | 0 |
| fra | 33333 | 277 | 285 / 653 | 54.5 | 82.6 | 112.6 | 193.8 | 53.9 / 110.0 | 55.9 / 117.1 | 0 | 0 | 0 |
| sjc | 33333 | 277 | 275 / 562 | 55.1 | 84.4 | 116.5 | 204.0 | 54.8 / 133.2 | 55.8 / 129.7 | 0 | 0 | 0 |

## Replication lag (probe acknowledged in the origin -> first read that sees it in the target)

| Pair | Probes | p50 | p95 | p99 | max (ms) | Missing |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fra->gru | 12 | 9289.7 | 16908.3 | 19267.6 | 19267.6 | 0 |
| fra->sjc | 12 | 9478.1 | 16760.8 | 18825.2 | 18825.2 | 0 |
| gru->fra | 13 | 10231.8 | 15073.3 | 17612.8 | 17612.8 | 0 |
| gru->sjc | 13 | 10084.4 | 16384.0 | 17940.5 | 17940.5 | 0 |
| sjc->fra | 13 | 9314.3 | 13942.8 | 15392.8 | 15392.8 | 0 |
| sjc->gru | 13 | 9289.7 | 15581.2 | 15786.0 | 15786.0 | 0 |

One probe in flight per origin, polled every 10 ms. Poll read latency (the lag's resolution): p50 3.7 ms, p99 14.9 ms, max 102.5 ms over 48800 reads.

## Convergence and correctness

- Expected documents: 99027 (deleted 973, unknown outcome 0); expected digest `bc6b92a958b334501e8a95b637d99fcf64501680b34a517e56f748453050756d`
- End markers visible everywhere: 2.9 s after the last acknowledgement
- Every region exact: from 2.9 s (start of the matching round) to 12.9 s (its end) after the last acknowledgement, 1 listing round(s)

| Region | Count | Missing | Extra | Mismatched | Digest | Listing (s) |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| gru | 99027 | 0 | 0 | 0 | `bc6b92a958b33450…` | 10.0 |
| fra | 99027 | 0 | 0 | 0 | `bc6b92a958b33450…` | 9.8 |
| sjc | 99027 | 0 | 0 | 0 | `bc6b92a958b33450…` | 9.7 |

## Conflicts (200 documents, every region overwrites each one concurrently)

- Identical in every region: 200 of 200
- Shared field `s` survivor (a region's last write; `invalid` = no region's last write): sjc 200
- Own field `f_<region>` (each region's last round): fra lost 200, gru lost 200, sjc kept 200

## Resources (/proc/348019 and the data directories)

the local nodes run in one host process, so CPU, memory and swap are their sum; disk is per node.

| Node | CPU cores avg / p95 / max | Memory MB avg / p95 / max | Swap MB max | Steal % avg / max | Disk MB (end) |
| --- | ---: | ---: | ---: | ---: | ---: |
| all nodes (one process) | 4.78 / 6.12 / 7.73 | 489 / 1211 / 2100 | 0 | n/a | 0.0 |
| fra | n/a | n/a | n/a | n/a | 449.4 |
| gru | n/a | n/a | n/a | n/a | 449.7 |
| sjc | n/a | n/a | n/a | n/a | 448.3 |

## Not tested in this run

- reads under load: the load is writes only (creates, deletes and conflict patches); reads happen only in probes and verification
- counters, sets, SQL writes and the changes feed: not part of this load
- faults (partitions, a region down, restarts, clock skew) during the load: covered by tests/TerraBase.E2E, not here
- network: the regions are in-process nodes on one machine, so latency and lag carry no WAN round trips
- per-node CPU and memory: the nodes share one host process; only disk is per node
- transactions: every document is its own PUT
