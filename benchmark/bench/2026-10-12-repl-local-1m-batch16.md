# Replication benchmark: repl-local-1m-batch16

- Verdict: **PASSED** (every region holds exactly the expected documents, conflicts identical everywhere)
- Kind: local; regions: gru, fra, sjc; host: mariogk-dev
- Commit: `af126ee80a6c`; run 2026-10-12 02:01:29 to 2026-10-12 02:13:04 (UTC)
- Config: `{"batch":16,"concurrency_per_region":16,"conflict_docs":200,"conflict_rounds":5,"delete_pct":1,"docs":1000000,"probe_every":"250ms","seed":1791770489478275722,"sizes":"256:70,1024:25,8192:5"}`
- Collection: `bench-01m4pxfwmaxcnrqm3bp5anbqej`

## Writes (load wall time 564.2 s)

| Region | Created | docs/s | per-second avg / max | Request p50 | p95 | p99 | max (ms) | Delete p50 / p99 | Conflict patch p50 / p99 | 429 | Retried | Failed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| gru | 333334 | 591 | 606 / 1312 | 367.1 | 445.7 | 523.5 | 762.4 | 356.1 / 503.6 | 364.5 / 506.9 | 0 | 0 | 0 |
| fra | 333333 | 591 | 591 / 1280 | 374.3 | 463.4 | 547.3 | 754.7 | 361.2 / 526.8 | 366.6 / 518.4 | 0 | 0 | 0 |
| sjc | 333333 | 591 | 597 / 992 | 373.0 | 456.2 | 530.4 | 707.6 | 361.2 / 511.0 | 367.1 / 504.1 | 0 | 0 | 0 |

## Replication lag (probe acknowledged in the origin -> first read that sees it in the target)

| Pair | Probes | p50 | p95 | p99 | max (ms) | Missing |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fra->gru | 30 | 17793.0 | 29769.7 | 30179.3 | 30179.3 | 0 |
| fra->sjc | 30 | 17219.6 | 31195.1 | 31227.9 | 31227.9 | 0 |
| gru->fra | 28 | 18382.8 | 39813.1 | 43352.1 | 43352.1 | 0 |
| gru->sjc | 28 | 17170.4 | 34111.5 | 36864.0 | 36864.0 | 0 |
| sjc->fra | 29 | 18399.2 | 41517.1 | 41680.9 | 41680.9 | 0 |
| sjc->gru | 29 | 18530.3 | 30343.2 | 30621.7 | 30621.7 | 0 |

One probe in flight per origin, polled every 10 ms. Poll read latency (the lag's resolution): p50 3.4 ms, p99 14.0 ms, max 445.7 ms over 222846 reads.

## Convergence and correctness

- Expected documents: 990183 (deleted 9817, unknown outcome 0); expected digest `44c7c83aaa79b0d0baac9439bfad9594244ee0f1cfb1391d03c71625e8fc8392`
- End markers visible everywhere: 2.3 s after the last acknowledgement
- Every region exact: from 2.3 s (start of the matching round) to 112.4 s (its end) after the last acknowledgement, 1 listing round(s)

| Region | Count | Missing | Extra | Mismatched | Digest | Listing (s) |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| gru | 990183 | 0 | 0 | 0 | `44c7c83aaa79b0d0…` | 108.9 |
| fra | 990183 | 0 | 0 | 0 | `44c7c83aaa79b0d0…` | 107.4 |
| sjc | 990183 | 0 | 0 | 0 | `44c7c83aaa79b0d0…` | 108.4 |

## Conflicts (200 documents, every region overwrites each one concurrently)

- Identical in every region: 200 of 200
- Shared field `s` survivor (a region's last write; `invalid` = no region's last write): sjc 200
- Own field `f_<region>` (each region's last round): fra lost 200, gru lost 200, sjc kept 200

## Resources (/proc/340911 and the data directories)

the local nodes run in one host process, so CPU, memory and swap are their sum; disk is per node.

| Node | CPU cores avg / p95 / max | Memory MB avg / p95 / max | Swap MB max | Steal % avg / max | Disk MB (end) |
| --- | ---: | ---: | ---: | ---: | ---: |
| all nodes (one process) | 3.94 / 4.54 / 5.43 | 1867 / 9436 / 10452 | 0 | n/a | 0.0 |
| fra | n/a | n/a | n/a | n/a | 4484.7 |
| gru | n/a | n/a | n/a | n/a | 4481.5 |
| sjc | n/a | n/a | n/a | n/a | 4479.7 |

## Not tested in this run

- reads under load: the load is writes only (creates, deletes and conflict patches); reads happen only in probes and verification
- counters, sets, SQL writes and the changes feed: not part of this load
- faults (partitions, a region down, restarts, clock skew) during the load: covered by tests/TerraBase.E2E, not here
- network: the regions are in-process nodes on one machine, so latency and lag carry no WAN round trips
- per-node CPU and memory: the nodes share one host process; only disk is per node
